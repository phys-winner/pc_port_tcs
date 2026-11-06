using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ClosingShift.Android
{
    // postVHSPro allocates seven full-screen RenderTextures and never releases them: four use
    // HideAndDontSave, so scene unloading and UnloadUnusedAssets keep every destroyed instance's
    // set alive (each menu -> game load leaked the menu's native-resolution set). Its feedback
    // chain is optional; without feedbackOn only texClear, texPass12 and texTape are sampled.
    internal static class AndroidVhsMemory
    {
        const BindingFlags Fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        static readonly string[] all = { "texPass12", "texPass23", "texLast", "texFeedback", "texFeedback2", "texClear", "texTape" };
        static readonly string[] feedbackOnly = { "texPass23", "texLast", "texFeedback", "texFeedback2" };
        static readonly List<MonoBehaviour> tracked = new List<MonoBehaviour>();
        static readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
        static FieldInfo feedbackOn;
        static int nextCheck;
#if CLOSING_SHIFT_PERF
        internal static bool Enabled = true; // diagnostic A/B switch only
#endif

        internal static void Observe(MonoBehaviour effect)
        {
            if (effect == null || tracked.Contains(effect)) return;
            if (feedbackOn == null)
            {
                Type type = effect.GetType();
                feedbackOn = type.GetField("feedbackOn", Fields);
                foreach (string name in all) fields[name] = type.GetField(name, Fields);
            }
            tracked.Add(effect);
        }

        static RenderTexture Texture(MonoBehaviour effect, string name)
        {
            FieldInfo field;
            return fields.TryGetValue(name, out field) && field != null ? field.GetValue(effect) as RenderTexture : null;
        }

        static long Release(MonoBehaviour effect, string[] names)
        {
            long bytes = 0;
            foreach (string name in names)
            {
                FieldInfo field;
                if (!fields.TryGetValue(name, out field) || field == null) continue;
                RenderTexture texture = field.GetValue(effect) as RenderTexture;
                if ((object)texture == null) continue;
                if (texture != null)
                {
                    bytes += (long)texture.width * texture.height * 4;
                    texture.Release();
                    UnityEngine.Object.Destroy(texture);
                }
                field.SetValue(effect, null);
            }
            return bytes;
        }

        // Scene scan: free the textures of instances destroyed by the previous scene.
        internal static void BeginScene()
        {
#if CLOSING_SHIFT_PERF
            if (!Enabled) return;
#endif
            long bytes = 0; int released = 0;
            for (int i = tracked.Count - 1; i >= 0; --i)
            {
                MonoBehaviour effect = tracked[i];
                if (effect != null) continue;
                if ((object)effect != null && feedbackOn != null) { bytes += Release(effect, all); ++released; }
                tracked.RemoveAt(i);
            }
            if (released > 0) Debug.Log("PORT_PERF vhs destroyed-instances released=" + released + " approxMB=" + bytes / 1048576);
            nextCheck = 0;
        }

        internal static void Enforce()
        {
#if CLOSING_SHIFT_PERF
            if (!Enabled) return;
#endif
            if (feedbackOn == null || Time.frameCount < nextCheck) return;
            nextCheck = Time.frameCount + 30;
            foreach (MonoBehaviour effect in tracked)
            {
                if (effect == null) continue;
                if (!effect.isActiveAndEnabled)
                {
                    // OnRenderImage recreates every texture when texPass12 is missing.
                    if (Texture(effect, "texPass12") != null || Texture(effect, "texTape") != null) Release(effect, all);
                }
                else if (!(bool)feedbackOn.GetValue(effect))
                {
                    if (Texture(effect, "texFeedback") != null || Texture(effect, "texLast") != null) Release(effect, feedbackOnly);
                }
                else if (Texture(effect, "texPass12") != null && (Texture(effect, "texFeedback") == null || Texture(effect, "texLast") == null))
                {
                    // A runtime feedback switch needs the full set again: force CreateTextures.
                    Release(effect, all);
                }
            }
        }
    }
}
