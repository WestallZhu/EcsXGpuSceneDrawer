#if UNITY_2022_2_OR_NEWER

using System.Runtime.CompilerServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using Unity.Rendering;

namespace Unity.Entities
{

    public static class QuadTreeRuntime
    {
        public static NativeArray<QuadNode> Nodes;
        public static NativeArray<ArchetypeChunk> Chunks;
        public static int RootCount;
        public static int ViewLodIndex;
        public static bool IsReady =>
            Nodes.IsCreated && Chunks.IsCreated && RootCount > 0 && ViewLodIndex >= 0;
    }

    public struct QuadNode
    {
        public int FirstChild;
        public int FirstChunk;
        public int ChunkCount;
        public float3 Center;
        public float3 Extents;
        public int LodIndex;
    }

    [BurstCompile]
    public unsafe struct QuadTreeCullJob : IJob
    {
        [ReadOnly] public NativeArray<QuadNode> Nodes;
        [ReadOnly] public NativeArray<ArchetypeChunk> Chunks;
        public int RootCount;
        public int ViewLodIndex;

        [DeallocateOnJobCompletion]
        [ReadOnly] public NativeArray<FrustumPlanes.PlanePacket4> Packet4;

        public float AddExpand;
        public float RemoveShrink;

        public NativeBitArray VisibleFlags;
        public NativeList<ArchetypeChunk> ToShow;
        public NativeList<ArchetypeChunk> ToHide;

        public void Execute()
        {
            int* stack = stackalloc int[256];
            int sp = 0;
            for (int r = 0; r < RootCount; ++r) stack[sp++] = r;

            while (sp > 0)
            {
                int idx = stack[--sp];
                var n = Nodes[idx];

                if (!Intersect(n.Center, n.Extents))
                {
                    if (VisibleFlags.IsSet(idx)) HideNode(idx, n);
                    continue;
                }

                bool matchLOD = (n.LodIndex == ViewLodIndex);
                if (matchLOD)
                {
                    bool wasVisible = VisibleFlags.IsSet(idx);
                    bool insideAdd = Intersect(n.Center, n.Extents + AddExpand);
                    bool insideRemove = Intersect(n.Center, n.Extents + RemoveShrink);

                    if (!wasVisible && insideAdd) ShowNode(idx, n);
                    else if (wasVisible && !insideRemove) HideNode(idx, n);
                    continue;
                }

                if (VisibleFlags.IsSet(idx))
                {
                    if(!Intersect(n.Center, n.Extents + RemoveShrink))
                        HideNode(idx, n);
                }

                if (n.FirstChild >= 0)
                {
                    int c = n.FirstChild;
                    stack[sp++] = c + 3;
                    stack[sp++] = c + 2;
                    stack[sp++] = c + 1;
                    stack[sp++] = c + 0;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool Intersect(float3 c, float3 e)
            => FrustumPlanes.Intersect2(Packet4, new AABB { Center = c, Extents = e })
               != FrustumPlanes.IntersectResult.Out;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void ShowNode(int idx, in QuadNode n)
        {
            VisibleFlags.Set(idx, true);
            for (int i = 0; i < n.ChunkCount; ++i)
                ToShow.Add(Chunks[n.FirstChunk + i]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void HideNode(int idx, in QuadNode n)
        {
            VisibleFlags.Set(idx, false);
            for (int i = 0; i < n.ChunkCount; ++i)
                ToHide.Add(Chunks[n.FirstChunk + i]);
        }
    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct VegetationSectionSystem : ISystem
    {

        public float AddExpand;
        public float RemoveShrink;

        NativeList<ArchetypeChunk> _toShow, _toHide;
        NativeBitArray _visible;

        JobHandle _producerPrev;
        bool _hasPrevJob;
        bool _inited, _ensuredDisableTag;

        public void OnCreate(ref SystemState s)
        {
            AddExpand = 16f;
            RemoveShrink = 32f;

            _toShow = new NativeList<ArchetypeChunk>(128, Allocator.Persistent);
            _toHide = new NativeList<ArchetypeChunk>(128, Allocator.Persistent);

            _producerPrev = default;
            _hasPrevJob = false;

            _inited = false;
            _ensuredDisableTag = false;
        }

        public void OnDestroy(ref SystemState s)
        {
            _producerPrev.Complete();
            if (_visible.IsCreated) _visible.Dispose();
            if (_toShow.IsCreated) _toShow.Dispose();
            if (_toHide.IsCreated) _toHide.Dispose();
        }

        public void ProducerComplete()
        {
            _producerPrev.Complete();
        }
        void EnsureInit()
        {
            if (_inited || !QuadTreeRuntime.IsReady) return;
            if (_visible.IsCreated) _visible.Dispose();
            _visible = new NativeBitArray(QuadTreeRuntime.Nodes.Length, Allocator.Persistent);
            _inited = true;
        }
        static readonly Plane[] s_Planes6 = new Plane[6];
        public void OnUpdate(ref SystemState s)
        {
            if (!QuadTreeRuntime.IsReady) return;
            EnsureInit();

            var cam = Camera.main;
            if (cam == null) return;

            _producerPrev.Complete();

            if (_hasPrevJob)
            {

                s.EntityManager.AddChunkComponentData<EntitiesGraphicsChunkInfo>(_toShow.AsArray(), new EntitiesGraphicsChunkInfo());
                s.EntityManager.RemoveChunkComponentData<EntitiesGraphicsChunkInfo>(_toHide.AsArray());
               _toShow.Clear();
                _toHide.Clear();
                _hasPrevJob = false;
            }

            GeometryUtility.CalculateFrustumPlanes(cam, s_Planes6);
            var planes6 = new NativeArray<Plane>(s_Planes6, Allocator.Temp);
            var planes4 = planes6.GetSubArray(0, 4);
            var packet4 = FrustumPlanes.BuildSOAPlanePackets(planes4, Allocator.TempJob);
            planes6.Dispose();

            var job = new QuadTreeCullJob
            {
                Nodes = QuadTreeRuntime.Nodes,
                Chunks = QuadTreeRuntime.Chunks,
                RootCount = QuadTreeRuntime.RootCount,
                ViewLodIndex = QuadTreeRuntime.ViewLodIndex,

                Packet4 = packet4,
                AddExpand = AddExpand,
                RemoveShrink = RemoveShrink,

                VisibleFlags = _visible,
                ToShow = _toShow,
                ToHide = _toHide
            };

            _producerPrev = job.Schedule(s.Dependency);
            _hasPrevJob = true;

        }
    }

    public struct VegetationCleanupRequestTag : IComponentData
    {

    }

    [DisableAutoCreation]
    [BurstCompile]
    public partial struct VegetationCleanupSystem : ISystem
    {

        public void OnCreate(ref SystemState state)
        {

            _req = state.GetEntityQuery(ComponentType.ReadOnly<VegetationCleanupRequestTag>());
            state.RequireForUpdate(_req);

            _victims = state.GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<ChunkSection>(),
                    ComponentType.ChunkComponent<ChunkSimpleLOD>()
                },
                Options = EntityQueryOptions.IncludePrefab
            });
        }

        public void OnUpdate(ref SystemState state)
        {

            if (!_victims.IsEmpty)
                state.EntityManager.DestroyEntity(_victims);

            state.EntityManager.DestroyEntity(_req);

        }

        EntityQuery _req;
        EntityQuery _victims;
    }

}

#endif
