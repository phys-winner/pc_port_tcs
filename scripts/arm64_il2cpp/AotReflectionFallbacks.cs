using System;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace ClosingShift.AotCompat
{
    public static class ReflectionFallbacks
    {
        static class UnityFields
        {
            internal static readonly FieldInfo CachedPtr = typeof(UnityEngine.Object).GetField("m_CachedPtr", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        public static bool IsUnityNull(UnityEngine.Object value)
        {
            if (Object.ReferenceEquals(value, null)) return true;
            if (UnityFields.CachedPtr == null) throw new NotSupportedException("UnityEngine.Object.m_CachedPtr metadata missing");
            return (IntPtr)UnityFields.CachedPtr.GetValue(value) == IntPtr.Zero;
        }

        static void Invoke(MethodInfo method, object instance, object[] arguments)
        {
            try { method.Invoke(instance, arguments); }
            catch (TargetInvocationException exception)
            {
                if (exception.InnerException != null) ExceptionDispatchInfo.Capture(exception.InnerException).Throw();
                throw;
            }
        }

        delegate void RefCall<T>(ref T instance);
        delegate void RefCall<T, TArg>(ref T instance, TArg argument);

        sealed class Callback<T>
        {
            readonly MethodInfo method;
            internal Callback(MethodInfo method) { this.method = method; }
            internal void Call(ref T instance)
            {
                object boxed = instance;
                try { Invoke(method, boxed, null); }
                finally { instance = (T)boxed; }
            }
        }

        sealed class Callback<T, TArg>
        {
            readonly MethodInfo method;
            internal Callback(MethodInfo method) { this.method = method; }
            internal void Call(ref T instance, TArg argument)
            {
                object boxed = instance;
                try { Invoke(method, boxed, new object[] { argument }); }
                finally { instance = (T)boxed; }
            }
        }

        public static Delegate CreateRefCaller<T>(MethodInfo methodInfo, Type delegateType)
        {
            if (methodInfo == null) throw new ArgumentNullException("methodInfo");
            if (methodInfo.IsStatic || methodInfo.GetParameters().Length != 0) throw new ArgumentException("Expected an instance callback with no arguments");
            RefCall<T> rooted = new Callback<T>(methodInfo).Call;
            return Delegate.CreateDelegate(delegateType, rooted.Target, rooted.Method);
        }

        public static Delegate CreateRefCaller<T, TArg>(MethodInfo methodInfo, Type delegateType)
        {
            if (methodInfo == null) throw new ArgumentNullException("methodInfo");
            if (methodInfo.IsStatic || methodInfo.GetParameters().Length != 1) throw new ArgumentException("Expected an instance callback with one argument");
            RefCall<T, TArg> rooted = new Callback<T, TArg>(methodInfo).Call;
            return Delegate.CreateDelegate(delegateType, rooted.Target, rooted.Method);
        }
    }
}
