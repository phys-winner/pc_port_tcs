using UnityEngine;

namespace ClosingShift.Android
{
    public static class AndroidSnowCompat
    {
        static bool checkedShaders, supported, logged;
        static string reason;

        public static bool AllowSnow(MonoBehaviour owner, Camera camera)
        {
            if (Application.platform != RuntimePlatform.Android || camera == null
                || camera.renderingPath != RenderingPath.DeferredShading) return true;
            if (!checkedShaders) { supported = CheckShaders(); checkedShaders = true; }
            if (supported) return true;
            if (owner != null && owner.enabled)
            {
                // The original OnDisable releases its command buffers, coverage RTs and global snow state.
                owner.enabled = false;
                if (!logged) { logged = true; Debug.Log("PORT_SNOW unsupported=true cleanup=OnDisable reason=" + reason); }
            }
            return false;
        }

        static bool CheckShaders()
        {
            string[] names = { "Hidden/ScreenSpaceSnow", "Hidden/ScreenSpaceSnowOccluders", "Hidden/ScreenSpaceSnowForward" };
            int[] passes = { 6, 4, 2 };
            for (int i = 0; i < names.Length; ++i)
            {
                Shader shader = Shader.Find(names[i]);
                if (shader == null || !shader.isSupported) { reason = names[i] + ":missing-or-unsupported"; return false; }
                Material material = new Material(shader);
                try
                {
                    if (material.passCount < passes[i]) { reason = names[i] + ":passes=" + material.passCount; return false; }
                }
                finally { Object.Destroy(material); }
            }
            return true;
        }
    }
}
