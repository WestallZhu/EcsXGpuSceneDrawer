using System;
using System.Runtime.InteropServices;

namespace Unity.Rendering
{
    /// <summary>
    /// Thin wrapper for native rendering plugin functions.
    /// </summary>
    public static class RenderingPluginAPI
    {
#if (UNITY_WEBGL || PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
        [DllImport("__Internal")]
#else
        [DllImport("RenderingPlugin")]
#endif
        public static extern void UpdateTexture2DSub(
            IntPtr texture,
            int xoffset,
            int yoffset,
            int width,
            int height,
            int pixelsByte,
            IntPtr data);

#if (UNITY_WEBGL || PLATFORM_IOS || PLATFORM_TVOS || PLATFORM_BRATWURST || PLATFORM_SWITCH) && !UNITY_EDITOR
        [DllImport("__Internal")]
#else
        [DllImport("RenderingPlugin")]
#endif
        public static extern IntPtr GetRenderEventFunc();

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        public static extern void RegisterPlugin();
#endif
    }
}