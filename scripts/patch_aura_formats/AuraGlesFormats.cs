using UnityEngine;
using UnityEngine.Rendering;

public static class AuraGlesFormats
{
    private static bool IsAndroidGles
    {
        get { return Application.platform == RuntimePlatform.Android && SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3; }
    }

    public static RenderTextureFormat Occlusion()
    {
        return IsAndroidGles ? RenderTextureFormat.RFloat : RenderTextureFormat.RHalf;
    }

    public static RenderTextureFormat DummySlice()
    {
        return IsAndroidGles ? RenderTextureFormat.RInt : RenderTextureFormat.R8;
    }
}
