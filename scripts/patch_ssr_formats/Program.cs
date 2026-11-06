using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 3) throw new ArgumentException("source Unity.Postprocessing.Runtime.dll, UnityEngine.CoreModule.dll, output.dll; or --inspect assembly.dll output.json");
const string ExpectedSource = "c333956288d816a62c3574d01f2fdffc42f5c4a484fec4fb7334da417ab8fae8";
void Require(bool condition, string text) { if (!condition) throw new InvalidDataException(text); }
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
string output = Path.GetFullPath(args[2]);
string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
Require(output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase), "Output must stay inside workspace work directory");
Layout ReadLayout(string path)
{
    using var stream = File.OpenRead(path);
    using var pe = new PEReader(stream);
    var metadata = pe.GetMetadataReader();
    var indexes = new[] { TableIndex.TypeDef, TableIndex.Field, TableIndex.MethodDef, TableIndex.Param,
        TableIndex.InterfaceImpl, TableIndex.MethodImpl, TableIndex.Event, TableIndex.Property,
        TableIndex.EventMap, TableIndex.PropertyMap, TableIndex.MethodSemantics, TableIndex.NestedClass,
        TableIndex.GenericParam, TableIndex.GenericParamConstraint,
        TableIndex.MethodPtr, TableIndex.FieldPtr, TableIndex.ParamPtr, TableIndex.EventPtr, TableIndex.PropertyPtr };
    var tables = indexes.ToDictionary(index => index.ToString(), index => metadata.GetTableRowCount(index));
    var owners = new Dictionary<int, int[]>();
    if (tables["MethodPtr"] == 0)
        foreach (var handle in metadata.TypeDefinitions)
            owners.Add(MetadataTokens.GetToken(handle), metadata.GetTypeDefinition(handle).GetMethods().Select(h => MetadataTokens.GetToken(h)).ToArray());
    return new Layout(tables, owners, metadata.MetadataVersion);
}
if (args[0] == "--inspect")
{
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    File.WriteAllText(output, JsonSerializer.Serialize(ReadLayout(args[1]), new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("METADATA_LAYOUT_INSPECTED " + output);
    return;
}
Require(Hash(args[0]) == ExpectedSource, "Canonical PPS preimage hash changed");
var originalLayout = ReadLayout(args[0]);
Require(new[] { "MethodPtr", "FieldPtr", "ParamPtr", "EventPtr", "PropertyPtr" }.All(name => originalLayout.Tables[name] == 0), "Original PPS has an unexpected pointer-table layout");
using var module = ModuleDefMD.Load(args[0]);
using var core = ModuleDefMD.Load(args[1]);
var originalTokens = module.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Fields).Concat(t.Methods)).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
string Body(MethodDef method)
{
    if (!method.HasBody) return "<no IL>";
    var instructions = method.Body.Instructions;
    string Operand(object? value) => value switch {
        null => "", Instruction target => "IL" + instructions.IndexOf(target), IList<Instruction> targets => string.Join(",", targets.Select(t => instructions.IndexOf(t))),
        Local local => "local" + local.Index, dnlib.DotNet.Parameter parameter => "arg" + parameter.Index, IFullName named => named.FullName,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    string Code(Instruction instruction) => instruction.OpCode.OperandType == OperandType.ShortInlineBrTarget ? instruction.OpCode.Code.ToString()[..^2] : instruction.OpCode.Code.ToString();
    return string.Join("\n", instructions.Select(i => Code(i) + ":" + Operand(i.Operand))) + "\nEH:" +
        string.Join(";", method.Body.ExceptionHandlers.Select(h => h.HandlerType + ":" + instructions.IndexOf(h.TryStart) + "," + instructions.IndexOf(h.TryEnd) + "," + instructions.IndexOf(h.HandlerStart) + "," + instructions.IndexOf(h.HandlerEnd) + "," + h.CatchType));
}
var originalBodies = module.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, Body);
var originalReferences = module.GetAssemblyRefs().Select(a => a.FullName).OrderBy(x => x).ToArray();
var renderer = module.Find("UnityEngine.Rendering.PostProcessing.ScreenSpaceReflectionsRenderer", false);
Require(renderer != null, "Exact SSR renderer missing");
var getter = (MethodDef)module.ResolveToken(0x060001fb);
Require(getter.FullName == "UnityEngine.RenderTextureFormat UnityEngine.Rendering.PostProcessing.RuntimeUtilities::get_defaultHDRRenderTextureFormat()", "PPS default HDR getter token/signature changed");
Require(getter.Body.Instructions.Count == 2 && getter.Body.Instructions[0].IsLdcI4() && getter.Body.Instructions[1].OpCode == OpCodes.Ret, "PPS getter is no longer a constant fallback");
int EnumValue(string type, string name) => Convert.ToInt32(core.Find(type, false).Fields.Single(f => f.Name == name).Constant.Value);
int android = EnumValue("UnityEngine.RuntimePlatform", "Android");
int gles = EnumValue("UnityEngine.Rendering.GraphicsDeviceType", "OpenGLES3");
int half = EnumValue("UnityEngine.RenderTextureFormat", "ARGBHalf");
Require(getter.Body.Instructions[0].GetLdcI4Value() == EnumValue("UnityEngine.RenderTextureFormat", "DefaultHDR"), "PPS getter default changed");
Require(android == 11 && gles == 11 && half == 2, "Exact engine enum contract changed");
var platformGetter = core.Find("UnityEngine.Application", false).Methods.Single(m => m.Name == "get_platform");
var apiGetter = core.Find("UnityEngine.SystemInfo", false).Methods.Single(m => m.Name == "get_graphicsDeviceType");
Require(!renderer!.Methods.Any(m => m.Name == "AndroidSsrHdrFormat"), "SSR helper already exists");
var importedPlatform = module.Import(platformGetter);
var importedApi = module.Import(apiGetter);
var patched = new List<object>();
var affected = new HashSet<uint> { 0x06000081, 0x06000082 };
var indexes = new Dictionary<uint, int>();
foreach (uint token in affected)
{
    var method = (MethodDef)module.ResolveToken(token);
    string expectedName = token == 0x06000081 ? "CheckRT" : "Render";
    Require(method.DeclaringType == renderer && method.Name == expectedName, "SSR target method shape changed");
    var il = method.Body.Instructions;
    var hits = il.Select((instruction, index) => (instruction, index)).Where(x => x.instruction.OpCode == OpCodes.Call && x.instruction.Operand is IMethod call && call.FullName == getter.FullName).ToArray();
    Require(hits.Length == 1, "SSR method must contain exactly one default HDR format read");
    int at = hits[0].index;
    if (token == 0x06000081)
        Require(il[at + 1].OpCode == OpCodes.Newobj && ((IMethod)il[at + 1].Operand).FullName == "System.Void UnityEngine.RenderTexture::.ctor(System.Int32,System.Int32,System.Int32,UnityEngine.RenderTextureFormat)", "SSR CheckRT format does not feed exact RenderTexture constructor");
    else
    {
        Require(il[at - 1].IsStloc() && il[at + 1].IsStloc(), "SSR gaussian format/kernel local shape changed");
        Require(il.Count(i => i.Operand is string value && value == "KMain") == 1 && il.Count(i => i.Operand is string value && value == "_Result") == 1 && il.Count(i => i.Operand is string value && value == "_Source") == 1, "SSR gaussian kernel/resource names changed");
        Require(il.Count(i => i.Operand is IMethod call && call.Name == "CopyTexture" && call.DeclaringType.FullName == "UnityEngine.Rendering.CommandBuffer") == 2, "SSR copy producer/consumer shape changed");
        Require(il.Count(i => i.Operand is IMethod call && call.Name == "CheckRT" && call.DeclaringType == renderer) == 2, "SSR resolve/history pair changed");
    }
    var join = il[at + 1];
    var fallback = Instruction.Create(OpCodes.Call, getter);
    // Keep the original instruction object so every existing branch/EH anchor
    // enters the guard. No MethodDef/TypeDef additions or MethodPtr table.
    hits[0].instruction.Operand = importedPlatform;
    var insert = new[] { Instruction.CreateLdcI4(android), Instruction.Create(OpCodes.Bne_Un, fallback),
        Instruction.Create(OpCodes.Call, importedApi), Instruction.CreateLdcI4(gles),
        Instruction.Create(OpCodes.Bne_Un, fallback), Instruction.CreateLdcI4(half),
        Instruction.Create(OpCodes.Br, join), fallback };
    for (int i = 0; i < insert.Length; i++) il.Insert(at + 1 + i, insert[i]);
    method.Body.SimplifyBranches();
    indexes[token] = at;
    patched.Add(new { token = token.ToString("x8"), method = method.FullName, instruction = at, before = getter.FullName,
        after = "Inline Android + OpenGLES3 ? ARGBHalf : original getter", inserted_instructions = insert.Length });
}
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(module);
options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
module.Write(output, options);
using var check = ModuleDefMD.Load(output);
var savedLayout = ReadLayout(output);
Require(JsonSerializer.Serialize(savedLayout) == JsonSerializer.Serialize(originalLayout), "Metadata ownership/table counts changed; inline patch must not create pointer tables");
foreach (var pair in originalTokens) Require(check.ResolveToken(pair.Key)?.ToString() == pair.Value, "Original metadata token changed: " + pair.Key.ToString("x8"));
Require(check.Mvid == module.Mvid && check.Assembly.FullName == module.Assembly.FullName, "Assembly identity/MVID changed");
Require(check.GetAssemblyRefs().Select(a => a.FullName).OrderBy(x => x).SequenceEqual(originalReferences), "External helper reference or assembly dependency added");
foreach (var pair in originalBodies)
{
    var actual = (MethodDef)check.ResolveToken(pair.Key);
    if (!affected.Contains(pair.Key)) Require(Body(actual) == pair.Value, "Unrelated PPS method IL changed: " + actual.FullName);
    else
    {
        var il = actual.Body.Instructions;
        int at = indexes[pair.Key];
        bool Calls(int index, string name) => il[index].OpCode == OpCodes.Call && il[index].Operand is IMethod call && call.FullName == name;
        Require(Calls(at, platformGetter.FullName) && il[at + 1].IsLdcI4() && il[at + 1].GetLdcI4Value() == android, "Inline platform predicate changed");
        Require(il[at + 2].OpCode == OpCodes.Bne_Un && ReferenceEquals(il[at + 2].Operand, il[at + 8]), "Inline platform fallback target changed");
        Require(Calls(at + 3, apiGetter.FullName) && il[at + 4].IsLdcI4() && il[at + 4].GetLdcI4Value() == gles, "Inline GLES predicate changed");
        Require(il[at + 5].OpCode == OpCodes.Bne_Un && ReferenceEquals(il[at + 5].Operand, il[at + 8]), "Inline GLES fallback target changed");
        Require(il[at + 6].IsLdcI4() && il[at + 6].GetLdcI4Value() == half && il[at + 7].OpCode == OpCodes.Br && ReferenceEquals(il[at + 7].Operand, il[at + 9]), "Inline half format/join changed");
        Require(Calls(at + 8, getter.FullName), "Original format fallback missing");
        Require(il.Count(i => i.Operand is IMethod call && call.FullName == platformGetter.FullName) == 1 && il.Count(i => i.Operand is IMethod call && call.FullName == apiGetter.FullName) == 1, "Inline guard call counts changed");
        il[at].Operand = check.ResolveToken(0x060001fb);
        for (int i = 8; i >= 1; i--) il.RemoveAt(at + i);
        Require(Body(actual) == pair.Value, "SSR IL differs beyond the inline format predicate (long/short branch encoding normalized)");
    }
}
Require(check.GetTypes().SelectMany(t => t.Methods).Count() == originalBodies.Count, "A method was added or removed");
Require(Hash(args[0]) == ExpectedSource, "Input modified during patch");
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source = Path.GetFullPath(args[0]), source_sha256 = ExpectedSource, output_sha256 = Hash(output), original_metadata_tokens_verified = originalTokens.Count,
    original_method_bodies_verified = originalBodies.Count, added_internal_methods = 0, new_external_references = 0,
    metadata_layout = savedLayout, metadata_ownership_identical = true, formats = new { android_gles3 = "ARGBHalf=2 / rgba16f", other_platforms_or_apis = "Original DefaultHDR getter" }, patches = patched, runtime_verified = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("SSR_FORMAT_PATCH_VERIFIED " + output);

sealed record Layout(Dictionary<string, int> Tables, Dictionary<int, int[]> MethodsByType, string MetadataVersion);
