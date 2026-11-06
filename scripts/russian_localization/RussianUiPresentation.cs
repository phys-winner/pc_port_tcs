using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace ClosingShift.Localization
{
    public static class RussianUiPresentation
    {
        private sealed class Binding
        {
            public WeakReference Owner;
            public Dictionary<string, string> Labels;
            public bool Caption, DynamicFps, Busy;
            public string Original, Translated;

            public void Apply()
            {
                Text text = Owner.Target as Text;
                if (!russian || Busy || text == null) return;
                string original = text.text, translated;
                if (!Labels.TryGetValue(original, out translated))
                {
                    translated = DynamicFps ? RussianPresentationPolicy.TranslateFps(original) : null;
                    if (translated == null) return;
                }
                if (original == translated) return;
                Busy = true;
                try
                {
                    Original = original;
                    Translated = translated;
                    text.text = translated;
                    RussianFontCompat.ApplyTo(text);
                }
                finally { Busy = false; }
            }

            public void Restore()
            {
                Text text = Owner.Target as Text;
                if (text == null || Caption || Original == null || text.text != Translated) return;
                text.text = Original;
            }
        }

        private static readonly Dictionary<int, Binding> bindings = new Dictionary<int, Binding>();
        private static readonly Dictionary<int, WeakReference> dropdowns = new Dictionary<int, WeakReference>();
        private static bool russian, subscribed;
        public static bool RussianActive { get { return russian; } }

        private static string Identity(GameObject owner)
        {
            var names = new List<string>();
            for (Transform current = owner.transform; current != null; current = current.parent) names.Add(current.name);
            names.Reverse();
            return owner.scene.buildIndex + ":" + String.Join("\u001f", names.ToArray());
        }

        private static void Bind(Text text, Dictionary<string, string> labels, bool caption, bool dynamicFps)
        {
            if (text == null) return;
            int id = text.GetInstanceID();
            Binding binding;
            if (!bindings.TryGetValue(id, out binding) || !System.Object.ReferenceEquals(binding.Owner.Target, text))
            {
                binding = new Binding { Owner = new WeakReference(text), Labels = labels, Caption = caption, DynamicFps = dynamicFps };
                bindings[id] = binding;
                text.RegisterDirtyVerticesCallback(binding.Apply);
            }
            else
            {
                binding.Labels = labels;
                binding.Caption |= caption;
                binding.DynamicFps |= dynamicFps;
            }
            binding.Apply();
        }

        private static void RegisterDropdown(Dropdown dropdown)
        {
            dropdowns[dropdown.GetInstanceID()] = new WeakReference(dropdown);
            var presenter = dropdown.GetComponent<RussianDropdownPresentation>();
            if (presenter == null) presenter = dropdown.gameObject.AddComponent<RussianDropdownPresentation>();
            presenter.Owner = dropdown;
            Bind(dropdown.captionText, RussianUiData.Labels, true, false);
            Bind(dropdown.itemText, RussianUiData.Labels, false, false);
        }

        public static void SetRussianActive(bool active)
        {
            if (!subscribed)
            {
                SceneManager.sceneLoaded += SceneLoaded;
                subscribed = true;
            }
            russian = active;
            if (!active) RussianAttribution.Hide();
            if (active) { Refresh(); return; }
            // Restore the CURRENT selected option from untouched OptionData, never a stale snapshot.
            foreach (var row in dropdowns)
            {
                Dropdown dropdown = row.Value.Target as Dropdown;
                if (dropdown != null) dropdown.RefreshShownValue();
            }
            foreach (var row in bindings) row.Value.Restore();
        }

        private static void SceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (!russian) return;
            Refresh();
            RussianFontCompat.Refresh();
        }

        public static void Refresh()
        {
            if (!russian) return;
            var dead = new List<int>();
            foreach (var row in bindings) if (row.Value.Owner.Target as Text == null) dead.Add(row.Key);
            foreach (int id in dead) bindings.Remove(id);
            dead.Clear();
            foreach (var row in dropdowns) if (row.Value.Target as Dropdown == null) dead.Add(row.Key);
            foreach (int id in dead) dropdowns.Remove(id);
            foreach (var row in bindings) row.Value.Apply();
            foreach (Text text in Resources.FindObjectsOfTypeAll<Text>())
            {
                if (text == null || !text.gameObject.scene.IsValid()) continue;
                Dictionary<string, string> labels;
                if (RussianUiData.Owners.TryGetValue(Identity(text.gameObject), out labels)) Bind(text, labels, false, false);
            }
            foreach (Dropdown dropdown in Resources.FindObjectsOfTypeAll<Dropdown>())
            {
                if (dropdown == null || !dropdown.gameObject.scene.IsValid()) continue;
                string identity = Identity(dropdown.gameObject);
                if (identity == "0:Canvas\u001fMenu\u001fDropdown") RussianAttribution.Apply(dropdown);
                else if (RussianUiData.DropdownOwners.Contains(identity)) RegisterDropdown(dropdown);
            }
        }

        public static void ApplyTo(GameObject owner)
        {
            if (!russian || owner == null) return;
            Dictionary<string, string> labels;
            if (RussianUiData.Owners.TryGetValue(Identity(owner), out labels)) Bind(owner.GetComponent<Text>(), labels, false, false);
        }

        // Caller supplies only the port's newly created control overlay root.
        public static void ApplyOverlay(GameObject root)
        {
            if (root == null) return;
            foreach (Text text in root.GetComponentsInChildren<Text>(true)) Bind(text, RussianUiData.Labels, false, true);
        }

        public static void TranslateOpenDropdown(Dropdown dropdown)
        {
            if (!russian || dropdown == null || !dropdown.IsActive() || !dropdown.IsInteractable()) return;
            // Show is the same idempotent native-UI operation used by Dropdown's own event handler.
            // Calling it first makes callback component order irrelevant and prevents an English frame.
            dropdown.Show();
            foreach (Text text in dropdown.GetComponentsInChildren<Text>(true))
                Bind(text, RussianUiData.Labels, text == dropdown.captionText, false);
        }
    }

    // No Update/Awake/Start callbacks; EventSystem dispatches the existing pointer/submit route.
    public sealed class RussianDropdownPresentation : MonoBehaviour, IPointerClickHandler, ISubmitHandler
    {
        public Dropdown Owner;
        public void OnPointerClick(PointerEventData data)
        {
            RussianUiPresentation.TranslateOpenDropdown(Owner);
        }
        public void OnSubmit(BaseEventData data) { RussianUiPresentation.TranslateOpenDropdown(Owner); }
    }
}
