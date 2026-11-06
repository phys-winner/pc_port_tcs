using dnlib.DotNet;
using dnlib.DotNet.Emit;

// Merge this class into the existing patcher; it adds no fields/types/methods to the game.
public static class StartupPatches
{
    public static void Apply(ModuleDef game, Action<MethodDef, string> mark, bool inactiveTemplateGuards = false)
    {
        if (game.Assembly.Name != "Assembly-CSharp") throw new InvalidOperationException("Wrong assembly identity");
        MethodDef Method(uint token, string signature)
        {
            var method = game.ResolveToken(token) as MethodDef;
            if (method == null || method.FullName != signature || !method.HasBody)
                throw new InvalidOperationException("Startup preimage mismatch: " + signature);
            return method;
        }
        ITypeDefOrRef Type(string name) => game.GetTypeRefs().First(t => t.FullName == name);
        IMethod Member(string owner, string name, MethodSig sig) => new MemberRefUser(game, name, sig, Type(owner));
        var objectBool = Member("UnityEngine.Object", "op_Implicit", MethodSig.CreateStatic(game.CorLibTypes.Boolean, new ClassSig(Type("UnityEngine.Object"))));
        var enabled = Member("UnityEngine.Behaviour", "get_enabled", MethodSig.CreateInstance(game.CorLibTypes.Boolean));
        var active = Member("UnityEngine.GameObject", "get_activeInHierarchy", MethodSig.CreateInstance(game.CorLibTypes.Boolean));
        static void Insert(MethodDef method, Instruction at, IEnumerable<Instruction> prefix, bool redirectIncoming = false)
        {
            var added = prefix.ToArray();
            if (redirectIncoming)
                foreach (var ins in method.Body.Instructions)
                    if (ReferenceEquals(ins.Operand, at)) ins.Operand = added[0];
            int index = method.Body.Instructions.IndexOf(at);
            foreach (var ins in added) method.Body.Instructions.Insert(index++, ins);
            method.Body.InitLocals = true;
        }

        foreach (var specification in new[] {
            (0x0600037Au, "System.Void UICustomOptions::Awake()"),
            (0x06000385u, "System.Void UICustomOptions::OnDestroy()") })
        {
            var options = Method(specification.Item1, specification.Item2);
            var optionsEntry = options.Body.Instructions[0];
            Insert(options, optionsEntry, new[] {
                Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, enabled),
                Instruction.Create(OpCodes.Brtrue, optionsEntry), Instruction.Create(OpCodes.Ret) });
            mark(options, "respect explicitly disabled legacy menu options component lifecycle");
        }

        var cameraMethod = Method(0x0600100F, "UnityEngine.Camera ThunderWire.Utility.Tools::MainCamera()");
        cameraMethod.Body = new CilBody();
        cameraMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Call, Member("UnityEngine.Camera", "get_main", MethodSig.CreateStatic(new ClassSig(Type("UnityEngine.Camera"))))));
        cameraMethod.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
        mark(cameraMethod, "use Unity's enabled main-camera selection instead of requiring a unique tag");

        foreach (var specification in new[] {
            (0x060009B1u, "System.Void PatronCardLoaderMainMenu::Start()"),
            (0x060009B2u, "System.Void PatronCardLoaderMainMenu::PatronNameButtonPressed(UnityEngine.UI.Button)") })
        {
            var method = Method(specification.Item1, specification.Item2);
            method.Body.SimplifyMacros(method.Parameters);
            var instructions = method.Body.Instructions;
            int cards = instructions.Select((ins, i) => (ins, i)).First(x => x.ins.Operand is IField f && f.Name == "PatronCards").i;
            if (instructions[cards - 1].OpCode != OpCodes.Ldarg || instructions[cards + 1].OpCode != OpCodes.Ldloc ||
                instructions[cards + 2].OpCode != OpCodes.Callvirt)
                throw new InvalidOperationException("Patron card load preimage changed");
            var field = (IField)instructions[cards].Operand;
            var local = (Local)instructions[cards + 1].Operand;
            var getter = (IMethod)instructions[cards + 2].Operand;
            Instruction increment = instructions.Skip(cards + 3).Select((ins, i) => (ins, i: i + cards + 3))
                .Where(x => x.ins.OpCode == OpCodes.Ldloc && ReferenceEquals(x.ins.Operand, local))
                .Where(x => x.i + 3 < instructions.Count && instructions[x.i + 1].IsLdcI4() && instructions[x.i + 1].GetLdcI4Value() == 1 &&
                            instructions[x.i + 2].OpCode == OpCodes.Add && instructions[x.i + 3].OpCode == OpCodes.Stloc &&
                            ReferenceEquals(instructions[x.i + 3].Operand, local)).Select(x => x.ins).First();
            Instruction entry = instructions[cards - 1];
            if (specification.Item1 == 0x060009B2)
            {
                int equality = instructions.Select((ins, i) => (ins, i)).First(x => x.ins.Operand is IMethod m && m.Name == "op_Equality").i;
                if (instructions[equality + 1].OpCode != OpCodes.Brfalse)
                    throw new InvalidOperationException("Patron button match preimage changed");
                entry = instructions[equality + 2];
            }
            Insert(method, entry, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, field),
                Instruction.Create(OpCodes.Ldloc, local), Instruction.Create(OpCodes.Callvirt, getter),
                Instruction.Create(OpCodes.Call, objectBool), Instruction.Create(OpCodes.Brfalse, increment) }, true);
            mark(method, "skip serialized null card slots, retain question-mark buttons and real card behavior");
        }

        // These optional guards are limited to a real, referenced but inactive Player.
        // Missing gameplay references continue down the original path and remain visible errors.
        if (!inactiveTemplateGuards) return;
        var managerType = game.GetTypes().Single(t => t.FullName == "HFPS_GameManager");
        var playerField = managerType.Fields.Single(f => f.Name == "Player");
        var cutsceneStart = Method(0x06000962, "System.Void CutsceneManager::Start()");
        var managerGetter = (IMethod)cutsceneStart.Body.Instructions.Single(i => i.Operand is IMethod m &&
            m.Name == "get_Instance" && m.DeclaringType.FullName.Contains("HFPS_GameManager")).Operand;
        void InactivePlayerReturn(MethodDef method, Instruction entry, IEnumerable<Instruction> managerLoad)
        {
            var manager = new Local(new ClassSig(managerType));
            var player = new Local(playerField.FieldSig.Type);
            method.Body.Variables.Add(manager); method.Body.Variables.Add(player);
            var prefix = managerLoad.Concat(new[] {
                Instruction.Create(OpCodes.Stloc, manager), Instruction.Create(OpCodes.Ldloc, manager), Instruction.Create(OpCodes.Call, objectBool),
                Instruction.Create(OpCodes.Brfalse, entry), Instruction.Create(OpCodes.Ldloc, manager), Instruction.Create(OpCodes.Ldfld, playerField),
                Instruction.Create(OpCodes.Stloc, player), Instruction.Create(OpCodes.Ldloc, player), Instruction.Create(OpCodes.Call, objectBool),
                Instruction.Create(OpCodes.Brfalse, entry), Instruction.Create(OpCodes.Ldloc, player), Instruction.Create(OpCodes.Callvirt, active),
                Instruction.Create(OpCodes.Brtrue, entry), Instruction.Create(OpCodes.Ret) });
            Insert(method, entry, prefix);
        }
        var managerStart = Method(0x0600041E, "System.Void HFPS_GameManager::Start()");
        int setup = managerStart.Body.Instructions.Select((ins, i) => (ins, i)).Single(x => x.ins.Operand is IMethod m && m.Name == "SetupUI").i;
        InactivePlayerReturn(managerStart, managerStart.Body.Instructions[setup + 1], new[] { Instruction.Create(OpCodes.Ldarg_0) });
        mark(managerStart, "retain SetupUI, defer player-control initialization for inactive menu template Player");
        InactivePlayerReturn(cutsceneStart, cutsceneStart.Body.Instructions[0], new[] { Instruction.Create(OpCodes.Call, managerGetter) });
        mark(cutsceneStart, "defer cutscene camera initialization while referenced Player is inactive");
        var cutsceneUpdate = Method(0x06000963, "System.Void CutsceneManager::Update()");
        var cutsceneEntry = cutsceneUpdate.Body.Instructions[0];
        var managerField = game.GetTypes().Single(t => t.FullName == "CutsceneManager").Fields.Single(f => f.Name == "gameManager");
        Insert(cutsceneUpdate, cutsceneEntry, new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, managerField),
            Instruction.Create(OpCodes.Call, objectBool), Instruction.Create(OpCodes.Brtrue, cutsceneEntry),
            Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Call, cutsceneStart) });
        InactivePlayerReturn(cutsceneUpdate, cutsceneUpdate.Body.Instructions[0], new[] { Instruction.Create(OpCodes.Call, managerGetter) });
        mark(cutsceneUpdate, "defer inactive-player cutscene update; lazily initialize after Player becomes active");
        var saveStart = Method(0x0600046C, "System.Void SaveGameHandler::Start()");
        var saveEntry = saveStart.Body.Instructions[0];
        var settingsField = game.GetTypes().Single(t => t.FullName == "SaveGameHandler").Fields.Single(f => f.Name == "SaveLoadSettings");
        InactivePlayerReturn(saveStart, saveEntry, new[] { Instruction.Create(OpCodes.Call, managerGetter) });
        Insert(saveStart, saveStart.Body.Instructions[0], new[] { Instruction.Create(OpCodes.Ldarg_0), Instruction.Create(OpCodes.Ldfld, settingsField),
            Instruction.Create(OpCodes.Call, objectBool), Instruction.Create(OpCodes.Brtrue, saveEntry) });
        mark(saveStart, "skip unconfigured legacy save startup only when settings absent and referenced Player inactive");
    }
}
