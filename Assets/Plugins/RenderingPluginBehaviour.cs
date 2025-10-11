using System.Collections;
using UnityEngine;

namespace Unity.Rendering
{
    /// <summary>
    /// Bootstraps the native rendering plugin and pumps a render event each frame.
    /// Attach automatically at load. Keep alive across scenes.
    /// </summary>
    public class RenderingPluginBehaviour : MonoBehaviour
    {
        private static bool sInit = false;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CreateRenderingPluginIfNeeded()
        {
            if (sInit) return;
            sInit = true;

            var go = new GameObject(nameof(RenderingPluginBehaviour));
            go.hideFlags = HideFlags.DontSave;
            go.AddComponent<RenderingPluginBehaviour>();
            DontDestroyOnLoad(go);
        }

        private IEnumerator Start()
        {
#if UNITY_WEBGL && !UNITY_EDITOR
            RenderingPluginAPI.RegisterPlugin();
#endif
            // Issue a render event at end-of-frame so the plugin can flush batched updates.
            yield return StartCoroutine("CallPluginAtEndOfFrames");
        }

        private IEnumerator CallPluginAtEndOfFrames()
        {
            while (true)
            {
                yield return new WaitForEndOfFrame();
                GL.IssuePluginEvent(RenderingPluginAPI.GetRenderEventFunc(), 3);
            }
        }
    }
}