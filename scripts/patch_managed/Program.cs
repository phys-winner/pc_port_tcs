using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using BackOwner=ClosingShift.Android.AndroidInputPolicy.BackOwner;
using Parameter=dnlib.DotNet.Parameter;

if (args.Length == 3 && args[0] == "--interact-messages20")
{
    string input=Path.GetFullPath(args[1]),folder=Path.GetFullPath(args[2]);
    const string accepted="9473F31C4A74D408EC8788E5D6D7099842BAE3C0CFBC4A101F5F02763E2891A1";
    Require(Hash(input)==accepted,"Accepted Game19 preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only interact20 patch directory");
    using var messagesGame=ModuleDefMD.Load(input);
    var localize=messagesGame.ResolveToken(0x06000A09) as MethodDef;
    Require(localize?.FullName=="System.Void InteractMessage::LocalizationUpdate()" && BodyHash(localize)=="0AF39EF0D9E97DF94605F9AFBE5C8105952FDA92FB395A4A93EEDCE5A0142534","InteractMessage.LocalizationUpdate body preimage changed");
    Require(localize!.Body.ExceptionHandlers.Count==0,"Unexpected message localization cleanup regions");
    var callers=messagesGame.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody && m.Body.Instructions.Any(i=>i.Operand is IMethod c && c.FullName==localize.FullName)).Select(m=>m.MDToken.Raw).ToArray();
    Require(callers.SequenceEqual(new uint[]{0x06000A08}),"Message localization callers changed");
    var before=messagesGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    Require(before.Count==6083,"Game19 method count changed");
    var messages=localize.DeclaringType.Fields.Single(f=>f.FullName=="System.Collections.Generic.List`1<InteractMessage_Set> InteractMessage::Messages");
    var messageIndex=localize.DeclaringType.Fields.Single(f=>f.FullName=="System.Int32 InteractMessage::messageIndex");
    var messagesInit=(MethodDef)messagesGame.ResolveToken(0x06000A08);
    Require(messagesInit.FullName=="System.Void InteractMessage::InitLocalization()","Message localization caller identity changed");
    var count=messagesInit.Body.Instructions.Select(i=>i.Operand).OfType<IMethod>().First(m=>m.FullName=="System.Int32 System.Collections.Generic.List`1<InteractMessage_Set>::get_Count()");
    var il=localize.Body.Instructions;
    int advance=Enumerable.Range(0,il.Count-5).Single(i=>il[i].OpCode==OpCodes.Ldarg_0 && il[i+1].OpCode==OpCodes.Ldarg_0 && il[i+2].OpCode==OpCodes.Ldfld && il[i+2].Operand is IField read && read.FullName==messageIndex.FullName && il[i+3].OpCode==OpCodes.Ldc_I4_1 && il[i+4].OpCode==OpCodes.Add && il[i+5].OpCode==OpCodes.Stfld && il[i+5].Operand is IField write && write.FullName==messageIndex.FullName)+6;
    Require(advance==30 && il.Count==105,"Message index advance layout changed");
    var first=il[0];var continuation=il[advance];
    var boundary=new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,messageIndex),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,messages),Instruction.Create(OpCodes.Callvirt,count),Instruction.Create(OpCodes.Blt,continuation),Instruction.Create(OpCodes.Ret)};
    var entry=new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,messages),Instruction.Create(OpCodes.Callvirt,count),Instruction.Create(OpCodes.Brtrue,first),Instruction.Create(OpCodes.Ret)};
    for(int i=0;i<boundary.Length;++i)il.Insert(advance+i,boundary[i]);
    for(int i=0;i<entry.Length;++i)il.Insert(i,entry[i]);
    string patchedBody=BodyHash(localize);
    Directory.CreateDirectory(folder);string messagesDestination=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(messagesGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;messagesGame.Write(messagesDestination,writer);VerifyMetadata(input,messagesDestination);
    using var check=ModuleDefMD.Load(messagesDestination);
    var saved=(MethodDef)check.ResolveToken(localize.MDToken.Raw);
    Require(BodyHash(saved)==patchedBody,"Message localization guards readback differs");
    for(int i=0;i<boundary.Length;++i)saved.Body.Instructions.RemoveAt(advance+entry.Length);
    for(int i=0;i<entry.Length;++i)saved.Body.Instructions.RemoveAt(0);
    foreach(var item in before)Require(BodyHash((MethodDef)check.ResolveToken(item.Key))==item.Value,"Unrelated interact20 instruction changed: "+item.Key.ToString("X8"));
    Require(Hash(input)==accepted,"Game19 input mutated");
    File.WriteAllText(Path.Combine(folder,"interact20.json"),JsonSerializer.Serialize(new{inputSHA256=accepted,outputSHA256=Hash(messagesDestination),methodToken="06000A09",originalBodySHA256=before[localize.MDToken.Raw],patchedBodySHA256=patchedBody,changedMethods=1,entryGuardInstructions=entry.Length,messageBoundaryInstructions=boundary.Length,allMethodBodiesReversedToOriginal=before.Count,metadataOwnersPreserved=true,helperUnchanged=true,localizationPayloadUnchanged=true},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("INTERACT_MESSAGES20_PATCH_PASS "+Hash(messagesDestination)+" reversed-methods="+before.Count);return;
}

if (args.Length == 4 && args[0] == "--snow19")
{
    string input=Path.GetFullPath(args[1]),helper=Path.GetFullPath(args[2]),folder=Path.GetFullPath(args[3]);
    const string accepted="BBC043B9974E4C08879073F8A1613DB7F6309C4002F1EE7F0FE9F9D8E2AA20B6";
    Require(Hash(input)==accepted,"Accepted reading19 Game preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only snow19 patch directory");
    using var snowGame=ModuleDefMD.Load(input);using var snowRuntime=ModuleDefMD.Load(helper);
    Require(snowRuntime.Assembly.Name=="ClosingShift.Android","Wrong snow19 helper assembly");
    var snowMethod=snowGame.ResolveToken(0x06000766) as MethodDef;
    Require(snowMethod?.FullName=="System.Void SnowRenderer::RenderSnowForCamera(UnityEngine.Camera,System.Boolean)","Snow renderer method identity changed");
    string snowBody=BodyHash(snowMethod!);
    Require(snowBody=="532E203235868A175BAB0EEC2D8DBDD61599BE2C49F90FDDCD6EDC0099BD763B","Snow renderer body preimage changed: "+snowBody);
    Require(snowMethod!.Body.ExceptionHandlers.Count==2 && snowMethod.Body.ExceptionHandlers.All(e=>snowMethod.Body.Instructions.IndexOf(e.TryStart)>0 && snowMethod.Body.Instructions.IndexOf(e.HandlerStart)>0),"Snow renderer entry must precede its original cleanup regions");
    var allowSnow=snowRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidSnowCompat").Methods.Single(m=>m.FullName=="System.Boolean ClosingShift.Android.AndroidSnowCompat::AllowSnow(UnityEngine.MonoBehaviour,UnityEngine.Camera)" && m.IsPublic && m.IsStatic);
    var before=snowGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    var snowFirst=snowMethod.Body.Instructions[0];
    snowMethod.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldarg_0));snowMethod.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Ldarg_1));
    snowMethod.Body.Instructions.Insert(2,Instruction.Create(OpCodes.Call,new Importer(snowGame).Import(allowSnow)));
    snowMethod.Body.Instructions.Insert(3,Instruction.Create(OpCodes.Brtrue,snowFirst));snowMethod.Body.Instructions.Insert(4,Instruction.Create(OpCodes.Ret));
    Directory.CreateDirectory(folder);string snowDestination=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(snowGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;snowGame.Write(snowDestination,writer);VerifyMetadata(input,snowDestination);
    using var check=ModuleDefMD.Load(snowDestination);
    var saved=(MethodDef)check.ResolveToken(snowMethod.MDToken.Raw);var instructions=saved.Body.Instructions;
    Require(instructions[0].OpCode==OpCodes.Ldarg_0 && instructions[1].OpCode==OpCodes.Ldarg_1 && instructions[2].OpCode==OpCodes.Call && instructions[2].Operand is IMethod call && call.FullName==allowSnow.FullName && instructions[3].OpCode==OpCodes.Brtrue && ReferenceEquals(instructions[3].Operand,instructions[5]) && instructions[4].OpCode==OpCodes.Ret,"Snow compatibility prefix readback differs");
    for(int i=0;i<5;++i)instructions.RemoveAt(0);
    foreach(var entry in before)Require(BodyHash((MethodDef)check.ResolveToken(entry.Key))==entry.Value,"Unrelated snow19 instruction or cleanup region changed: "+entry.Key.ToString("X8"));
    Require(Hash(input)==accepted,"Reading19 Game input mutated");
    File.WriteAllText(Path.Combine(folder,"snow19.json"),JsonSerializer.Serialize(new{inputSHA256=accepted,outputSHA256=Hash(snowDestination),runtimeSHA256=Hash(helper),changedMethods=1,prefixInstructions=5,allMethodBodiesReversedToOriginal=before.Count,originalFinallyRegionsPreserved=2,metadataOwnersPreserved=true},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("SNOW19_PATCH_PASS "+Hash(snowDestination)+" reversed-methods="+before.Count);return;
}

if (args.Length == 3 && args[0] == "--reading-pages19")
{
    string input=Path.GetFullPath(args[1]),folder=Path.GetFullPath(args[2]);
    const string accepted="0E8A7698701B7BE954ED54935694E1D1B35185ACB893CD205DEFB687F89E1F59";
    Require(Hash(input)==accepted,"Accepted Game18 preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only reading19 patch directory");
    using var readingGame=ModuleDefMD.Load(input);
    var localize=readingGame.ResolveToken(0x06000A31) as MethodDef;
    Require(localize?.FullName=="System.Void ReadingMaterials::Localize()" && BodyHash(localize)=="8F901D3CF34B70863F42323694E3135209758203519885BC5278F40AC1140236","ReadingMaterials.Localize body preimage changed");
    var before=readingGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    var contents=localize!.DeclaringType.Fields.Single(f=>f.FullName=="System.Collections.Generic.List`1<ReadingMatContent> ReadingMaterials::contents");
    var read=readingGame.ResolveToken(0x06000A33) as MethodDef;
    Require(read?.FullName=="System.Void ReadingMaterials::Read()","ReadingMaterials.Read identity changed");
    var count=read!.Body.Instructions.Select(i=>i.Operand).OfType<IMethod>().First(m=>m.FullName=="System.Int32 System.Collections.Generic.List`1<ReadingMatContent>::get_Count()");
    var il=localize.Body.Instructions;
    int index=Enumerable.Range(0,il.Count-3).Single(i=>il[i].OpCode==OpCodes.Ldloc_2 && il[i+1].OpCode==OpCodes.Ldlen && il[i+2].OpCode==OpCodes.Conv_I4 && il[i+3].OpCode==OpCodes.Blt_S);
    var original=il.Skip(index).Take(3).Select(i=>(i.OpCode,i.Operand)).ToArray();
    il[index].OpCode=OpCodes.Ldarg_0;il[index].Operand=null;
    il[index+1].OpCode=OpCodes.Ldfld;il[index+1].Operand=contents;
    il[index+2].OpCode=OpCodes.Callvirt;il[index+2].Operand=new Importer(readingGame).Import(count);
    Directory.CreateDirectory(folder);string readingDestination=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(readingGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;readingGame.Write(readingDestination,writer);VerifyMetadata(input,readingDestination);
    using var check=ModuleDefMD.Load(readingDestination);
    var saved=((MethodDef)check.ResolveToken(localize.MDToken.Raw)).Body.Instructions;
    Require(saved[index].OpCode==OpCodes.Ldarg_0 && saved[index+1].OpCode==OpCodes.Ldfld && saved[index+1].Operand is IField field && field.FullName==contents.FullName && saved[index+2].OpCode==OpCodes.Callvirt && saved[index+2].Operand is IMethod getCount && getCount.FullName==count.FullName,"Reading page-count bound readback differs");
    for(int i=0;i<3;++i){saved[index+i].OpCode=original[i].OpCode;saved[index+i].Operand=original[i].Operand;}
    foreach(var entry in before)Require(BodyHash((MethodDef)check.ResolveToken(entry.Key))==entry.Value,"Unrelated reading19 instruction changed: "+entry.Key.ToString("X8"));
    Require(Hash(input)==accepted,"Game18 input mutated");
    File.WriteAllText(Path.Combine(folder,"reading19.json"),JsonSerializer.Serialize(new{inputSHA256=accepted,outputSHA256=Hash(readingDestination),changedMethods=1,conditionInstructions=3,allMethodBodiesReversedToOriginal=before.Count,metadataOwnersPreserved=true,localizedHeaderAndPagePayloadPreserved=true,saveAndReaderEventsUnchanged=true},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("READING_PAGES19_PATCH_PASS "+Hash(readingDestination)+" reversed-methods="+before.Count);return;
}

if (args.Length == 4 && args[0] == "--dialogue-press18")
{
    string input=Path.GetFullPath(args[1]),helper=Path.GetFullPath(args[2]),folder=Path.GetFullPath(args[3]);
    const string accepted="921EFC77914CF83F6717E93B388A7FF2884615E4D8BE4CEBCE817DCBBFAB9DFE";
    Require(Hash(input)==accepted,"Accepted mobile52 Game17 preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only dialogue18 patch directory");
    using var dialogueGame=ModuleDefMD.Load(input);using var dialogueRuntime=ModuleDefMD.Load(helper);
    Require(dialogueRuntime.Assembly.Name=="ClosingShift.Android","Wrong dialogue18 helper assembly");
    var factory=dialogueGame.ResolveToken(0x06000C90) as MethodDef;
    var skip=dialogueGame.ResolveToken(0x06000C95) as MethodDef;
    Require(factory?.FullName=="System.Collections.IEnumerator DialogueManager::TypeSentence(System.String)" && BodyHash(factory)=="D4A36DF5D5D13E6151076B11E1C862F13181BEC352DAD58C98FA9B4131EADC3D","TypeSentence factory preimage changed");
    Require(skip?.FullName=="System.Void DialogueManager::SkipToDisplayAll(System.String)" && BodyHash(skip)=="634A47FF5E8450F836825D7495D61894FBE94C25A34C0427E5D1F49EA040CDF1","SkipToDisplayAll body preimage changed");
    var dialogueAdapter=dialogueRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidInput");
    var begin=dialogueAdapter.Methods.Single(m=>m.FullName=="System.Void ClosingShift.Android.AndroidInput::BeginDialogueSentence(UnityEngine.MonoBehaviour)" && m.IsPublic && m.IsStatic);
    var canSkip=dialogueAdapter.Methods.Single(m=>m.FullName=="System.Boolean ClosingShift.Android.AndroidInput::CanSkipDialogueSentence(UnityEngine.MonoBehaviour)" && m.IsPublic && m.IsStatic);
    var before=dialogueGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    factory!.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldarg_0));factory.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Call,new Importer(dialogueGame).Import(begin)));
    var authoredFirst=skip!.Body.Instructions[0];
    skip.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldarg_0));skip.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Call,new Importer(dialogueGame).Import(canSkip)));
    skip.Body.Instructions.Insert(2,Instruction.Create(OpCodes.Brtrue,authoredFirst));skip.Body.Instructions.Insert(3,Instruction.Create(OpCodes.Ret));
    Directory.CreateDirectory(folder);string dialogueDestination=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(dialogueGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;dialogueGame.Write(dialogueDestination,writer);VerifyMetadata(input,dialogueDestination);
    using var check=ModuleDefMD.Load(dialogueDestination);
    var checkedFactory=(MethodDef)check.ResolveToken(factory.MDToken.Raw);var checkedSkip=(MethodDef)check.ResolveToken(skip.MDToken.Raw);
    Require(checkedFactory.Body.Instructions[0].OpCode==OpCodes.Ldarg_0 && checkedFactory.Body.Instructions[1].OpCode==OpCodes.Call && checkedFactory.Body.Instructions[1].Operand is IMethod beginCall && beginCall.FullName==begin.FullName,"Dialogue sentence marker readback differs");
    Require(checkedSkip.Body.Instructions[0].OpCode==OpCodes.Ldarg_0 && checkedSkip.Body.Instructions[1].OpCode==OpCodes.Call && checkedSkip.Body.Instructions[1].Operand is IMethod skipCall && skipCall.FullName==canSkip.FullName && checkedSkip.Body.Instructions[2].OpCode==OpCodes.Brtrue && ReferenceEquals(checkedSkip.Body.Instructions[2].Operand,checkedSkip.Body.Instructions[4]) && checkedSkip.Body.Instructions[3].OpCode==OpCodes.Ret,"Dialogue opening-press guard readback differs");
    for(int i=0;i<2;++i)checkedFactory.Body.Instructions.RemoveAt(0);
    for(int i=0;i<4;++i)checkedSkip.Body.Instructions.RemoveAt(0);
    foreach(var entry in before)Require(BodyHash((MethodDef)check.ResolveToken(entry.Key))==entry.Value,"Unrelated dialogue18 instruction changed: "+entry.Key.ToString("X8"));
    Require(Hash(input)==accepted,"Game17 input mutated");
    File.WriteAllText(Path.Combine(folder,"dialogue18.json"),JsonSerializer.Serialize(new{inputSHA256=accepted,outputSHA256=Hash(dialogueDestination),runtimeSHA256=Hash(helper),changedMethods=2,allMethodBodiesReversedToOriginal=before.Count,metadataOwnersPreserved=true,typingSpeedAndSentenceCoroutinePreserved=true},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("DIALOGUE_PRESS18_PATCH_PASS "+Hash(dialogueDestination)+" reversed-methods="+before.Count);return;
}

if (args.Length == 4 && args[0] == "--mobile52-input")
{
    string input=Path.GetFullPath(args[1]),helper=Path.GetFullPath(args[2]),folder=Path.GetFullPath(args[3]);
    const string accepted="BB1445AA0DFF7AA32E15918E0959243689F040BE9646AB8DF657B86B6132D0F3";
    Require(Hash(input)==accepted,"Accepted mobile51 Game preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only mobile52 input patch directory");
    using var mobileGame=ModuleDefMD.Load(input);using var mobileRuntime=ModuleDefMD.Load(helper);
    Require(mobileRuntime.Assembly.Name=="ClosingShift.Android","Wrong mobile52 input helper assembly");
    var method=mobileGame.ResolveToken(0x06000455) as MethodDef;
    Require(method?.FullName=="System.Void InteractManager::InteractTypes()" && BodyHash(method)=="E50F12A81614D4E0FE5C2051EFAB83947882EC8E2378267AEBF0081C936C284E","InteractTypes body preimage changed");
    var wrapper=mobileRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidInput").Methods.Single(m=>m.FullName=="System.Boolean ClosingShift.Android.AndroidInput::RaycastInteraction(UnityEngine.Ray,UnityEngine.RaycastHit&,System.Single,System.Int32)" && m.IsPublic && m.IsStatic);
    var before=mobileGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    var instruction=method!.Body.Instructions.Single(i=>i.Operand is IMethod call && call.FullName=="System.Boolean UnityEngine.Physics::Raycast(UnityEngine.Ray,UnityEngine.RaycastHit&,System.Single,System.Int32)");
    Require(instruction.OpCode==OpCodes.Call,"Unexpected interaction raycast opcode");
    int index=method.Body.Instructions.IndexOf(instruction);var original=(IMethod)instruction.Operand;
    instruction.Operand=new Importer(mobileGame).Import(wrapper);
    var trash=mobileGame.ResolveToken(0x06000B55) as MethodDef;
    Require(trash?.FullName=="System.Void TCS_TrashCan::TrashAllItemsInInventory()" && BodyHash(trash)=="CE3C7F6BD1FB80945D1AB22C558030E472D190C087BF676CC9E7787432D96F1E","End-shift inventory cleanup body preimage changed");
    var cleanup=mobileRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidInput").Methods.Single(m=>m.FullName=="System.Void ClosingShift.Android.AndroidInput::CleanupExpiredOrderStickers()" && m.IsPublic && m.IsStatic);
    var trashReturn=trash!.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ret);
    Require(ReferenceEquals(trashReturn,trash.Body.Instructions.Last()),"Unexpected end-shift cleanup return shape");
    int trashIndex=trash.Body.Instructions.IndexOf(trashReturn);trashReturn.OpCode=OpCodes.Call;trashReturn.Operand=new Importer(mobileGame).Import(cleanup);trash.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    Directory.CreateDirectory(folder);string mobileDestination=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(mobileGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;mobileGame.Write(mobileDestination,writer);VerifyMetadata(input,mobileDestination);
    using var check=ModuleDefMD.Load(mobileDestination);
    var saved=((MethodDef)check.ResolveToken(method.MDToken.Raw)).Body.Instructions[index];
    Require(saved.OpCode==OpCodes.Call && saved.Operand is IMethod called && called.FullName==wrapper.FullName,"Interaction wrapper readback differs");
    saved.Operand=new Importer(check).Import(original);
    var checkedTrash=(MethodDef)check.ResolveToken(trash.MDToken.Raw);
    Require(checkedTrash.Body.Instructions.Count==trashIndex+2 && checkedTrash.Body.Instructions[trashIndex].OpCode==OpCodes.Call && checkedTrash.Body.Instructions[trashIndex].Operand is IMethod cleanupCall && cleanupCall.FullName==cleanup.FullName && checkedTrash.Body.Instructions.Last().OpCode==OpCodes.Ret,"Expired-sticker cleanup hook readback differs");
    checkedTrash.Body.Instructions[trashIndex].OpCode=OpCodes.Ret;checkedTrash.Body.Instructions[trashIndex].Operand=null;checkedTrash.Body.Instructions.RemoveAt(trashIndex+1);
    foreach(var entry in before)Require(BodyHash((MethodDef)check.ResolveToken(entry.Key))==entry.Value,"Unrelated mobile52 input instruction changed: "+entry.Key.ToString("X8"));
    Require(Hash(input)==accepted,"Mobile51 canonical Game input mutated");
    File.WriteAllText(Path.Combine(folder,"mobile52-input.json"),JsonSerializer.Serialize(new{inputSHA256=accepted,outputSHA256=Hash(mobileDestination),runtimeSHA256=Hash(helper),changedCalls=2,allMethodBodiesReversedToOriginal=before.Count,metadataOwnersPreserved=true},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("MOBILE52_INPUT_PATCH_PASS "+Hash(mobileDestination)+" reversed-methods="+before.Count);return;
}

if (args.Length == 4 && args[0] == "--fps-dropdown")
{
    string input=Path.GetFullPath(args[1]),helper=Path.GetFullPath(args[2]),folder=Path.GetFullPath(args[3]);
    Require(Hash(input)=="6A1DE2EB42A267563F8D3DA8A4C2C21ECDF8624C07057A03FA76B93E48A706E7","Accepted storage8 Game preimage changed");
    string work=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((folder+Path.DirectorySeparatorChar).StartsWith(work,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(folder),"Use a new work-only FPS patch directory");
    using var fpsGame=ModuleDefMD.Load(input);using var fpsRuntime=ModuleDefMD.Load(helper);
    Require(fpsRuntime.Assembly.Name=="ClosingShift.Android","Wrong FPS helper assembly");
    var wrapper=fpsRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidGraphics").Methods.Single(m=>m.FullName=="System.Void ClosingShift.Android.AndroidGraphics::SetLegacyFpsSelection(UnityEngine.UI.Dropdown,System.Int32)" && m.IsPublic && m.IsStatic);
    var before=fpsGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    var fpsExpected=new HashSet<string>{"UpdateOptions","SetDefaultGraphics","SetGraphicSettings","DeserializeSettings"};
    var changes=new List<(uint Token,int Index,IMethod Original)>();var seen=new HashSet<string>();
    var imported=new Importer(fpsGame).Import(wrapper);
    foreach(var method in fpsGame.GetTypes().Single(t=>t.FullName=="UICustomOptions").Methods.Where(m=>m.HasBody))
    for(int i=0;i<method.Body.Instructions.Count;++i)
    {
        var instruction=method.Body.Instructions[i];
        if(instruction.OpCode!=OpCodes.Callvirt || instruction.Operand is not IMethod call || call.FullName!="System.Void UnityEngine.UI.Dropdown::set_value(System.Int32)")continue;
        var receiver=method.Body.Instructions.Skip(Math.Max(0,i-4)).Take(Math.Min(i,4)).Where(v=>v.OpCode==OpCodes.Ldfld).Select(v=>v.Operand).OfType<IField>().LastOrDefault(f=>f.FieldSig?.Type.FullName=="UnityEngine.UI.Dropdown");
        if(receiver?.FullName!="UnityEngine.UI.Dropdown UICustomOptions::VSyncDropdown")continue;
        Require(fpsExpected.Contains(method.Name.String) && seen.Add(method.Name.String),"Unexpected native VSync writer: "+method.FullName);
        changes.Add((method.MDToken.Raw,i,call));instruction.OpCode=OpCodes.Call;instruction.Operand=imported;
    }
    Require(changes.Count==4 && seen.SetEquals(fpsExpected),"Native VSync writer coverage differs");
    Directory.CreateDirectory(folder);string fpsOutput=Path.Combine(folder,"Assembly-CSharp.dll");
    var writer=new ModuleWriterOptions(fpsGame);writer.MetadataOptions.Flags|=MetadataFlags.PreserveAll;fpsGame.Write(fpsOutput,writer);VerifyMetadata(input,fpsOutput);
    using var check=ModuleDefMD.Load(fpsOutput);
    foreach(var change in changes)
    {
        var instruction=((MethodDef)check.ResolveToken(change.Token)).Body.Instructions[change.Index];
        Require(instruction.OpCode==OpCodes.Call && instruction.Operand is IMethod call && call.FullName==wrapper.FullName,"FPS wrapper readback differs");
        instruction.OpCode=OpCodes.Callvirt;instruction.Operand=new Importer(check).Import(change.Original);
    }
    foreach(var method in check.GetTypes().SelectMany(t=>t.Methods))Require(BodyHash(method)==before[method.MDToken.Raw],"Unrelated FPS patch instruction changed: "+method.FullName);
    Require(Hash(input)=="6A1DE2EB42A267563F8D3DA8A4C2C21ECDF8624C07057A03FA76B93E48A706E7","FPS preimage mutated");
    var receipt=new{inputSHA256=Hash(input),outputSHA256=Hash(fpsOutput),runtimeSHA256=Hash(helper),changedCalls=4,changedMethods=seen.OrderBy(v=>v),allMethodBodiesReversedToOriginal=before.Count,metadataOwnersPreserved=true};
    File.WriteAllText(Path.Combine(folder,"patch.json"),JsonSerializer.Serialize(receipt,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("FPS_DROPDOWN_PATCH_PASS "+JsonSerializer.Serialize(receipt));return;
}

if (args.Length == 4 && args[0] == "--save-root")
{
    string saveSource=Path.GetFullPath(args[1]),saveHelper=Path.GetFullPath(args[2]),saveOutput=Path.GetFullPath(args[3]);
    string saveWork=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((saveOutput+Path.DirectorySeparatorChar).StartsWith(saveWork,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(saveOutput),"Use a new work-only save patch directory");
    using var saveRuntime=ModuleDefMD.Load(saveHelper);
    Require(saveRuntime.Assembly.Name=="ClosingShift.Android","Wrong save helper assembly");
    var saveRoot=saveRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidSaveStorage").Methods.Single(m=>m.FullName=="System.String ClosingShift.Android.AndroidSaveStorage::get_Root()" && m.IsPublic && m.IsStatic);
    var saveExpected=new Dictionary<string,int> {
        ["System.String ThunderWire.Configuration.ConfigManager::GetFolderPath(ThunderWire.Configuration.FilePath)"]=2,
        ["System.String ThunderWire.Configuration.ConfigManager::GetFilepathRoot(ThunderWire.Configuration.FilePath)"]=2,
        ["System.String ThunderWire.JsonManager.JsonManager::GetFilePath(ThunderWire.JsonManager.FilePath)"]=3,
        ["System.String[] ES3::GetFiles()"]=1,
        ["System.Void ES3Internal.ES3IO::.cctor()"]=1,
        ["System.String I2.Loc.I2BasePersistentStorage::UpdateFilename(I2.Loc.PersistentStorage/eFileType,System.String)"]=1,
        ["System.String PixelCrushers.DiskSavedGameDataStorer::GetBasePath()"]=1
    };
    var saveSeen=new Dictionary<string,int>();var saveReceipts=new List<object>();
    Directory.CreateDirectory(saveOutput);
    foreach(var saveFile in new[] { ("Assembly-CSharp.dll","2DAD1888AF35400B5934D1E16BA82B4D405D24829E7AFBEC81DFAFF2B5FA7532"), ("Assembly-CSharp-firstpass.dll","E6FBF32D7004F31879BBB871F10D0D8C6C7B036307620F57004E4752DDF8FCF2") })
    {
        string saveInput=Path.Combine(saveSource,saveFile.Item1),saveDestination=Path.Combine(saveOutput,saveFile.Item1);
        Require(Hash(saveInput)==saveFile.Item2,"Accepted save input changed: "+saveFile.Item1);
        using var saveModule=ModuleDefMD.Load(saveInput);
        var saveBodies=saveModule.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
        var saveChanges=new List<(uint Token,int Index,IMethod Original)>();
        var saveImport=new Importer(saveModule).Import(saveRoot);
        foreach(var saveMethod in saveModule.GetTypes().SelectMany(t=>t.Methods).Where(m=>m.HasBody))
        for(int saveIndex=0;saveIndex<saveMethod.Body.Instructions.Count;++saveIndex)
        {
            var saveInstruction=saveMethod.Body.Instructions[saveIndex];
            if(saveInstruction.OpCode!=OpCodes.Call || saveInstruction.Operand is not IMethod saveCall || saveCall.FullName!="System.String UnityEngine.Application::get_persistentDataPath()")continue;
            Require(saveExpected.ContainsKey(saveMethod.FullName),"Unreviewed save path consumer: "+saveMethod.FullName);
            saveSeen[saveMethod.FullName]=saveSeen.GetValueOrDefault(saveMethod.FullName)+1;
            saveChanges.Add((saveMethod.MDToken.Raw,saveIndex,saveCall));saveInstruction.Operand=saveImport;
        }
        var saveWriter=new ModuleWriterOptions(saveModule);saveWriter.MetadataOptions.Flags|=MetadataFlags.PreserveAll;
        saveModule.Write(saveDestination,saveWriter);VerifyMetadata(saveInput,saveDestination);
        using var saveCheck=ModuleDefMD.Load(saveDestination);
        foreach(var saveChange in saveChanges)
        {
            var saveInstruction=((MethodDef)saveCheck.ResolveToken(saveChange.Token)).Body.Instructions[saveChange.Index];
            Require(saveInstruction.OpCode==OpCodes.Call && saveInstruction.Operand is IMethod saveCall && saveCall.FullName==saveRoot.FullName,"Save path call readback differs");
            saveInstruction.Operand=new Importer(saveCheck).Import(saveChange.Original);
        }
        foreach(var saveMethod in saveCheck.GetTypes().SelectMany(t=>t.Methods))Require(BodyHash(saveMethod)==saveBodies[saveMethod.MDToken.Raw],"Unrelated save patch instruction changed: "+saveMethod.FullName);
        Require(Hash(saveInput)==saveFile.Item2,"Save patch input mutated");
        saveReceipts.Add(new{file=saveFile.Item1,inputSHA256=saveFile.Item2,outputSHA256=Hash(saveDestination),changedCalls=saveChanges.Count,allMethodBodiesReversedToOriginal=saveBodies.Count,metadataOwnersPreserved=true});
    }
    Require(saveExpected.Count==saveSeen.Count && saveExpected.All(pair=>saveSeen.GetValueOrDefault(pair.Key)==pair.Value),"Save path coverage differs: "+JsonSerializer.Serialize(saveSeen));
    File.WriteAllText(Path.Combine(saveOutput,"save-root-patch.json"),JsonSerializer.Serialize(new{files=saveReceipts,consumers=saveSeen,runtimeSHA256=Hash(saveHelper),automaticLegacyMigration=false},new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("SAVE_ROOT_PATCH_PASS calls="+saveSeen.Values.Sum()+" methods="+saveSeen.Count+" all other instructions and metadata owners preserved");
    return;
}

if (args.Length == 4 && args[0] == "--supporter-null")
{
    string supporterInput=Path.GetFullPath(args[1]),supporterHelper=Path.GetFullPath(args[2]),supporterFolder=Path.GetFullPath(args[3]);
    Require(Hash(supporterInput)=="26FEF9C610C46DB4CB396AE3FB87C687EF2DC8AE7191AD2294EB20E14A059039","Accepted canonical Game preimage changed");
    string supporterWork=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../work"))+Path.DirectorySeparatorChar;
    Require((supporterFolder+Path.DirectorySeparatorChar).StartsWith(supporterWork,StringComparison.OrdinalIgnoreCase) && !Directory.Exists(supporterFolder),"Use a new work-only supporter patch directory");
    using var supporterGame=ModuleDefMD.Load(supporterInput);
    using var supporterRuntime=ModuleDefMD.Load(supporterHelper);
    Require(supporterRuntime.Assembly.Name=="ClosingShift.Android","Wrong supporter helper assembly");
    var supporterMethod=supporterGame.GetTypes().Single(t=>t.FullName=="SupporterContents").Methods.Single(m=>m.Name=="Update" && m.MethodSig.Params.Count==0);
    var supporterBodies=supporterGame.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
    var supporterWrapper=supporterRuntime.GetTypes().Single(t=>t.FullName=="ClosingShift.Android.AndroidGraphics").Methods.Single(m=>m.FullName=="System.Void ClosingShift.Android.AndroidGraphics::SetSupporterActive(UnityEngine.GameObject,System.Boolean)" && m.IsStatic && m.IsPublic);
    var supporterCalls=supporterMethod.Body.Instructions.Select((i,index)=>(Instruction:i,Index:index)).Where(row=>row.Instruction.OpCode==OpCodes.Callvirt && row.Instruction.Operand is IMethod call && call.FullName=="System.Void UnityEngine.GameObject::SetActive(System.Boolean)").ToArray();
    Require(supporterCalls.Length==2,"Expected exactly the original ON/OFF object activation calls");
    var supporterOriginal=(IMethod)supporterCalls[0].Instruction.Operand;
    var supporterImport=new Importer(supporterGame).Import(supporterWrapper);
    foreach(var row in supporterCalls){row.Instruction.OpCode=OpCodes.Call;row.Instruction.Operand=supporterImport;}
    Directory.CreateDirectory(supporterFolder);
    string supporterOutput=Path.Combine(supporterFolder,"Assembly-CSharp.dll");
    var supporterWriter=new ModuleWriterOptions(supporterGame);supporterWriter.MetadataOptions.Flags|=MetadataFlags.PreserveAll;
    supporterGame.Write(supporterOutput,supporterWriter);VerifyMetadata(supporterInput,supporterOutput);
    using var supporterCheck=ModuleDefMD.Load(supporterOutput);
    var supporterSaved=(MethodDef)supporterCheck.ResolveToken(supporterMethod.MDToken.Raw);
    foreach(var row in supporterCalls)
    {
        var instruction=supporterSaved.Body.Instructions[row.Index];
        Require(instruction.OpCode==OpCodes.Call && instruction.Operand is IMethod savedCall && savedCall.FullName==supporterWrapper.FullName,"Supporter wrapper readback differs");
        instruction.OpCode=OpCodes.Callvirt;instruction.Operand=new Importer(supporterCheck).Import(supporterOriginal);
    }
    foreach(var method in supporterCheck.GetTypes().SelectMany(t=>t.Methods))Require(BodyHash(method)==supporterBodies[method.MDToken.Raw],"Unrelated supporter patch instruction changed: "+method.FullName);
    var supporterReceipt=new{inputSHA256=Hash(supporterInput),outputSHA256=Hash(supporterOutput),runtimeSHA256=Hash(supporterHelper),changedMethod=supporterMethod.FullName,changedCalls=2,allOtherInstructionsAndMetadataOwnersPreserved=true,unchangedMethodBodies=supporterBodies.Count-1};
    File.WriteAllText(Path.Combine(supporterFolder,"patch.json"),JsonSerializer.Serialize(supporterReceipt,new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("SUPPORTER_NULL_PATCH_PASS "+JsonSerializer.Serialize(supporterReceipt));
    return;
}

if (args.Length == 5 && args[0] == "--render-scale8")
{
    string inputGame = Path.GetFullPath(args[1]), runtimePath = Path.GetFullPath(args[2]), helperPath = Path.GetFullPath(args[3]), folder = Path.GetFullPath(args[4]);
    const string acceptedSha = "D1846DD283DA163FFD574239E1B7715E12C78CA3B8B146573251B69FC305B29B";
    Require(Hash(inputGame) == acceptedSha, "Accepted revision7 game preimage changed");
    string workRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
    Require((folder + Path.DirectorySeparatorChar).StartsWith(workRoot, StringComparison.OrdinalIgnoreCase), "Render candidate output must stay inside repository work");
    Require(!folder.StartsWith(Path.GetDirectoryName(inputGame)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Render output must be outside canonical Managed");
    using var input = ModuleDefMD.Load(inputGame);
    using var renderRuntime = ModuleDefMD.Load(runtimePath);
    using var helper = ModuleDefMD.Load(helperPath);
    Require(renderRuntime.Assembly.Name == "ClosingShift.Android" && helper.Assembly.Name == "ClosingShift.MobileRender", "Wrong render helper assembly identity");
    var mobileType = helper.GetTypes().Single(t => t.FullName == "ClosingShift.Android.MobileRenderSettings");
    foreach (string name in new[] { "SetResolution", "SetFullScreen" }) Require(mobileType.Methods.Count(m => m.Name == name && m.IsPublic && m.IsStatic) == 1, "Missing native fullscreen helper: " + name);
    var bodies = input.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, BodyHash);
    var changedRender = new HashSet<uint>();
    var calls = PatchRenderCalls(input, renderRuntime, changedRender);
    Directory.CreateDirectory(folder);
    string result = Path.Combine(folder, "Assembly-CSharp.dll");
    Require(!File.Exists(result), "Render candidate already exists");
    var writer = new ModuleWriterOptions(input); writer.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
    input.Write(result, writer); VerifyMetadata(inputGame, result);
    using var check = ModuleDefMD.Load(result);
    VerifyRenderCalls(check);
    foreach (var call in calls)
    {
        var saved = (MethodDef)check.ResolveToken(call.Token);
        Require(saved.Body.Instructions[call.Index].OpCode == OpCodes.Call && saved.Body.Instructions[call.Index].Operand is IMethod target && target.FullName == call.To, "Render wrapper readback changed");
        saved.Body.Instructions[call.Index].OpCode = call.Before;
        saved.Body.Instructions[call.Index].Operand = call.Operand;
    }
    // Reversing exactly the sixteen substitutions must recover every original method body.
    foreach (var pair in bodies) Require(BodyHash((MethodDef)check.ResolveToken(pair.Key)) == pair.Value, "Render patch changed other IL: " + pair.Key.ToString("X8"));
    Require(Hash(inputGame) == acceptedSha, "Canonical game input mutated");
    var audit = calls.Select(c => new { token = c.Token.ToString("X8"), method = ((MethodDef)input.ResolveToken(c.Token)).FullName, instructionIndex = c.Index, from = c.From, to = c.To });
    File.WriteAllText(Path.Combine(folder, "render-scale-patch.json"), JsonSerializer.Serialize(new { status = "NATIVE_FULLSCREEN_RENDER_SCALE_PATCH_PASS", source = inputGame, sourceSha256 = acceptedSha, output = result, outputSha256 = Hash(result), helperSha256 = Hash(helperPath), runtimeSha256 = Hash(runtimePath), substitutions = calls.Count, cameraPixelCalls = 7, resolutionWrites = 6, fullscreenWrites = 2, resetDropdownWrites = 1, changedMethods = changedRender.Count, allMethodBodiesReversedToOriginal = bodies.Count, metadataDefinitionsAndOwnersPreserved = true, canonicalModified = false, deviceModified = false, calls = audit }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine("RENDER_SCALE_PATCH_PASS " + Hash(result) + " substitutions=" + calls.Count + " all-method-bodies=" + bodies.Count);
    return;
}

if (args.Length == 3 && args[0] == "--bcg-tonemap")
{
    string firstpass = Path.GetFullPath(args[1]), folder = Path.GetFullPath(args[2]);
    Require(Hash(firstpass) == "42B0707025373391139A63149F5EBAFB334CD422CFD6C79E148880720843E844", "Canonical firstpass preimage changed");
    string workRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../work")) + Path.DirectorySeparatorChar;
    Require((folder + Path.DirectorySeparatorChar).StartsWith(workRoot, StringComparison.OrdinalIgnoreCase), "BCG candidate output must stay inside repository work");
    Require(!folder.StartsWith(Path.GetDirectoryName(firstpass)! + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "BCG output must be outside canonical Managed");
    using var module = ModuleDefMD.Load(firstpass);
    var method = module.ResolveToken(0x06002A77) as MethodDef;
    Require(method?.FullName == "System.Void Colorful.BrightnessContrastGamma::OnRenderImage(UnityEngine.RenderTexture,UnityEngine.RenderTexture)", "BCG method identity changed");
    string originalHash = BodyHash(method!);
    Require(originalHash == "C0736FE7146F7B584B11369FF26952817DFB7D9B95567F896590661228226C39", "BCG body preimage changed: " + originalHash);
    var bodies = module.GetTypes().SelectMany(t => t.Methods).ToDictionary(m => m.MDToken.Raw, BodyHash);
    var instructions = method!.Body.Instructions;
    var neutralReturn = instructions.First(i => i.OpCode == OpCodes.Ret);
    int neutralIndex = instructions.IndexOf(neutralReturn);
    Require(neutralIndex > 0 && instructions[neutralIndex - 1].Operand is IMethod blit && blit.FullName == "System.Void UnityEngine.Graphics::Blit(UnityEngine.Texture,UnityEngine.RenderTexture)", "BCG neutral blit changed");
    var materialEntry = instructions[neutralIndex + 1];
    Require(materialEntry.OpCode == OpCodes.Ldarg_0 && instructions[neutralIndex + 2].Operand is IMethod material && material.Name == "get_Material", "BCG shader entry changed");
    var platform = new MemberRefUser(module, "get_platform", MethodSig.CreateStatic(new ValueTypeSig(module.GetTypeRefs().Single(t => t.FullName == "UnityEngine.RuntimePlatform"))), module.GetTypeRefs().Single(t => t.FullName == "UnityEngine.Application"));
    instructions.Insert(0, Instruction.Create(OpCodes.Call, platform));
    instructions.Insert(1, Instruction.Create(OpCodes.Ldc_I4, 11));
    instructions.Insert(2, Instruction.Create(OpCodes.Beq, materialEntry));
    Directory.CreateDirectory(folder);
    string result = Path.Combine(folder, "Assembly-CSharp-firstpass.dll");
    Require(!File.Exists(result), "BCG candidate already exists");
    var writer = new ModuleWriterOptions(module); writer.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
    module.Write(result, writer); VerifyMetadata(firstpass, result);
    using var check = ModuleDefMD.Load(result);
    foreach (var pair in bodies.Where(p => p.Key != method.MDToken.Raw)) Require(BodyHash((MethodDef)check.ResolveToken(pair.Key)) == pair.Value, "Unrelated firstpass method changed");
    var saved = (MethodDef)check.ResolveToken(method.MDToken.Raw);
    Require(saved.Body.Instructions.Count == instructions.Count && saved.Body.Instructions[2].OpCode == OpCodes.Beq && saved.Body.Instructions.IndexOf((Instruction)saved.Body.Instructions[2].Operand) == neutralIndex + 4, "BCG Android branch readback changed");
    for (int i = 0; i < 3; ++i) saved.Body.Instructions.RemoveAt(0);
    Require(BodyHash(saved) == originalHash, "Original BCG body changed beyond Android entry branch");
    Require(Hash(firstpass) == "42B0707025373391139A63149F5EBAFB334CD422CFD6C79E148880720843E844", "Canonical firstpass mutated");
    File.WriteAllText(Path.Combine(folder, "bcg-tonemap.json"), JsonSerializer.Serialize(new { status="BCG_ANDROID_NEUTRAL_SHADER_PATH_PASS", source=firstpass, sourceSha256=Hash(firstpass), output=result, outputSha256=Hash(result), token="06002A77", originalBodyHash=originalHash, unrelatedMethodsVerified=bodies.Count-1, methodPointerRows=0, originalDefinitionsAndOwnersPreserved=true, canonicalModified=false },new JsonSerializerOptions{WriteIndented=true}));
    Console.WriteLine("BCG_TONEMAP_PASS " + Hash(result));
    return;
}

var keyCheck = new ClosingShift.Android.AndroidKeyState();
keyCheck.Set(1, true); keyCheck.Set(1, false); keyCheck.Advance();
Require(keyCheck.Down[1] && keyCheck.Held[1] && !keyCheck.Up[1], "Fast touch tap must survive one frame");
keyCheck.Advance();
Require(!keyCheck.Down[1] && !keyCheck.Held[1] && keyCheck.Up[1], "Tap release must occur once");
keyCheck.Advance();
Require(!keyCheck.Up[1], "Released key must not repeat");
keyCheck.Set(2, true); keyCheck.Advance(); keyCheck.Advance();
Require(keyCheck.Held[2] && !keyCheck.Down[2], "Held key must not repeat down");
keyCheck.Reset();
Require(!keyCheck.Held.Any(v => v) && !keyCheck.Down.Any(v => v) && !keyCheck.Up.Any(v => v), "Cancellation must clear virtual states");
keyCheck.Set(3, true); keyCheck.Cancel(3); keyCheck.Advance();
Require(!keyCheck.Held[3] && !keyCheck.Down[3], "Canceled touch must not produce a delayed tap");
keyCheck.Reset();
keyCheck.Set(0,4,true);keyCheck.Set(1,4,true);keyCheck.Advance();
keyCheck.CancelSource(0);keyCheck.Advance();
Require(keyCheck.Held[4]&&!keyCheck.Down[4]&&!keyCheck.Up[4],"Touch cancellation must preserve pad hold");
keyCheck.CancelSource(1);keyCheck.Advance();Require(keyCheck.Up[4]&&!keyCheck.Held[4],"Last source release must produce one up");
keyCheck.Advance();Require(!keyCheck.Up[4],"Disconnected source must not repeat release");
keyCheck.Set(1,5,true);keyCheck.Set(1,5,false);keyCheck.Advance();
Require(keyCheck.Down[5]&&keyCheck.Held[5],"Fast pad/trigger tap must survive one frame");keyCheck.Advance();Require(keyCheck.Up[5],"Fast trigger tap must release next frame");
var pointerCheck=new ClosingShift.Android.AndroidPointerState();pointerCheck.Advance(false,true);
Require(pointerCheck.Down&&pointerCheck.Held&&!pointerCheck.Up,"Quick pointer A tap must retain press");pointerCheck.Advance(false,false);
Require(pointerCheck.Up&&!pointerCheck.Held,"Quick pointer A tap must release and click exactly once");pointerCheck.Advance(false,false);Require(!pointerCheck.Up,"Pointer release must not repeat");
ClosingShift.Android.AndroidInputPolicy.Radial(.6f,0,.2f,out float rx,out float ry);Require(Math.Abs(rx-.5f)<1e-6f&&ry==0,"Radial deadzone must rescale remaining travel");
ClosingShift.Android.AndroidInputPolicy.Radial(1,1,.19f,out rx,out ry);Require(Math.Abs(Math.Sqrt(rx*rx+ry*ry)-1)<1e-6,"Radial diagonal must stay unit length");
ClosingShift.Android.AndroidInputPolicy.Radial(.1f,.1f,.19f,out rx,out ry);Require(rx==0&&ry==0,"Radial neutral must stay zero");
Require(!ClosingShift.Android.AndroidInputPolicy.Trigger(false,.54f)&&ClosingShift.Android.AndroidInputPolicy.Trigger(false,.56f)&&ClosingShift.Android.AndroidInputPolicy.Trigger(true,.4f)&&!ClosingShift.Android.AndroidInputPolicy.Trigger(true,.34f),"Trigger hysteresis boundaries changed");
float rate30=0,rate60=0;for(int i=0;i<30;++i)rate30+=ClosingShift.Android.AndroidInputPolicy.RateDelta(8,1f/30);for(int i=0;i<60;++i)rate60+=ClosingShift.Android.AndroidInputPolicy.RateDelta(8,1f/60);
Require(Math.Abs(rate30-rate60)<.00001f&&Math.Abs(rate60-8)<.00001f,"Right-stick rate must integrate equally at30/60fps with deltaTime once");
Require(ClosingShift.Android.AndroidInputPolicy.RateDelta(8,10)==.8f&&ClosingShift.Android.AndroidInputPolicy.RateDelta(8,-1)==0,"Hitch/resume delta must not cause unbounded look jumps");
Require(ClosingShift.Android.AndroidInputPolicy.ResolveBack(true,true,true,true,true,true)==BackOwner.Dropdown,"Dropdown must own Back before modal/gameplay");
Require(ClosingShift.Android.AndroidInputPolicy.ResolveBack(false,true,true,false,false,false)==BackOwner.Rebind,"Keyboard capture cancel must not escape into parent menu");
Require(ClosingShift.Android.AndroidInputPolicy.ResolveBack(false,false,false,true,true,false)==BackOwner.Document,"Document close must not also open inventory/pause");
Require(ClosingShift.Android.AndroidInputPolicy.ResolveBack(false,false,false,false,true,false)==BackOwner.Inventory,"Inventory close must not also open pause");
Require(ClosingShift.Android.AndroidInputPolicy.ResolveBack(false,false,false,false,false,true)==BackOwner.Story,"Story Back must not bypass dialogue events");
Require(ClosingShift.Android.AndroidNativeMousePolicy.Filter("Mouse Y",.447f,true,false,1)==0,"Touch-generated native mouse axis must be suppressed");
Require(ClosingShift.Android.AndroidNativeMousePolicy.Filter("Mouse X",2,true,true,0)==2,"Real Android mouse remains supported without active fingers");
Require(ClosingShift.Android.AndroidNativeMousePolicy.Filter("Mouse Y",2,true,true,1)==0,"Active touch owns native mouse-axis priority");
Require(ClosingShift.Android.AndroidNativeMousePolicy.Filter("Horizontal",.7f,true,false,1)==.7f,"Keyboard and non-mouse axes must remain unchanged");
Require(ClosingShift.Android.AndroidNativeMousePolicy.Filter("Mouse Y",2,false,false,1)==2,"Desktop mouse behavior must remain unchanged");
Console.WriteLine("INPUT_POLICY_PASS multisource/quicktap/pointer/trigger/radial/single-owner-back/native-mouse-source");
if (args.Length==1 && args[0]=="--self-test") return;

if (args.Length != 3) throw new ArgumentException("Usage: patch_managed <original Managed> <output directory> <ClosingShift.Android.dll>");
string source = Path.GetFullPath(args[0]), output = Path.GetFullPath(args[1]);
string protectedRoot = Directory.GetParent(source)!.Parent!.FullName.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
Require(!(output.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar).StartsWith(protectedRoot, StringComparison.OrdinalIgnoreCase), "Output must be outside the original game directory");
var expected = new Dictionary<string, string>
{
    ["Assembly-CSharp.dll"] = "856B084D4749F08252FD79F82083C09134537BEE992E8AFC7B5A017C2688D473",
    ["Assembly-CSharp-firstpass.dll"] = "87A432543F1FC63EF05B7C22EEEBC4D55F106C82FA69C096E8419C41043A51D7"
};
foreach (var item in expected) Require(Hash(Path.Combine(source, item.Key)) == item.Value, "Unexpected preimage: " + item.Key);
using var game = ModuleDefMD.Load(Path.Combine(source, "Assembly-CSharp.dll"));
var originalBodies=game.GetTypes().SelectMany(t=>t.Methods).ToDictionary(m=>m.MDToken.Raw,BodyHash);
using var runtime = ModuleDefMD.Load(args[2]);
Require(runtime.Assembly.Name == "ClosingShift.Android", "Wrong runtime assembly identity");
var adapter = runtime.GetTypes().Single(t => t.FullName == "ClosingShift.Android.AndroidInput");
var importer = new Importer(game);
var patches = new List<string>();
var changed = new HashSet<uint>();

IMethod RuntimeMethod(string name, MethodSig? signature = null) => importer.Import(adapter.Methods.Single(m => m.Name == name && (signature == null || m.MethodSig.ToString() == signature.ToString())));
MethodDef Method(uint token, string signature)
{
    var method = game.ResolveToken(token) as MethodDef;
    Require(method != null && method.FullName == signature && method.HasBody, "Method preimage mismatch: " + signature);
    return method!;
}
void Mark(MethodDef method, string reason) { changed.Add(method.MDToken.Raw); patches.Add(method.FullName + ": " + reason); }
void Empty(uint token, string signature, bool returnsBool = false)
{
    var method = Method(token, signature);
    method.Body = new CilBody();
    if (returnsBool) method.Body.Instructions.Add(Instruction.Create(OpCodes.Ldc_I4_0));
    method.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
    Mark(method, "offline Steam integration");
}

Empty(0x06000D25, "System.Boolean SteamManager::get_Initialized()", true);
Empty(0x06000D28, "System.Void SteamManager::Awake()");
Empty(0x06000D29, "System.Void SteamManager::OnEnable()");
Empty(0x06000D2A, "System.Void SteamManager::OnDestroy()");
Empty(0x06000D2B, "System.Void SteamManager::Update()");
Empty(0x06000B7E, "System.Void AchievementManager::UnlockSteamAchievement(System.String)");
Empty(0x06000B7F, "System.Boolean AchievementManager::TestSteamAchievement(System.String)", true);
Empty(0x06000B80, "System.Void AchievementManager::LockAchievement()");

var appData = game.GetMemberRefs().Single(m => m.FullName == "System.String UnityEngine.Application::get_dataPath()");
var persistent = new MemberRefUser(game, "get_persistentDataPath", MethodSig.CreateStatic(game.CorLibTypes.String), appData.DeclaringType);
int pathChanges = 0;
foreach (var entry in new[]
{
    (0x06000FFEu, "System.String ThunderWire.Configuration.ConfigManager::GetFolderPath(ThunderWire.Configuration.FilePath)"),
    (0x06000FFFu, "System.String ThunderWire.Configuration.ConfigManager::GetFilepathRoot(ThunderWire.Configuration.FilePath)"),
    (0x06000FD8u, "System.String ThunderWire.JsonManager.JsonManager::GetFilePath(ThunderWire.JsonManager.FilePath)")
})
{
    var method = Method(entry.Item1, entry.Item2);
    var instructions = method.Body.Instructions;
    for (int i = 0; i < instructions.Count; ++i)
    {
        if (instructions[i].Operand is not IMethod target) continue;
        if (target.FullName == appData.FullName) { instructions[i].Operand = persistent; ++pathChanges; }
        else if (target.FullName == "System.String System.Environment::GetFolderPath(System.Environment/SpecialFolder)")
        {
            // Keep the original instruction as a branch target while discarding its enum argument.
            instructions[i].OpCode = OpCodes.Pop;
            instructions[i].Operand = null;
            instructions.Insert(++i, Instruction.Create(OpCodes.Call, persistent));
            ++pathChanges;
        }
    }
    Mark(method, "persistent writable base, original suffixes retained");
}
Require(pathChanges == 7, "Writable-path patch coverage changed");

var setup = Method(0x06000CE4, "System.Void MenuManager::SetupResolutionSettings()");
var resolutions = setup.Body.Instructions.Single(i => i.Operand is IMethod m && m.FullName == "UnityEngine.Resolution[] UnityEngine.Screen::get_resolutions()");
resolutions.Operand = RuntimeMethod("GetResolutions");
Mark(setup, "native resolution fallback");
foreach (var entry in new[] { (0x06000CF0u, "System.Void MenuManager::SetResolution(System.Int32)"), (0x06000CF1u, "System.Void MenuManager::SetGraphics()") })
{
    var method = Method(entry.Item1, entry.Item2);
    var access = method.Body.Instructions.Single(i => i.OpCode == OpCodes.Ldelem && i.Operand is ITypeDefOrRef t && t.FullName == "UnityEngine.Resolution");
    access.OpCode = OpCodes.Call;
    access.Operand = RuntimeMethod("GetResolution");
    Mark(method, "bounds-checked resolution access");
}

int remainingResolutionLists = 0, remainingResolutionReads = 0;
foreach (var method in game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is IMethod target && target.FullName == "UnityEngine.Resolution[] UnityEngine.Screen::get_resolutions()")
        {
            instruction.Operand = RuntimeMethod("GetResolutions");
            ++remainingResolutionLists;
            changed.Add(method.MDToken.Raw);
        }
        else if (instruction.OpCode == OpCodes.Ldelem && instruction.Operand is ITypeDefOrRef type && type.FullName == "UnityEngine.Resolution")
        {
            instruction.OpCode = OpCodes.Call;
            instruction.Operand = RuntimeMethod("GetResolution");
            ++remainingResolutionReads;
            changed.Add(method.MDToken.Raw);
        }
    }
}
Require(remainingResolutionLists == 1 && remainingResolutionReads == 2, "Secondary options resolution coverage changed");
patches.Add("Native-resolution fallback and bounds checks also cover legacy UICustomOptions");
var renderScaleCalls = PatchRenderCalls(game, runtime, changed);
patches.Add("Native fullscreen guards cover all six resolution/two fullscreen writes; seven camera pixel conversions preserve native pointer coordinates");

int inputChanges = 0;
foreach (var method in game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is not IMethod target || target.DeclaringType.FullName != "UnityEngine.Input") continue;
        string wrapper=target.Name;
        if(method.DeclaringType.FullName=="MouseLook"&&wrapper=="GetAxis")wrapper="GetPlayerLookAxis";
        if(method.DeclaringType.FullName=="ReadingMaterials"&&wrapper=="GetKeyDown")wrapper="GetReadingKeyDown";
        if(method.FullName=="System.Void InputController::Update()"&&wrapper=="GetKeyDown")wrapper="GetPhysicalKeyDown";
        instruction.Operand = RuntimeMethod(wrapper, target.MethodSig);
        ++inputChanges;
        changed.Add(method.MDToken.Raw);
    }
}
Require(inputChanges == 320, "Gameplay Input call coverage changed: " + inputChanges);
patches.Add("Gameplay UnityEngine.Input calls routed: " + inputChanges);

int controlExtraCalls=0;
var playerUpdate=Method(0x0600024c,"System.Void PlayerController::Update()");
var playerIl=playerUpdate.Body.Instructions;
var diagonal=playerIl.Single(i=>i.OpCode==OpCodes.Ldc_R4&&i.Operand is float f&&f==.7071f);
int diagonalIndex=playerIl.IndexOf(diagonal);
var diagonalStore=playerIl.Skip(diagonalIndex+1).Take(8).First(i=>i.IsStloc());
Local diagonalLocal=diagonalStore.Operand as Local ?? playerUpdate.Body.Variables[diagonalStore.OpCode.Code switch{Code.Stloc_0=>0,Code.Stloc_1=>1,Code.Stloc_2=>2,Code.Stloc_3=>3,_=>throw new InvalidDataException("Diagonal local store differs")}];
Require(diagonalLocal.Type.FullName=="System.Single","Player diagonal local type differs");
var inputX=playerUpdate.DeclaringType.Fields.Single(f=>f.Name=="inputX"&&f.FieldSig.Type.FullName=="System.Single");
var inputY=playerUpdate.DeclaringType.Fields.Single(f=>f.Name=="inputY"&&f.FieldSig.Type.FullName=="System.Single");
var controllable=playerUpdate.DeclaringType.Fields.Single(f=>f.Name=="controllable"&&f.FieldSig.Type.FullName=="System.Boolean");
int analogAt=playerIl.IndexOf(diagonalStore)+1;
foreach(var instruction in new[]{Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldflda,inputX),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldflda,inputY),Instruction.Create(OpCodes.Ldarg_0),Instruction.Create(OpCodes.Ldfld,controllable),Instruction.Create(OpCodes.Call,RuntimeMethod("ApplyAnalogMove")),Instruction.Create(OpCodes.Ldloc,diagonalLocal),Instruction.Create(OpCodes.Call,RuntimeMethod("DiagonalFactor")),Instruction.Create(OpCodes.Stloc,diagonalLocal)})playerIl.Insert(analogAt++,instruction);
controlExtraCalls+=2;Mark(playerUpdate,"true radial analog at existing inputX/inputY seam; original locks/keyboard smoothing/physics retained, analog diagonal normalized once");

var inputUpdate=Method(0x060003f9,"System.Void InputController::Update()");
var rebindField=inputUpdate.DeclaringType.Fields.Single(f=>f.Name=="rebind"&&f.FieldSig.Type.FullName=="System.Boolean");
var originalFirst=inputUpdate.Body.Instructions[0];
inputUpdate.Body.Instructions.Insert(0,Instruction.Create(OpCodes.Ldarg_0));inputUpdate.Body.Instructions.Insert(1,Instruction.Create(OpCodes.Ldfld,rebindField));inputUpdate.Body.Instructions.Insert(2,Instruction.Create(OpCodes.Brtrue,originalFirst));inputUpdate.Body.Instructions.Insert(3,Instruction.Create(OpCodes.Ret));
Mark(inputUpdate,"skip full KeyCode enumeration outside physical keyboard capture; virtual controls never become keyboard bindings");
var beginCapture=Method(0x060003f8,"System.Void InputController::Rebind(System.String)");
var captureRet=beginCapture.Body.Instructions.Last();Require(captureRet.OpCode==OpCodes.Ret,"Rebind final return differs");
captureRet.OpCode=OpCodes.Call;captureRet.Operand=RuntimeMethod("BeginKeyboardCapture");beginCapture.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
++controlExtraCalls;Mark(beginCapture,"ignore the UI submit/click frame when keyboard capture begins; original capture remains cancelable");
var generateInputs=Method(0x060003f7,"System.Void InputController::GenerateInputs(System.Boolean)");
var rewriteRead=generateInputs.Body.Instructions.Single(i=>i.OpCode==OpCodes.Ldfld&&i.Operand is IField f&&f.Name=="RewriteConfig");
generateInputs.Body.Instructions.Insert(generateInputs.Body.Instructions.IndexOf(rewriteRead)+1,Instruction.Create(OpCodes.Call,RuntimeMethod("UseAuthoredRewrite")));
++controlExtraCalls;
var generateRet=generateInputs.Body.Instructions.Last();Require(generateRet.OpCode==OpCodes.Ret,"Input row generation final return differs");
generateRet.OpCode=OpCodes.Call;generateRet.Operand=RuntimeMethod("RefreshUi");generateInputs.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
++controlExtraCalls;Mark(generateInputs,"preserve saved keyboard rebinding on Android; cache newly generated eight UI rows after creation");
var interactPrompt=Method(0x06000459,"System.Void InteractManager::Interact(UnityEngine.GameObject)");
var promptIl=interactPrompt.Body.Instructions;int promptCalls=0;
for(int i=1;i+4<promptIl.Count;++i)
{
    if(promptIl[i].Operand is not IMethod call||call.FullName!="UnityEngine.KeyCode InputController::GetInput(System.String)"||promptIl[i-1].Operand is not IMethod between||between.Name!="GetBetween")continue;
    Require(promptIl.Skip(Math.Max(0,i-5)).Take(4).Any(x=>x.IsLdcI4()&&x.GetLdcI4Value()==123)&&promptIl.Skip(Math.Max(0,i-5)).Take(4).Any(x=>x.IsLdcI4()&&x.GetLdcI4Value()==125),"Prompt brace placeholders changed");
    Require(promptIl[i+1].IsStloc()&&promptIl[i+1].Operand is Local local&&local.Type.FullName=="UnityEngine.KeyCode"&&promptIl[i+2].OpCode==OpCodes.Ldloca_S&&ReferenceEquals(promptIl[i+1].Operand,promptIl[i+2].Operand),"Prompt enum local preimage differs");
    Require(promptIl[i+3].OpCode==OpCodes.Constrained&&promptIl[i+3].Operand is ITypeDefOrRef constraint&&constraint.FullName=="UnityEngine.KeyCode"&&promptIl[i+4].OpCode==OpCodes.Callvirt&&promptIl[i+4].Operand is IMethod text&&text.FullName=="System.String System.Object::ToString()","Prompt enum ToString preimage differs");
    for(int offset=1;offset<=3;++offset){var target=promptIl[i+offset];Require(!promptIl.Any(x=>ReferenceEquals(x.Operand,target)||x.Operand is IList<Instruction> list&&list.Contains(target)),"Unexpected branch target inside prompt expression");target.OpCode=OpCodes.Nop;target.Operand=null;}
    promptIl[i+4].OpCode=OpCodes.Call;promptIl[i+4].Operand=RuntimeMethod("PromptKeyName");++promptCalls;
}
Require(promptCalls==2,"Expected exactly two authored key-placeholder prompt conversions");controlExtraCalls+=promptCalls;
Mark(interactPrompt,"last meaningful touch/pad/physical source key labels in two authored localized target prompts; message text/requirements/events retained");

int volumeWrites = 0, volumeReads = 0;
foreach (var method in game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
{
    foreach (var instruction in method.Body.Instructions)
    {
        if (instruction.Operand is not IMethod target || target.DeclaringType.FullName != "UnityEngine.AudioListener") continue;
        if (target.Name == "set_volume") { instruction.Operand = RuntimeMethod("SetVolume"); ++volumeWrites;changed.Add(method.MDToken.Raw); }
        else if (target.Name == "get_volume") { instruction.Operand = RuntimeMethod("GetVolume"); ++volumeReads;changed.Add(method.MDToken.Raw); }
    }
}
Require(volumeWrites == 6 && volumeReads == 1, "Audio volume wrapper call coverage changed");
patches.Add("Audio volume wrappers preserve authored fades and audio playback clocks");

var getInput = Method(0x06000400, "UnityEngine.KeyCode InputController::GetInput(System.String)");
int bindingReturns = 0;
for (int i = 0; i < getInput.Body.Instructions.Count; ++i)
{
    var instruction = getInput.Body.Instructions[i];
    if (instruction.OpCode != OpCodes.Ret) continue;
    instruction.OpCode = OpCodes.Ldarg_1;
    getInput.Body.Instructions.Insert(++i, Instruction.Create(OpCodes.Call, RuntimeMethod("RecordBinding")));
    getInput.Body.Instructions.Insert(++i, Instruction.Create(OpCodes.Ret));
    ++bindingReturns;
}
Require(bindingReturns == 2, "Input binding return coverage changed");
Mark(getInput, "capture actual semantic bindings without changing return values");

var init = Method(0x06000D27, "System.Void SteamManager::InitOnPlayMode()");
var ret = init.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
ret.OpCode = OpCodes.Call;
ret.Operand = RuntimeMethod("Bootstrap");
init.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
Mark(init, "root shim bootstrap through original RuntimeInitializeOnLoads entry");
var configAwake = Method(0x060003E4, "System.Void ConfigHandler::Awake()");
configAwake.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Call, RuntimeMethod("Bootstrap")));
Mark(configAwake, "initialize persistent touch/FPS UI through the active configuration component");
StartupPatches.Apply(game, Mark, inactiveTemplateGuards: true);

var graphicsAdapter = runtime.GetTypes().Single(t => t.FullName == "ClosingShift.Android.AndroidGraphics");
var applyGraphicsOptions = importer.Import(graphicsAdapter.Methods.Single(m => m.FullName == "System.Void ClosingShift.Android.AndroidGraphics::ApplyOptions(UnityEngine.MonoBehaviour)"));
var defaultEffectEnabled = importer.Import(graphicsAdapter.Methods.Single(m => m.FullName == "System.Boolean ClosingShift.Android.AndroidGraphics::GetDefaultEffectEnabled()"));
int graphicsCalls = 0;
foreach (var specification in new[] {
    (0x0600037Du, "System.Void UICustomOptions::SetVHS()"),
    (0x0600037Eu, "System.Void UICustomOptions::SetDepthOfField()"),
    (0x0600038Au, "System.Void UICustomOptions::SetDefaultGraphics(System.Boolean)"),
    (0x0600038Du, "System.Void UICustomOptions::ApplyGraphic(System.Boolean)"),
    (0x06000390u, "System.Void UICustomOptions::SerializeGraphic()") })
{
    var method = Method(specification.Item1, specification.Item2);
    method.Body.Instructions.Insert(0, Instruction.Create(OpCodes.Ldarg_0));
    method.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Call, applyGraphicsOptions));
    ++graphicsCalls;
    Mark(method, "prepare six Android effect controls before the original callbacks, apply and configuration writer");
}
var defaultGraphics = Method(0x0600038A, "System.Void UICustomOptions::SetDefaultGraphics(System.Boolean)");
var resetInstructions = defaultGraphics.Body.Instructions;
foreach (var specification in new[] {
    ("DepthOfField", "System.Void UnityEngine.UI.Toggle::set_isOn(System.Boolean)"),
    ("VHSEffect", "System.Void UnityEngine.UI.Toggle::set_isOn(System.Boolean)"),
    ("vhsFilter", "System.Void UnityEngine.Behaviour::set_enabled(System.Boolean)") })
{
    var field = resetInstructions.Single(i => i.OpCode == OpCodes.Ldfld && i.Operand is IField f && f.Name == specification.Item1);
    int index = resetInstructions.IndexOf(field);
    Require(index > 0 && resetInstructions[index - 1].OpCode == OpCodes.Ldarg_0 &&
        resetInstructions[index + 1].IsLdcI4() && resetInstructions[index + 1].GetLdcI4Value() == 1 &&
        resetInstructions[index + 2].OpCode == OpCodes.Callvirt && resetInstructions[index + 2].Operand is IMethod setter && setter.FullName == specification.Item2,
        "Default graphics effect reset preimage changed: " + specification.Item1);
    resetInstructions[index + 1].OpCode = OpCodes.Call;
    resetInstructions[index + 1].Operand = defaultEffectEnabled;
    ++graphicsCalls;
}
Require(graphicsCalls == 8, "Android graphics policy call coverage changed");
patches.Add("Android reset defaults VHS/DOF off; original Graphic writer carries versioned optional effect choices through PrepareConfig");

IMethod GraphicsMethod(string name) => importer.Import(graphicsAdapter.Methods.Single(m => m.Name == name));
foreach (var specification in new[] { (0x0600038Du, "BeforeApplyQuality"), (0x06000390u, "BeforeSerializeQuality") })
{
    var method = (MethodDef)game.ResolveToken(specification.Item1);
    method.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Ldarg_0));
    method.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Call, GraphicsMethod(specification.Item2)));
    ++graphicsCalls; Mark(method, "synchronize Android quality tuple; suspend shadow collectors only before the original apply body");
}
defaultGraphics.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Ldarg_0));
defaultGraphics.Body.Instructions.Insert(3, Instruction.Create(OpCodes.Call, GraphicsMethod("ResetQuality")));
++graphicsCalls;
var qualityChanged = Method(0x0600039C, "System.Void UICustomOptions::OnQualityDropdownChange(UnityEngine.UI.Dropdown)");
Require(qualityChanged.Body.Instructions.Count(i => i.Operand is IMethod m && m.Name == "SetGraphicSettings") == 1, "Quality selection shape changed");
var qualityReturn = qualityChanged.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
qualityReturn.OpCode = OpCodes.Ldarg_0;
qualityChanged.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_1));
qualityChanged.Body.Instructions.Add(Instruction.Create(OpCodes.Call, GraphicsMethod("QualitySelected")));
qualityChanged.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
++graphicsCalls; Mark(qualityChanged, "numeric LOW/VeryLow selection updates existing controls without label matching or recursive events");
var serializeGraphics = Method(0x06000390, "System.Void UICustomOptions::SerializeGraphic()");
var configWrite = serializeGraphics.Body.Instructions.Single(i => i.Operand is IMethod m && m.FullName == "System.Void ConfigHandler::Serialize(System.String,System.Collections.Generic.Dictionary`2<System.String,System.String>)");
serializeGraphics.Body.Instructions.Insert(serializeGraphics.Body.Instructions.IndexOf(configWrite), Instruction.Create(OpCodes.Call, GraphicsMethod("PrepareConfig")));
++graphicsCalls;
var optionStart = Method(0x0600037B, "System.Void UICustomOptions::Start()");
Require(optionStart.Body.Instructions.Count(i => i.Operand is IMethod m && m.Name == "ApplyAllSettings") == 1 && optionStart.Body.Instructions.Count(i => i.Operand is IMethod m && m.Name == "UpdateOptions") == 1, "Options first-start apply route changed");
Require(graphicsCalls == 13, "LOW graphics hook coverage changed");
var configReturns = configAwake.Body.Instructions.Where(i => i.OpCode == OpCodes.Ret).ToArray();
Require(configReturns.Length == 2, "Configuration startup return shape changed");
foreach (var exit in configReturns)
{
    int index = configAwake.Body.Instructions.IndexOf(exit);
    exit.OpCode = OpCodes.Ldarg_0;
    configAwake.Body.Instructions.Insert(index + 1, Instruction.Create(OpCodes.Call, GraphicsMethod("ConfigurationLoaded")));
    configAwake.Body.Instructions.Insert(index + 2, Instruction.Create(OpCodes.Ret));
    ++graphicsCalls;
}
Mark(configAwake, "restore saved Android LOW profile after original config read, including cold menu without active options Start");
Require(graphicsCalls == 15, "Configuration/LOW hook coverage changed");
Require(configAwake.Body.Instructions[0].Operand is IMethod boot && boot.FullName == RuntimeMethod("Bootstrap").FullName &&
    configAwake.Body.Instructions.Count(i => i.Operand is IMethod m && m.FullName == "System.Void ThunderWire.Configuration.ConfigManager::SetFilePath(ThunderWire.Configuration.FilePath)") == 1,
    "Original configuration path/bootstrap entry changed");
configAwake.Body.Instructions.Insert(1, Instruction.Create(OpCodes.Ldarg_0));
configAwake.Body.Instructions.Insert(2, Instruction.Create(OpCodes.Call, GraphicsMethod("BeforeConfiguration")));
++graphicsCalls;
Mark(configAwake, "Android GameConfig uses existing Documents reader/writer in menu and world; custom config paths retained");
Require(graphicsCalls == 16, "Shared GameConfig/LOW hook coverage changed");
patches.Add("Android LOW540/VeryLow360; single original config writer includes versioned one-time LOW migration, desktop preserved");

var sceneAdapter = runtime.GetTypes().Single(t => t.FullName == "ClosingShift.Android.AndroidSceneLoader");
var sceneRuntimeLoad = sceneAdapter.Methods.Single(m => m.FullName == "System.Void ClosingShift.Android.AndroidSceneLoader::LoadScene(System.String)");
var sceneEntryCalls = sceneRuntimeLoad.Body.Instructions.Where(i => i.Operand is IMethod).Select(i => ((IMethod)i.Operand).FullName).ToArray();
Require(sceneEntryCalls.Count(s => s == "UnityEngine.RuntimePlatform UnityEngine.Application::get_platform()") == 1 &&
    sceneEntryCalls.Count(s => s == "System.Boolean UnityEngine.Application::CanStreamedLevelBeLoaded(System.String)") == 1 &&
    sceneEntryCalls.Count(s => s == "System.Void UnityEngine.SceneManagement.SceneManager::LoadScene(System.String)") == 1 &&
    sceneEntryCalls.Count(s => s == "System.Void UnityEngine.Object::DontDestroyOnLoad(UnityEngine.Object)") == 1,
    "Scene platform/original-fallback/persistent-owner coverage changed");
var delayFactory = Method(0x06000CF9, "System.Collections.IEnumerator SceneLoader::Delay(System.String,System.Single)");
var delayConstructor = (IMethod)delayFactory.Body.Instructions.Single(i => i.OpCode == OpCodes.Newobj).Operand;
var delayState = delayConstructor.ResolveMethodDef().DeclaringType;
Require(delayState.DeclaringType.FullName == "SceneLoader" && delayState.Name.String.StartsWith("<Delay>"), "Scene load iterator preimage changed");
var delayMoveNext = delayState.Methods.Single(m => m.Name == "MoveNext" && m.HasBody);
var sceneLoadCall = delayMoveNext.Body.Instructions.Single(i => i.Operand is IMethod m && m.FullName == "System.Void UnityEngine.SceneManagement.SceneManager::LoadScene(System.String)");
int sceneLoadIndex = delayMoveNext.Body.Instructions.IndexOf(sceneLoadCall);
Require(sceneLoadCall.OpCode == OpCodes.Call && sceneLoadIndex > 0 && delayMoveNext.Body.Instructions[sceneLoadIndex - 1].Operand is IField sceneNameField && sceneNameField.Name == "sceneName" && sceneNameField.FieldSig.Type.ElementType == ElementType.String, "Final scene load argument/preimage changed");
var sceneOriginalShape = delayMoveNext.Body.Instructions.Select(i => i.OpCode.Code).ToArray();
sceneLoadCall.Operand = importer.Import(sceneRuntimeLoad);
Mark(delayMoveNext, "Android empty-scene unload/unused-assets/GC transition at final load; prior save/events/delay retained");
var transitionState = runtime.GetTypes().Single(t => t.DeclaringType?.FullName == "ClosingShift.Android.AndroidSceneLoaderLifetime" && t.Name.String.StartsWith("<Transition>"));
var transitionCalls = transitionState.Methods.Single(m => m.Name == "MoveNext").Body.Instructions.Where(i => i.Operand is IMethod).Select(i => ((IMethod)i.Operand).FullName).ToList();
string[] cleanupSequence = {
    "UnityEngine.SceneManagement.Scene UnityEngine.SceneManagement.SceneManager::CreateScene(System.String)",
    "UnityEngine.AsyncOperation UnityEngine.SceneManagement.SceneManager::UnloadSceneAsync(UnityEngine.SceneManagement.Scene)",
    "UnityEngine.AsyncOperation UnityEngine.Resources::UnloadUnusedAssets()",
    "System.Void System.GC::Collect()",
    "UnityEngine.AsyncOperation UnityEngine.SceneManagement.SceneManager::LoadSceneAsync(System.String,UnityEngine.SceneManagement.LoadSceneMode)"
};
Require(cleanupSequence.All(s => transitionCalls.Count(c => c == s) == 1), "Android scene cleanup call coverage changed");
Require(cleanupSequence.Select(s => transitionCalls.IndexOf(s)).SequenceEqual(cleanupSequence.Select(s => transitionCalls.IndexOf(s)).Order()), "Android scene cleanup order changed");

Directory.CreateDirectory(output);
string destination = Path.Combine(output, "Assembly-CSharp.dll"), temporary = destination + ".tmp";
var options = new ModuleWriterOptions(game);
options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
game.Write(temporary, options);
VerifyMetadata(Path.Combine(source,"Assembly-CSharp.dll"),temporary);
using (var check = ModuleDefMD.Load(temporary))
{
    Require(check.GetTypes().Count() == game.GetTypes().Count(), "Type count changed");
    Require(check.GetTypes().Sum(t => t.Fields.Count) == game.GetTypes().Sum(t => t.Fields.Count), "Serialized fields changed");
    foreach (var type in game.GetTypes())
    {
        Require((check.ResolveToken(type.MDToken.Raw) as TypeDef)?.FullName == type.FullName, "Type token changed: " + type.FullName);
        foreach (var field in type.Fields) Require((check.ResolveToken(field.MDToken.Raw) as FieldDef)?.FullName == field.FullName, "Field token changed: " + field.FullName);
        foreach (var method in type.Methods) Require((check.ResolveToken(method.MDToken.Raw) as MethodDef)?.FullName == method.FullName, "Method token changed: " + method.FullName);
    }
    foreach(var pair in originalBodies.Where(p=>!changed.Contains(p.Key)))Require(BodyHash((MethodDef)check.ResolveToken(pair.Key))==pair.Value,"Unrelated method body changed: "+pair.Key.ToString("x8"));
    int wrappers = 0, sceneWrappers = 0, graphicsWrappers = 0;
    foreach (var method in check.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
    {
        foreach (var instruction in method.Body.Instructions)
        {
            if (instruction.Operand is not IMethod target) continue;
            Require(target.DeclaringType.FullName != "UnityEngine.Input", "Unpatched gameplay Input call");
            if (target.DeclaringType.FullName == adapter.FullName) ++wrappers;
            if (target.DeclaringType.FullName == sceneAdapter.FullName) ++sceneWrappers;
            if (target.DeclaringType.FullName == graphicsAdapter.FullName) ++graphicsWrappers;
            if (changed.Contains(method.MDToken.Raw)) Require(!target.DeclaringType.FullName.StartsWith("Steamworks."), "Native Steam path remains in patched method");
        }
    }
    Require(wrappers == inputChanges + 5 + bindingReturns + volumeWrites + volumeReads + remainingResolutionLists + remainingResolutionReads +controlExtraCalls, "Runtime call count mismatch");
    Require(sceneWrappers == 1, "Scene-load seam must have exactly one call site");
    Require(graphicsWrappers == graphicsCalls + 1, "Android graphics policy/reset resolution guard missing after write");
    VerifyRenderCalls(check);
    foreach (uint token in new uint[] { 0x0600037D, 0x0600037E, 0x0600038A, 0x0600038D, 0x06000390 })
    {
        var method = (MethodDef)check.ResolveToken(token);
        Require(method.Body.Instructions[0].OpCode == OpCodes.Ldarg_0 &&
            method.Body.Instructions[1].Operand is IMethod call && call.FullName == applyGraphicsOptions.FullName,
            "Android graphics checkbox guard must precede authored body: " + method.FullName);
    }
    Require(((MethodDef)check.ResolveToken(0x0600038A)).Body.Instructions.Count(i => i.Operand is IMethod m && m.FullName == defaultEffectEnabled.FullName) == 3,
        "Android reset must guard exactly its three original effect-enable writes");
    var checkedDelay = (MethodDef)check.ResolveToken(delayMoveNext.MDToken.Raw);
    Require(checkedDelay.Body.Instructions.Select(i => i.OpCode.Code).SequenceEqual(sceneOriginalShape), "Scene iterator control-flow shape changed");
    Require(checkedDelay.Body.Instructions.Count(i => i.Operand is IMethod m && m.FullName == sceneRuntimeLoad.FullName) == 1, "Scene wrapper missing after write");
    Require(!checkedDelay.Body.Instructions.Any(i => i.Operand is IMethod m && m.FullName == "System.Void UnityEngine.SceneManagement.SceneManager::LoadScene(System.String)"), "Original final scene load remains in patched iterator");
}
File.Move(temporary, destination, true);
string runtimeDestination = Path.Combine(output, "ClosingShift.Android.dll");
if (!String.Equals(Path.GetFullPath(args[2]), runtimeDestination, StringComparison.OrdinalIgnoreCase)) File.Copy(args[2], runtimeDestination, true);
foreach (var item in expected) Require(Hash(Path.Combine(source, item.Key)) == item.Value, "Original input changed");
File.WriteAllLines(Path.Combine(output, "managed-patches.txt"), patches.Append("Output SHA256: " + Hash(destination)).Append("Runtime SHA256: " + Hash(runtimeDestination)));
File.WriteAllText(Path.Combine(output,"controls-manifest.json"),JsonSerializer.Serialize(new{source_game_sha256=expected["Assembly-CSharp.dll"],game_sha256=Hash(destination),shim_sha256=Hash(runtimeDestination),patch_groups=patches.Count,original_input_calls=320,
    original_definition_counts_and_method_owners_preserved=true,method_pointer_table_rows=0,unrelated_method_bodies_verified=originalBodies.Count-changed.Count,
    android_decorative_effects_off=true,graphics_policy_call_sites=graphicsCalls+1,
    firstpass_not_in_delivery=true,device_controller_hardware_verified=false,app_or_device_modified=false,patches},new JsonSerializerOptions{WriteIndented=true}));
Console.WriteLine("PASS: strict preimages, " + patches.Count + " patch groups, " + inputChanges + " Input call sites, output structural self-check.");

static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static List<(uint Token, int Index, string From, string To, OpCode Before, object Operand)> PatchRenderCalls(ModuleDef game, ModuleDef runtime, ISet<uint> changed)
{
    var importer = new Importer(game);
    var component = runtime.GetTypes().Single(t => t.FullName == "ClosingShift.Android.AndroidRenderScale");
    var mobileAssembly = runtime.GetAssemblyRefs().Single(a => a.Name == "ClosingShift.MobileRender");
    var mobile = new TypeRefUser(game, "ClosingShift.Android", "MobileRenderSettings", new AssemblyRefUser(mobileAssembly));
    var calls = new List<(uint Token, int Index, string From, string To, OpCode Before, object Operand)>();
    var counts = new Dictionary<string, int>();
    foreach (var method in game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody))
    for (int i = 0; i < method.Body.Instructions.Count; ++i)
    {
        var instruction = method.Body.Instructions[i];
        if (instruction.Operand is not IMethod original) continue;
        IMethod? wrapper = null;
        string name = original.Name.String;
        if (original.DeclaringType.FullName == "UnityEngine.Camera" && new[] { "ScreenPointToRay", "ScreenToViewportPoint", "ScreenToWorldPoint", "WorldToScreenPoint", "ViewportToScreenPoint" }.Contains(name))
        {
            Require(original.MethodSig.HasThis && original.MethodSig.Params.Count == 1 && original.MethodSig.Params[0].FullName == "UnityEngine.Vector3", "Unexpected camera pixel overload: " + original.FullName);
            wrapper = importer.Import(component.Methods.Single(m => m.Name == name && m.IsPublic && m.IsStatic));
        }
        else if (original.FullName == "System.Void UnityEngine.Screen::SetResolution(System.Int32,System.Int32,System.Boolean)")
            wrapper = new MemberRefUser(game, "SetResolution", original.MethodSig.Clone(), mobile);
        else if (original.FullName == "System.Void UnityEngine.Screen::set_fullScreen(System.Boolean)")
            wrapper = new MemberRefUser(game, "SetFullScreen", original.MethodSig.Clone(), mobile);
        else if (method.MDToken.Raw == 0x0600038A && original.FullName == "System.Void UnityEngine.UI.Dropdown::set_value(System.Int32)" &&
            i >= 2 && method.Body.Instructions[i - 2].Operand is IField dropdownField && dropdownField.FullName == "UnityEngine.UI.Dropdown UICustomOptions::ResolutionDropdown")
        {
            Require(method.Body.Instructions[i - 1].IsLdcI4() && method.Body.Instructions[i - 1].GetLdcI4Value() == 0, "Legacy Reset resolution index changed");
            wrapper = importer.Import(runtime.GetTypes().Single(t => t.FullName == "ClosingShift.Android.AndroidGraphics").Methods.Single(m => m.Name == "SetLegacyResolutionSelection"));
            name = "reset-resolution-selection";
        }
        if (wrapper == null) continue;
        Require(instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt, "Unexpected render call opcode");
        calls.Add((method.MDToken.Raw, i, original.FullName, wrapper.FullName, instruction.OpCode, original));
        counts[name] = counts.TryGetValue(name, out int count) ? count + 1 : 1;
        instruction.OpCode = OpCodes.Call; instruction.Operand = wrapper;
        changed.Add(method.MDToken.Raw);
    }
    Require(calls.Count == 16 && counts.Count == 5 && counts["ScreenPointToRay"] == 5 && counts["WorldToScreenPoint"] == 2 && counts["SetResolution"] == 6 && counts["set_fullScreen"] == 2 && counts["reset-resolution-selection"] == 1, "Render/fullscreen/reset call coverage changed");
    var cameraTokens = calls.Where(c => c.From.Contains("UnityEngine.Camera::")).Select(c => c.Token).OrderBy(t => t);
    Require(cameraTokens.SequenceEqual(new uint[] { 0x0600013B, 0x060002A8, 0x060002B0, 0x0600040C, 0x06000E4C, 0x06000E4F, 0x06000E5A }), "Camera pixel caller identities changed");
    return calls;
}

static void VerifyRenderCalls(ModuleDef game)
{
    var methods = game.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody).SelectMany(m => m.Body.Instructions).Select(i => i.Operand).OfType<IMethod>().ToArray();
    Require(methods.Count(m => m.DeclaringType.FullName == "ClosingShift.Android.AndroidRenderScale") == 7, "Camera render wrapper count changed after write");
    Require(methods.Count(m => m.FullName == "System.Void ClosingShift.Android.MobileRenderSettings::SetResolution(System.Int32,System.Int32,System.Boolean)") == 6, "Fullscreen resolution guard count changed after write");
    Require(methods.Count(m => m.FullName == "System.Void ClosingShift.Android.MobileRenderSettings::SetFullScreen(System.Boolean)") == 2, "Fullscreen setter guard count changed after write");
    Require(methods.Count(m => m.FullName == "System.Void ClosingShift.Android.AndroidGraphics::SetLegacyResolutionSelection(UnityEngine.UI.Dropdown,System.Int32)") == 1, "Legacy Reset dropdown guard count changed after write");
    Require(!methods.Any(m => m.FullName == "System.Void UnityEngine.Screen::SetResolution(System.Int32,System.Int32,System.Boolean)" || m.FullName == "System.Void UnityEngine.Screen::set_fullScreen(System.Boolean)"), "Unguarded Android output write remains");
}

static string BodyHash(MethodDef method)
{
    if(!method.HasBody)return "no-body";
    var body=method.Body;
    string Operand(object? value)=>value switch{null=>"",Instruction instruction=>"i:"+body.Instructions.IndexOf(instruction),IList<Instruction> list=>String.Join(",",list.Select(i=>body.Instructions.IndexOf(i))),Local local=>"l:"+local.Index,Parameter parameter=>"p:"+parameter.Index,IFullName full=>full.FullName,string text=>"s:"+text,_=>value.ToString()??""};
    string text=String.Join("\n",body.Instructions.Select(i=>i.OpCode.Code+" "+Operand(i.Operand)))+"\nLOCALS:"+String.Join(",",body.Variables.Select(l=>l.Type.FullName))+"\nEH:"+String.Join(";",body.ExceptionHandlers.Select(e=>e.HandlerType+":"+Operand(e.TryStart)+":"+Operand(e.TryEnd)+":"+Operand(e.HandlerStart)+":"+Operand(e.HandlerEnd)+":"+Operand(e.FilterStart)+":"+e.CatchType?.FullName));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
}
static void VerifyMetadata(string original,string output)
{
    using var first=new PEReader(File.OpenRead(original));using var second=new PEReader(File.OpenRead(output));
    var a=first.GetMetadataReader();var b=second.GetMetadataReader();
    foreach(TableIndex table in new[]{TableIndex.TypeDef,TableIndex.MethodDef,TableIndex.Field,TableIndex.Param,TableIndex.MethodPtr,TableIndex.FieldPtr,TableIndex.EventPtr,TableIndex.PropertyPtr})
        Require(a.GetTableRowCount(table)==b.GetTableRowCount(table),"Original metadata table row count changed: "+table);
    Require(b.GetTableRowCount(TableIndex.MethodPtr)==0,"MethodPtr table is forbidden for Unity Mono/AOT compatibility");
    foreach(var handle in a.TypeDefinitions){var oldType=a.GetTypeDefinition(handle);var newType=b.GetTypeDefinition(handle);Require(oldType.GetMethods().Select(x=>MetadataTokens.GetToken(x)).SequenceEqual(newType.GetMethods().Select(x=>MetadataTokens.GetToken(x))),"Original type method ownership changed");Require(oldType.GetFields().Select(x=>MetadataTokens.GetToken(x)).SequenceEqual(newType.GetFields().Select(x=>MetadataTokens.GetToken(x))),"Original type field ownership changed");}
}
