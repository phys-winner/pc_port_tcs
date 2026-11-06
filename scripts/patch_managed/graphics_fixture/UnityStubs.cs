using System;
using System.Collections.Generic;
using System.Reflection;

namespace UnityEngine
{
    public enum RuntimePlatform { WindowsPlayer=2, Android=11 }
    public enum RenderingPath { Forward=1, DeferredShading=3 }
    public enum ShadowQuality { Disable, HardOnly, All }
    public enum LightShadows { None, Hard, Soft }
    public enum LightType { Spot, Directional, Point, Area }
    public enum LightmapBakeType { Realtime=4, Baked=2, Mixed=1 }
    public struct LightBakingOutput { public LightmapBakeType lightmapBakeType; }
    public enum AnisotropicFiltering { Disable, Enable, ForceEnable }
    public enum ShadowResolution { Low, Medium, High, VeryHigh }
    public enum ColorSpace { Gamma, Linear }
    [Flags] public enum DepthTextureMode { None=0, Depth=1, DepthNormals=2, MotionVectors=4 }
    public static class Application { public static RuntimePlatform platform=RuntimePlatform.Android; }
    public static class SystemInfo { public static string graphicsDeviceName="Mali-G76";public static int supportedRenderTargetCount=4,maxComputeBufferInputsCompute=8,systemMemorySize=8192;public static bool supportsMotionVectors=true; }
    public struct Scene { public int buildIndex,handle;public bool invalid;public bool IsValid(){return !invalid;} }
    public class Object { public static void DontDestroyOnLoad(Object value){} public static void Destroy(Object value){} }
    public class GameObject:Object
    {
        public static GameObject Find(string path){return null;}
        public Scene scene; public Camera camera; public bool activeSelf=true; public string name; public int layer;
        public bool activeInHierarchy{get{return activeSelf&&(transform.parent==null||transform.parent.gameObject.activeInHierarchy);}set{activeSelf=value;}}
        public Transform transform; readonly Dictionary<Type,object> components=new Dictionary<Type,object>();
        public GameObject(string name="",params Type[] types){this.name=name;transform=new RectTransform(this);foreach(Type t in types)Add(t);}
        internal GameObject(Transform owner){transform=owner;}
        object Add(Type t){var value=Activator.CreateInstance(t);if(value is Behaviour b)b.gameObject=this;components[t]=value;if(value is Camera c)camera=c;return value;}
        public T AddComponent<T>() where T:new(){return (T)Add(typeof(T));}
        public T GetComponent<T>() where T:class{if(typeof(T)==typeof(Camera))return camera as T;object value;return components.TryGetValue(typeof(T),out value)?value as T:null;}
        public void SetActive(bool value){activeSelf=value;}
    }
    public class Component:Object { public GameObject gameObject=new GameObject(); public Transform transform{get{return gameObject.transform;}} public T GetComponent<T>() where T:class{return gameObject.GetComponent<T>();} }
    public class Behaviour:Component { public virtual bool enabled{get;set;}=true;public bool isActiveAndEnabled{get{return enabled&&gameObject.activeInHierarchy;}} }
    public class MonoBehaviour:Behaviour { public string name{get{return gameObject.name;}set{gameObject.name=value;}}public object manager;public object GetComponent(string type){return type=="HFPS_GameManager"?manager:null;} }
    public static class PlayerPrefs
    {
        static readonly Dictionary<string,string> strings=new Dictionary<string,string>();
        public static bool HasKey(string key){return strings.ContainsKey(key);}
        public static string GetString(string key){return strings[key];}
        public static void SetString(string key,string value){strings[key]=value;}
        static readonly Dictionary<string,int> ints=new Dictionary<string,int>();
        public static int saves;public static int GetInt(string key,int fallback){int value;return ints.TryGetValue(key,out value)?value:fallback;}
        public static void SetInt(string key,int value){ints[key]=value;}public static void Save(){++saves;}
        public static void DeleteAll(){strings.Clear();ints.Clear();}
    }
    public enum CameraClearFlags { SolidColor,Nothing }
    public class Camera:Behaviour
    {
        public static Camera main;
        public static event Action<Camera> onPreRender;public static void FirePreRender(Camera camera){if(onPreRender!=null)onPreRender(camera);}
        public static event Action<Camera> onPreCull;public static void FirePreCull(Camera camera){if(onPreCull!=null)onPreCull(camera);}
        public RenderBuffer boundColor,boundDepth;
        public void SetTargetBuffers(RenderBuffer color,RenderBuffer depth){boundColor=color;boundDepth=depth;}
        public Shader replacementShader;public void SetReplacementShader(Shader value,string tag){replacementShader=value;}
        public void CopyFrom(Camera source){targetTexture=source.targetTexture;depth=source.depth;cullingMask=source.cullingMask;allowHDR=source.allowHDR;renderingPath=source.renderingPath;depthTextureMode=source.depthTextureMode;forceIntoRenderTexture=source.forceIntoRenderTexture;}
        public string name="Camera";

        public RenderingPath actualRenderingPath{get{return renderingPath;}}
        public int cullingMask=-1;
        public float nearClipPlane=.3f;
        public RenderingPath renderingPath=RenderingPath.DeferredShading;
        public bool allowHDR=true;
        public float farClipPlane=500;
        public DepthTextureMode depthTextureMode=DepthTextureMode.DepthNormals|DepthTextureMode.MotionVectors;
        public bool forceIntoRenderTexture=true;
        public RenderTexture targetTexture;public float aspect=2400f/1080,depth,orthographicSize;public bool orthographic,allowMSAA=true,useOcclusionCulling=true;public CameraClearFlags clearFlags;public Color backgroundColor;
        public int pixelWidth{get{return targetTexture==null?Screen.width:targetTexture.width;}}public int pixelHeight{get{return targetTexture==null?Screen.height:targetTexture.height;}}
        public readonly Dictionary<UnityEngine.Rendering.CameraEvent,UnityEngine.Rendering.CommandBuffer> commands=new Dictionary<UnityEngine.Rendering.CameraEvent,UnityEngine.Rendering.CommandBuffer>();
        public void AddCommandBuffer(UnityEngine.Rendering.CameraEvent evt,UnityEngine.Rendering.CommandBuffer buffer){commands[evt]=buffer;}public void RemoveCommandBuffer(UnityEngine.Rendering.CameraEvent evt,UnityEngine.Rendering.CommandBuffer buffer){commands.Remove(evt);}
        public Ray ScreenPointToRay(Vector3 value){return new Ray{direction=ScreenToViewportPoint(value)};}
        public Vector3 ScreenToViewportPoint(Vector3 value){return new Vector3(value.x/pixelWidth,value.y/pixelHeight,value.z);}
        public Vector3 ScreenToWorldPoint(Vector3 value){return ScreenToViewportPoint(value);}
        public Vector3 WorldToScreenPoint(Vector3 value){return ViewportToScreenPoint(value);}
        public Vector3 ViewportToScreenPoint(Vector3 value){return new Vector3(value.x*pixelWidth,value.y*pixelHeight,value.z);}
    }
    public class Light:Behaviour
    {
        public LightType type=LightType.Point;
        public LightShadows shadows=LightShadows.Soft;
        public LightBakingOutput bakingOutput=new LightBakingOutput{lightmapBakeType=LightmapBakeType.Realtime};
        public float range=10,intensity=2;

    }
    public struct Vector3Int
    {
        public int x,y,z;
        public Vector3Int(int x,int y,int z){this.x=x;this.y=y;this.z=z;}
    }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public float sqrMagnitude{get{return x*x+y*y+z*z;}}
        public static Vector3 zero{get{return new Vector3();}}
        public static Vector3 operator +(Vector3 a,Vector3 b){return new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);}
        public static Vector3 operator -(Vector3 a,Vector3 b){return new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);}
        public override string ToString(){return "("+x+","+y+","+z+")";}
    }
    public class Transform
    {
        public Vector3 localPosition;public Quaternion localRotation,rotation;
        public void SetPositionAndRotation(Vector3 value,Quaternion facing){position=value;rotation=facing;}
        public void GetComponentsInChildren<T>(bool inactive,List<T> results) where T:class{results.Clear();Collect(inactive,results);}
        void Collect<T>(bool inactive,List<T> results) where T:class{var component=gameObject.GetComponent<T>();if(component!=null&&(inactive||gameObject.activeInHierarchy))results.Add(component);foreach(var child in children)child.Collect(inactive,results);}
        public Vector3 position,eulerAngles,localScale=new Vector3(1,1,1);public GameObject gameObject;public Transform parent;readonly List<Transform> children=new List<Transform>();public string name{get{return gameObject.name;}}
        public Transform(){gameObject=new GameObject(this);}internal Transform(GameObject owner){gameObject=owner;}
        public void SetParent(Transform owner,bool worldPositionStays){if(parent!=null)parent.children.Remove(this);parent=owner;if(owner!=null)owner.children.Add(this);}
        public void SetAsLastSibling(){if(parent!=null){parent.children.Remove(this);parent.children.Add(this);}}public int GetSiblingIndex(){return parent==null?0:parent.children.IndexOf(this);}
        public Transform Find(string name){return children.Find(t=>t.name==name);}public T GetComponent<T>() where T:class{return gameObject.GetComponent<T>();}
        public T[] GetComponentsInChildren<T>(bool includeInactive) where T:class {var values=new List<T>();var value=GetComponent<T>();if(value!=null)values.Add(value);foreach(var child in children)values.AddRange(child.GetComponentsInChildren<T>(includeInactive));return values.ToArray();}
    }
    public struct Vector2 { public float x,y;public Vector2(float x,float y){this.x=x;this.y=y;} }
    public class RectTransform:Transform { public Vector2 anchoredPosition,sizeDelta,anchorMin,anchorMax,pivot;internal RectTransform(GameObject owner):base(owner){} }
    public static class Time { public static float unscaledTime;public static int frameCount; }
    public static class Resources
    {
        public static object[] objects=new object[0];
        public static int scans;
        public static T[] FindObjectsOfTypeAll<T>() {++scans;var values=new List<T>();foreach(object value in objects)if(value is T)values.Add((T)value);return values.ToArray(); }
    }
    public struct RenderBuffer { public int id; }
    public sealed class Display { public static Display main=new Display();public int systemWidth=2400,systemHeight=1080;public RenderBuffer colorBuffer=new RenderBuffer{id=42}; }
    public struct Resolution { public int width,height,refreshRate; }
    public struct Color { public static Color black=new Color(0,0,0,1),white=new Color(1,1,1,1); public float r,g,b,a;public Color(float r,float g,float b,float a){this.r=r;this.g=g;this.b=b;this.a=a;} }
    public static class Mathf
    {
        public static float LinearToGammaSpace(float value){return value<=.0031308f?value*12.92f:(float)(1.055*Math.Pow(value,1/2.4)-.055);}
        public static float GammaToLinearSpace(float value){return value<=.04045f?value/12.92f:(float)Math.Pow((value+.055)/1.055,2.4);}
    }
    public static class Screen
    {
        public static int width=2400,height=1080,calls;public static bool fullScreen=true;
        public static Resolution currentResolution=new Resolution{width=2400,height=1080,refreshRate=60};
        public static Resolution[] resolutions=new[]{currentResolution};
        public static void SetResolution(int w,int h,bool full){width=w;height=h;fullScreen=full;++calls;}
    }
    public static class QualitySettings
    {
        public static bool softParticles=true;
        public static ShadowQuality shadows=ShadowQuality.All;
        public static ColorSpace activeColorSpace=ColorSpace.Linear;
        public static ShadowResolution shadowResolution=ShadowResolution.VeryHigh;
        public static int pixelLightCount=4;
        public static int shadowCascades=4,maximumLODLevel=0,masterTextureLimit=0,antiAliasing=0;
        public static float shadowDistance=150,lodBias=2;
        public static float streamingMipmapsMemoryBudget=512;
        public static int streamingMipmapsMaxLevelReduction=2;
        public static AnisotropicFiltering anisotropicFiltering=AnisotropicFiltering.ForceEnable;
    }
    public static class Debug { public static readonly List<string> messages=new List<string>();public static void Log(object value){messages.Add(value.ToString());} }
}
namespace UnityEngine.Rendering.PostProcessing
{
    public class PostProcessResources { }
    public sealed class Parameter<T> { public T value;public bool overrideState;public void Override(T v){value=v;overrideState=true;} }
    public class PostProcessEffectSettings { public bool active=true;public Parameter<bool> enabled=new Parameter<bool>();public virtual PostProcessEffectSettings Clone(){return (PostProcessEffectSettings)MemberwiseClone();} }
    public sealed class Bloom:PostProcessEffectSettings
    {
        public Parameter<float> intensity=new Parameter<float>(),threshold=new Parameter<float>(),diffusion=new Parameter<float>(),dirtIntensity=new Parameter<float>();
        public Parameter<bool> fastMode=new Parameter<bool>();public Parameter<UnityEngine.Color> color=new Parameter<UnityEngine.Color>();
        public override PostProcessEffectSettings Clone(){return new Bloom{active=active};}
    }
    public sealed class Grain:PostProcessEffectSettings { public Parameter<float> intensity=new Parameter<float>(),size=new Parameter<float>();public Parameter<bool> colored=new Parameter<bool>(); }
    public enum KernelSize { Small,Medium,Large,VeryLarge }
    public sealed class DepthOfField:PostProcessEffectSettings { public Parameter<KernelSize> kernelSize=new Parameter<KernelSize>();public Parameter<float> focusDistance=new Parameter<float>(),aperture=new Parameter<float>(),focalLength=new Parameter<float>(); }
    public sealed class MotionBlur:PostProcessEffectSettings { public Parameter<float> shutterAngle=new Parameter<float>();public Parameter<int> sampleCount=new Parameter<int>(); }
    public sealed class PostProcessProfile
    {
        public List<PostProcessEffectSettings> settings=new List<PostProcessEffectSettings>();
        public bool TryGetSettings<T>(out T value) where T:PostProcessEffectSettings{foreach(var setting in settings)if(setting is T){value=(T)setting;return true;}value=null;return false;}
        public T AddSettings<T>() where T:PostProcessEffectSettings,new(){var value=new T();settings.Add(value);return value;}
    }
    public sealed class PostProcessVolume:UnityEngine.MonoBehaviour
    {
        public PostProcessProfile sharedProfile=new PostProcessProfile(),instance;
        public PostProcessProfile profile{get{if(instance==null){instance=new PostProcessProfile();foreach(var s in sharedProfile.settings)instance.settings.Add(s.Clone());}return instance;}}
    }
    public sealed class FastApproximateAntialiasing{public bool fastMode;}
    public sealed class PostProcessLayer:UnityEngine.MonoBehaviour
    {
        public bool finalBlitToCameraTarget;
        public enum Antialiasing{None,FastApproximateAntialiasing,SubpixelMorphologicalAntialiasing,TemporalAntialiasing}
        public Antialiasing antialiasingMode=Antialiasing.TemporalAntialiasing;public FastApproximateAntialiasing fastApproximateAntialiasing;
    }
}
namespace UnityEngine.UI
{
    public struct ColorBlock { public UnityEngine.Color normalColor,highlightedColor,selectedColor,pressedColor;public float colorMultiplier; }
    public class Graphic:UnityEngine.MonoBehaviour { public UnityEngine.Color color; }
    public class Toggle:UnityEngine.MonoBehaviour { public bool isOn=true,interactable=true;public ColorBlock colors;public Graphic targetGraphic=new Graphic();public int notifications; public void SetIsOnWithoutNotify(bool value){isOn=value;} }
    public class Slider { public float value=150;public int notifications; public void SetValueWithoutNotify(float v){value=v;} }
    public class Text:UnityEngine.MonoBehaviour { public string text;public object font=new object();public int fontSize=16,resizeTextMinSize=4,resizeTextMaxSize=20;public bool resizeTextForBestFit;public UnityEngine.RectTransform rectTransform{get{return (UnityEngine.RectTransform)transform;}} }
    public class Dropdown:UnityEngine.MonoBehaviour
    {
        public sealed class OptionData { public string text;public OptionData(string value){text=value;} }
        int selectedValue;public int notifications,refreshes;public string shownText;
        public int value{get{return selectedValue;}set{if(selectedValue==value)return;selectedValue=value;if(onValueChanged.count!=0)++notifications;onValueChanged.Invoke(value);}}
        public class DropdownEvent { readonly List<Action<int>> listeners=new List<Action<int>>();public int count{get{return listeners.Count;}}public void RemoveAllListeners(){listeners.Clear();}public void AddListener(Action<int> listener){listeners.Add(listener);}public void Invoke(int index){foreach(var listener in listeners)listener(index);} }
        public DropdownEvent onValueChanged=new DropdownEvent();
        public List<OptionData> options=new List<OptionData>();
        public Text captionText=new Text();
        public void SetValueWithoutNotify(int v){if(selectedValue==v)return;selectedValue=v;RefreshShownValue();}
        public void ClearOptions(){options.Clear();selectedValue=0;}
        public void RefreshShownValue(){++refreshes;shownText=options.Count==0?"":options[Math.Max(0,Math.Min(selectedValue,options.Count-1))].text;captionText.text=shownText;}
    }
}
namespace ClosingShift.Android
{
    static class AndroidContext
    {
        internal sealed class View
        {
            readonly object owner;
            internal View(object value){owner=value;}
            internal object Get(string name){return owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(owner);}
        }
    }
}

namespace UnityEngine {
    public struct Ray { public Vector3 direction; }
    public struct Rect { public float x,y,width,height; public Rect(float x,float y,float w,float h){this.x=x;this.y=y;width=w;height=h;} }
    public struct Matrix4x4 { public static Matrix4x4 identity=new Matrix4x4(); }
    public class Texture:Object { public string name; public static ulong currentTextureMemory,nonStreamingTextureMemory; }
    public enum RenderTextureFormat { DefaultHDR,Depth } public enum FilterMode { Bilinear } public enum TextureWrapMode { Clamp }
    public class RenderTexture:Texture { public RenderBuffer colorBuffer,depthBuffer;public static bool failNext; public int width,height,depth,antiAliasing; public bool useMipMap,autoGenerateMips,created,released;public FilterMode filterMode;public TextureWrapMode wrapMode;public RenderTextureFormat format;
        public RenderTexture(int w,int h,int d,RenderTextureFormat f){width=w;height=h;depth=d;format=f;}public bool Create(){created=!failNext;failNext=false;return created;}public bool IsCreated(){return created;}public void Release(){released=true;created=false;}
    }
    public class Shader:Object { public bool isSupported=true;public static Shader Find(string name){return new Shader();} public static int PropertyToID(string name){return name.GetHashCode();} }
    public class Material:Object { public Shader shader=new Shader();readonly Dictionary<string,float> floats=new Dictionary<string,float>();public void SetFloat(string name,float value){floats[name]=value;}public float GetFloat(string name){float value;return floats.TryGetValue(name,out value)?value:0;} }
    public struct Bounds { } public struct Plane { }
    public static class GeometryUtility {public static bool visible=true;public static void CalculateFrustumPlanes(Camera camera,Plane[] planes){}public static bool TestPlanesAABB(Plane[] planes,Bounds bounds){return visible;}}
    public class MeshRenderer:Behaviour {public Bounds bounds;public Material sharedMaterial;}
    public struct Quaternion {public static Vector3 operator *(Quaternion q,Vector3 v){return v;}}
    public class Mesh:Object { }
}
namespace UnityEngine.Rendering {
    public enum CameraEvent { AfterEverything } public enum BuiltinRenderTextureType { CameraTarget }
    public struct RenderTargetIdentifier : IEquatable<RenderTargetIdentifier> { public UnityEngine.RenderBuffer buffer;public RenderTargetIdentifier(UnityEngine.RenderBuffer value){buffer=value;}public bool Equals(RenderTargetIdentifier other){return buffer.id==other.buffer.id;} }
    public class CommandBuffer { public string name;public UnityEngine.Texture texture;public UnityEngine.Rect viewport;public RenderTargetIdentifier target;public UnityEngine.Material material;public int draws,clears;public bool released,usedPpsBlit;public readonly Dictionary<int,float> globals=new Dictionary<int,float>();
        public void Clear(){++clears;texture=null;material=null;draws=0;globals.Clear();usedPpsBlit=false;}public void SetGlobalFloat(int id,float value){globals[id]=value;}public void SetGlobalTexture(int id,UnityEngine.Texture value){texture=value;}public void SetRenderTarget(RenderTargetIdentifier value){target=value;}public void SetViewport(UnityEngine.Rect value){viewport=value;}public void DrawMesh(UnityEngine.Mesh mesh,UnityEngine.Matrix4x4 matrix,UnityEngine.Material value,int submesh,int pass){material=value;++draws;}public void Release(){released=true;}
    }
}
namespace TMPro {public class TMP_Text:UnityEngine.MonoBehaviour{public bool isOverlay;public UnityEngine.Material fontMaterial=new UnityEngine.Material();} public class TMP_SubMesh:UnityEngine.MonoBehaviour{public UnityEngine.Material material=new UnityEngine.Material();}}
namespace UnityEngine.Rendering.PostProcessing { public static class RuntimeUtilities { public static UnityEngine.Material copyMaterial=new UnityEngine.Material();public static UnityEngine.Mesh fullscreenTriangle=new UnityEngine.Mesh();
    public static void BlitFullscreenTriangle(this UnityEngine.Rendering.CommandBuffer cmd,UnityEngine.Texture source,UnityEngine.Rendering.RenderTargetIdentifier destination,bool clear=false,UnityEngine.Rect? viewport=null,bool preserveDepth=false){cmd.usedPpsBlit=true;cmd.SetGlobalTexture(UnityEngine.Shader.PropertyToID("_MainTex"),source);cmd.SetRenderTarget(destination);if(viewport.HasValue)cmd.SetViewport(viewport.Value);cmd.DrawMesh(fullscreenTriangle,UnityEngine.Matrix4x4.identity,copyMaterial,0,0);}
} }
namespace ClosingShift.Android {
    static class AndroidRoomCulling {internal static bool occluded;internal static bool Occluded(UnityEngine.Camera camera,UnityEngine.Bounds bounds){return occluded;}}
    static class AndroidInputSettings { internal static int FpsMode;internal static int[] FpsValues={30,45,60,0};internal static void Save(){UnityEngine.PlayerPrefs.SetInt("ClosingShift.FpsMode",FpsMode);UnityEngine.PlayerPrefs.Save();} }
    static class AndroidColorGrading { internal static void BeginScene(){}internal static void Observe(UnityEngine.MonoBehaviour component,string type){}internal static void Enforce(){} }
}
namespace ClosingShift.Localization { public static class RussianUiPresentation { public static bool RussianActive; } }
namespace UnityEngine.Video
{
    public enum VideoRenderMode { CameraFarPlane,CameraNearPlane,RenderTexture,MaterialOverride,APIOnly }
    public class VideoClip:UnityEngine.Object { public uint width=1024,height=1024; }
    public class VideoPlayer:UnityEngine.Behaviour { public VideoClip clip;public VideoRenderMode renderMode; }
}
