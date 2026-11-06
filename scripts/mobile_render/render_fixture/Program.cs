using System;
using System.Reflection;
using ClosingShift.Android;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

static class Program
{
    static int checks;
    static void RecipeTextCheck()
    {
        var root=new GameObject("Recipes List");var cold=new GameObject("Cold label");cold.transform.SetParent(root.transform,false);cold.transform.localPosition=new Vector3(1,2,3);cold.layer=4;
        var renderer=cold.AddComponent<MeshRenderer>();var text=cold.AddComponent<TMPro.TMP_Text>();text.isOverlay=true;
        var sharedFont=new Material();renderer.sharedMaterial=sharedFont;
        var fallback=new GameObject("TMP SubMesh [Cyrillic]");fallback.transform.SetParent(cold.transform,false);
        var fallbackRenderer=fallback.AddComponent<MeshRenderer>();fallbackRenderer.sharedMaterial=sharedFont;
        var submesh=fallback.AddComponent<TMPro.TMP_SubMesh>();
        var board=new GameObject("Board");board.layer=2;board.transform.SetParent(root.transform,false);board.AddComponent<MeshRenderer>();
        var component=root.AddComponent<AndroidRecipeText>();
        void Callback(string name){typeof(AndroidRecipeText).GetMethod(name,BindingFlags.Instance|BindingFlags.NonPublic).Invoke(component,null);}
        Callback("OnEnable");var world=World();world.cullingMask=-1;Camera.main=world;
        var post=world.gameObject.AddComponent<PostProcessLayer>();post.finalBlitToCameraTarget=false;
        RuntimeUtilities.copyMaterial.shader.isSupported=true;Time.unscaledTime+=2;MobileRenderSettings.SetRenderPercent(100);AndroidRenderScale.Enforce();
        Check(world.targetTexture!=null&&world.targetTexture.width==2400&&world.targetTexture.height==1080,"Native100 retains a full-size shared depth target while recipe protection is active");
        ClosingShift.Localization.RussianUiPresentation.RussianActive=true;Camera.FirePreCull(world);
        Check(!text.isOverlay&&renderer.sharedMaterial!=sharedFont&&renderer.sharedMaterial.GetFloat("unity_GUIZTestMode")==4,"Recipe base glyphs have local LessEqual depth instead of UI overlay state");
        Check(fallbackRenderer.sharedMaterial==submesh.material&&submesh.material.GetFloat("unity_GUIZTestMode")==4&&sharedFont.GetFloat("unity_GUIZTestMode")==0,"Cyrillic fallback glyphs get depth testing without modifying shared UI font material");
        var localMaterial=renderer.sharedMaterial;Camera.FirePreCull(world);
        Check(renderer.sharedMaterial==localMaterial,"Stable recipe frames reuse the TMP-owned material instance");
        submesh.material=new Material();fallbackRenderer.sharedMaterial=sharedFont;Camera.FirePreCull(world);
        Check(fallbackRenderer.sharedMaterial==submesh.material&&submesh.material.GetFloat("unity_GUIZTestMode")==4,"Regenerated fallback materials retain occlusion after locale or atlas changes");
        var overlay=(Camera)typeof(AndroidRecipeText).GetField("overlay",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(component);
        Check(!post.finalBlitToCameraTarget,"Recipes retain the authored PPS mode and all subsequent image effects");
        var depthCamera=(Camera)typeof(AndroidRecipeText).GetField("depthCamera",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(component);
        var depthTarget=depthCamera.targetTexture;
        Check(depthTarget!=world.targetTexture&&depthTarget.format==RenderTextureFormat.Depth&&depthTarget.depth==24&&depthTarget.width==world.targetTexture.width&&depthTarget.height==world.targetTexture.height,"Recipe occlusion has its own depth-only target at the current world resolution");
        Check(depthCamera.enabled&&depthCamera.depth>world.depth&&depthCamera.depth<overlay.depth&&depthCamera.cullingMask==world.cullingMask&&!depthCamera.allowHDR&&!depthCamera.allowMSAA&&depthCamera.replacementShader!=null,"A depth replacement pass runs between world effects and unfiltered text");
        Check(overlay.enabled&&overlay.targetTexture==world.targetTexture&&overlay.cullingMask==(1<<31)&&cold.layer==31&&(world.cullingMask&(1<<31))==0,"A separate camera renders only recipes into the shared world output");
        Check(board.layer==2,"The physical recipe board stays in the normal world pass with its lighting and filters");
        Check(overlay.clearFlags==CameraClearFlags.Nothing&&overlay.depth>world.depth&&overlay.depth<world.depth+.01f&&overlay.depthTextureMode==DepthTextureMode.None&&!overlay.forceIntoRenderTexture,"Recipes preserve world depth and render after world filters before native presentation");
        var oldTarget=world.targetTexture;MobileRenderSettings.SetRenderPercent(67);AndroidRenderScale.Enforce();
        Check(oldTarget.released&&!overlay.enabled&&overlay.targetTexture==null,"Resolution change detaches the recipe camera before releasing its shared buffers");
        Camera.FirePreCull(world);Check(overlay.enabled&&overlay.targetTexture==world.targetTexture&&overlay.targetTexture!=oldTarget,"The recipe camera binds the replacement buffers on the next world frame");
        Check(depthTarget.released,"Resolution change releases the old recipe depth target");
        MobileRenderSettings.SetRenderPercent(100);AndroidRenderScale.Enforce();Camera.FirePreCull(world);
        AndroidRoomCulling.occluded=true;Camera.FirePreCull(world);
        Check(!overlay.enabled&&!depthCamera.enabled&&cold.layer==4&&world.cullingMask==-1,"A board proven hidden by opaque room triangles needs no recipe cameras");
        AndroidRoomCulling.occluded=false;Camera.FirePreCull(world);
        Check(overlay.enabled&&depthCamera.enabled,"Moving past a wall restores unfiltered recipes on the same camera frame");
        GeometryUtility.visible=false;Camera.FirePreCull(world);
        Check(!overlay.enabled&&!depthCamera.enabled&&cold.layer==4&&world.cullingMask==-1,"A board outside the frustum costs no extra camera rendering and restores ordinary layers");
        GeometryUtility.visible=true;AndroidRecipeText.ReleaseTarget(world);RenderTexture.failNext=true;Camera.FirePreCull(world);
        Check(!overlay.enabled&&!depthCamera.enabled&&cold.layer==4&&world.cullingMask==-1,"Depth allocation failure falls back to the authored world text");
        Time.unscaledTime+=2;Camera.FirePreCull(world);Check(overlay.enabled&&depthCamera.enabled&&cold.layer==31,"Recipe protection retries allocation after a bounded delay");
        float shifted=cold.transform.localPosition.x;Camera.FirePreCull(world);
        Check(shifted>1&&cold.transform.localPosition.x==shifted,"Russian heading moves once without accumulating drift");
        ClosingShift.Localization.RussianUiPresentation.RussianActive=false;Camera.FirePreCull(world);
        Check(cold.transform.localPosition.x==1,"Switching away from Russian restores the authored heading");
        renderer.enabled=false;Camera.FirePreCull(world);Check(!renderer.enabled,"The recipe camera preserves authored renderer visibility");
        Callback("OnDisable");Check(cold.layer==4&&world.cullingMask==-1&&!overlay.enabled,"Scene teardown restores layers/mask and disables the recipe camera");
        Check(!post.finalBlitToCameraTarget&&!depthCamera.enabled&&depthCamera.targetTexture==null,"Recipe teardown preserves PPS and releases depth-camera attachments");
        Camera.FirePreCull(world);Check((Camera)typeof(AndroidRecipeText).GetField("overlay",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(component)==null,"Recipe teardown unsubscribes from camera callbacks");
        AndroidRenderScale.Enforce();Check(world.targetTexture==null,"Native100 releases the extra target after recipe protection is removed");
    }
    static void Check(bool value,string reason){if(!value)throw new Exception(reason);++checks;}
    static object Instance(){return typeof(AndroidRenderScale).GetField("instance",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null);}
    static T Read<T>(string name){return (T)typeof(AndroidRenderScale).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(Instance());}
    static bool Near(float a,float b){return Math.Abs(a-b)<.001f;}
    static Camera World(){var value=new Camera{depth=0,forceIntoRenderTexture=true};value.gameObject.scene=new Scene{buildIndex=1};return value;}
    static void VideoBudgetCheck()
    {
        int memory=SystemInfo.systemMemorySize;
        SystemInfo.systemMemorySize=2629;Time.unscaledTime+=3;
        RuntimeUtilities.copyMaterial.shader.isSupported=true;
        var world=World();world.gameObject.scene=new Scene{buildIndex=1,handle=901};Camera.main=world;
        var movie=new GameObject("authored tutorial").AddComponent<UnityEngine.Video.VideoPlayer>();
        movie.gameObject.scene=world.gameObject.scene;movie.renderMode=UnityEngine.Video.VideoRenderMode.MaterialOverride;
        movie.gameObject.AddComponent<MeshRenderer>();
        var uiMovie=new GameObject("Canvas credits").AddComponent<UnityEngine.Video.VideoPlayer>();uiMovie.gameObject.scene=world.gameObject.scene;uiMovie.renderMode=UnityEngine.Video.VideoRenderMode.RenderTexture;uiMovie.clip=new UnityEngine.Video.VideoClip();
        movie.clip=new UnityEngine.Video.VideoClip();movie.gameObject.SetActive(false);
        var previousObjects=Resources.objects;Resources.objects=new object[previousObjects.Length+2];Array.Copy(previousObjects,Resources.objects,previousObjects.Length);Resources.objects[previousObjects.Length]=movie;Resources.objects[previousObjects.Length+1]=uiMovie;
        MobileRenderSettings.SetRenderPercent(75);AndroidRenderScale.Enforce();var normal=world.targetTexture;
        Check(normal.width==1800&&normal.height==810,"Inactive tutorial preserves selected75 world resolution");
        movie.gameObject.SetActive(true);AndroidRenderScale.Enforce();var limited=world.targetTexture;
        Check(normal.released&&limited.width==1200&&limited.height==540,"Active world video replaces the larger RT before decoding on3GB device");
        Check(MobileRenderSettings.RenderPercent==75&&PlayerPrefs.GetInt("AndroidRenderPercent",-1)==75&&Screen.width==2400&&Screen.height==1080,"Video cap does not save a new preference or resize native UI output");
        movie.clip=null;Time.unscaledTime+=1;AndroidRenderScale.Enforce();
        Check(world.targetTexture==limited,"Decoder teardown retains the smaller target during the cooldown");
        Time.unscaledTime+=1.1f;AndroidRenderScale.Enforce();
        Check(limited.released&&world.targetTexture.width==1800,"Stopping video restores the user's exact resolution after2seconds");
        movie.clip=new UnityEngine.Video.VideoClip{width=512,height=512};AndroidRenderScale.Enforce();
        Check(world.targetTexture.width==1800,"Small video does not require the large-video budget");
        movie.clip.width=1280;movie.renderMode=UnityEngine.Video.VideoRenderMode.RenderTexture;AndroidRenderScale.Enforce();
        Check(world.targetTexture.width==1200,"Actual tutorial RenderTexture mode is limited when its owner is a world mesh");
        movie.gameObject.SetActive(false);Time.unscaledTime+=2.1f;AndroidRenderScale.Enforce();Check(world.targetTexture.width==1800,"Active Canvas RenderTexture movie without a world mesh is excluded");movie.gameObject.SetActive(true);
        movie.renderMode=UnityEngine.Video.VideoRenderMode.MaterialOverride;movie.gameObject.scene=new Scene{buildIndex=1,handle=902};AndroidRenderScale.Enforce();
        Check(world.targetTexture.width==1800,"Other scene's video cannot reduce this world's resolution");
        movie.gameObject.scene=world.gameObject.scene;movie.enabled=false;AndroidRenderScale.Enforce();
        Check(world.targetTexture.width==1800,"Disabled video does not start a budget");
        movie.enabled=true;SystemInfo.systemMemorySize=3073;AndroidRenderScale.Enforce();
        Check(world.targetTexture.width==1800,"Above3GB retains selected resolution");
        SystemInfo.systemMemorySize=0;AndroidRenderScale.Enforce();Check(world.targetTexture.width==1800,"Unknown memory does not impose a budget");
        SystemInfo.systemMemorySize=3072;AndroidRenderScale.Enforce();Check(world.targetTexture.width==1200,"Exactly3GB includes the tested low-memory policy");
        Application.platform=RuntimePlatform.WindowsPlayer;Check(AndroidVideoBudget.WorldPercent(901,75)==75,"Desktop does not use the Android budget");Application.platform=RuntimePlatform.Android;
        MobileRenderSettings.SetRenderPercent(33);AndroidRenderScale.Enforce();Check(world.targetTexture.width==792&&world.targetTexture.height==356,"User-selected lower resolution is preserved rather than raised to50");
        MobileRenderSettings.SetRenderPercent(75);var next=World();next.gameObject.scene=new Scene{buildIndex=1,handle=903};Camera.main=next;AndroidRenderScale.Enforce();
        Check(next.targetTexture.width==1800,"A new scene clears the previous video's cooldown");
        int scans=Resources.scans;AndroidVideoBudget.WorldPercent(903,75);AndroidVideoBudget.WorldPercent(903,75);Check(Resources.scans==scans,"Stable frames reuse the scene video inventory");
        var menu=new Camera();menu.gameObject.scene=new Scene{buildIndex=0,handle=904};Camera.main=menu;AndroidRenderScale.Enforce();Check(menu.targetTexture==null,"Menu UI still renders natively while a world video was active");
        movie.gameObject.SetActive(false);Resources.objects=previousObjects;SystemInfo.systemMemorySize=memory;
    }
    static void Main()
    {
        MobileRenderSettings.SetRenderPercent(50);
        var world=World();var ui=new Camera{depth=2,allowHDR=false};Camera.main=world;
        AndroidRenderScale.Enforce();var first=world.targetTexture;var presenter=Read<Camera>("presentation");var commands=Read<CommandBuffer>("composite");Camera.FirePreRender(presenter);
        Check(first!=null&&first.width==1200&&first.height==540&&first.depth==24&&first.format==RenderTextureFormat.DefaultHDR&&first.IsCreated(),"World draws into a half-size HDR target with retained depth attachment");
        Check(Screen.width==2400&&Screen.height==1080&&Screen.fullScreen&&ui.targetTexture==null&&ui.depth==2&&!ui.allowHDR,"Output and authored UI camera remain native with original order/HDR");
        Check(world.forceIntoRenderTexture&&Near(world.aspect,2400f/1080),"World keeps authored forceRT and native perspective aspect");
        Check(presenter.targetTexture==null&&presenter.pixelWidth==2400&&presenter.pixelHeight==1080&&presenter.depth>world.depth&&presenter.depth<.5f&&presenter.cullingMask==0&&!presenter.allowHDR&&!presenter.allowMSAA,"Native presentation camera draws only the world copy before icon/inventory/UI cameras");
        Check(commands.target.Equals(new RenderTargetIdentifier(Display.main.colorBuffer))&&commands.texture==first&&commands.viewport.width==2400&&commands.viewport.height==1080&&commands.material==RuntimeUtilities.copyMaterial&&commands.draws==1,"Presentation targets the actual display buffer with the opaque PPS copy, never the unpresented CameraTarget");
        Check(commands.usedPpsBlit&&commands.globals[Shader.PropertyToID("_RenderViewportScaleFactor")]==1f,"Presentation initializes GLES copy UV scale and uses the standard PPS blit/load-store path");
        int clears=commands.clears;
        for(int i=0;i<10;++i){AndroidRenderScale.Enforce();Camera.FirePreRender(presenter);}
        Check(commands.clears==clears+10&&world.targetTexture==first,"Every presentation frame rebuilds native copy commands without reallocating the world target");
        Check(Debug.messages.FindAll(m=>m.StartsWith("[MobileRender] present-callback ")).Count==1,"First-record diagnostic is bounded to one log for an unchanged target");
        Display.main.colorBuffer=new RenderBuffer{id=43};AndroidRenderScale.Enforce();Camera.FirePreRender(presenter);
        Check(commands.clears==clears+11&&commands.target.Equals(new RenderTargetIdentifier(Display.main.colorBuffer))&&world.targetTexture==first,"Presentation resolves the current display buffer each frame without relying on startup handle equality");
        int ownClears=commands.clears;Camera.FirePreRender(ui);Camera.FirePreRender(world);Check(commands.clears==ownClears,"Other camera callbacks cannot overwrite presentation commands");
        foreach(int percent in new[]{25,33,50,67,75,100})
        {
            MobileRenderSettings.SetRenderPercent(percent);AndroidRenderScale.Enforce();
            Vector3 input=new Vector3(1800,270,3);Ray ray=AndroidRenderScale.ScreenPointToRay(world,input);
            Vector3 viewport=AndroidRenderScale.ScreenToViewportPoint(world,input);Vector3 point=AndroidRenderScale.ScreenToWorldPoint(world,input);
            Vector3 native=AndroidRenderScale.WorldToScreenPoint(world,new Vector3(.75f,.25f,3));Vector3 nativeViewport=AndroidRenderScale.ViewportToScreenPoint(world,new Vector3(.75f,.25f,3));
            Check(Near(ray.direction.x,.75f)&&Near(ray.direction.y,.25f)&&Near(viewport.x,.75f)&&Near(viewport.y,.25f)&&Near(point.x,.75f)&&Near(point.y,.25f)&&Near(native.x,1800)&&Near(native.y,270)&&Near(nativeViewport.x,1800)&&Near(nativeViewport.y,270)&&native.z==3,"Camera pixel conversions preserve native pointer/markers at every render percentage");
            Vector3 corner=AndroidRenderScale.WorldToScreenPoint(world,new Vector3(1,1,5));Ray edge=AndroidRenderScale.ScreenPointToRay(world,new Vector3(2400,1080,5));Check(Near(corner.x,2400)&&Near(corner.y,1080)&&Near(edge.direction.x,1)&&Near(edge.direction.y,1)&&corner.z==5,"Scaled camera corner coordinates preserve full native bounds and depth");
        }
        Check(world.targetTexture==null&&!presenter.enabled&&first.released&&world.forceIntoRenderTexture,"100 percent releases scaled target and bypasses presentation without changing authored forceRT");
        MobileRenderSettings.SetRenderPercent(33);AndroidRenderScale.Enforce();var replaced=world.targetTexture;Check(replaced.width==792&&replaced.height==356&&presenter.enabled,"Returning to scaled rendering creates the selected target");
        MobileRenderSettings.SetRenderPercent(50);AndroidRenderScale.Enforce();Check(replaced.released&&world.targetTexture.width==1200,"Percentage change releases the previous target before rebinding");
        var originalTarget=new RenderTexture(128,128,24,RenderTextureFormat.DefaultHDR);var security=World();security.targetTexture=originalTarget;Camera.main=security;AndroidRenderScale.Enforce();Check(security.targetTexture==originalTarget&&world.targetTexture==null&&!presenter.enabled,"Authored camera targets stay intact and previous world target is restored");
        var menu=new Camera();menu.gameObject.scene=new Scene{buildIndex=0};Camera.main=menu;AndroidRenderScale.Enforce();Check(menu.targetTexture==null&&!presenter.enabled,"Authored menu scene stays native for screen-space camera canvases");
        Camera.main=null;AndroidRenderScale.Enforce();Check(!presenter.enabled,"Scene transition with no main camera disables presentation safely");
        Vector3 uiPosition=AndroidRenderScale.WorldToScreenPoint(ui,new Vector3(.75f,.25f,3));Check(Near(uiPosition.x,1800)&&Near(uiPosition.y,270),"Unscaled UI camera pixel calls pass through unchanged");
        var owner=(AndroidRenderScale)Instance();typeof(AndroidRenderScale).GetMethod("OnDestroy",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(owner,null);Check(commands.released&&presenter.commands.Count==0,"Persistent component destruction releases native command buffer");
        int destroyedClears=commands.clears;Camera.FirePreRender(presenter);Check(commands.clears==destroyedClears,"Destroy unsubscribes the presentation callback");
        RuntimeUtilities.copyMaterial.shader.isSupported=false;Camera.main=World();AndroidRenderScale.Enforce();Check(Camera.main.targetTexture==null&&Debug.messages.Exists(m=>m.Contains("mode=native-copy-fallback")),"Unsupported presentation shader restores native output with explicit diagnostic");
        RecipeTextCheck();
        VideoBudgetCheck();
        Console.WriteLine("RENDER_SCALE_LIFECYCLE_PASS "+checks+" assertions against actual AndroidRenderScale/MobileRender/RecipeText source; GPU output requires device verification");
    }
}
