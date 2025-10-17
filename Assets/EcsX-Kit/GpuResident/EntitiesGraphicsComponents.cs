#define ENABLE_BATCH_OPTIMIZATION

using Unity.Entities;

namespace Unity.Rendering
{

    public struct EntitiesGraphicsChunkInfo : IComponentData
    {

        internal int BatchIndex;
#if ENABLE_BATCH_OPTIMIZATION

        internal int SubBatchIndex;
#endif

        internal int ChunkTypesBegin;

        internal int ChunkTypesEnd;

        internal EntitiesGraphicsChunkCullingData CullingData;

        internal bool Valid;
    }

    internal unsafe struct EntitiesGraphicsChunkCullingData
    {

        public const int kFlagHasLodData = 1 << 0;

        public const int kFlagInstanceCulling = 1 << 1;

        public const int kFlagPerObjectMotion = 1 << 2;

        public const int kFlagHasChunkLodData = 1 << 3;

        public int ChunkOffsetInBatch;

        public ushort MovementGraceFixed16;

        public byte Flags;
        public byte ForceLowLODPrevious;

        public ChunkInstanceLodEnabled InstanceLodEnableds;
        //public fixed ulong FlippedWinding[2];
    }

    public struct EntitiesGraphicsBatchPartition : ISharedComponentData
    {

        public ulong PartitionValue;
    }

    public struct WorldToLocal_Tag : IComponentData {}

    public struct DepthSorted_Tag : IComponentData {}

    public struct PerVertexMotionVectors_Tag : IComponentData {}
}
