using System;
using ClosingShift.Android;
using HorizonBasedAmbientOcclusion;
using UnityEngine;

int count=0;
void Check(bool value,string message){if(!value)throw new Exception(message);++count;}
void Observe(HBAO effect){AndroidAmbientOcclusion.Observe(effect,"HorizonBasedAmbientOcclusion.HBAO");}
void Authored(HBAO effect)
{
    Check(effect.radius==1.37f && effect.intensity==2.42f && effect.maxDistance==71.3f && effect.distanceFalloff==12.7f &&
        effect.maxRadiusPixels==159.2f && effect.bias==.23f && effect.blurSharpness==4.1f &&
        effect.color.Equals(new Color(.1f,.2f,.3f,.4f)),"Author appearance and distance parameters changed");
}
var main=new Camera();var auxiliary=new Camera();
var effect=new HBAO{camera=main,enabled=false};var extra=new HBAO{camera=auxiliary};
Observe(effect);Observe(extra);Observe(effect);
Check(effect.componentReads==1,"Duplicate observation repeated camera lookup");
bool active=AndroidAmbientOcclusion.Enforce(main,true,3);
Check(active && effect.enabled && !extra.enabled,"Only active main gameplay camera should receive and report AO");
Check(effect.stage==HBAO.PipelineStage.BeforeImageEffectsOpaque && effect.quality==HBAO.Quality.Low &&
    effect.resolution==HBAO.Resolution.Half && effect.deinterleaving==HBAO.Deinterleaving.Disabled &&
    effect.normals==HBAO.PerPixelNormals.GBuffer && !effect.temporal && !effect.colorBleeding && effect.blur==HBAO.BlurType.Narrow,
    "Mobile AO profile must reuse deferred G-buffer normals without temporal or color bleeding");
Check(main.depthTextureMode==DepthTextureMode.Depth && auxiliary.depthTextureMode==DepthTextureMode.None,"Only main depth required");
Authored(effect);
int writes=effect.writes;
for(int i=0;i<10;++i)AndroidAmbientOcclusion.Enforce(main,true,0);
Check(effect.enableAttempts==1 && effect.writes==writes && effect.componentReads==1,"Steady frames must not reinitialize or search components");
active=AndroidAmbientOcclusion.Enforce(main,true,4);
Check(!active && !effect.enabled && main.depthTextureMode==DepthTextureMode.None,"Minimum must remove and report no AO or owned depth request");
AndroidAmbientOcclusion.Enforce(main,true,5);
Check(effect.enabled && effect.enableAttempts==2,"Custom after Minimum must reactivate AO");
Authored(effect);
AndroidAmbientOcclusion.Enforce(main,false,0);
Check(!effect.enabled && main.depthTextureMode==DepthTextureMode.None,"Renderer policy fallback must disable AO");
main.actualRenderingPath=RenderingPath.Forward;AndroidAmbientOcclusion.Enforce(main,true,0);
Check(!effect.enabled,"Actual Forward fallback overrides requested Deferred");
main.actualRenderingPath=RenderingPath.DeferredShading;main.gameObject.scene=new Scene{buildIndex=0};
AndroidAmbientOcclusion.Enforce(main,true,0);Check(!effect.enabled,"Menu must stay free of AO");
main.gameObject.scene=new Scene{buildIndex=1};main.enabled=false;
AndroidAmbientOcclusion.Enforce(main,true,0);Check(!effect.enabled,"Disabled main camera must not run AO");
main.enabled=true;main.gameObject.activeInHierarchy=false;
AndroidAmbientOcclusion.Enforce(main,true,0);Check(!effect.enabled,"Inactive main camera must not run AO");
main.gameObject.activeInHierarchy=true;AndroidAmbientOcclusion.Enforce(main,true,0);
Check(effect.enabled,"Gameplay reactivation must enable AO");
AndroidAmbientOcclusion.Enforce(auxiliary,true,1);
Check(!effect.enabled && extra.enabled && main.depthTextureMode==DepthTextureMode.None && auxiliary.depthTextureMode==DepthTextureMode.Depth,
    "Camera replacement must move AO and its depth request");
AndroidAmbientOcclusion.Enforce(null,true,1);
Check(!extra.enabled && auxiliary.depthTextureMode==DepthTextureMode.None,"No main camera must disable cached AO");
main.depthTextureMode=DepthTextureMode.Depth|DepthTextureMode.MotionVectors|DepthTextureMode.DepthNormals;
AndroidAmbientOcclusion.Enforce(main,true,3);AndroidAmbientOcclusion.Enforce(main,false,3);
Check(main.depthTextureMode==(DepthTextureMode.Depth|DepthTextureMode.MotionVectors|DepthTextureMode.DepthNormals),"Preexisting depth flags must survive disabling AO");
main.depthTextureMode=DepthTextureMode.MotionVectors;
AndroidAmbientOcclusion.Enforce(main,true,3);
Check(main.depthTextureMode==(DepthTextureMode.Depth|DepthTextureMode.MotionVectors),"AO must OR depth without replacing other requests");
main.depthTextureMode|=DepthTextureMode.DepthNormals;AndroidAmbientOcclusion.Enforce(main,true,4);
Check(main.depthTextureMode==(DepthTextureMode.MotionVectors|DepthTextureMode.DepthNormals),"Only AO's added Depth bit should be released");
main.depthTextureMode=DepthTextureMode.None;effect.rejectEnable=true;
active=AndroidAmbientOcclusion.Enforce(main,true,0);int failedAttempts=effect.enableAttempts;
for(int i=0;i<5;++i)AndroidAmbientOcclusion.Enforce(main,true,0);
Check(!active && !effect.enabled && effect.enableAttempts==failedAttempts && main.depthTextureMode==DepthTextureMode.None,"Unsupported HBAO must report inactive and not retry or request depth each frame");
effect.rejectEnable=false;AndroidAmbientOcclusion.Enforce(main,true,4);AndroidAmbientOcclusion.Enforce(main,true,0);
Check(effect.enabled && effect.enableAttempts==failedAttempts+1,"A new eligibility transition may retry initialization");
effect.enabled=false;AndroidAmbientOcclusion.Enforce(main,true,0);
Check(main.depthTextureMode==DepthTextureMode.None && !effect.enabled,"Self-disabled effect must release depth without retrying");
AndroidAmbientOcclusion.Enforce(main,true,4);AndroidAmbientOcclusion.Enforce(main,true,0);
AndroidAmbientOcclusion.BeginScene();Check(!effect.enabled && main.depthTextureMode==DepthTextureMode.None,"Scene reset must release old effect and owned depth");
int reads=effect.componentReads;AndroidAmbientOcclusion.Enforce(main,true,0);
Check(!effect.enabled && effect.componentReads==reads,"Cleared scene entries must not return without observation");
var missingCamera=new HBAO();Observe(missingCamera);AndroidAmbientOcclusion.Enforce(main,true,0);
Check(!missingCamera.enabled,"Missing attached camera must fail closed");
AndroidAmbientOcclusion.Observe(null,"HorizonBasedAmbientOcclusion.HBAO");
AndroidAmbientOcclusion.Observe(new MonoBehaviour(),"HorizonBasedAmbientOcclusion.HBAO");
var unrelated=new HBAO{camera=main};AndroidAmbientOcclusion.Observe(unrelated,"Different.Component");
AndroidAmbientOcclusion.Enforce(main,true,0);Check(unrelated.enabled && unrelated.writes==0,"Unrelated type must remain untouched");
AndroidAmbientOcclusion.BeginScene();Application.platform=RuntimePlatform.WindowsPlayer;
var desktop=new HBAO{camera=main};main.depthTextureMode=DepthTextureMode.MotionVectors;
Observe(desktop);AndroidAmbientOcclusion.Enforce(main,true,0);AndroidAmbientOcclusion.BeginScene();
Check(desktop.enabled && desktop.writes==0 && desktop.componentReads==0 && main.depthTextureMode==DepthTextureMode.MotionVectors,"Desktop baseline must remain untouched");
Authored(desktop);
Console.WriteLine("MOBILE_AO_POLICY_PASS "+count+" assertions against actual helper; no device");
