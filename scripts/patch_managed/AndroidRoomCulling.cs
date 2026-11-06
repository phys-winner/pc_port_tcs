using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClosingShift.Android
{
    internal sealed class AndroidRoomCulling : MonoBehaviour
    {
        static AndroidRoomCulling instance;
        internal sealed class Source
        {
            internal readonly string Path, Mesh;
            internal readonly int VertexCount;
            internal readonly float[] Matrix;
            internal readonly string[] Materials;
            internal readonly Vector3[] Vertices, Normals;
            internal Source(string path,string mesh,int count,float[] matrix,string[] materials,Vector3[] vertices)
            {
                Path=path;Mesh=mesh;VertexCount=count;Matrix=matrix;Materials=materials;Vertices=vertices;
                Normals=new Vector3[vertices.Length/3];
                for(int i=0;i<Normals.Length;i++)Normals[i]=Vector3.Cross(vertices[i*3+1]-vertices[i*3],vertices[i*3+2]-vertices[i*3]).normalized;
            }
        }
        sealed class Wall
        {
            internal Source Source;
            internal MeshRenderer Renderer;
        }
        internal struct Cone
        {
            internal Vector3 A, Normal, Side0, Side1, Side2;
            internal static Vector3 Edge(Vector3 a,Vector3 b,Vector3 inside,Vector3 eye)
            {
                Vector3 normal=Vector3.Cross(a-eye,b-eye).normalized;
                return Vector3.Dot(normal,inside-eye)<0?-normal:normal;
            }
            internal static bool Create(Vector3 a,Vector3 b,Vector3 c,Vector3 normal,Vector3 eye,Vector3 forward,float near,out Cone cone)
            {
                cone=new Cone();
                if(Vector3.Dot(normal,eye-a)<=.02f || Vector3.Dot(forward,a-eye)<=near+.02f ||
                    Vector3.Dot(forward,b-eye)<=near+.02f || Vector3.Dot(forward,c-eye)<=near+.02f)return false;
                cone.A=a;cone.Normal=normal;cone.Side0=Edge(a,b,c,eye);cone.Side1=Edge(b,c,a,eye);cone.Side2=Edge(c,a,b,eye);
                return cone.Side0.sqrMagnitude>.99f && cone.Side1.sqrMagnitude>.99f && cone.Side2.sqrMagnitude>.99f;
            }
            static float Support(Vector3 normal,Vector3 extent)
            {return Mathf.Abs(normal.x)*extent.x+Mathf.Abs(normal.y)*extent.y+Mathf.Abs(normal.z)*extent.z;}
            internal bool Contains(Bounds bounds,Vector3 eye)
            {
                Vector3 point=bounds.center-eye,extent=bounds.extents;
                // The support radius proves all eight AABB corners lie strictly behind this opaque triangle.
                return Vector3.Dot(Normal,A-bounds.center)>Support(Normal,extent)+.02f &&
                    Vector3.Dot(Side0,point)>Support(Side0,extent)+.02f &&
                    Vector3.Dot(Side1,point)>Support(Side1,extent)+.02f &&
                    Vector3.Dot(Side2,point)>Support(Side2,extent)+.02f;
            }
        }
        struct Changed
        {
            internal MeshRenderer Renderer;
            internal ShadowCastingMode Shadows;
            internal bool Off;
        }
        readonly List<Wall> walls=new List<Wall>();
        readonly List<MeshRenderer> candidates=new List<MeshRenderer>();
        // Static-batched candidates cannot move; their bounds are cached when first valid.
        readonly List<Bounds> candidateBounds=new List<Bounds>();
        // Material/shader names allocate a new string on every read; identities never rename.
        readonly Dictionary<Material,string> materialNames=new Dictionary<Material,string>();
        readonly Dictionary<Shader,string> shaderNames=new Dictionary<Shader,string>();
        readonly List<Material> materials=new List<Material>();
        readonly List<Cone> cones=new List<Cone>();
        readonly List<Changed> changed=new List<Changed>();
        readonly Plane[] planes=new Plane[6];
        Camera camera;
        int frame=-1;
        bool diagnosticLogged;
        internal static int HiddenCount, CandidateCount, ActiveCones, HiddenLights, HiddenProbes;
        // A light or reflection probe whose whole influence box lies behind the same opaque wall
        // triangle as seen from the eye cannot change any visible pixel: skip its deferred volume
        // pass for this camera render only. Directional lights are never culled.
        internal static bool CullLightsAndProbes = true;
        readonly List<Light> lights=new List<Light>();
        readonly List<ReflectionProbe> probes=new List<ReflectionProbe>();
        readonly List<Light> hiddenLights=new List<Light>();
        readonly List<ReflectionProbe> hiddenProbes=new List<ReflectionProbe>();

        internal static void Prepare()
        {
            GameObject root=GameObject.Find("Store props");
            if(root!=null && root.scene.buildIndex==1 && root.GetComponent<AndroidRoomCulling>()==null)root.AddComponent<AndroidRoomCulling>();
        }
        void OnEnable()
        {
            instance=this;
            walls.Clear();candidates.Clear();candidateBounds.Clear();materialNames.Clear();shaderNames.Clear();
            MeshRenderer[] renderers=GetComponentsInChildren<MeshRenderer>(true);
            foreach(Source source in AndroidRoomOccluders.Sources)
                foreach(MeshRenderer renderer in renderers)
                {
                    if(!renderer.isPartOfStaticBatch || Path(renderer.transform)!=source.Path)continue;
                    MeshFilter filter=renderer.GetComponent<MeshFilter>();
                    if(filter==null || filter.sharedMesh==null || filter.sharedMesh.name!=source.Mesh || filter.sharedMesh.vertexCount!=source.VertexCount)continue;
                    walls.Add(new Wall{Source=source,Renderer=renderer});break;
                }
            foreach(string branch in new[]{"coffee_items","counter_props","Pastries","Props"})
            {
                Transform root=transform.Find(branch);
                if(root==null)continue;
                foreach(MeshRenderer renderer in root.GetComponentsInChildren<MeshRenderer>(true))
                    if(renderer.isPartOfStaticBatch && !candidates.Contains(renderer)){candidates.Add(renderer);candidateBounds.Add(renderer.bounds);}
            }
            CandidateCount=candidates.Count;
            lights.Clear();probes.Clear();
            foreach(Light light in Resources.FindObjectsOfTypeAll<Light>())
                if(light!=null && light.gameObject.scene==gameObject.scene)lights.Add(light);
            foreach(ReflectionProbe probe in Resources.FindObjectsOfTypeAll<ReflectionProbe>())
                if(probe!=null && probe.gameObject.scene==gameObject.scene)probes.Add(probe);
            Camera.onPreCull+=Cull;Camera.onPostRender+=After;
            Debug.Log("PORT_ROOM_CULL walls="+walls.Count+" candidates="+CandidateCount+" lights="+lights.Count+" probes="+probes.Count+" opaqueTriangleProof=true shadowsPreserved=true");
        }
        static string Path(Transform value)
        {
            string result=value.name;
            while(value.parent!=null){value=value.parent;result=value.name+"/"+result;}
            return result;
        }
        internal static bool OpaqueMaterial(Material material)
        {
            return material!=null && material.shader!=null && OpaqueMaterial(material,material.shader.name);
        }
        static bool OpaqueMaterial(Material material,string shaderName)
        {
            if(material.renderQueue>2450 ||
                material.IsKeywordEnabled("_ALPHATEST_ON") || material.IsKeywordEnabled("_ALPHABLEND_ON") || material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON"))return false;
            // UVFree's opaque blend/depth states are fixed in its shader; it declares none of these uniforms.
            if(shaderName=="UVFree/StandardMetallic/Single-Texture")return material.shader.GetPropertyCount()==25;
            return shaderName=="Standard" && material.HasProperty("_Mode") && material.HasProperty("_ZWrite") &&
                material.HasProperty("_SrcBlend") && material.HasProperty("_DstBlend") && material.GetFloat("_Mode")==0 &&
                material.GetFloat("_ZWrite")==1 && material.GetFloat("_SrcBlend")==1 && material.GetFloat("_DstBlend")==0;
        }
        bool Valid(Wall wall)
        {
            MeshRenderer renderer=wall.Renderer;
            if(renderer==null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                renderer.shadowCastingMode==ShadowCastingMode.ShadowsOnly)return false;
            Matrix4x4 matrix=renderer.transform.localToWorldMatrix;
            for(int i=0;i<16;i++)if(Mathf.Abs(matrix[i/4,i%4]-wall.Source.Matrix[i])>.001f)return false;
            renderer.GetSharedMaterials(materials);
            if(materials.Count!=wall.Source.Materials.Length)return false;
            for(int i=0;i<materials.Count;i++)
            {
                Material material=materials[i];
                if(material==null)return false;
                Shader shader=material.shader;
                if(shader==null)return false;
                string materialName,shaderName;
                if(!materialNames.TryGetValue(material,out materialName))materialNames[material]=materialName=material.name;
                if(!shaderNames.TryGetValue(shader,out shaderName))shaderNames[shader]=shaderName=shader.name;
                if(materialName!=wall.Source.Materials[i] || !OpaqueMaterial(material,shaderName))return false;
            }
            return true;
        }
        static string Property(Material material,string name)
        {
            return material.HasProperty(name)?material.GetFloat(name).ToString("G9",System.Globalization.CultureInfo.InvariantCulture):"missing";
        }
        static string MaterialDiagnostic(Material material)
        {
            if(material==null)return "material=null";
            return "material="+material.name+" shader="+(material.shader==null?"null":material.shader.name)+
                " properties="+(material.shader==null?-1:material.shader.GetPropertyCount())+" queue="+material.renderQueue+
                " hasMode="+material.HasProperty("_Mode")+" mode="+Property(material,"_Mode")+" zWrite="+Property(material,"_ZWrite")+
                " src="+Property(material,"_SrcBlend")+" dst="+Property(material,"_DstBlend")+
                " alphaTest="+material.IsKeywordEnabled("_ALPHATEST_ON")+" alphaBlend="+material.IsKeywordEnabled("_ALPHABLEND_ON")+
                " alphaPremultiply="+material.IsKeywordEnabled("_ALPHAPREMULTIPLY_ON");
        }
        void Diagnose(Camera current)
        {
            diagnosticLogged=true;
            int validCount=0;
            foreach(Wall wall in walls)
            {
                MeshRenderer renderer=wall.Renderer;
                if(renderer==null){Debug.Log("PORT_ROOM_CULL_DIAG wall="+wall.Source.Path+" reason=destroyed");continue;}
                bool valid=Valid(wall);
                if(valid)validCount++;
                float delta=0;
                Matrix4x4 matrix=renderer.transform.localToWorldMatrix;
                for(int i=0;i<16;i++)delta=Mathf.Max(delta,Mathf.Abs(matrix[i/4,i%4]-wall.Source.Matrix[i]));
                renderer.GetSharedMaterials(materials);
                int bad=-1,preview=-1;
                for(int i=0;i<materials.Count;i++)
                {
                    Material material=materials[i];
                    if(preview<0 && material!=null && material.shader!=null && material.shader.name=="UVFree/StandardMetallic/Single-Texture")preview=i;
                    if(bad<0 && (i>=wall.Source.Materials.Length || material==null || material.name!=wall.Source.Materials[i] || !OpaqueMaterial(material)))bad=i;
                }
                if(bad>=0)preview=bad;
                if(preview<0 && materials.Count>0)preview=0;
                string reason=!renderer.enabled?"disabled":renderer.forceRenderingOff?"forcedOff":!renderer.gameObject.activeInHierarchy?"inactive":
                    renderer.shadowCastingMode==ShadowCastingMode.ShadowsOnly?"shadowsOnly":delta>.001f?"matrix":
                    materials.Count!=wall.Source.Materials.Length?"materialCount":bad>=0?"material":"ok";
                Debug.Log("PORT_ROOM_CULL_DIAG wall="+wall.Source.Path+" valid="+valid+" reason="+reason+
                    " matrixDelta="+delta.ToString("G9",System.Globalization.CultureInfo.InvariantCulture)+
                    " active="+renderer.gameObject.activeInHierarchy+" enabled="+renderer.enabled+" forcedOff="+renderer.forceRenderingOff+
                    " shadows="+renderer.shadowCastingMode+" materials="+materials.Count+"/"+wall.Source.Materials.Length+
                    " badMaterial="+bad+" preview="+preview+" expected="+(preview>=0 && preview<wall.Source.Materials.Length?wall.Source.Materials[preview]:"none")+
                    " "+(preview>=0?MaterialDiagnostic(materials[preview]):"material=none"));
            }
            Debug.Log("PORT_ROOM_CULL_DIAG validWalls="+validCount+"/"+walls.Count+" candidates="+candidates.Count+
                " eye="+current.transform.position+" forward="+current.transform.forward+" fov="+current.fieldOfView+" near="+current.nearClipPlane+" far="+current.farClipPlane);
            foreach(MeshRenderer renderer in candidates)
            {
                if(renderer==null || Path(renderer.transform)!="Store props/Props/sink02")continue;
                Bounds bounds=renderer.bounds;
                MeshFilter filter=renderer.GetComponent<MeshFilter>();
                Debug.Log("PORT_ROOM_CULL_DIAG sink02 center="+bounds.center+" extents="+bounds.extents+
                    " batch="+renderer.isPartOfStaticBatch+" enabled="+renderer.enabled+" forcedOff="+renderer.forceRenderingOff+
                    " shadows="+renderer.shadowCastingMode+" meshBounds="+(filter==null || filter.sharedMesh==null?"none":filter.sharedMesh.bounds.ToString()));
                break;
            }
        }
        void Cull(Camera current)
        {
            Restore();
            if(current!=Camera.main || current.gameObject.scene.buildIndex!=1 || current.orthographic)return;
            camera=current;frame=Time.frameCount;cones.Clear();HiddenCount=0;
            if(!diagnosticLogged)Diagnose(current);
            Vector3 eye=current.transform.position,forward=current.transform.forward;
            GeometryUtility.CalculateFrustumPlanes(current,planes);
            foreach(Wall wall in walls)
            {
                // Test visibility first: walls outside the frustum never need material validation.
                if(wall.Renderer==null || !GeometryUtility.TestPlanesAABB(planes,wall.Renderer.bounds) || !Valid(wall))continue;
                Source source=wall.Source;
                for(int i=0;i<source.Normals.Length;i++)
                {
                    Cone cone;
                    if(Cone.Create(source.Vertices[i*3],source.Vertices[i*3+1],source.Vertices[i*3+2],source.Normals[i],eye,forward,current.nearClipPlane,out cone))cones.Add(cone);
                }
            }
            ActiveCones=cones.Count;
            if(cones.Count==0)return;
            for(int index=0;index<candidates.Count;index++)
            {
                MeshRenderer renderer=candidates[index];
                Bounds bounds=candidateBounds[index];
                // An object inactive during the scan may report empty bounds; refresh it once it is valid.
                if(bounds.extents.sqrMagnitude==0)
                {
                    if(renderer==null)continue;
                    candidateBounds[index]=bounds=renderer.bounds;
                }
                if(!GeometryUtility.TestPlanesAABB(planes,bounds))continue;
                if(renderer==null || !renderer.enabled || renderer.forceRenderingOff || !renderer.gameObject.activeInHierarchy ||
                    renderer.shadowCastingMode==ShadowCastingMode.ShadowsOnly || renderer.shadowCastingMode==ShadowCastingMode.TwoSided)continue;
                foreach(Cone cone in cones)
                {
                    if(!cone.Contains(bounds,eye))continue;
                    Changed saved=new Changed{Renderer=renderer,Shadows=renderer.shadowCastingMode,Off=renderer.forceRenderingOff};
                    changed.Add(saved);
                    if(saved.Shadows==ShadowCastingMode.On)renderer.shadowCastingMode=ShadowCastingMode.ShadowsOnly;
                    else renderer.forceRenderingOff=true;
                    HiddenCount++;break;
                }
            }
            CullVolumes(eye);
        }
        // Conservative world box of a light's influence: a point light's sphere, or a spot light's
        // cone (apex plus the base disk at range, which contains every lit point of the cone).
        internal static Bounds LightBounds(Light light)
        {
            Transform transform=light.transform;
            Vector3 position=transform.position;
            float range=light.range;
            if(light.type==LightType.Spot && light.spotAngle<170)
            {
                Vector3 direction=transform.forward;
                float radius=range*Mathf.Tan(light.spotAngle*.5f*Mathf.Deg2Rad);
                Vector3 extents=new Vector3(radius*Mathf.Sqrt(Mathf.Max(0,1-direction.x*direction.x)),
                    radius*Mathf.Sqrt(Mathf.Max(0,1-direction.y*direction.y)),radius*Mathf.Sqrt(Mathf.Max(0,1-direction.z*direction.z)));
                Bounds cone=new Bounds(position+direction*range,extents*2);
                cone.Encapsulate(position);
                return cone;
            }
            return new Bounds(position,new Vector3(range*2,range*2,range*2));
        }
        void CullVolumes(Vector3 eye)
        {
            if(!CullLightsAndProbes)return;
            foreach(Light light in lights)
            {
                if(light==null || !light.enabled || (light.type!=LightType.Point && light.type!=LightType.Spot) || !light.gameObject.activeInHierarchy)continue;
                Bounds bounds=LightBounds(light);
                if(!GeometryUtility.TestPlanesAABB(planes,bounds))continue;
                foreach(Cone cone in cones)
                    if(cone.Contains(bounds,eye)){light.enabled=false;hiddenLights.Add(light);break;}
            }
            foreach(ReflectionProbe probe in probes)
            {
                if(probe==null || !probe.enabled || !probe.gameObject.activeInHierarchy)continue;
                Bounds bounds=probe.bounds;
                bounds.Expand(probe.blendDistance*2);
                if(!GeometryUtility.TestPlanesAABB(planes,bounds))continue;
                foreach(Cone cone in cones)
                    if(cone.Contains(bounds,eye)){probe.enabled=false;hiddenProbes.Add(probe);break;}
            }
            HiddenLights=hiddenLights.Count;HiddenProbes=hiddenProbes.Count;
        }
        internal static bool Occluded(Camera current,Bounds bounds)
        {
            if(instance==null || instance.camera!=current || instance.frame!=Time.frameCount)return false;
            Vector3 eye=current.transform.position;
            foreach(Cone cone in instance.cones)if(cone.Contains(bounds,eye))return true;
            return false;
        }
        void After(Camera current){if(current==camera)Restore();}
        void Restore()
        {
            foreach(Changed saved in changed)if(saved.Renderer!=null)
            {saved.Renderer.shadowCastingMode=saved.Shadows;saved.Renderer.forceRenderingOff=saved.Off;}
            changed.Clear();camera=null;frame=-1;
            foreach(Light light in hiddenLights)if(light!=null)light.enabled=true;
            foreach(ReflectionProbe probe in hiddenProbes)if(probe!=null)probe.enabled=true;
            hiddenLights.Clear();hiddenProbes.Clear();
        }
        void OnDisable()
        {
            Camera.onPreCull-=Cull;Camera.onPostRender-=After;Restore();
            walls.Clear();candidates.Clear();candidateBounds.Clear();materialNames.Clear();shaderNames.Clear();cones.Clear();lights.Clear();probes.Clear();HiddenCount=CandidateCount=ActiveCones=HiddenLights=HiddenProbes=0;
            if(instance==this)instance=null;
        }
    }
}
