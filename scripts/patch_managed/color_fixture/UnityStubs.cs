using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public enum RuntimePlatform { WindowsPlayer=2, Android=11 }
    public enum ColorSpace { Gamma, Linear }
    public static class Application { public static RuntimePlatform platform=RuntimePlatform.Android; }
    public static class QualitySettings { public static ColorSpace activeColorSpace=ColorSpace.Linear; }
    public static class Mathf { public static float Clamp01(float x){return Math.Clamp(x,0,1);} }
    public class MonoBehaviour { public Camera camera; public bool enabled=false; public T GetComponent<T>() where T:class{return camera as T;} }
    public class Camera { public static Camera main; public string name="MainCamera"; public float aspect=2; }
    public class Texture2D { public string name="LUTAC_horror3"; public int width=1024,height=32; }
    public struct Vector2 { public float x,y; public Vector2(float a,float b){x=a;y=b;} }
    public struct Color { public float r,g,b,a; public Color(float red,float green,float blue,float alpha){r=red;g=green;b=blue;a=alpha;} }
    public struct Vector4
    {
        public float x,y,z,w;
        public Vector4(float a,float b,float c,float d){x=a;y=b;z=c;w=d;}
    }
    public class Material
    {
        public bool property=true,vignetteProperties=true; public int writes;
        public Texture2D texture;public Vector4 parameters,vignette,vignetteColor;
        public bool HasProperty(string name){return property&&(name=="_ClosingShiftLut"||(vignetteProperties&&(name=="_ClosingShiftVignette"||name=="_ClosingShiftVignetteColor")));}
        public void SetTexture(string name,Texture2D value){if(name!="_LUTTex")throw new Exception(name);texture=value;writes++;}
        public void SetVector(string name,Vector4 value){if(!HasProperty(name))throw new Exception(name);if(name=="_ClosingShiftLut")parameters=value;else if(name=="_ClosingShiftVignette")vignette=value;else if(name=="_ClosingShiftVignetteColor")vignetteColor=value;else throw new Exception(name);writes++;}
    }
    public static class Debug {public static readonly List<string> logs=new List<string>();public static void Log(string text){logs.Add(text);}}
}
