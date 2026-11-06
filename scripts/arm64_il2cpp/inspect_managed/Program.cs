using System.Text.Json;
using dnlib.DotNet;

if (args.Length != 2) throw new ArgumentException("Expected input-path-list.json output.json");
var paths = JsonSerializer.Deserialize<string[]>(File.ReadAllText(args[0]))!;
var modules = new List<object>();
foreach (string path in paths)
{
    using var module = ModuleDefMD.Load(path);
    var dynamicCalls = new List<object>();
    var genericCalls = new SortedSet<string>(StringComparer.Ordinal);
    var nativeCalls = new List<object>();
    foreach (var type in module.GetTypes())
    foreach (var method in type.Methods)
    {
        if (method.ImplMap != null)
            nativeCalls.Add(new { method = method.FullName, token = method.MDToken.Raw, library = method.ImplMap.Module.Name.String, entry = method.ImplMap.Name.String });
        if (!method.HasBody) continue;
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not IMethod called) continue;
            string target = called.FullName;
            string owner = called.DeclaringType?.FullName ?? "";
            if (instruction.Operand is MethodSpec) genericCalls.Add(target);
            if (owner.StartsWith("System.Reflection.Emit.", StringComparison.Ordinal)
                || called.Name.String is "MakeGenericMethod" or "MakeGenericType"
                || (called.Name == "Compile" && owner.StartsWith("System.Linq.Expressions.", StringComparison.Ordinal)))
                dynamicCalls.Add(new { method = method.FullName, token = method.MDToken.Raw, ilOffset = instruction.Offset, target });
        }
    }
    modules.Add(new { path = Path.GetFullPath(path), name = module.Assembly.Name.String,
        fullName = module.Assembly.FullName, references = module.GetAssemblyRefs().Select(x => x.Name.String).Distinct().OrderBy(x => x).ToArray(),
        definedTypes = module.GetTypes().Count(), definedMethods = module.GetTypes().Sum(x => x.Methods.Count),
        typeFields = module.GetTypes().Select(type => new { type = type.FullName, baseType = type.BaseType?.FullName,
            isValueType = type.IsValueType, serializable = type.IsSerializable,
            fields = type.Fields.Where(field => !field.IsStatic && !field.IsNotSerialized).Select(field => new {
                name = field.Name.String, type = field.FieldType.FullName, token = field.MDToken.Raw,
                publicField = field.IsPublic, attributes = field.CustomAttributes.Select(attribute => attribute.TypeFullName).ToArray() }).ToArray() }).ToArray(),
        dynamicCalls, genericCalls, nativeCalls });
}
File.WriteAllText(args[1], JsonSerializer.Serialize(modules, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"METADATA_AUDIT_PASS {modules.Count} assemblies; no game code executed");
