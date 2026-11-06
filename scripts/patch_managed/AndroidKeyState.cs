using System;

namespace ClosingShift.Android
{
    internal sealed class AndroidKeyState
    {
        readonly byte[] desired = new byte[512];
        readonly byte[] queuedPress = new byte[512];
        internal readonly bool[] Held = new bool[512], Down = new bool[512], Up = new bool[512];

        internal void Set(int key, bool value)
        { Set(0, key, value); }

        internal void Set(int source, int key, bool value)
        {
            if (source < 0 || source > 7 || key <= 0 || key >= desired.Length) return;
            byte mask = (byte)(1 << source);
            if (value && (desired[key] & mask) == 0) queuedPress[key] |= mask;
            if (value) desired[key] |= mask; else desired[key] &= (byte)~mask;
        }

        internal void Advance()
        {
            for (int i = 0; i < Held.Length; ++i)
            {
                bool next = desired[i] != 0 || queuedPress[i] != 0;
                Down[i] = next && !Held[i];
                Up[i] = !next && Held[i];
                Held[i] = next;
                queuedPress[i] = 0;
            }
        }

        internal void Cancel(int key)
        {
            if (key <= 0 || key >= desired.Length) return;
            Cancel(0, key);
        }

        internal void Cancel(int source, int key)
        {
            if (source < 0 || source > 7 || key <= 0 || key >= desired.Length) return;
            byte mask = (byte)~(1 << source);
            desired[key] &= mask;
            queuedPress[key] &= mask;
        }

        internal void CancelSource(int source)
        {
            for (int i = 1; i < desired.Length; ++i) Cancel(source, i);
        }

        internal void Reset()
        {
            Array.Clear(desired, 0, desired.Length);
            Array.Clear(queuedPress, 0, queuedPress.Length);
            Array.Clear(Held, 0, Held.Length);
            Array.Clear(Down, 0, Down.Length);
            Array.Clear(Up, 0, Up.Length);
        }
    }
}
