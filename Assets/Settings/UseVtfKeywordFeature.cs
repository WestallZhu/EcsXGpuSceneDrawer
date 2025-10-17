using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Rendering.Universal.Internal;


public class UseVtfKeywordFeature : ScriptableRendererFeature
{
    [System.Serializable]
    public class Settings
    {
        public RenderPassEvent renderPassEvent = RenderPassEvent.BeforeRendering;
    }

    class ToggleKeywordPass : ScriptableRenderPass
    {
        private readonly Settings settings;
        private static readonly ProfilingSampler s_Sampler = new ProfilingSampler("UseVTF Keyword");


        static readonly GlobalKeyword kUseVtfKeyword = GlobalKeyword.Create("USE_VTF");

        public ToggleKeywordPass(Settings settings)
        {
            this.settings = settings;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var cmd = CommandBufferPool.Get("Toggle USE_VTF");
            using (new ProfilingScope(cmd, s_Sampler))
            {
#if UNITY_WEBGL && TUANJIE_1_6_OR_NEWER
                cmd.EnableKeyword(kUseVtfKeyword);
#else
                cmd.DisableKeyword(kUseVtfKeyword);
#endif
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }
    }

    public Settings settings = new Settings();
    ToggleKeywordPass m_Pass;

    public override void Create()
    {
        m_Pass = new ToggleKeywordPass(settings)
        {
            renderPassEvent = settings.renderPassEvent,
        };
    }

    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        renderer.EnqueuePass(m_Pass);
    }
}


