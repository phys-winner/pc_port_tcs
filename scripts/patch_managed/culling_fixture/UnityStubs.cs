namespace UnityEngine.Rendering { public enum ShadowCastingMode { Off,On,TwoSided,ShadowsOnly } }
namespace UnityEngine
{
    public class Object { public string name; }
    public struct Scene
    {
        public int buildIndex;
        public static bool operator ==(Scene a,Scene b)=>a.buildIndex==b.buildIndex;
        public static bool operator !=(Scene a,Scene b)=>a.buildIndex!=b.buildIndex;
        public override bool Equals(object other)=>other is Scene scene && scene==this;
        public override int GetHashCode()=>buildIndex;
    }
    public enum LightType { Spot,Directional,Point,Area }
    public class Light:Component { public bool enabled=true; public LightType type=LightType.Point; public float range=10,spotAngle=30; }
    public class ReflectionProbe:Component { public bool enabled=true; public Bounds bounds; public float blendDistance; }
    public static class Resources { public static T[] FindObjectsOfTypeAll<T>() where T:Object=>GameObject.all.SelectMany(value=>value.AllComponents).OfType<T>().ToArray(); }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public float sqrMagnitude=>x*x+y*y+z*z;
        public Vector3 normalized=>sqrMagnitude>1e-12f?this*(1/MathF.Sqrt(sqrMagnitude)):new Vector3();
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new Vector3(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator -(Vector3 a)=>new Vector3(-a.x,-a.y,-a.z);
        public static Vector3 operator *(Vector3 a,float b)=>new Vector3(a.x*b,a.y*b,a.z*b);
        public static float Dot(Vector3 a,Vector3 b)=>a.x*b.x+a.y*b.y+a.z*b.z;
        public static Vector3 Cross(Vector3 a,Vector3 b)=>new Vector3(a.y*b.z-a.z*b.y,a.z*b.x-a.x*b.z,a.x*b.y-a.y*b.x);
    }
    public struct Bounds
    {
        public Vector3 center,size;
        public Bounds(Vector3 center,Vector3 size){this.center=center;this.size=size;}
        public Vector3 extents=>size*.5f;
        public void Expand(float amount){size=size+new Vector3(amount,amount,amount);}
        public Vector3 min=>center-extents;
        public Vector3 max=>center+extents;
        public void Encapsulate(Vector3 point)
        {
            Vector3 low=new Vector3(MathF.Min(min.x,point.x),MathF.Min(min.y,point.y),MathF.Min(min.z,point.z));
            Vector3 high=new Vector3(MathF.Max(max.x,point.x),MathF.Max(max.y,point.y),MathF.Max(max.z,point.z));
            center=(low+high)*.5f;size=high-low;
        }
    }
    public struct Matrix4x4
    {
        public float[] values;
        public float this[int row,int col]=>values==null?(row==col?1:0):values[row*4+col];
    }
    public struct Plane { }
    public static class Mathf { public const float Deg2Rad=MathF.PI/180; public static float Abs(float value)=>MathF.Abs(value); public static float Max(float a,float b)=>MathF.Max(a,b); public static float Tan(float value)=>MathF.Tan(value); public static float Sqrt(float value)=>MathF.Sqrt(value); }
    public static class Time { public static int frameCount; }
    public static class Debug { public static void Log(object value) { } }
    public class Component:Object
    {
        public GameObject gameObject;
        public Transform transform=>gameObject.transform;
        public T GetComponent<T>() where T:Component=>gameObject.GetComponent<T>();
        public T[] GetComponentsInChildren<T>(bool inactive) where T:Component=>gameObject.GetComponentsInChildren<T>(inactive);
    }
    public class MonoBehaviour:Component { }
    public class Transform:Component
    {
        public Transform parent;
        public Vector3 position,forward=new Vector3(0,0,1);
        public Matrix4x4 localToWorldMatrix;
        public Transform Find(string name)=>gameObject.children.FirstOrDefault(child=>child.name==name)?.transform;
    }
    public class GameObject:Object
    {
        public static readonly List<GameObject> all=new List<GameObject>();
        public readonly List<GameObject> children=new List<GameObject>();
        readonly List<Component> components=new List<Component>();
        public IEnumerable<Component> AllComponents=>components;
        public Scene scene=new Scene{buildIndex=1};
        public bool activeInHierarchy=true;
        public Transform transform;
        public GameObject(string name,GameObject parent=null)
        {
            this.name=name;all.Add(this);transform=AddComponent<Transform>();transform.name=name;
            if(parent!=null){parent.children.Add(this);transform.parent=parent.transform;}
        }
        public static GameObject Find(string name)=>all.FirstOrDefault(value=>value.name==name);
        public T AddComponent<T>() where T:Component,new(){T value=new T{gameObject=this};components.Add(value);return value;}
        public T GetComponent<T>() where T:Component=>components.OfType<T>().FirstOrDefault();
        public T[] GetComponentsInChildren<T>(bool inactive) where T:Component=>components.OfType<T>().Concat(children.SelectMany(child=>child.GetComponentsInChildren<T>(inactive))).ToArray();
    }
    public class Mesh:Object { public int vertexCount; public Bounds bounds; }
    public class MeshFilter:Component { public Mesh sharedMesh; }
    public class Shader:Object { public int propertyCount=27; public int GetPropertyCount()=>propertyCount; }
    public class Material:Object
    {
        public Shader shader=new Shader{name="Standard"};
        public int renderQueue=2000;
        public readonly Dictionary<string,float> values=new Dictionary<string,float>{{"_Mode",0},{"_ZWrite",1},{"_SrcBlend",1},{"_DstBlend",0}};
        public readonly HashSet<string> keywords=new HashSet<string>();
        public bool HasProperty(string key)=>values.ContainsKey(key);
        public float GetFloat(string key)=>values.TryGetValue(key,out float value)?value:0;
        public bool IsKeywordEnabled(string key)=>keywords.Contains(key);
    }
    public class MeshRenderer:Component
    {
        public bool isPartOfStaticBatch=true,enabled=true,forceRenderingOff;
        public Bounds bounds;
        public Rendering.ShadowCastingMode shadowCastingMode=Rendering.ShadowCastingMode.On;
        public readonly List<Material> materials=new List<Material>();
        public void GetSharedMaterials(List<Material> result){result.Clear();result.AddRange(materials);}
    }
    public class Camera:Component
    {
        public static Camera main;
        public static event Action<Camera> onPreCull,onPostRender;
        public bool orthographic;
        public float nearClipPlane=.1f,farClipPlane=80,fieldOfView=50;
        public static void Before(Camera value)=>onPreCull?.Invoke(value);
        public static void After(Camera value)=>onPostRender?.Invoke(value);
    }
    public static class GeometryUtility
    {
        public static void CalculateFrustumPlanes(Camera camera,Plane[] result) { }
        public static bool TestPlanesAABB(Plane[] planes,Bounds bounds)=>true;
    }
}
