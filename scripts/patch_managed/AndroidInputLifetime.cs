using UnityEngine;

namespace ClosingShift.Android
{
    [DefaultExecutionOrder(-32000)]
    public sealed class AndroidInputLifetime : MonoBehaviour
    {
        AndroidTouchOverlay overlay;
        int width, height;
        Rect safeArea;
        void Update()
        {
            bool cancel=width!=Screen.width || height!=Screen.height || safeArea!=Screen.safeArea;
            width=Screen.width; height=Screen.height; safeArea=Screen.safeArea;
            for(int i=0;i<Input.touchCount;++i)cancel|=Input.GetTouch(i).phase==TouchPhase.Canceled;
            // Clear Unity's pending pointer press before its input module treats CANCEL as a release.
            if(cancel)AndroidInput.Reset();
            if(overlay!=null)overlay.ProcessCapturedTouches();
            AndroidInput.EnsureFrame();
            if(overlay==null)overlay=gameObject.AddComponent<AndroidTouchOverlay>();
#if CLOSING_SHIFT_PERF
            AndroidPerfProbe.Ensure();
#endif
        }
        void LateUpdate() { AndroidGraphics.Enforce(); AndroidRenderScale.Enforce(); }
        internal void ReleaseTouch() { if (overlay != null) overlay.ReleaseAll(); }
        internal void CloseSettings(){if(overlay!=null)overlay.CloseSettings();}
        void OnApplicationFocus(bool focused) { if (!focused) AndroidInput.Reset(); }
        void OnApplicationPause(bool paused) { if (paused) AndroidInput.Reset(); }
        void OnDestroy() { AndroidInput.Reset(); }
    }
}
