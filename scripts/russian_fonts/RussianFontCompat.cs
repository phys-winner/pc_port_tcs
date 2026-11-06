using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ClosingShift.Localization
{
    // Call after the localization system has assigned translated strings/fonts.
    // No Update loop, OS font dependency, text rewriting or layout mutation.
    public static class RussianFontCompat
    {
        public const string Required = "АБВГДЕЁЖЗИЙКЛМНОПРСТУФХЦЧШЩЪЫЬЭЮЯабвгдеёжзийклмнопрстуфхцчшщъыьэюя«»—–…№";
        public const string OswaldPath = "closing_shift/fonts/oswald_regular";
        public const string LiberationPath = "closing_shift/fonts/liberation_sans";
        private static readonly Dictionary<Text, Font> originals = new Dictionary<Text, Font>();
        private sealed class GlyphCoverage
        {
            public WeakReference Font;
            public readonly Dictionary<char, bool> Characters = new Dictionary<char, bool>();
        }
        private sealed class TextState
        {
            public WeakReference Owner;
            public int FontId;
            public string Value;
        }
        private static readonly Dictionary<int, GlyphCoverage> glyphCoverage = new Dictionary<int, GlyphCoverage>();
        private static readonly Dictionary<int, TextState> legacyState = new Dictionary<int, TextState>();
        private static readonly Dictionary<int, TextState> tmpState = new Dictionary<int, TextState>();
        internal static int NativeCoverageQueries { get; private set; }
        private static bool russian;
        private static Font oswald, liberation;

        public static void SetRussianActive(bool value)
        {
            russian = value;
            if (!value)
            {
                foreach (var row in originals)
                    if (row.Key != null && (row.Key.font == oswald || row.Key.font == liberation)) row.Key.font = row.Value;
                originals.Clear();
                legacyState.Clear();
                tmpState.Clear();
                return;
            }
            Refresh();
        }

        public static bool ContainsRussian(string text)
        {
            if (String.IsNullOrEmpty(text)) return false;
            for (int i = 0; i < text.Length; ++i)
                if ((text[i] >= '\u0400' && text[i] <= '\u04ff') || text[i] == '\u2116') return true;
            return false;
        }

        public static bool Covers(Font font, string required)
        {
            if (font == null) return false;
            int id = font.GetInstanceID();
            GlyphCoverage entry;
            if (!glyphCoverage.TryGetValue(id, out entry) || !System.Object.ReferenceEquals(entry.Font.Target, font))
            {
                entry = new GlyphCoverage { Font = new WeakReference(font) };
                glyphCoverage[id] = entry;
            }
            for (int i = 0; i < required.Length; ++i)
            {
                char character = required[i];
                if (Char.IsWhiteSpace(character)) continue;
                bool present;
                if (!entry.Characters.TryGetValue(character, out present))
                {
                    present = font.HasCharacter(character);
                    ++NativeCoverageQueries;
                    entry.Characters.Add(character, present);
                }
                if (!present) return false;
            }
            return true;
        }

        private static bool Unchanged(UnityEngine.Object owner, int fontId, string value, Dictionary<int, TextState> cache)
        {
            TextState state;
            return cache.TryGetValue(owner.GetInstanceID(), out state) && System.Object.ReferenceEquals(state.Owner.Target, owner) && state.FontId == fontId && state.Value == value;
        }

        private static void Remember(UnityEngine.Object owner, int fontId, string value, Dictionary<int, TextState> cache)
        {
            cache[owner.GetInstanceID()] = new TextState { Owner = new WeakReference(owner), FontId = fontId, Value = value };
        }

        private static void RemoveDead(Dictionary<int, TextState> cache)
        {
            var dead = new List<int>();
            foreach (var row in cache)
            {
                UnityEngine.Object target = row.Value.Owner.Target as UnityEngine.Object;
                if (target == null) dead.Add(row.Key);
            }
            foreach (int id in dead) cache.Remove(id);
        }

        private static Font RequiredFont(bool condensed)
        {
            Font font = condensed ? oswald : liberation;
            if (font == null)
            {
                font = Resources.Load<Font>(condensed ? OswaldPath : LiberationPath);
                if (!Covers(font, Required))
                    throw new InvalidOperationException("Russian font alias missing or incomplete: " + (condensed ? OswaldPath : LiberationPath));
                if (condensed) oswald = font; else liberation = font;
            }
            return font;
        }

        public static void ApplyTo(Text text)
        {
            if (!russian || text == null) return;
            Font original = text.font;
            int originalId = original == null ? 0 : original.GetInstanceID();
            if (Unchanged(text, originalId, text.text, legacyState)) return;
            if (!ContainsRussian(text.text)) { Remember(text, originalId, text.text, legacyState); return; }
            string name = original == null ? String.Empty : original.name;
            // The ending's CJK font keeps full-width advances even when Cyrillic glyphs exist.
            bool ending = text.name == "Ending Text" && text.transform.parent != null && text.transform.parent.name == "Ending_Text";
            if (!ending && Covers(original, text.text)) { Remember(text, originalId, text.text, legacyState); return; }
            bool condensed = !ending && (name.IndexOf("Teko", StringComparison.OrdinalIgnoreCase) >= 0 || name.IndexOf("Oswald", StringComparison.OrdinalIgnoreCase) >= 0);
            Font replacement = RequiredFont(condensed);
            if (!Covers(replacement, text.text))
                throw new InvalidOperationException("Russian replacement would lose existing mixed-script glyphs: " + text.name);
            if (!originals.ContainsKey(text)) originals.Add(text, original);
            text.font = replacement;
            text.SetAllDirty();
            Remember(text, replacement.GetInstanceID(), text.text, legacyState);
        }

        public static void ApplyTo(TMP_Text text)
        {
            if (!russian || text == null || text.font == null) return;
            TMP_FontAsset font = text.font;
            int fontId = font.GetInstanceID();
            if (Unchanged(text, fontId, text.text, tmpState)) return;
            if (!ContainsRussian(text.text)) { Remember(text, fontId, text.text, tmpState); return; }
            uint[] missing;
            if (font.HasCharacters(Required, out missing, false, true)) { Remember(text, fontId, text.text, tmpState); return; }
            // The default static Liberation Sans already has a matching dynamic
            // font asset in the original game's Resources. Keep the primary
            // material and use that same-family fallback for added Cyrillic.
            if (font.name.IndexOf("LiberationSans", StringComparison.OrdinalIgnoreCase) >= 0 || font.faceInfo.familyName == "Liberation Sans")
            {
                TMP_FontAsset fallback = Resources.Load<TMP_FontAsset>("Fonts & Materials/LiberationSans SDF - Fallback");
                if (fallback == null) throw new InvalidOperationException("Original Liberation Sans dynamic fallback was not found");
                fallback.isMultiAtlasTexturesEnabled = true;
                if (!fallback.HasCharacters(Required, out missing, false, true))
                    throw new InvalidOperationException("Original Liberation Sans failed to create Russian glyphs");
                if (font.fallbackFontAssetTable == null) font.fallbackFontAssetTable = new List<TMP_FontAsset>();
                if (!font.fallbackFontAssetTable.Contains(fallback)) font.fallbackFontAssetTable.Add(fallback);
                text.SetAllDirty();
                Remember(text, fontId, text.text, tmpState);
                return;
            }
            throw new InvalidOperationException("Russian glyphs unavailable in TMP font: " + font.name + " / " + font.faceInfo.familyName);
        }

        public static void ApplyTo(GameObject owner)
        {
            if (!russian || owner == null) return;
            ApplyTo(owner.GetComponent<Text>());
            ApplyTo(owner.GetComponent<TMP_Text>());
        }

        public static void Refresh()
        {
            if (!russian) return;
            var dead = new List<Text>();
            foreach (var row in originals) if (row.Key == null) dead.Add(row.Key);
            foreach (Text text in dead) originals.Remove(text);
            RemoveDead(legacyState);
            RemoveDead(tmpState);
            var deadFonts = new List<int>();
            foreach (var row in glyphCoverage) if (row.Value.Font.Target as Font == null) deadFonts.Add(row.Key);
            foreach (int id in deadFonts) glyphCoverage.Remove(id);
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
                if (text != null && text.gameObject.scene.IsValid()) ApplyTo(text);
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
                if (text != null && text.gameObject.scene.IsValid()) ApplyTo(text);
        }
    }
}
