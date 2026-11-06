using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 4) throw new ArgumentException("source Aura2_Core.dll, resources.assets, level1, work output directory");
const string ExpectedSource = "de954e69e912fb252c23a5b10a5cea13559664aeb76994d3f574b976ed56d73d";
const uint TargetToken = 0x06000092;
const string TargetName = "System.Void Aura2API.Frustum::ComputeData()";
const string SetBufferName = "System.Void UnityEngine.ComputeShader::SetBuffer(System.Int32,System.String,UnityEngine.ComputeBuffer)";
const string DispatchName = "System.Void UnityEngine.ComputeShader::DispatchIndirect(System.Int32,UnityEngine.ComputeBuffer)";
void Require(bool condition, string message) { if (!condition) throw new InvalidDataException(message); }
string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
void CheckPreimage(byte[] bytes) => Require(Hash(bytes) == ExpectedSource, "Canonical Aura DLL preimage hash changed");
void Reject(Action action, string name)
{
    try { action(); }
    catch (InvalidDataException) { return; }
    throw new InvalidDataException("Negative self-check accepted " + name);
}
string root = Directory.GetCurrentDirectory();
Require(File.Exists(Path.Combine(root, "scripts/arm64_il2cpp/actual_build.py")), "Run from the project root");
string outputDirectory = Path.GetFullPath(args[3]);
string allowed = Path.GetFullPath(Path.Combine(root, "work/evidence/test-device/aura-dispatch21"));
Require(outputDirectory.Equals(allowed, StringComparison.OrdinalIgnoreCase), "Use the assigned private work output directory");
for (var directory = new DirectoryInfo(outputDirectory); directory != null && directory.FullName.Length >= root.Length; directory = directory.Parent)
    Require(!directory.Exists || (directory.Attributes & System.IO.FileAttributes.ReparsePoint) == 0, "Output path contains a reparse point");
string output = Path.Combine(outputDirectory, "Aura2_Core.dll");
Require(!File.Exists(output) && !File.Exists(output + ".manifest.json"), "Candidate outputs must be new");
byte[] input = File.ReadAllBytes(args[0]);
CheckPreimage(input);

MethodDef Target(ModuleDefMD module)
{
    var method = module.ResolveToken(TargetToken) as MethodDef;
    Require(method != null && method.FullName == TargetName && method.HasBody, "Exact method token/signature missing");
    return method!;
}
int Anchor(ModuleDefMD module)
{
    var il = Target(module).Body.Instructions;
    var hits = il.Select((instruction, index) => (instruction, index)).Where(x => x.instruction.Offset == 0x608).ToArray();
    Require(hits.Length == 1, "Original IL_0608 anchor missing");
    int at = hits[0].index;
    Require(il[at].OpCode == OpCodes.Ldloc_S && il[at].Operand is Local local && local.Index == 9, "Expected ldloc.s V_9 at IL_0608");
    bool Code(int index, OpCode code) => il[index].OpCode == code;
    bool Number(int index, int value) => il[index].IsLdcI4() && il[index].GetLdcI4Value() == value;
    bool Field(int index, string name) => Code(index, OpCodes.Ldfld) && il[index].Operand is IField field && field.FullName == name;
    bool Call(int index, string name) => Code(index, OpCodes.Callvirt) && il[index].Operand is IMethod call && call.FullName == name;
    const string ShaderField = "UnityEngine.ComputeShader Aura2API.Frustum::_computeVisibleCellsComputeShader";
    const string BuffersField = "UnityEngine.ComputeBuffer[] Aura2API.Frustum::_dispatchBuffers";
    // Each SetBuffer is this.shader, kernel4, name, this.buffers[index], call.
    foreach (var binding in new[] { (start: at - 20, name: "visibleCellsAmountBuffer", buffer: 0), (start: at - 11, name: "sizeBuffer", buffer: 1) })
    {
        int i = binding.start;
        Require(Code(i, OpCodes.Ldarg_0) && Field(i + 1, ShaderField) && Number(i + 2, 4) && Code(i + 3, OpCodes.Ldstr) && (string)il[i + 3].Operand == binding.name &&
            Code(i + 4, OpCodes.Ldarg_0) && Field(i + 5, BuffersField) && Number(i + 6, binding.buffer) && Code(i + 7, OpCodes.Ldelem_Ref) && Call(i + 8, SetBufferName), "Evaluator kernel4 resource binding changed");
    }
    Require(Code(at - 2, OpCodes.Ldarg_0) && Field(at - 1, ShaderField) && Code(at + 1, OpCodes.Ldarg_0) && Field(at + 2, BuffersField) &&
        Number(at + 3, 4) && Code(at + 4, OpCodes.Ldelem_Ref) && Call(at + 5, DispatchName), "Evaluator dispatch target/argument buffer changed");
    Require(il.Count(i => i.OpCode == OpCodes.Ldstr && Equals(i.Operand, "sizeBuffer")) == 1, "Ambiguous sizeBuffer binding");
    return at;
}
string Body(MethodDef method)
{
    if (!method.HasBody) return "<no IL>";
    var body = method.Body;
    var instructions = body.Instructions;
    string Operand(object? value) => value switch {
        null => "", Instruction target => "IL" + instructions.IndexOf(target), IList<Instruction> targets => string.Join(",", targets.Select(instructions.IndexOf)),
        Local local => "local" + local.Index, dnlib.DotNet.Parameter parameter => "arg" + parameter.Index,
        IMDTokenProvider token => token.MDToken.Raw.ToString("x8") + ":" + token,
        IFormattable number => number.ToString(null, CultureInfo.InvariantCulture), _ => value.ToString() ?? ""
    };
    return "Init=" + body.InitLocals + ";MaxStack=" + body.MaxStack + ";Locals=" + string.Join(";", body.Variables.Select(v => v.Type.FullName)) + "\n" +
        string.Join("\n", instructions.Select(i => i.OpCode.Code + ":" + Operand(i.Operand))) + "\nEH:" +
        string.Join(";", body.ExceptionHandlers.Select(h => h.HandlerType + ":" + instructions.IndexOf(h.TryStart) + "," + instructions.IndexOf(h.TryEnd) + "," + instructions.IndexOf(h.HandlerStart) + "," + instructions.IndexOf(h.HandlerEnd) + "," + instructions.IndexOf(h.FilterStart) + "," + h.CatchType));
}
byte[] MetadataExceptMethodRvas(byte[] bytes)
{
    using var pe = new PEReader(new MemoryStream(bytes, false));
    var reader = pe.GetMetadataReader();
    byte[] metadata = pe.GetMetadata().GetContent().ToArray();
    int offset = reader.GetTableMetadataOffset(TableIndex.MethodDef), size = reader.GetTableRowSize(TableIndex.MethodDef);
    for (int i = 0; i < reader.GetTableRowCount(TableIndex.MethodDef); i++) Array.Clear(metadata, offset + i * size, 4);
    return metadata;
}
byte[] Serialize(ModuleDefMD module)
{
    var options = new ModuleWriterOptions(module);
    options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
    using var stream = new MemoryStream();
    module.Write(stream, options);
    return stream.ToArray();
}
using var source = ModuleDefMD.Load(input);
int anchor = Anchor(source);
var bodies = source.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, Body);
var tokens = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Fields).Concat(t.Methods).Concat(t.Properties).Concat(t.Events)).ToDictionary(t => t.MDToken.Raw, t => t.ToString());
byte[] malformed = (byte[])input.Clone(); malformed[^1] ^= 1;
Reject(() => CheckPreimage(malformed), "wrong preimage");
using (var negative = ModuleDefMD.Load(input))
{
    var il = Target(negative).Body.Instructions;
    il[anchor].Operand = Target(negative).Body.Variables[8];
    Reject(() => Anchor(negative), "wrong local anchor");
    il[anchor].Operand = Target(negative).Body.Variables[9];
    il[anchor - 9].OpCode = OpCodes.Ldc_I4_3;
    Reject(() => Anchor(negative), "wrong evaluator resource kernel");
    il[anchor - 9].OpCode = OpCodes.Ldc_I4_4;
    il[anchor + 3].OpCode = OpCodes.Ldc_I4_3;
    Reject(() => Anchor(negative), "wrong dispatch argument buffer");
}

// Read current serialized producers/consumers with the installed UnityPy; no assets are written.
const string AssetCheck = """
import hashlib,json,math,re,struct,sys,UnityPy
def sha(b): return hashlib.sha256(b).hexdigest()
resources,level=sys.argv[1:]
e=UnityPy.load(resources)
visible=next(o for o in e.objects if o.path_id==1825)
assert sha(visible.get_raw_data())=='ced8494b63866184ee9417fb9c1fa64fe884690e405165777da5f1404c55c790'
t=visible.read_typetree(); assert t['m_Name']=='ComputeVisibleCellsBufferComputeShader'
v=next(v for v in t['variants'] if v['targetRenderer']==11)
assert v['targetLevel']==3 and v['resourcesResolved']
k=v['kernels'][4]; assert k['name']=='EvaluateDispatchSize'
m=k['variantMap'][0][1]; assert m['threadGroupSize']==[1,1,1]
assert [b['name'] for b in m['inBuffers']]==['visibleCellsAmountBuffer']
assert [b['name'] for b in m['outBuffers']]==['sizeBuffer']
s=bytes(m['code']).rstrip(b'\0').decode()
assert 'sizeBuffer_buf[0].value[(0 >> 2) + 2] = uint(u_xlatu0) * 4u;' in s
assert 'atomic' not in s and 'appendedCellsBuffer' not in s
data=next(o for o in e.objects if o.path_id==1826)
dv=next(v for v in data.read_typetree()['variants'] if v['targetRenderer']==11)
assert all(m['threadGroupSize']==[8,8,2] for k in dv['kernels'] for _,m in k['variantMap'])
quality=next(o for o in UnityPy.load(level).objects if o.path_id==36456).get_raw_data()
assert sha(quality)=='696ac2b39bd9097eb77703d3576b83a8de0b212e518728a349c1997e5b1c6747'
grid=struct.unpack_from('<3i',quality,68); assert grid==(64,40,64)
assert quality[172]==0 and quality[180]==1
cap=math.prod(grid); extra=8*8*grid[2]
assert cap==163840 and extra==4096 and extra*40==163840
for count in [0,1,127,128,129,511,512,513,4095,4096,4097,cap]:
    groups=math.ceil(math.cbrt(count)/8)
    assert groups*groups*(groups*4)*128>=count
print(json.dumps(dict(resources_sha256=sha(open(resources,'rb').read()),level_sha256=sha(open(level,'rb').read()),visible_shader_raw_sha256=sha(visible.get_raw_data()),data_shader_raw_sha256=sha(data.get_raw_data()),quality_raw_sha256=sha(quality),grid=grid,temporal=False,occlusion=True,evaluator_kernel=4,evaluator_threads=[1,1,1],evaluator_z_multiplier=4,consumer_threads=[8,8,2],capacity=cap,stride=40,duplicate_append_max=extra,potential_overrun_bytes=extra*40,singleton_evaluator_cannot_append=True,indirect_coverage_cases=12)))
""";
var processInfo = new ProcessStartInfo("C:/Program Files/Python313/python.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
foreach (string argument in new[] { "-B", "-c", AssetCheck, Path.GetFullPath(args[1]), Path.GetFullPath(args[2]) }) processInfo.ArgumentList.Add(argument);
using var process = Process.Start(processInfo) ?? throw new InvalidOperationException("Cannot start installed Python");
var stdoutTask = process.StandardOutput.ReadToEndAsync();
var stderrTask = process.StandardError.ReadToEndAsync();
Require(process.WaitForExit(30000), "Asset guard timed out");
string stdout = stdoutTask.GetAwaiter().GetResult(), stderr = stderrTask.GetAwaiter().GetResult();
Require(process.ExitCode == 0, "Asset guard failed: " + stderr);
using var assetEvidence = JsonDocument.Parse(stdout);

var changed = Target(source).Body.Instructions[anchor];
changed.OpCode = OpCodes.Ldc_I4_4;
changed.Operand = null;
Reject(() => Anchor(source), "already patched method");
byte[] candidate = Serialize(source);
using var check = ModuleDefMD.Load(candidate);
var saved = Target(check);
Require(saved.Body.Instructions.Count == Target(source).Body.Instructions.Count && saved.Body.Instructions[anchor].OpCode == OpCodes.Ldc_I4_4 && saved.Body.Instructions[anchor].Operand == null, "Saved replacement is not exactly ldc.i4.4");
Require(saved.Body.Instructions[anchor + 5].Operand is IMethod dispatch && dispatch.FullName == DispatchName, "Saved dispatch callee changed");
Require(MetadataExceptMethodRvas(candidate).SequenceEqual(MetadataExceptMethodRvas(input)), "Serialized metadata differs beyond method RVAs");
foreach (var pair in tokens) Require(check.ResolveToken(pair.Key)?.ToString() == pair.Value, "Metadata token/signature changed: " + pair.Key.ToString("x8"));
Require(check.Mvid == source.Mvid && check.Assembly.FullName == source.Assembly.FullName, "Assembly identity changed");
Require(check.GetTypes().SelectMany(t => t.Methods).Count() == bodies.Count, "Method count changed");
// Undo only the replacement in the reloaded candidate, then compare every method.
saved.Body.Instructions[anchor].OpCode = OpCodes.Ldloc_S;
saved.Body.Instructions[anchor].Operand = saved.Body.Variables[9];
foreach (var pair in bodies) Require(Body((MethodDef)check.ResolveToken(pair.Key)) == pair.Value, "Method differs beyond the one replacement: " + pair.Key.ToString("x8"));
byte[] restored = Serialize(check);
bool inverseByteExact = restored.SequenceEqual(input);
Require(MetadataExceptMethodRvas(restored).SequenceEqual(MetadataExceptMethodRvas(input)), "Inverse metadata reconstruction differs");
CheckPreimage(File.ReadAllBytes(args[0]));
Require(Hash(File.ReadAllBytes(args[1])) == assetEvidence.RootElement.GetProperty("resources_sha256").GetString() && Hash(File.ReadAllBytes(args[2])) == assetEvidence.RootElement.GetProperty("level_sha256").GetString(), "Read-only asset input changed during checks");
Directory.CreateDirectory(outputDirectory);
File.WriteAllBytes(output, candidate);
Require(File.ReadAllBytes(output).SequenceEqual(candidate), "Candidate file reread differs");
var receipt = new { source = Path.GetFullPath(args[0]), source_sha256 = ExpectedSource, output_sha256 = Hash(candidate), token = TargetToken.ToString("x8"), method = TargetName,
    original_il_offset = "0608", instruction_index = anchor, before = "ldloc.s V_9", after = "ldc.i4.4", changed_instructions = 1,
    metadata_identical_except_method_rvas = true, metadata_tokens_verified = tokens.Count, normalized_method_bodies_verified = bodies.Count, inverse_byte_exact = inverseByteExact,
    inverse_sha256 = Hash(restored), rejected_negative_cases = 5, assets = assetEvidence.RootElement.Clone(), runtime_verified = false, canonical_or_device_modified = false };
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }) + "\n");
Console.WriteLine(JsonSerializer.Serialize(receipt, new JsonSerializerOptions { WriteIndented = true }));
