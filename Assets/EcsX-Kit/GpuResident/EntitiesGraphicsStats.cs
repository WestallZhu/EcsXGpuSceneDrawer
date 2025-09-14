namespace Unity.Rendering
{

    public struct EntitiesGraphicsPerThreadStats
    {

        public int ChunkTotal;

        public int ChunkCountAnyLod;

        public int ChunkCountInstancesProcessed;

        public int ChunkCountFullyIn;

        public int InstanceTests;

        public int LodTotal;

        public int LodNoRequirements;

        public int LodChanged;

        public int LodChunksTested;

        public int RenderedEntityCount;

        public int DrawCommandCount;

        public int DrawRangeCount;
    }

    public struct EntitiesGraphicsStats
    {

        public int ChunkTotal;

        public int ChunkCountAnyLod;

        public int ChunkCountInstancesProcessed;

        public int ChunkCountFullyIn;

        public int InstanceTests;

        public int LodTotal;

        public int LodNoRequirements;

        public int LodChanged;

        public int LodChunksTested;

        public float CameraMoveDistance;

        public int BatchCount;

        public int RenderedInstanceCount;

        public int DrawCommandCount;

        public int DrawRangeCount;

        public long BytesGPUMemoryUsed;

        public long BytesGPUMemoryUploadedCurr;

        public long BytesGPUMemoryUploadedMax;
    }
}
