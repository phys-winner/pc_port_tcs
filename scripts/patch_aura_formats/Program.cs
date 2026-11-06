using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 4) throw new ArgumentException("source.dll helper.dll UnityEngine.CoreModule.dll output.dll");
const string ExpectedSource = "be698f844f7db1bc1914c9c29ac66b8d07a727a7cbe56a881b279427ccddf4a0";
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
if (Hash(args[0]) != ExpectedSource) throw new InvalidDataException("Original Aura2_Core.dll hash mismatch");
string output = Path.GetFullPath(args[3]);
string allowed = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
if (!output.StartsWith(allowed, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Output must stay inside workspace work directory");
using var source = ModuleDefMD.Load(args[0]);
using var helper = ModuleDefMD.Load(args[1]);
using var core = ModuleDefMD.Load(args[2]);
var originals = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Methods).Concat(t.Fields)).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
var enumType = core.Find("UnityEngine.RenderTextureFormat", false);
int EnumValue(string name) => Convert.ToInt32(enumType.Fields.Single(f => f.Name == name).Constant.Value);
var evidence = new List<object>();
void Patch(uint token, string fullName, string constructorType, int constantsFromEnd, string oldFormat, string helperName)
{
    var method = (MethodDef)source.ResolveToken(token);
    if (method.FullName != fullName) throw new InvalidDataException("Aura method token/signature mismatch");
    var instructions = method.Body.Instructions;
    var allocations = instructions.Select((instruction, index) => (instruction, index)).Where(x => x.instruction.OpCode == OpCodes.Newobj && ((IMethod)x.instruction.Operand).DeclaringType.FullName == constructorType).ToArray();
    if (allocations.Length != 1) throw new InvalidDataException("Expected exactly one Aura texture allocation");
    int position = allocations[0].index - constantsFromEnd;
    if (position < 0 || !instructions[position].IsLdcI4() || instructions[position].GetLdcI4Value() != EnumValue(oldFormat)) throw new InvalidDataException("Aura texture format argument mismatch");
    for (int i = position + 1; i < allocations[0].index; ++i)
        if (!instructions[i].IsLdcI4()) throw new InvalidDataException("Unexpected allocation argument stack shape");
    var choose = helper.Find("AuraGlesFormats", false).Methods.Single(m => m.Name == helperName);
    if (choose.MethodSig.Params.Count != 0 || choose.ReturnType.FullName != "UnityEngine.RenderTextureFormat") throw new InvalidDataException("Format helper signature mismatch");
    instructions[position].OpCode = OpCodes.Call;
    instructions[position].Operand = source.Import(choose);
    evidence.Add(new { token = token.ToString("x8"), method = method.FullName, instruction = position, original_format = oldFormat, helper = choose.FullName, android_gles_format = helperName == "Occlusion" ? "RFloat" : "RInt" });
}
Patch(0x0600013f, "Aura2API.SwappableRenderTexture Aura2API.TextureBuffers::get_OcclusionTexture()", "Aura2API.SwappableRenderTexture", 4, "RHalf", "Occlusion");
Patch(0x06000044, "UnityEngine.RenderTexture Aura2API.AuraResourcesCollection::get_DummyTextureUAV()", "UnityEngine.RenderTexture", 2, "R8", "DummySlice");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(source);
options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
source.Write(output, options);
using var check = ModuleDefMD.Load(output);
foreach (var pair in originals)
    if (check.ResolveToken(pair.Key)?.ToString() != pair.Value) throw new InvalidDataException("Original metadata token changed: " + pair.Key.ToString("x8"));
foreach (var entry in evidence)
{
    using var row = JsonDocument.Parse(JsonSerializer.Serialize(entry));
    uint token = Convert.ToUInt32(row.RootElement.GetProperty("token").GetString(), 16);
    int position = row.RootElement.GetProperty("instruction").GetInt32();
    var instruction = ((MethodDef)check.ResolveToken(token)).Body.Instructions[position];
    if (instruction.OpCode != OpCodes.Call || ((IMethod)instruction.Operand).FullName != row.RootElement.GetProperty("helper").GetString()) throw new InvalidDataException("Written format patch verification failed");
}
if (Hash(args[0]) != ExpectedSource) throw new InvalidDataException("Original changed during conversion");
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source_sha256 = ExpectedSource, output_sha256 = Hash(output), helper_sha256 = Hash(args[1]), original_metadata_tokens_verified = originals.Count, patches = evidence, runtime_verified = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("AURA_GLES_FORMAT_PATCH_VERIFIED " + output);
