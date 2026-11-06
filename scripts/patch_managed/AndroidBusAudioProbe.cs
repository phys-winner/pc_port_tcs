using System;
using System.Globalization;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidBusAudioProbe
    {
        static AndroidContext.View bus;
        static float nextSample;
        static int logged;
        static string previous;
        static readonly float[] samples = new float[1024];
        static int dspSamples;
        static bool wasPlaying;

        internal static void BeginScene()
        {
            bus = null;
            nextSample = 0;
            logged = 0;
            previous = null;
            dspSamples = 0;
            wasPlaying = false;
        }

        internal static void Observe(MonoBehaviour component, string type)
        {
            if (type == "IT_Car" && component.gameObject.name == "bus") bus = new AndroidContext.View(component);
        }

        internal static void Update()
        {
            if (bus == null || bus.Owner == null || !bus.Owner.gameObject.activeInHierarchy
                || logged >= 20 || Time.unscaledTime < nextSample) return;
            nextSample = Time.unscaledTime + 0.5f;
            AudioSource engine = bus.Get("Engine") as AudioSource;
            AudioClip clip = engine == null ? null : engine.clip;
            bool playing = engine != null && engine.isPlaying;
            if (playing && !wasPlaying) dspSamples = 0;
            wasPlaying = playing;
            string state = "enabled=" + bus.Owner.enabled + " moving=" + bus.Flag("moving")
                + " halted=" + bus.Flag("halted") + " reachedEnd=" + bus.Flag("reachedEnd")
                + " engine=" + (engine != null) + " playing=" + playing
                + " volume=" + (engine == null ? "null" : engine.volume.ToString("R", CultureInfo.InvariantCulture))
                + " mute=" + (engine != null && engine.mute) + " virtual=" + (engine != null && engine.isVirtual)
                + " pitch=" + (engine == null ? "null" : engine.pitch.ToString("R", CultureInfo.InvariantCulture))
                + " loop=" + (engine != null && engine.loop)
                + " spatialBlend=" + (engine == null ? "null" : engine.spatialBlend.ToString("R", CultureInfo.InvariantCulture))
                + " mixer=" + (engine == null || engine.outputAudioMixerGroup == null ? "null" : engine.outputAudioMixerGroup.name)
                + " clip=" + (clip == null ? "null" : clip.name)
                + " loadState=" + (clip == null ? "null" : clip.loadState.ToString())
                + " loadType=" + (clip == null ? "null" : clip.loadType.ToString())
                + " listenerVolume=" + AudioListener.volume.ToString("R", CultureInfo.InvariantCulture)
                + " listenerPaused=" + AudioListener.pause;
            string dsp = "";
            if (playing && bus.Flag("moving") && clip != null && clip.loadState == AudioDataLoadState.Loaded && dspSamples < 4)
            {
                Array.Clear(samples, 0, samples.Length);
                engine.GetOutputData(samples, 0);
                double power = 0;
                float peak = 0;
                foreach (float value in samples) { power += value * value; peak = Mathf.Max(peak, Mathf.Abs(value)); }
                ++dspSamples;
                dsp = " dspSample=" + dspSamples + " dspRms=" + Math.Sqrt(power / samples.Length).ToString("R", CultureInfo.InvariantCulture)
                    + " dspPeak=" + peak.ToString("R", CultureInfo.InvariantCulture);
            }
            if (state == previous && dsp == "") return;
            previous = state;
            ++logged;
            Debug.Log("PORT_BUS_AUDIO " + state + dsp);
        }
    }
}
