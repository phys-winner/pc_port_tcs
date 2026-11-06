using UnityEngine;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    public sealed class AndroidActionIcon : MaskableGraphic
    {
        static void Capsule(VertexHelper mesh,Vector2 origin,float scale,Vector2 a,Vector2 b,float radius)
        {
            int start=mesh.currentVertCount;
            Vector2 direction=(b-a).normalized;
            float rotation=Mathf.Atan2(direction.y,direction.x);
            mesh.AddVert(origin+(a+b)*(.5f*scale),Color.white,Vector2.zero);
            for(int i=0;i<=24;++i)
            {
                float angle=rotation-Mathf.PI*.5f+i*Mathf.PI/12;
                Vector2 endpoint=i<=12?b:a;
                mesh.AddVert(origin+(endpoint+new Vector2(Mathf.Cos(angle),Mathf.Sin(angle))*radius)*scale,Color.white,Vector2.zero);
                if(i>0)mesh.AddTriangle(start,start+i,start+i+1);
            }
            mesh.AddTriangle(start,start+25,start+1);
        }
        protected override void OnPopulateMesh(VertexHelper mesh)
        {
            mesh.Clear();
            Vector2 center=rectTransform.rect.center;
            float radius=Mathf.Min(rectTransform.rect.width,rectTransform.rect.height)*.5f;
            mesh.AddVert(center,new Color(0,0,0,.35f),Vector2.zero);
            for(int i=0;i<=48;++i)
            {
                float angle=i*Mathf.PI/24;
                Vector2 direction=new Vector2(Mathf.Cos(angle),Mathf.Sin(angle));
                mesh.AddVert(center+direction*radius,new Color(1,1,1,.8f),Vector2.zero);
                mesh.AddVert(center+direction*(radius-2),new Color(0,0,0,.35f),Vector2.zero);
                if(i==0)continue;
                int v=1+i*2;mesh.AddTriangle(0,v-1,v+1);
                mesh.AddTriangle(v-2,v,v+1);mesh.AddTriangle(v-2,v+1,v-1);
            }
            Capsule(mesh,center,radius,new Vector2(0,-.20f),new Vector2(0,-.02f),.27f);
            Capsule(mesh,center,radius,new Vector2(-.20f,.02f),new Vector2(-.20f,.40f),.065f);
            Capsule(mesh,center,radius,new Vector2(-.065f,.04f),new Vector2(-.065f,.56f),.065f);
            Capsule(mesh,center,radius,new Vector2(.07f,.04f),new Vector2(.07f,.49f),.065f);
            Capsule(mesh,center,radius,new Vector2(.205f,.01f),new Vector2(.205f,.33f),.065f);
            Capsule(mesh,center,radius,new Vector2(-.19f,-.23f),new Vector2(-.44f,-.01f),.075f);
            Capsule(mesh,center,radius,new Vector2(0,-.43f),new Vector2(0,-.51f),.15f);
        }
        public override bool Raycast(Vector2 point,Camera camera)
        {
            Vector2 local;
            return RectTransformUtility.ScreenPointToLocalPointInRectangle(rectTransform,point,camera,out local) &&
                (local-rectTransform.rect.center).magnitude<=Mathf.Min(rectTransform.rect.width,rectTransform.rect.height)*.5f && base.Raycast(point,camera);
        }
    }
}
