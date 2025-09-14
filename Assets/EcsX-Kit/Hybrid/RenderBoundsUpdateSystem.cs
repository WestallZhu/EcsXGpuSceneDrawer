using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unity.Burst;
using Unity.Burst.Intrinsics;

namespace Unity.Entities
{

    [UpdateInGroup(typeof(UpdatePresentationSystemGroup))]
    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.EntitySceneOptimizations)]
    partial class RenderBoundsUpdateSystem : SystemBase
    {
        EntityQuery m_WorldRenderBounds;

        [BurstCompile]
        struct BoundsJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<RenderBounds> RendererBounds;
            [ReadOnly] public ComponentTypeHandle<LocalToWorld> LocalToWorld;
            public ComponentTypeHandle<WorldRenderBounds> WorldRenderBounds;
            public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;

#if UNITY_2022_2_OR_NEWER
            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
#else
            public void Execute(ArchetypeChunk chunk, int chunkIndex, int firstEntityIndex)
#endif
            {
                var worldBounds = chunk.GetNativeArray(ref WorldRenderBounds);
                var localBounds = chunk.GetNativeArray(ref RendererBounds);
                var localToWorld = chunk.GetNativeArray(ref LocalToWorld);
                MinMaxAABB combined = MinMaxAABB.Empty;
                for (int i = 0; i != localBounds.Length; i++)
                {
                    var transformed = AABB.Transform(localToWorld[i].Value, localBounds[i].Value);

                    worldBounds[i] = new WorldRenderBounds { Value = transformed };
                    combined.Encapsulate(transformed);
                }

                chunk.SetChunkComponentData(ref ChunkWorldRenderBounds, new ChunkWorldRenderBounds { Value = combined });
            }
        }

        protected override void OnCreate()
        {
            m_WorldRenderBounds = GetEntityQuery
            (
            new EntityQueryDesc
            {
                All = new[] { ComponentType.ChunkComponent<ChunkWorldRenderBounds>(), ComponentType.ReadWrite<WorldRenderBounds>(), ComponentType.ReadOnly<RenderBounds>(), ComponentType.ReadOnly<LocalToWorld>() },
            }
            );
            m_WorldRenderBounds.SetChangedVersionFilter(new[] { ComponentType.ReadOnly<RenderBounds>(), ComponentType.ReadOnly<LocalToWorld>() });
            m_WorldRenderBounds.AddOrderVersionFilter();

        }

        protected override void OnUpdate()
        {
            var boundsJob = new BoundsJob
            {
                RendererBounds = GetComponentTypeHandle<RenderBounds>(true),
                LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true),
                WorldRenderBounds = GetComponentTypeHandle<WorldRenderBounds>(),
                ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(),
            };

            Dependency = boundsJob.ScheduleParallel(m_WorldRenderBounds, Dependency);

        }
    }
}
