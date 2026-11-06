using System;
using System.Collections.Generic;
using ClosingShift.Android;
using UnityEngine;

static class RuntimeSelfCheck
{
    static void Check(bool condition, string description) { if (!condition) throw new Exception(description); }
    public static void Main()
    {
        Screen.width = 1200; Screen.height = 540; Screen.fullScreen = false;
        MobileRenderSettings.Initialize();
        Check(Screen.width == 2400 && Screen.height == 1080 && Screen.fullScreen, "Native fullscreen restores a previously scaled surface");
        Check(MobileRenderSettings.RenderPercent == 75 && MobileRenderSettings.GetRenderResolution().height == 810, "Default75 world render keeps native UI output");
        int calls = Screen.calls;
        MobileRenderSettings.SetRenderPercent(33);
        MobileRenderSettings.SetRenderPercent(33);
        Check(MobileRenderSettings.GetRenderResolution().width == 792 && MobileRenderSettings.GetRenderResolution().height == 356, "Independent percentage produces even RT dimensions");
        Check(Screen.width == 2400 && Screen.height == 1080 && Screen.calls == calls && PlayerPrefs.saves == 1, "Percent saves once and never resizes the surface");
        MobileRenderSettings.SetProfileShortSide(360);
        Check(MobileRenderSettings.RenderPercent == 33 && Screen.calls == calls && MobileRenderSettings.GetProfileResolution(360).height == 1080, "Quality presets cannot change render percent or native resolution");
        Check(MobileRenderSettings.GetResolution(new Resolution[0], 999).width == 2400 && MobileRenderSettings.GetResolutions().Length == 1, "Legacy option reads are native and tolerate old saved indices");
        MobileRenderSettings.SetResolution(800, 360, false);
        MobileRenderSettings.SetFullScreen(false);
        Check(Screen.width == 2400 && Screen.height == 1080 && Screen.fullScreen, "Authored desktop fullscreen and resolution values cannot resize Android output");
        MobileRenderSettings.SetRenderPercent(100);
        Check(MobileRenderSettings.GetRenderResolution().width == 2400 && MobileRenderSettings.GetRenderResolution().height == 1080, "100 percent uses native world dimensions");
        Display.main.systemWidth = 1080; Display.main.systemHeight = 2400;
        Check(MobileRenderSettings.GetNativeResolution().width == 2400, "Portrait panel metrics preserve landscape output orientation");
        Display.main.systemWidth = 1600; Display.main.systemHeight = 720;
        Check(MobileRenderSettings.GetNativeResolution().width == 1600 && Screen.width == 1600 && Screen.height == 720, "Native display changes invalidate cached output dimensions");
        bool rejected = false;
        try { MobileRenderSettings.SetRenderPercent(24); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Check(rejected && MobileRenderSettings.RenderPercent == 100, "Invalid render percentage leaves saved selection intact");
        Application.platform = RuntimePlatform.WindowsPlayer;
        MobileRenderSettings.SetResolution(800, 600, false);
        Check(Screen.width == 800 && Screen.height == 600 && !Screen.fullScreen && Object.ReferenceEquals(MobileRenderSettings.GetResolutions(), Screen.resolutions), "Desktop resolution behavior is preserved");
        Console.WriteLine("PASS: native fullscreen, independent saved percentage, even world target, quality independence, legacy saved indices, orientation/display change and desktop passthrough");
    }
}

namespace UnityEngine
{
    public enum RuntimePlatform { WindowsPlayer, Android }
    public static class Application { public static RuntimePlatform platform = RuntimePlatform.Android; }
    public sealed class Display { public static Display main = new Display(); public int systemWidth = 2400, systemHeight = 1080; }
    public struct Resolution { public int width, height, refreshRate; }
    public static class Screen
    {
        public static int width, height, calls;
        public static bool fullScreen;
        public static Resolution currentResolution = new Resolution { width = 2400, height = 1080, refreshRate = 60 };
        public static Resolution[] resolutions = new[] { currentResolution };
        public static void SetResolution(int w, int h, bool fullscreen) { width = w; height = h; fullScreen = fullscreen; ++calls; }
    }
    public static class PlayerPrefs
    {
        static readonly Dictionary<string, int> values = new Dictionary<string, int>();
        public static int saves;
        public static int GetInt(string key, int fallback) { int value; return values.TryGetValue(key, out value) ? value : fallback; }
        public static void SetInt(string key, int value) { values[key] = value; }
        public static void Save() { ++saves; }
    }
    public static class Debug { public static void Log(object value) { } }
}
