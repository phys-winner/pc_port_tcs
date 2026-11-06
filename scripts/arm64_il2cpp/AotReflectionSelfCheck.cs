using System;
using System.Reflection;
using ClosingShift.AotCompat;

public delegate void RefZero<T>(ref T instance);
public delegate void RefOne<T, TArg>(ref T instance, TArg value);
public struct State
{
    public int Value;
    private void Increment() { Value++; }
    private void Add(int amount) { Value += amount; }
    private void MutateThenFail() { Value += 10; throw new InvalidOperationException("struct-mutated-zero"); }
    private void MutateThenFailOne(int amount) { Value += amount; throw new InvalidOperationException("struct-mutated-one"); }
}
public sealed class ReferenceState
{
    public int Value;
    public int Property { get; set; }
    private void Increment() { Value++; }
    private void Fail() { throw new InvalidOperationException("callback-original-exception"); }
}
static class AotReflectionSelfCheck
{
    static MethodInfo Method(Type type, string name) { return type.GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic); }
    public static void Main(string[] arguments)
    {
        State state = new State { Value = 3 };
        var zero = (RefZero<State>)ReflectionFallbacks.CreateRefCaller<State>(Method(typeof(State), "Increment"), typeof(RefZero<State>));
        zero(ref state);
        var one = (RefOne<State, int>)ReflectionFallbacks.CreateRefCaller<State, int>(Method(typeof(State), "Add"), typeof(RefOne<State, int>));
        one(ref state, 5);
        if (state.Value != 9) throw new Exception("Boxed value-type callback changes were lost");
        int previous = state.Value;
        bool changedThenFailed = false;
        try
        {
            var mutate = (RefZero<State>)ReflectionFallbacks.CreateRefCaller<State>(Method(typeof(State), "MutateThenFail"), typeof(RefZero<State>));
            mutate(ref state);
        }
        catch (InvalidOperationException exception) { changedThenFailed = exception.Message == "struct-mutated-zero"; }
        if (!changedThenFailed || state.Value != previous + 10) throw new Exception("Zero-argument struct callback lost mutation on throw");
        previous = state.Value; changedThenFailed = false;
        try
        {
            var mutate = (RefOne<State, int>)ReflectionFallbacks.CreateRefCaller<State, int>(Method(typeof(State), "MutateThenFailOne"), typeof(RefOne<State, int>));
            mutate(ref state, 7);
        }
        catch (InvalidOperationException exception) { changedThenFailed = exception.Message == "struct-mutated-one"; }
        if (!changedThenFailed || state.Value != previous + 7) throw new Exception("One-argument struct callback lost mutation on throw");
        var reference = new ReferenceState();
        var callback = (RefZero<ReferenceState>)ReflectionFallbacks.CreateRefCaller<ReferenceState>(Method(typeof(ReferenceState), "Increment"), typeof(RefZero<ReferenceState>));
        callback(ref reference);
        if (reference.Value != 1) throw new Exception("Reference-type callback failed");
        var failure = (RefZero<ReferenceState>)ReflectionFallbacks.CreateRefCaller<ReferenceState>(Method(typeof(ReferenceState), "Fail"), typeof(RefZero<ReferenceState>));
        bool exact = false;
        try { failure(ref reference); }
        catch (InvalidOperationException exception) { exact = exception.Message == "callback-original-exception"; }
        if (!exact) throw new Exception("Reflection wrapper changed callback exception semantics");
        if (arguments.Length == 2)
        {
            string patched = arguments[0], original = arguments[1];
            AppDomain.CurrentDomain.AssemblyResolve += (sender, eventArgs) => {
                string name = new AssemblyName(eventArgs.Name).Name + ".dll";
                string file = System.IO.Path.Combine(patched, name);
                if (!System.IO.File.Exists(file)) file = System.IO.Path.Combine(original, name);
                return System.IO.File.Exists(file) ? Assembly.LoadFrom(file) : null;
            };
            var json = Assembly.LoadFrom(System.IO.Path.Combine(patched, "Newtonsoft.Json.dll"));
            if ((bool)json.GetType("Newtonsoft.Json.Serialization.JsonTypeReflector").GetProperty("DynamicCodeGeneration", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).GetValue(null, null))
                throw new Exception("Newtonsoft still selects emitted delegates");
            var convert = json.GetType("Newtonsoft.Json.JsonConvert");
            reference.Value = 23; reference.Property = 29;
            string serialized = (string)convert.GetMethod("SerializeObject", new[] { typeof(object) }).Invoke(null, new object[] { reference });
            var restored = (ReferenceState)convert.GetMethod("DeserializeObject", new[] { typeof(string), typeof(Type) }).Invoke(null, new object[] { serialized, typeof(ReferenceState) });
            if (restored.Value != 23 || restored.Property != 29) throw new Exception("Newtonsoft late-bound field/property roundtrip failed");
            foreach (string file in new[] { "Sirenix.Utilities.dll", "Sirenix.Serialization.dll" })
            {
                var assembly = Assembly.LoadFrom(System.IO.Path.Combine(patched, file));
                string scope = file == "Sirenix.Utilities.dll" ? "Sirenix.Utilities" : "Sirenix.Serialization.Utilities";
                var utilities = assembly.GetType(scope + ".EmitUtilities");
                if ((bool)utilities.GetProperty("CanEmit", BindingFlags.Public | BindingFlags.Static).GetValue(null, null))
                    throw new Exception("Odin still selects emitted formatters");
                var unityNull = assembly.GetType(scope + ".UnityExtensions").GetMethod("SafeIsUnityNull", BindingFlags.Public | BindingFlags.Static);
                if (!(bool)unityNull.Invoke(null, new object[] { null })) throw new Exception("Patched Unity null check failed");
                if (file != "Sirenix.Serialization.dll") continue;
                var serializer = assembly.GetType("Sirenix.Serialization.UnitySerializationUtility");
                foreach (MemberInfo member in new MemberInfo[] { typeof(ReferenceState).GetField("Value"), typeof(ReferenceState).GetProperty("Property") })
                {
                    var getter = (Delegate)serializer.GetMethod("GetCachedUnityMemberGetter", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { member });
                    var setter = (Delegate)serializer.GetMethod("GetCachedUnityMemberSetter", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { member });
                    object boxed = reference;
                    setter.DynamicInvoke(new object[] { boxed, 19 });
                    if ((int)getter.DynamicInvoke(new object[] { boxed }) != 19) throw new Exception("Existing cached reflection field/property path failed");
                }
                foreach (int arity in new[] { 1, 2 })
                {
                    MethodInfo factory = null;
                    foreach (var method in utilities.GetMethods(BindingFlags.Static | BindingFlags.Public))
                        if (method.Name == "CreateInstanceRefMethodCaller" && method.GetGenericArguments().Length == arity) factory = method;
                    var closed = factory.MakeGenericMethod(arity == 1 ? new[] { typeof(State) } : new[] { typeof(State), typeof(int) });
                    var callbackMethod = Method(typeof(State), arity == 1 ? "Increment" : "Add");
                    var emittedReplacement = (Delegate)closed.Invoke(null, new object[] { callbackMethod });
                    object[] callbackArguments = arity == 1 ? new object[] { state } : new object[] { state, 2 };
                    emittedReplacement.DynamicInvoke(callbackArguments);
                    if (((State)callbackArguments[0]).Value != state.Value + (arity == 1 ? 1 : 2)) throw new Exception("Patched Odin custom ref callback failed");
                    var throwing = (Delegate)closed.Invoke(null, new object[] { Method(typeof(State), arity == 1 ? "MutateThenFail" : "MutateThenFailOne") });
                    if (!throwing.GetType().FullName.StartsWith("Sirenix.Serialization.Utilities.EmitUtilities+InstanceRefMethodCaller"))
                        throw new Exception("Factory did not return the actual original Odin delegate type");
                    previous = state.Value; changedThenFailed = false;
                    // Invoke the exact factory-produced target/method through a
                    // strongly typed ref delegate so DynamicInvoke's own argument
                    // marshalling cannot hide mutation on an exception.
                    try
                    {
                        if (arity == 1)
                        {
                            var typed = (RefZero<State>)Delegate.CreateDelegate(typeof(RefZero<State>), throwing.Target, throwing.Method);
                            typed(ref state);
                        }
                        else
                        {
                            var typed = (RefOne<State, int>)Delegate.CreateDelegate(typeof(RefOne<State, int>), throwing.Target, throwing.Method);
                            typed(ref state, 7);
                        }
                    }
                    catch (InvalidOperationException exception)
                    { changedThenFailed = exception.Message == (arity == 1 ? "struct-mutated-zero" : "struct-mutated-one"); }
                    if (!changedThenFailed || state.Value != previous + (arity == 1 ? 10 : 7))
                        throw new Exception("Actual patched Odin callback lost mutation or original exception on throw");
                }
            }
            Console.WriteLine("PASS: persisted patched capabilities, Newtonsoft late-bound JSON roundtrip, Unity null path, original cached field/property delegates and original Odin custom ref delegates");
        }
        Console.WriteLine("PASS: private reference/value-type callbacks, ref writeback including mutate-then-throw0/1arg, exact custom delegates and original inner exceptions; no Emit/JIT");
    }
}
