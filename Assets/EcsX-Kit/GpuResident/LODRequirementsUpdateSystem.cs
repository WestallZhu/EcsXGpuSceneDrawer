using System.Diagnostics;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Entities;
using Unity.Jobs;
using Unity.Mathematics;
using Unity.Transforms;


namespace Unity.Rendering
{

    public struct PerInstanceCullingTag : IComponentData {}

    internal struct RootLODWorldReferencePoint : IComponentData
    {

        public float3 Value;
    }

    internal struct SkipRootLODWorldReferencePointUpdate : IComponentData
    {
    }

    internal struct RootLODRange : IComponentData
    {

        public LODRange LOD;
    }

    internal struct LODWorldReferencePoint : IComponentData
    {
        public float3 Value;
    }

    internal struct SkipLODWorldReferencePointUpdate : IComponentData
    {
    }

    internal struct LODRange : IComponentData
    {

        public float MinDist;

        public float MaxDist;

        public int LODMask;

        public LODRange(MeshLODGroupComponent lodGroup, int lodMask)
        {
            float minDist = float.MaxValue;
            float maxDist = 0.0F;

            if ((lodMask & 0x01) == 0x01)
            {
                minDist = 0.0f;
                maxDist = math.max(maxDist, lodGroup.LODDistances0.x);
            }
            if ((lodMask & 0x02) == 0x02)
            {
                minDist = math.min(minDist, lodGroup.LODDistances0.x);
                maxDist = math.max(maxDist, lodGroup.LODDistances0.y);
            }
            if ((lodMask & 0x04) == 0x04)
            {
                minDist = math.min(minDist, lodGroup.LODDistances0.y);
                maxDist = math.max(maxDist, lodGroup.LODDistances0.z);
            }
            if ((lodMask & 0x08) == 0x08)
            {
                minDist = math.min(minDist, lodGroup.LODDistances0.z);
                maxDist = math.max(maxDist, lodGroup.LODDistances0.w);
            }
            if ((lodMask & 0x10) == 0x10)
            {
                minDist = math.min(minDist, lodGroup.LODDistances0.w);
                maxDist = math.max(maxDist, lodGroup.LODDistances1.x);
            }
            if ((lodMask & 0x20) == 0x20)
            {
                minDist = math.min(minDist, lodGroup.LODDistances1.x);
                maxDist = math.max(maxDist, lodGroup.LODDistances1.y);
            }
            if ((lodMask & 0x40) == 0x40)
            {
                minDist = math.min(minDist, lodGroup.LODDistances1.y);
                maxDist = math.max(maxDist, lodGroup.LODDistances1.z);
            }
            if ((lodMask & 0x80) == 0x80)
            {
                minDist = math.min(minDist, lodGroup.LODDistances1.z);
                maxDist = math.max(maxDist, lodGroup.LODDistances1.w);
            }

            MinDist = minDist;
            MaxDist = maxDist;
            LODMask = lodMask;
        }
    }

    internal struct SkipLODRangeUpdate : IComponentData
    {
    }

    public struct ChunkSimpleLOD : IComponentData
    {

        public int Value;
    }

}

