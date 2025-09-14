using Unity.Collections;
using UnityEngine;
#if !UNITY_2022_2_OR_NEWER
using EntitiesGraphicsChunkInfo = Unity.Entities.HybridChunkInfo;
#else
using Unity.Rendering;
#endif

namespace Unity.Entities
{

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.EntitySceneOptimizations)]
    public partial class StructuralChangePresentationSystemGroup : ComponentSystemGroup
    {
        EntityCommandBuffer m_CommandBuffer;

        public EntityCommandBuffer GetUpdateCommands()
        {
            if (!m_CommandBuffer.IsCreated)
            {
                m_CommandBuffer = new EntityCommandBuffer(Allocator.TempJob);
            }
            return m_CommandBuffer;

        }

        protected override void OnDestroy()
        {
            if (m_CommandBuffer.IsCreated)
            {
                m_CommandBuffer.Dispose();
            }
            base.OnDestroy();
        }
        protected override void OnUpdate()
        {

            if (m_CommandBuffer.IsCreated)
            {
                try
                {
                    m_CommandBuffer.Playback(EntityManager);
                    m_CommandBuffer.Dispose();
                }
                finally
                {
                }
            }
            base.OnUpdate();
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(StructuralChangePresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.EntitySceneOptimizations)]
    public partial class UpdatePresentationSystemGroup : ComponentSystemGroup
    {
    }

    [UpdateInGroup(typeof(StructuralChangePresentationSystemGroup))]
    public partial class UpdateHybridChunksStructure : SystemBase
    {
        private EntityQuery m_MissingHybridChunkInfo;
        private EntityQuery m_DisabledRenderingQuery;
        protected override void OnCreate()
        {
            m_MissingHybridChunkInfo = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<RenderMesh>(),
                },

                None = new[]
                {
                    ComponentType.ChunkComponentReadOnly<EntitiesGraphicsChunkInfo>(),

                },

#if UNITY_2022_2_OR_NEWER
                Options = EntityQueryOptions.IncludeDisabledEntities | EntityQueryOptions.IncludePrefab,
#else
                Options = EntityQueryOptions.IncludeDisabled | EntityQueryOptions.IncludePrefab,
#endif
            });

        }

        protected override void OnUpdate()
        {
            UnityEngine.Profiling.Profiler.BeginSample("UpdateHybridChunksStructure");
            {
                EntityManager.AddComponent(m_MissingHybridChunkInfo, ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>());

            }
            UnityEngine.Profiling.Profiler.EndSample();
        }
    }

}