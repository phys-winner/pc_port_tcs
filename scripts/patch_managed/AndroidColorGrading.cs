using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ClosingShift.Android
{
    // Reuse the author's camera LUT and unmasked vignette in the existing BCG/ACES pass.
    internal static class AndroidColorGrading
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly List<MonoBehaviour> sources = new List<MonoBehaviour>();
        static readonly List<MonoBehaviour> displays = new List<MonoBehaviour>();
        static readonly Dictionary<Type, Dictionary<string, FieldInfo>> fieldCache = new Dictionary<Type, Dictionary<string, FieldInfo>>();
        static readonly Dictionary<Type, PropertyInfo> materialCache = new Dictionary<Type, PropertyInfo>();
        static Camera pairedCamera;
        static MonoBehaviour pairedSource, pairedDisplay;
        static Camera boundCamera;
        static Material boundMaterial;
        static Texture2D boundTexture;
        static float boundStrength = -1;
        static Material vignetteMaterial;
        static Vector4 boundVignette, boundVignetteColor;
        static object lastStyle;
        static bool lastStyleHuman;

        internal static void BeginScene()
        {
            sources.Clear(); displays.Clear(); boundCamera = null;
            boundMaterial = null; boundTexture = null; boundStrength = -1;
            pairedCamera = null; pairedSource = pairedDisplay = null;
            vignetteMaterial = null;
        }

        internal static void Observe(MonoBehaviour component, string name)
        {
            if (Application.platform != RuntimePlatform.Android || component == null) return;
            if (name == "BeautifyEffect.Beautify") { sources.Add(component); pairedCamera = null; }
            else if (name == "Colorful.BrightnessContrastGamma") { displays.Add(component); pairedCamera = null; }
        }

        static object Field(object owner, string name)
        {
            if (owner == null) return null;
            Type type = owner.GetType();
            Dictionary<string, FieldInfo> fields;
            if (!fieldCache.TryGetValue(type, out fields))
            {
                fields = new Dictionary<string, FieldInfo>(); fieldCache.Add(type, fields);
            }
            FieldInfo field;
            if (!fields.TryGetValue(name, out field)) { field = type.GetField(name, Fields); fields[name] = field; }
            return field == null ? null : field.GetValue(owner);
        }

        static bool Finite(float value) { return !Single.IsNaN(value) && !Single.IsInfinity(value); }

        static void Vignette(object author, string prefix, Camera camera, out Vector4 parameters, out Vector4 colorFade)
        {
            parameters = new Vector4(.5f, .5f, 1, 0); colorFade = new Vector4(0, 0, 0, 0);
            // shortcut: masked vignettes need an extra texture sample; restore them only if a used scene requires one.
            if (!System.Object.Equals(Field(author, prefix + "vignetting"), true) ||
                Field(author, prefix + "vignettingMask") != null || QualitySettings.activeColorSpace != ColorSpace.Linear) return;
            object colorValue = Field(author, prefix + "vignettingColor"), centerValue = Field(author, prefix + "vignettingCenter");
            object aspectValue = Field(author, prefix + "vignettingAspectRatio"), blinkValue = Field(author, prefix + "vignettingBlink");
            object fadeValue = Field(author, prefix + "vignettingFade");
            if (!(colorValue is Color) || !(centerValue is Vector2) || !(aspectValue is float) || !(blinkValue is float) || !(fadeValue is float)) return;
            Color color = (Color)colorValue; Vector2 center = (Vector2)centerValue;
            float aspect = (float)aspectValue, blink = (float)blinkValue, fade = (float)fadeValue;
            if (!Finite(color.r) || !Finite(color.g) || !Finite(color.b) || !Finite(color.a) || !Finite(center.x) || !Finite(center.y) ||
                !Finite(aspect) || !Finite(blink) || !Finite(fade) || !Finite(camera.aspect) || aspect <= 0 || camera.aspect <= 0) return;
            blink = Mathf.Clamp01(blink);
            bool circular = System.Object.Equals(Field(author, prefix + "vignettingCircularShape"), true);
            aspect = circular && blink <= 0 ? 1f / camera.aspect : aspect + 1.001f / (1.001f - blink) - 1f;
            object style = Field(author, prefix + "vignettingBlinkStyle");
            if (style != null)
            {
                // Boxed enum equality compares values without the per-frame ToString allocation.
                if (!style.Equals(lastStyle)) { lastStyle = style; lastStyleHuman = style.ToString() == "Human"; }
                if (lastStyleHuman) center.y -= blink * .5f;
            }
            float tint = Math.Max(0, 1 - blink * 2);
            if (!Finite(aspect) || !Finite(color.a * 32)) return;
            parameters = new Vector4(center.x, center.y, aspect, color.a * 32);
            colorFade = new Vector4(color.r * tint, color.g * tint, color.b * tint, blink > .99f ? 1 : fade + blink * .5f);
        }

        static void BindVignette(Material material, MonoBehaviour source, object author, string prefix, Camera camera)
        {
            if (!material.HasProperty("_ClosingShiftVignette") || !material.HasProperty("_ClosingShiftVignetteColor")) return;
            // OnEnable loads the profile once; subsequent Blink() animation belongs to the live component.
            if (Field(source, "currentCamera") as Camera == camera) { author = source; prefix = "_"; }
            Vector4 parameters, colorFade;
            Vignette(author, prefix, camera, out parameters, out colorFade);
            if (vignetteMaterial == material && boundVignette.Equals(parameters) && boundVignetteColor.Equals(colorFade)) return;
            material.SetVector("_ClosingShiftVignette", parameters);
            material.SetVector("_ClosingShiftVignetteColor", colorFade);
            vignetteMaterial = material; boundVignette = parameters; boundVignetteColor = colorFade;
        }

        internal static void Enforce()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            Camera main = Camera.main;
            if (main == null) return;
            if (pairedCamera != main || pairedSource == null || pairedDisplay == null)
            {
                pairedSource = pairedDisplay = null;
                foreach (MonoBehaviour candidate in sources)
                    if (candidate != null && candidate.GetComponent<Camera>() == main) { pairedSource = candidate; break; }
                foreach (MonoBehaviour candidate in displays)
                    if (candidate != null && candidate.GetComponent<Camera>() == main) { pairedDisplay = candidate; break; }
                pairedCamera = main;
            }
            MonoBehaviour source = pairedSource, display = pairedDisplay;
            if (source == null || display == null) return;

            // Beautify.OnEnable loads these same public profile fields when sync is enabled.
            // Read the profile directly even when our earlier scan disabled the expensive component.
            object profile = Field(source, "_profile");
            bool sync = System.Object.Equals(Field(source, "_syncWithProfile"), true) && profile != null;
            object author = sync ? profile : source;
            string prefix = sync ? "" : "_";
            Texture2D texture = Field(author, prefix + "lutTexture") as Texture2D;
            object value = Field(author, prefix + "lutIntensity");
            bool enabled = System.Object.Equals(Field(author, prefix + "lut"), true);
            float strength = enabled && texture != null && value is float ? Mathf.Clamp01((float)value) : 0;
            if (texture != null && (texture.height < 2 || texture.width != texture.height * texture.height)) strength = 0;
            if (QualitySettings.activeColorSpace != ColorSpace.Linear) strength = 0;

            Type displayType = display.GetType();
            PropertyInfo property;
            if (!materialCache.TryGetValue(displayType, out property))
            {
                property = displayType.GetProperty("Material", BindingFlags.Instance | BindingFlags.Public);
                materialCache[displayType] = property;
            }
            Material material = property == null ? null : property.GetValue(display, null) as Material;
            if (material == null || !material.HasProperty("_ClosingShiftLut")) return;
            BindVignette(material, source, author, prefix, main);
            if (boundCamera == main && boundMaterial == material && boundTexture == texture && boundStrength == strength) return;
            material.SetTexture("_LUTTex", texture);
            material.SetVector("_ClosingShiftLut", texture == null ? new Vector4(1, 1, 1, 0) :
                new Vector4(1f / texture.width, 1f / texture.height, texture.height - 1, strength));
            boundCamera = main; boundMaterial = material; boundTexture = texture; boundStrength = strength;
            Debug.Log("PORT_COLOR_GRADING camera=" + main.name + " lut=" + (texture == null ? "none" : texture.name) +
                " strength=" + strength + " source=" + (sync ? "profile" : "component"));
        }
    }
}
