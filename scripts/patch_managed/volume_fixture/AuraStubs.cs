using System;
using System.Collections.Generic;
using UnityEngine;

namespace Aura2API
{
    public enum Texture3DFiltering { Linear, Cubic }
    public sealed class AuraQualitySettings
    {
        public Vector3Int frustumGridResolution=new Vector3Int(64,40,64);
        public Texture3DFiltering texture3DFiltering=Texture3DFiltering.Linear;
        public float farClipPlaneDistance=123.5f,temporalReprojectionFactor=.81f;
        public bool enableTemporalReprojection=true,enableVolumes=true,enableOcclusionCulling=true;
    }
    public sealed class FrustumSettings
    {
        public AuraQualitySettings qualitySettings=new AuraQualitySettings();
        public AuraQualitySettings QualitySettings { get{throw new Exception("QualitySettings getter may allocate and must not be used");} }
    }
    public static class Lifecycle
    {
        public static readonly List<string> events=new List<string>();
        public static int directionalShadowCount;
    }
    public class AuraLight:MonoBehaviour
    {
        Light lightComponent;
        public Light LightComponent { get{if(lightComponent==null)lightComponent=GetComponent<Light>();return lightComponent;} }
        public bool useShadow=true,initialized,shadowBuffer,shadowMaterial;
        bool previousShadow;
        LightType previousType;
        public int enableAttempts;
        public bool rejectEnable;
        public bool CastsShadows { get{return LightComponent!=null&&QualitySettings.shadows!=ShadowQuality.Disable&&LightComponent.shadows!=LightShadows.None&&useShadow;} }
        public void InitializeForTest()
        {
            if(initialized)return;
            previousShadow=CastsShadows;previousType=LightComponent==null?LightType.Point:LightComponent.type;
            shadowBuffer=shadowMaterial=previousShadow;
            if(previousType==LightType.Directional&&CastsShadows)++Lifecycle.directionalShadowCount;
            initialized=true;
        }
        void Uninitialize()
        {
            if(!initialized)return;
            // Exact Aura manager behavior: unregister reads the current effective shadow state.
            if(previousType==LightType.Directional&&CastsShadows)--Lifecycle.directionalShadowCount;
            initialized=shadowBuffer=shadowMaterial=false;
        }
        public void SimulateUpdate()
        {
            if(enabled&&initialized&&previousShadow!=CastsShadows){Uninitialize();InitializeForTest();}
        }
        public void SimulatePreCull()
        {
            if(enabled&&CastsShadows&&LightComponent.type==LightType.Point&&!shadowBuffer)throw new NullReferenceException("Aura point shadow command buffer is missing");
        }
        public void SimulatePreRender()
        {
            if(enabled&&CastsShadows&&LightComponent.type==LightType.Point&&!shadowMaterial)throw new NullReferenceException("Aura point shadow material is missing");
        }
        public override bool enabled
        {
            get{return base.enabled;}
            set
            {
                if(value==base.enabled)return;
                Lifecycle.events.Add(name+":"+value);
                if(value)++enableAttempts;
                if(!value)Uninitialize();
                base.enabled=value&&!rejectEnable;
                if(base.enabled&&gameObject.activeInHierarchy)InitializeForTest();
            }
        }
    }
    public class AuraCamera:MonoBehaviour
    {
        Camera cameraComponent;
        public Camera CameraComponent { get{if(cameraComponent==null)cameraComponent=GetComponent<Camera>();return cameraComponent;} }
        public FrustumSettings frustumSettings=new FrustumSettings();
        public int frames,frameReads,initializedReads,enableAttempts;
        public bool initialized=true,rejectEnable,failInitialization;
        public int FrameId { get{++frameReads;return frames;} }
        public bool IsInitialized { get{++initializedReads;return initialized;} }
        // Unity renders only active/enabled effects; actual Aura increments FrameId after ComputeData.
        public void SimulateRender() { if(isActiveAndEnabled&&initialized)++frames; }
        public override bool enabled
        {
            get{return base.enabled;}
            set
            {
                if(value==base.enabled)return;
                Lifecycle.events.Add(name+":"+value);
                if(value)++enableAttempts;
                base.enabled=value&&!rejectEnable;
                initialized=base.enabled&&!failInitialization;
                if(initialized&&CameraComponent!=null)CameraComponent.depthTextureMode|=DepthTextureMode.Depth;
            }
        }
    }
}
