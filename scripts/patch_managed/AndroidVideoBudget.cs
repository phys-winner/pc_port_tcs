using System;
using UnityEngine;
using UnityEngine.Video;

namespace ClosingShift.Android
{
    internal static class AndroidVideoBudget
    {
        static int scene = Int32.MinValue;
        static VideoPlayer[] players = new VideoPlayer[0];
        static float until = -1;

        internal static int WorldPercent(int sceneHandle, int selected)
        {
            int memory = AndroidDevice.MemoryMB;
            if (Application.platform != RuntimePlatform.Android || memory <= 0 || memory > 3072) return selected;
            if (scene != sceneHandle)
            {
                scene = sceneHandle; until = -1;
                // shortcut: players are scene-authored; rescan if future video prefabs spawn dynamically.
                players = Resources.FindObjectsOfTypeAll<VideoPlayer>();
            }
            foreach (VideoPlayer player in players)
            {
                // Tutorial screens use RenderTexture; world geometry distinguishes them from Canvas movies.
                if (player == null || !player.isActiveAndEnabled || player.gameObject.scene.handle != sceneHandle ||
                    player.GetComponent<MeshRenderer>() == null) continue;
                VideoClip clip = player.clip;
                if (clip == null || clip.width < 720 && clip.height < 720) continue;
                until = Time.unscaledTime + 2;
                break;
            }
            // Keep room for decoder teardown before restoring the larger world buffers.
            return Time.unscaledTime < until ? Math.Min(selected, 50) : selected;
        }
    }
}
