using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace ClosingShift.Localization
{
    public static class RussianGlyphProbe
    {
        public static string Run()
        {
            var result = new StringBuilder();
            result.AppendLine("RUSSIAN_GLYPH_PROBE Unity=" + Application.unityVersion + " graphics=" + SystemInfo.graphicsDeviceType);
            foreach (string path in new[] { RussianFontCompat.OswaldPath, RussianFontCompat.LiberationPath })
            {
                Font font = Resources.Load<Font>(path);
                if (font == null) throw new InvalidOperationException("Russian font alias absent: " + path);
                if (!RussianFontCompat.Covers(font, RussianFontCompat.Required)) throw new InvalidOperationException("Russian source glyph missing: " + font.name);
                const int size = 32;
                font.RequestCharactersInTexture(RussianFontCompat.Required, size, FontStyle.Normal);
                foreach (char c in RussianFontCompat.Required)
                {
                    CharacterInfo info;
                    if (!font.GetCharacterInfo(c, out info, size, FontStyle.Normal) || info.advance <= 0)
                        throw new InvalidOperationException("Russian glyph atlas generation failed: " + font.name + " U+" + ((int)c).ToString("X4"));
                }
                result.AppendLine("FONT PASS name=" + font.name + " source=" + path + " required=" + RussianFontCompat.Required.Length + " atlas=" + font.material.mainTexture.width + "x" + font.material.mainTexture.height);
            }
            foreach (TMP_FontAsset font in Resources.FindObjectsOfTypeAll<TMP_FontAsset>())
            {
                if (font == null || font.sourceFontFile == null || font.faceInfo.familyName != "Noto Serif" && font.faceInfo.familyName != "Liberation Sans") continue;
                font.isMultiAtlasTexturesEnabled = true;
                uint[] missing;
                bool covered = font.HasCharacters(RussianFontCompat.Required, out missing, false, true);
                if (!covered) throw new InvalidOperationException("Russian TMP atlas generation failed: " + font.name);
                foreach (char c in RussianFontCompat.Required)
                {
                    TMP_Character character;
                    if (!font.characterLookupTable.TryGetValue(c, out character) || character.glyphIndex == 0 || character.glyph == null || character.glyph.glyphRect.width <= 0 || character.glyph.glyphRect.height <= 0)
                        throw new InvalidOperationException("Russian TMP glyph has empty geometry: " + font.name + " U+" + ((int)c).ToString("X4"));
                }
                result.AppendLine("TMP PASS name=" + font.name + " family=" + font.faceInfo.familyName + " chars=" + font.characterTable.Count + " atlases=" + font.atlasTextures.Length + " shader=" + (font.material == null ? "null" : font.material.shader.name));
            }
            Font teko = null;
            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
                if (font != null && font.name.IndexOf("Teko", StringComparison.OrdinalIgnoreCase) >= 0) { teko = font; break; }
            if (teko == null) throw new InvalidOperationException("Original Teko font was not loaded for repair fixture");
            var fixture = new GameObject("RussianFontRepairFixture", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            var fixtureText = fixture.GetComponent<Text>();
            fixtureText.font = teko;
            fixtureText.fontSize = 27;
            fixtureText.lineSpacing = 1.2f;
            fixtureText.alignment = TextAnchor.LowerRight;
            fixtureText.rectTransform.sizeDelta = new Vector2(650, 70);
            fixtureText.text = "Ёжик ещё ждёт: «Проверьте дверь» — № 12…";
            RussianFontCompat.SetRussianActive(true);
            RussianFontCompat.ApplyTo(fixtureText);
            if (fixtureText.font == teko || fixtureText.font.name != "Oswald-Regular" || !RussianFontCompat.Covers(fixtureText.font, fixtureText.text))
                throw new InvalidOperationException("Teko Cyrillic repair failed");
            if (fixtureText.fontSize != 27 || fixtureText.lineSpacing != 1.2f || fixtureText.alignment != TextAnchor.LowerRight || fixtureText.rectTransform.sizeDelta != new Vector2(650, 70))
                throw new InvalidOperationException("Teko repair changed author layout fields");
            int beforeQueries = RussianFontCompat.NativeCoverageQueries;
            for (int i = 0; i < 1000; ++i) RussianFontCompat.ApplyTo(fixture);
            if (RussianFontCompat.NativeCoverageQueries != beforeQueries) throw new InvalidOperationException("Unchanged scoped text repair repeated native glyph coverage queries");
            RussianFontCompat.SetRussianActive(false);
            if (fixtureText.font != teko) throw new InvalidOperationException("Original Teko font was not restored when Russian was disabled");
            UnityEngine.Object.DestroyImmediate(fixture);
            var endingOwner = new GameObject("Ending_Text", typeof(RectTransform));
            var ending = new GameObject("Ending Text", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            ending.transform.SetParent(endingOwner.transform, false);
            var endingText = ending.GetComponent<Text>();
            Font endingOriginal = Resources.Load<Font>(RussianFontCompat.OswaldPath);
            endingText.font = endingOriginal;
            endingText.fontSize = 14;
            endingText.lineSpacing = 1.5f;
            endingText.text = "Я убила человека. Когда я подняла кирпич...";
            RussianFontCompat.SetRussianActive(true);
            RussianFontCompat.ApplyTo(endingText);
            if (endingText.font != Resources.Load<Font>(RussianFontCompat.LiberationPath)
                || endingText.fontSize != 14 || endingText.lineSpacing != 1.5f)
                throw new InvalidOperationException("Ending Cyrillic proportional font or preserved layout failed");
            RussianFontCompat.SetRussianActive(false);
            if (endingText.font != endingOriginal)
                throw new InvalidOperationException("Ending font was not restored outside Russian");
            UnityEngine.Object.DestroyImmediate(endingOwner);
            result.AppendLine("ENDING_FONT PASS proportional Cyrillic even with existing glyph coverage; layout and locale restoration preserved");
            result.AppendLine("REPAIR PASS Teko-to-Oswald; size/spacing/alignment/rectangle preserved; original font restored");
            result.AppendLine("CACHE PASS 1000 unchanged scoped repairs; zero additional native glyph coverage queries");
            int legacy = 0, tmp = 0;
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid() || !RussianFontCompat.ContainsRussian(text.text)) continue;
                ++legacy;
                if (!RussianFontCompat.Covers(text.font, text.text)) throw new InvalidOperationException("Visible Russian Legacy Text lacks glyphs: " + text.name);
                result.AppendLine("TEXT name=" + text.name + " font=" + text.font.name + " size=" + text.fontSize + " rect=" + text.rectTransform.rect + " preferred=" + text.preferredWidth.ToString(CultureInfo.InvariantCulture) + "x" + text.preferredHeight.ToString(CultureInfo.InvariantCulture));
            }
            foreach (TMP_Text text in Resources.FindObjectsOfTypeAll<TMP_Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid() || !RussianFontCompat.ContainsRussian(text.text)) continue;
                ++tmp;
                uint[] missing;
                if (text.font == null || !text.font.HasCharacters(text.text, out missing, true, true)) throw new InvalidOperationException("Visible Russian TMP Text lacks glyphs: " + text.name);
                text.ForceMeshUpdate(true, false);
                result.AppendLine("TMP_TEXT name=" + text.name + " font=" + text.font.name + " size=" + text.fontSize.ToString(CultureInfo.InvariantCulture) + " overflow=" + text.isTextOverflowing + " truncated=" + text.isTextTruncated);
            }
            result.AppendLine("RUSSIAN_GLYPH_PROBE PASS legacy=" + legacy + " tmp=" + tmp);
            Debug.Log(result.ToString());
            return result.ToString();
        }
    }
}
