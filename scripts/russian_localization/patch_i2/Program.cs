using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

if (args.Length != 7) throw new ArgumentException("source.dll sourceSHA256 helper.dll CoreModule.dll UI.dll output.dll workspaceRoot");
string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();
void Require(bool value, string message) { if (!value) throw new InvalidDataException(message); }
string output = Path.GetFullPath(args[5]);
string work = Path.GetFullPath(Path.Combine(args[6], "work")) + Path.DirectorySeparatorChar;
Require(output.StartsWith(work, StringComparison.OrdinalIgnoreCase), "Patched firstpass output must stay inside workspace work");
Require(Hash(args[0]) == args[1], "Current canonical firstpass preimage differs");
using var source = ModuleDefMD.Load(args[0]);
using var helper = ModuleDefMD.Load(args[2]);
using var core = ModuleDefMD.Load(args[3]);
using var ui = ModuleDefMD.Load(args[4]);
Require(helper.GetAssemblyRefs().All(a => a.Name != "Assembly-CSharp-firstpass"), "Russian helper must not reference I2/gameplay assembly");
var helperCalls = helper.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions)
    .Where(i => i.Operand is IMethod).Select(i => (IMethod)i.Operand).ToArray();
Require(!helperCalls.Any(m => m.DeclaringType.FullName.Contains("Dropdown/OptionData") || m.FullName.Contains("Dropdown::set_options") || m.FullName.Contains("Dropdown::get_options")), "Russian UI helper must never read or mutate semantic OptionData");
Require(!helper.GetTypes().SelectMany(t => t.Methods).Any(m => m.Name == "Update" || m.Name == "LateUpdate" || m.Name == "FixedUpdate"), "Russian UI helper must not add frame loops");
var scopedHook = helper.Find("ClosingShift.Localization.RussianLocaleDefaults", false).Methods.Single(m => m.Name == "AfterObjectLocalized");
Require(!scopedHook.Body.Instructions.Any(i => i.Operand is IMethod m && (m.Name == "Refresh" || m.Name == "SetRussianActive" || m.Name == "FindObjectsOfTypeAll")), "Individual I2 hook must remain scoped");
var originalTokens = source.GetTypes().SelectMany(t => new IMDTokenProvider[] { t }.Concat(t.Methods).Concat(t.Fields)).ToDictionary(x => x.MDToken.Raw, x => x.ToString());
string Body(MethodDef method)
{
    if (!method.HasBody) return "no-body";
    var body = method.Body;
    string Operand(object? value) => value switch {
        null => "", Instruction instruction => "i:" + body.Instructions.IndexOf(instruction),
        IList<Instruction> instructions => String.Join(",", instructions.Select(i => body.Instructions.IndexOf(i))),
        Local local => "local:" + local.Index, Parameter parameter => "arg:" + parameter.Index,
        IFullName full => full.FullName, string text => "s:" + text,
        _ => value.ToString() ?? "" };
    string serialized = String.Join("\n", body.Instructions.Select(i => i.OpCode.Code + " " + Operand(i.Operand))) + "\nLOCALS:" + String.Join(",", body.Variables.Select(l => l.Type.FullName)) +
        "\nEH:" + String.Join(";", body.ExceptionHandlers.Select(e => e.HandlerType + ":" + Operand(e.TryStart) + ":" + Operand(e.TryEnd) + ":" + Operand(e.HandlerStart) + ":" + Operand(e.HandlerEnd) + ":" + Operand(e.FilterStart) + ":" + e.CatchType?.FullName));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serialized))).ToLowerInvariant();
}
var originalBodies = source.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, Body);
var edits = new List<object>();
var changed = new HashSet<uint>();
MethodDef Method(uint token, string declaringType, string name)
{
    var method = source.ResolveToken(token) as MethodDef;
    Require(method != null && method.DeclaringType.FullName == declaringType && method.Name == name && method.HasBody, "I2 patch token/signature mismatch: " + token.ToString("x8"));
    Require(!method!.Body.Instructions.Any(i => i.Operand is IMethod m && m.DeclaringType.FullName == "ClosingShift.Localization.RussianLocaleDefaults"), "I2 helper hook already exists; use frozen output or a clean canonical preimage");
    changed.Add(token);
    return method;
}
IMethod Hook(string name) => source.Import(helper.Find("ClosingShift.Localization.RussianLocaleDefaults", false).Methods.Single(m => m.Name == name));
void InsertBeforePreservingTarget(MethodDef method, Instruction target, params Instruction[] prefix)
{
    Require(prefix.Length > 0, "Empty hook");
    var original = new Instruction(target.OpCode, target.Operand);
    target.OpCode = prefix[0].OpCode; target.Operand = prefix[0].Operand;
    int index = method.Body.Instructions.IndexOf(target) + 1;
    foreach (var instruction in prefix.Skip(1).Append(original)) method.Body.Instructions.Insert(index++, instruction);
}
void InsertAfter(MethodDef method, Instruction target, params Instruction[] additions)
{
    int index = method.Body.Instructions.IndexOf(target) + 1;
    foreach (var instruction in additions) method.Body.Instructions.Insert(index++, instruction);
}
var startup = Method(0x0600251c, "I2.Loc.LocalizationManager", "SelectStartupLanguage");
var hasLanguage = (MethodDef)source.ResolveToken(0x0600251d);
Require(hasLanguage.FullName == "System.Boolean I2.Loc.LocalizationManager::HasLanguage(System.String,System.Boolean,System.Boolean,System.Boolean)", "I2 HasLanguage signature differs");
var prefLiteral = startup.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldstr && (string)i.Operand == "I2 Language");
InsertBeforePreservingTarget(startup, prefLiteral, Instruction.Create(OpCodes.Ldstr, "Русский (AI)"), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Ldc_I4_0), Instruction.Create(OpCodes.Ldc_I4_1), Instruction.Create(OpCodes.Call, hasLanguage), Instruction.Create(OpCodes.Call, Hook("Prepare")));
edits.Add(new { token = startup.MDToken.ToString(), purpose = "One-time OS-Russian migration after sources are ready, before saved language read; HasLanguage Initialize=false" });

var dropdown = Method(0x06002645, "I2.Loc.SetLanguageDropdown", "OnEnable");
var valueSetter = dropdown.Body.Instructions.Single(i => i.Operand is IMethod m && m.FullName == "System.Void UnityEngine.UI.Dropdown::set_value(System.Int32)");
var silentSetter = ui.Find("UnityEngine.UI.Dropdown", false).Methods.Single(m => m.Name == "SetValueWithoutNotify" && m.MethodSig.Params.Count == 1);
Require(silentSetter.FullName == "System.Void UnityEngine.UI.Dropdown::SetValueWithoutNotify(System.Int32)", "Unity dropdown silent setter differs");
valueSetter.Operand = source.Import(silentSetter);
edits.Add(new { token = dropdown.MDToken.ToString(), purpose = "Initialize dropdown without emitting a manual-choice event" });

var selection = Method(0x06002646, "I2.Loc.SetLanguageDropdown", "OnValueChanged");
var languageSetter = selection.Body.Instructions.Single(i => i.Operand is IMethod m && m.FullName == "System.Void I2.Loc.LocalizationManager::set_CurrentLanguage(System.String)");
Require(selection.Body.Instructions.Skip(selection.Body.Instructions.IndexOf(languageSetter) + 1).All(i => i.OpCode == OpCodes.Ret || i.OpCode == OpCodes.Nop), "Unexpected code after real language selection");
InsertAfter(selection, languageSetter, Instruction.Create(OpCodes.Call, Hook("MarkManualChoice")));
edits.Add(new { token = selection.MDToken.ToString(), purpose = "Persist explicit-choice marker after I2 saves real selected language" });

var all = Method(0x0600254a, "I2.Loc.LocalizationManager", "DoLocalizeAll");
Require(all.Body.Instructions.Count(i => i.OpCode == OpCodes.Ret) == 1, "Unexpected global-localize return structure");
var codeField = all.DeclaringType.Fields.Single(f => f.Name == "mLanguageCode" && f.IsStatic);
InsertBeforePreservingTarget(all, all.Body.Instructions.Last(), Instruction.Create(OpCodes.Ldsfld, codeField), Instruction.Create(OpCodes.Call, Hook("AfterLocalize")));
edits.Add(new { token = all.MDToken.ToString(), purpose = "One font sweep after localization has completed" });

var individual = Method(0x060024f1, "I2.Loc.Localize", "OnLocalize");
var assignment = individual.Body.Instructions.Single(i => i.OpCode == OpCodes.Stfld && i.Operand is IField f && f.Name == "mTranslation");
Require(individual.Body.Instructions.Skip(individual.Body.Instructions.IndexOf(assignment) + 1).All(i => i.OpCode == OpCodes.Ret || i.OpCode == OpCodes.Nop), "Actual Localize translation assignment is no longer the final successful path");
var gameObjectGetter = core.Find("UnityEngine.Component", false).Methods.Single(m => m.Name == "get_gameObject");
InsertAfter(individual, assignment, Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, source.Import(gameObjectGetter)), Instruction.Create(OpCodes.Call, Hook("AfterObjectLocalized")));
edits.Add(new { token = individual.MDToken.ToString(), purpose = "Scoped same-GameObject font repair only after actual text assignment, no global scan" });
foreach (uint token in changed) { var m = (MethodDef)source.ResolveToken(token); m.Body.SimplifyBranches(); m.Body.OptimizeBranches(); }
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var options = new ModuleWriterOptions(source);
options.MetadataOptions.Flags = MetadataFlags.PreserveAll;
source.Write(output, options);
using var check = ModuleDefMD.Load(output);
Require(check.GetTypes().Sum(t => 1 + t.Methods.Count + t.Fields.Count) == originalTokens.Count, "Original type/field/method definition count changed");
foreach (var token in originalTokens) Require(check.ResolveToken(token.Key)?.ToString() == token.Value, "Original type/method/field metadata token changed: " + token.Key.ToString("x8"));
foreach (var pair in originalBodies.Where(p => !changed.Contains(p.Key))) Require(Body((MethodDef)check.ResolveToken(pair.Key)) == pair.Value, "Unrelated original method body changed: " + pair.Key.ToString("x8"));
foreach (uint token in changed) Require(Body((MethodDef)source.ResolveToken(token)) == Body((MethodDef)check.ResolveToken(token)), "Written I2 patch differs from intended IL");
Require(Hash(args[0]) == args[1], "Canonical input changed during patching");
File.WriteAllText(output + ".manifest.json", JsonSerializer.Serialize(new { source_sha256 = args[1], output_sha256 = Hash(output), helper_sha256 = Hash(args[2]), original_metadata_tokens_verified = originalTokens.Count,
    unrelated_method_bodies_verified = originalBodies.Count - changed.Count, no_original_types_fields_methods_added = true, helper_has_no_firstpass_reference = true,
    helper_never_reads_or_writes_semantic_dropdown_options = true, no_added_frame_loops = true, individual_i2_hook_has_no_global_refresh = true,
    patches = edits, runtime_verified = false }, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("RUSSIAN_I2_MANAGED_PATCH_PASS " + Hash(output));
