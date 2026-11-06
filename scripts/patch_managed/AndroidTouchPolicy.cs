using System;

namespace ClosingShift.Android
{
    internal static class AndroidTouchPolicy
    {
        internal const int DefaultFpsCap = 60;
        internal static bool IsKeyboardTutorial(string path, bool keyboardIcons)
        {
            return keyboardIcons && (path == "Canvas (Options menu)/Show tutorial/tutorial" ||
                path == "Canvas (Options menu)/UI/PauseUI/tutorial");
        }
        internal static float AccelerationGain(float dpPerMs)
        {
            if (Single.IsNaN(dpPerMs) || Single.IsInfinity(dpPerMs)) return 1;
            return 1 + 1.5f * Math.Max(0, Math.Min(1, (dpPerMs - .5f) / 2));
        }
        internal static float LookDensity(float logicalDensity, int renderHeight, int nativeShortSide)
        {
            if (Single.IsNaN(logicalDensity) || Single.IsInfinity(logicalDensity) || logicalDensity <= 0 || renderHeight <= 0 || nativeShortSide <= 0) return 0;
            float scaled = logicalDensity * (renderHeight / (float)nativeShortSide);
            return Single.IsNaN(scaled) || Single.IsInfinity(scaled) || scaled <= 0 ? 0 : scaled;
        }
        internal static bool IsTap(float distanceSquared, float slop, float elapsed)
        { return distanceSquared <= slop * slop && elapsed >= 0 && elapsed <= .4f; }
        internal static float MoveRadius(float height, float scale) { return height * .125f * scale; }
        internal static int FrameCap(int requested, int refresh, bool menu)
        { int cap=requested==0?Math.Max(30,refresh):requested;return menu?Math.Min(30,cap):cap; }
        internal static bool ShouldRun(float distance, float radius) { return radius > 0 && distance >= radius * .85f; }
        internal static bool HasInteraction(Type type)
        {
            for(;type!=null;type=type.BaseType)
            {
                // These original scripts poll InteractingObject in Update instead of receiving UseObject.
                switch(type.FullName)
                {
                    case "AM_Item":case "InteractiveItem":case "AM_ItemInstantiator":case "SP_Item":case "SP_UseItem":
                    case "AdvancedDoorScript":case "Okaeri_DoorScript":case "NumberPadlockDigits":case "BatteryPickup":
                    case "DialogueTrigger":case "DialogueYesNo":case "SlideShow_DialogueTrigger":case "InteractMessage":
                    case "ActivationScript":case "SequenceActivationScript":case "ActivateCutscene":case "HidableObject":
                    case "PatronCard":case "SupporterContent_Show":case "ConsumableItems":case "ItemRequired":
                    case "ReadingMaterials":case "HealingStation":case "Saferoom_ChargeEnergy":case "SecurityCameras":
                    case "PlayerSwitchCamLookAtObj":case "ObjectFaceTarget":case "PlayerFaceObject":
                    case "TCS_Condiments":case "TCS_Machine":case "TCS_TrashCan":case "SpotlightControl":case "Unlock_Delay":return true;
                }
                if(type.GetMethod("UseObject",System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.DeclaredOnly,null,Type.EmptyTypes,null)!=null)return true;
            }
            return false;
        }
        internal static int InventoryGesture(float dx, float dy, float travelSquared, float width, float height, float elapsed, float x)
        {
            if (Math.Abs(dx) >= width * .08f && Math.Abs(dx) > Math.Abs(dy) * 1.3f) return dx < 0 ? 1 : -1;
            if (!IsTap(travelSquared, height * .035f, elapsed)) return 0;
            return x < .32f ? -1 : x > .68f ? 1 : 2;
        }
        internal static bool IsOpeningDialogue(string identity)
        { return identity == "1:---------Sequences---------\u001f-----INTRO-----\u001fIntro dialogue"; }
        internal static bool OpeningDialoguePending(bool active, bool completed)
        { return active && !completed; }
        internal static float TutorialRemaining(float remaining, bool controllableWorld, float elapsed)
        { return controllableWorld ? Math.Max(0, remaining - Math.Max(0, elapsed)) : remaining; }
        internal static float TutorialAlpha(float remaining)
        { float value=Math.Max(0,Math.Min(1,remaining));return value*value*(3-2*value); }
        internal static bool DocumentPage(bool reading, int page, int lastPage, bool previous)
        { return reading && lastPage>0 && (previous?page>0:page<lastPage); }
        internal static bool PauseSettings(string identity)
        { return identity!=null && !identity.StartsWith("0:",StringComparison.Ordinal) && identity.EndsWith("\u001fCanvas (Options menu)\u001fUI\u001fPauseUI\u001fPausePanel\u001fBTN_Options",StringComparison.Ordinal); }
        internal static float MenuFit(float width, float height, float safeWidth, float safeHeight, float preference)
        {
            if (width <= 0 || height <= 0 || safeWidth <= 0 || safeHeight <= 0) return 1;
            return Math.Min(safeWidth * .96f / width, safeHeight * .96f / height) * Math.Max(.8f, Math.Min(1, preference));
        }
        internal static float MenuOffset(float minimum, float maximum, float safeMinimum, float safeMaximum)
        { return (safeMinimum + safeMaximum - minimum - maximum) * .5f; }
    }

    internal sealed class AndroidPauseSettingsHold
    {
        int pointer;
        bool active;
        float began,x,y;
        internal void Begin(int id,float now,float px,float py,bool pauseSettings)
        { active=pauseSettings;pointer=id;began=now;x=px;y=py; }
        internal bool Sample(int id,float now,float px,float py,bool paused,bool canceled,float slop)
        {
            if(!active || id!=pointer)return false;
            float dx=px-x,dy=py-y;
            if(!paused || canceled || now<began || dx*dx+dy*dy>slop*slop){Cancel();return false;}
            if(now-began<5)return false;
            Cancel();return true;
        }
        internal void Cancel(){active=false;}
    }

    internal sealed class AndroidReadingClose
    {
        bool opened;
        internal bool Sample(bool reading){bool closed=opened && !reading;opened=reading;return closed;}
        internal void Reset(){opened=false;}
    }

    internal sealed class AndroidDialoguePress
    {
        object owner;
        int sentenceFrame=-1;
        internal void Begin(object manager,int frame){owner=manager;sentenceFrame=frame;}
        internal bool CanSkip(object manager,int frame){return !Object.ReferenceEquals(owner,manager) || frame!=sentenceFrame;}
    }

    internal sealed class AndroidThreeFingerTap
    {
        readonly int[] ids = new int[10];
        readonly float[] xs = new float[10], ys = new float[10];
        int count, maximum;
        float began;
        bool valid;
        internal bool Captured { get; private set; }
        internal bool HasTouches { get { return count != 0; } }
        internal bool Sample(int id, float x, float y, float now, int phase, float slop)
        {
            int index = Array.IndexOf(ids, id, 0, count);
            if (phase == 0 && index < 0)
            {
                if (count == 0) { began = now; maximum = 0; valid = true; Captured = false; }
                if (count == ids.Length) { valid = false; return false; }
                index = count++; ids[index] = id; xs[index] = x; ys[index] = y;
                maximum = Math.Max(maximum, count);
                if (maximum > 3 || now - began > .12f) valid = false;
                if (count == 3 && valid) Captured = true;
            }
            if (index < 0) return false;
            float dx = x - xs[index], dy = y - ys[index];
            if (dx * dx + dy * dy > slop * slop || now - began > .4f || now < began || phase == 4) valid = false;
            if (phase != 3 && phase != 4) return false;
            --count; ids[index] = ids[count]; xs[index] = xs[count]; ys[index] = ys[count];
            bool fire = count == 0 && Captured && valid && maximum == 3;
            if (count == 0) Cancel();
            return fire;
        }
        internal void Cancel() { count = maximum = 0; valid = Captured = false; }
    }

    internal sealed class AndroidLookAcceleration
    {
        float time, speed, distance;
        internal void Begin(float now) { time = now; speed = distance = 0; }
        internal float Sample(float pixels, float density, float now)
        {
            if (now < time) { Begin(now); return 1; }
            distance += pixels / Math.Max(.1f, density);
            float ms = (now - time) * 1000;
            if (ms > 0)
            {
                speed += (distance / ms - speed) * (float)(1 - Math.Exp(-ms / 30));
                distance = 0; time = now;
            }
            return AndroidTouchPolicy.AccelerationGain(speed);
        }
    }
}
