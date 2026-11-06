using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering.PostProcessing;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    // Keep the authored HDR display curve without the desktop effect stack.
    public static class AndroidGraphics
    {
        public static void SetSupporterActive(GameObject target, bool active)
        {
            // The main-menu instance has the toggle but no in-world supporter-content object.
            if(target!=null)target.SetActive(active);
        }
        static readonly string[] checkboxFields = {
            "VHSEffect", "DepthOfField", "GrainToggle", "BloomToggle", "MotionBlurToggle", "AmbientOccToggle"
        };
        static readonly string[] effectKeys = { "PostP_VHS", "PostP_DepthOfField", "PostP_Grain", "PostP_Bloom", "PostP_MotionBlur", "PostP_AmbientOcclusion" };
        static readonly bool[] effectEnabled = { true, false, false, false, false, false };
        static readonly string[] effectEnglish = { "VHS", "Depth of field", "Film grain", "Bloom", "Motion blur", "Ambient occlusion (AO)" };
        static readonly string[] effectRussian = { "VHS", "Глубина резкости", "Зерно плёнки", "Свечение", "Размытие в движении", "Объёмное затенение (AO)" };
        const string EffectsVersionKey = "AndroidEffectsVersion";
        static readonly List<Behaviour> effects = new List<Behaviour>();
        static readonly List<Behaviour> vhsEffects = new List<Behaviour>();
        // Components never move between GameObjects; resolve each owner camera once per scan.
        static readonly List<Camera> vhsCameras = new List<Camera>();
        static readonly List<Camera> postLayerCameras = new List<Camera>();
        static readonly Dictionary<MonoBehaviour, Toggle[]> options = new Dictionary<MonoBehaviour, Toggle[]>();
        static readonly Dictionary<MonoBehaviour, OptionState> qualityOptions = new Dictionary<MonoBehaviour, OptionState>();
        static readonly List<CameraState> cameras = new List<CameraState>();
        static readonly List<LightState> lights = new List<LightState>();
        static LightState shadowLight;
        static LightState higherShadowLight, higherSecondShadowLight, higherDirectional;
        static float nextShadowUpdate;
        static int shadowProfile = -1;
        static readonly List<PostProcessLayer> postLayers = new List<PostProcessLayer>();
        static readonly List<PostProcessVolume> postVolumes = new List<PostProcessVolume>();
        static readonly Dictionary<PostProcessEffectSettings, Camera> disabledPostEffects = new Dictionary<PostProcessEffectSettings, Camera>();
        static readonly HashSet<PostProcessVolume> preparedVolumes = new HashSet<PostProcessVolume>();
        static readonly int[] renderPercents = { 25, 33, 50, 67, 75, 100 };
        const string VersionKey = "AndroidLowProfileVersion";
        static int profile = 4;
        static bool captured;
        static float authoredLod;
        static int authoredMaxLod;
        static float authoredTextureBudget;
        static int authoredMipReduction;
        static float cameraReportAt;
        static Camera reportedCamera;
        static int reportedProfile = -1;
        static int reportedEffects = -1;
        static Camera reportedEffectsCamera;

        sealed class CameraState
        {
            internal Camera camera;
            internal bool main;
            internal float far, near;
            internal bool forceRT;
        }

        sealed class LightState
        {
            internal Light light;
            internal LightShadows shadows;
        }

        sealed class OptionState
        {
            internal readonly MonoBehaviour owner;
            readonly Dictionary<string, FieldInfo> fields = new Dictionary<string, FieldInfo>();
            internal readonly Dropdown resolutions;
            internal readonly Toggle fullscreen;
            internal bool loaded;
            internal bool displayConfigured;
            internal bool effectsLoaded;
            internal bool resetPending;
            internal bool fpsConfigured;
            internal Text fpsLabel;
            internal Text renderLabel;
            internal int selected;
            internal OptionState(MonoBehaviour value)
            {
                owner = value;
                foreach (FieldInfo field in value.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)) fields[field.Name] = field;
                resolutions = Get("ResolutionDropdown") as Dropdown;
                fullscreen = Get("FullscreenToggle") as Toggle;
            }
            internal object Get(string name) { FieldInfo field; return fields.TryGetValue(name, out field) ? field.GetValue(owner) : null; }
            internal void Set(string name, object value)
            {
                FieldInfo field;
                if (!fields.TryGetValue(name, out field)) throw new MissingFieldException(owner.GetType().FullName, name);
                field.SetValue(owner, value);
            }
            internal void Load()
            {
                if (loaded) return;
                Dictionary<string, string> section = ReadGraphic(Get("configHandler"));
                string version, value; int saved;
                selected = section != null && section.TryGetValue(VersionKey, out version) && version == "1" &&
                    section.TryGetValue("GraphicQuality", out value) && Int32.TryParse(value, out saved) && saved >= 0 && saved <= 5 ? saved : 4;
                loaded = true;
            }
        }

        public static bool GetDefaultEffectEnabled() { return Application.platform != RuntimePlatform.Android; }

        static Dictionary<string, string> ReadGraphic(object config)
        {
            if (config == null) return null;
            MethodInfo read = config.GetType().GetMethod("DeserializeSection", new[] { typeof(string) });
            return read == null ? null : read.Invoke(config, new object[] { "Graphic" }) as Dictionary<string, string>;
        }

        static void LoadEffects(Dictionary<string, string> values, int quality)
        {
            string version, text; bool enabled;
            bool saved = values != null && values.TryGetValue(EffectsVersionKey, out version) && version == "1";
            for (int i = 0; i < effectEnabled.Length; ++i)
                effectEnabled[i] = saved && values.TryGetValue(effectKeys[i], out text) && Boolean.TryParse(text, out enabled) ? enabled : i == 0 || i == 5 && quality != 4;
            effectEnabled[4] &= SystemInfo.supportsMotionVectors;
            // The original typed reader still reads these four keys, including old/corrupt configs.
            if (values != null) for (int i = 2; i < effectKeys.Length; ++i) values[effectKeys[i]] = effectEnabled[i].ToString();
        }

        static void ReadEffectControls(MonoBehaviour owner, OptionState state)
        {
            ApplyOptions(owner);
            Toggle[] toggles = options[owner];
            if(state.resetPending)
            {
                state.resetPending=false;state.effectsLoaded=true;LoadEffects(null,state.selected);
                for(int i=0;i<toggles.Length;++i)if(toggles[i]!=null)toggles[i].SetIsOnWithoutNotify(effectEnabled[i]);
            }
            if (!state.effectsLoaded)
            {
                LoadEffects(ReadGraphic(state.Get("configHandler")), state.selected);
                for (int i = 0; i < toggles.Length; ++i) if (toggles[i] != null) toggles[i].SetIsOnWithoutNotify(effectEnabled[i]);
                state.effectsLoaded = true;
            }
            for (int i = 0; i < toggles.Length; ++i) if (toggles[i] != null) effectEnabled[i] = toggles[i].isOn;
            effectEnabled[4] &= SystemInfo.supportsMotionVectors;
        }

        static void CaptureBaseline()
        {
            if (captured) return;
            captured = true; authoredLod = QualitySettings.lodBias; authoredMaxLod = QualitySettings.maximumLODLevel;
            authoredTextureBudget=QualitySettings.streamingMipmapsMemoryBudget;
            authoredMipReduction=QualitySettings.streamingMipmapsMaxLevelReduction;
        }

        static void ApplyLow()
        {
            bool minimum = profile == 4;
            ShadowQuality shadows = minimum ? ShadowQuality.Disable : ShadowQuality.HardOnly;
            if (QualitySettings.shadows != shadows)
            {
                AndroidVolumetrics.BeforeQualityChange();
                QualitySettings.shadows = shadows;
            }
            QualitySettings.shadowResolution = ShadowResolution.Low;
            QualitySettings.shadowCascades = 0; QualitySettings.shadowDistance = minimum ? 0 : 20;
            QualitySettings.lodBias = minimum ? .6f : 1; QualitySettings.maximumLODLevel = 0;
            QualitySettings.masterTextureLimit = PreparedMipLimit(profile); QualitySettings.antiAliasing = 0;
            QualitySettings.anisotropicFiltering = minimum ? AnisotropicFiltering.Disable : AnisotropicFiltering.Enable;
        }

        static int PreparedMipLimit(int quality)
        {
            // The full scene exhausts RAM and swap on the 3 GB test device.
            int memory=AndroidDevice.MemoryMB;
            return quality==4 && memory>0 && memory<=3072?2:0;
        }

        public static void BeforeConfiguration(MonoBehaviour config)
        {
            if (Application.platform != RuntimePlatform.Android || config == null) return;
            Type type = config.GetType();
            FieldInfo name = type.GetField("ConfigName"), path = type.GetField("savePath");
            string selected = name == null ? null : name.GetValue(config) as string;
            // Match the original HFPS-only PlayerPrefs filename override.
            if (config.GetComponent("HFPS_GameManager") != null && PlayerPrefs.HasKey("GameConfig"))
                selected = PlayerPrefs.GetString("GameConfig");
            // The authored menu uses Data while the real game saves Documents.
            // Share only that named config through its original reader and writer.
            if (path != null && selected == "GameConfig" &&
                path.FieldType.IsEnum && Convert.ToInt32(path.GetValue(config)) == 0)
                path.SetValue(config, Enum.ToObject(path.FieldType, 1));
        }

        public static void ConfigurationLoaded(MonoBehaviour config)
        {
            if (Application.platform != RuntimePlatform.Android || config == null) return;
            Dictionary<string, string> values = ReadGraphic(config);
            string version, selected; int saved;
            profile = values != null && values.TryGetValue(VersionKey, out version) && version == "1" &&
                values.TryGetValue("GraphicQuality", out selected) && Int32.TryParse(selected, out saved) && saved >= 0 && saved <= 5 ? saved : 4;
            // The original typed deserializer runs later; keep its selected profile parseable.
            if (values != null) values["GraphicQuality"] = profile.ToString(System.Globalization.CultureInfo.InvariantCulture);
            LoadEffects(values, profile);
            CaptureBaseline();
            MobileRenderSettings.SetProfileShortSide(profile == 3 ? 540 : profile == 4 ? 360 : 720);
            if (profile == 3 || profile == 4) ApplyLow();
            ApplyCameras();
        }

        internal static void BeginScene()
        {
            AndroidColorGrading.BeginScene();
            AndroidVhsMemory.BeginScene();
            AndroidAmbientOcclusion.BeginScene();
            AndroidVolumetrics.BeginScene();
            cameraReportAt = Time.unscaledTime + .5f; reportedCamera = null; reportedProfile = -1;
            effects.Clear(); vhsEffects.Clear(); vhsCameras.Clear(); postLayerCameras.Clear(); options.Clear(); qualityOptions.Clear(); cameras.Clear(); reportedEffects = -1; reportedEffectsCamera = null;
            postLayers.Clear(); postVolumes.Clear(); disabledPostEffects.Clear(); preparedVolumes.Clear();
            if (Application.platform != RuntimePlatform.Android) return;
            CaptureBaseline();
            // A repeated context scan must capture authored modes, not our previous budget.
            foreach (LightState state in lights) AndroidVolumetrics.SetLightShadows(state.light, state.shadows);
            lights.Clear(); shadowLight = null; higherShadowLight = higherSecondShadowLight = higherDirectional = null; shadowProfile = -1; nextShadowUpdate = 0;
            foreach (Light light in Resources.FindObjectsOfTypeAll<Light>())
                if (light != null && light.gameObject.scene.IsValid())
                    lights.Add(new LightState { light = light, shadows = light.shadows });
            Camera main = Camera.main;
            foreach (Camera camera in Resources.FindObjectsOfTypeAll<Camera>())
                if (camera != null && camera.gameObject.scene.IsValid())
                    cameras.Add(new CameraState { camera = camera, main = camera == main, far = camera.farClipPlane, near = camera.nearClipPlane, forceRT = camera.forceIntoRenderTexture });
            if (profile == 3 || profile == 4) ApplyLow();
            ApplyCameras();
        }

        // Called from the existing context scan, including inactive objects in the loaded scene.
        internal static void Observe(MonoBehaviour component, string type)
        {
            AndroidColorGrading.Observe(component, type);
            AndroidAmbientOcclusion.Observe(component, type);
            AndroidVolumetrics.Observe(component, type);
            switch (type)
            {
                case "UnityEngine.Rendering.PostProcessing.PostProcessLayer": postLayers.Add((PostProcessLayer)component); postLayerCameras.Add(component.GetComponent<Camera>()); break;
                case "UnityEngine.Rendering.PostProcessing.PostProcessVolume": postVolumes.Add((PostProcessVolume)component); break;
                case "BeautifyEffect.Beautify":
                case "Colorful.Letterbox":
                case "Colorful.DoubleVision":
                case "Colorful.Glitch":
                case "UnityEngine.PostProcessing.Blur":
                case "CameraBloodEffect":
                    effects.Add(component);
                    break;
                case "postVHSPro": vhsEffects.Add(component); vhsCameras.Add(component.GetComponent<Camera>()); AndroidVhsMemory.Observe(component); break;
                case "UICustomOptions": ApplyOptions(component); break;
            }
        }

        public static void ApplyOptions(MonoBehaviour owner)
        {
            if (Application.platform != RuntimePlatform.Android || owner == null) return;
            Toggle[] toggles;
            if (!options.TryGetValue(owner, out toggles))
            {
                var view = new AndroidContext.View(owner);
                toggles = new Toggle[checkboxFields.Length];
                for (int i = 0; i < toggles.Length; ++i) toggles[i] = view.Get(checkboxFields[i]) as Toggle;
                options.Add(owner, toggles);
                for (int i = 0; i < toggles.Length; ++i) if (toggles[i] != null) toggles[i].SetIsOnWithoutNotify(effectEnabled[i]);
                LayoutEffectControls(owner, toggles);
            }
            SetCheckboxes(toggles);
        }

        static OptionState State(MonoBehaviour owner)
        {
            OptionState state;
            if (!qualityOptions.TryGetValue(owner, out state)) { state = new OptionState(owner); qualityOptions.Add(owner, state); }
            state.Load();
            return state;
        }

        // The original ApplyGraphic/SerializeGraphic methods remain the only config writer.
        public static void BeforeApplyQuality(MonoBehaviour owner)
        {
            if (Application.platform != RuntimePlatform.Android || owner == null) return;
            CaptureBaseline();
            OptionState state = State(owner);
            PrepareUi(state);
            profile = state.selected;
            ReadEffectControls(owner, state);
            MobileRenderSettings.SetProfileShortSide(profile == 3 ? 540 : profile == 4 ? 360 : 720);
            if (profile == 3 || profile == 4)
                ApplyLow();
            else
            {
                if (captured) { QualitySettings.lodBias = authoredLod; QualitySettings.maximumLODLevel = authoredMaxLod; }
                var distance = state.Get("ShadowDistanceSlider") as Slider;
                if (distance != null) QualitySettings.shadowDistance = distance.value;
            }
            ApplyCameras();
            // The original graphics body writes global shadow quality after this prefix returns.
            AndroidVolumetrics.BeforeQualityChange();
        }

        public static void BeforeSerializeQuality(MonoBehaviour owner)
        {
            if (Application.platform != RuntimePlatform.Android || owner == null) return;
            OptionState state = State(owner);
            PrepareUi(state, false);
            profile = state.selected;
            ReadEffectControls(owner, state);
        }

        public static void ResetQuality(MonoBehaviour owner)
        {
            if (Application.platform != RuntimePlatform.Android || owner == null) return;
            OptionState state = State(owner);
            state.selected = 4;
            LoadEffects(null, 4); ApplyOptions(owner);
            Toggle[] toggles = options[owner];
            for (int i = 0; i < toggles.Length; ++i) if (toggles[i] != null) toggles[i].SetIsOnWithoutNotify(effectEnabled[i]);
            state.effectsLoaded = true;
            BeforeApplyQuality(owner);
            // The authored reset writes its legacy effect defaults after this prefix hook.
            state.resetPending=true;
        }

        public static void SetLegacyResolutionSelection(Dropdown dropdown, int value)
        {
            // Original Reset(false) writes index0 after its prefix hooks. Android's
            // dropdown now selects render percent, so that write must not change it.
            if (Application.platform != RuntimePlatform.Android) dropdown.value = value;
        }

        public static void SetLegacyFpsSelection(Dropdown dropdown, int value)
        {
            // The original VSync writers must not change the cap selected in this reused row.
            if (Application.platform != RuntimePlatform.Android) dropdown.value = value;
        }

        public static void QualitySelected(MonoBehaviour owner, Dropdown dropdown)
        {
            if (Application.platform != RuntimePlatform.Android || owner == null || dropdown == null) return;
            OptionState state = State(owner);
            state.selected = Math.Max(0, Math.Min(5, dropdown.value));
            PrepareUi(state);
        }

        public static Dictionary<string, string> PrepareConfig(Dictionary<string, string> values)
        {
            if (Application.platform == RuntimePlatform.Android)
            {
                values[VersionKey] = "1"; values[EffectsVersionKey] = "1";
                for (int i = 0; i < effectKeys.Length; ++i) values[effectKeys[i]] = effectEnabled[i].ToString();
            }
            return values;
        }

        static void DropdownLevel(OptionState state, string control, string field, int value, string dirty)
        {
            var dropdown = state.Get(control) as Dropdown;
            if (dropdown != null) dropdown.SetValueWithoutNotify(value);
            state.Set(field, value);
            if (dirty != null) state.Set(dirty, true);
        }

        static void PrepareUi(OptionState state, bool prepareResolution = true)
        {
            bool low = state.selected == 3 || state.selected == 4;
            DropdownLevel(state, "QualityDropdown", "QualityLevel", state.selected, "qChanged");
            if (state.selected != 5) DropdownLevel(state, "TextureQualityDropdown", "TextureQualityLevel", PreparedMipLimit(state.selected), "tqChanged");
            if (low)
            {
                state.Set("custom", false);
                DropdownLevel(state, "AntialiasingDropdown", "AntialiasingLevel", 0, "antiaChanged");
                DropdownLevel(state, "AnisotropicDropdown", "AnisotropicLevel", state.selected == 4 ? 0 : 1, "anisoChanged");
                DropdownLevel(state, "BlendWeightsDropdown", "BlendWeightsLevel", 2, "bwChanged");
                DropdownLevel(state, "VSyncDropdown", "VSyncLevel", 0, "vsyncChanged");
                DropdownLevel(state, "ShadowResolutionDropdown", "ShadowResolutionLevel", 0, "swResChanged");
                DropdownLevel(state, "ShadowQualityDropdown", "ShadowQualityLevel", state.selected == 4 ? 0 : 1, "swQChanged");
                DropdownLevel(state, "ShadowCascadesDropdown", "ShadowCascadesLevel", 0, "swCasChanged");
                DropdownLevel(state, "ShadowProjecionDropdown", "ShadowProjecionLevel", 1, "swProjChanged");
                var distance = state.Get("ShadowDistanceSlider") as Slider;
                if (distance != null) distance.SetValueWithoutNotify(state.selected == 4 ? 0 : 20);
            }
            if (!low)
            {
                var shadowResolution = state.Get("ShadowResolutionDropdown") as Dropdown;
                DropdownLevel(state, "ShadowResolutionDropdown", "ShadowResolutionLevel", shadowResolution == null ? 1 : Math.Max(0, Math.Min(1, shadowResolution.value)), "swResChanged");
                DropdownLevel(state, "ShadowCascadesDropdown", "ShadowCascadesLevel", 0, "swCasChanged");
                var distance = state.Get("ShadowDistanceSlider") as Slider;
                if (distance != null && distance.value > 30) distance.SetValueWithoutNotify(30);
            }
            // SerializeGraphic writes no display fields. Avoid native display initialization while saving.
            if (!prepareResolution) return;
            Resolution resolution = MobileRenderSettings.GetProfileResolution(state.selected == 3 ? 540 : state.selected == 4 ? 360 : 720);
            state.Set("Resolutions", new[] { resolution }); state.Set("ResolutionLevel", 0); state.Set("resChanged", false);
            state.Set("fullscreen", true); state.Set("fullscreenChanged", true);
            var resolutions = state.Get("ResolutionDropdown") as Dropdown;
            if (resolutions != null && !state.displayConfigured)
            {
                resolutions.ClearOptions(); resolutions.options.Add(new Dropdown.OptionData(resolution.width + "x" + resolution.height));
                resolutions.SetValueWithoutNotify(0); resolutions.RefreshShownValue();
            }
        }

        static void ApplyCameras()
        {
            bool low = profile == 3 || profile == 4;
            int pixelLights = low ? 1 : 3;
#if CLOSING_SHIFT_PERF
            if (AndroidPerfProbe.PixelLightsOverride >= 0) pixelLights = AndroidPerfProbe.PixelLightsOverride;
#endif
            if (QualitySettings.pixelLightCount != pixelLights) QualitySettings.pixelLightCount = pixelLights;
            // Minimum has a low-memory budget; Custom retains the user's mip limit.
            int mipLimit=PreparedMipLimit(profile);
            if (profile != 5 && QualitySettings.masterTextureLimit != mipLimit) QualitySettings.masterTextureLimit = mipLimit;
            if(captured)
            {
                float budget=mipLimit>0?Math.Min(authoredTextureBudget,128):authoredTextureBudget;
                int reduction=mipLimit>0?Math.Max(authoredMipReduction,4):authoredMipReduction;
                if(QualitySettings.streamingMipmapsMemoryBudget!=budget)QualitySettings.streamingMipmapsMemoryBudget=budget;
                if(QualitySettings.streamingMipmapsMaxLevelReduction!=reduction)QualitySettings.streamingMipmapsMaxLevelReduction=reduction;
            }
            // Legacy particle materials then avoid depth-sampling variants in later sequences too.
            if (QualitySettings.softParticles) QualitySettings.softParticles = false;
            Camera main = Camera.main;
            bool deferred = AndroidDevice.DeferredCapable;
            foreach (CameraState state in cameras)
            {
                Camera camera = state.camera; if (camera == null) continue;
                if (main != null) state.main = camera == main;
                // Limit the dense-lighting path to the GPUs selected for hardware validation.
                RenderingPath path = deferred && state.main && camera.gameObject.scene.buildIndex == 1 ? RenderingPath.DeferredShading : RenderingPath.Forward;
                bool hdr = state.main;
#if CLOSING_SHIFT_PERF
                if (state.main && AndroidPerfProbe.PathOverride >= 0) path = AndroidPerfProbe.PathOverride == 1 ? RenderingPath.DeferredShading : RenderingPath.Forward;
                if (state.main && AndroidPerfProbe.HdrOverride >= 0) hdr = AndroidPerfProbe.HdrOverride == 1;
#endif
                if (camera.renderingPath != path) camera.renderingPath = path;
                if (camera.allowHDR != hdr) camera.allowHDR = hdr;
                if (state.main)
                {
                    float far = camera.gameObject.scene.buildIndex == 1 ? (low ? 80 : Math.Min(160, state.far)) : state.far;
                    if (camera.farClipPlane != far) camera.farClipPlane = far;
                    // The desktop 1cm near plane wastes depth precision on closely spaced outdoor surfaces.
                    float near = camera.gameObject.scene.buildIndex == 1 ? Math.Max(.1f, state.near) : state.near;
                    if (camera.nearClipPlane != near) camera.nearClipPlane = near;
                    // Effects below request only the depth input they use.
                    DepthTextureMode depth = camera.gameObject.scene.buildIndex != 1 ? DepthTextureMode.None :
                        (effectEnabled[1] || effectEnabled[4] ? DepthTextureMode.Depth : DepthTextureMode.None) |
                        (effectEnabled[4] ? DepthTextureMode.MotionVectors : DepthTextureMode.None);
                    bool force = state.forceRT;
                    if (camera.depthTextureMode != depth) camera.depthTextureMode = depth;
                    if (camera.forceIntoRenderTexture != force) camera.forceIntoRenderTexture = force;
                }
            }
            ApplyLightShadows(main);
            bool ao = AndroidAmbientOcclusion.Enforce(main, deferred, effectEnabled[5] ? 3 : 4);
            bool volume = AndroidVolumetrics.Enforce(main, deferred, profile);
            // Releasing AO/Aura must not remove a depth input now shared with PPS effects.
            if (main != null && (ao || volume || main.gameObject.scene.buildIndex == 1 && (effectEnabled[1] || effectEnabled[4])))
                main.depthTextureMode |= DepthTextureMode.Depth;
        }

        static float ShadowDistanceSquared(LightState state, Camera main, float limit = 20)
        {
            Light light = state == null ? null : state.light;
            if (main == null || light == null || !light.isActiveAndEnabled || state.shadows == LightShadows.None ||
                (light.type != LightType.Point && light.type != LightType.Spot) || light.bakingOutput.lightmapBakeType == LightmapBakeType.Baked)
                return Single.PositiveInfinity;
            float radius = Math.Min(limit, light.range);
            float distance = (light.transform.position - main.transform.position).sqrMagnitude;
            return radius > 0 && distance <= radius * radius ? distance : Single.PositiveInfinity;
        }

        static bool ValidDirectional(LightState state)
        {
            return state != null && state.light != null && state.light.isActiveAndEnabled &&
                state.shadows != LightShadows.None && state.light.type == LightType.Directional;
        }

        static LightState NearestHigherShadow(Camera main, LightState first, LightState second)
        {
            LightState nearest = null;
            float best = Single.PositiveInfinity;
            foreach (LightState state in lights)
            {
                if (state == first || state == second) continue;
                float distance = ShadowDistanceSquared(state, main, 30);
                if (distance < best) { nearest = state; best = distance; }
            }
            return nearest;
        }

        static void UpdateHigherShadows(Camera main)
        {
            bool oneLocal = AndroidDevice.ContainsMaliG715;
            if (Single.IsPositiveInfinity(ShadowDistanceSquared(higherShadowLight, main, 30))) higherShadowLight = null;
            if (higherSecondShadowLight == higherShadowLight || Single.IsPositiveInfinity(ShadowDistanceSquared(higherSecondShadowLight, main, 30))) higherSecondShadowLight = null;
            if (oneLocal) higherSecondShadowLight = null;
            if (higherShadowLight == null) higherShadowLight = NearestHigherShadow(main, null, higherSecondShadowLight);
            if (!oneLocal && higherSecondShadowLight == null) higherSecondShadowLight = NearestHigherShadow(main, higherShadowLight, null);
            // Replace valid slots only for an unselected light at least20% closer than the worst slot.
            for (int replacement = 0; replacement < (oneLocal ? 1 : 2); ++replacement)
            {
                LightState candidate = NearestHigherShadow(main, higherShadowLight, higherSecondShadowLight);
                if (candidate == null) break;
                float first = ShadowDistanceSquared(higherShadowLight, main, 30), second = ShadowDistanceSquared(higherSecondShadowLight, main, 30);
                bool replaceFirst = oneLocal || first >= second;
                if (ShadowDistanceSquared(candidate, main, 30) >= (replaceFirst ? first : second) * .64f) break;
                if (replaceFirst) higherShadowLight = candidate; else higherSecondShadowLight = candidate;
            }
            higherDirectional = null;
            if (main != null)
                foreach (LightState state in lights)
                    if (ValidDirectional(state)) { higherDirectional = state; break; }
        }

        static void ApplyLightShadows(Camera main)
        {
            float currentDistance = ShadowDistanceSquared(shadowLight, main);
            bool invalid = shadowLight != null && Single.IsPositiveInfinity(currentDistance);
            if (profile != 3 && profile != 4)
                invalid |= (higherShadowLight != null && Single.IsPositiveInfinity(ShadowDistanceSquared(higherShadowLight, main, 30))) ||
                    (higherSecondShadowLight != null && Single.IsPositiveInfinity(ShadowDistanceSquared(higherSecondShadowLight, main, 30))) ||
                    (higherDirectional != null && (main == null || !ValidDirectional(higherDirectional)));
            if (shadowProfile == profile && Time.unscaledTime < nextShadowUpdate && !invalid) return;
            nextShadowUpdate = Time.unscaledTime + .25f; shadowProfile = profile;
            LightState nearest = null;
            if (profile == 3)
            {
                float nearestDistance = Single.PositiveInfinity;
                foreach (LightState state in lights)
                {
                    float distance = ShadowDistanceSquared(state, main);
                    if (distance < nearestDistance) { nearest = state; nearestDistance = distance; }
                }
                // Keep a valid current light until its replacement is at least20% closer.
                if (!Single.IsPositiveInfinity(currentDistance) && nearestDistance >= currentDistance * .64f) nearest = shadowLight;
            }
            shadowLight = nearest;
            if (profile != 3 && profile != 4) UpdateHigherShadows(main);
            else higherShadowLight = higherSecondShadowLight = higherDirectional = null;
            foreach (LightState state in lights)
            {
                Light light = state.light; if (light == null) continue;
                LightShadows shadows = profile == 4 ? LightShadows.None : profile == 3 ?
                    (state == shadowLight ? LightShadows.Hard : LightShadows.None) :
                    (state == higherShadowLight || state == higherSecondShadowLight || state == higherDirectional ? state.shadows : LightShadows.None);
                AndroidVolumetrics.SetLightShadows(light, shadows);
            }
        }

        static void SetCheckboxes(Toggle[] toggles)
        {
            for (int i = 0; i < toggles.Length; ++i)
            {
                Toggle toggle = toggles[i];
                if (toggle == null) continue;
                bool supported = i != 4 || SystemInfo.supportsMotionVectors;
                if (toggle.interactable != supported) toggle.interactable = supported;
                if (!supported && toggle.isOn) toggle.SetIsOnWithoutNotify(false);
                if (!toggle.gameObject.activeInHierarchy) continue;
                Transform row = toggle.transform.parent;
                Text label = row == null ? null : row.Find("GraphicLabel") == null ? null : row.Find("GraphicLabel").GetComponent<Text>();
                string title = ClosingShift.Localization.RussianUiPresentation.RussianActive ? effectRussian[i] : effectEnglish[i];
                if (!supported) title += ClosingShift.Localization.RussianUiPresentation.RussianActive ? " (недоступно)" : " (unavailable)";
                if (label != null && label.text != title) label.text = title;
            }
        }

        static void LayoutEffectControls(MonoBehaviour owner, Toggle[] toggles)
        {
            if (toggles[0] == null || toggles[0].transform.parent == null) return;
            Transform content = toggles[0].transform.parent.parent;
            if (content == null) return;
            Transform template = toggles[0].transform.parent.Find("GraphicLabel");
            Text templateText = template == null ? null : template.GetComponent<Text>();
            var view = new AndroidContext.View(owner);
            string[] fields = { "ResolutionDropdown", "QualityDropdown", "TextureQualityDropdown", "VSyncDropdown", "ShadowQualityDropdown" };
            for (int i = 0; i < fields.Length; ++i)
            {
                Component control = view.Get(fields[i]) as Component;
                RectTransform row = control == null ? null : control.transform.parent as RectTransform;
                if (row != null) row.anchoredPosition = new Vector2(0, -34 * i);
            }
            for (int i = 0; i < toggles.Length; ++i)
            {
                if (toggles[i] == null) continue;
                RectTransform row = toggles[i].transform.parent as RectTransform;
                if (row == null) continue;
                row.SetParent(content, false); row.SetAsLastSibling(); row.anchoredPosition = new Vector2(0, -34 * (fields.Length + i));
                row.gameObject.SetActive(true);
                RectTransform target = toggles[i].transform as RectTransform;
                if (target != null) target.sizeDelta = new Vector2(60, 30);
                Transform label = row.Find("GraphicLabel");
                Text text = label == null ? null : label.GetComponent<Text>();
                if (templateText != null && text != null)
                {
                    text.font = templateText.font; text.fontSize = templateText.fontSize;
                    text.resizeTextForBestFit = templateText.resizeTextForBestFit;
                    text.resizeTextMinSize = templateText.resizeTextMinSize; text.resizeTextMaxSize = templateText.resizeTextMaxSize;
                    RectTransform labelRect = text.rectTransform, templateRect = templateText.rectTransform;
                    labelRect.anchorMin = templateRect.anchorMin; labelRect.anchorMax = templateRect.anchorMax; labelRect.pivot = templateRect.pivot;
                    labelRect.anchoredPosition = templateRect.anchoredPosition; labelRect.sizeDelta = templateRect.sizeDelta; labelRect.localScale = templateRect.localScale;
                }
                ColorBlock colors = toggles[i].colors;
                colors.normalColor = new Color(.16f, .18f, .21f, 1); colors.highlightedColor = colors.selectedColor = new Color(.25f, .29f, .34f, 1);
                colors.pressedColor = new Color(.34f, .39f, .45f, 1); colors.colorMultiplier = 1; toggles[i].colors = colors;
                if (toggles[i].targetGraphic != null) toggles[i].targetGraphic.color = Color.white;
            }
            RectTransform area = content as RectTransform;
            if (area != null) area.sizeDelta = new Vector2(area.sizeDelta.x, 34 * (fields.Length + toggles.Length) + 30);
        }

        static void ConfigurePostProcessing()
        {
            foreach (PostProcessVolume volume in postVolumes)
            {
                if (volume == null || !preparedVolumes.Add(volume)) continue;
                var settings = volume.profile;
                Camera camera = volume.GetComponent<Camera>();
                if (camera == Camera.main)
                {
                    Bloom bloom; Grain grain; DepthOfField dof; MotionBlur motion;
                    if (!settings.TryGetSettings(out bloom)) bloom = settings.AddSettings<Bloom>();
                    if (!settings.TryGetSettings(out grain)) grain = settings.AddSettings<Grain>();
                    if (!settings.TryGetSettings(out dof)) dof = settings.AddSettings<DepthOfField>();
                    if (!settings.TryGetSettings(out motion)) motion = settings.AddSettings<MotionBlur>();
                    bloom.fastMode.Override(true); bloom.intensity.Override(.65f); bloom.threshold.Override(1.1f); bloom.diffusion.Override(4); bloom.dirtIntensity.Override(0);
                    grain.intensity.Override(.18f); grain.size.Override(1); grain.colored.Override(false);
                    // shortcut: fixed 2 m focus; add autofocus only if a later scene needs it.
                    dof.kernelSize.Override(KernelSize.Small); dof.focusDistance.Override(2); dof.aperture.Override(5.6f); dof.focalLength.Override(35);
                    motion.shutterAngle.Override(180); motion.sampleCount.Override(4);
                }
                foreach (PostProcessEffectSettings effect in volume.profile.settings)
                {
                    if (effect == null) continue;
                    effect.active = false; disabledPostEffects[effect] = camera;
                }
            }
        }

        static void ConfigureDisplay(OptionState state)
        {
            ConfigureFps(state);
            var resolutions = state.resolutions;
            if (state.displayConfigured && resolutions != null && !resolutions.gameObject.activeInHierarchy) return;
            if (resolutions != null)
            {
                if (!state.displayConfigured)
                {
                    // Run after the original Start, which still parses its native WxH list.
                    resolutions.onValueChanged.RemoveAllListeners();
                    resolutions.ClearOptions();
                    foreach (int value in renderPercents) resolutions.options.Add(new Dropdown.OptionData(value + "%"));
                    int current = MobileRenderSettings.RenderPercent;
                    if (Array.IndexOf(renderPercents, current) < 0) resolutions.options.Add(new Dropdown.OptionData(current + "%"));
                    resolutions.onValueChanged.AddListener(index => {
                        int value;
                        if (index >= 0 && index < resolutions.options.Count &&
                            Int32.TryParse(resolutions.options[index].text.TrimEnd('%'), out value))
                            MobileRenderSettings.SetRenderPercent(value);
                    });
                    Transform labelTransform = resolutions.transform.parent.Find("GraphicLabel");
                    if (labelTransform != null) state.renderLabel = labelTransform.GetComponent<Text>();
                    state.displayConfigured = true;
                }
                if (state.renderLabel != null)
                {
                    string title = ClosingShift.Localization.RussianUiPresentation.RussianActive ? "Процент рендера 3D" : "3D render percentage";
                    if (state.renderLabel.text != title) state.renderLabel.text = title;
                }
                string label = MobileRenderSettings.RenderPercent + "%";
                for (int i = 0; i < resolutions.options.Count; ++i)
                    if (resolutions.options[i].text == label) { resolutions.SetValueWithoutNotify(i); break; }
                if(resolutions.captionText==null || resolutions.captionText.text!=label)resolutions.RefreshShownValue();
            }
            var fullscreen = state.fullscreen;
            if (fullscreen != null)
            {
                fullscreen.SetIsOnWithoutNotify(true);
                fullscreen.interactable = false;
                Transform row = fullscreen.transform.parent;
                if (row != null && row.name == "Fullscreen" && row.gameObject.activeSelf) row.gameObject.SetActive(false);
            }
        }

        static void ConfigureFps(OptionState state)
        {
            var dropdown=state.Get("VSyncDropdown") as Dropdown;
            if(dropdown==null || state.fpsConfigured && !dropdown.gameObject.activeInHierarchy)return;
            if(!state.fpsConfigured || dropdown.options.Count!=AndroidInputSettings.FpsValues.Length || dropdown.options[0].text!="30")
            {
                // I2's original VSync term has three options; this row now owns FPS labels.
                if(dropdown.transform.parent!=null)
                    foreach(MonoBehaviour component in dropdown.transform.parent.GetComponentsInChildren<MonoBehaviour>(true))
                        if(component!=null && component.GetType().FullName=="I2.Loc.Localize")component.enabled=false;
                dropdown.onValueChanged.RemoveAllListeners();dropdown.ClearOptions();
                foreach(int value in AndroidInputSettings.FpsValues)dropdown.options.Add(new Dropdown.OptionData(value==0?"Display":value.ToString()));
                dropdown.onValueChanged.AddListener(index=>{
                    if(index<0 || index>=AndroidInputSettings.FpsValues.Length)return;
                    AndroidInputSettings.FpsMode=index;AndroidInputSettings.Save();
                });
                Transform label=dropdown.transform.parent==null?null:dropdown.transform.parent.Find("GraphicLabel");
                if(label!=null)state.fpsLabel=label.GetComponent<Text>();
                state.fpsConfigured=true;
            }
            bool russian=ClosingShift.Localization.RussianUiPresentation.RussianActive;
            if(state.fpsLabel!=null)state.fpsLabel.text=russian?"Лимит FPS":"FPS limit";
            string display=russian?"Экран":"Display";
            int last=dropdown.options.Count-1;
            if(dropdown.options[last].text!=display){dropdown.options[last].text=display;dropdown.RefreshShownValue();}
            dropdown.SetValueWithoutNotify(AndroidInputSettings.FpsMode);
            if(dropdown.captionText==null || dropdown.captionText.text!=dropdown.options[AndroidInputSettings.FpsMode].text)dropdown.RefreshShownValue();
        }

        internal static void Enforce()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            foreach(var entry in qualityOptions)if(entry.Key!=null && entry.Value.resetPending)ReadEffectControls(entry.Key,entry.Value);
            foreach (Behaviour effect in effects) if (effect != null && effect.enabled) effect.enabled = false;
            ConfigurePostProcessing();
            AndroidColorGrading.Enforce();
            Camera main = Camera.main;
            bool gameplay = main != null && main.gameObject.scene.buildIndex == 1;
            for (int index = 0; index < vhsEffects.Count; ++index)
            {
                Behaviour effect = vhsEffects[index];
                if (effect != null)
                {
                    Camera owner = vhsCameras[index];
                    bool enabled = effectEnabled[0] && owner != null && owner == main;
#if CLOSING_SHIFT_PERF
                    if (AndroidPerfProbe.VhsOverride >= 0) enabled = AndroidPerfProbe.VhsOverride == 1 && owner != null && owner == main;
#endif
                    if (effect.enabled != enabled) effect.enabled = enabled;
                }
            }
            foreach (var entry in disabledPostEffects)
            {
                PostProcessEffectSettings effect = entry.Key;
                if (effect == null) continue;
                bool enabled = gameplay && entry.Value == main && (effect is Bloom ? effectEnabled[3] : effect is Grain ? effectEnabled[2] :
                    effect is DepthOfField ? effectEnabled[1] : effect is MotionBlur && effectEnabled[4]);
                effect.active = enabled; effect.enabled.Override(enabled);
            }
            int effectReport = 0;
            for (int i = 0; i < effectEnabled.Length; ++i) if (effectEnabled[i]) effectReport |= 1 << i;
            if (main != null && (effectReport != reportedEffects || main != reportedEffectsCamera))
            {
                reportedEffects = effectReport; reportedEffectsCamera = main;
                Debug.Log("PORT_EFFECTS camera=" + main.name + " vhs=" + effectEnabled[0] + " dof=" + effectEnabled[1] + " grain=" + effectEnabled[2] +
                    " bloom=" + effectEnabled[3] + " motionBlur=" + effectEnabled[4] + " ao=" + effectEnabled[5] + " motionVectorsSupported=" + SystemInfo.supportsMotionVectors);
            }
            if (main != null && Time.unscaledTime >= cameraReportAt && (reportedCamera != main || reportedProfile != profile))
            {
                reportedCamera = main; reportedProfile = profile;
                int shadowCount = 0;
                if (QualitySettings.shadows != ShadowQuality.Disable)
                    foreach (LightState state in lights)
                        if (state.light != null && state.light.isActiveAndEnabled && state.light.shadows != LightShadows.None) ++shadowCount;
                Debug.Log("PORT_CAMERA profile=" + profile + " camera=" + main.name + " position=" + main.transform.position +
                    " rotation=" + main.transform.eulerAngles + " path=" + main.actualRenderingPath + " mask=" + main.cullingMask +
                    " near=" + main.nearClipPlane + " far=" + main.farClipPlane + " hdr=" + main.allowHDR +
                    " forceRT=" + main.forceIntoRenderTexture + " depth=" + main.depthTextureMode + " shadows=" + QualitySettings.shadows + " shadowLights=" + shadowCount +
                    " textureMip=" + QualitySettings.masterTextureLimit + " textureBudgetMB=" + QualitySettings.streamingMipmapsMemoryBudget +
                    " textureMB=" + Texture.currentTextureMemory/1048576 + " nonStreamingMB=" + Texture.nonStreamingTextureMemory/1048576);
            }
            for (int index = 0; index < postLayers.Count; ++index)
            {
                PostProcessLayer layer = postLayers[index];
                if (layer == null) continue;
                // Mali needs the PPS final output path even when Minimum disables every image effect.
                Camera owner = postLayerCameras[index];
                bool enabled = main != null && owner != null && owner == main;
                // On Adreno the inactive stack only adds a NaN-guard and an R11G11B10 copy (1.4 ms at
                // 1800x810, 3-4% of the main thread): keep it only for FXAA or an enabled PPS effect.
                if (enabled && profile == 4 && !effectEnabled[1] && !effectEnabled[2] && !effectEnabled[3] && !effectEnabled[4] &&
                    AndroidDevice.DeferredCapable && !AndroidDevice.ContainsMaliG715) enabled = false;
#if CLOSING_SHIFT_PERF
                if (AndroidPerfProbe.PpsOverride >= 0) enabled = AndroidPerfProbe.PpsOverride == 1 && main != null && owner != null && owner == main;
#endif
                if (enabled)
                {
                    layer.antialiasingMode = profile == 4 ? PostProcessLayer.Antialiasing.None : PostProcessLayer.Antialiasing.FastApproximateAntialiasing;
                    if (layer.fastApproximateAntialiasing == null) layer.fastApproximateAntialiasing = new FastApproximateAntialiasing();
                    layer.fastApproximateAntialiasing.fastMode = true;
                }
                if (layer.enabled != enabled) layer.enabled = enabled;
            }
            AndroidVhsMemory.Enforce();
            foreach (var entry in options) if (entry.Key != null) SetCheckboxes(entry.Value);
            foreach (var entry in qualityOptions) if (entry.Key != null) ConfigureDisplay(entry.Value);
            ApplyCameras();
        }
    }
}
