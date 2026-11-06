using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 3) throw new ArgumentException("canonical Aura2_Core.dll, UnityEngine.CoreModule.dll, work/output.dll");
const string ExpectedSource = "bdd78261a17ff2fa8a00e0115d12d4decff2d40380de700569cc2236f8a18e5a";
void Require(bool condition, string text) { if (!condition) throw new InvalidDataException(text); }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
string output = Path.GetFullPath(args[2]);
string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
Require(output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Output must remain in workspace work");
Require(Hash(args[0]) == ExpectedSource, "Canonical Aura preimage differs (must retain existing occlusion/dummy format patches)");
Layout ReadLayout(string path)
{
    using var stream = File.OpenRead(path); using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var indexes = new[] { TableIndex.TypeDef, TableIndex.Field, TableIndex.MethodDef, TableIndex.Param, TableIndex.InterfaceImpl,
        TableIndex.MethodImpl, TableIndex.Event, TableIndex.Property, TableIndex.EventMap, TableIndex.PropertyMap,
        TableIndex.MethodSemantics, TableIndex.NestedClass, TableIndex.GenericParam, TableIndex.GenericParamConstraint,
        TableIndex.MethodPtr, TableIndex.FieldPtr, TableIndex.ParamPtr, TableIndex.EventPtr, TableIndex.PropertyPtr };
    var counts = indexes.ToDictionary(index => index.ToString(), index => metadata.GetTableRowCount(index));
    Require(new[] { "MethodPtr", "FieldPtr", "ParamPtr", "EventPtr", "PropertyPtr" }.All(name => counts[name] == 0), "Unexpected pointer table");
    var owners = metadata.TypeDefinitions.ToDictionary(handle => MetadataTokens.GetToken(handle), handle =>
        metadata.GetTypeDefinition(handle).GetMethods().Select(h => MetadataTokens.GetToken(h)).ToArray());
    return new Layout(counts, owners);
}
var originalLayout = ReadLayout(args[0]);
using var source = ModuleDefMD.Load(args[0]); using var core = ModuleDefMD.Load(args[1]);
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
var originalTokens = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Fields).Concat(t.Methods)).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
var originalBodies = source.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, Body);
var references = source.GetAssemblyRefs().Select(a => a.FullName).OrderBy(x => x).ToArray();
int EnumValue(string type, string name) => Convert.ToInt32(core.Find(type, false).Fields.Single(f => f.Name == name).Constant.Value);
int android = EnumValue("UnityEngine.RuntimePlatform", "Android"), gles = EnumValue("UnityEngine.Rendering.GraphicsDeviceType", "OpenGLES3");
int rtR = EnumValue("UnityEngine.RenderTextureFormat", "RFloat"), rtRGBA = EnumValue("UnityEngine.RenderTextureFormat", "ARGBFloat");
int texR = EnumValue("UnityEngine.TextureFormat", "RFloat"), texRGBA = EnumValue("UnityEngine.TextureFormat", "RGBAFloat");
Require(android == 11 && gles == 11 && rtR == 14 && rtRGBA == 11 && texR == 18 && texRGBA == 20, "Exact Unity enum contract differs");
IMethod Import(string type, string name) => source.Import(core.Find(type, false).Methods.Single(m => m.Name == name));
var platform = Import("UnityEngine.Application", "get_platform");
var api = Import("UnityEngine.SystemInfo", "get_graphicsDeviceType");
var supportsRT = Import("UnityEngine.SystemInfo", "SupportsRenderTextureFormat");
var supportsTex = Import("UnityEngine.SystemInfo", "SupportsTextureFormat");
var changed = new Dictionary<uint, Patch>();
void InsertGuard(MethodDef method, int at, int oldFormat, int newFormat, Func<Instruction[]> width, Func<Instruction[]> height)
{
    var il = method.Body.Instructions;
    Require(il[at].IsLdcI4() && il[at].GetLdcI4Value() == oldFormat, "Original format constant differs");
    var oldCode = il[at].OpCode; var oldOperand = il[at].Operand;
    var fallback = Instruction.CreateLdcI4(oldFormat); var join = il[at + 1];
    var caps = Instruction.CreateLdcI4(rtR);
    var guard = new List<Instruction> {
        Instruction.Create(OpCodes.Call, platform), Instruction.CreateLdcI4(android), Instruction.Create(OpCodes.Bne_Un, fallback),
        Instruction.Create(OpCodes.Call, api), Instruction.CreateLdcI4(gles), Instruction.Create(OpCodes.Bne_Un, fallback) };
    // Only authored square spot/directional maps are single-channel-safe.
    // Point maps (512x256) and directional two-cascade maps retain RGBAFloat.
    guard.AddRange(width()); guard.AddRange(height()); guard.Add(Instruction.Create(OpCodes.Bne_Un, fallback));
    guard.AddRange(width()); guard.Add(Instruction.CreateLdcI4(256)); guard.Add(Instruction.Create(OpCodes.Beq, caps));
    guard.AddRange(width()); guard.Add(Instruction.CreateLdcI4(512)); guard.Add(Instruction.Create(OpCodes.Bne_Un, fallback));
    guard.Add(caps); guard.Add(Instruction.Create(OpCodes.Call, supportsRT)); guard.Add(Instruction.Create(OpCodes.Brfalse, fallback));
    guard.Add(Instruction.CreateLdcI4(texR)); guard.Add(Instruction.Create(OpCodes.Call, supportsTex)); guard.Add(Instruction.Create(OpCodes.Brfalse, fallback));
    guard.Add(Instruction.CreateLdcI4(newFormat)); guard.Add(Instruction.Create(OpCodes.Br, join)); guard.Add(fallback);
    // Preserve original branch/EH anchors by reusing the first instruction object.
    il[at].OpCode = guard[0].OpCode; il[at].Operand = guard[0].Operand;
    for (int i = 1; i < guard.Count; ++i) il.Insert(at + i, guard[i]);
    method.Body.SimplifyBranches();
    changed.Add(method.MDToken.Raw, new Patch(at, guard.Count - 1, oldCode, oldOperand, oldFormat, newFormat, Body(method)));
}
var arrayCtor = (MethodDef)source.ResolveToken(0x060000f8);
Require(arrayCtor.FullName == "System.Void Aura2API.ShadowmapsCollector::.ctor(System.Int32,System.Int32)", "Shadow collector constructor changed");
var a = arrayCtor.Body.Instructions;
Require(a.Count == 10 && a[0].OpCode == OpCodes.Ldarg_0 && a[1].OpCode == OpCodes.Ldarg_1 && a[2].OpCode == OpCodes.Ldarg_2 &&
    a[4].IsLdcI4() && a[4].GetLdcI4Value() == 1 && a[5].OpCode == OpCodes.Call && a[5].Operand is IMethod parent &&
    parent.FullName == "System.Void Aura2API.Texture2DArrayComposer::.ctor(System.Int32,System.Int32,UnityEngine.TextureFormat,System.Boolean)" &&
    a[8].OpCode == OpCodes.Stfld && ((IField)a[8].Operand).Name == "alwaysGenerateOnUpdate", "Shadow collector format/flags dataflow changed");
InsertGuard(arrayCtor, 3, texRGBA, texR, () => new[] { Instruction.Create(OpCodes.Ldarg_1) }, () => new[] { Instruction.Create(OpCodes.Ldarg_2) });
var initialize = (MethodDef)source.ResolveToken(0x0600002f);
Require(initialize.FullName == "System.Void Aura2API.AuraLight::Initialize()", "AuraLight initialize changed");
var b = initialize.Body.Instructions;
var rtConstructors = b.Select((i, index) => (i, index)).Where(x => x.i.OpCode == OpCodes.Newobj && x.i.Operand is IMethod m && m.FullName ==
    "System.Void UnityEngine.RenderTexture::.ctor(System.Int32,System.Int32,System.Int32,UnityEngine.RenderTextureFormat,UnityEngine.RenderTextureReadWrite)").ToArray();
Require(rtConstructors.Length == 2, "Expected separate shadow-map and packed-directional-data allocations");
int allocation = rtConstructors[0].index, format = allocation - 2;
Require(b[allocation + 1].OpCode == OpCodes.Stfld && ((IField)b[allocation + 1].Operand).Name == "shadowMapRenderTexture", "First RT is not shadow-map producer");
Require(b[rtConstructors[1].index + 1].OpCode == OpCodes.Stfld && ((IField)b[rtConstructors[1].index + 1].Operand).Name == "shadowDataRenderTexture", "Packed directional data boundary changed");
Require(b[allocation - 1].IsLdcI4() && b[allocation - 1].GetLdcI4Value() == 1 && b[format - 1].IsLdcI4() && b[format - 1].GetLdcI4Value() == 0, "Shadow producer depth/linear flags changed");
Require(b[format - 5].OpCode == OpCodes.Ldloca_S && b[format - 3].OpCode == OpCodes.Ldloca_S && ReferenceEquals(b[format - 5].Operand, b[format - 3].Operand), "Shadow width/height must read one Vector2Int local");
var size = (Local)b[format - 5].Operand;
Require(size.Type.FullName == "UnityEngine.Vector2Int" && b[format - 4].OpCode == OpCodes.Call && b[format - 2].OpCode == OpCodes.Call, "Shadow size getters changed");
var xGetter = (IMethod)b[format - 4].Operand; var yGetter = (IMethod)b[format - 2].Operand;
Require(xGetter.FullName == "System.Int32 UnityEngine.Vector2Int::get_x()" && yGetter.FullName == "System.Int32 UnityEngine.Vector2Int::get_y()", "Shadow dimensions are no longer exact x/y");
InsertGuard(initialize, format, rtRGBA, rtR,
    () => new[] { Instruction.Create(OpCodes.Ldloca, size), Instruction.Create(OpCodes.Call, xGetter) },
    () => new[] { Instruction.Create(OpCodes.Ldloca, size), Instruction.Create(OpCodes.Call, yGetter) });
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(source); options.MetadataOptions.Flags = MetadataFlags.PreserveAll; source.Write(output, options);
using var check = ModuleDefMD.Load(output);
var layout = ReadLayout(output);
Require(JsonSerializer.Serialize(layout) == JsonSerializer.Serialize(originalLayout), "Metadata counts/method ownership changed");
foreach (var pair in originalTokens) Require(check.ResolveToken(pair.Key)?.ToString() == pair.Value, "Original metadata token changed");
Require(check.Mvid == source.Mvid && check.Assembly.FullName == source.Assembly.FullName && check.GetAssemblyRefs().Select(r => r.FullName).OrderBy(x => x).SequenceEqual(references), "Assembly identity/dependencies changed");
foreach (var pair in originalBodies)
{
    var method = (MethodDef)check.ResolveToken(pair.Key);
    if (!changed.TryGetValue(pair.Key, out var patch)) { Require(Body(method) == pair.Value, "Unrelated Aura method changed: " + method.FullName); continue; }
    Require(Body(method) == patch.WrittenBody, "Saved predicate/branch targets differ");
    method.Body.Instructions[patch.At].OpCode = patch.OriginalCode; method.Body.Instructions[patch.At].Operand = patch.OriginalOperand;
    for (int i = patch.Inserted; i >= 1; --i) method.Body.Instructions.RemoveAt(patch.At + i);
    Require(Body(method) == pair.Value, "A non-format field/operation changed");
}
// The RT and array predicates must choose the same storage family for all cases.
int Choose(int w, int h, bool androidGles, bool renderable, bool sampleable, int before, int after) =>
    androidGles && renderable && sampleable && w == h && (w == 256 || w == 512) ? after : before;
int cases = 0;
foreach (var dims in new[] { (256, 256), (512, 512), (512, 256), (32, 1), (1024, 1024), (256, 512) })
foreach (bool device in new[] { false, true }) foreach (bool rt in new[] { false, true }) foreach (bool texture in new[] { false, true })
{
    bool renderR = Choose(dims.Item1, dims.Item2, device, rt, texture, rtRGBA, rtR) == rtR;
    bool arrayR = Choose(dims.Item1, dims.Item2, device, rt, texture, texRGBA, texR) == texR;
    Require(renderR == arrayR, "Producer/array mismatch"); ++cases;
}
Require(Hash(args[0]) == ExpectedSource, "Canonical source changed concurrently");
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source = Path.GetFullPath(args[0]), source_sha256 = ExpectedSource,
    output_sha256 = Hash(output), original_tokens_verified = originalTokens.Count, original_bodies_verified = originalBodies.Count,
    metadata_layout_identical = true, method_ptr_rows = layout.Tables["MethodPtr"], added_methods = 0, added_types = 0, new_assembly_references = 0,
    source_2_existing_format_patches_preserved = true, predicate_cases = cases, scope = "Android OpenGLES3 + both RFloat capabilities + exact square 256/512 shadows only",
    patches = changed.Select(p => new { token = p.Key.ToString("x8"), instruction = p.Value.At, inserted_instructions = p.Value.Inserted,
        old_format = p.Value.OldFormat, new_format = p.Value.NewFormat }), untouched = new[] { "512x256 point shadows", "512x256 directional two cascades", "32x1 packed directional data", "cookies", "all shadow resolutions", "all non-GLES platforms" },
    authored_initial_spot_cpu_array_bytes_before = 15728640, authored_initial_spot_cpu_array_bytes_after = 3932160,
    authored_initial_spot_rt_payload_bytes_before = 15728640, authored_initial_spot_rt_payload_bytes_after = 3932160,
    runtime_verified = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("AURA_SHADOW_RFLOAT_VERIFIED " + output + " " + Hash(output));

sealed record Layout(Dictionary<string, int> Tables, Dictionary<int, int[]> MethodsByType);
sealed record Patch(int At, int Inserted, OpCode OriginalCode, object? OriginalOperand, int OldFormat, int NewFormat, string WrittenBody);
