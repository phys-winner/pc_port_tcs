using System;

namespace UnityEngine
{
    public enum RuntimePlatform { WindowsPlayer=2, Android=11 }
    public enum RenderingPath { Forward=1, DeferredShading=3 }
    [Flags] public enum DepthTextureMode { None=0, Depth=1, DepthNormals=2, MotionVectors=4 }
    public static class Application { public static RuntimePlatform platform=RuntimePlatform.Android; }
    public struct Scene { public int buildIndex; }
    public class GameObject { public Scene scene=new Scene{buildIndex=1}; public bool activeInHierarchy=true; }
    public class Behaviour
    {
        bool value=true;
        public virtual bool enabled { get{return value;} set{this.value=value;} }
        public GameObject gameObject=new GameObject();
        public bool isActiveAndEnabled { get{return enabled && gameObject.activeInHierarchy;} }
    }
    public class MonoBehaviour:Behaviour
    {
        public Camera camera; public int componentReads;
        public T GetComponent<T>() where T:class { ++componentReads; return camera as T; }
    }
    public class Camera:Behaviour
    {
        public RenderingPath actualRenderingPath=RenderingPath.DeferredShading;
        public DepthTextureMode depthTextureMode;
    }
    public struct Color
    {
        public float r,g,b,a;
        public Color(float red,float green,float blue,float alpha){r=red;g=green;b=blue;a=alpha;}
    }
}
