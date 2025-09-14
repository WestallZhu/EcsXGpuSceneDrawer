#if UNITY_2022_2_OR_NEWER

using UnityEngine.Assertions;
using System;
using Unity.Collections;
using Unity.Mathematics;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Rendering
{

    public struct FixedSizeAllocator : IDisposable
    {

        NativeArray<int> m_BlockFreelist;

        int m_FirstFree;

        int m_FreeCount;

        int m_BlockSize;

        public FixedSizeAllocator(int blockSize, int maxBlockCount)
        {
            m_BlockFreelist = new NativeArray<int>(maxBlockCount, Allocator.Persistent);

            m_BlockSize = blockSize;

            m_FirstFree = 0;
            m_FreeCount = maxBlockCount;

            for (int i = 0; i < maxBlockCount; i++)
            {
                m_BlockFreelist[i] = i + 1;
            }
            m_BlockFreelist[maxBlockCount - 1] = -1;
        }

        public HeapBlock Allocate()
        {
            if (m_FreeCount == 0 || m_FirstFree < 0)
                return new HeapBlock();

            int idx = m_FirstFree;
            ulong memBeg = (ulong)(idx * m_BlockSize);
            HeapBlock block = new HeapBlock(memBeg, memBeg + (ulong)m_BlockSize);

            m_FirstFree = m_BlockFreelist[idx];
            m_BlockFreelist[idx] = -2;
            --m_FreeCount;

            return block;
        }

        public void Dealloc(HeapBlock block)
        {
            if (block.Empty)
                return;

            int blockIndex = (int)block.begin / m_BlockSize;

            m_BlockFreelist[blockIndex] = m_FirstFree;
            m_FirstFree = blockIndex;

            ++m_FreeCount;
        }

        public bool Empty { get { return m_FreeCount == m_BlockFreelist.Length; } }

        public bool Full { get { return m_FreeCount == 0; } }

        public int FreeCount { get { return m_FreeCount; } }

        public int MaxBlockCount { get { return m_BlockFreelist.IsCreated ? m_BlockFreelist.Length : 0; } }

        public int UsedCount { get { return MaxBlockCount - m_FreeCount; } }

        public float UtilizationRatio { get { return MaxBlockCount > 0 ? (float)UsedCount / MaxBlockCount : 0f; } }

        public void Resize(int newMaxBlockCount)
        {
            int currentMaxCount = m_BlockFreelist.Length;

            if (newMaxBlockCount <= currentMaxCount)
                return;

            Assert.IsTrue(newMaxBlockCount > currentMaxCount,
                "New block count must be greater than current count");

            var newBlockFreelist = new NativeArray<int>(newMaxBlockCount, Allocator.Persistent);

            for (int i = 0; i < currentMaxCount; i++)
            {
                newBlockFreelist[i] = m_BlockFreelist[i];
            }

            int lastNewBlock = newMaxBlockCount - 1;
            for (int i = currentMaxCount; i < newMaxBlockCount; i++)
            {
                newBlockFreelist[i] = (i < lastNewBlock) ? i + 1 : m_FirstFree;
            }

            if (m_FreeCount == 0)
            {

                m_FirstFree = currentMaxCount;
            }
            else
            {

                newBlockFreelist[lastNewBlock] = m_FirstFree;
                m_FirstFree = currentMaxCount;
            }

            m_FreeCount += (newMaxBlockCount - currentMaxCount);

            m_BlockFreelist.Dispose();
            m_BlockFreelist = newBlockFreelist;
        }

        public void Dispose()
        {
            if (m_BlockFreelist.IsCreated)
                m_BlockFreelist.Dispose();
        }
    }
}

#endif