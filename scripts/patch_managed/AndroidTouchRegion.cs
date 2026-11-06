using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace ClosingShift.Android
{
    public sealed class AndroidTouchRegion : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, ICancelHandler
    {
        public Action<PointerEventData> begin, drag, end, cancel;
        int pointer = int.MinValue, lastPollFrame = -1, lastDragFrame = -1;
        int canceledPointer = int.MinValue, canceledFrame = -1;
        PointerEventData capturedData;

        public void OnPointerDown(PointerEventData e)
        {
            if (AndroidTouchOverlay.SuppressTouches || pointer != int.MinValue || e.button != PointerEventData.InputButton.Left) return;
            if (e.pointerId >= 0)
            {
                // Clearing uGUI pointers during a context switch recreates a held finger as a fresh press.
                bool began = false;
                for (int i = 0; i < Input.touchCount; ++i)
                {
                    Touch touch = Input.GetTouch(i);
                    if (touch.fingerId == e.pointerId) { began = touch.phase == TouchPhase.Began; break; }
                }
                if (!began || canceledPointer == e.pointerId && canceledFrame == Time.frameCount) return;
            }
            if (e.pointerId >= 0) AndroidInput.NotifyTouch();
            pointer = e.pointerId; lastPollFrame = lastDragFrame = -1;
            if (capturedData == null) capturedData = new PointerEventData(EventSystem.current);
            {
                capturedData.Reset(); capturedData.pointerId = pointer; capturedData.button = e.button;
                capturedData.position = capturedData.pressPosition = e.position; capturedData.delta = Vector2.zero;
                capturedData.pointerPressRaycast = e.pointerPressRaycast;
                capturedData.pointerCurrentRaycast = e.pointerCurrentRaycast;
            }
#if TOUCH_PROBE
            AndroidTouchLeakProbe.Pointer(gameObject.name, pointer, "down", e.position, e.delta);
#endif
            if (begin != null) begin(e);
        }

        // Unity2020 PointerInputModule suppresses all drags while Cursor is locked, including real touches.
        internal void ProcessCapturedTouch()
        {
            if (pointer < 0 || Cursor.lockState != CursorLockMode.Locked || lastPollFrame == Time.frameCount) return;
            lastPollFrame = Time.frameCount;
            for (int i = 0; i < Input.touchCount; ++i)
            {
                Touch touch = Input.GetTouch(i);
                if (touch.fingerId != pointer) continue;
                if (touch.phase == TouchPhase.Ended || touch.phase == TouchPhase.Canceled)
                {
                    capturedData.position = touch.position;
                    Release(touch.phase == TouchPhase.Canceled); return;
                }
                Vector2 position = touch.position;
                Vector2 displacement = position - capturedData.position;
                if ((touch.phase == TouchPhase.Moved || displacement.sqrMagnitude > 0f) && drag != null && lastDragFrame != Time.frameCount)
                {
                    capturedData.position = position; capturedData.delta = displacement;
                    capturedData.dragging = true; lastDragFrame = Time.frameCount;
#if TOUCH_PROBE
                    AndroidTouchLeakProbe.Pointer(gameObject.name, pointer, "drag", touch.position, touch.deltaPosition);
#endif
                    drag(capturedData);
                }
                return;
            }
            Release(true);
        }

        public void OnDrag(PointerEventData e)
        {
            if (pointer != e.pointerId || drag == null || lastDragFrame == Time.frameCount) return;
            if (pointer >= 0 && Cursor.lockState == CursorLockMode.Locked) return;
            lastDragFrame = Time.frameCount;
            if (pointer >= 0 && capturedData != null) capturedData.position = e.position;
#if TOUCH_PROBE
            AndroidTouchLeakProbe.Pointer(gameObject.name, pointer, "drag", e.position, e.delta);
#endif
            drag(e);
        }
        public void OnPointerUp(PointerEventData e) { if (pointer == e.pointerId) { capturedData.position = e.position; Release(); } }
        public void OnCancel(BaseEventData e) { Release(true); }
        public void Release(bool canceled = false)
        {
            if (pointer == int.MinValue) return;
#if TOUCH_PROBE
            AndroidTouchLeakProbe.Pointer(gameObject.name, pointer, canceled ? "cancel" : "up", Vector2.zero, Vector2.zero);
#endif
            if (canceled) { canceledPointer = pointer; canceledFrame = Time.frameCount; }
            pointer = int.MinValue;
            var callback = canceled && cancel != null ? cancel : end;
            if (callback != null) callback(capturedData);
        }
        void OnDisable() { Release(true); }
    }
}
