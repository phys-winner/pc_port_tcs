using System;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidController
    {
        static AndroidJavaClass bridge;
        static IntPtr bridgeClass, pollMethod;
        static readonly jvalue[] noArguments = new jvalue[0];
        static bool unavailable, logged;
        static int generation = -1;
        internal static Vector2 Move, Look, Navigate;
        internal static int Buttons, Presses, BackCounter;
        internal static bool Connected, Changed, Activity;
        internal static float RightTrigger, LeftTrigger;

        internal static void Poll()
        {
            Changed = Activity = false;
            if (Application.platform != RuntimePlatform.Android || unavailable) return;
            try
            {
                if (bridge == null) bridge = new AndroidJavaClass("com.chillasart.theclosingshift.port.ControllerBridge");
                // Resolve the static method once; AndroidJavaClass.CallStatic rebuilds its JNI
                // signature and converts the result through reflection on every frame.
                if (pollMethod == IntPtr.Zero)
                {
                    bridgeClass = bridge.GetRawClass();
                    pollMethod = AndroidJNIHelper.GetMethodID(bridgeClass, "poll", "()[F", true);
                    if (pollMethod == IntPtr.Zero) throw new InvalidOperationException("Controller poll method missing");
                }
                IntPtr array = AndroidJNI.CallStaticObjectMethod(bridgeClass, pollMethod, noArguments);
                IntPtr thrown = AndroidJNI.ExceptionOccurred();
                if (thrown != IntPtr.Zero)
                {
                    AndroidJNI.ExceptionClear(); AndroidJNI.DeleteLocalRef(thrown);
                    if (array != IntPtr.Zero) AndroidJNI.DeleteLocalRef(array);
                    throw new InvalidOperationException("Controller poll threw");
                }
                float[] value = array == IntPtr.Zero ? null : AndroidJNI.FromFloatArray(array);
                if (array != IntPtr.Zero) AndroidJNI.DeleteLocalRef(array);
                if (value == null || value.Length != 17) throw new InvalidOperationException("Controller snapshot schema differs");
                Vector2 oldMove=Move,oldLook=Look,oldNav=Navigate;int oldButtons=Buttons;
                float oldLeft=LeftTrigger,oldRight=RightTrigger;
                Connected = value[2] != 0;
                Changed = generation != (int)value[13]; generation = (int)value[13];
                float x, y;
                AndroidInputPolicy.Radial(value[3], -value[4], Mathf.Max(AndroidInputSettings.MoveDeadzone, value[14]), out x, out y);
                Move = new Vector2(x, y);
                AndroidInputPolicy.Radial(value[5], -value[6], Mathf.Max(AndroidInputSettings.LookDeadzone, value[15]), out x, out y);
                Look = new Vector2(x, AndroidInputSettings.InvertPadY ? -y : y);
                Buttons = (int)value[11]; Presses = (int)value[16]; BackCounter = (int)value[12];
                Navigate = new Vector2(value[7], -value[8]);
                if ((Buttons & (1 << 11)) != 0) Navigate.y = 1;
                if ((Buttons & (1 << 12)) != 0) Navigate.y = -1;
                if ((Buttons & (1 << 13)) != 0) Navigate.x = -1;
                if ((Buttons & (1 << 14)) != 0) Navigate.x = 1;
                LeftTrigger = Mathf.Max(value[9], (Buttons & (1 << 15)) != 0 ? 1 : 0);
                RightTrigger = Mathf.Max(value[10], (Buttons & (1 << 16)) != 0 ? 1 : 0);
                Activity=(Move-oldMove).sqrMagnitude>.0025f||(Look-oldLook).sqrMagnitude>.0025f||(Navigate-oldNav).sqrMagnitude>.01f||Buttons!=oldButtons||Presses!=0||Mathf.Abs(LeftTrigger-oldLeft)>.1f||Mathf.Abs(RightTrigger-oldRight)>.1f;
                if (!Connected) { Move = Look = Navigate = Vector2.zero; Buttons = Presses = 0; LeftTrigger = RightTrigger = 0; }
                if (!logged) { logged = true; Debug.Log("PORT_INPUT Android semantic controller bridge ready; physical profile not yet hardware-verified"); }
            }
            catch (Exception error)
            {
                unavailable = true; Connected = false; Changed = true; Move = Look = Navigate = Vector2.zero; Buttons = Presses = 0; LeftTrigger = RightTrigger = 0;
                Debug.LogError("PORT_INPUT controller bridge unavailable: " + error.GetType().Name + ": " + error.Message);
            }
        }

        internal static void Cancel()
        {
            if (bridge != null) try { bridge.CallStatic("cancel"); } catch (Exception) { }
            Move = Look = Navigate = Vector2.zero; Buttons = Presses = 0; RightTrigger = LeftTrigger = 0;
        }
    }

    internal static class AndroidInputSettings
    {
        internal static float TouchSensitivity = 1, PadSensitivity = 8, MoveDeadzone = .19f, LookDeadzone = .19f, Scale = 1;
        internal static bool InvertTouchY, InvertPadY, LeftHanded;
        internal static int FpsMode;
        internal static int[] FpsValues = { 30, 60, 0 };
        static AndroidJavaClass display;
        static int displayCap=-1;
        internal static int DisplayRate { get { return FpsValues[FpsValues.Length-2]; } }
        internal static void Haptic()
        {
            if(display!=null)try { display.CallStatic("haptic"); } catch(Exception) { }
        }
        internal static void RequestFrameRate(int cap)
        {
            if(cap==displayCap || display==null)return;
            displayCap=cap;
            try { display.CallStatic("setFrameRate",cap); }
            catch(Exception error) { Debug.LogWarning("PORT_DISPLAY request failed: "+error.GetType().Name); }
        }
        internal static void Load()
        {
            TouchSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.TouchLook", 1), .25f, 3);
            PadSensitivity = Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.PadLook", 8), 1, 24);
            MoveDeadzone = Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.PadMoveDeadzone", .19f), .05f, .4f);
            LookDeadzone = Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.PadLookDeadzone", .19f), .05f, .4f);
            Scale = Mathf.Clamp(PlayerPrefs.GetFloat("ClosingShift.TouchScale", 1), .7f, 1.4f);
            InvertTouchY = PlayerPrefs.GetInt("ClosingShift.InvertTouchY", 0) != 0;
            InvertPadY = PlayerPrefs.GetInt("ClosingShift.InvertPadY", 0) != 0;
            LeftHanded = PlayerPrefs.GetInt("ClosingShift.LeftHanded", 0) != 0;
            try {
                display=new AndroidJavaClass("com.chillasart.theclosingshift.port.PortDisplay");
                int[] values=display.CallStatic<int[]>("frameRates");
                if(values==null || values.Length<2 || values[0]!=30 || values[values.Length-1]!=0)throw new InvalidOperationException("Invalid display modes");
                for(int i=1;i<values.Length-1;++i)if(values[i]<=values[i-1] || values[i]>240)throw new InvalidOperationException("Unsorted display modes");
                FpsValues=values;
            } catch(Exception error) { Debug.LogWarning("PORT_DISPLAY using fallback modes: "+error.GetType().Name); }
            int[] legacy={30,45,60,0};
            int requested=PlayerPrefs.GetInt("ClosingShift.FpsCap",legacy[Mathf.Clamp(PlayerPrefs.GetInt("ClosingShift.FpsMode",2),0,legacy.Length-1)]);
            FpsMode=Array.IndexOf(FpsValues,requested);
            if(FpsMode<0)FpsMode=Math.Max(0,Array.IndexOf(FpsValues,AndroidTouchPolicy.DefaultFpsCap));
        }
        internal static void Save()
        {
            PlayerPrefs.SetFloat("ClosingShift.TouchLook", TouchSensitivity); PlayerPrefs.SetFloat("ClosingShift.PadLook", PadSensitivity);
            PlayerPrefs.SetFloat("ClosingShift.PadMoveDeadzone", MoveDeadzone); PlayerPrefs.SetFloat("ClosingShift.PadLookDeadzone", LookDeadzone);
            PlayerPrefs.SetFloat("ClosingShift.TouchScale", Scale);
            PlayerPrefs.SetInt("ClosingShift.InvertTouchY", InvertTouchY ? 1 : 0); PlayerPrefs.SetInt("ClosingShift.InvertPadY", InvertPadY ? 1 : 0);
            PlayerPrefs.SetInt("ClosingShift.LeftHanded", LeftHanded ? 1 : 0);
            PlayerPrefs.SetInt("ClosingShift.FpsMode", FpsMode);
            PlayerPrefs.SetInt("ClosingShift.FpsCap",FpsValues[FpsMode]);
            PlayerPrefs.Save();
        }
    }
}
