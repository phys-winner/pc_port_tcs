#if CLOSING_SHIFT_PERF
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace ClosingShift.Android
{
    // Diagnostic-only remote control for performance work; never compiled into release builds.
    // Commands are read from <persistentDataPath>/perf/cmd.txt (one per line) and results are
    // appended to <persistentDataPath>/perf/out.txt. Overrides are runtime-only and never saved.
    public sealed class AndroidPerfProbe : MonoBehaviour
    {
        static AndroidPerfProbe instance;
        internal static int PathOverride = -1, HdrOverride = -1, PpsOverride = -1, VhsOverride = -1, ScaleOverride = -1, PixelLightsOverride = -1;
        static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
        string root;
        float nextPoll;
        readonly List<float> samples = new List<float>(16384);
        readonly List<long> allocations = new List<long>(16384);
        float measureUntil = -1;
        string measureLabel;
        int gcAtStart;
        long lastMonoUsed;
        readonly Dictionary<string, List<Behaviour>> disabledBehaviours = new Dictionary<string, List<Behaviour>>();
        readonly List<Light> disabledLights = new List<Light>();
        readonly List<Renderer> disabledRenderers = new List<Renderer>();
        readonly List<Camera> disabledCameras = new List<Camera>();

        void ToggleCamera(string name, bool on)
        {
            int count = 0;
            if (on) { foreach (Camera camera in disabledCameras) if (camera != null && camera.name == name) { camera.enabled = true; ++count; } disabledCameras.RemoveAll(c => c == null || c.name == name); }
            else foreach (Camera camera in Camera.allCameras) if (camera.name == name) { camera.enabled = false; disabledCameras.Add(camera); ++count; }
            Write("CAMERA " + name + " " + (on ? "enabled=" : "disabled=") + count);
        }

        static string PathOf(Transform transform)
        {
            string path = transform.name;
            while (transform.parent != null) { transform = transform.parent; path = transform.name + "/" + path; }
            return path;
        }

        void List(string typeName)
        {
            Type type = FindType(typeName);
            if (type == null) { Write("LIST " + typeName + " type-missing"); return; }
            var text = new StringBuilder("LIST " + typeName);
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(type))
            {
                Component component = found as Component;
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                Behaviour behaviour = component as Behaviour;
                text.Append("\n  ").Append(PathOf(component.transform)).Append(" active=").Append(component.gameObject.activeInHierarchy)
                    .Append(" enabled=").Append(behaviour == null || behaviour.enabled).Append(" pos=").Append(component.transform.position);
            }
            Write(text.ToString());
        }

        void Fields(string typeName, int limit)
        {
            Type type = FindType(typeName);
            if (type == null) { Write("FIELDS " + typeName + " type-missing"); return; }
            var text = new StringBuilder("FIELDS " + typeName);
            int shown = 0;
            foreach (UnityEngine.Object found in Resources.FindObjectsOfTypeAll(type))
            {
                Component component = found as Component;
                if (component == null || !component.gameObject.scene.IsValid()) continue;
                if (shown++ >= limit) break;
                text.Append("\n  [").Append(PathOf(component.transform)).Append("]");
                for (Type current = type; current != null && current != typeof(MonoBehaviour); current = current.BaseType)
                    foreach (var field in current.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly))
                    {
                        object value;
                        try { value = field.GetValue(component); } catch (Exception) { value = "?"; }
                        string shownValue = value == null ? "null" : value is UnityEngine.Object ? ((UnityEngine.Object)value == null ? "destroyed" : ((UnityEngine.Object)value).name + "#" + value.GetType().Name) : value.ToString();
                        if (shownValue.Length > 80) shownValue = shownValue.Substring(0, 80);
                        text.Append("\n    ").Append(field.Name).Append('=').Append(shownValue);
                    }
            }
            Write(text.ToString());
        }

        internal static void Ensure()
        {
            if (instance != null || Application.platform != RuntimePlatform.Android) return;
            var owner = new GameObject("Android perf probe");
            DontDestroyOnLoad(owner);
            instance = owner.AddComponent<AndroidPerfProbe>();
        }

        void Awake()
        {
            root = Path.Combine(Application.persistentDataPath, "perf");
            try { Directory.CreateDirectory(root); } catch (Exception) { }
            Write("READY frame=" + Time.frameCount);
        }

        void Update()
        {
            if (measureUntil >= 0)
            {
                samples.Add(Time.unscaledDeltaTime * 1000f);
                long used = Profiler.GetMonoUsedSizeLong();
                allocations.Add(used - lastMonoUsed);
                lastMonoUsed = used;
                if (Time.unscaledTime >= measureUntil) FinishMeasure();
            }
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + .5f;
            string command = Path.Combine(root, "cmd.txt");
            if (!File.Exists(command)) return;
            string[] lines;
            try { lines = File.ReadAllLines(command); File.Delete(command); }
            catch (Exception error) { Write("ERROR read " + error.GetType().Name + " " + error.Message); return; }
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                try { Execute(line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)); }
                catch (Exception error) { Write("ERROR " + line + " -> " + error); }
            }
        }

        void Write(string text)
        {
            string line = DateTime.UtcNow.ToString("HH:mm:ss.fff", Invariant) + " f" + Time.frameCount + " " + text;
            try { File.AppendAllText(Path.Combine(root, "out.txt"), line + "\n"); } catch (Exception) { }
            Debug.Log("PORT_PERF " + (text.Length > 300 ? text.Substring(0, 300) + "..." : text));
        }

        static int Tri(string value) { return value == "on" || value == "1" ? 1 : value == "off" || value == "0" ? 0 : -1; }

        void Execute(string[] words)
        {
            switch (words[0])
            {
                case "path": PathOverride = words[1] == "deferred" ? 1 : words[1] == "forward" ? 0 : -1; Write("SET path=" + PathOverride); break;
                case "hdr": HdrOverride = Tri(words[1]); Write("SET hdr=" + HdrOverride); break;
                case "pps": PpsOverride = Tri(words[1]); Write("SET pps=" + PpsOverride); break;
                case "vhs": VhsOverride = Tri(words[1]); Write("SET vhs=" + VhsOverride); break;
                case "scale": ScaleOverride = words[1] == "auto" ? -1 : Int32.Parse(words[1], Invariant); Write("SET scale=" + ScaleOverride); break;
                case "behaviour": ToggleBehaviours(words[1], words[2] == "on"); break;
                case "pixellights": PixelLightsOverride = words[1] == "auto" ? -1 : Int32.Parse(words[1], Invariant); Write("SET pixellights=" + PixelLightsOverride); break;
                case "camera": ToggleCamera(words[1], words[2] == "on"); break;
                case "reload": Write("RELOAD " + SceneManager.GetActiveScene().name); AndroidSceneLoader.LoadScene(SceneManager.GetActiveScene().name); break;
                case "vhsfix": AndroidVhsMemory.Enabled = words[1] == "on"; Write("SET vhsfix=" + AndroidVhsMemory.Enabled); break;
                case "volumecull": AndroidRoomCulling.CullLightsAndProbes = words[1] == "on"; Write("SET volumecull=" + AndroidRoomCulling.CullLightsAndProbes); break;
                case "stats": Write("STATS hiddenRenderers=" + AndroidRoomCulling.HiddenCount + " hiddenLights=" + AndroidRoomCulling.HiddenLights + " hiddenProbes=" + AndroidRoomCulling.HiddenProbes + " cones=" + AndroidRoomCulling.ActiveCones + " candidates=" + AndroidRoomCulling.CandidateCount); break;
                case "list": List(words[1]); break;
                case "fields": Fields(words[1], words.Length > 2 ? Int32.Parse(words[2], Invariant) : 1); break;
                case "lights": ToggleLights(words.Length > 2 ? words[1] : "all", words[words.Length - 1] == "on"); break;
                case "renderers": ToggleRenderers(words[1], words[2] == "on"); break;
                case "measure": BeginMeasure(Single.Parse(words[1], Invariant), words.Length > 2 ? String.Join(" ", words, 2, words.Length - 2) : ""); break;
                case "cameras": Cameras(); break;
                case "lightsinfo": LightsInfo(); break;
                case "census": Census(words.Length > 1 ? Int32.Parse(words[1], Invariant) : 60); break;
                case "mem": Memory(words.Length > 1 && words[1] == "full"); break;
                case "gc": GC.Collect(); Resources.UnloadUnusedAssets(); Write("GC requested"); break;
                case "reset": ResetAll(); break;
                default: Write("UNKNOWN " + String.Join(" ", words)); break;
            }
        }

        void BeginMeasure(float seconds, string label)
        {
            samples.Clear(); allocations.Clear();
            measureLabel = label; measureUntil = Time.unscaledTime + seconds;
            gcAtStart = GC.CollectionCount(0); lastMonoUsed = Profiler.GetMonoUsedSizeLong();
            Write("MEASURE_BEGIN " + label + " seconds=" + seconds.ToString(Invariant));
        }

        void FinishMeasure()
        {
            measureUntil = -1;
            if (samples.Count < 3) { Write("MEASURE_EMPTY " + measureLabel); return; }
            samples.RemoveAt(0); allocations.RemoveAt(0);
            float total = 0; foreach (float value in samples) total += value;
            var ordered = new List<float>(samples); ordered.Sort();
            long allocated = 0; int allocFrames = 0;
            foreach (long delta in allocations) if (delta > 0) { allocated += delta; ++allocFrames; }
            Camera main = Camera.main;
            Write("MEASURE " + measureLabel + " frames=" + samples.Count + " meanMs=" + (total / samples.Count).ToString("F2", Invariant) +
                " p50=" + ordered[ordered.Count / 2].ToString("F2", Invariant) + " p90=" + ordered[(int)(ordered.Count * .9f)].ToString("F2", Invariant) +
                " p99=" + ordered[Math.Min(ordered.Count - 1, (int)(ordered.Count * .99f))].ToString("F2", Invariant) + " max=" + ordered[ordered.Count - 1].ToString("F2", Invariant) +
                " gcCount=" + (GC.CollectionCount(0) - gcAtStart) + " allocKBPerFrame=" + (allocated / 1024f / samples.Count).ToString("F2", Invariant) +
                " path=" + (main == null ? "none" : main.actualRenderingPath.ToString()) + " hdr=" + (main != null && main.allowHDR) +
                " target=" + (main == null || main.targetTexture == null ? "screen" : main.targetTexture.width + "x" + main.targetTexture.height + ":" + main.targetTexture.format));
        }

        static Type FindType(string name)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type type = assembly.GetType(name, false);
                if (type != null) return type;
            }
            return null;
        }

        void ToggleBehaviours(string typeName, bool on)
        {
            List<Behaviour> list;
            if (on)
            {
                int restored = 0;
                if (disabledBehaviours.TryGetValue(typeName, out list))
                    foreach (Behaviour behaviour in list) if (behaviour != null) { behaviour.enabled = true; ++restored; }
                disabledBehaviours.Remove(typeName);
                Write("BEHAVIOUR " + typeName + " restored=" + restored);
                return;
            }
            Type type = FindType(typeName);
            if (type == null) { Write("BEHAVIOUR " + typeName + " type-missing"); return; }
            if (!disabledBehaviours.TryGetValue(typeName, out list)) disabledBehaviours[typeName] = list = new List<Behaviour>();
            foreach (UnityEngine.Object found in FindObjectsOfType(type))
            {
                Behaviour behaviour = found as Behaviour;
                if (behaviour != null && behaviour.enabled) { behaviour.enabled = false; list.Add(behaviour); }
            }
            Write("BEHAVIOUR " + typeName + " disabled=" + list.Count);
        }

        void ToggleLights(string kind, bool on)
        {
            if (on)
            {
                foreach (Light light in disabledLights) if (light != null) light.enabled = true;
                Write("LIGHTS restored=" + disabledLights.Count); disabledLights.Clear(); return;
            }
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.enabled) continue;
                bool match = kind == "all" || kind == "point" && light.type == LightType.Point || kind == "spot" && light.type == LightType.Spot ||
                    kind == "local" && (light.type == LightType.Point || light.type == LightType.Spot) || kind == "dir" && light.type == LightType.Directional;
                if (match) { light.enabled = false; disabledLights.Add(light); }
            }
            Write("LIGHTS " + kind + " disabled=" + disabledLights.Count);
        }

        void ToggleRenderers(string kind, bool on)
        {
            if (on)
            {
                foreach (Renderer renderer in disabledRenderers) if (renderer != null) renderer.enabled = true;
                Write("RENDERERS restored=" + disabledRenderers.Count); disabledRenderers.Clear(); return;
            }
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                bool match = kind == "skinned" && renderer is SkinnedMeshRenderer || kind == "particles" && renderer is ParticleSystemRenderer ||
                    kind == "mesh" && renderer is MeshRenderer || kind == "all" || renderer.GetType().Name == kind;
                if (match) { renderer.enabled = false; disabledRenderers.Add(renderer); }
            }
            Write("RENDERERS " + kind + " disabled=" + disabledRenderers.Count);
        }

        void ResetAll()
        {
            PathOverride = HdrOverride = PpsOverride = VhsOverride = ScaleOverride = PixelLightsOverride = -1;
            AndroidRoomCulling.CullLightsAndProbes = true;
            foreach (Camera camera in disabledCameras) if (camera != null) camera.enabled = true;
            disabledCameras.Clear();
            foreach (var entry in new List<string>(disabledBehaviours.Keys)) ToggleBehaviours(entry, true);
            ToggleLights("all", true); ToggleRenderers("all", true);
            Write("RESET");
        }

        static string Describe(RenderTexture texture)
        {
            return texture == null ? "screen" : texture.name + ":" + texture.width + "x" + texture.height + ":" + texture.format + ":d" + texture.depth + ":aa" + texture.antiAliasing;
        }

        void Cameras()
        {
            var text = new StringBuilder("CAMERAS");
            foreach (Camera camera in Camera.allCameras)
            {
                text.Append("\n  ").Append(camera.name).Append(" depth=").Append(camera.depth.ToString(Invariant)).Append(" path=").Append(camera.actualRenderingPath)
                    .Append(" hdr=").Append(camera.allowHDR).Append(" msaa=").Append(camera.allowMSAA).Append(" clear=").Append(camera.clearFlags)
                    .Append(" mask=").Append(camera.cullingMask).Append(" far=").Append(camera.farClipPlane.ToString(Invariant))
                    .Append(" target=").Append(Describe(camera.targetTexture)).Append(" depthTex=").Append(camera.depthTextureMode)
                    .Append(" forceRT=").Append(camera.forceIntoRenderTexture).Append(" occl=").Append(camera.useOcclusionCulling)
                    .Append(" cmdBuffers=").Append(camera.commandBufferCount).Append(" scene=").Append(camera.gameObject.scene.buildIndex)
                    .Append(" behaviours=");
                foreach (MonoBehaviour behaviour in camera.GetComponents<MonoBehaviour>())
                    if (behaviour != null) text.Append(behaviour.GetType().Name).Append(behaviour.enabled ? "+" : "-").Append(',');
            }
            text.Append("\n  pixelLights=").Append(QualitySettings.pixelLightCount).Append(" shadows=").Append(QualitySettings.shadows)
                .Append(" shadowDistance=").Append(QualitySettings.shadowDistance.ToString(Invariant)).Append(" lodBias=").Append(QualitySettings.lodBias.ToString(Invariant))
                .Append(" textureLimit=").Append(QualitySettings.masterTextureLimit).Append(" aniso=").Append(QualitySettings.anisotropicFiltering)
                .Append(" vsync=").Append(QualitySettings.vSyncCount).Append(" target=").Append(Application.targetFrameRate)
                .Append(" realtimeReflectionProbes=").Append(QualitySettings.realtimeReflectionProbes).Append(" softParticles=").Append(QualitySettings.softParticles)
                .Append(" skinWeights=").Append(QualitySettings.skinWeights).Append(" streamingMips=").Append(QualitySettings.streamingMipmapsActive);
            Write(text.ToString());
        }

        void LightsInfo()
        {
            var text = new StringBuilder("LIGHTSINFO");
            Camera main = Camera.main;
            int count = 0;
            foreach (Light light in FindObjectsOfType<Light>())
            {
                if (!light.isActiveAndEnabled) continue;
                ++count;
                float distance = main == null ? -1 : Vector3.Distance(main.transform.position, light.transform.position);
                text.Append("\n  ").Append(light.name).Append(' ').Append(light.type).Append(" mode=").Append(light.renderMode).Append(" bake=").Append(light.bakingOutput.lightmapBakeType)
                    .Append(" range=").Append(light.range.ToString("F1", Invariant)).Append(" intensity=").Append(light.intensity.ToString("F2", Invariant))
                    .Append(" shadows=").Append(light.shadows).Append(" dist=").Append(distance.ToString("F1", Invariant)).Append(" cookie=").Append(light.cookie != null)
                    .Append(" cullMask=").Append(light.cullingMask).Append(" cmd=").Append(light.commandBufferCount);
            }
            text.Append("\n  active=").Append(count);
            Write(text.ToString());
        }

        void Census(int limit)
        {
            var counts = new Dictionary<string, int>();
            foreach (MonoBehaviour behaviour in FindObjectsOfType<MonoBehaviour>())
            {
                if (behaviour == null || !behaviour.enabled) continue;
                string name = behaviour.GetType().FullName;
                int value; counts.TryGetValue(name, out value); counts[name] = value + 1;
            }
            var ordered = new List<KeyValuePair<string, int>>(counts);
            ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
            var text = new StringBuilder("CENSUS behaviours=" + ordered.Count);
            for (int i = 0; i < ordered.Count && i < limit; ++i) text.Append("\n  ").Append(ordered[i].Value).Append(' ').Append(ordered[i].Key);
            int skinned = 0, skinnedVisible = 0, offscreen = 0, bones = 0, meshes = 0, meshVisible = 0, particles = 0, particlesPlaying = 0;
            foreach (Renderer renderer in FindObjectsOfType<Renderer>())
            {
                if (!renderer.enabled) continue;
                var skin = renderer as SkinnedMeshRenderer;
                if (skin != null) { ++skinned; if (skin.isVisible) ++skinnedVisible; if (skin.updateWhenOffscreen) ++offscreen; bones += skin.bones == null ? 0 : skin.bones.Length; }
                else if (renderer is MeshRenderer) { ++meshes; if (renderer.isVisible) ++meshVisible; }
            }
            foreach (ParticleSystem system in FindObjectsOfType<ParticleSystem>()) { ++particles; if (system.isPlaying) ++particlesPlaying; }
            int animators = 0, animatorsAlways = 0;
            foreach (Animator animator in FindObjectsOfType<Animator>()) if (animator.enabled) { ++animators; if (animator.cullingMode == AnimatorCullingMode.AlwaysAnimate) ++animatorsAlways; }
            int probes = 0, realtimeProbes = 0;
            foreach (ReflectionProbe probe in FindObjectsOfType<ReflectionProbe>()) if (probe.enabled) { ++probes; if (probe.mode == ReflectionProbeMode.Realtime) ++realtimeProbes; }
            int canvases = 0; foreach (Canvas canvas in FindObjectsOfType<Canvas>()) if (canvas.enabled && canvas.isRootCanvas) ++canvases;
            int audio = 0; foreach (AudioSource source in FindObjectsOfType<AudioSource>()) if (source.isPlaying) ++audio;
            int rigid = 0, rigidAwake = 0; foreach (Rigidbody body in FindObjectsOfType<Rigidbody>()) { ++rigid; if (!body.IsSleeping()) ++rigidAwake; }
            text.Append("\n  skinned=").Append(skinned).Append(" skinnedVisible=").Append(skinnedVisible).Append(" updateWhenOffscreen=").Append(offscreen).Append(" bones=").Append(bones)
                .Append(" meshRenderers=").Append(meshes).Append(" meshVisible=").Append(meshVisible).Append(" particleSystems=").Append(particles).Append(" playing=").Append(particlesPlaying)
                .Append(" animators=").Append(animators).Append(" alwaysAnimate=").Append(animatorsAlways).Append(" reflectionProbes=").Append(probes).Append(" realtime=").Append(realtimeProbes)
                .Append(" rootCanvases=").Append(canvases).Append(" audioPlaying=").Append(audio).Append(" rigidbodies=").Append(rigid).Append(" awake=").Append(rigidAwake)
                .Append(" gameObjects=").Append(FindObjectsOfType<Transform>().Length);
            Write(text.ToString());
        }

        static long Estimate(Texture texture)
        {
            long bytes = Profiler.GetRuntimeMemorySizeLong(texture);
            if (bytes > 0) return bytes;
            RenderTexture render = texture as RenderTexture;
            if (render != null)
            {
                if (!render.IsCreated()) return 0;
                int pixel = render.format == RenderTextureFormat.ARGBHalf || render.format == RenderTextureFormat.DefaultHDR ? 8 : render.format == RenderTextureFormat.ARGBFloat ? 16 : 4;
                return (long)render.width * render.height * Math.Max(1, render.volumeDepth) * (pixel + (render.depth > 0 ? 4 : 0)) * Math.Max(1, render.antiAliasing);
            }
            return (long)texture.width * texture.height * 4;
        }

        void Memory(bool full)
        {
            var text = new StringBuilder("MEM");
            text.Append(" totalAllocMB=").Append(Mb(Profiler.GetTotalAllocatedMemoryLong())).Append(" totalReservedMB=").Append(Mb(Profiler.GetTotalReservedMemoryLong()))
                .Append(" unusedReservedMB=").Append(Mb(Profiler.GetTotalUnusedReservedMemoryLong())).Append(" monoHeapMB=").Append(Mb(Profiler.GetMonoHeapSizeLong()))
                .Append(" monoUsedMB=").Append(Mb(Profiler.GetMonoUsedSizeLong())).Append(" gfxDriverMB=").Append(Mb(Profiler.GetAllocatedMemoryForGraphicsDriver()))
                .Append(" gcCount=").Append(GC.CollectionCount(0)).Append(" textureMB=").Append(Mb((long)Texture.currentTextureMemory))
                .Append(" desiredTextureMB=").Append(Mb((long)Texture.desiredTextureMemory)).Append(" nonStreamingMB=").Append(Mb((long)Texture.nonStreamingTextureMemory))
                .Append(" streamingTextures=").Append(Texture.streamingTextureCount).Append(" loadedStreaming=").Append(Texture.streamingTextureLoadingCount)
                .Append(" scene=").Append(SceneManager.GetActiveScene().buildIndex).Append(" scenes=").Append(SceneManager.sceneCount);
            Count<Texture2D>(text); Count<RenderTexture>(text); Count<Cubemap>(text); Count<Texture3D>(text); Count<Material>(text); Count<Mesh>(text);
            Count<Shader>(text); Count<AudioClip>(text); Count<AnimationClip>(text); Count<Font>(text); Count<Sprite>(text); Count<GameObject>(text);
            Count<Component>(text); Count<ScriptableObject>(text);
            int instances = 0; foreach (Material material in Resources.FindObjectsOfTypeAll<Material>()) if (material.name.EndsWith("(Instance)", StringComparison.Ordinal)) ++instances;
            int runtimeMeshes = 0; foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>()) if (mesh.name.Length == 0 || mesh.name.EndsWith("Instance", StringComparison.Ordinal)) ++runtimeMeshes;
            int audioLoaded = 0; long audioBytes = 0;
            foreach (AudioClip clip in Resources.FindObjectsOfTypeAll<AudioClip>()) if (clip.loadState == AudioDataLoadState.Loaded) { ++audioLoaded; audioBytes += Profiler.GetRuntimeMemorySizeLong(clip); }
            text.Append(" materialInstances=").Append(instances).Append(" runtimeMeshes=").Append(runtimeMeshes).Append(" audioLoaded=").Append(audioLoaded).Append(" audioMB=").Append(Mb(audioBytes));
            long rtBytes = 0; int rtCreated = 0;
            var rts = new StringBuilder();
            foreach (RenderTexture rt in Resources.FindObjectsOfTypeAll<RenderTexture>())
            {
                if (!rt.IsCreated()) continue;
                ++rtCreated; long bytes = Estimate(rt); rtBytes += bytes;
                rts.Append("\n  RT ").Append(Describe(rt)).Append(" mb=").Append(Mb(bytes));
            }
            text.Append(" rtCreated=").Append(rtCreated).Append(" rtMB=").Append(Mb(rtBytes));
            if (full)
            {
                text.Append(rts);
                var textures = new List<KeyValuePair<long, string>>();
                foreach (Texture2D texture in Resources.FindObjectsOfTypeAll<Texture2D>())
                    textures.Add(new KeyValuePair<long, string>(Estimate(texture), texture.name + ":" + texture.width + "x" + texture.height + ":" + texture.format + ":mips" + texture.mipmapCount));
                textures.Sort((a, b) => b.Key.CompareTo(a.Key));
                for (int i = 0; i < textures.Count && i < 25; ++i) text.Append("\n  TEX ").Append(textures[i].Value).Append(" mb=").Append(Mb(textures[i].Key));
                var meshes = new List<KeyValuePair<long, string>>();
                foreach (Mesh mesh in Resources.FindObjectsOfTypeAll<Mesh>())
                    meshes.Add(new KeyValuePair<long, string>(Profiler.GetRuntimeMemorySizeLong(mesh), mesh.name + ":v" + mesh.vertexCount + ":readable" + mesh.isReadable));
                meshes.Sort((a, b) => b.Key.CompareTo(a.Key));
                for (int i = 0; i < meshes.Count && i < 15; ++i) text.Append("\n  MESH ").Append(meshes[i].Value).Append(" mb=").Append(Mb(meshes[i].Key));
                var names = new Dictionary<string, int>();
                foreach (Material material in Resources.FindObjectsOfTypeAll<Material>())
                    if (material.name.EndsWith("(Instance)", StringComparison.Ordinal)) { int value; names.TryGetValue(material.name, out value); names[material.name] = value + 1; }
                var ordered = new List<KeyValuePair<string, int>>(names);
                ordered.Sort((a, b) => b.Value.CompareTo(a.Value));
                for (int i = 0; i < ordered.Count && i < 15; ++i) text.Append("\n  MATINST ").Append(ordered[i].Value).Append(' ').Append(ordered[i].Key);
            }
            Write(text.ToString());
        }

        static void Count<T>(StringBuilder text) where T : UnityEngine.Object
        {
            text.Append(' ').Append(typeof(T).Name).Append('=').Append(Resources.FindObjectsOfTypeAll<T>().Length);
        }

        static string Mb(long bytes) { return (bytes / 1048576.0).ToString("F1", Invariant); }
    }
}
#endif
