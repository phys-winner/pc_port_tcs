using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace ClosingShift.Android
{
    public sealed class AndroidQaSettingsHold : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IPointerExitHandler
    {
        internal Button Target;
        readonly AndroidPauseSettingsHold hold = new AndroidPauseSettingsHold();
        PointerEventData pointer;
        static bool Paused { get { return AndroidQaOrders.Available && !AndroidQaOrders.Enabled && AndroidContext.Game != null && AndroidContext.Game.Flag("isPaused"); } }

        public void OnPointerDown(PointerEventData value)
        {
            Cancel();
            if (value.button != PointerEventData.InputButton.Left || !Paused || Target == null || !Target.IsActive() || !Target.IsInteractable()) return;
            pointer = value;
            hold.Begin(value.pointerId, Time.unscaledTime, value.position.x, value.position.y, true);
        }
        public void OnPointerUp(PointerEventData value) { if (pointer != null && value.pointerId == pointer.pointerId) Cancel(); }
        public void OnPointerExit(PointerEventData value) { OnPointerUp(value); }
        internal void Cancel() { pointer = null; hold.Cancel(); }
        void OnDisable() { Cancel(); }
        void OnApplicationFocus(bool focused) { if (!focused) Cancel(); }
        void OnApplicationPause(bool paused) { if (paused) Cancel(); }
        void Update()
        {
            if (pointer == null) return;
            if (!Paused || Target == null || !Target.IsActive() || !Target.IsInteractable()) { Cancel(); return; }
            if (!hold.Sample(pointer.pointerId, Time.unscaledTime, pointer.position.x, pointer.position.y, true, false, Screen.height * .035f)) return;
            // Consume the held press so its release cannot also open the ordinary settings.
            pointer.eligibleForClick = false;
            Cancel();
            AndroidQaOrders.Unlock();
            AndroidTouchOverlay.ShowQaEnabled();
            AndroidInputSettings.Haptic();
        }
    }
}
