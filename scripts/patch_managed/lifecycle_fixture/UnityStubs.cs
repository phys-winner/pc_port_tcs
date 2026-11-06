namespace UnityEngine
{
    public class DefaultExecutionOrder : Attribute { public DefaultExecutionOrder(int value) { } }
    public class MonoBehaviour { public GameObject gameObject = new GameObject(); }
    public class GameObject { public T AddComponent<T>() where T : new() => new T(); }
    public struct Rect : IEquatable<Rect>
    {
        public int x;
        public bool Equals(Rect other) => x == other.x;
        public override bool Equals(object other) => other is Rect rect && Equals(rect);
        public override int GetHashCode() => x;
        public static bool operator ==(Rect a,Rect b) => a.Equals(b);
        public static bool operator !=(Rect a,Rect b) => !a.Equals(b);
    }
    public static class Screen { public static int width=2400,height=1080;public static Rect safeArea; }
    public enum TouchPhase { Began,Moved,Stationary,Ended,Canceled }
    public struct Touch { public TouchPhase phase; }
    public static class Input { public static Touch[] touches=Array.Empty<Touch>();public static int touchCount=>touches.Length;public static Touch GetTouch(int index)=>touches[index]; }
    public enum LogType { Error,Assert,Warning,Log,Exception }
    public static class Application
    {
        public static string persistentDataPath, unityVersion="fixture";
        public static event Action<string,string,LogType> logMessageReceivedThreaded;
        public static void Log(string text,LogType type=LogType.Log,string stack="")=>logMessageReceivedThreaded?.Invoke(text,stack,type);
    }
    public static class SystemInfo { public static string graphicsDeviceName="fixture GPU"; }
}
namespace ClosingShift.Android
{
    internal static class AndroidInput
    {
        internal static readonly List<string> events=new List<string>();
        public static void Reset()=>events.Add("reset");
        public static void EnsureFrame()=>events.Add("frame");
    }
    internal class AndroidTouchOverlay { internal void ProcessCapturedTouches()=>AndroidInput.events.Add("touch");internal void ReleaseAll(){}internal void CloseSettings(){} }
    internal static class AndroidGraphics { internal static void Enforce(){} }
    internal static class AndroidRenderScale { internal static void Enforce(){} }
}
