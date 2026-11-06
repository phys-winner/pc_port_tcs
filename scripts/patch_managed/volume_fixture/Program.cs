using System;
using ClosingShift.Android;
using Aura2API;
using HorizonBasedAmbientOcclusion;
using UnityEngine;

System.Collections.Generic.List<string> Proofs()=>Debug.messages.FindAll(m=>m.StartsWith("PORT_VOLUME status="));
int checks=0;
void Check(bool value,string message){if(!value)throw new Exception(message);++checks;}
void Observe(MonoBehaviour effect,string type){AndroidVolumetrics.Observe(effect,"Aura2API."+type);}
AuraCamera Effect(Camera camera,string name){return new AuraCamera{camera=camera,gameObject=camera.gameObject,name=name};}
var main=new Camera{name="Main"};var aux=new Camera{name="Aux"};
var volume=Effect(main,"Volume");var auxiliary=Effect(aux,"AuxVolume");
var light=new AuraLight{name="Light"};
Observe(light,"AuraLight");Observe(volume,"AuraCamera");Observe(auxiliary,"AuraCamera");Observe(volume,"AuraCamera");
Check(volume.componentReads==1,"Repeated observation must cache the camera only once");
AndroidVolumetrics.Enforce(main,true,3);
Check(!volume.enabled&&!auxiliary.enabled&&!light.enabled,"Low disables camera and light collectors");
Check(Lifecycle.events[0]=="Light:False"&&Lifecycle.events[1]=="Volume:False","Collectors unregister before the camera is disabled");
Lifecycle.events.Clear();
bool active=AndroidVolumetrics.Enforce(main,true,0);
Check(active&&volume.enabled&&light.enabled&&!auxiliary.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Only main gameplay volume is active and requests Depth alone");
Check(Lifecycle.events.Count==2&&Lifecycle.events[0]=="Light:True"&&Lifecycle.events[1]=="Volume:True","Collectors register before camera initialization");
var quality=volume.frustumSettings.qualitySettings;
Check(quality.frustumGridResolution.x==64&&quality.frustumGridResolution.y==40&&quality.frustumGridResolution.z==64&&quality.texture3DFiltering==Texture3DFiltering.Linear&&quality.farClipPlaneDistance==123.5f&&quality.temporalReprojectionFactor==.81f&&quality.enableTemporalReprojection&&quality.enableVolumes,"Enable policy must preserve the asset's grid, filter and other quality settings");
foreach(int profile in new[]{1,2,5,0})Check(AndroidVolumetrics.Enforce(main,true,profile),"Every higher and Custom profile enables volume");
Check(volume.enableAttempts==1&&volume.componentReads==2&&volume.frameReads==0&&volume.initializedReads==0,"Eligible profile changes neither repeat the policy/Aura camera lookups nor probe immediately");
Check(Debug.messages.FindAll(m=>m.StartsWith("PORT_VOLUME configure buffers=8 occlusion=True temporal=True")).Count==1,"Configuration capability log is immediate and not repeated by stable eligible profiles");
Time.now=.99f;AndroidVolumetrics.Enforce(main,true,0);
Check(volume.frameReads==0&&Proofs().Count==0,"Proof waits one second before its first sample");
Time.now=1.05f;volume.frames=45;AndroidVolumetrics.Enforce(main,true,0);
Check(volume.frameReads==1&&volume.initializedReads==1&&Proofs().Count==0,"Initialization alone cannot produce a positive proof");
Time.now=1.9f;volume.frames=90;AndroidVolumetrics.Enforce(main,true,0);
Check(volume.frameReads==1,"The second sample is at least one second after the first");
Time.now=2.05f;volume.frames=105;AndroidVolumetrics.Enforce(main,true,0);
Check(Proofs().Count==1&&Proofs()[0].Contains("status=PASS reason=frame_advanced camera=Main grid=64x40x64 filter=Linear start=45 end=105 initialized=True"),"Proof records actual camera, asset settings and advancing compute frames");
int clockReads=Time.reads,frameReads=volume.frameReads,initReads=volume.initializedReads;
Time.now=100;for(int i=0;i<5;++i)AndroidVolumetrics.Enforce(main,true,0);
Check(Time.reads==clockReads&&volume.frameReads==frameReads&&volume.initializedReads==initReads&&Proofs().Count==1,"Completed proof performs no ongoing timing, frame or initialization reads");
Check(Object.ReferenceEquals(quality,volume.frustumSettings.qualitySettings),"Proof must not replace or clone the quality asset");

active=AndroidVolumetrics.Enforce(main,true,4);
Check(!active&&!volume.enabled&&!light.enabled&&main.depthTextureMode==DepthTextureMode.None,"Minimum returns inactive and releases owned depth");
AndroidVolumetrics.Enforce(main,true,2);Time.now=101;volume.frames=105;AndroidVolumetrics.Enforce(main,true,2);
Time.now=102;AndroidVolumetrics.Enforce(main,true,2);
Check(Proofs().Count==2&&Proofs()[1].Contains("status=FAIL reason=frame_stalled")&&Proofs()[1].Contains("start=105 end=105"),"Swallowed compute exceptions cannot pass with a frozen FrameId");
frameReads=volume.frameReads;Time.now=110;AndroidVolumetrics.Enforce(main,true,2);
Check(volume.frameReads==frameReads&&Proofs().Count==2,"Failed proof also stops sampling");
AndroidVolumetrics.Enforce(main,true,3);volume.failInitialization=true;AndroidVolumetrics.Enforce(main,true,2);
Time.now=111;AndroidVolumetrics.Enforce(main,true,2);
Check(Proofs().Count==3&&Proofs()[2].Contains("status=FAIL reason=not_initialized"),"Uninitialized camera explicitly fails rather than reporting activity");
AndroidVolumetrics.Enforce(main,true,3);volume.failInitialization=false;AndroidVolumetrics.Enforce(main,true,2);
Time.now=112;AndroidVolumetrics.Enforce(main,true,2);volume.initialized=false;volume.frames=120;
Time.now=113;AndroidVolumetrics.Enforce(main,true,2);
Check(Proofs().Count==4&&Proofs()[3].Contains("status=FAIL reason=lost_initialization"),"A changed frame count cannot pass if initialization was lost");

AndroidVolumetrics.Enforce(main,true,3);volume.rejectEnable=true;int attempts=volume.enableAttempts;
active=AndroidVolumetrics.Enforce(main,true,2);frameReads=volume.frameReads;
for(int i=0;i<5;++i)AndroidVolumetrics.Enforce(main,true,2);
Check(!active&&!volume.enabled&&volume.enableAttempts==attempts+1&&volume.frameReads==frameReads&&main.depthTextureMode==DepthTextureMode.None,"Unsupported OnEnable gets one attempt and no depth or frame probes per eligible transition");
Check(Proofs().Count==5&&Proofs()[4].Contains("status=FAIL reason=self_disabled"),"A self-disabling unsupported camera emits one explicit failure");
volume.rejectEnable=false;AndroidVolumetrics.Enforce(main,true,3);AndroidVolumetrics.Enforce(main,true,5);
Check(volume.enabled&&volume.enableAttempts==attempts+2,"A later eligibility transition can attempt initialization again");
volume.enabled=false;AndroidVolumetrics.Enforce(main,true,5);
Check(!volume.enabled&&main.depthTextureMode==DepthTextureMode.None&&volume.enableAttempts==attempts+2&&Proofs().Count==6,"External self-disable releases depth without retrying or hiding failure");

AndroidVolumetrics.Enforce(main,true,3);Lifecycle.events.Clear();AndroidVolumetrics.BeginScene();
Check(volume.enabled&&light.enabled&&Lifecycle.events[0]=="Light:True"&&Lifecycle.events[1]=="Volume:True","Same-scene reset restores authored states in dependency order before recapture");
Observe(light,"AuraLight");Observe(volume,"AuraCamera");Observe(auxiliary,"AuraCamera");
main.depthTextureMode=DepthTextureMode.None; // ApplyCameras clears the active main before the effect policies.
AndroidVolumetrics.Enforce(main,true,3);active=AndroidVolumetrics.Enforce(main,true,2);
Check(active&&volume.enabled&&light.enabled,"Recapture must not mistake a policy-disabled source for authored disabled");
main.gameObject.activeInHierarchy=false;
Check(!AndroidVolumetrics.Enforce(main,true,2)&&!volume.enabled&&!light.enabled&&!main.gameObject.activeInHierarchy,"Inactive main stays inactive and disables all volume work");
main.gameObject.activeInHierarchy=true;AndroidVolumetrics.Enforce(main,true,2);
light.gameObject.activeInHierarchy=false;AndroidVolumetrics.Enforce(main,true,2);
Check(!light.enabled&&!light.gameObject.activeInHierarchy&&volume.enabled,"An inactive light object stays inactive without disabling a valid main camera");
light.gameObject.activeInHierarchy=true;AndroidVolumetrics.Enforce(main,true,2);
Check(light.enabled,"A newly active authored light re-registers");
aux.depthTextureMode=DepthTextureMode.None;active=AndroidVolumetrics.Enforce(aux,true,2);
Check(active&&auxiliary.enabled&&!volume.enabled&&main.depthTextureMode==DepthTextureMode.None&&aux.depthTextureMode==DepthTextureMode.Depth,"Main camera replacement moves volume and its owned depth request");
Check(!AndroidVolumetrics.Enforce(null,true,2)&&!auxiliary.enabled&&!light.enabled,"Losing the main camera disables volume and lights");
main.gameObject.scene=new Scene{buildIndex=0};
Check(!AndroidVolumetrics.Enforce(main,true,2)&&!volume.enabled,"Menu never enables volume");
main.gameObject.scene=new Scene{buildIndex=1};main.actualRenderingPath=RenderingPath.Forward;
Check(!AndroidVolumetrics.Enforce(main,true,2),"Actual Forward fallback overrides requested Deferred");
main.actualRenderingPath=RenderingPath.DeferredShading;
Check(!AndroidVolumetrics.Enforce(main,false,2),"GPU policy fallback disables volume");
Check(!AndroidVolumetrics.Enforce(main,true,6),"Only the explicit supported higher profiles enable volume");
main.enabled=false;Check(!AndroidVolumetrics.Enforce(main,true,2),"Disabled main camera cannot enable volume");main.enabled=true;

main.depthTextureMode=DepthTextureMode.Depth|DepthTextureMode.DepthNormals|DepthTextureMode.MotionVectors;
AndroidVolumetrics.Enforce(main,true,2);AndroidVolumetrics.Enforce(main,true,3);
Check(main.depthTextureMode==(DepthTextureMode.Depth|DepthTextureMode.DepthNormals|DepthTextureMode.MotionVectors),"Preexisting depth flags, including AO's Depth, survive volume disable");
main.depthTextureMode=DepthTextureMode.MotionVectors;
AndroidVolumetrics.Enforce(main,true,2);
Check(main.depthTextureMode==(DepthTextureMode.Depth|DepthTextureMode.MotionVectors),"Volume adds only Depth, including when Aura Initialize adds it first");
AndroidVolumetrics.Enforce(main,true,3);
Check(main.depthTextureMode==DepthTextureMode.MotionVectors,"Release removes only the owned Depth bit");

var ao=new HBAO{camera=main,gameObject=main.gameObject,enabled=false};
AndroidAmbientOcclusion.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");main.depthTextureMode=DepthTextureMode.None;
AndroidVolumetrics.Enforce(main,true,2);AndroidAmbientOcclusion.Enforce(main,true,2);
main.depthTextureMode=DepthTextureMode.None;
bool aoActive=AndroidAmbientOcclusion.Enforce(main,true,3);bool volumeActive=AndroidVolumetrics.Enforce(main,true,3);
if(aoActive||volumeActive)main.depthTextureMode|=DepthTextureMode.Depth;
Check(aoActive&&!volumeActive&&main.depthTextureMode==DepthTextureMode.Depth,"Central bool results retain AO Depth when volume releases an overlapping claim");
main.depthTextureMode=DepthTextureMode.None;volumeActive=AndroidVolumetrics.Enforce(main,true,2);
ao.enabled=false;aoActive=AndroidAmbientOcclusion.Enforce(main,true,2);
if(aoActive||volumeActive)main.depthTextureMode|=DepthTextureMode.Depth;
Check(!aoActive&&volumeActive&&main.depthTextureMode==DepthTextureMode.Depth,"Central bool results retain volume Depth when AO self-disables");
int proofLogs=Proofs().Count;AndroidVolumetrics.Enforce(main,true,3);
Check(Proofs().Count==proofLogs+1&&Proofs()[proofLogs].Contains("status=CANCELLED reason=policy_disabled"),"A fast legitimate profile change cancels unfinished proof without reporting compute failure");
AndroidVolumetrics.Enforce(main,true,2);proofLogs=Proofs().Count;
AndroidAmbientOcclusion.BeginScene();AndroidVolumetrics.BeginScene();
Check(Proofs().Count==proofLogs+1&&Proofs()[proofLogs].Contains("status=CANCELLED reason=scene_reset"),"A scene reset cancels unfinished proof once without reporting compute failure");

var sourceCamera=new Camera{name="SourceDisabled"};var sourceDisabled=Effect(sourceCamera,"DisabledVolume");sourceDisabled.enabled=false;
var sourceLight=new AuraLight{name="DisabledLight",enabled=false};
Observe(sourceDisabled,"AuraCamera");Observe(sourceLight,"AuraLight");
Check(!AndroidVolumetrics.Enforce(sourceCamera,true,2)&&!sourceDisabled.enabled&&!sourceLight.enabled,"Authored-disabled camera and light remain disabled");
AndroidVolumetrics.BeginScene();Observe(sourceDisabled,"AuraCamera");Observe(sourceLight,"AuraLight");
Check(!AndroidVolumetrics.Enforce(sourceCamera,true,2)&&!sourceDisabled.enabled&&!sourceLight.enabled,"Repeated scene reset preserves authored-disabled states");
var activeVolume=Effect(main,"ActiveVolume");Observe(activeVolume,"AuraCamera");
Check(AndroidVolumetrics.Enforce(main,true,2)&&activeVolume.enabled&&!sourceDisabled.enabled&&!sourceLight.enabled,"An active main volume must not activate authored-disabled light collectors or another camera");
var unsupportedLight=new AuraLight{name="UnsupportedLight"};Observe(unsupportedLight,"AuraLight");
AndroidVolumetrics.Enforce(main,true,3);unsupportedLight.rejectEnable=true;AndroidVolumetrics.Enforce(main,true,2);
for(int i=0;i<5;++i)AndroidVolumetrics.Enforce(main,true,2);
Check(!unsupportedLight.enabled&&unsupportedLight.enableAttempts==1,"Unsupported light collectors also get only one enable attempt per eligibility transition");
var missing=new AuraCamera();Observe(missing,"AuraCamera");AndroidVolumetrics.Enforce(main,true,2);
Check(!missing.enabled,"A volume without a cached camera fails closed");
AndroidVolumetrics.Observe(null,"Aura2API.AuraCamera");Observe(new MonoBehaviour(),"AuraCamera");
var unrelated=new AuraCamera();Observe(unrelated,"Unknown");AndroidVolumetrics.Enforce(main,true,2);
Check(unrelated.enabled&&unrelated.componentReads==0,"Unrelated and invalid observed types stay untouched");
AndroidVolumetrics.BeginScene();MaliG715Guard();ShadowLifecycle.Run(Check);Application.platform=RuntimePlatform.WindowsPlayer;
var desktop=Effect(main,"Desktop");var desktopLight=new AuraLight();int logs=Proofs().Count;
main.depthTextureMode=DepthTextureMode.MotionVectors;Observe(desktop,"AuraCamera");Observe(desktopLight,"AuraLight");
active=AndroidVolumetrics.Enforce(main,true,2);AndroidVolumetrics.BeginScene();
Check(!active&&desktop.enabled&&desktopLight.enabled&&desktop.componentReads==0&&Proofs().Count==logs&&main.depthTextureMode==DepthTextureMode.MotionVectors,"Desktop is unchanged and reports no managed mobile depth claim");
Console.WriteLine("MOBILE_VOLUME_POLICY_PASS "+checks+" assertions against actual volume/AO helpers; no device");

void MaliG715Guard()
{
    SystemInfo.graphicsDeviceName="Mali-G715";QualitySettings.shadows=ShadowQuality.All;
    var camera=new Camera{name="GuardMain",depthTextureMode=DepthTextureMode.MotionVectors};
    var effect=Effect(camera,"GuardVolume");var other=Effect(new Camera{name="GuardAux"},"GuardAuxVolume");
    var lamp=new Light{shadows=LightShadows.Soft,type=LightType.Directional};
    var collector=new AuraLight{name="GuardLight",light=lamp,gameObject=lamp.gameObject};lamp.aura=collector;collector.InitializeForTest();
    effect.SimulateRender();Check(effect.frames==1,"Render-boundary negative control advances an enabled initialized Aura camera");effect.frames=0;
    Observe(effect,"AuraCamera");Observe(other,"AuraCamera");Observe(collector,"AuraLight");
    int GuardLogs()=>Debug.messages.FindAll(m=>m.Contains("reason=mali_g715_gpu_fence_guard")).Count;
    int logs=GuardLogs();Lifecycle.events.Clear();var sourceQuality=effect.frustumSettings.qualitySettings;
    Check(!AndroidVolumetrics.Enforce(camera,true,0),"Mali-G715 returns inactive on the first observed High frame");
    effect.SimulateRender();other.SimulateRender();
    Check(!effect.enabled&&!other.enabled&&!collector.enabled&&effect.frames==0&&other.frames==0&&Lifecycle.directionalShadowCount==0,"First enforcement excludes camera dispatch and unregisters every collector before rendering");
    Check(Lifecycle.events[0]=="GuardLight:False"&&Lifecycle.events[1]=="GuardVolume:False"&&effect.enableAttempts==0,"The GPU guard uses the existing collector-before-camera teardown without an enable attempt");
    Check(camera.depthTextureMode==DepthTextureMode.MotionVectors&&GuardLogs()==logs+1&&Debug.messages.Exists(m=>m=="PORT_VOLUME status=DISABLED reason=mali_g715_gpu_fence_guard gpu=Mali-G715 camera=GuardMain frame=0 cameraEnabled=False"),"The main-only guard log records frame zero after disable and preserves unrelated depth");
    Check(effect.frameReads==1&&effect.initializedReads==0&&other.frameReads==0,"The bounded log reads only the main camera's plain FrameId getter");
    SystemInfo.graphicsDeviceName="mALi-g715";int events=Lifecycle.events.Count;
    foreach(int selected in new[]{0,1,2,3,4,5})for(int repeat=0;repeat<2;++repeat)
    {
        Check(!AndroidVolumetrics.Enforce(camera,true,selected),"All six profiles and repeated case-insensitive G715 enforcement remain disabled");
        effect.SimulateRender();
    }
    Check(effect.frames==0&&effect.enableAttempts==0&&collector.enableAttempts==0&&Lifecycle.events.Count==events&&GuardLogs()==logs+1&&effect.frameReads==1,"Stable blocked frames never restart effects, dispatch, or repeat logging/getter work");
    Check(Object.ReferenceEquals(sourceQuality,effect.frustumSettings.qualitySettings)&&sourceQuality.frustumGridResolution.x==64&&sourceQuality.texture3DFiltering==Texture3DFiltering.Linear&&sourceQuality.enableOcclusionCulling&&sourceQuality.enableTemporalReprojection,"GPU eligibility leaves authored Aura settings untouched");
    AndroidVolumetrics.BeginScene();Observe(effect,"AuraCamera");Observe(other,"AuraCamera");Observe(collector,"AuraLight");
    Check(effect.enabled&&collector.enabled,"Scene rescan retains the existing authored-state recapture contract");
    AndroidVolumetrics.Enforce(camera,true,2);effect.SimulateRender();
    Check(!effect.enabled&&!collector.enabled&&effect.frames==0&&GuardLogs()==logs+2&&Lifecycle.directionalShadowCount==0,"Synchronous rescan enforcement blocks rendering again and logs once for the new observation");
    int attempts=collector.enableAttempts;
    foreach(ShadowQuality global in new[]{ShadowQuality.Disable,ShadowQuality.All})
    {
        AndroidVolumetrics.BeforeQualityChange();QualitySettings.shadows=global;
        AndroidVolumetrics.SetLightShadows(lamp,global==ShadowQuality.Disable?LightShadows.Hard:LightShadows.Soft);
        AndroidVolumetrics.Enforce(camera,true,5);collector.SimulatePreCull();collector.SimulatePreRender();
        Check(!collector.enabled&&!effect.enabled&&collector.enableAttempts==attempts&&Lifecycle.directionalShadowCount==0&&!collector.shadowBuffer&&!collector.shadowMaterial,"Real local/global shadow transactions retain surface shadows without reviving blocked collectors");
    }
    Check(lamp.shadows==LightShadows.Soft&&lamp.enabled&&QualitySettings.shadows==ShadowQuality.All,"The guard leaves the real light enabled with its requested shadow mode");
    foreach(string gpu in new[]{"Adreno (TM) 735","Mali-G76","Mali-G715X","prefix Mali-G715",null})
    {
        SystemInfo.graphicsDeviceName=gpu;AndroidVolumetrics.Enforce(camera,true,3);
        Check(AndroidVolumetrics.Enforce(camera,true,2)&&effect.enabled&&collector.enabled,"Only the exact G715 name is blocked; other helper eligibility is unchanged");
    }
    SystemInfo.graphicsDeviceName="Mali-G715";Application.platform=RuntimePlatform.WindowsPlayer;events=Lifecycle.events.Count;
    Check(!AndroidVolumetrics.Enforce(camera,true,2)&&effect.enabled&&collector.enabled&&Lifecycle.events.Count==events&&GuardLogs()==logs+2,"Desktop with the same GPU name remains untouched");
    Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Adreno (TM) 735";
    AndroidVolumetrics.Enforce(camera,true,3);AndroidVolumetrics.BeginScene();collector.enabled=false;effect.enabled=false;other.enabled=false;
    Check(Lifecycle.directionalShadowCount==0,"GPU guard fixture leaves no directional registration imbalance");
}
