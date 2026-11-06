using System;
using UnityEngine;

namespace ClosingShift.Android
{
    public static class AndroidSaveStorage
    {
        static string root;
        public static string Root
        {
            get
            {
                if (Application.platform != RuntimePlatform.Android) return Application.persistentDataPath;
                if (root == null)
                {
                    using (var storage = new AndroidJavaClass("com.chillasart.theclosingshift.port.PortStorage"))
                        root = storage.CallStatic<string>("saveRoot");
                    if (String.IsNullOrEmpty(root)) throw new InvalidOperationException("Save storage is unavailable");
                    Debug.Log("PORT_SAVE Android/data app-specific user data ready");
                }
                return root;
            }
        }
    }
}
