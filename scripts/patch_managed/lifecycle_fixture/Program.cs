using System.Reflection;
using ClosingShift.Android;
using UnityEngine;

int checks=0;
void Check(bool value,string name){if(!value)throw new Exception(name);++checks;}
var owner=new AndroidInputLifetime();
void Call(string name,params object[] arguments){AndroidInput.events.Clear();typeof(AndroidInputLifetime).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,arguments);}
Call("Update");Check(AndroidInput.events.SequenceEqual(new[]{"reset","frame"}),"Initial geometry cancels before polling");
Call("Update");Check(AndroidInput.events.SequenceEqual(new[]{"touch","frame"}),"Stable geometry retains the gesture");
Screen.width=2300;Call("Update");Check(AndroidInput.events.SequenceEqual(new[]{"reset","touch","frame"}),"Resize cancels before captured touch dispatch");
Screen.safeArea=new Rect{x=90};Call("Update");Check(AndroidInput.events[0]=="reset","Inset change cancels the old gesture");
Input.touches=new[]{new Touch{phase=TouchPhase.Canceled}};Call("Update");Check(AndroidInput.events[0]=="reset","CANCEL clears pending Unity click before input polling");
Input.touches=new[]{new Touch{phase=TouchPhase.Ended}};Call("Update");Check(!AndroidInput.events.Contains("reset"),"Ordinary UP preserves native click handling");
Input.touches=Array.Empty<Touch>();
Call("OnApplicationFocus",false);Check(AndroidInput.events.SequenceEqual(new[]{"reset"}),"Focus loss releases input");
Call("OnApplicationFocus",true);Check(AndroidInput.events.Count==0,"Focus return does not synthesize input");
Call("OnApplicationPause",true);Check(AndroidInput.events.SequenceEqual(new[]{"reset"}),"Pause releases input");
Call("OnDestroy");Check(AndroidInput.events.SequenceEqual(new[]{"reset"}),"Destroy releases input");
string directory=Path.Combine(AppContext.BaseDirectory,"session-fixture-"+Guid.NewGuid().ToString("N"));
Application.persistentDataPath=directory;
AndroidSessionLog.Initialize();Application.Log("native UI error",LogType.Exception,"original stack");
string ReadLog(){using(var stream=new FileStream(Path.Combine(directory,"logs/closing-shift.log"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite))using(var reader=new StreamReader(stream))return reader.ReadToEnd();}
string log=ReadLog();
Check(log.Contains("fixture GPU")&&log.Contains("native UI error")&&log.Contains("original stack"),"Persistent log flushes engine header, errors and stack");
Parallel.For(0,30,i=>Application.Log("parallel-"+i));
Check(ReadLog().Split('\n',StringSplitOptions.RemoveEmptyEntries).Length==33,"Concurrent callbacks preserve complete log records");
Console.WriteLine("LIFECYCLE_LOG_PASS assertions="+checks);
