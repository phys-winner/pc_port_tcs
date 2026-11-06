using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ClosingShift.Android
{
    public static class AndroidSceneLoader
    {
        static AndroidSceneLoaderLifetime lifetime;

        public static void LoadScene(string sceneName)
        {
            if (Application.platform != RuntimePlatform.Android || !Application.CanStreamedLevelBeLoaded(sceneName))
            {
                SceneManager.LoadScene(sceneName);
                return;
            }
            if (lifetime == null)
            {
                var owner = new GameObject("AndroidSceneLoader");
                UnityEngine.Object.DontDestroyOnLoad(owner);
                lifetime = owner.AddComponent<AndroidSceneLoaderLifetime>();
            }
            lifetime.Begin(sceneName);
        }
    }

    public sealed class AndroidSceneLoaderLifetime : MonoBehaviour
    {
        bool loading;

        public void Begin(string sceneName)
        {
            if (loading) return;
            loading = true;
            StartCoroutine(Transition(sceneName));
        }

        IEnumerator Transition(string sceneName)
        {
            var previousPriority = Application.backgroundLoadingPriority;
            // This transition replaces the scene; spend loading time on integration instead of idle frames.
            Application.backgroundLoadingPriority = ThreadPriority.High;
            try
            {
                // The original final call uses Single mode, so every ordinary loaded
                // scene is replaced. DontDestroyOnLoad's special scene is not in this list.
                var previous = new Scene[SceneManager.sceneCount];
                for (int i = 0; i < previous.Length; ++i) previous[i] = SceneManager.GetSceneAt(i);
                var empty = SceneManager.CreateScene("__AndroidTransition");
                SceneManager.SetActiveScene(empty);
                Debug.Log("AndroidSceneLoader: unloading old scenes before " + sceneName);
                for (int i = 0; i < previous.Length; ++i)
                {
                    if (!previous[i].IsValid() || !previous[i].isLoaded) continue;
                    var unload = SceneManager.UnloadSceneAsync(previous[i]);
                    if (unload == null)
                    {
                        Debug.LogError("AndroidSceneLoader: unload unavailable, using original scene load");
                        SceneManager.LoadScene(sceneName);
                        yield break;
                    }
                    yield return unload;
                }
                Debug.Log("AndroidSceneLoader: old scenes unloaded, releasing unused assets");
                yield return Resources.UnloadUnusedAssets();
                GC.Collect();
                yield return null;
                Debug.Log("AndroidSceneLoader: cleanup complete, loading " + sceneName);
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Single);
                // Release loading scratch data before the 3 GB device starts uploading the whole cafe.
                int memory=AndroidDevice.MemoryMB;
                if(memory>0 && memory<=3072)
                {
                    yield return Resources.UnloadUnusedAssets();
                    GC.Collect();
                    Debug.Log("AndroidSceneLoader: low-memory post-load cleanup managedMB="+GC.GetTotalMemory(false)/1048576);
                }
                Debug.Log("AndroidSceneLoader: completed " + sceneName);
            }
            finally
            {
                Application.backgroundLoadingPriority = previousPriority;
                loading = false;
            }
        }
    }
}
