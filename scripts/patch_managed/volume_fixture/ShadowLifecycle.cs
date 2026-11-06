using System;
using Aura2API;
using ClosingShift.Android;
using UnityEngine;

static class ShadowLifecycle
{
    static AuraLight Make(LightType type,LightShadows shadows,string name)
    {
        var light=new Light{type=type,shadows=shadows};
        var aura=new AuraLight{light=light,gameObject=light.gameObject,name=name};light.aura=aura;
        aura.InitializeForTest();return aura;
    }
    internal static void Run(Action<bool,string> check)
    {
        QualitySettings.shadows=ShadowQuality.All;Lifecycle.directionalShadowCount=0;
        var broken=Make(LightType.Point,LightShadows.None,"OldPoint");broken.SimulateUpdate();
        broken.light.shadows=LightShadows.Soft;
        bool cullFailed=false,packFailed=false;
        try{broken.SimulatePreCull();}catch(NullReferenceException){cullFailed=true;}
        try{broken.SimulatePreRender();}catch(NullReferenceException){packFailed=true;}
        check(cullFailed&&packFailed,"Negative control reproduces both first-frame point resource failures after a late unbracketed write");
        broken.SimulateUpdate();broken.SimulatePreCull();broken.SimulatePreRender();
        check(broken.shadowBuffer&&broken.shadowMaterial,"The old lifecycle recovers only on the next Update, matching the device trace");
        broken.enabled=false;
        var brokenSun=Make(LightType.Directional,LightShadows.None,"OldSun");
        brokenSun.light.shadows=LightShadows.Soft;brokenSun.enabled=false;brokenSun.enabled=true;
        check(Lifecycle.directionalShadowCount==0&&brokenSun.CastsShadows,"Negative control proves an after-write restart miscounts a newly shadowed directional light");
        brokenSun.enabled=false;Lifecycle.directionalShadowCount=0;

        var main=new Camera{name="ShadowMain"};var volume=new AuraCamera{camera=main,gameObject=main.gameObject};
        var point=Make(LightType.Point,LightShadows.None,"Point");var sun=Make(LightType.Directional,LightShadows.None,"Sun");
        AndroidVolumetrics.Observe(volume,"Aura2API.AuraCamera");
        AndroidVolumetrics.Observe(point,"Aura2API.AuraLight");AndroidVolumetrics.Observe(sun,"Aura2API.AuraLight");
        AndroidVolumetrics.Enforce(main,true,2);point.SimulateUpdate();
        int attempts=point.enableAttempts,reads=point.light.componentReads;
        AndroidVolumetrics.SetLightShadows(point.light,LightShadows.Soft);
        point.SimulatePreCull();point.SimulatePreRender();
        check(point.shadowBuffer&&point.shadowMaterial&&point.enableAttempts==attempts+1,"Bracketed late point activation creates both resources before any next Update");
        attempts=point.enableAttempts;
        for(int i=0;i<5;++i){AndroidVolumetrics.SetLightShadows(point.light,LightShadows.Soft);AndroidVolumetrics.Enforce(main,true,2);}
        check(point.enableAttempts==attempts&&point.light.componentReads==reads,"Stable modes neither restart Aura nor repeat cached light lookups");
        AndroidVolumetrics.SetLightShadows(point.light,LightShadows.None);
        check(point.enabled&&!point.shadowBuffer&&!point.shadowMaterial,"Shadow removal releases resources while retaining the volume light itself");
        AndroidVolumetrics.SetLightShadows(sun.light,LightShadows.Soft);
        check(Lifecycle.directionalShadowCount==1&&sun.shadowBuffer,"Directional activation unregisters under the old mode and registers exactly one shadow caster");
        AndroidVolumetrics.SetLightShadows(sun.light,LightShadows.Hard);
        check(Lifecycle.directionalShadowCount==1,"A shadow-mode change preserves the directional caster count");
        AndroidVolumetrics.SetLightShadows(sun.light,LightShadows.None);
        check(Lifecycle.directionalShadowCount==0&&!sun.shadowBuffer,"Directional removal decrements before changing the effective shadow mode");

        AndroidVolumetrics.SetLightShadows(point.light,LightShadows.Soft);AndroidVolumetrics.SetLightShadows(sun.light,LightShadows.Soft);
        AndroidVolumetrics.BeforeQualityChange();
        check(!point.enabled&&!sun.enabled&&Lifecycle.directionalShadowCount==0,"Quality prefix suspends collectors under the old global shadow state");
        QualitySettings.shadows=ShadowQuality.Disable;AndroidVolumetrics.Enforce(main,true,2);
        check(point.enabled&&sun.enabled&&!point.shadowBuffer&&!sun.shadowBuffer&&Lifecycle.directionalShadowCount==0,"Global off resumes volume lights without shadow resources or stale directional counts");
        AndroidVolumetrics.BeforeQualityChange();QualitySettings.shadows=ShadowQuality.All;
        point.SimulatePreCull();point.SimulatePreRender();
        check(!point.enabled&&!sun.enabled,"Original quality setter runs with callbacks suspended until the subsequent enforcement");
        AndroidVolumetrics.Enforce(main,true,2);point.SimulatePreCull();point.SimulatePreRender();
        check(point.shadowBuffer&&point.shadowMaterial&&sun.shadowBuffer&&Lifecycle.directionalShadowCount==1,"Global off-to-on allocates point resources and one directional caster before rendering");

        var disabled=Make(LightType.Point,LightShadows.None,"Disabled");disabled.enabled=false;
        AndroidVolumetrics.Observe(disabled,"Aura2API.AuraLight");attempts=disabled.enableAttempts;
        AndroidVolumetrics.SetLightShadows(disabled.light,LightShadows.Soft);AndroidVolumetrics.BeforeQualityChange();AndroidVolumetrics.Enforce(main,true,2);
        check(!disabled.enabled&&disabled.enableAttempts==attempts,"Local and global changes do not enable an authored-disabled collector");
        var inactive=Make(LightType.Point,LightShadows.None,"Inactive");inactive.enabled=false;inactive.gameObject.activeInHierarchy=false;inactive.enabled=true;
        AndroidVolumetrics.Observe(inactive,"Aura2API.AuraLight");attempts=inactive.enableAttempts;
        AndroidVolumetrics.SetLightShadows(inactive.light,LightShadows.Soft);
        check(inactive.enabled&&!inactive.gameObject.activeInHierarchy&&!inactive.shadowBuffer&&inactive.enableAttempts==attempts,"A bracketed write does not activate or cycle an inactive object");
        AndroidVolumetrics.Enforce(main,true,2);
        check(!inactive.enabled&&!inactive.gameObject.activeInHierarchy,"Ordinary policy continues to keep inactive collectors disabled");

        var uncached=Make(LightType.Directional,LightShadows.None,"SceneRestore");reads=uncached.light.componentReads;
        AndroidVolumetrics.SetLightShadows(uncached.light,LightShadows.Soft);
        check(uncached.shadowBuffer&&uncached.light.componentReads==reads+1&&Lifecycle.directionalShadowCount==2,"Pre-observation scene restoration resolves the local Aura component and brackets its old mode");
        AndroidVolumetrics.SetLightShadows(uncached.light,LightShadows.None);
        check(uncached.light.componentReads==reads+1&&Lifecycle.directionalShadowCount==1,"Scene-restore lookup is cached and its directional removal remains balanced");
        var plain=new Light();AndroidVolumetrics.SetLightShadows(plain,LightShadows.Soft);AndroidVolumetrics.SetLightShadows(plain,LightShadows.None);
        check(plain.componentReads==1&&plain.shadows==LightShadows.None,"Lights without Aura retain normal mode changes and one bounded negative lookup");

        AndroidVolumetrics.BeginScene();point.enabled=false;sun.enabled=false;uncached.enabled=false;inactive.enabled=false;volume.enabled=false;
        check(Lifecycle.directionalShadowCount==0,"Lifecycle test leaves no directional registration imbalance");
    }
}
