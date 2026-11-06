using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace ClosingShift.Android
{
    internal static class AndroidSessionLog
    {
        static readonly object gate = new object();
        static StreamWriter writer;
        static long bytes;
        static bool initialized;
        internal static void Initialize()
        {
            if (initialized) return;
            initialized=true;
            try
            {
                string directory = Path.Combine(Application.persistentDataPath, "logs");
                Directory.CreateDirectory(directory);
                writer = new StreamWriter(new FileStream(Path.Combine(directory, "closing-shift.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite));
                writer.AutoFlush = true;
                Application.logMessageReceivedThreaded += Write;
                Write("[engine] Unity=" + Application.unityVersion + "; GPU=" + SystemInfo.graphicsDeviceName, "", LogType.Log);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        static void Write(string message, string stack, LogType type)
        {
            lock (gate)
            {
                if (writer == null || bytes >= 4 * 1024 * 1024) return;
                try
                {
                    string text = DateTime.UtcNow.ToString("O") + " [" + type + "] " + message;
                    if (type != LogType.Log && !String.IsNullOrEmpty(stack)) text += "\n" + stack;
                    writer.WriteLine(text);
                    bytes += Encoding.UTF8.GetByteCount(text) + 1;
                    if (bytes >= 4 * 1024 * 1024) writer.WriteLine("[session] log size limit reached");
                }
                catch (IOException) { writer = null; }
            }
        }
    }
}
