namespace UnityEngine
{
    public class GameObject
    {
        public string name="FixtureRegion";public readonly List<MonoBehaviour> components=new();
        public readonly Transform transform;
        public GameObject(){transform=new Transform{gameObject=this};}
        public void GetComponents<T>(List<T> result){result.Clear();result.AddRange(components.OfType<T>());}
    }
    public class Transform {public GameObject gameObject;public Transform parent;public void GetComponents<T>(List<T> result)=>gameObject.GetComponents(result);}
    public class MonoBehaviour { public GameObject gameObject = new GameObject();public bool enabled=true;public bool isActiveAndEnabled=>enabled; }
    public enum RuntimePlatform {Android,WindowsPlayer}
    public static class Application {public static RuntimePlatform platform=RuntimePlatform.Android;}
    public struct Vector3
    {
        public float x,y,z;public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public float magnitude=>(float)Math.Sqrt(x*x+y*y+z*z);
        public static Vector3 operator -(Vector3 a,Vector3 b)=>new Vector3(a.x-b.x,a.y-b.y,a.z-b.z);
        public static Vector3 operator /(Vector3 a,float divisor)=>new Vector3(a.x/divisor,a.y/divisor,a.z/divisor);
    }
    public struct Ray {public Vector3 origin,direction;public Ray(Vector3 origin,Vector3 direction){this.origin=origin;this.direction=direction;}}
    public class Collider {public GameObject gameObject;}
    public struct RaycastHit {public Collider collider;public Vector3 point;}
    public static class Physics
    {
        public static Func<Ray,float,int,RaycastHit?> Cast;
        public static RaycastHit[] Candidates=Array.Empty<RaycastHit>();public static int SphereCalls;
        public static bool Raycast(Ray ray,out RaycastHit hit,float range,int layers){var value=Cast(ray,range,layers);hit=value??default;return value.HasValue;}
        public static int SphereCastNonAlloc(Ray ray,float radius,RaycastHit[] results,float range,int layers)
        {if(radius!=.18f)throw new Exception("Unexpected interaction widening");++SphereCalls;int count=Math.Min(results.Length,Candidates.Length);Array.Copy(Candidates,results,count);return count;}
    }
    public struct Vector2
    {
        public float x,y;
        public Vector2(float x,float y){this.x=x;this.y=y;}
        public static Vector2 zero => new Vector2();
        public float sqrMagnitude => x*x+y*y;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector2 normalized => this*(1/magnitude);
        public static Vector2 operator +(Vector2 a,Vector2 b)=>new Vector2(a.x+b.x,a.y+b.y);
        public static Vector2 operator *(Vector2 a,float scale)=>new Vector2(a.x*scale,a.y*scale);
        public static Vector2 operator -(Vector2 a,Vector2 b)=>new Vector2(a.x-b.x,a.y-b.y);
    }
    public enum CursorLockMode { None, Locked }
    public class Camera { }
    public struct Color {public float r,g,b,a;public Color(float r,float g,float b,float a=1){this.r=r;this.g=g;this.b=b;this.a=a;}public static Color white=>new Color(1,1,1);}
    public struct Rect {public float width,height;public Vector2 center=>Vector2.zero;}
    public class RectTransform {public Rect rect=new Rect{width=100,height=100};}
    public static class RectTransformUtility {public static bool ScreenPointToLocalPointInRectangle(RectTransform rect,Vector2 point,Camera camera,out Vector2 local){local=point;return true;}}
    public static class Mathf {public const float PI=(float)Math.PI;public static float Min(float a,float b)=>Math.Min(a,b);public static float Sin(float a)=>(float)Math.Sin(a);public static float Cos(float a)=>(float)Math.Cos(a);public static float Atan2(float y,float x)=>(float)Math.Atan2(y,x);}
    public static class Cursor { public static CursorLockMode lockState; }
    public static class Time { public static int frameCount; public static float unscaledTime; }
    public static class Screen { public static int height = 1080; }
    public enum TouchPhase { Began, Moved, Stationary, Ended, Canceled }
    public struct Touch { public int fingerId; public TouchPhase phase; public Vector2 position,deltaPosition; }
    public static class Input
    {
        public static Touch[] Touches = Array.Empty<Touch>();
        public static int touchCount => Touches.Length;
        public static Touch GetTouch(int index) => Touches[index];
    }
}
namespace UnityEngine.EventSystems
{
    public class BaseEventData { }
    public class EventSystem { public static readonly EventSystem current = new EventSystem(); }
    public struct RaycastResult { public object module; }
    public class PointerEventData : BaseEventData
    {
        public enum InputButton { Left, Right }
        public int pointerId;
        public InputButton button;
        public UnityEngine.Vector2 position,pressPosition,delta;
        public RaycastResult pointerPressRaycast,pointerCurrentRaycast;
        public bool dragging;
        public bool eligibleForClick = true;
        public PointerEventData(EventSystem events) { }
        public void Reset() { dragging=false; }
    }
    public interface IPointerDownHandler { void OnPointerDown(PointerEventData e); }
    public interface IPointerUpHandler { void OnPointerUp(PointerEventData e); }
    public interface IPointerExitHandler { void OnPointerExit(PointerEventData e); }
    public interface IDragHandler { void OnDrag(PointerEventData e); }
    public interface ICancelHandler { void OnCancel(BaseEventData e); }
    public interface IPointerClickHandler { void OnPointerClick(PointerEventData e); }
}
namespace UnityEngine.UI
{
    public class Button { public bool active=true,interactable=true; public bool IsActive()=>active; public bool IsInteractable()=>interactable; }
    public class MaskableGraphic:UnityEngine.MonoBehaviour {public UnityEngine.RectTransform rectTransform=new();protected virtual void OnPopulateMesh(VertexHelper mesh){}public virtual bool Raycast(UnityEngine.Vector2 point,UnityEngine.Camera camera)=>true;}
    public class VertexHelper {public readonly List<UnityEngine.Vector2> vertices=new();public readonly List<int> triangles=new();public int currentVertCount=>vertices.Count;public void Clear(){vertices.Clear();triangles.Clear();}public void AddVert(UnityEngine.Vector2 position,UnityEngine.Color color,UnityEngine.Vector2 uv){vertices.Add(position);}public void AddTriangle(int a,int b,int c){triangles.AddRange(new[]{a,b,c});}}
    public class Toggle
    {
        public bool isOn,active=true,interactable=true;
        public bool IsActive()=>active;
        public bool IsInteractable()=>interactable;
    }
}
namespace ClosingShift.Android
{
    internal static class AndroidInput { internal static void NotifyTouch() { } }
    internal static class AndroidTouchOverlay { internal static bool SuppressTouches; internal static int QaNotices; internal static void ShowQaEnabled(){++QaNotices;} }
    internal static class AndroidQaOrders { internal static bool Available=true,Enabled; internal static void Unlock(){Enabled=true;} }
    internal static class AndroidInputSettings { internal static void Haptic(){} }
    internal static class AndroidContext { internal sealed class View { internal bool Paused=true; internal bool Flag(string name)=>name=="isPaused"&&Paused; } internal static View Game=new(); }
}
