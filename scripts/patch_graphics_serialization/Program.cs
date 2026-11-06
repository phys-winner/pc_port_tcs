using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 3) throw new ArgumentException("source Assembly-CSharp.dll, rebuilt ClosingShift.Android.dll, private work/output.dll");
const string ExpectedSource = "a094d21c5e8821c4be04f5f56b4427ffe7b3de15cc1bedda77c018e3d5769899";
const uint TargetToken = 0x06000390;
const string OldHook = "System.Void ClosingShift.Android.AndroidGraphics::BeforeApplyQuality(UnityEngine.MonoBehaviour)";
const string NewHook = "System.Void ClosingShift.Android.AndroidGraphics::BeforeSerializeQuality(UnityEngine.MonoBehaviour)";
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
void Preimage(byte[] bytes) => Require(Hash(bytes) == ExpectedSource, "Exact a094 Game preimage required");
void Reject(Action action, string name)
{
    try { action(); } catch (InvalidDataException) { return; }
    throw new InvalidDataException("Negative guard accepted " + name);
}
string root = Path.GetFullPath(Directory.GetCurrentDirectory());
Require(File.Exists(Path.Combine(root, "scripts/patch_managed/AndroidGraphics.cs")), "Run from the project root");
string output = Path.GetFullPath(args[2]);
Require(output.StartsWith(Path.Combine(root, "work") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Output must remain under project work");
for (var directory = new DirectoryInfo(Path.GetDirectoryName(output)!); directory != null && directory.FullName.Length >= root.Length; directory = directory.Parent)
    Require(!directory.Exists || (directory.Attributes & System.IO.FileAttributes.ReparsePoint) == 0, "Output parent is a reparse point");
Require(!File.Exists(output) && !File.Exists(output + ".manifest.json"), "Private outputs must be new");
byte[] input = File.ReadAllBytes(args[0]), runtimeBytes = File.ReadAllBytes(args[1]);
Preimage(input);
MethodDef Target(ModuleDefMD module)
{
    var method = module.ResolveToken(TargetToken) as MethodDef;
    Require(method != null && method.FullName == "System.Void UICustomOptions::SerializeGraphic()" && method.HasBody, "Serialization token/signature changed");
    return method!;
}
MemberRef Anchor(ModuleDefMD module)
{
    var il = Target(module).Body.Instructions;
    Require(il.Count > 4 && il[0].OpCode == OpCodes.Ldarg_0 && il[1].OpCode == OpCodes.Call &&
        il[1].Operand is IMethod first && first.FullName == "System.Void ClosingShift.Android.AndroidGraphics::ApplyOptions(UnityEngine.MonoBehaviour)" &&
        il[2].OpCode == OpCodes.Ldarg_0 && il[3].OpCode == OpCodes.Call && il[3].Offset == 7 &&
        il[3].Operand is MemberRef hook && hook.FullName == OldHook, "Exact two-prefix serialization anchor changed");
    Require(il.Count(i => i.Operand is IMethod call && call.FullName == OldHook) == 1 &&
        il.Count(i => i.Operand is IMethod call && call.FullName == "System.Void ConfigHandler::Serialize(System.String,System.Collections.Generic.Dictionary`2<System.String,System.String>)") == 1,
        "Serialization hook/writer count changed");
    var apply = module.ResolveToken(0x0600038D) as MethodDef;
    Require(apply != null && apply.FullName == "System.Void UICustomOptions::ApplyGraphic(System.Boolean)" &&
        apply.Body.Instructions.Count(i => i.Operand is IMethod call && call.FullName == OldHook) == 1, "Actual apply must retain its original suspension prefix");
    return (MemberRef)il[3].Operand;
}
void RuntimeContract(ModuleDefMD module)
{
    Require(module.Assembly.Name == "ClosingShift.Android", "Unexpected helper assembly");
    var type = module.Find("ClosingShift.Android.AndroidGraphics", false);
    var method = type?.Methods.SingleOrDefault(m => m.FullName == NewHook);
    Require(method != null && method.IsPublic && method.IsStatic && method.HasBody, "Rebuilt serialization helper is missing");
    string[] forbidden = { "BeforeQualityChange", "ApplyCameras", "ApplyLow", "CaptureBaseline", "SetProfileShortSide", "SetRenderPercent" };
    Require(!method!.Body.Instructions.Any(i => i.Operand is IMethod call && forbidden.Contains(call.Name.String)), "Serialization helper directly invokes runtime quality work");
    var prepare = method.Body.Instructions.Select((i, n) => (i, n)).Where(x => x.i.Operand is IMethod call && call.Name == "PrepareUi").ToArray();
    Require(prepare.Length == 1 && prepare[0].n > 0 && method.Body.Instructions[prepare[0].n - 1].IsLdcI4() &&
        method.Body.Instructions[prepare[0].n - 1].GetLdcI4Value() == 0, "Serialization must skip PrepareUi display initialization");
}
Snapshot ReadSnapshot(byte[] bytes)
{
    using var stream = new MemoryStream(bytes); using var pe = new PEReader(stream); var md = pe.GetMetadataReader();
    var tables = Enum.GetValues<TableIndex>().ToDictionary(t => t.ToString(), t => md.GetTableRowCount(t));
    var bodies = new Dictionary<int, RawBody>();
    foreach (var handle in md.MethodDefinitions)
    {
        var definition = md.GetMethodDefinition(handle); if (definition.RelativeVirtualAddress == 0) continue;
        var body = pe.GetMethodBody(definition.RelativeVirtualAddress);
        string regions = JsonSerializer.Serialize(body.ExceptionRegions.Select(e => new { e.Kind, e.TryOffset, e.TryLength, e.HandlerOffset, e.HandlerLength, e.FilterOffset, Catch = MetadataTokens.GetToken(e.CatchType) }));
        bodies[MetadataTokens.GetToken(handle)] = new RawBody(Convert.ToHexString(body.GetILBytes()!), body.MaxStack,
            body.LocalVariablesInitialized, MetadataTokens.GetToken(body.LocalSignature), regions);
    }
    var owners = md.TypeDefinitions.ToDictionary(t => MetadataTokens.GetToken(t), t => md.GetTypeDefinition(t).GetMethods().Select(m => MetadataTokens.GetToken(m)).ToArray());
    return new Snapshot(tables, owners, bodies, unchecked((uint)pe.PEHeaders.CoffHeader.TimeDateStamp));
}
var before = ReadSnapshot(input);
using var source = ModuleDefMD.Load(input); using var runtime = ModuleDefMD.Load(runtimeBytes);
RuntimeContract(runtime);
var oldHook = Anchor(source);
var identities = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Fields).Concat(t.Methods).Concat(t.Properties).Concat(t.Events))
    .Concat(source.GetTypeRefs()).Concat(source.GetMemberRefs()).Concat(source.GetAssemblyRefs()).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
string[] dependencies = source.GetAssemblyRefs().Select(a => a.FullName).ToArray();
var bad = (byte[])input.Clone(); bad[^1] ^= 1; Reject(() => Preimage(bad), "wrong preimage");
using (var altered = ModuleDefMD.Load(input)) { Target(altered).Name = "SerializeOther"; Reject(() => Anchor(altered), "wrong method identity"); }
using (var altered = ModuleDefMD.Load(input)) { Target(altered).Body.Instructions[2].OpCode = OpCodes.Ldnull; Reject(() => Anchor(altered), "wrong owner argument"); }
using (var altered = ModuleDefMD.Load(input)) { Target(altered).Body.Instructions[3].Operand = Target(altered).Body.Instructions[1].Operand; Reject(() => Anchor(altered), "wrong original hook"); }
using (var altered = ModuleDefMD.Load(runtimeBytes)) { altered.Find("ClosingShift.Android.AndroidGraphics", false).Methods.Single(m => m.FullName == NewHook).Name = "Missing"; Reject(() => RuntimeContract(altered), "old runtime helper"); }
// Reuse the exact existing TypeRef and method signature; add only one MemberRef.
var newHook = new MemberRefUser(source, "BeforeSerializeQuality", oldHook.MethodSig.Clone(), oldHook.DeclaringType);
Target(source).Body.Instructions[3].Operand = newHook;
foreach (var method in source.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody)) method.Body.KeepOldMaxStack = true;
using var result = new MemoryStream();
var options = new ModuleWriterOptions(source); options.MetadataOptions.Flags = MetadataFlags.PreserveAll; options.PEHeadersOptions.TimeDateStamp = before.TimeStamp;
source.Write(result, options); byte[] written = result.ToArray();
var after = ReadSnapshot(written);
foreach (var pair in before.Tables) Require(after.Tables[pair.Key] == pair.Value + (pair.Key == nameof(TableIndex.MemberRef) ? 1 : 0), "Unexpected metadata table change: " + pair.Key);
Require(JsonSerializer.Serialize(before.Owners) == JsonSerializer.Serialize(after.Owners), "Method ownership changed");
Require(before.Bodies.Count == after.Bodies.Count, "Method body count changed");
foreach (var pair in before.Bodies)
{
    var actual = after.Bodies[pair.Key];
    if (pair.Key != TargetToken) { Require(actual == pair.Value, "Unrelated raw IL/body metadata changed: " + pair.Key.ToString("x8")); continue; }
    byte[] inverse = Convert.FromHexString(actual.IL), original = Convert.FromHexString(pair.Value.IL);
    Require(inverse.Length == original.Length, "Serialization IL size changed");
    Array.Copy(original, 8, inverse, 8, 4);
    Require(actual with { IL = Convert.ToHexString(inverse) } == pair.Value, "Serialization differs beyond the one call operand");
}
using var check = ModuleDefMD.Load(written);
Require(check.Mvid == source.Mvid && check.Assembly.FullName == source.Assembly.FullName && check.GetAssemblyRefs().Select(a => a.FullName).SequenceEqual(dependencies), "Assembly identity/dependencies changed");
foreach (var pair in identities) Require(check.ResolveToken(pair.Key)?.ToString() == pair.Value, "Existing token/signature changed: " + pair.Key.ToString("x8"));
Require(Target(check).Body.Instructions[3].Operand is IMethod saved && saved.FullName == NewHook, "New call not saved");
Reject(() => Preimage(written), "double patch");
Reject(() => Anchor(check), "already redirected hook");
Preimage(File.ReadAllBytes(args[0])); Require(Hash(File.ReadAllBytes(args[1])) == Hash(runtimeBytes), "Helper changed concurrently");
Directory.CreateDirectory(Path.GetDirectoryName(output)!); File.WriteAllBytes(output, written);
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source_sha256 = ExpectedSource, output_sha256 = Hash(written),
    runtime_sha256 = Hash(runtimeBytes), method = "UICustomOptions.SerializeGraphic", token = TargetToken.ToString("x8"),
    il_call_offset = 7, changed_operand_offset = 8, old_hook = OldHook, new_hook = NewHook,
    original_tokens_verified = identities.Count, unchanged_raw_bodies = before.Bodies.Count - 1, method_ownership_unchanged = true,
    added_member_refs = 1, other_metadata_table_counts_unchanged = true, inverse_il_exact = true, negative_guards = 7,
    actual_apply_body_exact = true, runtime_verified_on_device = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("GRAPHICS_SERIALIZATION_PATCH_PASS " + output + " " + Hash(written));

sealed record RawBody(string IL, int MaxStack, bool InitLocals, int LocalSignature, string ExceptionRegions);
sealed record Snapshot(Dictionary<string, int> Tables, Dictionary<int, int[]> Owners, Dictionary<int, RawBody> Bodies, uint TimeStamp);
