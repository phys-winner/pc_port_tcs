using System;
using UnityEngine;
using ClosingShift.Android;

int count=0;
void Check(bool condition,string text){count++;if(!condition)throw new Exception(text);}
void Near(float value,float expected,string text){Check(Math.Abs(value-expected)<1e-7,text);}
var main=new Camera();var other=new Camera{name="OtherCamera"};
var componentLut=new Texture2D();var profileLut=new Texture2D{name="profile"};
var profile=new AuthorProfile{lut=true,lutIntensity=.7f,lutTexture=profileLut};
var source=new AuthorSource{camera=main,_lut=true,_lutIntensity=.4f,_lutTexture=componentLut};
var display=new Display{camera=main};
AndroidColorGrading.BeginScene();
AndroidColorGrading.Observe(source,"BeautifyEffect.Beautify");
AndroidColorGrading.Observe(display,"Colorful.BrightnessContrastGamma");
AndroidColorGrading.Enforce();Check(display.Material.writes==0,"No main camera must defer");
Camera.main=main;AndroidColorGrading.Enforce();
Check(display.Material.texture==componentLut,"Direct component LUT");Near(display.Material.parameters.w,.4f,"Direct strength");
Near(display.Material.parameters.x,1f/1024,"Atlas reciprocal width");Near(display.Material.parameters.y,1f/32,"Atlas reciprocal height");Near(display.Material.parameters.z,31,"Cube maximum");
int writes=display.Material.writes;AndroidColorGrading.Enforce();Check(display.Material.writes==writes,"Stable frames do not repeat material writes");
source._profile=profile;source._syncWithProfile=true;AndroidColorGrading.Enforce();
Check(display.Material.texture==profileLut,"Sync uses linked profile before OnEnable");Near(display.Material.parameters.w,.7f,"Linked strength");
Check(!source.enabled,"Does not re-enable full Beautify");Check(display.Brightness==-40,"Preserves brightness control");
profile.lut=false;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,0,"Disabled author LUT identity");
profile.lut=true;profile.lutIntensity=2;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,1,"Bound intensity");
profile.lutIntensity=-2;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,0,"Negative intensity bound");
profile.lutIntensity=1;profileLut.width=100;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,0,"Reject malformed LUT layout");
profileLut.width=1024;QualitySettings.activeColorSpace=ColorSpace.Gamma;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,0,"Linear source requirement");
QualitySettings.activeColorSpace=ColorSpace.Linear;AndroidColorGrading.Enforce();Near(display.Material.parameters.w,1,"Restore after valid data returns");
profile.vignetting=true;AndroidColorGrading.Enforce();
Near(display.Material.vignette.w,1.6f,"Authored profile alpha is multiplied by32");
Near(display.Material.vignette.x,.5f,"Profile vignette centerX");Near(display.Material.vignette.y,.5f,"Profile vignette centerY");
Near(display.Material.vignette.z,1,"Noncircular authored aspect");Near(display.Material.vignetteColor.x,.3f,"Authored vignette tint");
Near(display.Material.parameters.w,1,"Vignette leaves existing LUT bound");
writes=display.Material.writes;AndroidColorGrading.Enforce();Check(display.Material.writes==writes,"Stable vignette has no material writes");
profile.vignettingCircularShape=true;AndroidColorGrading.Enforce();Near(display.Material.vignette.z,.5f,"Circular vignette follows camera aspect");
main.aspect=4;AndroidColorGrading.Enforce();Near(display.Material.vignette.z,.25f,"Aspect change updates existing material");main.aspect=2;
profile.vignettingBlink=.25f;profile.vignettingBlinkStyle=BlinkStyle.Human;profile.vignettingFade=.1f;
writes=display.Material.writes;AndroidColorGrading.Enforce();
Check(display.Material.writes==writes+2,"Changing vignette does not rewrite LUT texture or parameters");
Near(display.Material.vignette.y,.375f,"Human blink offsets center");Near(display.Material.vignetteColor.x,.15f,"Blink dims tint");
Near(display.Material.vignetteColor.w,.225f,"Blink contributes to fade");
Check(Math.Abs(display.Material.vignette.z-1.33288948f)<1e-6f,"Blink overrides circular aspect with authored formula");
profile.vignettingMask=new Texture2D();AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Masked vignette safely neutral");Near(display.Material.vignetteColor.w,0,"Masked fade neutral");
profile.vignettingMask=null;profile.vignetting=false;AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Disabled author vignette neutral");
profile.vignetting=true;QualitySettings.activeColorSpace=ColorSpace.Gamma;AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Gamma context cannot use linear authored term");
QualitySettings.activeColorSpace=ColorSpace.Linear;profile.vignettingBlink=0;profile.vignettingFade=0;profile.vignettingCircularShape=false;
source.currentCamera=main;source._vignetting=true;source._vignettingBlink=.5f;source._vignettingCenter=new Vector2(.4f,.6f);source._vignettingBlinkStyle=BlinkStyle.Human;
AndroidColorGrading.Enforce();Near(display.Material.vignette.x,.4f,"After OnEnable, live component overrides profile vignette");
Near(display.Material.vignette.y,.35f,"Live Blink animation survives synced profile");Near(display.Material.vignetteColor.x,0,"Half-closed blink has black tint");
Near(display.Material.parameters.w,1,"Live vignette does not alter existing profile LUT selection");
source._vignettingBlink=.999f;AndroidColorGrading.Enforce();Near(display.Material.vignetteColor.w,1,"Nearly closed blink fills viewport");
source._vignettingBlink=float.NaN;AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Nonfinite author value cannot reach shader");
source._vignettingBlink=0;source._vignettingCenter=new Vector2(float.PositiveInfinity,.5f);AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Nonfinite center rejected");
source._vignettingCenter=new Vector2(.4f,.6f);source._vignettingAspectRatio=0;AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Invalid aspect rejected");
source._vignettingAspectRatio=1;AndroidColorGrading.Enforce();Near(display.Material.vignette.w,1.6f,"Valid live vignette recovers");
source._vignettingColor=new Color(.3f,.3f,.3f,float.MaxValue);AndroidColorGrading.Enforce();Near(display.Material.vignette.w,0,"Overflowing author intensity cannot reach shader");
source._vignettingColor=new Color(.3f,.3f,.3f,.05f);
display.Material=new Material{vignetteProperties=false};AndroidColorGrading.Enforce();Check(display.Material.writes==2,"Legacy LUT shader binds without vignette uniforms");
display.Material=new Material();AndroidColorGrading.Enforce();Check(display.Material.texture==profileLut,"Rebind recreated material");
Near(display.Material.vignette.w,1.6f,"Recreated material receives vignette");
display.Material=new Material{property=false};AndroidColorGrading.Enforce();Check(display.Material.writes==0,"Unpatched shader cannot bind");
var secondSource=new AuthorSource{camera=other,_lut=true,_lutIntensity=.2f,_lutTexture=componentLut};var secondDisplay=new Display{camera=other};
AndroidColorGrading.Observe(secondSource,"BeautifyEffect.Beautify");AndroidColorGrading.Observe(secondDisplay,"Colorful.BrightnessContrastGamma");
Camera.main=other;AndroidColorGrading.Enforce();Near(secondDisplay.Material.parameters.w,.2f,"New camera source isolation");
Application.platform=RuntimePlatform.WindowsPlayer;secondSource._lutIntensity=.8f;AndroidColorGrading.Enforce();Near(secondDisplay.Material.parameters.w,.2f,"Desktop untouched");
Application.platform=RuntimePlatform.Android;AndroidColorGrading.BeginScene();AndroidColorGrading.Enforce();Near(secondDisplay.Material.parameters.w,.2f,"Scene reset clears sources");
Console.WriteLine("AUTHOR_DISPLAY_BINDING_PASS "+count+" assertions against actual AndroidColorGrading.cs; no device");

enum BlinkStyle {Default,Human}
sealed class AuthorProfile {
    public bool lut;public float lutIntensity;public Texture2D lutTexture;
    public bool vignetting,vignettingCircularShape;public Color vignettingColor=new Color(.3f,.3f,.3f,.05f);
    public Vector2 vignettingCenter=new Vector2(.5f,.5f);public float vignettingAspectRatio=1,vignettingBlink,vignettingFade;
    public Texture2D vignettingMask;public BlinkStyle vignettingBlinkStyle;
}
sealed class AuthorSource:MonoBehaviour {
    public bool _lut,_syncWithProfile;public float _lutIntensity;public Texture2D _lutTexture;public AuthorProfile _profile;public Camera currentCamera;
    public bool _vignetting,_vignettingCircularShape;public Color _vignettingColor=new Color(.3f,.3f,.3f,.05f);
    public Vector2 _vignettingCenter=new Vector2(.5f,.5f);public float _vignettingAspectRatio=1,_vignettingBlink,_vignettingFade;
    public Texture2D _vignettingMask;public BlinkStyle _vignettingBlinkStyle;
}
sealed class Display:MonoBehaviour {public Material Material{get;set;}=new Material();public float Brightness=-40;}
