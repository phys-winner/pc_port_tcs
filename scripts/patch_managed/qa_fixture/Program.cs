using System.Collections;
using System.Reflection;
using ClosingShift.Android;
using UnityEngine;

class Program
{
    static int checks;
    static void Check(bool value,string message=""){++checks;if(!value)throw new Exception("QA assertion "+checks+": "+message);}
    static void Main()
    {
        Check(AndroidQaOrders.Available && !AndroidQaOrders.Enabled);
        AndroidContext.Current=InputContext.Inventory;
        var inventory=new AM_Inventory();inventory.item=new AM_Item{inventory=inventory};AndroidContext.Inventory=new AndroidContext.View(inventory);
        Check(!AndroidQaOrders.HasSelectedOrder);
        var slot=new GameObject();inventory.ItemSlots.Add(slot);Check(!AndroidQaOrders.HasSelectedOrder);
        var item=new Transform();slot.transform.children.Add(item);Check(!AndroidQaOrders.HasSelectedOrder);
        var sticker=new TCS_OrderSticker();item.components.Add(sticker);Check(!AndroidQaOrders.HasSelectedOrder);
        var customer=new TCS_OrderCustomer();sticker.orderingCustomer=customer;Check(!AndroidQaOrders.HasSelectedOrder);
        AndroidQaOrders.Unlock();Check(AndroidQaOrders.Enabled && AndroidQaOrders.HasSelectedOrder);AndroidQaOrders.Unlock();Check(AndroidQaOrders.HasSelectedOrder);
        inventory.selectedItem=-1;Check(!AndroidQaOrders.HasSelectedOrder);inventory.selectedItem=1;Check(!AndroidQaOrders.HasSelectedOrder);inventory.selectedItem=0;
        AndroidContext.Current=InputContext.World;AndroidQaOrders.CompleteSelected();Check(customer.orderCompleteNum==0);AndroidContext.Current=InputContext.Inventory;
        customer.enabled=false;Check(!AndroidQaOrders.HasSelectedOrder);customer.enabled=true;
        AndroidQaOrders.CompleteSelected();Check(customer.orderCompleteNum==1&&customer.events==1&&inventory.removals==1&&!inventory.isOpen&&inventory.unequipped&&inventory.item.onUse==1&&inventory.item.numOfUses==0);
        AndroidQaOrders.CompleteSelected();Check(customer.orderCompleteNum==1&&inventory.removals==1);
        inventory.isOpen=true;Check(!AndroidQaOrders.HasSelectedOrder);customer.orderCompleteNum=0;customer.OrderQueues.Clear();Check(!AndroidQaOrders.HasSelectedOrder);
        var closing=new AM_Inventory();AndroidContext.Inventory=new AndroidContext.View(closing);
        AM_Item Add(MonoBehaviour sticker){int index=closing.ItemSlots.Count;var go=new GameObject();var child=new Transform();if(sticker!=null)child.components.Add(sticker);go.transform.children.Add(child);closing.ItemSlots.Add(go);var value=new AM_Item{inventory=closing,index=index};closing.items.Add(value);return value;}
        var expired=Add(new TCS_OrderSticker());
        var complete=Add(new TCS_OrderSticker{orderingCustomer=new TCS_OrderCustomer{orderCompleteNum=1}});
        var valid=Add(new TCS_OrderSticker{orderingCustomer=new TCS_OrderCustomer()});
        var ordinary=Add(null);
        typeof(AndroidQaOrders).GetProperty("Enabled",BindingFlags.Static|BindingFlags.NonPublic).SetValue(null,false);
        AndroidContext.Current=InputContext.World;AndroidQaOrders.CleanupInvalidOrders();
        Check(!AndroidQaOrders.Enabled && closing.removedIndices.SequenceEqual(new[]{0,1}),"Shift cleanup removes only invalid receipts even with QA hidden");
        Check(expired.onUse==1 && complete.onUse==1 && valid.onUse==0 && ordinary.onUse==0,"Cleanup preserves the stock consume event and every live order or ordinary item");
        AndroidQaOrders.CleanupInvalidOrders();Check(closing.removals==2,"Repeating shift cleanup cannot consume an already removed receipt");
        Console.WriteLine("QA_ORDER_PASS assertions="+checks);
    }
}
public class TCS_OrderSticker:MonoBehaviour {public TCS_OrderCustomer orderingCustomer;}
public class TCS_OrderCustomer:MonoBehaviour
{
    public IList OrderQueues=new ArrayList{new object()};public int orderCompleteNum,events;
    public void CompleteOrder(){++orderCompleteNum;++events;}
}
public class AM_Inventory:MonoBehaviour
{
    public IList ItemSlots=new ArrayList();public int selectedItem,removals;public bool isOpen=true,unequipped;
    public readonly List<AM_Item> items=new();public readonly List<int> removedIndices=new();
    public AM_Item item;public AM_Item GetItemWithIndex(int index){if(items.Count>0)return items[index];if(index!=selectedItem)throw new Exception();return item;}
    public void InventoryClose(){isOpen=false;}
    public void RemoveItemFromInventory(int index,bool unequip){++removals;unequipped=unequip;removedIndices.Add(index);((GameObject)ItemSlots[index]).transform.children.Clear();}
}
public class AM_Item {public AM_Inventory inventory;public int numOfUses=1,onUse,index;public void UseItem(bool placed){if(placed)throw new Exception();++onUse;if(--numOfUses<1)inventory.RemoveItemFromInventory(index,true);}}
namespace UnityEngine
{
    public class GameObject {public Transform transform=new Transform();}
    public class Transform {public List<Transform> children=new();public List<MonoBehaviour> components=new();public int childCount=>children.Count;public Transform GetChild(int index)=>children[index];public T[] GetComponents<T>()=>components.OfType<T>().ToArray();}
    public class MonoBehaviour {public string name="Fixture";public bool enabled=true;public bool isActiveAndEnabled=>enabled;}
    public static class Debug {public static void Log(string value){}}
}
namespace ClosingShift.Android
{
    enum InputContext {World,Inventory}
    static class AndroidContext
    {
        public static InputContext Current;public static View Inventory;
        internal class View
        {
            internal MonoBehaviour Owner;internal View(MonoBehaviour owner){Owner=owner;}
            internal object Get(string name)=>Owner.GetType().GetField(name,BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic)?.GetValue(Owner);
            internal bool Flag(string name)=>Get(name) is bool value&&value;
            internal void Invoke(string name)=>Owner.GetType().GetMethod(name).Invoke(Owner,null);
        }
    }
}
