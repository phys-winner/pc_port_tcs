using System;
using System.Collections.Generic;

namespace UnityEngine
{
    public enum RuntimePlatform { WindowsPlayer=2, Android=11 }
    public enum RenderingPath { Forward=1, DeferredShading=3 }
    public enum LightShadows { None, Hard, Soft }
    public enum LightType { Spot, Directional, Point, Area }
    public enum ShadowQuality { Disable, HardOnly, All }
    public static class QualitySettings { public static ShadowQuality shadows=ShadowQuality.All; }
    [Flags] public enum DepthTextureMode { None=0, Depth=1, DepthNormals=2, MotionVectors=4 }
    public static class Application { public static RuntimePlatform platform=RuntimePlatform.Android; }
    public static class SystemInfo { public static int maxComputeBufferInputsCompute=8;public static string graphicsDeviceName="Adreno (TM) 735"; }
    public struct Scene { public int buildIndex; }
    public class GameObject { public Scene scene=new Scene{buildIndex=1}; public bool activeInHierarchy=true; }
    public class Behaviour
    {
        public string name="Component";
        public virtual bool enabled { get; set; }=true;
        public GameObject gameObject=new GameObject();
        public bool isActiveAndEnabled { get{return enabled && gameObject.activeInHierarchy;} }
    }
    public class MonoBehaviour:Behaviour
    {
        public Camera camera; public Light light=new Light(); public int componentReads;
        public T GetComponent<T>() where T:class { ++componentReads; return typeof(T)==typeof(Light)?light as T:camera as T; }
    }
    public class Light:Behaviour
    {
        public LightShadows shadows;
        public LightType type=LightType.Point;
        public Aura2API.AuraLight aura;public int componentReads;
        public T GetComponent<T>() where T:class { ++componentReads;return aura as T; }
    }
    public class Camera:Behaviour
    {
        public RenderingPath actualRenderingPath=RenderingPath.DeferredShading;
        public DepthTextureMode depthTextureMode;
    }
    public struct Vector3Int
    {
        public int x,y,z;
        public Vector3Int(int x,int y,int z){this.x=x;this.y=y;this.z=z;}
    }
    public struct Color
    {
        public float r,g,b,a;
        public Color(float red,float green,float blue,float alpha){r=red;g=green;b=blue;a=alpha;}
    }
    public static class Time
    {
        public static float now;public static int reads;
        public static float unscaledTime { get{++reads;return now;} }
    }
    public static class Debug
    {
        public static readonly List<string> messages=new List<string>();
        public static void Log(object value){messages.Add(value.ToString());}
    }
}
