using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 3) throw new ArgumentException("RFloat Aura2_Core.dll, canonical Managed directory, work/output.dll");
const string ExpectedSource = "f9fb15c6ee4e0956ce3be176828c82f23d05693bc4700401f1b44582b72b688f";
void Require(bool condition, string text) { if (!condition) throw new InvalidDataException(text); }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
string output = Path.GetFullPath(args[2]);
string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
Require(output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Output must remain in workspace work");
Require(Hash(args[0]) == ExpectedSource, "Aura source must be exact square-RFloat candidate");
Layout ReadLayout(string path)
{
    using var stream = File.OpenRead(path); using var pe = new PEReader(stream); var md = pe.GetMetadataReader();
    var tables = new[] { TableIndex.TypeDef, TableIndex.Field, TableIndex.MethodDef, TableIndex.Param, TableIndex.InterfaceImpl,
        TableIndex.MethodImpl, TableIndex.Event, TableIndex.Property, TableIndex.EventMap, TableIndex.PropertyMap, TableIndex.MethodSemantics,
        TableIndex.NestedClass, TableIndex.GenericParam, TableIndex.GenericParamConstraint, TableIndex.MethodPtr, TableIndex.FieldPtr, TableIndex.ParamPtr,
        TableIndex.EventPtr, TableIndex.PropertyPtr }.ToDictionary(t => t.ToString(), t => md.GetTableRowCount(t));
    Require(new[] { "MethodPtr", "FieldPtr", "ParamPtr", "EventPtr", "PropertyPtr" }.All(t => tables[t] == 0), "Unexpected pointer table");
    return new Layout(tables, md.TypeDefinitions.ToDictionary(t => MetadataTokens.GetToken(t), t => md.GetTypeDefinition(t).GetMethods().Select(m => MetadataTokens.GetToken(m)).ToArray()));
}
var layoutBefore = ReadLayout(args[0]);
using var source = ModuleDefMD.Load(args[0]);
using var core = ModuleDefMD.Load(Path.Combine(args[1], "UnityEngine.CoreModule.dll"));
var tokens = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Fields).Concat(t.Methods)).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
string Body(MethodDef method)
{
    if (!method.HasBody) return "<no IL>";
    var il = method.Body.Instructions;
    string Operand(object? value) => value switch {
        null => "", Instruction target => "IL" + il.IndexOf(target), IList<Instruction> targets => string.Join(",", targets.Select(t => il.IndexOf(t))),
        Local local => "local" + local.Index, dnlib.DotNet.Parameter parameter => "arg" + parameter.Index, IFullName named => named.FullName,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? "" };
    string Code(Instruction i) => i.OpCode.OperandType == OperandType.ShortInlineBrTarget ? i.OpCode.Code.ToString()[..^2] : i.OpCode.Code.ToString();
    return string.Join("\n", il.Select(i => Code(i) + ":" + Operand(i.Operand))) + "\nLocals:" + method.Body.InitLocals + ":" +
        string.Join(",", method.Body.Variables.Select(v => v.Type.FullName)) + "\nEH:" + string.Join(";", method.Body.ExceptionHandlers.Select(h =>
        h.HandlerType + ":" + il.IndexOf(h.TryStart) + "," + il.IndexOf(h.TryEnd) + "," + il.IndexOf(h.HandlerStart) + "," + il.IndexOf(h.HandlerEnd) + "," + h.CatchType));
}
var bodies = source.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, Body);
var refs = source.GetAssemblyRefs().Select(r => r.FullName).OrderBy(x => x).ToArray();
// Inventory actual managed callers, including current port helpers. Engine API
// implementations themselves are not consumers of Aura's privately-created arrays.
var cpuApis = new HashSet<string> { "GetPixels", "GetPixels32", "GetPixel", "GetPixelBilinear", "GetPixelData", "GetRawTextureData", "SetPixel", "SetPixels", "SetPixels32", "SetPixelData", "LoadRawTextureData" };
var cpuCalls = new List<object>(); var inspected = new List<object>(); var arrayCalls = new List<object>();
foreach (string path in Directory.GetFiles(args[1], "*.dll").OrderBy(x => x))
{
    using var module = ModuleDefMD.Load(path); int methods = 0;
    foreach (var method in module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
    {
        ++methods;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not IMethod call || call.DeclaringType.FullName != "UnityEngine.Texture2DArray") continue;
            var row = new { assembly = Path.GetFileName(path), method = method.FullName, token = method.MDToken.Raw.ToString("x8"), call = call.FullName };
            if (!Path.GetFileName(path).StartsWith("UnityEngine.", StringComparison.Ordinal)) arrayCalls.Add(row);
            if (cpuApis.Contains(call.Name) && !Path.GetFileName(path).StartsWith("UnityEngine.", StringComparison.Ordinal)) cpuCalls.Add(row);
        }
    }
    inspected.Add(new { file = Path.GetFileName(path), sha256 = Hash(path), bodies = methods });
}
Require(cpuCalls.Count == 0, "A non-engine Texture2DArray CPU reader/writer exists; review before making arrays unreadable");
var generate = (MethodDef)source.ResolveToken(0x06000120);
Require(generate.FullName == "System.Void Aura2API.Texture2DArrayComposer::Generate()", "Exact array generator changed");
var il = generate.Body.Instructions;
var allocations = il.Select((i, index) => (i, index)).Where(x => x.i.OpCode == OpCodes.Newobj && x.i.Operand is IMethod m && m.FullName ==
    "System.Void UnityEngine.Texture2DArray::.ctor(System.Int32,System.Int32,System.Int32,UnityEngine.TextureFormat,System.Boolean,System.Boolean)").ToArray();
Require(allocations.Length == 1, "Expected one guarded array creation");
int allocation = allocations[0].index;
Require(il[allocation - 3].IsLdcI4() && il[allocation - 3].GetLdcI4Value() == 0 && il[allocation - 2].OpCode == OpCodes.Ldarg_0 &&
    il[allocation - 1].OpCode == OpCodes.Ldfld && ((IField)il[allocation - 1].Operand).Name == "_linear", "Array must have no mip chain and retain exact linear flag");
Require(il[allocation + 1].OpCode == OpCodes.Call && il[allocation + 1].Operand is IMethod set && set.FullName == "System.Void Aura2API.Texture2DArrayComposer::set_ArrayTexture(UnityEngine.Texture2DArray)", "Array initialization setter changed");
var getter = source.Find("Aura2API.Texture2DArrayComposer", false).Methods.Single(m => m.Name == "get_ArrayTexture");
var copy = il.Select((i, index) => (i, index)).Where(x => x.i.Operand is IMethod m && m.DeclaringType.FullName == "UnityEngine.Graphics" && m.Name == "CopyTexture").ToArray();
Require(copy.Length == 1 && copy[0].index > allocation + 2, "Expected a later GPU-only copy loop");
Require(!source.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Any(i => i.Operand is IMethod m && m.DeclaringType.FullName == "UnityEngine.Texture2DArray" && m.Name == "Apply"), "Existing array Apply requires a new sequence audit");
var platform = source.Import(core.Find("UnityEngine.Application", false).Methods.Single(m => m.Name == "get_platform"));
var api = source.Import(core.Find("UnityEngine.SystemInfo", false).Methods.Single(m => m.Name == "get_graphicsDeviceType"));
var apply = source.Import(core.Find("UnityEngine.Texture2DArray", false).Methods.Single(m => m.Name == "Apply" && m.MethodSig.Params.Count == 2));
Require(apply.FullName == "System.Void UnityEngine.Texture2DArray::Apply(System.Boolean,System.Boolean)", "Apply overload changed");
int at = allocation + 2;
var join = il[at];
var guard = new[] { Instruction.Create(OpCodes.Call, platform), Instruction.CreateLdcI4(11), Instruction.Create(OpCodes.Bne_Un, join),
    Instruction.Create(OpCodes.Call, api), Instruction.CreateLdcI4(11), Instruction.Create(OpCodes.Bne_Un, join),
    Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, getter), Instruction.CreateLdcI4(0), Instruction.CreateLdcI4(1), Instruction.Create(OpCodes.Callvirt, apply) };
for (int i = 0; i < guard.Length; ++i) il.Insert(at + i, guard[i]);
generate.Body.SimplifyBranches(); string writtenBody = Body(generate);
// A branch entering the pre-existing loop bypasses this new one-time block.
Require(copy[0].i == il[copy[0].index + guard.Length], "GPU copy loop reordered");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(source); options.MetadataOptions.Flags = MetadataFlags.PreserveAll; source.Write(output, options);
using var check = ModuleDefMD.Load(output);
Require(JsonSerializer.Serialize(ReadLayout(output)) == JsonSerializer.Serialize(layoutBefore), "Metadata table/ownership changed");
Require(check.Mvid == source.Mvid && check.Assembly.FullName == source.Assembly.FullName && check.GetAssemblyRefs().Select(r => r.FullName).OrderBy(x => x).SequenceEqual(refs), "Assembly identity/dependencies changed");
foreach (var pair in tokens) Require(check.ResolveToken(pair.Key)?.ToString() == pair.Value, "Original token changed");
foreach (var pair in bodies)
{
    var method = (MethodDef)check.ResolveToken(pair.Key);
    if (pair.Key != generate.MDToken.Raw) { Require(Body(method) == pair.Value, "Unrelated Aura method changed"); continue; }
    Require(Body(method) == writtenBody, "Saved one-time Apply predicate differs");
    Require(method.Body.Instructions.Count(i => i.Operand is IMethod m && m.FullName == apply.FullName) == 1, "Apply count changed");
    for (int i = guard.Length - 1; i >= 0; --i) method.Body.Instructions.RemoveAt(at + i);
    Require(Body(method) == pair.Value, "Generator differs beyond one-time pre-copy Apply");
}
Require(Hash(args[0]) == ExpectedSource, "Input changed concurrently");
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source = Path.GetFullPath(args[0]), source_sha256 = ExpectedSource,
    output_sha256 = Hash(output), original_tokens_verified = tokens.Count, original_bodies_verified = bodies.Count,
    metadata_layout_identical = true, method_ptr_rows = 0, added_methods = 0, added_types = 0, new_assembly_references = 0,
    patch = new { method = generate.FullName, token = generate.MDToken.Raw.ToString("x8"), instruction = at, inserted_instructions = guard.Length,
        behavior = "Android/GLES3 Apply(false,true) after fresh array setter, BEFORE every initial GPU copy; never repeated in existing-array copy loop" },
    cpu_array_readers_or_writers = cpuCalls, non_engine_array_calls = arrayCalls, audited_assemblies = inspected, source_rfloat_guards_retained = true,
    runtime_verified = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("AURA_ARRAY_CPU_COPY_RELEASE_VERIFIED " + output + " " + Hash(output));

sealed record Layout(Dictionary<string, int> Tables, Dictionary<int, int[]> MethodsByType);
