using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Rendering
{

    internal struct SubBatch
    {
        public HeapBlock ChunkOffsetInBatch;
        public HeapBlock ChunkMetadataAllocation;
        public int BatchID;
        internal int NextID;
        internal int PrevID;
    }

    internal unsafe struct SubBatchAllocator : IDisposable
    {
        internal int m_Length;
        internal SubBatch* m_SubBatchList;

        int m_FirstFree;
        int m_FreeCount;

        int m_MaxIssuedId;

        internal const int InvalidBatchNumber = -1;
        public SubBatchAllocator(int length)
        {
            m_Length = length;
            m_SubBatchList = (SubBatch*)UnsafeUtility.Malloc(UnsafeUtility.SizeOf<SubBatch>() * length, 64, Allocator.Persistent);
            UnsafeUtility.MemClear(m_SubBatchList, (long)length * (long)UnsafeUtility.SizeOf<SubBatch>());

            m_FirstFree = 0;
            m_FreeCount = length;

            m_MaxIssuedId = -1;

            for (int i = 0; i < m_Length; i++)
            {

                m_SubBatchList[i] = default;
                m_SubBatchList[i].BatchID = InvalidBatchNumber;
                m_SubBatchList[i].PrevID = InvalidBatchNumber;
                m_SubBatchList[i].NextID = i != m_Length - 1 ? i + 1 : InvalidBatchNumber;
            }
        }
        public void Dispose()
        {
            UnsafeUtility.Free(m_SubBatchList, Allocator.Persistent);
        }

        public int Allocate()
        {
            if (m_FreeCount == 0)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogWarning($"SubBatchAllocator exhausted! FreeCount=0, Length={m_Length}");
#endif
                return -1;
            }

            if (m_FirstFree < 0 || m_FirstFree >= m_Length)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL: Corrupt m_FirstFree={m_FirstFree}, FreeCount={m_FreeCount}, Length={m_Length}");
#endif
                return -1;
            }

            int idx = m_FirstFree;
            var block = m_SubBatchList + m_FirstFree;
            m_FirstFree = block->NextID;
            *block = default;

            block->BatchID = -2;
            block->PrevID = block->NextID = InvalidBatchNumber;
            --m_FreeCount;
#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"SubBatch allocated: {idx}, remaining free: {m_FreeCount}/{m_Length}");
#endif
            if (idx > m_MaxIssuedId) m_MaxIssuedId = idx;

            return idx;
        }

        public void Dealloc(int subBatchID)
        {

            if (m_FreeCount >= m_Length)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL: SubBatchAllocator double-free detected! FreeCount={m_FreeCount}, Length={m_Length}, attempting to free ID={subBatchID}");
#endif
                return;
            }

            if (subBatchID < 0 || subBatchID >= m_Length)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL: Invalid SubBatch ID {subBatchID}, valid range [0, {m_Length-1}]");
#endif
                return;
            }

            ref var subBatch = ref m_SubBatchList[subBatchID];

            if (subBatch.BatchID == InvalidBatchNumber &&
                subBatch.ChunkOffsetInBatch.Empty &&
                subBatch.ChunkMetadataAllocation.Empty)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogWarning($"SubBatch {subBatchID} already freed (BatchID={subBatch.BatchID}), skipping duplicate deallocation");
#endif
                return;
            }

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"SubBatch deallocated: {subBatchID}, free count will be: {m_FreeCount + 1}/{m_Length}");
#endif

            subBatch = default;
            subBatch.NextID = m_FirstFree;
            m_FirstFree = subBatchID;

            ++m_FreeCount;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubBatch* GetUnsafePtr()
        {
            return m_SubBatchList;
        }

        public int MaxIssuedId => m_MaxIssuedId;
    }
}
