using System.Collections.Generic;
using Aura2API;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidVolumetrics
    {
        sealed class LightState
        {
            internal AuraLight effect;
            internal bool authored, requested;
        }

        sealed class CameraState
        {
            internal AuraCamera effect;
            internal Camera camera;
            internal bool authored, requested, addedDepth, gpuGuardReported;
            internal int probe, frameStart = -1;
            internal float checkAt;
        }

        static readonly List<LightState> lights = new List<LightState>();
        static readonly List<CameraState> cameras = new List<CameraState>();
        static readonly Dictionary<Light, AuraLight> lightEffects = new Dictionary<Light, AuraLight>();

        internal static void SetLightShadows(Light light, LightShadows shadows)
        {
            if (light == null || light.shadows == shadows) return;
            if (Application.platform != RuntimePlatform.Android) { light.shadows = shadows; return; }
            AuraLight effect;
            if (!lightEffects.TryGetValue(light, out effect))
            {
                // Scene restoration can precede Observe; retain even a missing component result for this scene.
                effect = light.GetComponent<AuraLight>();
                lightEffects[light] = effect;
            }
            bool resume = effect != null && effect.isActiveAndEnabled;
            // Aura unregisters shadows using the current mode, so teardown must precede the write.
            if (resume) effect.enabled = false;
            light.shadows = shadows;
            if (resume) effect.enabled = true;
        }

        internal static void BeforeQualityChange()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            foreach (LightState state in lights)
            {
                if (state.effect != null && state.effect.enabled) state.effect.enabled = false;
                state.requested = false;
            }
        }

        static void ReleaseDepth(CameraState state)
        {
            if (state.addedDepth && state.camera != null) state.camera.depthTextureMode &= ~DepthTextureMode.Depth;
            state.addedDepth = false;
        }

        static void Report(CameraState state, string status, string reason, int frameEnd = -1, string initialized = "unchecked")
        {
            if (state.probe == 0 || state.probe == 3) return;
            state.probe = 3;
            // Read the field: the QualitySettings getter can create an asset.
            AuraQualitySettings quality = state.effect == null || state.effect.frustumSettings == null ? null : state.effect.frustumSettings.qualitySettings;
            string grid = quality == null ? "missing" : quality.frustumGridResolution.x + "x" + quality.frustumGridResolution.y + "x" + quality.frustumGridResolution.z;
            Debug.Log("PORT_VOLUME status=" + status + " reason=" + reason + " camera=" + (state.camera == null ? "missing" : state.camera.name) +
                " grid=" + grid + " filter=" + (quality == null ? "missing" : quality.texture3DFiltering.ToString()) +
                " start=" + state.frameStart + " end=" + frameEnd + " initialized=" + initialized);
        }

        static void Probe(CameraState state)
        {
            if ((state.probe != 1 && state.probe != 2) || Time.unscaledTime < state.checkAt) return;
            bool initialized = state.effect.IsInitialized;
            int frame = state.effect.FrameId;
            if (state.probe == 1)
            {
                state.frameStart = frame;
                if (!initialized) Report(state, "FAIL", "not_initialized", frame, "False");
                else { state.probe = 2; state.checkAt = Time.unscaledTime + 1f; }
            }
            else Report(state, initialized && frame > state.frameStart ? "PASS" : "FAIL",
                !initialized ? "lost_initialization" : frame > state.frameStart ? "frame_advanced" : "frame_stalled", frame, initialized.ToString());
        }

        internal static void BeginScene()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            // Context scans can repeat on the same objects; restore their authored enabled baseline before recapture.
            foreach (LightState state in lights)
                if (state.effect != null && state.effect.enabled != state.authored) state.effect.enabled = state.authored;
            foreach (CameraState state in cameras)
            {
                Report(state, "CANCELLED", "scene_reset");
                ReleaseDepth(state);
                if (state.effect != null && state.effect.enabled != state.authored) state.effect.enabled = state.authored;
            }
            lights.Clear(); cameras.Clear(); lightEffects.Clear();
        }

        internal static void Observe(MonoBehaviour component, string type)
        {
            if (Application.platform != RuntimePlatform.Android || component == null) return;
            if (type == "Aura2API.AuraLight")
            {
                AuraLight effect = component as AuraLight;
                if (effect == null) return;
                foreach (LightState state in lights) if (state.effect == effect) return;
                lights.Add(new LightState { effect = effect, authored = effect.enabled });
                Light light = effect.GetComponent<Light>();
                if (light != null) lightEffects[light] = effect;
            }
            else if (type == "Aura2API.AuraCamera")
            {
                AuraCamera effect = component as AuraCamera;
                if (effect == null) return;
                foreach (CameraState state in cameras) if (state.effect == effect) return;
                cameras.Add(new CameraState { effect = effect, camera = effect.GetComponent<Camera>(), authored = effect.enabled });
            }
        }

        internal static bool Enforce(Camera main, bool deferred, int profile)
        {
            if (Application.platform != RuntimePlatform.Android) return false;
            string gpu = SystemInfo.graphicsDeviceName;
            // This GPU repeatedly stalls with Aura; retain the independent deferred/AO path.
            bool blockedGpu = string.Equals(gpu, "Mali-G715", System.StringComparison.OrdinalIgnoreCase);
            bool gameplay = !blockedGpu && deferred && (profile == 0 || profile == 1 || profile == 2 || profile == 5) && main != null && main.isActiveAndEnabled &&
                main.gameObject.scene.buildIndex == 1 && main.actualRenderingPath == RenderingPath.DeferredShading;
            bool hasCamera = false;
            foreach (CameraState state in cameras)
                if (gameplay && state.authored && state.effect != null && state.camera == main && state.effect.gameObject.activeInHierarchy) hasCamera = true;
            // Register light collectors before enabling the camera, and unregister them before camera shutdown.
            foreach (LightState state in lights)
            {
                Behaviour effect = state.effect;
                if (effect == null) continue;
                if (!hasCamera || !state.authored || !effect.gameObject.activeInHierarchy)
                {
                    if (effect.enabled) effect.enabled = false;
                    state.requested = false;
                }
                else if (!state.requested)
                {
                    state.requested = true;
                    if (!effect.enabled) effect.enabled = true;
                }
            }
            bool active = false;
            foreach (CameraState state in cameras)
            {
                AuraCamera effect = state.effect;
                if (effect == null || !gameplay || !state.authored || state.camera != main || !effect.gameObject.activeInHierarchy)
                {
                    Report(state, "CANCELLED", "policy_disabled");
                    if (effect != null && effect.enabled) effect.enabled = false;
                    state.requested = false; ReleaseDepth(state);
                    if (blockedGpu && !state.gpuGuardReported && effect != null && main != null && state.camera == main)
                    {
                        state.gpuGuardReported = true;
                        Debug.Log("PORT_VOLUME status=DISABLED reason=mali_g715_gpu_fence_guard gpu=" + gpu +
                            " camera=" + main.name + " frame=" + effect.FrameId + " cameraEnabled=" + effect.enabled);
                    }
                    continue;
                }
                if (!state.requested)
                {
                    state.requested = true; state.probe = 1; state.frameStart = -1; state.checkAt = Time.unscaledTime + 1f;
                    AuraQualitySettings quality = effect.frustumSettings == null ? null : effect.frustumSettings.qualitySettings;
                    Debug.Log("PORT_VOLUME configure buffers=" + SystemInfo.maxComputeBufferInputsCompute +
                        " occlusion=" + (quality == null ? "missing" : quality.enableOcclusionCulling.ToString()) +
                        " temporal=" + (quality == null ? "missing" : quality.enableTemporalReprojection.ToString()));
                    // Initialize also ORs Depth; capture ownership before invoking OnEnable.
                    if ((main.depthTextureMode & DepthTextureMode.Depth) == 0) state.addedDepth = true;
                    if (!effect.enabled) effect.enabled = true;
                }
                if (effect.enabled)
                {
                    active = true;
                    if ((main.depthTextureMode & DepthTextureMode.Depth) == 0) { main.depthTextureMode |= DepthTextureMode.Depth; state.addedDepth = true; }
                    Probe(state);
                }
                else { ReleaseDepth(state); Report(state, "FAIL", "self_disabled"); }
            }
            return active;
        }
    }
}
