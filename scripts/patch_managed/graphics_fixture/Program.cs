using System;
using System.Collections.Generic;
using System.Reflection;
using ClosingShift.Android;
using HorizonBasedAmbientOcclusion;
using Aura2API;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.Rendering.PostProcessing;
using Object=System.Object;

enum ConfigPath { GameDataPath, DocumentsPath, Other }
sealed class Config:MonoBehaviour
{
    public string ConfigName="GameConfig";public ConfigPath savePath;
    public Dictionary<string,string> graphic=new Dictionary<string,string>();
    public Dictionary<string,string> DeserializeSection(string section){return section=="Graphic"?graphic:null;}
}
sealed class Options:MonoBehaviour
{
    public Config configHandler=new Config();
    public Dropdown QualityDropdown=new Dropdown(),AntialiasingDropdown=new Dropdown(),AnisotropicDropdown=new Dropdown(),TextureQualityDropdown=new Dropdown(),BlendWeightsDropdown=new Dropdown(),VSyncDropdown=new Dropdown(),ShadowResolutionDropdown=new Dropdown(),ShadowQualityDropdown=new Dropdown(),ShadowCascadesDropdown=new Dropdown(),ShadowProjecionDropdown=new Dropdown(),ResolutionDropdown=new Dropdown();
    public Toggle VHSEffect=new Toggle(),DepthOfField=new Toggle(),GrainToggle=new Toggle(),BloomToggle=new Toggle(),MotionBlurToggle=new Toggle(),AmbientOccToggle=new Toggle(),FullscreenToggle=new Toggle();
    public Slider ShadowDistanceSlider=new Slider();
    int QualityLevel,AntialiasingLevel,AnisotropicLevel,TextureQualityLevel,BlendWeightsLevel,VSyncLevel,ShadowResolutionLevel,ShadowQualityLevel,ShadowCascadesLevel,ShadowProjecionLevel,ResolutionLevel;
    bool qChanged,custom,antiaChanged,anisoChanged,tqChanged,bwChanged,vsyncChanged,swResChanged,swQChanged,swCasChanged,swProjChanged,resChanged,fullscreen,fullscreenChanged;
    Resolution[] Resolutions;
    internal object Read(string name){return GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic).GetValue(this);}
    // Critical branch from actual UICustomOptions.SetDefaultGraphics: Reset hook,
    // then !Serialize sets resChanged=true, then the legacy native reset write.
    internal void AuthoredResetBranch(bool serialize)
    {
        AndroidGraphics.ResetQuality(this);
        VHSEffect.isOn=false;DepthOfField.isOn=false;
        if(serialize)AndroidGraphics.BeforeSerializeQuality(this);
        if(!serialize)resChanged=true;
        if(resChanged)
        {
            Resolution resolution=MobileRenderSettings.GetResolution(Resolutions,0);
            MobileRenderSettings.SetResolution(resolution.width,resolution.height,fullscreen);
            AndroidGraphics.SetLegacyResolutionSelection(ResolutionDropdown,0);
            resChanged=false;
        }
    }
    internal Options(int quality=0,bool migrated=false)
    {
        QualityDropdown.value=quality;
        foreach(var d in new[]{QualityDropdown,AntialiasingDropdown,AnisotropicDropdown,TextureQualityDropdown,BlendWeightsDropdown,VSyncDropdown,ShadowResolutionDropdown,ShadowQualityDropdown,ShadowCascadesDropdown,ShadowProjecionDropdown})for(int i=0;i<6;++i)d.options.Add(new Dropdown.OptionData("Localized "+i));
        var row=new GameObject("Resolution");ResolutionDropdown.transform.SetParent(row.transform,false);
        var label=new GameObject("GraphicLabel",typeof(Text));label.transform.SetParent(row.transform,false);label.GetComponent<Text>().text="Resolution";
        var fullrow=new GameObject("Fullscreen");FullscreenToggle.transform.SetParent(fullrow.transform,false);
        if(migrated){configHandler.graphic["AndroidLowProfileVersion"]="1";configHandler.graphic["GraphicQuality"]=quality.ToString();}
    }
}
static class Program
{
    static int checks;
    static void Check(bool condition,string message){if(!condition)throw new Exception(message);++checks;}
    static void Select(Options owner,int quality){owner.QualityDropdown.value=quality;AndroidGraphics.QualitySelected(owner,owner.QualityDropdown);AndroidGraphics.BeforeApplyQuality(owner);AndroidGraphics.Enforce();}
    static Light LightAt(float x,LightType type=LightType.Point,LightShadows shadows=LightShadows.Soft){var value=new Light{type=type,shadows=shadows};value.transform.position=new Vector3(x,0,0);return value;}
    static void HiddenDisplayIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Mali-G76";
        var camera=new Camera{nearClipPlane=.01f};camera.gameObject.scene=new Scene{buildIndex=1};var ui=new Camera{nearClipPlane=.01f};ui.gameObject.scene=new Scene{buildIndex=1};Camera.main=camera;Resources.objects=new object[]{camera,ui};AndroidGraphics.BeginScene();
        MobileRenderSettings.SetRenderPercent(50);ClosingShift.Localization.RussianUiPresentation.RussianActive=false;
        var owner=new Options(3,true);owner.gameObject.name="GAMEMANAGER";
        var panel=new GameObject("OptionsPanel");panel.transform.SetParent(owner.transform,false);
        var tab=new GameObject("TAB_GRAPHIC");tab.transform.SetParent(panel.transform,false);
        owner.ResolutionDropdown.transform.parent.SetParent(tab.transform,false);
        owner.FullscreenToggle.transform.parent.SetParent(tab.transform,false);panel.SetActive(false);
        var dropdown=owner.ResolutionDropdown;var label=dropdown.transform.parent.Find("GraphicLabel").GetComponent<Text>();
        owner.FullscreenToggle.isOn=false;
        AndroidGraphics.BeforeApplyQuality(owner);
        Check(camera.nearClipPlane==.1f&&ui.nearClipPlane==.01f,"World depth gains10x precision without clipping the authored UI/item camera");
        int obsoleteCalls=0;dropdown.onValueChanged.AddListener(index=>++obsoleteCalls);
        int refreshes=dropdown.refreshes;AndroidGraphics.Enforce();
        Check(owner.gameObject.activeInHierarchy&&dropdown.gameObject.activeSelf&&!dropdown.gameObject.activeInHierarchy,"Fixture models the active settings owner with controls hidden by an ancestor");
        Check(dropdown.options.Count==6&&dropdown.onValueChanged.count==1&&dropdown.value==2&&dropdown.shownText=="50%"&&dropdown.refreshes>refreshes,"First hidden enforcement installs and displays the percent control");
        Check(label.text=="3D render percentage"&&owner.FullscreenToggle.isOn&&!owner.FullscreenToggle.interactable&&!owner.FullscreenToggle.transform.parent.gameObject.activeSelf,"First hidden enforcement also configures the label and fullscreen row");
        dropdown.onValueChanged.Invoke(1);
        Check(MobileRenderSettings.RenderPercent==33&&obsoleteCalls==0,"The replacement listener installed while hidden works and removes the desktop callback");
        MobileRenderSettings.SetRenderPercent(50);
        refreshes=dropdown.refreshes;int saves=PlayerPrefs.saves;
        for(int i=0;i<8;++i)AndroidGraphics.Enforce();
        Check(dropdown.refreshes==refreshes&&PlayerPrefs.saves==saves,"Stable hidden frames do not refresh the caption or save preferences");
        MobileRenderSettings.SetRenderPercent(75);ClosingShift.Localization.RussianUiPresentation.RussianActive=true;
        for(int i=0;i<3;++i)AndroidGraphics.Enforce();
        Check(dropdown.refreshes==refreshes&&dropdown.shownText=="50%"&&label.text=="3D render percentage"&&MobileRenderSettings.GetRenderResolution().height==810,"Hidden percentage and language changes affect rendering without touching the hidden presentation");
        Check(PlayerPrefs.saves==saves+1&&PlayerPrefs.GetInt("AndroidRenderPercent",0)==75,"Only the actual percentage change persists PlayerPrefs");
        Screen.fullScreen=false;saves=PlayerPrefs.saves;
        owner.AuthoredResetBranch(false);owner.AuthoredResetBranch(true);AndroidGraphics.Enforce();
        Check(owner.QualityDropdown.value==4&&MobileRenderSettings.RenderPercent==75&&Screen.fullScreen&&Screen.height==1080&&!(bool)owner.Read("resChanged")&&PlayerPrefs.saves==saves,"Hidden reset restores Minimum and native fullscreen while preserving the render percentage");
        Check(dropdown.refreshes==refreshes,"Quality reset does not restart hidden display setup or refresh its caption");
        Check(owner.VHSEffect.isOn&&!owner.DepthOfField.isOn,"Authored reset cannot overwrite the requested effect defaults before serialization or rendering");
        owner.FullscreenToggle.isOn=false;owner.FullscreenToggle.interactable=true;owner.FullscreenToggle.transform.parent.gameObject.SetActive(true);
        panel.SetActive(true);AndroidGraphics.Enforce();
        Check(dropdown.refreshes>refreshes&&dropdown.value==4&&dropdown.shownText=="75%"&&label.text=="Процент рендера 3D","Opening the graphics tab reconciles the current percentage and language");
        Check(owner.FullscreenToggle.isOn&&!owner.FullscreenToggle.interactable&&!owner.FullscreenToggle.transform.parent.gameObject.activeSelf&&PlayerPrefs.saves==saves,"Opening repairs fullscreen UI without extra preference writes");
        tab.SetActive(false);refreshes=dropdown.refreshes;
        MobileRenderSettings.SetRenderPercent(67);ClosingShift.Localization.RussianUiPresentation.RussianActive=false;AndroidGraphics.Enforce();
        Check(dropdown.refreshes==refreshes&&owner.gameObject.activeInHierarchy&&panel.activeInHierarchy,"A hidden graphics tab also skips display work when the options panel stays open");
        tab.SetActive(true);AndroidGraphics.Enforce();
        Check(dropdown.value==3&&dropdown.shownText=="67%"&&label.text=="3D render percentage"&&dropdown.onValueChanged.count==1,"Reopening the tab reconciles state without duplicating the listener");
    }
    static void AmbientOcclusionIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Adreno (TM) 735";SystemInfo.supportedRenderTargetCount=4;
        var mainObject=new GameObject("AO gameplay");mainObject.scene=new Scene{buildIndex=1};
        var main=mainObject.AddComponent<Camera>();var ao=mainObject.AddComponent<HBAO>();ao.enabled=false;
        var auxObject=new GameObject("AO auxiliary");auxObject.scene=new Scene{buildIndex=1};
        var aux=auxObject.AddComponent<Camera>();var auxAo=auxObject.AddComponent<HBAO>();
        Camera.main=main;Resources.objects=new object[]{main,aux};AndroidGraphics.BeginScene();
        AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");AndroidGraphics.Observe(auxAo,"HorizonBasedAmbientOcclusion.HBAO");
        var options=new Options(3,true);Select(options,3);
        Check(ao.enabled&&!auxAo.enabled&&main.renderingPath==RenderingPath.DeferredShading&&ao.normals==HBAO.PerPixelNormals.GBuffer&&main.depthTextureMode==DepthTextureMode.Depth,"Observed HBAO uses G-buffer normals only on the deferred main camera with Depth alone");
        int writes=ao.writes,attempts=ao.enableAttempts,scans=Resources.scans;
        for(int i=0;i<3;++i)AndroidGraphics.Enforce();
        Check(ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth&&ao.writes==writes&&ao.enableAttempts==attempts&&Resources.scans==scans,"Repeated ApplyCameras restores AO depth after resetting the mask without reconfiguration, reenable or scans");
        Select(options,4);
        Check(ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"The independent enabled AO switch survives selection of Minimum");
        Select(options,2);
        Check(ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Higher profile restores observed AO and Depth after Minimum");
        main.gameObject.scene=new Scene{buildIndex=0};AndroidGraphics.Enforce();
        Check(!ao.enabled&&main.renderingPath==RenderingPath.Forward&&main.depthTextureMode==DepthTextureMode.None,"Menu transition disables AO when graphics policy switches the camera to Forward");
        main.gameObject.scene=new Scene{buildIndex=1};AndroidGraphics.Enforce();
        Check(ao.enabled&&main.renderingPath==RenderingPath.DeferredShading&&main.depthTextureMode==DepthTextureMode.Depth,"Gameplay resume restores deferred AO and Depth through the graphics hook");
        SystemInfo.supportedRenderTargetCount=3;AndroidGraphics.Enforce();
        Check(!ao.enabled&&main.renderingPath==RenderingPath.Forward&&main.depthTextureMode==DepthTextureMode.None,"Insufficient MRT support disables AO together with the deferred path");
        SystemInfo.supportedRenderTargetCount=4;AndroidGraphics.Enforce();
        Check(ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Restored MRT support reenables observed AO and Depth");
        SystemInfo.graphicsDeviceName="Mali-G76";AndroidGraphics.Enforce();
        Check(!ao.enabled&&main.renderingPath==RenderingPath.Forward&&main.depthTextureMode==DepthTextureMode.None,"GPU fallback disables AO and its depth request");
        SystemInfo.graphicsDeviceName="Adreno (TM) 735";
        var volume=mainObject.AddComponent<AuraCamera>();var volumeLight=new GameObject("Volume light").AddComponent<AuraLight>();
        AndroidGraphics.Observe(volume,"Aura2API.AuraCamera");AndroidGraphics.Observe(volumeLight,"Aura2API.AuraLight");
        Select(options,2);
        Check(ao.enabled&&volume.enabled&&volumeLight.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Higher graphics profile enables AO and volume with one shared Depth request");
        Select(options,3);
        Check(ao.enabled&&!volume.enabled&&!volumeLight.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Higher to Low disables volume while retaining AO's required Depth");
        Select(options,4);
        Check(ao.enabled&&!volume.enabled&&!volumeLight.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Minimum stops Aura while independently selected AO retains its shared Depth request");
        Select(options,1);
        Check(ao.enabled&&volume.enabled&&volumeLight.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Minimum to Higher restores both effects and shared Depth through real graphics hooks");
        int volumeAttempts=volume.enableAttempts;writes=ao.writes;scans=Resources.scans;
        for(int i=0;i<3;++i)AndroidGraphics.Enforce();
        Check(ao.enabled&&volume.enabled&&main.depthTextureMode==DepthTextureMode.Depth&&volume.enableAttempts==volumeAttempts&&ao.writes==writes&&Resources.scans==scans,"Repeated graphics enforcement keeps both effects and Depth without reinitializing or scanning");
        AndroidVolumetrics.BeforeQualityChange();QualitySettings.shadows=ShadowQuality.Disable;
        var point=volumeLight.gameObject.AddComponent<Light>();point.shadows=LightShadows.Soft;
        var sunObject=new GameObject("Volume sun");var sun=sunObject.AddComponent<Light>();sun.type=LightType.Directional;sun.shadows=LightShadows.Soft;
        var sunVolume=sunObject.AddComponent<AuraLight>();sunVolume.InitializeForTest();AndroidGraphics.Observe(sunVolume,"Aura2API.AuraLight");
        AndroidGraphics.BeforeApplyQuality(options);
        Check(!volumeLight.enabled&&!sunVolume.enabled&&Lifecycle.directionalShadowCount==0,"Real quality prefix leaves collectors suspended after its internal ApplyCameras");
        QualitySettings.shadows=ShadowQuality.All; // Original game body executes after the prefix.
        AndroidGraphics.Enforce();volumeLight.SimulatePreCull();volumeLight.SimulatePreRender();
        Check(volumeLight.shadowBuffer&&volumeLight.shadowMaterial&&sunVolume.shadowBuffer&&Lifecycle.directionalShadowCount==1,"Original global enable followed by real enforcement prepares point resources and one sun before rendering");
        AndroidGraphics.BeforeApplyQuality(options);
        Check(!volumeLight.enabled&&!sunVolume.enabled&&Lifecycle.directionalShadowCount==0,"Global-disable prefix unregisters the sun while the old global mode is still enabled");
        QualitySettings.shadows=ShadowQuality.Disable;AndroidGraphics.Enforce();
        Check(volumeLight.enabled&&sunVolume.enabled&&!volumeLight.shadowBuffer&&!sunVolume.shadowBuffer&&Lifecycle.directionalShadowCount==0,"Original global disable resumes volume collectors without stale shadow resources or counters");
    }
    static void SerializeQualityIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Adreno (TM) 735";
        var root=new GameObject("Serialize gameplay");root.scene=new Scene{buildIndex=1};
        var main=root.AddComponent<Camera>();var ao=root.AddComponent<HBAO>();var volume=root.AddComponent<AuraCamera>();
        var lamp=new GameObject("Serialize light");var light=lamp.AddComponent<Light>();var auraLight=lamp.AddComponent<AuraLight>();
        Camera.main=main;Resources.objects=new object[]{main,light};AndroidGraphics.BeginScene();
        AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");AndroidGraphics.Observe(volume,"Aura2API.AuraCamera");AndroidGraphics.Observe(auraLight,"Aura2API.AuraLight");
        var active=new Options(1,true);Select(active,1);
        Check(ao.enabled&&volume.enabled&&auraLight.enabled&&auraLight.initialized,"Serialization fixture starts with live AO, Aura camera and initialized collector");
        // Deliberately differ from the preset: a save must not apply these global/camera/display values.
        main.depthTextureMode=DepthTextureMode.Depth|DepthTextureMode.MotionVectors;main.farClipPlane=123;
        QualitySettings.masterTextureLimit=2;QualitySettings.pixelLightCount=7;QualitySettings.shadowDistance=145;
        QualitySettings.shadowResolution=ShadowResolution.VeryHigh;QualitySettings.shadowCascades=4;QualitySettings.lodBias=1.8f;
        MobileRenderSettings.SetRenderPercent(67);Screen.fullScreen=false;
        int screenCalls=Screen.calls,saves=PlayerPrefs.saves,scans=Resources.scans,events=Lifecycle.events.Count;
        int lightAttempts=auraLight.enableAttempts,cameraAttempts=volume.enableAttempts,aoWrites=ao.writes;
        ShadowQuality shadows=QualitySettings.shadows;LightShadows lightShadows=light.shadows;
        bool shadowBuffer=auraLight.shadowBuffer;
        void Unchanged()
        {
            Check(auraLight.enabled&&auraLight.initialized&&auraLight.enableAttempts==lightAttempts&&auraLight.shadowBuffer==shadowBuffer&&volume.enabled&&volume.enableAttempts==cameraAttempts&&Lifecycle.events.Count==events,"Saving never suspends or restarts Aura collectors or camera");
            Check(ao.enabled&&ao.writes==aoWrites&&main.depthTextureMode==(DepthTextureMode.Depth|DepthTextureMode.MotionVectors)&&main.farClipPlane==123&&main.renderingPath==RenderingPath.DeferredShading,"Saving preserves AO configuration and exact camera depth, range and path");
            Check(QualitySettings.shadows==shadows&&light.shadows==lightShadows&&QualitySettings.masterTextureLimit==2&&QualitySettings.pixelLightCount==7&&QualitySettings.shadowDistance==145&&QualitySettings.shadowResolution==ShadowResolution.VeryHigh&&QualitySettings.shadowCascades==4&&QualitySettings.lodBias==1.8f,"Saving leaves global quality and local shadows untouched");
            Check(!Screen.fullScreen&&Screen.calls==screenCalls&&MobileRenderSettings.RenderPercent==67&&PlayerPrefs.saves==saves&&Resources.scans==scans,"Saving cannot resize the display, persist render percentage, or scan the scene");
        }
        foreach(int quality in new[]{0,1,2,3,4,5})
        {
            var owner=new Options(quality,true);owner.TextureQualityDropdown.value=2;
            owner.ShadowResolutionDropdown.value=3;owner.ShadowCascadesDropdown.value=2;owner.ShadowDistanceSlider.value=100;
            // Calls the actual new prefix directly, including an owner not previously observed.
            AndroidGraphics.BeforeSerializeQuality(owner);AndroidGraphics.BeforeSerializeQuality(owner);
            Check(owner.QualityDropdown.value==quality&&(int)owner.Read("QualityLevel")==quality&&(int)typeof(AndroidGraphics).GetField("profile",BindingFlags.Static|BindingFlags.NonPublic).GetValue(null)==quality,"Serialization retains the selected profile and its original config/private values");
            Check(owner.TextureQualityDropdown.value==(quality==5?2:0)&&(quality==5||(int)owner.Read("TextureQualityLevel")==0),"Preset serialization stores full prepared mips while Custom retains its selected mip level");
            Check(owner.VHSEffect.isOn&&!owner.DepthOfField.isOn&&!owner.GrainToggle.isOn&&!owner.BloomToggle.isOn&&!owner.MotionBlurToggle.isOn&&owner.AmbientOccToggle.isOn==(quality!=4)&&owner.BloomToggle.interactable&&owner.BloomToggle.notifications==0,"Serialization initializes VHS and interactive controls without invoking listeners");
            Check(owner.ShadowResolutionDropdown.value==(quality==3||quality==4?0:1)&&owner.ShadowCascadesDropdown.value==0&&owner.ShadowDistanceSlider.value==(quality==3?20:quality==4?0:30),"Serialized shadow controls retain existing Low/Minimum/higher caps");
            if(quality==3||quality==4)Check(owner.ShadowQualityDropdown.value==(quality==4?0:1)&&owner.AntialiasingDropdown.value==0&&owner.VSyncDropdown.value==0,"Low and Minimum serialization retains the original normalized quality tuple");
            Unchanged();
        }
        AndroidGraphics.BeforeSerializeQuality(null);Unchanged();
        var desktop=new Options(2,true);Application.platform=RuntimePlatform.WindowsPlayer;
        AndroidGraphics.BeforeSerializeQuality(desktop);
        Check(desktop.QualityDropdown.value==2&&desktop.VHSEffect.isOn&&(int)desktop.Read("QualityLevel")==0,"Serialization prefix is inert for desktop settings");
        Application.platform=RuntimePlatform.Android;Screen.fullScreen=true;
        // Actual Apply must still suspend under the old shadow mode before the original setter.
        AndroidGraphics.BeforeApplyQuality(active);
        Check(!auraLight.enabled&&volume.enabled,"Actual Apply retains mandatory collector teardown while keeping its camera initialized");
        QualitySettings.shadows=ShadowQuality.Disable;AndroidGraphics.Enforce();
        Check(auraLight.enabled&&!auraLight.shadowBuffer&&volume.enabled,"Actual global-shadow write resumes a correctly initialized collector on ordinary enforcement");
    }
    static void MaliG715GuardIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Mali-G715";SystemInfo.supportedRenderTargetCount=4;
        AndroidVolumetrics.BeforeQualityChange();QualitySettings.shadows=ShadowQuality.All;
        var root=new GameObject("Guard gameplay");root.scene=new Scene{buildIndex=1};
        var main=root.AddComponent<Camera>();main.name="GuardMain";var ao=root.AddComponent<HBAO>();ao.enabled=false;var volume=root.AddComponent<AuraCamera>();
        var point=LightAt(1);var second=LightAt(2,LightType.Spot,LightShadows.Hard);var sun=LightAt(0,LightType.Directional);
        var collector=point.gameObject.AddComponent<AuraLight>();var sunCollector=sun.gameObject.AddComponent<AuraLight>();
        Camera.main=main;Resources.objects=new object[]{main,point,second,sun};AndroidGraphics.BeginScene();
        collector.InitializeForTest();sunCollector.InitializeForTest();
        AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");AndroidGraphics.Observe(volume,"Aura2API.AuraCamera");
        AndroidGraphics.Observe(collector,"Aura2API.AuraLight");AndroidGraphics.Observe(sunCollector,"Aura2API.AuraLight");
        var options=new Options(0,true);QualitySettings.masterTextureLimit=2;Select(options,0);volume.SimulateRender();
        Check(main.renderingPath==RenderingPath.DeferredShading&&main.allowHDR&&ao.enabled&&ao.normals==HBAO.PerPixelNormals.GBuffer&&main.depthTextureMode==DepthTextureMode.Depth,"G715 fallback preserves Deferred, HDR, G-buffer AO and shared Depth");
        Check(!volume.enabled&&!collector.enabled&&!sunCollector.enabled&&volume.frames==0&&Lifecycle.directionalShadowCount==0,"Real graphics hook blocks the initial Aura render and removes shadow collectors");
        Check(point.shadows==LightShadows.Soft&&second.shadows==LightShadows.None&&sun.shadows==LightShadows.Soft&&point.enabled&&second.enabled&&sun.enabled&&point.intensity==2&&QualitySettings.masterTextureLimit==0&&QualitySettings.pixelLightCount==3,"G715 higher profile keeps one local surface shadow plus sun, light properties and prepared texture mips");
        int volumeAttempts=volume.enableAttempts,lightAttempts=collector.enableAttempts,events=Lifecycle.events.Count,reads=volume.frameReads;
        for(int i=0;i<3;++i){AndroidGraphics.BeforeSerializeQuality(options);AndroidGraphics.Enforce();volume.SimulateRender();}
        Check(!volume.enabled&&!collector.enabled&&volume.frames==0&&volume.enableAttempts==volumeAttempts&&collector.enableAttempts==lightAttempts&&Lifecycle.events.Count==events&&volume.frameReads==reads&&ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Repeated serialization and enforcement do not revive Aura, repeat its log, or lose AO depth");
        foreach(ShadowQuality global in new[]{ShadowQuality.Disable,ShadowQuality.All})
        {
            AndroidGraphics.BeforeApplyQuality(options);QualitySettings.shadows=global;AndroidGraphics.Enforce();
            AndroidVolumetrics.SetLightShadows(point,LightShadows.Hard);collector.SimulatePreCull();collector.SimulatePreRender();
            Check(!volume.enabled&&!collector.enabled&&!sunCollector.enabled&&Lifecycle.directionalShadowCount==0&&!collector.shadowBuffer&&point.shadows==LightShadows.Hard&&QualitySettings.shadows==global,"Actual Apply and local shadow changes preserve requested surface state while keeping blocked collectors stopped");
        }
        foreach(int quality in new[]{0,1,2,3,4,5})
        {
            Select(options,quality);AndroidGraphics.BeforeSerializeQuality(options);AndroidGraphics.Enforce();volume.SimulateRender();
            Check(!volume.enabled&&!collector.enabled&&!sunCollector.enabled&&volume.frames==0&&main.renderingPath==RenderingPath.DeferredShading&&ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Every real graphics profile honors the independent AO switch without enabling G715 Aura");
        }
        AndroidGraphics.BeginScene();AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");AndroidGraphics.Observe(volume,"Aura2API.AuraCamera");
        AndroidGraphics.Observe(collector,"Aura2API.AuraLight");AndroidGraphics.Observe(sunCollector,"Aura2API.AuraLight");AndroidGraphics.Enforce();volume.SimulateRender();
        Check(!volume.enabled&&!collector.enabled&&!sunCollector.enabled&&volume.frames==0&&ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"Real same-scene recapture reenforces the guard before rendering without losing AO");
        SystemInfo.graphicsDeviceName="Adreno (TM) 735";AndroidGraphics.Enforce();
        Check(volume.enabled&&collector.enabled&&sunCollector.enabled&&ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"The same observed authored state still restores full Adreno volume behavior");
    }
    static void MaliHigherShadowBudgetIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Mali-G715";SystemInfo.supportedRenderTargetCount=4;
        AndroidVolumetrics.BeforeQualityChange();QualitySettings.shadows=ShadowQuality.All;
        var root=new GameObject("G715 shadow budget");root.scene=new Scene{buildIndex=1};var main=root.AddComponent<Camera>();
        var ao=root.AddComponent<HBAO>();ao.enabled=false;
        var point=LightAt(1);var spot=LightAt(2,LightType.Spot,LightShadows.Hard);var sun=LightAt(0,LightType.Directional);
        var distant=LightAt(40);distant.range=100;
        Camera.main=main;Resources.objects=new object[]{main,point,spot,sun,distant};Time.unscaledTime=200;
        AndroidGraphics.BeginScene();AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");
        var options=new Options(0,true);
        int ShadowCount(){int count=0;foreach(var value in Resources.objects)if(value is Light light&&light.isActiveAndEnabled&&light.shadows!=LightShadows.None)++count;return count;}
        foreach(int quality in new[]{0,1,2,5})
        {
            Select(options,quality);
            Check(point.shadows==LightShadows.Soft&&spot.shadows==LightShadows.None&&sun.shadows==LightShadows.Soft&&distant.shadows==LightShadows.None&&ShadowCount()==2,"Every G715 higher/Custom profile retains one authored local shadow plus one sun");
            Check(point.enabled&&spot.enabled&&sun.enabled&&point.intensity==2&&spot.intensity==2&&point.range==10&&spot.range==10&&QualitySettings.pixelLightCount==3&&QualitySettings.masterTextureLimit==0,"Reducing G715 shadow slots preserves illumination, range, pixel-light count and prepared mips");
            Check(main.renderingPath==RenderingPath.DeferredShading&&main.allowHDR&&main.farClipPlane==160&&ao.enabled&&ao.normals==HBAO.PerPixelNormals.GBuffer&&main.depthTextureMode==DepthTextureMode.Depth&&QualitySettings.shadowDistance==30,"G715 budget leaves Deferred/HDR, geometry distance, authored shadow distance and AO unchanged");
        }
        int scans=Resources.scans;
        spot.transform.position=new Vector3(.9f,0,0);Time.unscaledTime=200.3f;AndroidGraphics.Enforce();
        Check(point.shadows==LightShadows.Soft&&spot.shadows==LightShadows.None,"A ten-percent closer local does not defeat the existing twenty-percent hysteresis");
        spot.transform.position=new Vector3(.7f,0,0);Time.unscaledTime=200.31f;AndroidGraphics.Enforce();
        Check(point.shadows==LightShadows.Soft&&spot.shadows==LightShadows.None,"A valid G715 slot waits for the existing quarter-second refresh");
        Time.unscaledTime=200.6f;AndroidGraphics.Enforce();
        Check(point.shadows==LightShadows.None&&spot.shadows==LightShadows.Hard&&sun.shadows==LightShadows.Soft&&ShadowCount()==2,"A clearly closer spotlight replaces the single slot and retains its authored Hard mode");
        spot.enabled=false;Time.unscaledTime=200.61f;AndroidGraphics.Enforce();
        Check(!spot.enabled&&spot.shadows==LightShadows.None&&point.shadows==LightShadows.Soft&&ShadowCount()==2,"Disabling the selected local immediately restores the other authored Soft shadow without reenabling it");
        spot.enabled=true;Time.unscaledTime=200.9f;AndroidGraphics.Enforce();spot.gameObject.SetActive(false);Time.unscaledTime=200.91f;AndroidGraphics.Enforce();
        Check(!spot.gameObject.activeSelf&&spot.shadows==LightShadows.None&&point.shadows==LightShadows.Soft,"An inactive selected local is immediately replaced without changing hierarchy");
        point.range=100;point.transform.position=new Vector3(30.1f,0,0);Time.unscaledTime=200.92f;AndroidGraphics.Enforce();
        Check(point.shadows==LightShadows.None&&distant.shadows==LightShadows.None&&sun.shadows==LightShadows.Soft&&ShadowCount()==1,"The G715 local ceiling remains thirty meters even for a larger authored range");
        Check(Resources.scans==scans,"G715 refresh, hysteresis and invalidation reuse cached lights without global scans");
        point.range=10;point.transform.position=new Vector3(1,0,0);spot.gameObject.SetActive(true);spot.transform.position=new Vector3(2,0,0);
        Select(options,3);Check(point.shadows==LightShadows.Hard&&spot.shadows==LightShadows.None&&sun.shadows==LightShadows.None&&ShadowCount()==1&&main.farClipPlane==80,"G715 High to Low retains the established single Hard local policy");
        Select(options,4);Check(ShadowCount()==0&&ao.enabled&&main.depthTextureMode==DepthTextureMode.Depth,"G715 Minimum disables shadows while retaining the independent AO choice");
        Select(options,0);Check(point.shadows==LightShadows.Soft&&spot.shadows==LightShadows.None&&sun.shadows==LightShadows.Soft&&ShadowCount()==2&&ao.enabled&&main.farClipPlane==160,"Returning to High restores one authored Soft local plus sun and the original AO/geometry policy");
        foreach(string gpu in new[]{"Adreno (TM) 735","Mali-G76",null})
        {
            SystemInfo.graphicsDeviceName=gpu;AndroidGraphics.BeginScene();Select(options,0);
            Check(point.shadows==LightShadows.Soft&&spot.shadows==LightShadows.Hard&&sun.shadows==LightShadows.Soft&&ShadowCount()==3,"Other GPUs keep both authored local shadows plus sun");
        }
    }
    static void EffectControlsIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.graphicsDeviceName="Mali-G715";SystemInfo.supportedRenderTargetCount=4;
        var world=new GameObject("Effect world");world.scene=new Scene{buildIndex=1};
        var main=world.AddComponent<Camera>();var ao=world.AddComponent<HBAO>();var vhs=world.AddComponent<MonoBehaviour>();
        var post=world.AddComponent<PostProcessVolume>();var originalBloom=new Bloom();post.sharedProfile.settings.Add(originalBloom);
        var aux=new GameObject("Effect auxiliary");aux.scene=new Scene{buildIndex=1};var auxCamera=aux.AddComponent<Camera>();var auxVhs=aux.AddComponent<MonoBehaviour>();
        var auxPost=aux.AddComponent<PostProcessVolume>();auxPost.sharedProfile.settings.Add(new Bloom());
        Camera.main=main;Resources.objects=new object[]{main,auxCamera};AndroidGraphics.BeginScene();
        AndroidGraphics.Observe(ao,"HorizonBasedAmbientOcclusion.HBAO");AndroidGraphics.Observe(vhs,"postVHSPro");AndroidGraphics.Observe(auxVhs,"postVHSPro");
        AndroidGraphics.Observe(post,"UnityEngine.Rendering.PostProcessing.PostProcessVolume");AndroidGraphics.Observe(auxPost,"UnityEngine.Rendering.PostProcessing.PostProcessVolume");
        var owner=new Options(3,true);var toggles=new[]{owner.VHSEffect,owner.DepthOfField,owner.GrainToggle,owner.BloomToggle,owner.MotionBlurToggle,owner.AmbientOccToggle};
        string[] keys={"PostP_VHS","PostP_DepthOfField","PostP_Grain","PostP_Bloom","PostP_MotionBlur","PostP_AmbientOcclusion"};
        var content=new GameObject("Content");var oldGeneral=new GameObject("General content");
        for(int i=0;i<toggles.Length;++i)
        {
            var row=new GameObject("Effect row "+i);row.transform.SetParent(i==0?content.transform:oldGeneral.transform,false);
            toggles[i].transform.SetParent(row.transform,false);row.SetActive(i==0);
            var label=new GameObject("GraphicLabel",typeof(Text));label.transform.SetParent(row.transform,false);
            label.transform.localScale=new Vector3(i<2?2.2967f:1,i<2?2.2967f:1,i<2?2.2967f:1);
            ((RectTransform)label.transform).sizeDelta=new Vector2(i<2?272.15f:150,0);
        }
        AndroidGraphics.BeforeApplyQuality(owner);AndroidGraphics.Enforce();
        for(int i=0;i<toggles.Length;++i)
        {
            var row=(RectTransform)toggles[i].transform.parent;
            Check(row.parent==content.transform&&row.GetSiblingIndex()==i&&row.gameObject.activeSelf&&row.anchoredPosition.y==-34*(5+i)&&toggles[i].interactable,"Existing effect row is visible, interactive and ordered in the graphics content: "+i);
            var style=owner.VHSEffect.transform.parent.Find("GraphicLabel").GetComponent<Text>();var rowText=row.Find("GraphicLabel").GetComponent<Text>();
            Check(Object.ReferenceEquals(rowText.font,style.font)&&rowText.fontSize==style.fontSize&&toggles[i].colors.normalColor.Equals(new Color(.16f,.18f,.21f,1))&&toggles[i].targetGraphic.color.Equals(Color.white),"New effect rows share the readable font and neutral checkbox style: "+i);
            Check(rowText.rectTransform.localScale.Equals(style.rectTransform.localScale)&&rowText.rectTransform.sizeDelta.Equals(style.rectTransform.sizeDelta),"Restored labels preserve the visible scale and width of the VHS template: "+i);
        }
        Check(((RectTransform)content.transform).sizeDelta.y==404&&((RectTransform)owner.VHSEffect.transform).sizeDelta.x==60,"Graphics content fits eleven rows with wider checkbox hit targets");
        var labelText=owner.BloomToggle.transform.parent.Find("GraphicLabel").GetComponent<Text>();
        ClosingShift.Localization.RussianUiPresentation.RussianActive=true;AndroidGraphics.Enforce();Check(labelText.text=="Свечение","Effect label follows Russian language");
        ClosingShift.Localization.RussianUiPresentation.RussianActive=false;AndroidGraphics.Enforce();Check(labelText.text=="Bloom","Effect label restores English without altering the selected setting");
        Bloom bloom;Grain grain;DepthOfField dof;MotionBlur motion;
        Check(post.profile.TryGetSettings(out bloom)&&post.profile.TryGetSettings(out grain)&&post.profile.TryGetSettings(out dof)&&post.profile.TryGetSettings(out motion)&&post.profile.settings.Count==4,"Main runtime clone has exactly one of each switchable PPS effect");
        post.profile.TryGetSettings(out bloom);post.profile.TryGetSettings(out grain);post.profile.TryGetSettings(out dof);post.profile.TryGetSettings(out motion);
        Check(bloom.fastMode.value&&bloom.diffusion.value==4&&grain.intensity.value==.18f&&dof.kernelSize.value==KernelSize.Small&&motion.sampleCount.value==4,"Heavy effects use bounded mobile parameters");
        Check(originalBloom.active&&originalBloom.intensity.value==0&&post.sharedProfile.settings.Count==1,"Prepared mobile effect settings leave the authored profile intact");
        foreach(var toggle in toggles)toggle.isOn=false;
        AndroidGraphics.BeforeApplyQuality(owner);AndroidGraphics.Enforce();
        Check(!vhs.enabled&&!auxVhs.enabled&&!ao.enabled&&!bloom.active&&!grain.active&&!dof.active&&!motion.active&&main.depthTextureMode==DepthTextureMode.None,"All six effects can be disabled and release their camera inputs");
        for(int selected=0;selected<toggles.Length;++selected)
        {
            for(int i=0;i<toggles.Length;++i)toggles[i].isOn=i==selected;
            AndroidGraphics.BeforeApplyQuality(owner);AndroidGraphics.Enforce();
            Check(vhs.enabled==(selected==0)&&dof.active==(selected==1)&&grain.active==(selected==2)&&bloom.active==(selected==3)&&motion.active==(selected==4)&&ao.enabled==(selected==5),"Each individual checkbox controls only its requested effect: "+selected);
            Check(!auxVhs.enabled&&!auxPost.profile.settings[0].active,"Effect switches do not enable an auxiliary camera stack");
            DepthTextureMode expected=selected==4?DepthTextureMode.Depth|DepthTextureMode.MotionVectors:selected==1||selected==5?DepthTextureMode.Depth:DepthTextureMode.None;
            Check(main.depthTextureMode==expected,"Each selected effect requests only the camera inputs it needs: "+selected);
        }
        foreach(int selected in new[]{1,4})
        {
            for(int i=0;i<toggles.Length;++i)toggles[i].isOn=i==5;
            AndroidGraphics.BeforeSerializeQuality(owner);AndroidGraphics.Enforce();
            toggles[5].isOn=false;toggles[selected].isOn=true;
            AndroidGraphics.BeforeSerializeQuality(owner);AndroidGraphics.Enforce();
            Check(main.depthTextureMode==(selected==1?DepthTextureMode.Depth:DepthTextureMode.Depth|DepthTextureMode.MotionVectors),"Releasing AO cannot remove the live DOF or motion blur camera input: "+selected);
        }
        foreach(var toggle in toggles)toggle.isOn=true;
        AndroidGraphics.BeforeSerializeQuality(owner);var saved=AndroidGraphics.PrepareConfig(new Dictionary<string,string>{{"unknown","keep"}});
        Check(saved["AndroidEffectsVersion"]=="1"&&saved["unknown"]=="keep","Original configuration writer carries versioned effect keys and unrelated values");
        foreach(string key in keys)Check(saved[key]=="True","The original Graphic section persists the effect switch: "+key);
        var restarted=new Options(3,true);foreach(var entry in saved)restarted.configHandler.graphic[entry.Key]=entry.Value;
        AndroidGraphics.ConfigurationLoaded(restarted.configHandler);AndroidGraphics.BeforeApplyQuality(restarted);AndroidGraphics.Enforce();
        Check(restarted.VHSEffect.isOn&&restarted.DepthOfField.isOn&&restarted.GrainToggle.isOn&&restarted.BloomToggle.isOn&&restarted.MotionBlurToggle.isOn&&restarted.AmbientOccToggle.isOn&&vhs.enabled&&dof.active&&grain.active&&bloom.active&&motion.active&&ao.enabled,"Cold configuration and a new options owner restore every effect");
        Select(restarted,4);Check(vhs.enabled&&dof.active&&grain.active&&bloom.active&&motion.active&&ao.enabled&&QualitySettings.shadows==ShadowQuality.Disable,"Explicit effect choices remain independent of the quality and shadow preset");
        int count=post.profile.settings.Count,scans=Resources.scans;
        for(int i=0;i<8;++i)AndroidGraphics.Enforce();Check(post.profile.settings.Count==count&&Resources.scans==scans,"Stable enforcement neither adds duplicate effects nor rescans the scene");
        restarted.configHandler.graphic["PostP_VHS"]="invalid";restarted.configHandler.graphic["PostP_Bloom"]="invalid";
        AndroidGraphics.ConfigurationLoaded(restarted.configHandler);AndroidGraphics.Enforce();Check(vhs.enabled&&!bloom.active,"Corrupt booleans use the requested defaults before any original typed read");
        AndroidGraphics.ResetQuality(restarted);AndroidGraphics.Enforce();
        Check(restarted.VHSEffect.isOn&&!restarted.DepthOfField.isOn&&!restarted.GrainToggle.isOn&&!restarted.BloomToggle.isOn&&!restarted.MotionBlurToggle.isOn&&!restarted.AmbientOccToggle.isOn&&!ao.enabled&&vhs.enabled&&restarted.QualityDropdown.value==4,"Reset restores Minimum with VHS and no other effects");
        var minimum=new Options(4,true);AndroidGraphics.BeforeApplyQuality(minimum);AndroidGraphics.Enforce();Check(!minimum.AmbientOccToggle.isOn&&!ao.enabled,"A versionless Minimum config keeps its original no-AO default");
        SystemInfo.supportsMotionVectors=false;var unsupported=new Options(3,true);foreach(var entry in saved)unsupported.configHandler.graphic[entry.Key]=entry.Value;
        AndroidGraphics.ConfigurationLoaded(unsupported.configHandler);AndroidGraphics.BeforeApplyQuality(unsupported);AndroidGraphics.Enforce();
        Check(!unsupported.MotionBlurToggle.isOn&&!unsupported.MotionBlurToggle.interactable&&!motion.active&&(main.depthTextureMode&DepthTextureMode.MotionVectors)==0,"Unsupported motion vectors disable the effect and its control without allocating motion inputs");
        Check(unsupported.VHSEffect.isOn&&vhs.enabled&&unsupported.BloomToggle.interactable&&bloom.active,"Unsupported motion blur does not block the other effect switches");
        Check(AndroidGraphics.PrepareConfig(new Dictionary<string,string>())["PostP_MotionBlur"]=="False","Unsupported saved motion blur is normalized through the original writer");SystemInfo.supportsMotionVectors=true;
        Application.platform=RuntimePlatform.WindowsPlayer;var desktop=new Dictionary<string,string>();AndroidGraphics.PrepareConfig(desktop);Check(desktop.Count==0,"Desktop configurations receive no Android effect keys");Application.platform=RuntimePlatform.Android;
    }

    static void LowMemoryMinimumIntegration()
    {
        Application.platform=RuntimePlatform.Android;SystemInfo.systemMemorySize=3072;
        var owner=new Options(4,true);Select(owner,4);
        Check(QualitySettings.masterTextureLimit==2&&owner.TextureQualityDropdown.value==2,"Minimum on a3GB device uses quarter-dimension prepared textures before scene rendering");
        Check(QualitySettings.streamingMipmapsMemoryBudget==128&&QualitySettings.streamingMipmapsMaxLevelReduction==4,"Low-memory Minimum leaves room for the scene instead of filling the original512MB streaming cache");
        AndroidGraphics.BeforeSerializeQuality(owner);Check((int)owner.Read("TextureQualityLevel")==2,"Original config writer persists the same low-memory mip budget shown by the dropdown");
        Select(owner,3);Check(QualitySettings.masterTextureLimit==0&&owner.TextureQualityDropdown.value==0,"Other presets retain their full prepared texture policy");
        Check(QualitySettings.streamingMipmapsMemoryBudget==512&&QualitySettings.streamingMipmapsMaxLevelReduction==2,"Leaving low-memory Minimum restores the authored streaming budget and reduction");
        Select(owner,4);QualitySettings.masterTextureLimit=2;Select(owner,5);Check(QualitySettings.masterTextureLimit==2,"Custom keeps an explicit texture limit on a low-memory device");
        SystemInfo.systemMemorySize=8192;Select(owner,4);Check(QualitySettings.masterTextureLimit==0&&owner.TextureQualityDropdown.value==0,"Larger devices retain full prepared mips even on Minimum");
        SystemInfo.systemMemorySize=0;Select(owner,4);Check(QualitySettings.masterTextureLimit==0,"Unknown device RAM does not activate a guessed hardware policy");SystemInfo.systemMemorySize=8192;
    }
    static void Main()
    {
        AndroidGraphics.SetSupporterActive(null,true);Check(true,"Missing menu supporter target is safe");
        var supporterTarget=new GameObject();AndroidGraphics.SetSupporterActive(supporterTarget,false);Check(!supporterTarget.activeSelf,"Existing supporter content keeps the authored OFF action");
        AndroidGraphics.SetSupporterActive(supporterTarget,true);Check(supporterTarget.activeSelf,"Existing supporter content keeps the authored ON action");
        var config=new Config();AndroidGraphics.BeforeConfiguration(config);Check(config.savePath==ConfigPath.DocumentsPath,"GameConfig menu and gameplay share Documents path");
        var customConfig=new Config{ConfigName="Custom"};AndroidGraphics.BeforeConfiguration(customConfig);Check(customConfig.savePath==ConfigPath.GameDataPath,"Custom filename path remains authored");
        var otherPath=new Config{savePath=ConfigPath.Other};AndroidGraphics.BeforeConfiguration(otherPath);Check(otherPath.savePath==ConfigPath.Other,"Explicit other storage path remains intact");
        PlayerPrefs.SetString("GameConfig","Custom");var overrideCustom=new Config{manager=new object()};AndroidGraphics.BeforeConfiguration(overrideCustom);Check(overrideCustom.savePath==ConfigPath.GameDataPath,"HFPS custom filename override retains path");
        var noManager=new Config();AndroidGraphics.BeforeConfiguration(noManager);Check(noManager.savePath==ConfigPath.DocumentsPath&&PlayerPrefs.GetString("GameConfig")=="Custom","Non-HFPS defaults ignore custom override without mutating prefs");PlayerPrefs.DeleteAll();
        var main=new Camera();main.gameObject.scene=new Scene{buildIndex=1};var aux=new Camera();aux.gameObject.scene=new Scene{buildIndex=1};Camera.main=main;Resources.objects=new object[]{main,aux};AndroidGraphics.BeginScene();
        var layer=new PostProcessLayer();layer.gameObject.camera=main;var auxLayer=new PostProcessLayer();auxLayer.gameObject.camera=aux;
        var volume=new PostProcessVolume();volume.gameObject.camera=main;var expensive=new PostProcessEffectSettings();var sourceBloom=new Bloom();volume.sharedProfile.settings.Add(expensive);volume.sharedProfile.settings.Add(sourceBloom);
        var beautify=new MonoBehaviour();beautify.gameObject.camera=main;var aura=new AuraCamera();
        AndroidGraphics.Observe(layer,"UnityEngine.Rendering.PostProcessing.PostProcessLayer");AndroidGraphics.Observe(auxLayer,"UnityEngine.Rendering.PostProcessing.PostProcessLayer");AndroidGraphics.Observe(volume,"UnityEngine.Rendering.PostProcessing.PostProcessVolume");AndroidGraphics.Observe(beautify,"BeautifyEffect.Beautify");AndroidGraphics.Observe(aura,"Aura2API.AuraCamera");
        var first=new Options();AndroidGraphics.BeforeApplyQuality(first);
        Check(first.QualityDropdown.value==4&&(int)first.Read("QualityLevel")==4&&first.VHSEffect.isOn&&!first.AmbientOccToggle.isOn,"Fresh config uses Minimum with VHS and no AO");
        first.QualityDropdown.value=3;AndroidGraphics.QualitySelected(first,first.QualityDropdown);AndroidGraphics.BeforeApplyQuality(first);
        Check(QualitySettings.shadows==ShadowQuality.HardOnly&&QualitySettings.shadowCascades==0&&QualitySettings.shadowDistance==20&&QualitySettings.shadowResolution==ShadowResolution.Low,"Low hard-shadow atlas stays bounded");
        Check(QualitySettings.lodBias==1&&QualitySettings.masterTextureLimit==0&&QualitySettings.antiAliasing==0&&QualitySettings.anisotropicFiltering==AnisotropicFiltering.Enable,"Low LOD/full textures and authored anisotropy remain");
        Check(QualitySettings.pixelLightCount==1,"Low uses one pixel light while Auto lights retain vertex lighting");
        Check(main.renderingPath==RenderingPath.Forward&&main.allowHDR&&main.farClipPlane==80&&main.depthTextureMode==DepthTextureMode.None&&main.forceIntoRenderTexture,"Low Forward HDR keeps authored output and omits unused depth texture");
        Check(aux.renderingPath==RenderingPath.Forward&&!aux.allowHDR&&aux.farClipPlane==500&&aux.forceIntoRenderTexture&&aux.depthTextureMode==(DepthTextureMode.DepthNormals|DepthTextureMode.MotionVectors),"Auxiliary camera keeps native geometry and authored depth/forceRT");
        Check(Screen.width==2400&&Screen.height==1080&&MobileRenderSettings.RenderPercent==75&&MobileRenderSettings.GetRenderResolution().height==810,"Native UI and independent default75 world resolution");
        Check(((Resolution[])first.Read("Resolutions")).Length==1&&((Resolution[])first.Read("Resolutions"))[0].height==1080&&(int)first.Read("ResolutionLevel")==0&&(bool)first.Read("fullscreen")&&(bool)first.Read("fullscreenChanged"),"Original config fields use valid native fullscreen values before Apply");
        first.ResolutionDropdown.onValueChanged.AddListener(index=>{throw new Exception("Obsolete desktop resolution callback survived");});AndroidGraphics.Enforce();
        Check(first.ResolutionDropdown.options.Count==6&&first.ResolutionDropdown.options[0].text=="25%"&&first.ResolutionDropdown.options[5].text=="100%"&&first.ResolutionDropdown.onValueChanged.count==1,"Six-step percent control replaces desktop listener");
        Check(first.ResolutionDropdown.transform.parent.Find("GraphicLabel").GetComponent<Text>().text=="3D render percentage"&&!first.FullscreenToggle.transform.parent.gameObject.activeSelf&&first.FullscreenToggle.isOn&&!first.FullscreenToggle.interactable,"Clear render label and hidden obsolete fullscreen row");
        Check(first.VSyncDropdown.options.Count==4&&first.VSyncDropdown.options[0].text=="30"&&first.VSyncDropdown.options[3].text=="Display"&&first.VSyncDropdown.onValueChanged.count==1,"Original VSync row hosts30/45/60/display FPS with one listener");
        Check(first.VSyncDropdown.shownText=="30","Initial FPS caption replaces the old VSync caption at index zero");
        first.VSyncDropdown.ClearOptions();foreach(string text in new[]{"Off","1 frame","2 frames"})first.VSyncDropdown.options.Add(new Dropdown.OptionData(text));
        AndroidGraphics.Enforce();Check(first.VSyncDropdown.options.Count==4&&first.VSyncDropdown.shownText=="30"&&first.VSyncDropdown.onValueChanged.count==1,"FPS recovers original localization refill without an invalid read or duplicated listener");
        first.VSyncDropdown.onValueChanged.Invoke(2);Check(AndroidInputSettings.FpsMode==2&&PlayerPrefs.GetInt("ClosingShift.FpsMode",-1)==2,"FPS selection persists through existing input preferences");
        first.VSyncDropdown.SetValueWithoutNotify(2);int capSaves=PlayerPrefs.saves;
        foreach(int legacyVsync in new[]{0,1,2,0})AndroidGraphics.SetLegacyFpsSelection(first.VSyncDropdown,legacyVsync);
        Check(first.VSyncDropdown.value==2&&AndroidInputSettings.FpsMode==2&&PlayerPrefs.saves==capSaves,"All four original VSync assignments leave the FPS choice and saved cap untouched");
        AndroidGraphics.BeforeApplyQuality(first);AndroidGraphics.Enforce();Check(first.VSyncDropdown.value==2&&(int)first.Read("VSyncLevel")==0,"Original Apply cannot overwrite the selected cap or enable native VSync");
        AndroidInputSettings.FpsValues=new[]{30,40,60,90,120,0};AndroidInputSettings.FpsMode=4;AndroidGraphics.Enforce();
        Check(first.VSyncDropdown.options.Count==6&&first.VSyncDropdown.options[4].text=="120"&&first.VSyncDropdown.options[5].text=="Display"&&first.VSyncDropdown.value==4,"Reported display modes replace the fixed four-row list without losing the selected cap");
        ClosingShift.Localization.RussianUiPresentation.RussianActive=true;AndroidGraphics.Enforce();
        Check(first.VSyncDropdown.options[5].text=="Экран"&&first.VSyncDropdown.options[3].text=="90","Display label follows the actual last item and does not overwrite a numeric frequency");
        ClosingShift.Localization.RussianUiPresentation.RussianActive=false;AndroidInputSettings.FpsValues=new[]{30,45,60,0};
        AndroidInputSettings.FpsMode=0;
        ClosingShift.Localization.RussianUiPresentation.RussianActive=true;AndroidGraphics.Enforce();Check(first.ResolutionDropdown.transform.parent.Find("GraphicLabel").GetComponent<Text>().text=="Процент рендера 3D"&&first.VSyncDropdown.options[3].text=="Экран","Render and FPS controls follow Russian language changes");ClosingShift.Localization.RussianUiPresentation.RussianActive=false;
        first.ResolutionDropdown.onValueChanged.Invoke(1);Check(MobileRenderSettings.RenderPercent==33&&MobileRenderSettings.GetRenderResolution().height==356&&Screen.height==1080,"Percent selection changes RT without resizing native UI");
        Check(!volume.profile.settings[0].active&&!volume.profile.settings[1].active&&expensive.active&&sourceBloom.active&&!Object.ReferenceEquals(volume.profile,volume.sharedProfile),"Bloom and expensive effects disabled in runtime clone; author profile intact");
        Check(layer.enabled&&layer.antialiasingMode==PostProcessLayer.Antialiasing.FastApproximateAntialiasing&&layer.fastApproximateAntialiasing.fastMode&&!auxLayer.enabled&&!beautify.enabled&&!aura.enabled,"Main FXAA/output layer stays enabled; desktop stacks disabled");
        volume.profile.settings[0].active=volume.profile.settings[1].active=true;AndroidGraphics.Enforce();Check(!volume.profile.settings[0].active&&!volume.profile.settings[1].active&&volume.profile.settings.Count==5,"Callbacks cannot reactivate unselected effects or add duplicate settings");
        var original=new Dictionary<string,string>{{"GraphicQuality","3"},{"unknown","keep"}};Check(Object.ReferenceEquals(original,AndroidGraphics.PrepareConfig(original))&&original["unknown"]=="keep"&&original["AndroidLowProfileVersion"]=="1","Original config dictionary remains quality writer");
        foreach(int quality in new[]{4,3,2,5,3}){Select(first,quality);Check(MobileRenderSettings.RenderPercent==33&&Screen.height==1080&&Screen.fullScreen,"Every quality preserves render percentage and native fullscreen");}
        Select(first,4);Check(layer.enabled&&layer.antialiasingMode==PostProcessLayer.Antialiasing.None&&QualitySettings.shadows==ShadowQuality.Disable&&QualitySettings.shadowDistance==0,"Minimum keeps final output layer with no AA or shadows");
        Check(QualitySettings.pixelLightCount==1,"Minimum also uses one pixel light");
        {
            string gpu=SystemInfo.graphicsDeviceName;int targets=SystemInfo.supportedRenderTargetCount;
            bool bloom=first.BloomToggle.isOn,grain=first.GrainToggle.isOn,dof=first.DepthOfField.isOn,motion=first.MotionBlurToggle.isOn;
            first.BloomToggle.isOn=first.GrainToggle.isOn=first.DepthOfField.isOn=first.MotionBlurToggle.isOn=false;
            SystemInfo.graphicsDeviceName="Adreno (TM) 618";SystemInfo.supportedRenderTargetCount=4;
            Select(first,4);AndroidGraphics.Enforce();Check(!layer.enabled,"Adreno Minimum without PPS effects skips the inactive final copy");
            first.BloomToggle.isOn=true;Select(first,4);AndroidGraphics.Enforce();Check(layer.enabled&&layer.antialiasingMode==PostProcessLayer.Antialiasing.None,"An enabled PPS effect restores the Adreno layer");first.BloomToggle.isOn=false;
            Select(first,3);AndroidGraphics.Enforce();Check(layer.enabled&&layer.antialiasingMode==PostProcessLayer.Antialiasing.FastApproximateAntialiasing,"Adreno Low keeps FXAA output");
            SystemInfo.graphicsDeviceName="Mali-G715";Select(first,4);AndroidGraphics.Enforce();Check(layer.enabled,"Mali keeps the final output path at Minimum");
            SystemInfo.graphicsDeviceName=gpu;SystemInfo.supportedRenderTargetCount=targets;
            first.BloomToggle.isOn=bloom;first.GrainToggle.isOn=grain;first.DepthOfField.isOn=dof;first.MotionBlurToggle.isOn=motion;
            Select(first,4);AndroidGraphics.Enforce();Check(layer.enabled,"Fixture device restored");
        }
        first.ShadowDistanceSlider.value=100;Select(first,2);Check(main.farClipPlane==160&&QualitySettings.lodBias==2&&QualitySettings.shadowDistance==30&&main.depthTextureMode==DepthTextureMode.None&&main.forceIntoRenderTexture,"Higher applies only visibility ceilings while retaining authored LOD and safe output");
        Check(first.ShadowDistanceSlider.value==30&&(int)first.Read("QualityLevel")==2&&aux.farClipPlane==500,"Before original Apply, native slider/private quality agree and auxiliary far is unchanged");
        first.ShadowDistanceSlider.value=12;Select(first,5);Check(QualitySettings.shadowDistance==12&&first.ShadowDistanceSlider.value==12,"A shorter selected shadow distance is preserved");
        AndroidGraphics.ResetQuality(first);Check(first.QualityDropdown.value==4&&MobileRenderSettings.RenderPercent==33,"Quality reset preserves render percent");
        Check(!(bool)first.Read("resChanged"),"Reset prefix leaves native resolution clean before the authored body");
        int percentSaves=PlayerPrefs.saves;first.AuthoredResetBranch(false);
        Check(MobileRenderSettings.RenderPercent==33&&first.ResolutionDropdown.value==1&&PlayerPrefs.saves==percentSaves&&!(bool)first.Read("resChanged"),"Actual serialized Reset(false) branch cannot notify the percent listener or change its saved selection");
        first.AuthoredResetBranch(true);Check(MobileRenderSettings.RenderPercent==33&&PlayerPrefs.saves==percentSaves,"Reset(true) also preserves render percentage");
        var desktopResolution=new Dropdown();desktopResolution.SetValueWithoutNotify(1);int desktopResolutionEvents=0;desktopResolution.onValueChanged.AddListener(index=>++desktopResolutionEvents);Application.platform=RuntimePlatform.WindowsPlayer;
        AndroidGraphics.SetLegacyResolutionSelection(desktopResolution,0);Check(desktopResolution.value==0&&desktopResolutionEvents==1,"Desktop legacy reset retains the original dropdown write and notification");Application.platform=RuntimePlatform.Android;
        var desktopFps=new Dropdown();desktopFps.SetValueWithoutNotify(2);int fpsEvents=0;desktopFps.onValueChanged.AddListener(index=>++fpsEvents);Application.platform=RuntimePlatform.WindowsPlayer;
        AndroidGraphics.SetLegacyFpsSelection(desktopFps,1);Check(desktopFps.value==1&&fpsEvents==1,"Desktop native VSync assignments retain their normal notification");Application.platform=RuntimePlatform.Android;
        var restarted=new Options(4,true);AndroidGraphics.BeforeApplyQuality(restarted);Check(restarted.QualityDropdown.value==4&&MobileRenderSettings.RenderPercent==33,"Saved quality reload remains independent");
        var early=new Options();AndroidGraphics.ApplyOptions(early);early.configHandler.graphic["AndroidLowProfileVersion"]="1";early.configHandler.graphic["GraphicQuality"]="4";AndroidGraphics.BeforeApplyQuality(early);Check(early.QualityDropdown.value==4,"Early scan waits for config load");
        var cold=new Config();cold.graphic["AndroidLowProfileVersion"]="1";cold.graphic["GraphicQuality"]="4";AndroidGraphics.ConfigurationLoaded(cold);Check(MobileRenderSettings.RenderPercent==33&&Screen.height==1080,"Cold config preserves render percent and native output");cold.graphic["GraphicQuality"]="invalid";AndroidGraphics.ConfigurationLoaded(cold);Check(cold.graphic["GraphicQuality"]=="4","Corrupt quality normalized before original typed read");
        var corrupt=new Options(5,true);corrupt.configHandler.graphic["GraphicQuality"]="999";AndroidGraphics.BeforeApplyQuality(corrupt);Check(corrupt.QualityDropdown.value==4,"Out-of-range quality falls back Minimum");
        var legacy=new Options(4);AndroidGraphics.BeforeApplyQuality(legacy);AndroidGraphics.ApplyOptions(legacy);Check(legacy.QualityDropdown.value==4&&legacy.VHSEffect.isOn&&!legacy.BloomToggle.isOn&&!legacy.DepthOfField.isOn&&!legacy.MotionBlurToggle.isOn&&!legacy.AmbientOccToggle.isOn&&legacy.BloomToggle.interactable&&legacy.BloomToggle.notifications==0,"Versionless settings use Minimum with VHS and interactive effects");
        MobileRenderSettings.SetRenderPercent(51);var customPercent=new Options(3,true);AndroidGraphics.BeforeApplyQuality(customPercent);AndroidGraphics.Enforce();Check(customPercent.ResolutionDropdown.options.Count==7&&customPercent.ResolutionDropdown.options[6].text=="51%"&&customPercent.ResolutionDropdown.value==6,"Saved valid nonpreset percentage remains visible");MobileRenderSettings.SetRenderPercent(50);
        Screen.fullScreen=false;MobileRenderSettings.Initialize();Check(Screen.fullScreen&&Screen.height==1080,"Fullscreen enforcement restores native Android output");
        int oldCalls=Screen.calls;var desktop=new Options(1);Application.platform=RuntimePlatform.WindowsPlayer;AndroidGraphics.BeforeApplyQuality(desktop);AndroidGraphics.ResetQuality(desktop);AndroidGraphics.QualitySelected(desktop,desktop.QualityDropdown);AndroidGraphics.ApplyOptions(desktop);Check(desktop.QualityDropdown.value==1&&desktop.VHSEffect.isOn&&Screen.calls==oldCalls&&AndroidGraphics.GetDefaultEffectEnabled(),"Desktop settings and effects remain authored");Application.platform=RuntimePlatform.Android;
        var menu=new Camera{farClipPlane=1000};menu.gameObject.scene=new Scene{buildIndex=0};Camera.main=menu;Resources.objects=new object[]{menu};AndroidGraphics.BeginScene();Check(menu.farClipPlane==1000,"Menu retains Canvas-compatible far1000");
        var view=new Camera();view.gameObject.scene=new Scene{buildIndex=1};Camera.main=view;var near=LightAt(2);var spot=LightAt(4,LightType.Spot);var far=LightAt(30);var sun=LightAt(0,LightType.Directional);var unshadowed=LightAt(.1f,LightType.Point,LightShadows.None);var baked=LightAt(.1f);baked.bakingOutput=new LightBakingOutput{lightmapBakeType=LightmapBakeType.Baked};var disabled=LightAt(.1f);disabled.enabled=false;var inactive=LightAt(.1f);inactive.gameObject.activeInHierarchy=false;var prefab=LightAt(.1f);prefab.gameObject.scene=new Scene{invalid=true};
        var budget=new Options(3,true);Select(budget,3);Resources.objects=new object[]{view,near,spot,far,sun,unshadowed,baked,disabled,inactive,prefab};Time.unscaledTime=10;AndroidGraphics.BeginScene();
        Check(view.farClipPlane==80&&near.shadows==LightShadows.Hard&&spot.shadows==LightShadows.None&&far.shadows==LightShadows.None&&sun.shadows==LightShadows.None,"Low selects one nearby local shadow and retains far80");
        Check(baked.shadows==LightShadows.None&&disabled.shadows==LightShadows.None&&inactive.shadows==LightShadows.None&&unshadowed.shadows==LightShadows.None&&prefab.shadows==LightShadows.Soft,"Eligibility excludes baked/disabled/inactive/unshadowed/prefab lights");
        Check(near.enabled&&near.intensity==2&&near.range==10&&!disabled.enabled&&!inactive.gameObject.activeInHierarchy,"Shadow policy preserves illumination and hierarchy");
        int scans=Resources.scans;for(int i=0;i<8;++i){Time.unscaledTime+=.03f;AndroidGraphics.Enforce();}Check(Resources.scans==scans,"Steady frames reuse cached lights without resource scans");
        view.transform.position=new Vector3(3.1f,0,0);Time.unscaledTime=10.3f;AndroidGraphics.Enforce();Check(near.shadows==LightShadows.Hard&&spot.shadows==LightShadows.None,"Slightly closer light does not churn shadow selection");view.transform.position=new Vector3(3.2f,0,0);Time.unscaledTime=10.31f;AndroidGraphics.Enforce();Check(near.shadows==LightShadows.Hard,"Valid light waits for bounded refresh");Time.unscaledTime=10.6f;AndroidGraphics.Enforce();Check(spot.shadows==LightShadows.Hard&&near.shadows==LightShadows.None,"Sufficiently closer local light takes the budget");
        spot.enabled=false;Time.unscaledTime=10.61f;AndroidGraphics.Enforce();Check(near.shadows==LightShadows.Hard&&!spot.enabled,"Disabled current light replaced immediately without enabling it");view.transform.position=new Vector3(25,0,0);Time.unscaledTime=10.62f;AndroidGraphics.Enforce();Check(far.shadows==LightShadows.Hard&&near.shadows==LightShadows.None,"Leaving range switches immediately");far.range=1;Time.unscaledTime=10.63f;AndroidGraphics.Enforce();Check(far.shadows==LightShadows.None,"No eligible local light leaves budget empty");far.range=100;view.transform.position=new Vector3(55,0,0);Time.unscaledTime=10.9f;AndroidGraphics.Enforce();Check(far.shadows==LightShadows.None,"Authored large range cannot extend Low shadows beyond20m");
        Select(budget,4);Check(near.shadows==LightShadows.None&&sun.shadows==LightShadows.None&&QualitySettings.shadows==ShadowQuality.Disable,"Minimum disables every cached shadow");Select(budget,2);Check(near.shadows==LightShadows.None&&spot.shadows==LightShadows.None&&far.shadows==LightShadows.Soft&&sun.shadows==LightShadows.Soft&&unshadowed.shadows==LightShadows.None,"Higher retains authored modes only for nearby budgeted lights and sun");Select(budget,5);Check(view.renderingPath==RenderingPath.Forward,"Custom retains safe Forward path");Select(budget,3);AndroidGraphics.BeginScene();Select(budget,2);Check(near.shadows==LightShadows.None&&far.shadows==LightShadows.Soft&&sun.shadows==LightShadows.Soft,"Repeated scan restores source modes before reapplying higher budget");
        Select(budget,3);Camera.main=null;Time.unscaledTime=11.2f;AndroidGraphics.Enforce();Check(near.shadows==LightShadows.None,"Losing main camera clears its shadow budget immediately");near.shadows=LightShadows.Soft;view.renderingPath=RenderingPath.DeferredShading;Application.platform=RuntimePlatform.WindowsPlayer;AndroidGraphics.BeginScene();AndroidGraphics.Enforce();Check(near.shadows==LightShadows.Soft&&view.renderingPath==RenderingPath.DeferredShading,"Desktop light and camera state passes through unchanged");
        Application.platform=RuntimePlatform.Android;
        var shorter=new Camera{farClipPlane=120};shorter.gameObject.scene=new Scene{buildIndex=1};Camera.main=shorter;Resources.objects=new object[]{shorter};AndroidGraphics.BeginScene();
        var high=new Options(2,true);high.ShadowDistanceSlider.value=100;QualitySettings.masterTextureLimit=2;AndroidGraphics.BeforeApplyQuality(high);
        Check(shorter.farClipPlane==120&&QualitySettings.shadowDistance==30&&QualitySettings.masterTextureLimit==0&&QualitySettings.lodBias==2&&high.TextureQualityDropdown.value==0&&(int)high.Read("TextureQualityLevel")==0,"Higher preset preserves shorter far/LOD and uses full prepared textures instead of reducing them to128px");
        Select(high,5);high.TextureQualityDropdown.value=2;QualitySettings.masterTextureLimit=2;AndroidGraphics.Enforce();
        Check(QualitySettings.masterTextureLimit==2&&high.TextureQualityDropdown.value==2,"Custom retains a manually selected mip reduction");
        // Match the two relevant setters in original UICustomOptions.ApplyGraphic.
        void ApplyShadowFields(Options owner)
        {
            if ((bool)owner.Read("swResChanged")) QualitySettings.shadowResolution=(ShadowResolution)(int)owner.Read("ShadowResolutionLevel");
            if ((bool)owner.Read("swCasChanged")) QualitySettings.shadowCascades=(int)owner.Read("ShadowCascadesLevel")==0 ? 0 : (int)owner.Read("ShadowCascadesLevel")==1 ? 2 : 4;
        }
        foreach (int higher in new[]{0,1,2,5})
        {
            high.ShadowResolutionDropdown.value=3;high.ShadowCascadesDropdown.value=2;
            high.ShadowQualityDropdown.value=2;high.ShadowDistanceSlider.value=100;
            Select(high,higher);
            Check(high.ShadowResolutionDropdown.value==1&&(int)high.Read("ShadowResolutionLevel")==1&&(bool)high.Read("swResChanged"),"Higher preset/Custom shadow-resolution UI and private apply field cap at Medium");
            Check(high.ShadowCascadesDropdown.value==0&&(int)high.Read("ShadowCascadesLevel")==0&&(bool)high.Read("swCasChanged"),"Higher preset/Custom zero-cascade UI and private apply field agree");
            ApplyShadowFields(high);
            Check(QualitySettings.shadowResolution==ShadowResolution.Medium&&QualitySettings.shadowCascades==0&&QualitySettings.lodBias==2&&shorter.farClipPlane==120&&QualitySettings.shadowDistance==30&&high.ShadowQualityDropdown.value==2,"Original apply setters consume capped fields without changing LOD, far, distance or shadow-quality selection");
        }
        high.ShadowResolutionDropdown.value=0;Select(high,5);ApplyShadowFields(high);
        Check(high.ShadowResolutionDropdown.value==0&&(int)high.Read("ShadowResolutionLevel")==0&&QualitySettings.shadowResolution==ShadowResolution.Low,"Custom lower shadow resolution is preserved");
        Select(high,3);ApplyShadowFields(high);
        Check(QualitySettings.shadowResolution==ShadowResolution.Low&&QualitySettings.shadowCascades==0&&QualitySettings.shadowDistance==20&&shorter.farClipPlane==80,"Low shadow policy remains unchanged");
        Select(high,4);ApplyShadowFields(high);
        Check(QualitySettings.shadows==ShadowQuality.Disable&&QualitySettings.shadowDistance==0&&shorter.farClipPlane==80,"Minimum shadow policy remains unchanged");
        var budgetCamera=new Camera();budgetCamera.gameObject.scene=new Scene{buildIndex=1};Camera.main=budgetCamera;
        var localA=LightAt(1);var localB=LightAt(5,LightType.Spot,LightShadows.Hard);var localC=LightAt(6);
        var distant=LightAt(31);distant.range=100;
        var bakedNear=LightAt(.2f);bakedNear.bakingOutput=new LightBakingOutput{lightmapBakeType=LightmapBakeType.Baked};
        var noShadow=LightAt(.1f,LightType.Point,LightShadows.None);
        var directionalA=LightAt(0,LightType.Directional);var directionalB=LightAt(0,LightType.Directional,LightShadows.Hard);
        Resources.objects=new object[]{budgetCamera,localA,localB,localC,distant,bakedNear,noShadow,directionalA,directionalB};Time.unscaledTime=100;
        AndroidGraphics.BeginScene();var higherBudget=new Options(0,true);Select(higherBudget,0);
        Check(localA.shadows==LightShadows.Soft&&localB.shadows==LightShadows.Hard&&localC.shadows==LightShadows.None&&directionalA.shadows==LightShadows.Soft&&directionalB.shadows==LightShadows.None,"Higher selects two nearest locals in their authored modes and only one sun");
        int ActiveShadowCount(){int n=0;foreach(var value in Resources.objects)if(value is Light light&&light.isActiveAndEnabled&&light.shadows!=LightShadows.None)n++;return n;}
        Check(ActiveShadowCount()==3&&distant.shadows==LightShadows.None&&bakedNear.shadows==LightShadows.None&&noShadow.shadows==LightShadows.None,"Higher shadow budget is bounded to three and excludes baked, distant and unshadowed lights");
        Check(localA.enabled&&localB.enabled&&localA.intensity==2&&localA.range==10&&distant.range==100&&QualitySettings.pixelLightCount==3,"Higher shadow budget preserves illumination and fits two local plus one directional pixel lights");
        localC.transform.position=new Vector3(4.5f,0,0);Time.unscaledTime=100.3f;AndroidGraphics.Enforce();
        Check(localB.shadows==LightShadows.Hard&&localC.shadows==LightShadows.None,"Higher does not churn for less than20 percent improvement");
        localC.transform.position=new Vector3(3.5f,0,0);Time.unscaledTime=100.31f;AndroidGraphics.Enforce();
        Check(localB.shadows==LightShadows.Hard,"Valid higher slots wait for quarter-second refresh");
        Time.unscaledTime=100.6f;AndroidGraphics.Enforce();
        Check(localC.shadows==LightShadows.Soft&&localB.shadows==LightShadows.None&&ActiveShadowCount()==3,"A sufficiently closer candidate replaces the worst higher slot");
        localC.enabled=false;Time.unscaledTime=100.61f;AndroidGraphics.Enforce();
        Check(localB.shadows==LightShadows.Hard&&!localC.enabled&&localC.shadows==LightShadows.None,"Invalid second slot is replaced immediately without enabling its old light");
        localB.gameObject.activeInHierarchy=false;Time.unscaledTime=100.62f;AndroidGraphics.Enforce();
        Check(ActiveShadowCount()==2&&localB.shadows==LightShadows.None&&!localB.gameObject.activeInHierarchy,"Inactive second slot clears immediately even before the timer");
        localB.gameObject.activeInHierarchy=true;localB.transform.position=new Vector3(5,0,0);localC.enabled=true;localC.range=.1f;Time.unscaledTime=100.9f;AndroidGraphics.Enforce();
        Check(localB.shadows==LightShadows.Hard&&localC.shadows==LightShadows.None,"Refill uses source range as well as thirty-meter ceiling");
        directionalA.enabled=false;Time.unscaledTime=100.91f;AndroidGraphics.Enforce();
        Check(directionalB.shadows==LightShadows.Hard&&directionalA.shadows==LightShadows.None&&!directionalA.enabled,"Inactive sun is replaced immediately in its authored mode");
        Select(higherBudget,3);Check(ActiveShadowCount()==1&&localA.shadows==LightShadows.Hard&&directionalB.shadows==LightShadows.None,"Higher to Low restores the original single hard local policy");
        Select(higherBudget,4);Check(ActiveShadowCount()==0&&QualitySettings.shadows==ShadowQuality.Disable,"Higher to Minimum keeps no shadows");
        Select(higherBudget,5);Check(ActiveShadowCount()==3&&localA.shadows==LightShadows.Soft&&localB.shadows==LightShadows.Hard&&directionalB.shadows==LightShadows.Hard,"Custom reuses higher budget and source soft/hard modes after profile transitions");
        localA.range=localB.range=localC.range=100;localA.transform.position=new Vector3(20,0,0);localB.transform.position=new Vector3(25,0,0);localC.transform.position=new Vector3(26,0,0);Time.unscaledTime=101.3f;AndroidGraphics.Enforce();
        localC.transform.position=new Vector3(2,0,0);distant.transform.position=new Vector3(3,0,0);Time.unscaledTime=101.6f;AndroidGraphics.Enforce();
        Check(localC.shadows==LightShadows.Soft&&distant.shadows==LightShadows.Soft&&localA.shadows==LightShadows.None&&localB.shadows==LightShadows.None&&ActiveShadowCount()==3,"Both higher slots can accept clearly closer newcomers during one bounded refresh");
        distant.transform.position=new Vector3(30.1f,0,0);Time.unscaledTime=101.61f;AndroidGraphics.Enforce();
        Check(distant.shadows==LightShadows.None&&localA.shadows==LightShadows.Soft&&ActiveShadowCount()==3,"Thirty-meter departure invalidates a selected higher light immediately despite large authored range");
        var cloneA=LightAt(2);var cloneB=LightAt(4,LightType.Spot,LightShadows.Hard);var cloneSun=LightAt(0,LightType.Directional);
        Resources.objects=new object[]{budgetCamera,cloneA,cloneB,cloneSun};AndroidGraphics.BeginScene();
        Check(localC.shadows==LightShadows.Soft&&localC.enabled&&distant.shadows==LightShadows.Soft,"Scene reset restores previous authored shadow modes without changing enabled states");
        Select(higherBudget,2);Check(cloneA.shadows==LightShadows.Soft&&cloneB.shadows==LightShadows.Hard&&cloneSun.shadows==LightShadows.Soft&&ActiveShadowCount()==3,"Scene clone obtains its own source shadows without stale slots");
        Camera.main=null;Time.unscaledTime=101;AndroidGraphics.Enforce();Check(ActiveShadowCount()==0,"Losing main camera clears both higher locals and sun immediately");
        Camera.main=budgetCamera;
        foreach(int quality in new[]{3,4,0,1,2,5,3})
        {
            Select(higherBudget,quality);
            Check(QualitySettings.pixelLightCount==(quality==3||quality==4?1:3),"Profile transitions select Low/Minimum1 and every higher/Custom3 pixel lights");
        }
        SystemInfo.graphicsDeviceName="Adreno (TM) 735";SystemInfo.supportedRenderTargetCount=4;
        budgetCamera.gameObject.scene=new Scene{buildIndex=1};AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.DeferredShading&&budgetCamera.allowHDR,"Adreno gameplay restores deferred local lighting");
        budgetCamera.gameObject.scene=new Scene{buildIndex=0};AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.Forward,"Menu stays on its established Forward path on Adreno");
        budgetCamera.gameObject.scene=new Scene{buildIndex=1};SystemInfo.supportedRenderTargetCount=3;AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.Forward,"Insufficient MRT support retains Forward");
        SystemInfo.supportedRenderTargetCount=4;SystemInfo.graphicsDeviceName="Mali-G76";AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.Forward,"Mali retains its verified output path");
        SystemInfo.graphicsDeviceName="Mali-G715";AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.DeferredShading,"Mali-G715 gameplay selects the deferred hardware candidate");
        budgetCamera.gameObject.scene=new Scene{buildIndex=0};AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.Forward,"Mali-G715 menus retain Forward");budgetCamera.gameObject.scene=new Scene{buildIndex=1};
        SystemInfo.graphicsDeviceName=null;AndroidGraphics.Enforce();
        Check(budgetCamera.renderingPath==RenderingPath.Forward,"Unknown GPU uses the established fallback");
        SystemInfo.graphicsDeviceName="Adreno (TM) 735";QualitySettings.pixelLightCount=7;Application.platform=RuntimePlatform.WindowsPlayer;
        AndroidGraphics.Enforce();AndroidGraphics.BeforeApplyQuality(higherBudget);
        Check(QualitySettings.pixelLightCount==7,"Desktop preserves its original pixel-light count");
        AmbientOcclusionIntegration();
        HiddenDisplayIntegration();
        SerializeQualityIntegration();
        MaliG715GuardIntegration();
        MaliHigherShadowBudgetIntegration();
        EffectControlsIntegration();
        LowMemoryMinimumIntegration();
        Console.WriteLine("GRAPHICS_POLICY_PASS "+checks+" assertions against actual AndroidGraphics/AO/Volumetrics/MobileRender source; no device");
    }
}
