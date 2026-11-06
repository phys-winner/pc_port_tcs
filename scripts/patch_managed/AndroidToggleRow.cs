using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    public sealed class AndroidToggleRow : MonoBehaviour, IPointerClickHandler
    {
        public Toggle Target;
        public void OnPointerClick(PointerEventData data)
        {
            // The checkbox keeps its own click handler; only its surrounding row forwards a click.
            if (Target != null && !data.dragging && data.button == PointerEventData.InputButton.Left && Target.IsActive() && Target.IsInteractable())
                Target.isOn = !Target.isOn;
        }
    }
}
