#if UNITY_EDITOR
using UnityEditor;
#endif

using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_2022_2_OR_NEWER

namespace Unity.Rendering
{
    [BurstCompile]
    internal unsafe struct SelectLodEnabled : IJobChunk
    {
        [ReadOnly] public LODGroupExtensions.LODParams LODParams;
        [ReadOnly] public NativeList<byte> ForceLowLOD;
        [ReadOnly] public ComponentTypeHandle<RootLODRange> RootLODRanges;
        [ReadOnly] public ComponentTypeHandle<RootLODWorldReferencePoint> RootLODReferencePoints;
        [ReadOnly] public ComponentTypeHandle<LODRange> LODRanges;
        [ReadOnly] public ComponentTypeHandle<LODWorldReferencePoint> LODReferencePoints;
        [ReadOnly] public ComponentTypeHandle<ChunkSimpleLOD> ChunkSimpleLODs;
        public ushort CameraMoveDistanceFixed16;
        public float DistanceScale;
        public bool DistanceScaleChanged;
        public int MaximumLODLevelMask;
        public int SimpleChunkLOD;

        public ComponentTypeHandle<EntitiesGraphicsChunkInfo> EntitiesGraphicsChunkInfo;
        [ReadOnly] public ComponentTypeHandle<ChunkHeader> ChunkHeader;

#if UNITY_EDITOR
        [NativeDisableUnsafePtrRestriction] public EntitiesGraphicsPerThreadStats* Stats;

#pragma warning disable 649
        [NativeSetThreadIndex] public int ThreadIndex;
#pragma warning restore 649

#endif

        public void Execute(in ArchetypeChunk archetypeChunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
        {

            Assert.IsFalse(useEnabledMask);

            var entitiesGraphicsChunkInfoArray = archetypeChunk.GetNativeArray(ref EntitiesGraphicsChunkInfo);
            var chunkHeaderArray = archetypeChunk.GetNativeArray(ref ChunkHeader);
#if UNITY_EDITOR
            ref var stats = ref Stats[ThreadIndex];
#endif

            for (int entityIndex = 0, chunkEntityCount = archetypeChunk.Count; entityIndex < chunkEntityCount; entityIndex++)
            {
                var entitiesGraphicsChunkInfo = entitiesGraphicsChunkInfoArray[entityIndex];
                if (!entitiesGraphicsChunkInfo.Valid)
                    continue;

                var chunkHeader = chunkHeaderArray[entityIndex];

#if UNITY_EDITOR
                stats.LodTotal++;
#endif
                var batchIndex = entitiesGraphicsChunkInfo.BatchIndex;
                var chunkInstanceCount = chunkHeader.ArchetypeChunk.Count;
                var isOrtho = LODParams.isOrtho;

                ref var chunkCullingData = ref entitiesGraphicsChunkInfo.CullingData;
                ChunkInstanceLodEnabled chunkEntityLodEnabled = chunkCullingData.InstanceLodEnableds;

#if UNITY_EDITOR
                ChunkInstanceLodEnabled oldEntityLodEnabled = chunkEntityLodEnabled;
#endif
                var forceLowLOD = ForceLowLOD[batchIndex];

                if (0 == (chunkCullingData.Flags & EntitiesGraphicsChunkCullingData.kFlagHasLodData))
                {
#if UNITY_EDITOR
                    stats.LodNoRequirements++;
#endif
                    chunkEntityLodEnabled.Enabled[0] = 0;
                    chunkEntityLodEnabled.Enabled[1] = 0;
                    chunkCullingData.ForceLowLODPrevious = forceLowLOD;

                    bool skipChunk = false;
                    if (0 != (chunkCullingData.Flags & EntitiesGraphicsChunkCullingData.kFlagHasChunkLodData))
                    {
                        var chunkSimpleLod = chunkHeader.ArchetypeChunk.GetChunkComponentData<ChunkSimpleLOD>(ref ChunkSimpleLODs);
                        skipChunk = chunkSimpleLod.Value != SimpleChunkLOD;
                    }
                    if(!skipChunk)
                    {

                        if (chunkInstanceCount < 64)
                        {

                            chunkEntityLodEnabled.Enabled[0] = (1UL << chunkInstanceCount) - 1;
                            chunkEntityLodEnabled.Enabled[1] = 0;
                        }
                        else
                        {

                            chunkEntityLodEnabled.Enabled[0] = ~0UL;

                            int bitsInSecondUlong = chunkInstanceCount - 64;

                            if (bitsInSecondUlong >= 64)
                            {
                                chunkEntityLodEnabled.Enabled[1] = ~0UL;
                            }
                            else
                            {
                                chunkEntityLodEnabled.Enabled[1] = (1UL << bitsInSecondUlong) - 1;
                            }
                        }

                    }

                }
                else
                {
                    int diff = (int)chunkCullingData.MovementGraceFixed16 - CameraMoveDistanceFixed16;
                    chunkCullingData.MovementGraceFixed16 = (ushort)math.max(0, diff);

                    chunkEntityLodEnabled.Enabled[0] = 0;
                    chunkEntityLodEnabled.Enabled[1] = 0;

#if UNITY_EDITOR
                    stats.LodChunksTested++;
#endif
                    var chunk = chunkHeader.ArchetypeChunk;

                    var rootLODRanges = chunk.GetNativeArray(ref RootLODRanges);
                    var rootLODReferencePoints = chunk.GetNativeArray(ref RootLODReferencePoints);
                    var lodRanges = chunk.GetNativeArray(ref LODRanges);
                    var lodReferencePoints = chunk.GetNativeArray(ref LODReferencePoints);

                    float graceDistance = float.MaxValue;

                    for (int i = 0; i < chunkInstanceCount; i++)
                    {
                        var rootLODRange = rootLODRanges[i];
                        var rootLODReferencePoint = rootLODReferencePoints[i];

                        var rootLodDistance =
                            math.select(
                                DistanceScale *
                                math.length(LODParams.cameraPos - rootLODReferencePoint.Value),
                                DistanceScale, isOrtho);

                        float rootMinDist = math.select(rootLODRange.LOD.MinDist, 0.0f, forceLowLOD == 1);
                        float rootMaxDist = rootLODRange.LOD.MaxDist;

                        graceDistance = math.min(math.abs(rootLodDistance - rootMinDist), graceDistance);
                        graceDistance = math.min(math.abs(rootLodDistance - rootMaxDist), graceDistance);

                        var rootLodIntersect = (rootLodDistance < rootMaxDist) && (rootLodDistance >= rootMinDist);

                        if (rootLodIntersect)
                        {
                            var lodRange = lodRanges[i];
                            if (lodRange.LODMask < MaximumLODLevelMask)
                            {
                                continue;
                            }
                            if (lodRange.LODMask == MaximumLODLevelMask)
                            {

                                lodRange.MinDist = 0.0f;
                            }
                            var lodReferencePoint = lodReferencePoints[i];

                            var instanceDistance =
                                math.select(
                                    DistanceScale *
                                    math.length(LODParams.cameraPos - lodReferencePoint.Value), DistanceScale,
                                        isOrtho);

                            var instanceLodIntersect =
                                (instanceDistance < lodRange.MaxDist) &&
                                (instanceDistance >= lodRange.MinDist);

                            graceDistance = math.min(math.abs(instanceDistance - lodRange.MinDist),
                                graceDistance);
                            graceDistance = math.min(math.abs(instanceDistance - lodRange.MaxDist),
                                graceDistance);

                            if (instanceLodIntersect)
                            {
                                var index = i;
                                var wordIndex = index >> 6;
                                var bitIndex = index & 0x3f;
                                var lodWord = chunkEntityLodEnabled.Enabled[wordIndex];

                                lodWord |= 1UL << bitIndex;
                                chunkEntityLodEnabled.Enabled[wordIndex] = lodWord;
                            }
                        }
                    }

                    chunkCullingData.MovementGraceFixed16 = Fixed16CamDistance.FromFloatFloor(graceDistance);
                    chunkCullingData.ForceLowLODPrevious = forceLowLOD;

                }

#if UNITY_EDITOR
                if (oldEntityLodEnabled.Enabled[0] != chunkEntityLodEnabled.Enabled[0] ||
                    oldEntityLodEnabled.Enabled[1] != chunkEntityLodEnabled.Enabled[1])
                {
                    stats.LodChanged++;
                }
#endif

                chunkCullingData.InstanceLodEnableds = chunkEntityLodEnabled;
                entitiesGraphicsChunkInfoArray[entityIndex] = entitiesGraphicsChunkInfo;
            }
        }
    }

    [BurstCompile]
    public unsafe struct PlanarShadowSelect : IJobChunk
    {
        [ReadOnly] public ComponentTypeHandle<WorldRenderBounds> BoundsComponent;
        [ReadOnly] public float maxDistSq;
        [ReadOnly] public float3 cameraPos;
        public ComponentTypeHandle<EntitiesGraphicsChunkInfo> EntitiesGraphicsChunkInfo;
        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 enabledMaskIn)
        {
            var enabledMask128 = useEnabledMask ? enabledMaskIn : EntitiesGraphicsUtils.ComputeBitmask(chunk.Count);

            var entitiesGraphicsChunkInfo = chunk.GetChunkComponentData(ref EntitiesGraphicsChunkInfo);
            if (!entitiesGraphicsChunkInfo.Valid)
                return;

            var chunkInstanceBounds = chunk.GetComponentDataPtrRO(ref BoundsComponent);

            ref var chunkCullingData = ref entitiesGraphicsChunkInfo.CullingData;
            ref ChunkInstanceLodEnabled chunkEntityLodEnabled = ref chunkCullingData.InstanceLodEnableds;

            for (int j = 0; j < 2; j++)
            {
                var enabledMask = j == 0 ? enabledMask128.ULong0 : enabledMask128.ULong1;
                var lodWord = chunkEntityLodEnabled.Enabled[j] & enabledMask;
                ulong noVisibleWord = 0;

                while (lodWord != 0)
                {
                    var bitIndex = math.tzcnt(lodWord);
                    var finalIndex = (j << 6) + bitIndex;

                    int noVisible = math.distancesq(chunkInstanceBounds[finalIndex].Value.Center, cameraPos) > maxDistSq ? 1 : 0;

                    lodWord ^= 1ul << bitIndex;
                    noVisibleWord |= (ulong)noVisible << bitIndex;

                }
                chunkEntityLodEnabled.Enabled[j] &= ~noVisibleWord;
            }
            chunk.SetChunkComponentData(ref EntitiesGraphicsChunkInfo, entitiesGraphicsChunkInfo);
        }
    }

    internal unsafe struct ChunkVisibility
    {
        public fixed ulong VisibleEntities[2];
        public fixed byte SplitMasks[128];

        public bool AnyVisible => (VisibleEntities[0] | VisibleEntities[1]) != 0;
    }

    internal unsafe struct ChunkVisibilityItem
    {
        public ArchetypeChunk Chunk;
        public ChunkVisibility* Visibility;
    }

    internal static class CullingExtensions
    {

        internal static unsafe NativeArray<T> AsNativeArray<T>(this UnsafeList<T> list) where T : unmanaged
        {
            var array = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<T>(list.Ptr, list.Length, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref array, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            return array;
        }

        internal static NativeArray<T> GetSubNativeArray<T>(this UnsafeList<T> list, int start, int length)
            where T : unmanaged =>
            list.AsNativeArray().GetSubArray(start, length);
    }

    internal unsafe struct CullingSplits
    {
        public UnsafeList<Plane> BackfacingReceiverPlanes;
        public UnsafeList<FrustumPlanes.PlanePacket4> SplitPlanePackets;
        public UnsafeList<FrustumPlanes.PlanePacket4> ReceiverPlanePackets;
        public UnsafeList<FrustumPlanes.PlanePacket4> CombinedSplitAndReceiverPlanePackets;
        public UnsafeList<CullingSplitData> Splits;
        public ReceiverSphereCuller ReceiverSphereCuller;
        public bool SphereTestEnabled;

        public static CullingSplits Create(BatchCullingContext* cullingContext, ShadowProjection shadowProjection, AllocatorManager.AllocatorHandle allocator)
        {
            CullingSplits cullingSplits = default;

            var createJob = new CreateJob
            {
                cullingContext = cullingContext,
                shadowProjection = shadowProjection,
                allocator = allocator,
                Splits = &cullingSplits
            };
            createJob.Run();

            return cullingSplits;
        }

        [BurstCompile]
        private struct CreateJob : IJob
        {
            [NativeDisableUnsafePtrRestriction]
            [ReadOnly] public BatchCullingContext* cullingContext;
            [ReadOnly] public ShadowProjection shadowProjection;
            [ReadOnly] public AllocatorManager.AllocatorHandle allocator;

            [NativeDisableUnsafePtrRestriction]
            public CullingSplits* Splits;

            public void Execute()
            {
                *Splits = new CullingSplits(ref *cullingContext, shadowProjection, allocator);
            }
        }

        private CullingSplits(ref BatchCullingContext cullingContext,
            ShadowProjection shadowProjection,
            AllocatorManager.AllocatorHandle allocator)
        {
            BackfacingReceiverPlanes = default;
            SplitPlanePackets = default;
            ReceiverPlanePackets = default;
            CombinedSplitAndReceiverPlanePackets = default;
            Splits = default;
            ReceiverSphereCuller = default;
            SphereTestEnabled = false;

            InitializeReceiverPlanes(ref cullingContext, allocator);
            InitializeSplits(ref cullingContext, allocator);
            InitializeSphereTest(ref cullingContext, shadowProjection);
        }

        private void InitializeReceiverPlanes(ref BatchCullingContext cullingContext, AllocatorManager.AllocatorHandle allocator)
        {
#if DISABLE_HYBRID_RECEIVER_CULLING
            bool disableReceiverCulling = true;
#else
            bool disableReceiverCulling = false;
#endif

            if ((cullingContext.viewType != BatchCullingViewType.Light) ||
                (cullingContext.receiverPlaneCount == 0) ||
                disableReceiverCulling)
            {

                ReceiverPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(0, allocator);
                return;
            }

            bool isOrthographic = cullingContext.projectionType == BatchCullingProjectionType.Orthographic;
            int numPlanes = 0;

            var planes = cullingContext.cullingPlanes.GetSubArray(
                cullingContext.receiverPlaneOffset,
                cullingContext.receiverPlaneCount);
            BackfacingReceiverPlanes = new UnsafeList<Plane>(planes.Length, allocator);
            BackfacingReceiverPlanes.Resize(planes.Length);

            float3 lightDir = ((float4)cullingContext.localToWorldMatrix.GetColumn(2)).xyz;
            Vector3 lightPos = cullingContext.localToWorldMatrix.GetPosition();

            for (int i = 0; i < planes.Length; ++i)
            {
                var p = planes[i];
                float3 n = p.normal;

                const float kEpsilon = (float)1e-12;

                bool isBackfacing = isOrthographic
                    ? math.dot(n, lightDir) < -kEpsilon
                    : p.GetSide(lightPos);

                if (isBackfacing)
                {
                    BackfacingReceiverPlanes[numPlanes] = p;
                    ++numPlanes;
                }
            }

            ReceiverPlanePackets = FrustumPlanes.BuildSOAPlanePackets(
                BackfacingReceiverPlanes.GetSubNativeArray(0, numPlanes),
                allocator);
            BackfacingReceiverPlanes.Resize(numPlanes);
        }

#if DEBUG_VALIDATE_EXTRA_SPLITS
        private static int s_DebugExtraSplitsCounter = 0;
#endif

        private void InitializeSplits(ref BatchCullingContext cullingContext, AllocatorManager.AllocatorHandle allocator)
        {
            var cullingPlanes = cullingContext.cullingPlanes;
            var cullingSplits = cullingContext.cullingSplits;

            int numSplits = cullingSplits.Length;

#if DEBUG_VALIDATE_EXTRA_SPLITS

            if (numSplits > 1 && numSplits < 5)
            {
                numSplits = 5 + s_DebugExtraSplitsCounter;
                s_DebugExtraSplitsCounter = (s_DebugExtraSplitsCounter + 1) % 4;
            }
#endif

            Assert.IsTrue(numSplits > 0, "No culling splits provided, expected at least 1");
            Assert.IsTrue(numSplits <= 8, "Split count too high, only up to 8 splits supported");

            int planePacketCount = 0;
            int combinedPlanePacketCount = 0;
            for (int i = 0; i < numSplits; ++i)
            {
                int splitIndex = i;
#if DEBUG_VALIDATE_EXTRA_SPLITS
                splitIndex %= cullingSplits.Length;
#endif

                planePacketCount += (cullingSplits[splitIndex].cullingPlaneCount + 3) / 4;
                combinedPlanePacketCount +=
                    ((cullingSplits[splitIndex].cullingPlaneCount + BackfacingReceiverPlanes.Length) + 3) / 4;
            }

            SplitPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(planePacketCount, allocator);
            CombinedSplitAndReceiverPlanePackets = new UnsafeList<FrustumPlanes.PlanePacket4>(combinedPlanePacketCount, allocator);
            Splits = new UnsafeList<CullingSplitData>(numSplits, allocator);

            var combinedPlanes = new UnsafeList<Plane>(combinedPlanePacketCount * 4, allocator);

            int planeIndex = 0;
            int combinedPlaneIndex = 0;

            for (int i = 0; i < numSplits; ++i)
            {
                int splitIndex = i;
#if DEBUG_VALIDATE_EXTRA_SPLITS
                splitIndex %= cullingSplits.Length;
#endif

                var s = cullingSplits[splitIndex];
                float3 p = s.sphereCenter;
                float r = s.sphereRadius;

                if (s.sphereRadius <= 0)
                    r = 0;

                var splitCullingPlanes = cullingPlanes.GetSubArray(s.cullingPlaneOffset, s.cullingPlaneCount);

                var planePackets = FrustumPlanes.BuildSOAPlanePackets(
                    splitCullingPlanes,
                    allocator);

                foreach (var pp in planePackets)
                    SplitPlanePackets.Add(pp);

                combinedPlanes.Resize(splitCullingPlanes.Length + BackfacingReceiverPlanes.Length);

                UnsafeUtility.MemCpy(
                    combinedPlanes.Ptr,
                    splitCullingPlanes.GetUnsafeReadOnlyPtr(),
                    splitCullingPlanes.Length * UnsafeUtility.SizeOf<Plane>());
                UnsafeUtility.MemCpy(
                    combinedPlanes.Ptr + splitCullingPlanes.Length,
                    BackfacingReceiverPlanes.Ptr,
                    BackfacingReceiverPlanes.Length * UnsafeUtility.SizeOf<Plane>());

                var combined = FrustumPlanes.BuildSOAPlanePackets(
                    combinedPlanes.AsNativeArray(),
                    allocator);

                foreach (var pp in combined)
                    CombinedSplitAndReceiverPlanePackets.Add(pp);

                Splits.Add(new CullingSplitData
                {
                    CullingSphereCenter = p,
                    CullingSphereRadius = r,
                    ShadowCascadeBlendCullingFactor = s.cascadeBlendCullingFactor,
                    PlanePacketOffset = planeIndex,
                    PlanePacketCount = planePackets.Length,
                    CombinedPlanePacketOffset = combinedPlaneIndex,
                    CombinedPlanePacketCount = combined.Length,
                });

                planeIndex += planePackets.Length;
                combinedPlaneIndex += combined.Length;
            }
        }

        private void InitializeSphereTest(ref BatchCullingContext cullingContext, ShadowProjection shadowProjection)
        {

            bool projectionIsStable = shadowProjection == ShadowProjection.StableFit;
            bool allSplitsHaveValidReceiverSpheres = true;
            for (int i = 0; i < Splits.Length; ++i)
            {

                if (!(Splits[i].CullingSphereRadius > 0))
                {
                    allSplitsHaveValidReceiverSpheres = false;
                    break;
                }
            }

            if (projectionIsStable && allSplitsHaveValidReceiverSpheres)
            {
                ReceiverSphereCuller = new ReceiverSphereCuller(cullingContext, this);
                SphereTestEnabled = true;
            }
        }
    }

    internal unsafe struct CullingSplitData
    {
        public float3 CullingSphereCenter;
        public float CullingSphereRadius;
        public float ShadowCascadeBlendCullingFactor;
        public int PlanePacketOffset;
        public int PlanePacketCount;
        public int CombinedPlanePacketOffset;
        public int CombinedPlanePacketCount;
    }

    [BurstCompile]
    internal unsafe struct IncludeExcludeListFilter
    {
#if !DISABLE_INCLUDE_EXCLUDE_LIST_FILTERING
        public NativeParallelHashSet<int> IncludeEntityIndices;
        public NativeParallelHashSet<int> ExcludeEntityIndices;
        public bool IsIncludeEnabled;
        public bool IsExcludeEnabled;

        public bool IsEnabled => IsIncludeEnabled || IsExcludeEnabled;
        public bool IsIncludeEmpty => IncludeEntityIndices.IsEmpty;
        public bool IsExcludeEmpty => ExcludeEntityIndices.IsEmpty;

        public IncludeExcludeListFilter(
            EntityManager entityManager,
            NativeArray<int> includeEntityIndices,
            NativeArray<int> excludeEntityIndices,
            Allocator allocator)
        {
            IncludeEntityIndices = default;
            ExcludeEntityIndices = default;

            IsIncludeEnabled = includeEntityIndices.IsCreated;
            IsExcludeEnabled = excludeEntityIndices.IsCreated;

            if (IsIncludeEnabled)
            {
                IncludeEntityIndices = new NativeParallelHashSet<int>(includeEntityIndices.Length, allocator);
                for (int i = 0; i < includeEntityIndices.Length; ++i)
                    IncludeEntityIndices.Add(includeEntityIndices[i]);
            }
            else
            {

                IncludeEntityIndices = new NativeParallelHashSet<int>(0, allocator);
            }

            if (IsExcludeEnabled)
            {
                ExcludeEntityIndices = new NativeParallelHashSet<int>(excludeEntityIndices.Length, allocator);
                for (int i = 0; i < excludeEntityIndices.Length; ++i)
                    ExcludeEntityIndices.Add(excludeEntityIndices[i]);
            }
            else
            {

                ExcludeEntityIndices = new NativeParallelHashSet<int>(0, allocator);
            }
        }

        public void Dispose()
        {
            if (IncludeEntityIndices.IsCreated)
                IncludeEntityIndices.Dispose();

            if (ExcludeEntityIndices.IsCreated)
                ExcludeEntityIndices.Dispose();
        }

        public JobHandle Dispose(JobHandle dependencies)
        {
            JobHandle disposeInclude = IncludeEntityIndices.IsCreated ? IncludeEntityIndices.Dispose(dependencies) : default;
            JobHandle disposeExclude = ExcludeEntityIndices.IsCreated ? ExcludeEntityIndices.Dispose(dependencies) : default;
            return JobHandle.CombineDependencies(disposeInclude, disposeExclude);
        }

        public bool EntityPassesFilter(int entityIndex)
        {
            if (IsIncludeEnabled)
            {
                if (!IncludeEntityIndices.Contains(entityIndex))
                    return false;
            }

            if (IsExcludeEnabled)
            {
                if (ExcludeEntityIndices.Contains(entityIndex))
                    return false;
            }

            return true;
        }
#else
        public bool IsIncludeEnabled => false;
        public bool IsExcludeEnabled => false;
        public bool IsEnabled => false;
        public bool IsIncludeEmpty => true;
        public bool IsExcludeEmpty => true;
        public bool EntityPassesFilter(int entityIndex) => true;
        public void Dispose() { }
        public JobHandle Dispose(JobHandle dependencies) => new JobHandle();
#endif
    }

    [BurstCompile]
    internal unsafe struct FrustumCullingJob : IJobChunk
    {
        public IndirectList<ChunkVisibilityItem> VisibilityItems;
        public ThreadLocalAllocator ThreadLocalAllocator;

        [ReadOnly] public CullingSplits Splits;

        [ReadOnly] public ComponentTypeHandle<WorldRenderBounds> BoundsComponent;
        [ReadOnly] public ComponentTypeHandle<EntitiesGraphicsChunkInfo> EntitiesGraphicsChunkInfo;
        [ReadOnly] public ComponentTypeHandle<ChunkWorldRenderBounds> ChunkWorldRenderBounds;

        [ReadOnly] public IncludeExcludeListFilter IncludeExcludeListFilter;
        [ReadOnly] public EntityTypeHandle EntityHandle;

        public BatchCullingViewType CullingViewType;

        public bool CullLightmapShadowCasters;

#pragma warning disable 649
        [NativeSetThreadIndex] public int ThreadIndex;
#pragma warning restore 649

#if UNITY_EDITOR
        [NativeDisableUnsafePtrRestriction] public EntitiesGraphicsPerThreadStats* Stats;
#endif

        public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 enabledMaskIn)
        {
            var enabledMask = useEnabledMask ? enabledMaskIn : EntitiesGraphicsUtils.ComputeBitmask(chunk.Count);

            var allocator = ThreadLocalAllocator.ThreadAllocator(ThreadIndex);
            var visibilityItemWriter = VisibilityItems.List->AsParallelWriter();
            ChunkVisibility chunkVisibility;

            bool isLightCulling = CullingViewType == BatchCullingViewType.Light;

            var entitiesGraphicsChunkInfo = chunk.GetChunkComponentData(ref EntitiesGraphicsChunkInfo);
            if (!entitiesGraphicsChunkInfo.Valid)
                return;

            var chunkBounds = chunk.GetChunkComponentData(ref ChunkWorldRenderBounds);

#if UNITY_EDITOR
            ref var stats = ref Stats[ThreadIndex];
            stats.ChunkTotal++;
#else
            var stats = new EntitiesGraphicsPerThreadStats{};
#endif

            ref var chunkCullingData = ref entitiesGraphicsChunkInfo.CullingData;
            var chunkEntityLodEnabled = chunkCullingData.InstanceLodEnableds;
            var anyLodEnabled = (chunkEntityLodEnabled.Enabled[0] | chunkEntityLodEnabled.Enabled[1]) != 0;

            chunkVisibility.VisibleEntities[0] = 0;
            chunkVisibility.VisibleEntities[1] = 0;

            var perInstanceCull = 0 != (chunkCullingData.Flags & EntitiesGraphicsChunkCullingData.kFlagInstanceCulling);

            if (anyLodEnabled)
            {
                stats.ChunkCountAnyLod++;
                if (isLightCulling)
                {
                    bool useSphereTest = Splits.SphereTestEnabled;
#if DISABLE_HYBRID_SPHERE_CULLING
                    useSphereTest = false;
#endif
                    FrustumCullWithReceiverAndSphereCulling(chunkBounds, chunk, enabledMask, chunkEntityLodEnabled,
                        perInstanceCull, &chunkVisibility, ref stats,
                        useSphereTest: useSphereTest);
                }
                else
                    FrustumCull(chunkBounds, chunk, enabledMask, chunkEntityLodEnabled, perInstanceCull, &chunkVisibility, ref stats);

                if (chunkVisibility.AnyVisible)
                {
                    var visibilityItem = new ChunkVisibilityItem
                    {
                        Chunk = chunk,
                        Visibility = allocator->Allocate(chunkVisibility, 1),
                    };
                    UnsafeUtility.MemCpy(visibilityItem.Visibility, &chunkVisibility, UnsafeUtility.SizeOf<ChunkVisibility>());
                    visibilityItemWriter.AddNoResize(visibilityItem);
                }
            }
        }

        private void FrustumCull(ChunkWorldRenderBounds chunkBounds,
            ArchetypeChunk chunk,
            v128 enabledMask128,
            ChunkInstanceLodEnabled chunkEntityLodEnabled,
            bool perInstanceCull,
            ChunkVisibility* chunkVisibility,
            ref EntitiesGraphicsPerThreadStats stats)
        {
            Assert.IsTrue(Splits.Splits.Length == 1);

            var chunkIn = perInstanceCull
                ? FrustumPlanes.Intersect2(Splits.SplitPlanePackets.AsNativeArray(), chunkBounds.Value)
                : FrustumPlanes.Intersect2NoPartial(Splits.SplitPlanePackets.AsNativeArray(), chunkBounds.Value);

            if (IncludeExcludeListFilter.IsEnabled && chunkIn == FrustumPlanes.IntersectResult.In)
                chunkIn = FrustumPlanes.IntersectResult.Partial;

            if (chunkIn == FrustumPlanes.IntersectResult.Partial)
            {
#if UNITY_EDITOR
                int instanceTestCount = 0;
#endif
                var chunkInstanceBounds = chunk.GetNativeArray(ref BoundsComponent);
                var chunkEntities = chunk.GetNativeArray(EntityHandle);

                for (int j = 0; j < 2; j++)
                {
                    var enabledMask = j == 0 ? enabledMask128.ULong0 : enabledMask128.ULong1;
                    var lodWord = chunkEntityLodEnabled.Enabled[j] & enabledMask;
                    ulong visibleWord = 0;

                    while (lodWord != 0)
                    {
                        var bitIndex = math.tzcnt(lodWord);
                        var finalIndex = (j << 6) + bitIndex;

                        int visible = FrustumPlanes.Intersect2NoPartial(Splits.SplitPlanePackets.AsNativeArray(), chunkInstanceBounds[finalIndex].Value) !=
                                      FrustumPlanes.IntersectResult.Out
                            ? 1
                            : 0;

                        if (IncludeExcludeListFilter.IsEnabled && visible != 0)
                        {
                            if (!IncludeExcludeListFilter.EntityPassesFilter(chunkEntities[finalIndex].Index))
                                visible = 0;
                        }

                        lodWord ^= 1ul << bitIndex;
                        visibleWord |= (ulong)visible << bitIndex;

#if UNITY_EDITOR
                        instanceTestCount++;
#endif
                    }

                    chunkVisibility->VisibleEntities[j] = visibleWord;
                }

#if UNITY_EDITOR
                stats.ChunkCountInstancesProcessed++;
                stats.InstanceTests += instanceTestCount;
#endif
            }
            else if (chunkIn == FrustumPlanes.IntersectResult.In)
            {
#if UNITY_EDITOR
                stats.ChunkCountFullyIn++;
#endif
                for (int j = 0; j < 2; j++)
                {
                    var enabledMask = j == 0 ? enabledMask128.ULong0 : enabledMask128.ULong1;
                    var lodWord = chunkEntityLodEnabled.Enabled[j] & enabledMask;
                    chunkVisibility->VisibleEntities[j] = lodWord;
                }
            }
            else if (chunkIn == FrustumPlanes.IntersectResult.Out)
            {

            }
        }

        private void FrustumCullWithReceiverAndSphereCulling(
            ChunkWorldRenderBounds chunkBounds,
            ArchetypeChunk chunk,
            v128 enabledMask128,
            ChunkInstanceLodEnabled chunkEntityLodEnabled,
            bool perInstanceCull,
            ChunkVisibility* chunkVisibility,
            ref EntitiesGraphicsPerThreadStats stats,
            bool useSphereTest)
        {
            int numEntities = chunk.Count;

            ref var receiverPlanes = ref Splits.ReceiverPlanePackets;

            bool haveReceiverPlanes = Splits.ReceiverPlanePackets.Length > 0;

            if (haveReceiverPlanes)
            {
                if (FrustumPlanes.Intersect2NoPartial(receiverPlanes.AsNativeArray(), chunkBounds.Value) ==
                    FrustumPlanes.IntersectResult.Out)
                    return;
            }

            UnsafeUtility.MemSet(chunkVisibility->SplitMasks, 0, numEntities);

            ref var splits = ref Splits.Splits;

            var worldRenderBounds = chunk.GetNativeArray(ref BoundsComponent);

            int visibleSplitMask = ~0;
            if (useSphereTest)
                visibleSplitMask = Splits.ReceiverSphereCuller.Cull(chunkBounds.Value);

            for (int splitIndex = 0; splitIndex < splits.Length; ++splitIndex)
            {
                var s = splits[splitIndex];

                byte splitMask = (byte)(1 << splitIndex);

                var splitPlanes = Splits.SplitPlanePackets.GetSubNativeArray(
                    s.PlanePacketOffset,
                    s.PlanePacketCount);
                var combinedSplitPlanes = Splits.CombinedSplitAndReceiverPlanePackets.GetSubNativeArray(
                    s.CombinedPlanePacketOffset,
                    s.CombinedPlanePacketCount);

                if ((visibleSplitMask & (1 << splitIndex)) == 0)
                    continue;

                var chunkIn = perInstanceCull
                    ? FrustumPlanes.Intersect2(splitPlanes, chunkBounds.Value)
                    : FrustumPlanes.Intersect2NoPartial(splitPlanes, chunkBounds.Value);

                if (chunkIn == FrustumPlanes.IntersectResult.Partial)
                {
                    for (int j = 0; j < 2; j++)
                    {
                        var enabledMask = j == 0 ? enabledMask128.ULong0 : enabledMask128.ULong1;
                        var lodWord = chunkEntityLodEnabled.Enabled[j] & enabledMask;
                        ulong visibleWord = 0;

                        while (lodWord != 0)
                        {
                            var bitIndex = math.tzcnt(lodWord);
                            var entityIndex = (j << 6) + bitIndex;
                            ulong mask = 1ul << bitIndex;

                            var bounds = worldRenderBounds[entityIndex].Value;

                            int visible =
                                FrustumPlanes.Intersect2NoPartial(combinedSplitPlanes, bounds) != FrustumPlanes.IntersectResult.Out
                                    ? 1
                                    : 0;

#if DEBUG_VALIDATE_COMBINED_SPLIT_RECEIVER_CULLING
                            bool visibleFrustum = FrustumPlanes.Intersect2NoPartial(splitPlanes, bounds) != FrustumPlanes.IntersectResult.Out;
                            bool visibleReceiver = FrustumPlanes.Intersect2NoPartial(receiverPlanes.AsNativeArray(), bounds) != FrustumPlanes.IntersectResult.Out;
                            int visibleReference = (visibleFrustum && visibleReceiver) ? 1 : 0;

                            if (visible != visibleReference)
                                Debug.Log($"Combined Split+Receiver ({visible}) plane culling mismatch with separate Split ({visibleFrustum}) and Receiver ({visibleReceiver})");
#endif

                            lodWord ^= mask;
                            visibleWord |= ((ulong)visible) << bitIndex;

                            if (visible != 0)
                                chunkVisibility->SplitMasks[entityIndex] |= splitMask;
                        }

                        chunkVisibility->VisibleEntities[j] |= visibleWord;
                    }
                }
                else if (chunkIn == FrustumPlanes.IntersectResult.In)
                {

                    chunkVisibility->VisibleEntities[0] |= chunkEntityLodEnabled.Enabled[0] & enabledMask128.ULong0;
                    chunkVisibility->VisibleEntities[1] |= chunkEntityLodEnabled.Enabled[1] & enabledMask128.ULong1;

                    for (int i = 0; i < numEntities; ++i)
                        chunkVisibility->SplitMasks[i] |= splitMask;
                }
                else if (chunkIn == FrustumPlanes.IntersectResult.Out)
                {

                }
            }

            if (useSphereTest && chunkVisibility->AnyVisible)
            {
                for (int j = 0; j < 2; j++)
                {
                    ulong visibleWord = chunkVisibility->VisibleEntities[j];

                    while (visibleWord != 0)
                    {
                        int bitIndex = math.tzcnt(visibleWord);
                        int entityIndex = (j << 6) + bitIndex;
                        ulong mask = 1ul << bitIndex;

                        var bounds = worldRenderBounds[entityIndex].Value;

                        int planeSplitMask = chunkVisibility->SplitMasks[entityIndex];
                        int sphereSplitMask = Splits.ReceiverSphereCuller.Cull(bounds);

                        byte newSplitMask = (byte)(planeSplitMask & sphereSplitMask);
                        chunkVisibility->SplitMasks[entityIndex] = newSplitMask;

                        if (newSplitMask == 0)
                            chunkVisibility->VisibleEntities[j] ^= mask;

                        visibleWord ^= mask;
                    }
                }
            }
        }
    }

    internal struct ReceiverSphereCuller
    {
        float4 ReceiverSphereCenterX4;
        float4 ReceiverSphereCenterY4;
        float4 ReceiverSphereCenterZ4;
        float4 LSReceiverSphereCenterX4;
        float4 LSReceiverSphereCenterY4;
        float4 LSReceiverSphereCenterZ4;
        float4 ReceiverSphereRadius4;
        float4 CoreSphereRadius4;
        UnsafeList<Plane> ShadowFrustumPlanes;

        float3 LightAxisX;
        float3 LightAxisY;
        float3 LightAxisZ;
        int NumSplits;

        public ReceiverSphereCuller(in BatchCullingContext cullingContext, in CullingSplits splits)
        {
            int numSplits = splits.Splits.Length;

            Assert.IsTrue(numSplits <= 4, "More than 4 culling splits is not supported for sphere testing");
            Assert.IsTrue(numSplits > 0, "No valid culling splits for sphere testing");

            if (numSplits > 4)
                numSplits = 4;

            ReceiverSphereCenterX4 = new float4(float.PositiveInfinity);
            ReceiverSphereCenterY4 = new float4(float.PositiveInfinity);
            ReceiverSphereCenterZ4 = new float4(float.PositiveInfinity);
            LSReceiverSphereCenterX4 = new float4(float.PositiveInfinity);
            LSReceiverSphereCenterY4 = new float4(float.PositiveInfinity);
            LSReceiverSphereCenterZ4 = new float4(float.PositiveInfinity);
            ReceiverSphereRadius4 = float4.zero;
            CoreSphereRadius4 = float4.zero;

            LightAxisX = new float4(cullingContext.localToWorldMatrix.GetColumn(0)).xyz;
            LightAxisY = new float4(cullingContext.localToWorldMatrix.GetColumn(1)).xyz;
            LightAxisZ = new float4(cullingContext.localToWorldMatrix.GetColumn(2)).xyz;
            NumSplits = numSplits;

            ShadowFrustumPlanes = GetUnsafeListView(cullingContext.cullingPlanes,
                cullingContext.receiverPlaneOffset,
                cullingContext.receiverPlaneCount);

            for (int i = 0; i < numSplits; ++i)
            {
                int elementIndex = i & 3;
                ref CullingSplitData split = ref splits.Splits.ElementAt(i);
                float3 lsReceiverSphereCenter = TransformToLightSpace(split.CullingSphereCenter, LightAxisX, LightAxisY, LightAxisZ);

                ReceiverSphereCenterX4[elementIndex] = split.CullingSphereCenter.x;
                ReceiverSphereCenterY4[elementIndex] = split.CullingSphereCenter.y;
                ReceiverSphereCenterZ4[elementIndex] = split.CullingSphereCenter.z;

                LSReceiverSphereCenterX4[elementIndex] = lsReceiverSphereCenter.x;
                LSReceiverSphereCenterY4[elementIndex] = lsReceiverSphereCenter.y;
                LSReceiverSphereCenterZ4[elementIndex] = lsReceiverSphereCenter.z;

                ReceiverSphereRadius4[elementIndex] = split.CullingSphereRadius;
                CoreSphereRadius4[elementIndex] = split.CullingSphereRadius * split.ShadowCascadeBlendCullingFactor;
            }
        }

        public int Cull(AABB aabb)
        {
            int visibleSplitMask = CullSIMD(aabb);

#if DEBUG_VALIDATE_VECTORIZED_CULLING
            int referenceSplitMask = CullNonSIMD(aabb);

            if (visibleSplitMask != referenceSplitMask)
                Debug.Log($"Vectorized culling test ({visibleSplitMask:x2}) disagrees with reference test ({referenceSplitMask:x2})");
#endif

            return visibleSplitMask;
        }

        int CullSIMD(AABB aabb)
        {
            float4 casterRadius4 = new float4(math.length(aabb.Extents));
            float4 combinedRadius4 = casterRadius4 + ReceiverSphereRadius4;
            float4 combinedRadiusSq4 = combinedRadius4 * combinedRadius4;

            float3 lsCasterCenter = TransformToLightSpace(aabb.Center, LightAxisX, LightAxisY, LightAxisZ);
            float4 lsCasterCenterX4 = lsCasterCenter.xxxx;
            float4 lsCasterCenterY4 = lsCasterCenter.yyyy;
            float4 lsCasterCenterZ4 = lsCasterCenter.zzzz;

            float4 lsCasterToReceiverSphereX4 = lsCasterCenterX4 - LSReceiverSphereCenterX4;
            float4 lsCasterToReceiverSphereY4 = lsCasterCenterY4 - LSReceiverSphereCenterY4;
            float4 lsCasterToReceiverSphereSqX4 = lsCasterToReceiverSphereX4 * lsCasterToReceiverSphereX4;
            float4 lsCasterToReceiverSphereSqY4 = lsCasterToReceiverSphereY4 * lsCasterToReceiverSphereY4;

            float4 lsCasterToReceiverSphereDistanceSq4 = lsCasterToReceiverSphereSqX4 + lsCasterToReceiverSphereSqY4;
            bool4 doCirclesOverlap4 = lsCasterToReceiverSphereDistanceSq4 <= combinedRadiusSq4;

            float4 lsZMaxAccountingForCasterRadius4 = LSReceiverSphereCenterZ4 + math.sqrt(combinedRadiusSq4 - lsCasterToReceiverSphereSqX4 - lsCasterToReceiverSphereSqY4);
            bool4 isBehindCascade4 = lsCasterCenterZ4 <= lsZMaxAccountingForCasterRadius4;

            int isFullyCoveredByCascadeMask = 0b1111;

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
            float3 shadowCapsuleBegin;
            float3 shadowCapsuleEnd;
            float shadowCapsuleRadius;
            ComputeShadowCapsule(LightAxisZ, aabb.Center, casterRadius4.x, ShadowFrustumPlanes,
                out shadowCapsuleBegin, out shadowCapsuleEnd, out shadowCapsuleRadius);

            bool4 isFullyCoveredByCascade4 = IsCapsuleInsideSphereSIMD(shadowCapsuleBegin, shadowCapsuleEnd, shadowCapsuleRadius,
                ReceiverSphereCenterX4, ReceiverSphereCenterY4, ReceiverSphereCenterZ4, CoreSphereRadius4);

            if (math.any(isFullyCoveredByCascade4))
            {

                int boolMask = math.bitmask(isFullyCoveredByCascade4);
                isFullyCoveredByCascadeMask = 1 << math.tzcnt(boolMask);
                isFullyCoveredByCascadeMask = isFullyCoveredByCascadeMask << 1;
                isFullyCoveredByCascadeMask = isFullyCoveredByCascadeMask - 1;
            }
#endif

            return math.bitmask(doCirclesOverlap4 & isBehindCascade4) & isFullyCoveredByCascadeMask;
        }

        int CullNonSIMD(AABB aabb)
        {

            float casterRadius = math.length(aabb.Extents);

            float3 lsCasterCenter = TransformToLightSpace(aabb.Center, LightAxisX, LightAxisY, LightAxisZ);
            float2 lsCasterCenterXY = new float2(lsCasterCenter.x, lsCasterCenter.y);

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
            float3 shadowCapsuleBegin;
            float3 shadowCapsuleEnd;
            float shadowCapsuleRadius;
            ComputeShadowCapsule(LightAxisZ, aabb.Center, casterRadius, ShadowFrustumPlanes,
                out shadowCapsuleBegin, out shadowCapsuleEnd, out shadowCapsuleRadius);
#endif

            int visibleSplitMask = 0;

            for (int i = 0; i < NumSplits; i++)
            {
                float receiverSphereRadius = ReceiverSphereRadius4[i];
                float3 lsReceiverSphereCenter = new float3(LSReceiverSphereCenterX4[i], LSReceiverSphereCenterY4[i], LSReceiverSphereCenterZ4[i]);
                float2 lsReceiverSphereCenterXY = new float2(lsReceiverSphereCenter.x, lsReceiverSphereCenter.y);

                float lsCasterToReceiverSphereDistanceSq = math.lengthsq(lsCasterCenterXY - lsReceiverSphereCenterXY);
                float combinedRadius = casterRadius + receiverSphereRadius;
                float combinedRadiusSq = combinedRadius * combinedRadius;

                if (lsCasterToReceiverSphereDistanceSq <= combinedRadiusSq)
                {
                    float2 lsCasterToReceiverSphereXY = lsCasterCenterXY - lsReceiverSphereCenterXY;
                    float2 lsCasterToReceiverSphereSqXY = lsCasterToReceiverSphereXY * lsCasterToReceiverSphereXY;

                    float lsZMaxAccountingForCasterRadius = lsReceiverSphereCenter.z + math.sqrt(combinedRadiusSq - lsCasterToReceiverSphereSqXY.x - lsCasterToReceiverSphereSqXY.y);
                    if (lsCasterCenter.z > lsZMaxAccountingForCasterRadius)
                    {

                        continue;
                    }

                    visibleSplitMask |= 1 << i;

#if !DISABLE_SHADOW_CULLING_CAPSULE_TEST
                    float3 receiverSphereCenter = new float3(ReceiverSphereCenterX4[i], ReceiverSphereCenterY4[i], ReceiverSphereCenterZ4[i]);
                    float coreSphereRadius = CoreSphereRadius4[i];

                    if (IsCapsuleInsideSphere(shadowCapsuleBegin, shadowCapsuleEnd, shadowCapsuleRadius, receiverSphereCenter, coreSphereRadius))
                    {

                        break;
                    }
#endif
                }
            }

            return visibleSplitMask;
        }

        static void ComputeShadowCapsule(float3 lightDirection, float3 casterPosition, float casterRadius, UnsafeList<Plane> shadowFrustumPlanes,
            out float3 shadowCapsuleBegin, out float3 shadowCapsuleEnd, out float shadowCapsuleRadius)
        {
            float shadowCapsuleLength = GetShadowVolumeLengthFromCasterAndFrustumAndLightDir(lightDirection,
                casterPosition,
                casterRadius,
                shadowFrustumPlanes);

            shadowCapsuleBegin = casterPosition;
            shadowCapsuleEnd = casterPosition + shadowCapsuleLength * lightDirection;
            shadowCapsuleRadius = casterRadius;
        }

        static float GetShadowVolumeLengthFromCasterAndFrustumAndLightDir(float3 lightDir, float3 casterPosition, float casterRadius, UnsafeList<Plane> planes)
        {

            var casterRay = new Ray(casterPosition, lightDir);
            int planeIndex;
            float distFromCasterToFrustumInLightDirection = RayDistanceToFrustumOriented(casterRay, planes, out planeIndex);
            if (planeIndex == -1)
            {

                return 0;
            }

            Assert.IsTrue(planeIndex >= 0 && planeIndex < planes.Length);

            float distFromCasterToPlane = math.abs(planes[planeIndex].GetDistanceToPoint(casterPosition));
            float sinAlpha = distFromCasterToPlane / (distFromCasterToFrustumInLightDirection + 0.0001f);
            float tanAlpha = sinAlpha / (math.sqrt(1.0f - (sinAlpha * sinAlpha)));
            distFromCasterToFrustumInLightDirection += casterRadius / (tanAlpha + 0.0001f);

            return distFromCasterToFrustumInLightDirection;
        }

        static float RayDistanceToFrustumOriented(Ray ray, UnsafeList<Plane> planes, out int planeNumber)
        {
            planeNumber = -1;
            float maxDistance = float.PositiveInfinity;
            for (int i = 0; i < planes.Length; ++i)
            {
                float distance;
                if (IntersectRayPlaneOriented(ray, planes[i], out distance) && distance < maxDistance)
                {
                    maxDistance = distance;
                    planeNumber = i;
                }
            }

            return planeNumber != -1 ? maxDistance : -1.0f;
        }

        static bool IntersectRayPlaneOriented(Ray ray, Plane plane, out float distance)
        {
            distance = 0f;

            float vdot = math.dot(ray.direction, plane.normal);
            float ndot = -math.dot(ray.origin, plane.normal) - plane.distance;

            if (vdot > 0)
                return false;

            if (Mathf.Approximately(vdot, 0.0F))
                return false;

            distance = ndot / vdot;

            return distance > 0.0F;
        }

        static bool IsInsideSphere(BoundingSphere sphere, BoundingSphere containingSphere)
        {
            if (sphere.radius >= containingSphere.radius)
                return false;

            float squaredDistance = math.lengthsq(containingSphere.position - sphere.position);
            float radiusDelta = containingSphere.radius - sphere.radius;
            float squaredRadiusDelta = radiusDelta * radiusDelta;

            return squaredDistance < squaredRadiusDelta;
        }

        static bool4 IsInsideSphereSIMD(float4 sphereCenterX, float4 sphereCenterY, float4 sphereCenterZ, float4 sphereRadius,
            float4 containingSphereCenterX, float4 containingSphereCenterY, float4 containingSphereCenterZ, float4 containingSphereRadius)
        {
            float4 dx = containingSphereCenterX - sphereCenterX;
            float4 dy = containingSphereCenterY - sphereCenterY;
            float4 dz = containingSphereCenterZ - sphereCenterZ;

            float4 squaredDistance = dx * dx + dy * dy + dz * dz;
            float4 radiusDelta = containingSphereRadius - sphereRadius;
            float4 squaredRadiusDelta = radiusDelta * radiusDelta;

            bool4 canSphereFit = sphereRadius < containingSphereRadius;
            bool4 distanceTest = squaredDistance < squaredRadiusDelta;

            return canSphereFit & distanceTest;
        }

        static bool IsCapsuleInsideSphere(float3 capsuleBegin, float3 capsuleEnd, float capsuleRadius, float3 sphereCenter, float sphereRadius)
        {
            var sphere = new BoundingSphere(sphereCenter, sphereRadius);
            var beginPoint = new BoundingSphere(capsuleBegin, capsuleRadius);
            var endPoint = new BoundingSphere(capsuleEnd, capsuleRadius);

            return IsInsideSphere(beginPoint, sphere) && IsInsideSphere(endPoint, sphere);
        }

        static bool4 IsCapsuleInsideSphereSIMD(float3 capsuleBegin, float3 capsuleEnd, float capsuleRadius,
            float4 sphereCenterX, float4 sphereCenterY, float4 sphereCenterZ, float4 sphereRadius)
        {
            float4 beginSphereX = capsuleBegin.xxxx;
            float4 beginSphereY = capsuleBegin.yyyy;
            float4 beginSphereZ = capsuleBegin.zzzz;

            float4 endSphereX = capsuleEnd.xxxx;
            float4 endSphereY = capsuleEnd.yyyy;
            float4 endSphereZ = capsuleEnd.zzzz;

            float4 capsuleRadius4 = new float4(capsuleRadius);

            bool4 isInsideBeginSphere = IsInsideSphereSIMD(beginSphereX, beginSphereY, beginSphereZ, capsuleRadius4,
                sphereCenterX, sphereCenterY, sphereCenterZ, sphereRadius);

            bool4 isInsideEndSphere = IsInsideSphereSIMD(endSphereX, endSphereY, endSphereZ, capsuleRadius4,
                sphereCenterX, sphereCenterY, sphereCenterZ, sphereRadius);

            return isInsideBeginSphere & isInsideEndSphere;
        }

        static float3 TransformToLightSpace(float3 positionWS, float3 lightAxisX, float3 lightAxisY, float3 lightAxisZ) => new float3(
            math.dot(positionWS, lightAxisX),
            math.dot(positionWS, lightAxisY),
            math.dot(positionWS, lightAxisZ));

        static unsafe UnsafeList<Plane> GetUnsafeListView(NativeArray<Plane> array, int start, int length)
        {
            NativeArray<Plane> subArray = array.GetSubArray(start, length);
            return new UnsafeList<Plane>((Plane*)subArray.GetUnsafeReadOnlyPtr(), length);
        }
    }
}

#endif