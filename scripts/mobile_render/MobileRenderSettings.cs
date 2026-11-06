using System;
using UnityEngine;

namespace ClosingShift.Android
{
    // Output stays at the display resolution. Only the world camera uses RenderPercent.
    public static class MobileRenderSettings
    {
        const string PercentKey = "AndroidRenderPercent";
        static bool initialized, percentLoaded;
        static Resolution native;
        static int percent = PureScalePolicy.DefaultPercent;

        public static int RenderPercent
        {
            get
            {
                if (!percentLoaded)
                {
                    percentLoaded = true;
                    percent = Math.Max(PureScalePolicy.MinimumPercent, Math.Min(100, PlayerPrefs.GetInt(PercentKey, PureScalePolicy.DefaultPercent)));
                }
                return percent;
            }
        }

        public static void SetRenderPercent(int value)
        {
            if (value < PureScalePolicy.MinimumPercent || value > 100) throw new ArgumentOutOfRangeException("value");
            if (RenderPercent == value) return;
            percent = value;
            PlayerPrefs.SetInt(PercentKey, value);
            PlayerPrefs.Save();
        }

        public static void Initialize()
        {
            if (Application.platform != RuntimePlatform.Android) return;
            Display display = Display.main;
            int width = display == null ? 0 : display.systemWidth;
            int height = display == null ? 0 : display.systemHeight;
            if (width < 2 || height < 2)
            {
                Resolution current = Screen.currentResolution;
                width = current.width; height = current.height;
            }
            if (width < 2 || height < 2) return;
            // Some devices report the fixed portrait panel dimensions for a landscape surface.
            if ((Screen.width >= Screen.height) != (width >= height)) { int swap = width; width = height; height = swap; }
            bool changed = !initialized || native.width != width || native.height != height;
            native = new Resolution { width = width, height = height, refreshRate = Screen.currentResolution.refreshRate };
            initialized = true;
            if (Screen.width != width || Screen.height != height || !Screen.fullScreen)
                Screen.SetResolution(width, height, true);
            if (changed) Debug.Log("[MobileRender] native=" + width + "x" + height + " fullscreen=true renderPercent=" + RenderPercent);
        }

        public static Resolution GetNativeResolution()
        {
            Initialize();
            return initialized ? native : Screen.currentResolution;
        }

        public static Resolution GetRenderResolution()
        {
            Resolution value = GetNativeResolution();
            int width, height;
            if (!PureScalePolicy.TryChoosePercent(value.width, value.height, RenderPercent, out width, out height)) return default(Resolution);
            value.width = width; value.height = height;
            return value;
        }

        public static void SetResolution(int width, int height, bool fullscreen)
        {
            if (Application.platform == RuntimePlatform.Android) Initialize();
            else Screen.SetResolution(width, height, fullscreen);
        }

        public static void SetFullScreen(bool fullscreen)
        {
            Screen.fullScreen = Application.platform == RuntimePlatform.Android || fullscreen;
            if (Application.platform == RuntimePlatform.Android) Initialize();
        }

        // Retain existing patched callers while decoupling presets from resolution.
        public static Resolution GetProfileResolution(int requestedShortSide)
        {
            if (requestedShortSide != 360 && requestedShortSide != 540 && requestedShortSide != 720)
                throw new ArgumentOutOfRangeException("requestedShortSide");
            return GetNativeResolution();
        }

        public static void SetProfileShortSide(int requestedShortSide)
        {
            if (Application.platform == RuntimePlatform.Android) GetProfileResolution(requestedShortSide);
        }

        public static Resolution[] GetResolutions()
        {
            if (Application.platform != RuntimePlatform.Android)
            {
                Resolution[] original = Screen.resolutions;
                return original != null && original.Length != 0 ? original : new[] { Screen.currentResolution };
            }
            Resolution value = GetNativeResolution();
            return value.width < 2 || value.height < 2 ? new Resolution[0] : new[] { value };
        }

        public static Resolution GetResolution(Resolution[] resolutions, int index)
        {
            if (Application.platform == RuntimePlatform.Android) return GetNativeResolution();
            if (resolutions == null || resolutions.Length == 0) return Screen.currentResolution;
            return resolutions[Math.Max(0, Math.Min(index, resolutions.Length - 1))];
        }
    }
}
