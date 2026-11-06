using System;

namespace ClosingShift.Android
{
    internal sealed class AndroidPointerState
    {
        internal bool Held, Down, Up;
        internal void Advance(bool held, bool pulse)
        {
            bool next=held||pulse;
            Down=next&&!Held;Up=!next&&Held;Held=next;
        }
    }
    internal static class AndroidInputPolicy
    {
        internal static void Radial(float x, float y, float dead, out float resultX, out float resultY)
        {
            double magnitude = Math.Sqrt(x * x + y * y);
            dead = Math.Max(0, Math.Min(.9f, dead));
            if (magnitude <= dead) { resultX = resultY = 0; return; }
            float scale = (float)(Math.Min(1, (magnitude - dead) / (1 - dead)) / magnitude);
            resultX = x * scale; resultY = y * scale;
        }

        internal static bool Trigger(bool held, float value)
        { return value >= (held ? .35f : .55f); }
        internal static float RateDelta(float rate,float seconds)
        { return Single.IsNaN(seconds)||Single.IsInfinity(seconds)?0:rate*Math.Max(0,Math.Min(.1f,seconds)); }

        // Exactly one owner consumes Back; each resolved context has one matching original route.
        internal enum BackOwner { None, Dropdown, Rebind, NativePanel, Document, Inventory, Story, Gameplay }
        internal static BackOwner ResolveBack(bool dropdown, bool rebind, bool nativePanel, bool document, bool inventory, bool story)
        {
            if (dropdown) return BackOwner.Dropdown;
            if (rebind) return BackOwner.Rebind;
            if (nativePanel) return BackOwner.NativePanel;
            if (document) return BackOwner.Document;
            if (inventory) return BackOwner.Inventory;
            if (story) return BackOwner.Story;
            return BackOwner.Gameplay;
        }
    }
}
