using System;
using System.Collections;
using System.Reflection;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidQaOrders
    {
        static MonoBehaviour Sticker(GameObject slot)
        {
            if(slot==null || slot.transform.childCount==0)return null;
            foreach(MonoBehaviour component in slot.transform.GetChild(0).GetComponents<MonoBehaviour>())
                if(component!=null && component.GetType().FullName=="TCS_OrderSticker")return component;
            return null;
        }
        static bool HasOutstandingOrder(MonoBehaviour sticker)
        {
            if(sticker==null)return false;
            MonoBehaviour customer=new AndroidContext.View(sticker).Get("orderingCustomer") as MonoBehaviour;
            if(customer==null || !customer.isActiveAndEnabled)return false;
            var view=new AndroidContext.View(customer);
            IList orders=view.Get("OrderQueues") as IList;
            object completed=view.Get("orderCompleteNum");
            return orders!=null && completed is int && (int)completed>=0 && (int)completed<orders.Count;
        }
        internal static void UseInventoryItem(object item)
        {
            MethodInfo consume=item==null?null:item.GetType().GetMethod("UseItem",new[]{typeof(bool)});
            if(consume==null)throw new MissingMethodException("AM_Item.UseItem(bool)");
            consume.Invoke(item,new object[]{false});
        }
        internal static void CleanupInvalidOrders()
        {
            var inventory=AndroidContext.Inventory;
            IList slots=inventory==null?null:inventory.Get("ItemSlots") as IList;
            if(slots==null)return;
            MethodInfo select=inventory.Owner.GetType().GetMethod("GetItemWithIndex",new[]{typeof(int)});
            if(select==null)throw new MissingMethodException("AM_Inventory.GetItemWithIndex(int)");
            int removed=0;
            for(int i=0;i<slots.Count;++i)
            {
                MonoBehaviour sticker=Sticker(slots[i] as GameObject);
                if(sticker==null || HasOutstandingOrder(sticker))continue;
                UseInventoryItem(select.Invoke(inventory.Owner,new object[]{i}));++removed;
            }
            if(removed>0)Debug.Log("PORT_ORDER_CLEANUP expired="+removed);
        }
#if CLOSING_SHIFT_QA
        internal static bool Available { get { return true; } }
        internal static bool Enabled { get; private set; }
        internal static void Unlock(){if(!Enabled){Enabled=true;Debug.Log("PORT_QA_ORDER enabled=pause-settings-hold");}}
        static bool busy;
        static MonoBehaviour SelectedSticker()
        {
            var inventory=AndroidContext.Inventory;
            if(!Enabled || busy || AndroidContext.Current!=InputContext.Inventory || inventory==null || !inventory.Flag("isOpen"))return null;
            IList slots=inventory.Get("ItemSlots") as IList;
            object selection=inventory.Get("selectedItem");
            if(slots==null || !(selection is int) || (int)selection<0 || (int)selection>=slots.Count)return null;
            return Sticker(slots[(int)selection] as GameObject);
        }
        internal static bool HasSelectedOrder
        {
            get
            {
                return HasOutstandingOrder(SelectedSticker());
            }
        }
        internal static void CompleteSelected()
        {
            if(!HasSelectedOrder)return;
            MonoBehaviour sticker=SelectedSticker();
            var inventory=AndroidContext.Inventory;
            MonoBehaviour customer=(MonoBehaviour)new AndroidContext.View(sticker).Get("orderingCustomer");
            var view=new AndroidContext.View(customer);
            int before=(int)view.Get("orderCompleteNum");
            MethodInfo select=inventory.Owner.GetType().GetMethod("GetItemWithIndex",new[]{typeof(int)});
            object item=select==null?null:select.Invoke(inventory.Owner,new object[]{(int)inventory.Get("selectedItem")});
            if(item==null)throw new MissingMethodException("AM_Inventory.GetItemWithIndex(int)");
            busy=true;
            try
            {
                inventory.Invoke("InventoryClose");
                view.Invoke("CompleteOrder");
                if((int)view.Get("orderCompleteNum")!=before+1)throw new InvalidOperationException("QA order completion did not advance exactly once");
                UseInventoryItem(item);
                Debug.Log("PORT_QA_ORDER customer="+customer.name+" completed="+(before+1)+" stickerConsumed=true");
            }
            finally { busy=false; }
        }
#else
        internal static bool Available { get { return false; } }
        internal static bool Enabled { get { return false; } }
        internal static void Unlock() { }
        internal static bool HasSelectedOrder { get { return false; } }
        internal static void CompleteSelected() { }
#endif
    }
}
