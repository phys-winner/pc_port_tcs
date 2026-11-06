using System.Collections.Generic;
using UnityEngine;
using TMPro;

namespace ClosingShift.Android
{
    public sealed class AndroidRecipeText : MonoBehaviour
    {
        static AndroidRecipeText instance;
        readonly List<MeshRenderer> renderers=new List<MeshRenderer>();
        readonly Dictionary<GameObject,int> layers=new Dictionary<GameObject,int>();
        readonly Dictionary<MeshRenderer,Material> depthMaterials=new Dictionary<MeshRenderer,Material>();
        readonly Plane[] planes=new Plane[6];
        Camera world, overlay, depthCamera;
        Shader depthShader;
        RenderTexture depthTarget;
        Transform cold;
        Vector3 coldPosition;
        int mask;
        float retryAt;
        internal static bool Active { get; private set; }

        internal static void Prepare()
        {
            GameObject root=GameObject.Find("-------Job Systems--------/Coffee Serving Systems/Recipes List");
            if(root!=null && root.GetComponent<AndroidRecipeText>()==null)root.AddComponent<AndroidRecipeText>();
        }
        void OnEnable()
        {
            instance=this;Active=true;
            cold=transform.Find("Cold label");
            if(cold!=null)coldPosition=cold.localPosition;
            Camera.onPreCull+=Record;
        }
        void Record(Camera current)
        {
            if(current!=Camera.main || current.gameObject.scene.buildIndex!=1)return;
            if(cold!=null)cold.localPosition=coldPosition+(ClosingShift.Localization.RussianUiPresentation.RussianActive?
                cold.localRotation*new Vector3(.28f,0,0):Vector3.zero);
            if(world!=current)
            {
                Detach();world=current;mask=world.cullingMask;
                var owner=new GameObject("Unfiltered recipe text",typeof(Camera));
                owner.transform.SetParent(transform,false);overlay=owner.GetComponent<Camera>();
                owner=new GameObject("Recipe occlusion depth",typeof(Camera));
                owner.transform.SetParent(transform,false);depthCamera=owner.GetComponent<Camera>();
                depthShader=Shader.Find("Hidden/Internal-DepthNormalsTexture");
                Debug.Log("PORT_RECIPE_TEXT cameraAfterEffects=true occlusionDepth=true russianHeadingShift=0.28");
            }
            bool visible=false;
            GeometryUtility.CalculateFrustumPlanes(world,planes);
            transform.GetComponentsInChildren<MeshRenderer>(true,renderers);
            foreach(MeshRenderer renderer in renderers)
            {
                if(renderer==null)continue;
                TMP_Text text=renderer.GetComponent<TMP_Text>();
                TMP_SubMesh submesh=renderer.GetComponent<TMP_SubMesh>();
                if(text==null && submesh==null)continue;
                Material material;
                if(!depthMaterials.TryGetValue(renderer,out material) || renderer.sharedMaterial!=material)
                {
                    if(text!=null)text.isOverlay=false;
                    // TMP owns these instances; shared font materials also serve screen-space UI.
                    material=text!=null?text.fontMaterial:submesh.material;
                    if(material!=null){renderer.sharedMaterial=material;depthMaterials[renderer]=material;}
                }
                if(material!=null)material.SetFloat("unity_GUIZTestMode",4);
                if(!layers.ContainsKey(renderer.gameObject))layers.Add(renderer.gameObject,renderer.gameObject.layer);
                if(renderer.enabled && renderer.gameObject.activeInHierarchy && GeometryUtility.TestPlanesAABB(planes,renderer.bounds) &&
                    !AndroidRoomCulling.Occluded(world,renderer.bounds))visible=true;
            }
            overlay.enabled=depthCamera.enabled=false;
            if(!visible || world.targetTexture==null || Time.unscaledTime<retryAt){RestoreLayers();return;}
            if(depthTarget==null || depthTarget.width!=world.targetTexture.width || depthTarget.height!=world.targetTexture.height)
            {
                ReleaseBuffers();
                depthTarget=new RenderTexture(world.targetTexture.width,world.targetTexture.height,24,RenderTextureFormat.Depth);
                depthTarget.name="Recipe occlusion depth";depthTarget.antiAliasing=1;
                if(!depthTarget.Create()){ReleaseBuffers();retryAt=Time.unscaledTime+1;RestoreLayers();return;}
            }
            world.cullingMask&=~(1<<31);
            foreach(var entry in layers)if(entry.Key!=null)entry.Key.layer=31;
            depthCamera.CopyFrom(world);
            if(depthShader!=null && depthShader.isSupported)depthCamera.SetReplacementShader(depthShader,"RenderType");
            depthCamera.transform.SetPositionAndRotation(world.transform.position,world.transform.rotation);
            depthCamera.renderingPath=RenderingPath.Forward;depthCamera.depth=world.depth+.002f;
            depthCamera.clearFlags=CameraClearFlags.SolidColor;depthCamera.depthTextureMode=DepthTextureMode.None;
            depthCamera.allowHDR=depthCamera.allowMSAA=depthCamera.forceIntoRenderTexture=false;
            depthCamera.targetTexture=depthTarget;depthCamera.enabled=true;
            overlay.CopyFrom(world);
            overlay.transform.SetPositionAndRotation(world.transform.position,world.transform.rotation);
            overlay.renderingPath=RenderingPath.Forward;overlay.depth=world.depth+.005f;
            overlay.cullingMask=1<<31;overlay.clearFlags=CameraClearFlags.Nothing;
            overlay.depthTextureMode=DepthTextureMode.None;overlay.forceIntoRenderTexture=false;
            // Image effects discard world depth; replay depth only before drawing unfiltered glyphs.
            overlay.SetTargetBuffers(world.targetTexture.colorBuffer,depthTarget.depthBuffer);overlay.enabled=true;
        }
        void RestoreLayers()
        {
            if(world!=null)world.cullingMask=mask;
            foreach(var entry in layers)if(entry.Key!=null)entry.Key.layer=entry.Value;
        }
        void ReleaseBuffers()
        {
            if(overlay!=null){overlay.enabled=false;overlay.targetTexture=null;}
            if(depthCamera!=null){depthCamera.enabled=false;depthCamera.targetTexture=null;}
            if(depthTarget!=null){depthTarget.Release();Object.Destroy(depthTarget);depthTarget=null;}
        }
        internal static void ReleaseTarget(Camera current)
        {
            if(instance!=null && instance.world==current)instance.ReleaseBuffers();
        }
        void Detach()
        {
            RestoreLayers();ReleaseBuffers();
            if(overlay!=null){Object.Destroy(overlay.gameObject);overlay=null;}
            if(depthCamera!=null){Object.Destroy(depthCamera.gameObject);depthCamera=null;}
            world=null;
        }
        void OnDisable()
        {
            Active=false;Camera.onPreCull-=Record;Detach();if(instance==this)instance=null;
            layers.Clear();depthMaterials.Clear();
            if(cold!=null)cold.localPosition=coldPosition;
        }
    }
}
