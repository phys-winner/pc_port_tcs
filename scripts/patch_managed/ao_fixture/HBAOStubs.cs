using UnityEngine;

namespace HorizonBasedAmbientOcclusion
{
    public sealed class HBAO:MonoBehaviour
    {
        public enum PipelineStage { BeforeImageEffectsOpaque, AfterLighting, BeforeReflections }
        public enum Quality { Lowest, Low, Medium, High, Highest }
        public enum Resolution { Full, Half }
        public enum Deinterleaving { Disabled, x4 }
        public enum PerPixelNormals { GBuffer, Camera, Reconstruct }
        public enum BlurType { None, Narrow, Medium, Wide, ExtraWide }
        public PipelineStage stage=PipelineStage.AfterLighting;
        public Quality quality=Quality.Highest;
        public Resolution resolution=Resolution.Full;
        public Deinterleaving deinterleaving=Deinterleaving.x4;
        public PerPixelNormals normals=PerPixelNormals.Camera;
        public BlurType blur=BlurType.ExtraWide;
        public bool temporal=true,colorBleeding=true,rejectEnable;
        public int writes,enableAttempts;
        public float radius=1.37f,intensity=2.42f,maxDistance=71.3f,distanceFalloff=12.7f,maxRadiusPixels=159.2f,bias=.23f,blurSharpness=4.1f;
        public Color color=new Color(.1f,.2f,.3f,.4f);
        public override bool enabled
        {
            get{return base.enabled;}
            set { if(value && !base.enabled)++enableAttempts; base.enabled=value&&!rejectEnable; }
        }
        public void SetPipelineStage(PipelineStage value){stage=value;++writes;}
        public void SetQuality(Quality value){quality=value;++writes;}
        public void SetResolution(Resolution value){resolution=value;++writes;}
        public void SetDeinterleaving(Deinterleaving value){deinterleaving=value;++writes;}
        public void SetAoPerPixelNormals(PerPixelNormals value){normals=value;++writes;}
        public void EnableTemporalFilter(bool value=true){temporal=value;++writes;}
        public void EnableColorBleeding(bool value=true){colorBleeding=value;++writes;}
        public void SetBlurType(BlurType value){blur=value;++writes;}
    }
}
