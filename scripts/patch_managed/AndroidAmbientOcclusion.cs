using System.Collections.Generic;
using HorizonBasedAmbientOcclusion;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidAmbientOcclusion
    {
        sealed class State
        {
            internal HBAO effect;
            internal Camera camera;
            internal bool requested, addedDepth;
        }

        static readonly List<State> effects = new List<State>();

        static void ReleaseDepth(State state)
        {
            if (state.addedDepth && state.camera != null) state.camera.depthTextureMode &= ~DepthTextureMode.Depth;
            state.addedDepth = false;
        }

        internal static void BeginScene()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            foreach (State state in effects)
            {
                if (state.effect != null && state.effect.enabled) state.effect.enabled = false;
                ReleaseDepth(state);
            }
            effects.Clear();
        }

        internal static void Observe(MonoBehaviour component, string type)
        {
            if (Application.platform != RuntimePlatform.Android || type != "HorizonBasedAmbientOcclusion.HBAO") return;
            HBAO effect = component as HBAO;
            if (effect == null) return;
            foreach (State state in effects) if (state.effect == effect) return;
            effects.Add(new State { effect = effect, camera = effect.GetComponent<Camera>() });
        }

        static void Configure(HBAO effect)
        {
            effect.SetPipelineStage(HBAO.PipelineStage.BeforeImageEffectsOpaque);
            effect.SetQuality(HBAO.Quality.Low);
            effect.SetResolution(HBAO.Resolution.Half);
            effect.SetDeinterleaving(HBAO.Deinterleaving.Disabled);
            effect.SetAoPerPixelNormals(HBAO.PerPixelNormals.GBuffer);
            effect.EnableTemporalFilter(false);
            effect.EnableColorBleeding(false);
            effect.SetBlurType(HBAO.BlurType.Narrow);
        }

        internal static bool Enforce(Camera main, bool deferred, int profile)
        {
            if (Application.platform != RuntimePlatform.Android) return false;
            bool active = false;
            bool gameplay = deferred && profile != 4 && main != null && main.isActiveAndEnabled &&
                main.gameObject.scene.buildIndex == 1 && main.actualRenderingPath == RenderingPath.DeferredShading;
            foreach (State state in effects)
            {
                HBAO effect = state.effect;
                if (effect == null || !gameplay || state.camera != main)
                {
                    if (effect != null && effect.enabled) effect.enabled = false;
                    state.requested = false; ReleaseDepth(state); continue;
                }
                if (!state.requested)
                {
                    Configure(effect);
                    // HBAO may disable itself on unsupported hardware; do not retry every frame.
                    state.requested = true;
                    if (!effect.enabled) effect.enabled = true;
                }
                if (effect.enabled)
                {
                    active = true;
                    if ((main.depthTextureMode & DepthTextureMode.Depth) == 0)
                    {
                        main.depthTextureMode |= DepthTextureMode.Depth;
                        state.addedDepth = true;
                    }
                }
                else ReleaseDepth(state);
            }
            return active;
        }
    }
}
