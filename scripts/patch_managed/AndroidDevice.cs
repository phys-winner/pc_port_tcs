using System;
using UnityEngine;

namespace ClosingShift.Android
{
    // Device facts that cannot change while the player runs. Unity's getters allocate a new
    // string (graphicsDeviceName) or parse /proc/meminfo (systemMemorySize) on every call.
    // Host fixtures switch the simulated device between cases, so they read values directly.
    internal static class AndroidDevice
    {
#if CLOSING_SHIFT_FIXTURE
        internal static string Gpu { get { return SystemInfo.graphicsDeviceName ?? ""; } }
        internal static int MemoryMB { get { return SystemInfo.systemMemorySize; } }
        internal static bool DeferredCapable { get { return ComputeDeferred(); } }
        internal static bool ContainsMaliG715 { get { return Gpu.IndexOf("Mali-G715", StringComparison.OrdinalIgnoreCase) >= 0; } }
#else
        static string gpu;
        static int memory = -1, deferred = -1, maliG715 = -1;
        internal static string Gpu { get { return gpu ?? (gpu = SystemInfo.graphicsDeviceName ?? ""); } }
        internal static int MemoryMB { get { if (memory < 0) memory = SystemInfo.systemMemorySize; return memory; } }
        internal static bool DeferredCapable { get { if (deferred < 0) deferred = ComputeDeferred() ? 1 : 0; return deferred == 1; } }
        internal static bool ContainsMaliG715
        {
            get { if (maliG715 < 0) maliG715 = Gpu.IndexOf("Mali-G715", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; return maliG715 == 1; }
        }
#endif
        // Limit the dense-lighting path to the GPUs selected for hardware validation.
        static bool ComputeDeferred()
        {
            return SystemInfo.supportedRenderTargetCount >= 4 && (Gpu.IndexOf("Adreno", StringComparison.OrdinalIgnoreCase) >= 0 ||
                Gpu.IndexOf("Mali-G715", StringComparison.OrdinalIgnoreCase) >= 0);
        }
    }
}
