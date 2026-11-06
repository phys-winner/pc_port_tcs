using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace ClosingShift.Android
{
    // An explicit target works on GLES, where hardware dynamic resolution is unavailable.
    // Only the gameplay world is scaled; the authored icon, inventory and UI cameras stay native.
    public sealed class AndroidRenderScale : MonoBehaviour
    {
        static AndroidRenderScale instance;
        Camera world, presentation;
        CommandBuffer composite;
        RenderTexture target, authoredTarget;
        RenderTextureFormat targetFormat = RenderTextureFormat.DefaultHDR;
        static readonly int ViewportScale = Shader.PropertyToID("_RenderViewportScaleFactor");
        bool subscribed, recordReported;
        float authoredAspect;
        float retryAt;
        int loggedPercent = -1, loggedWidth, loggedHeight;

        public static void Enforce()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            MobileRenderSettings.Initialize();
            if (instance == null)
            {
                var owner = new GameObject("Android world render scale");
                Object.DontDestroyOnLoad(owner);
                instance = owner.AddComponent<AndroidRenderScale>();
            }
            instance.Apply();
        }

        // PPS publishes its shader resources only from an enabled layer's SetupContext. The
        // presentation copy also serves profiles whose PPS layer stays disabled (AndroidGraphics).
        static void EnsurePostProcessResources()
        {
            const System.Reflection.BindingFlags Internal = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
            var valid = typeof(RuntimeUtilities).GetMethod("isValidResources", Internal);
            if (valid == null || (bool)valid.Invoke(null, null)) return;
            var update = typeof(RuntimeUtilities).GetMethod("UpdateResources", Internal);
            PostProcessResources[] resources = Resources.FindObjectsOfTypeAll<PostProcessResources>();
            if (update == null || resources.Length == 0) return;
            update.Invoke(null, new object[] { resources[0] });
            Debug.Log("[MobileRender] PPS resources initialized for presentation without an active layer");
        }

        bool CreatePresentation()
        {
            Material copy;
            try { EnsurePostProcessResources(); copy = RuntimeUtilities.copyMaterial; }
            catch (System.Exception error)
            {
                Debug.Log("[MobileRender] PPS copy unavailable; using native output: " + error.GetType().Name);
                return false;
            }
            if (copy == null || copy.shader == null || !copy.shader.isSupported)
            {
                Debug.Log("[MobileRender] PPS copy shader unsupported; using native output");
                return false;
            }
            var owner = new GameObject("Native world presentation", typeof(Camera));
            owner.transform.SetParent(transform, false);
            presentation = owner.GetComponent<Camera>();
            presentation.renderingPath = RenderingPath.Forward;
            presentation.clearFlags = CameraClearFlags.SolidColor;
            presentation.backgroundColor = Color.black;
            presentation.cullingMask = 0;
            presentation.orthographic = true;
            presentation.orthographicSize = 1;
            presentation.nearClipPlane = .1f;
            presentation.farClipPlane = 10;
            presentation.allowHDR = presentation.allowMSAA = presentation.useOcclusionCulling = false;
            composite = new CommandBuffer { name = "Native opaque world presentation" };
            presentation.AddCommandBuffer(CameraEvent.AfterEverything, composite);
            if (!subscribed) { Camera.onPreRender += RecordPresentation; subscribed = true; }
            return true;
        }

        void Apply()
        {
            Camera main = Camera.main;
            bool gameplay = main != null && main.isActiveAndEnabled && main.gameObject.scene.buildIndex == 1;
            int selected = MobileRenderSettings.RenderPercent;
#if CLOSING_SHIFT_PERF
            if (AndroidPerfProbe.ScaleOverride > 0) selected = AndroidPerfProbe.ScaleOverride;
#endif
            int percent = gameplay ? AndroidVideoBudget.WorldPercent(main.gameObject.scene.handle, selected) : selected;
            if (!gameplay || percent == 100 && !AndroidRecipeText.Active)
            {
                Detach();
                if (loggedPercent != percent || loggedWidth != Screen.width || loggedHeight != Screen.height)
                    Report(percent, Screen.width, Screen.height, gameplay ? "native" : "menu-native");
                return;
            }
            if (Time.unscaledTime < retryAt) return;

            if (world != main)
            {
                Detach();
                // Preserve authored outputs (security cameras and future scene-specific targets).
                if (main.targetTexture != null) return;
                world = main; authoredTarget = main.targetTexture; authoredAspect = main.aspect;
            }
            if (world.targetTexture != null && world.targetTexture != target) { Detach(); return; }
            Resolution native = MobileRenderSettings.GetNativeResolution();
            int width, height;
            if (!PureScalePolicy.TryChoosePercent(native.width, native.height, percent, out width, out height)) return;
            Resolution render = native;
            render.width = width; render.height = height;
            RenderTextureFormat format = RenderTextureFormat.DefaultHDR;
#if CLOSING_SHIFT_PERF
            if (AndroidPerfProbe.HdrOverride == 0) format = RenderTextureFormat.Default;
#endif
            if (target == null || target.width != render.width || target.height != render.height || !target.IsCreated() || targetFormat != format)
            {
                ReleaseTarget();
                target = new RenderTexture(render.width, render.height, 24, format);
                targetFormat = format;
                target.name = "Android scaled HDR world";
                target.antiAliasing = 1; target.filterMode = FilterMode.Bilinear; target.wrapMode = TextureWrapMode.Clamp;
                target.useMipMap = target.autoGenerateMips = false;
                if (!target.Create())
                {
                    Debug.Log("[MobileRender] target allocation failed; using native output");
                    Detach(); retryAt = Time.unscaledTime + 1; Report(percent, Screen.width, Screen.height, "native-allocation-fallback"); return;
                }
            }
            if (presentation == null && !CreatePresentation())
            {
                Detach(); retryAt = Time.unscaledTime + 1; Report(percent, Screen.width, Screen.height, "native-copy-fallback"); return;
            }
            world.targetTexture = target;
            // Even RT dimensions can differ from the native aspect by a fraction of a pixel.
            world.aspect = native.width / (float)native.height;
            presentation.depth = world.depth + .01f;
            presentation.enabled = true;
            if (loggedPercent != percent || loggedWidth != render.width || loggedHeight != render.height)
                Report(percent, render.width, render.height, "HDR-RT");
        }

        void RecordPresentation(Camera camera)
        {
            if (camera != presentation || composite == null || target == null || world == null || world.targetTexture != target) return;
            if (!recordReported)
            {
                recordReported = true;
                Debug.Log("[MobileRender] present-callback frame=" + Time.frameCount + " world=" + target.width + "x" + target.height +
                    " native=" + Screen.width + "x" + Screen.height + " camera=" + camera.pixelWidth + "x" + camera.pixelHeight +
                    " display=" + (Display.main != null) + " destination=Display.main.colorBuffer");
            }
            // Resolve the current display buffer and PPS material for this presentation frame.
            // Reusing startup commands can keep an unpresented buffer/material on this GLES player.
            composite.Clear();
            composite.SetGlobalFloat(ViewportScale, 1f);
            composite.BlitFullscreenTriangle(target, new RenderTargetIdentifier(Display.main.colorBuffer),
                false, new Rect(0, 0, Screen.width, Screen.height), false);
        }

        void Report(int percent, int width, int height, string mode)
        {
            loggedPercent = percent; loggedWidth = width; loggedHeight = height;
            Debug.Log("[MobileRender] native=" + Screen.width + "x" + Screen.height + " percent=" + percent +
                " world=" + width + "x" + height + " mode=" + mode + " fullscreen=" + Screen.fullScreen);
        }

        void ReleaseTarget()
        {
            recordReported = false;
            if (target != null) AndroidRecipeText.ReleaseTarget(world);
            if (world != null && world.targetTexture == target) world.targetTexture = authoredTarget;
            if (composite != null) composite.Clear();
            if (target != null) { target.Release(); Object.Destroy(target); target = null; }
        }

        void Detach()
        {
            ReleaseTarget();
            if (world != null) world.aspect = authoredAspect;
            world = null; authoredTarget = null;
            if (presentation != null) presentation.enabled = false;
        }

        void OnDestroy()
        {
            if (subscribed) { Camera.onPreRender -= RecordPresentation; subscribed = false; }
            Detach();
            if (composite != null)
            {
                if (presentation != null) presentation.RemoveCommandBuffer(CameraEvent.AfterEverything, composite);
                composite.Release(); composite = null;
            }
            if (instance == this) instance = null;
        }

        static Vector3 ToWorldPixels(Camera camera, Vector3 position)
        {
            if (instance != null && instance.world == camera && instance.target != null && camera.targetTexture == instance.target)
            {
                position.x *= instance.target.width / (float)Screen.width;
                position.y *= instance.target.height / (float)Screen.height;
            }
            return position;
        }

        static Vector3 ToNativePixels(Camera camera, Vector3 position)
        {
            if (instance != null && instance.world == camera && instance.target != null && camera.targetTexture == instance.target)
            {
                position.x *= Screen.width / (float)instance.target.width;
                position.y *= Screen.height / (float)instance.target.height;
            }
            return position;
        }

        // Game IL forwards only these pixel-space calls. UI camera calls pass through unchanged.
        public static Ray ScreenPointToRay(Camera camera, Vector3 position) { return camera.ScreenPointToRay(ToWorldPixels(camera, position)); }
        public static Vector3 ScreenToViewportPoint(Camera camera, Vector3 position) { return camera.ScreenToViewportPoint(ToWorldPixels(camera, position)); }
        public static Vector3 ScreenToWorldPoint(Camera camera, Vector3 position) { return camera.ScreenToWorldPoint(ToWorldPixels(camera, position)); }
        public static Vector3 WorldToScreenPoint(Camera camera, Vector3 position) { return ToNativePixels(camera, camera.WorldToScreenPoint(position)); }
        public static Vector3 ViewportToScreenPoint(Camera camera, Vector3 position) { return ToNativePixels(camera, camera.ViewportToScreenPoint(position)); }
    }
}
