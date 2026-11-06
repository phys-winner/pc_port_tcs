using System.Reflection;
using ClosingShift.Android;
using UnityEngine;
using UnityEngine.Rendering;

namespace ClosingShift.Android
{
    internal static class AndroidRoomOccluders
    {
        internal static readonly AndroidRoomCulling.Source[] Sources={new AndroidRoomCulling.Source(
            "Store props/wall","wallMesh",3,new float[]{1,0,0,0,0,1,0,0,0,0,1,0,0,0,0,1},new[]{"wall"},
            new[]{new Vector3(-4,-4,5),new Vector3(0,4,5),new Vector3(4,-4,5)})};
    }
}
static class Program
{
    static int assertions;
    static void Check(bool value,string message){assertions++;if(!value)throw new Exception(message);}
    static void Invoke(object target,string name)=>target.GetType().GetMethod(name,BindingFlags.NonPublic|BindingFlags.Instance).Invoke(target,null);
    static Vector3[] Corners(Bounds bounds)
    {
        Vector3 extent=bounds.extents;
        return Enumerable.Range(0,8).Select(i=>bounds.center+new Vector3((i&1)==0?-extent.x:extent.x,(i&2)==0?-extent.y:extent.y,(i&4)==0?-extent.z:extent.z)).ToArray();
    }
    static bool Hits(Vector3 eye,Vector3 target,Vector3 a,Vector3 b,Vector3 c)
    {
        Vector3 direction=target-eye,e0=b-a,e1=c-a,p=Vector3.Cross(direction,e1);
        float determinant=Vector3.Dot(e0,p);
        if(MathF.Abs(determinant)<1e-7f)return false;
        Vector3 t=eye-a;
        float u=Vector3.Dot(t,p)/determinant;
        Vector3 q=Vector3.Cross(t,e0);
        float v=Vector3.Dot(direction,q)/determinant,depth=Vector3.Dot(e1,q)/determinant;
        return u>=0 && v>=0 && u+v<=1 && depth>0 && depth<1;
    }
    static void Main()
    {
        var fixedOpaque=new Material{shader=new Shader{name="UVFree/StandardMetallic/Single-Texture",propertyCount=25}};
        fixedOpaque.values.Clear();
        Check(AndroidRoomCulling.OpaqueMaterial(fixedOpaque),"exact fixed opaque UVFree needs no nonexistent uniforms");
        foreach(string keyword in new[]{"_ALPHATEST_ON","_ALPHABLEND_ON","_ALPHAPREMULTIPLY_ON"})
        {fixedOpaque.keywords.Add(keyword);Check(!AndroidRoomCulling.OpaqueMaterial(fixedOpaque),"UVFree alpha keyword rejected");fixedOpaque.keywords.Remove(keyword);}
        fixedOpaque.renderQueue=3000;Check(!AndroidRoomCulling.OpaqueMaterial(fixedOpaque),"UVFree transparent queue rejected");fixedOpaque.renderQueue=2000;
        fixedOpaque.shader.propertyCount=24;Check(!AndroidRoomCulling.OpaqueMaterial(fixedOpaque),"different UVFree shader shape rejected");fixedOpaque.shader.propertyCount=25;
        fixedOpaque.shader.name="UnreviewedShader";Check(!AndroidRoomCulling.OpaqueMaterial(fixedOpaque),"unknown fixed-state shader rejected");
        foreach(string property in new[]{"_Mode","_ZWrite","_SrcBlend","_DstBlend"})
        {var standard=new Material();standard.values.Remove(property);Check(!AndroidRoomCulling.OpaqueMaterial(standard),"Standard missing required property rejected");}

        Vector3 eye=new Vector3(),a=new Vector3(-4,-4,5),b=new Vector3(0,4,5),c=new Vector3(4,-4,5),normal=Vector3.Cross(b-a,c-a).normalized;
        Check(AndroidRoomCulling.Cone.Create(a,b,c,normal,eye,new Vector3(0,0,1),.1f,out var cone),"front-facing opaque triangle");
        Check(cone.Contains(new Bounds(new Vector3(0,-1,10),new Vector3(1,1,1)),eye),"fully hidden bounds");
        Check(!cone.Contains(new Bounds(new Vector3(0,-1,4.9f),new Vector3(1,1,1)),eye),"plane intersection stays visible");
        Check(!cone.Contains(new Bounds(new Vector3(3,0,10),new Vector3(4,1,1)),eye),"partial silhouette stays visible");
        Check(!cone.Contains(new Bounds(eye,new Vector3(100,100,100)),eye),"camera inside target stays visible");
        Check(!AndroidRoomCulling.Cone.Create(a,b,c,normal,eye,new Vector3(0,0,1),6,out _),"near-clipped wall cannot occlude");
        Check(!AndroidRoomCulling.Cone.Create(a,b,c,normal,new Vector3(0,0,6),new Vector3(0,0,-1),.1f,out _),"back-facing wall cannot occlude");
        var random=new Random(7401);int accepted=0;
        for(int i=0;i<4000;i++)
        {
            eye=new Vector3((float)random.NextDouble()*4-2,(float)random.NextDouble()*2-1,0);
            AndroidRoomCulling.Cone.Create(a,b,c,normal,eye,new Vector3(0,0,1),.1f,out cone);
            var bounds=new Bounds(new Vector3((float)random.NextDouble()*20-10,(float)random.NextDouble()*20-10,(float)random.NextDouble()*20),
                new Vector3((float)random.NextDouble()*2,(float)random.NextDouble()*2,(float)random.NextDouble()*2));
            if(!cone.Contains(bounds,eye))continue;
            accepted++;
            foreach(Vector3 corner in Corners(bounds))Check(Hits(eye,corner,a,b,c),"support plane proof agrees with independent ray/triangle intersection");
        }
        Check(accepted>100,"random proof exercises hidden boxes");
        eye=new Vector3();
        Vector3 la=new Vector3(-4,-4,5),lb=new Vector3(-.05f,4,5),lc=new Vector3(-.05f,-4,5);
        Vector3 ra=new Vector3(.05f,-4,5),rb=new Vector3(.05f,4,5),rc=new Vector3(4,-4,5);
        AndroidRoomCulling.Cone.Create(la,lb,lc,normal,eye,new Vector3(0,0,1),.1f,out var left);
        AndroidRoomCulling.Cone.Create(ra,rb,rc,normal,eye,new Vector3(0,0,1),.1f,out var right);
        Bounds gap=new Bounds(new Vector3(0,0,10),new Vector3(.5f,.5f,.5f));
        Check(!left.Contains(gap,eye) && !right.Contains(gap,eye),"narrow portal sliver stays visible");

        GameObject store=new GameObject("Store props"),wallObject=new GameObject("wall",store);
        var wall=wallObject.AddComponent<MeshRenderer>();wall.bounds=new Bounds(new Vector3(0,0,5),new Vector3(8,8,.1f));
        wall.materials.Add(new Material{name="wall"});wallObject.AddComponent<MeshFilter>().sharedMesh=new Mesh{name="wallMesh",vertexCount=3};
        GameObject branch=new GameObject("coffee_items",store);
        var hidden=new GameObject("hidden",branch).AddComponent<MeshRenderer>();hidden.bounds=new Bounds(new Vector3(0,-1,10),new Vector3(1,1,1));
        var visible=new GameObject("sliver",branch).AddComponent<MeshRenderer>();visible.bounds=new Bounds(new Vector3(3,0,10),new Vector3(4,1,1));
        var main=new GameObject("world").AddComponent<Camera>();Camera.main=main;
        var other=new GameObject("recipe depth").AddComponent<Camera>();
        var hiddenLight=new GameObject("lamp behind wall").AddComponent<Light>();hiddenLight.transform.position=new Vector3(0,-1,10);hiddenLight.range=.5f;
        var visibleLight=new GameObject("lamp crossing silhouette").AddComponent<Light>();visibleLight.transform.position=new Vector3(3,0,10);visibleLight.range=2;
        var sunLight=new GameObject("sun behind wall").AddComponent<Light>();sunLight.type=LightType.Directional;sunLight.transform.position=new Vector3(0,-1,10);sunLight.range=.5f;
        var hiddenSpot=new GameObject("downlight behind wall").AddComponent<Light>();hiddenSpot.type=LightType.Spot;hiddenSpot.spotAngle=40;hiddenSpot.range=2;
        hiddenSpot.transform.position=new Vector3(0,0,9);hiddenSpot.transform.forward=new Vector3(0,-1,0);
        var wideSpot=new GameObject("wide spot crossing silhouette").AddComponent<Light>();wideSpot.type=LightType.Spot;wideSpot.spotAngle=150;wideSpot.range=2;
        wideSpot.transform.position=new Vector3(0,0,9);wideSpot.transform.forward=new Vector3(0,-1,0);
        var hiddenProbe=new GameObject("probe behind wall").AddComponent<ReflectionProbe>();hiddenProbe.bounds=new Bounds(new Vector3(0,-1,10),new Vector3(1,1,1));
        var blendProbe=new GameObject("probe blending past silhouette").AddComponent<ReflectionProbe>();blendProbe.bounds=new Bounds(new Vector3(0,-1,10),new Vector3(1,1,1));blendProbe.blendDistance=2;
        AndroidRoomCulling.Prepare();var runtime=store.GetComponent<AndroidRoomCulling>();Invoke(runtime,"OnEnable");
        Camera.Before(main);
        Check(!hiddenLight.enabled && visibleLight.enabled && sunLight.enabled,"only a local light wholly behind the wall skips this render");
        Check(!hiddenSpot.enabled && wideSpot.enabled && AndroidRoomCulling.HiddenLights==2,"a narrow spot cone is bounded tighter than its range sphere");
        Bounds coneBox=AndroidRoomCulling.LightBounds(hiddenSpot);
        Check(MathF.Abs(coneBox.max.y-0)<1e-4f && MathF.Abs(coneBox.min.y+2)<1e-4f && coneBox.extents.x<.75f,"spot bounds contain apex and base disk only");
        Check(!hiddenProbe.enabled && blendProbe.enabled && AndroidRoomCulling.HiddenProbes==1,"reflection probe culling includes its blend distance");
        Camera.After(main);
        Check(hiddenLight.enabled && hiddenSpot.enabled && hiddenProbe.enabled,"post-render restores culled lights and probes");
        Camera.Before(other);Check(hiddenLight.enabled && hiddenProbe.enabled,"other cameras never cull lights or probes");Camera.After(other);
        AndroidRoomCulling.CullLightsAndProbes=false;Camera.Before(main);
        Check(hiddenLight.enabled && hiddenProbe.enabled,"volume culling can be switched off");Camera.After(main);AndroidRoomCulling.CullLightsAndProbes=true;
        hiddenLight.enabled=false;Camera.Before(main);Camera.After(main);Check(!hiddenLight.enabled,"authored disabled light remains disabled");hiddenLight.enabled=true;
        Camera.Before(main);
        Check(hidden.shadowCastingMode==ShadowCastingMode.ShadowsOnly && hidden.enabled && !hidden.forceRenderingOff,"hidden color draw preserves original shadow caster and gameplay");
        Check(visible.shadowCastingMode==ShadowCastingMode.On,"partial silhouette remains rendered");
        Check(AndroidRoomCulling.HiddenCount==1 && AndroidRoomCulling.ActiveCones==1,"actual camera cull counts");
        Check(AndroidRoomCulling.Occluded(main,hidden.bounds),"recipe helper uses current camera proof");
        Check(!AndroidRoomCulling.Occluded(other,hidden.bounds),"recipe helper rejects other camera");
        Time.frameCount++;
        Check(!AndroidRoomCulling.Occluded(main,hidden.bounds),"recipe helper rejects stale frame");
        Camera.After(main);
        Check(hidden.shadowCastingMode==ShadowCastingMode.On && !AndroidRoomCulling.Occluded(main,hidden.bounds),"post-render restores state and expires cone ownership");
        hidden.shadowCastingMode=ShadowCastingMode.Off;
        Camera.Before(main);Check(hidden.forceRenderingOff && hidden.enabled,"non-shadow caster hidden without gameplay disable");
        Camera.Before(other);Check(!hidden.forceRenderingOff && hidden.shadowCastingMode==ShadowCastingMode.Off,"nested depth camera restores prior renderer settings");
        hidden.shadowCastingMode=ShadowCastingMode.TwoSided;
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.TwoSided && !hidden.forceRenderingOff,"two-sided shadow contract is preserved");
        hidden.shadowCastingMode=ShadowCastingMode.On;hidden.enabled=false;
        Camera.Before(main);Camera.After(main);Check(!hidden.enabled,"authored disabled renderer remains disabled");hidden.enabled=true;
        Camera.Before(main);main.transform.position=new Vector3(20,0,0);
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.On && AndroidRoomCulling.HiddenCount==0,"moving camera reevaluates visibility every render");main.transform.position=new Vector3();
        main.transform.forward=new Vector3(0,0,-1);
        Camera.Before(main);Check(AndroidRoomCulling.ActiveCones==0,"rotating camera expires old cone set");main.transform.forward=new Vector3(0,0,1);
        wall.materials[0].values["_Mode"]=1;
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.On && AndroidRoomCulling.ActiveCones==0,"transparent runtime wall cannot occlude");wall.materials[0].values["_Mode"]=0;
        wall.enabled=false;
        Camera.Before(main);Check(AndroidRoomCulling.ActiveCones==0,"disabled wall cannot occlude");wall.enabled=true;
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.ShadowsOnly,"culling resumes after wall restoration");
        main.gameObject.scene=new Scene{buildIndex=0};
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.On,"scene change restores renderer");main.gameObject.scene=new Scene{buildIndex=1};
        Camera.Before(main);Invoke(runtime,"OnDisable");
        Check(hidden.shadowCastingMode==ShadowCastingMode.On && !hidden.forceRenderingOff && !AndroidRoomCulling.Occluded(main,hidden.bounds),"component disable restores all renderer state");
        Camera.Before(main);Check(hidden.shadowCastingMode==ShadowCastingMode.On,"disabled component unsubscribes camera callbacks");
        Console.WriteLine("ROOM_CULLING_PASS "+assertions+" assertions; independent corner ray proof "+accepted+" boxes");
    }
}
