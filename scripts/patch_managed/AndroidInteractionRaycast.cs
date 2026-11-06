using System;
using System.Collections.Generic;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidInteractionRaycast
    {
        static readonly RaycastHit[] hits=new RaycastHit[64];
        static readonly List<MonoBehaviour> components=new List<MonoBehaviour>();
        static bool Usable(GameObject target,bool broaden)
        {
            if(target==null)return false;
            target.GetComponents<MonoBehaviour>(components);
            bool usable=false;
            foreach(MonoBehaviour component in components)
                if(component!=null && component.isActiveAndEnabled && AndroidTouchPolicy.HasInteraction(component.GetType())){usable=true;break;}
            if(!usable || !broaden)return usable;
            for(Transform node=target.transform;node!=null;node=node.parent)
            {
                node.GetComponents<MonoBehaviour>(components);
                foreach(MonoBehaviour component in components)
                {
                    if(component==null)continue;
                    Type type=component.GetType();
                    if(type.Name.IndexOf("Door",StringComparison.OrdinalIgnoreCase)>=0)return false;
                    if(type.FullName=="DynamicObject")
                    {
                        var field=type.GetField("dynamicType");
                        if(field!=null && Convert.ToString(field.GetValue(component))=="Door")return false;
                    }
                }
            }
            return true;
        }
        internal static bool Raycast(Ray ray,out RaycastHit hit,float range,int layers)
        {
            bool found=Physics.Raycast(ray,out hit,range,layers);
            if(Application.platform!=RuntimePlatform.Android || range<=0 || found && Usable(hit.collider.gameObject,false))return found;
            int count=Physics.SphereCastNonAlloc(ray,.18f,hits,range,layers);
            if(count==hits.Length)return found;
            float nearest=range;
            for(int i=0;i<count;++i)
            {
                RaycastHit candidate=hits[i];
                if(candidate.collider==null || !Usable(candidate.collider.gameObject,true))continue;
                Vector3 delta=candidate.point-ray.origin;
                float distance=delta.magnitude;
                if(distance<=.001f || distance>nearest)continue;
                RaycastHit visible;
                if(!Physics.Raycast(new Ray(ray.origin,delta/distance),out visible,distance+.02f,layers) || visible.collider!=candidate.collider)continue;
                nearest=distance;hit=candidate;found=true;
            }
            return found;
        }
    }
}
