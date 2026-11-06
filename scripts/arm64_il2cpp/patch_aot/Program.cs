using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

static class PatchAot
{
    static string Hash(byte[] raw) => Convert.ToHexString(SHA256.HashData(raw)).ToLowerInvariant();
    static readonly Dictionary<string, string> Expected = new()
    {
        ["Sirenix.Serialization.dll"] = "65038d0bbb7a8afaccccda02bb719db5bc89d488da6763372c1c4b30e208d744",
        ["Sirenix.Utilities.dll"] = "eb7821b703f3b3b85914fe74d5b0606aa94fd5064f547fb0450188597c27f1ae",
        ["Newtonsoft.Json.dll"] = "830e019e1796b4eb5a8b0e72824972a70c4bd266428ecd1770e680bcb6861bdc"
    };
    static void Require(bool test, string message) { if (!test) throw new InvalidOperationException(message); }
    static string Operand(object? operand, CilBody body) => operand switch
    {
        null => "", Instruction target => "I:" + body.Instructions.IndexOf(target),
        IList<Instruction> targets => "IS:" + string.Join(",", targets.Select(body.Instructions.IndexOf)),
        IFullName item => item.FullName, Local local => "L:" + local.Index + ":" + local.Type.FullName,
        Parameter parameter => "P:" + parameter.Index, _ => operand.ToString() ?? ""
    };
    static string Body(MethodDef method)
    {
        if (!method.HasBody) return "none";
        var body = method.Body;
        return JsonSerializer.Serialize(new { body.InitLocals, locals = body.Variables.Select(x => x.Type.FullName),
            il = body.Instructions.Select(x => x.OpCode.Code + ":" + Operand(x.Operand, body)),
            handlers = body.ExceptionHandlers.Select(x => new { x.HandlerType, type = x.CatchType?.FullName,
                a = Operand(x.TryStart, body), b = Operand(x.TryEnd, body), c = Operand(x.HandlerStart, body), d = Operand(x.HandlerEnd, body), e = Operand(x.FilterStart, body) }) });
    }
    static Dictionary<uint, string> Definitions(ModuleDef module) => module.GetTypes().SelectMany(type =>
        new[] { (type.MDToken.Raw, type.FullName + ":" + type.Attributes + ":" + type.BaseType?.FullName + ":" + type.ClassLayout?.ClassSize + ":" + type.ClassLayout?.PackingSize) }
        .Concat(type.Fields.Select(field => (field.MDToken.Raw, field.FullName + ":" + field.Attributes)))
        .Concat(type.Methods.Select(method => (method.MDToken.Raw, method.FullName + ":" + method.Attributes + ":" + method.ImplAttributes))))
        .ToDictionary(x => x.Item1, x => x.Item2);
    static MethodDef Method(ModuleDef module, string owner, string name, int? generic = null)
    {
        var type = module.Find(owner, false) ?? throw new Exception("Missing type " + owner);
        return type.Methods.Single(x => x.Name == name && (generic == null || x.GenericParameters.Count == generic));
    }
    static void ConstantFalse(MethodDef method)
    {
        method.Body = new CilBody();
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    static void Redirect(MethodDef method, IMethod target)
    {
        method.Body = new CilBody();
        for (int i = 0; i < method.MethodSig.Params.Count; i++) method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, method.Parameters[i]));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    }
    public static void Main(string[] args)
    {
        Require(args.Length == 3, "Expected accepted-input-Managed output-work-directory helper.dll");
        string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
        string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../"));
        string work = Path.Combine(root, "work") + Path.DirectorySeparatorChar;
        Require(output.StartsWith(work, StringComparison.OrdinalIgnoreCase) && output != source, "Output must be a separate work directory");
        Directory.CreateDirectory(output);
        using var helper = ModuleDefMD.Load(args[2]);
        var helperType = helper.Find("ClosingShift.AotCompat.ReflectionFallbacks", false)!;
        var reports = new List<object>();
        foreach (var pair in Expected)
        {
            byte[] input = File.ReadAllBytes(Path.Combine(source, pair.Key));
            Require(Hash(input) == pair.Value, "Original assembly preimage differs: " + pair.Key);
            using var module = ModuleDefMD.Load(input);
            var definitions = Definitions(module);
            var bodies = module.GetTypes().SelectMany(x => x.Methods).ToDictionary(x => x.MDToken.Raw, Body);
            var changes = new Dictionary<uint, string>();
            var importer = new Importer(module);
            void Changed(MethodDef method, string reason) { Require(!changes.ContainsKey(method.MDToken.Raw), "Duplicate patch"); changes[method.MDToken.Raw] = reason; }
            if (pair.Key == "Newtonsoft.Json.dll")
            {
                var method = Method(module, "Newtonsoft.Json.Serialization.JsonTypeReflector", "get_DynamicCodeGeneration");
                ConstantFalse(method); Changed(method, "Select existing LateBoundReflectionDelegateFactory; no JIT capability on IL2CPP");
            }
            else
            {
                string scope = pair.Key == "Sirenix.Serialization.dll" ? "Sirenix.Serialization.Utilities" : "Sirenix.Utilities";
                var capability = Method(module, scope + ".EmitUtilities", "get_CanEmit");
                Require(capability.Body.Instructions.Count == 2 && capability.Body.Instructions[0].GetLdcI4Value() == 1, "Expected desktop CanEmit literal true");
                ConstantFalse(capability); Changed(capability, "Select existing supported reflection formatter path");
                var initializer = Method(module, scope + ".UnityExtensions", ".cctor");
                Require(initializer.Body.Instructions.Count(x => x.Operand is IMethod called && called.Name == "CreateInstanceFieldGetter") == 1, "Unexpected Unity pointer getter initialization");
                initializer.Body = new CilBody(); initializer.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                Changed(initializer, "Cached-pointer checks use supported reflection helper; no dynamic getter construction");
                var nullCheck = Method(module, scope + ".UnityExtensions", "SafeIsUnityNull");
                Redirect(nullCheck, importer.Import(helperType.Methods.Single(x => x.Name == "IsUnityNull")));
                Changed(nullCheck, "Read the same Unity m_CachedPtr through a cached FieldInfo");
                if (pair.Key == "Sirenix.Serialization.dll")
                {
                    foreach (string name in new[] { "GetCachedUnityMemberGetter", "GetCachedUnityMemberSetter" })
                    {
                        var method = Method(module, "Sirenix.Serialization.UnitySerializationUtility", name);
                        var tests = method.Body.Instructions.Where(x => x.OpCode.Code == Code.Isinst && x.Operand is ITypeDefOrRef t &&
                            t.FullName is "System.Reflection.FieldInfo" or "System.Reflection.PropertyInfo").ToArray();
                        Require(tests.Length == 4, "Expected two tests and two casts in the original fallback branches");
                        tests = tests.Where(test => method.Body.Instructions[method.Body.Instructions.IndexOf(test) + 1].OpCode.FlowControl == FlowControl.Cond_Branch).ToArray();
                        Require(tests.Length == 2, "Existing field/property/reflection fallback shape differs");
                        foreach (var test in tests)
                        {
                            int index = method.Body.Instructions.IndexOf(test);
                            test.OpCode = OpCodes.Pop; test.Operand = null;
                            method.Body.Instructions.Insert(index + 1, Instruction.Create(OpCodes.Ldnull));
                        }
                        Changed(method, "Use its existing cached FormatterUtilities reflection delegate for fields/properties");
                    }
                    foreach (int arity in new[] { 1, 2 })
                    {
                        var method = Method(module, scope + ".EmitUtilities", "CreateInstanceRefMethodCaller", arity);
                        var factory = importer.Import(helperType.Methods.Single(x => x.Name == "CreateRefCaller" && x.GenericParameters.Count == arity));
                        var specification = new MethodSpecUser((IMethodDefOrRef)factory, new GenericInstMethodSig(Enumerable.Range(0, arity).Select(i => (TypeSig)new GenericMVar(i)).ToArray()));
                        var getType = module.GetMemberRefs().First(x => x.DeclaringType.FullName == "System.Type" && x.Name == "GetTypeFromHandle");
                        method.Body = new CilBody();
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldtoken, method.MethodSig.RetType.ToTypeDefOrRef()));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, getType));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, specification));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Castclass, method.MethodSig.RetType.ToTypeDefOrRef()));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        Changed(method, "Reflection callback with exact ref/value-type writeback and existing delegate signature");
                    }
                    foreach (string name in new[] { "DeserializeImplementation", "SerializeImplementation" })
                    {
                        var method = Method(module, "Sirenix.Serialization.ReflectionOrEmittedBaseFormatter`1", name);
                        var originalBase = Method(module, "Sirenix.Serialization.ReflectionFormatter`1", name);
                        var call = new MemberRefUser(module, originalBase.Name, originalBase.MethodSig, method.DeclaringType.BaseType);
                        method.Body = new CilBody();
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_2));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Call, call));
                        method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
                        Changed(method, "Use existing ReflectionFormatter base implementation; never silently skip serialization");
                    }
                }
            }
            string path = Path.Combine(output, pair.Key);
            var options = new ModuleWriterOptions(module);
            options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
            module.Write(path, options);
            using var persisted = ModuleDefMD.Load(path);
            var afterDefinitions = Definitions(persisted);
            Require(definitions.Count == afterDefinitions.Count && definitions.All(x => afterDefinitions.GetValueOrDefault(x.Key) == x.Value), "Type/field/method identities, tokens or layouts changed");
            var afterMethods = persisted.GetTypes().SelectMany(x => x.Methods).ToDictionary(x => x.MDToken.Raw);
            foreach (var row in bodies) Require(changes.ContainsKey(row.Key) || Body(afterMethods[row.Key]) == row.Value, "Unselected method body changed");
            foreach (var row in changes) Require(Body(afterMethods[row.Key]) != bodies[row.Key], "Selected patch absent after persistence");
            reports.Add(new { file = pair.Key, oldSha256 = pair.Value, newSha256 = Hash(File.ReadAllBytes(path)),
                changes = changes.Select(x => new { token = x.Key, method = afterMethods[x.Key].FullName, reason = x.Value }),
                serializedIdentitiesAndAllOriginalDefinitionTokensPreserved = true, allUnselectedMethodBodiesPreserved = true });
        }
        File.Copy(args[2], Path.Combine(output, "ClosingShift.AotCompat.dll"), true);
        File.WriteAllText(Path.Combine(output, "manifest.json"), JsonSerializer.Serialize(new { format = "closing-shift-aot-compat-v1", files = reports,
            helperSha256 = Hash(File.ReadAllBytes(args[2])), canonicalAppChanged = false, gameAotConversionExecuted = false }, new JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("AOT_COMPAT_PATCH_PASS 3 assembly copies; 13 existing methods; no original definitions added or removed");
    }
}
