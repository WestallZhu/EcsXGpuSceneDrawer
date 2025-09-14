using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

#if ENABLE_UNITY_COLLECTIONS_CHECKS
using UnityEngine;
#endif

namespace Unity.Rendering
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct FreeBlock
    {
        public int offset;
        public int count;
        public int nextID;
        public int prevID;
    }

    internal unsafe struct FreeBlockAllocator : IDisposable
    {
        private int m_Length;
        internal FreeBlock* m_Blocks;
        private int m_FirstFree;
        private int m_FreeCount;
        private const int InvalidID = -1;

        public FreeBlockAllocator(int capacity)
        {
            m_Length = capacity;
            long size = (long)UnsafeUtility.SizeOf<FreeBlock>() * capacity;
            m_Blocks = (FreeBlock*)UnsafeUtility.Malloc(size, UnsafeUtility.AlignOf<FreeBlock>(), Allocator.Persistent);
            UnsafeUtility.MemClear(m_Blocks, size);

            m_FirstFree = 0;
            m_FreeCount = capacity;

            for (int i = 0; i < capacity; i++)
            {
                m_Blocks[i].nextID = (i < capacity - 1) ? (i + 1) : InvalidID;
                m_Blocks[i].prevID = InvalidID;
            }
        }

        public void Dispose()
        {
            if (m_Blocks != null)
            {
                UnsafeUtility.Free(m_Blocks, Allocator.Persistent);
                m_Blocks = null;
            }
            m_Length = 0;
            m_FirstFree = InvalidID;
            m_FreeCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int AllocateNode()
        {
            if (m_FreeCount == 0 || m_FirstFree == InvalidID)
                return InvalidID;

            int id = m_FirstFree;
            ref FreeBlock node = ref m_Blocks[id];
            m_FirstFree = node.nextID;
            node.nextID = InvalidID;
            node.prevID = InvalidID;
            m_FreeCount--;
            return id;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void FreeNode(int id)
        {
            ref FreeBlock node = ref m_Blocks[id];
            node.offset = 0;
            node.count = 0;
            node.prevID = InvalidID;
            node.nextID = m_FirstFree;
            m_FirstFree = id;
            m_FreeCount++;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FreeBlock* GetUnsafePtr() => m_Blocks;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool EnsureFreeNode()
        {
            if (m_FreeCount > 0 && m_FirstFree != InvalidID) return true;
            int newCapacity = (m_Length > 0) ? (m_Length * 2) : 64;
            return Grow(newCapacity);
        }

        private bool Grow(int newCapacity)
        {
            if (newCapacity <= m_Length) return true;

            long oldSize = (long)UnsafeUtility.SizeOf<FreeBlock>() * m_Length;
            long newSize = (long)UnsafeUtility.SizeOf<FreeBlock>() * newCapacity;
            var newBlocks = (FreeBlock*)UnsafeUtility.Malloc(newSize, UnsafeUtility.AlignOf<FreeBlock>(), Allocator.Persistent);
            if (newBlocks == null)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError("FreeBlockAllocator.Grow: allocation failed.");
#endif
                return false;
            }

            UnsafeUtility.MemClear(newBlocks, newSize);

            if (m_Blocks != null && m_Length > 0)
                UnsafeUtility.MemCpy(newBlocks, m_Blocks, oldSize);

            int firstAdded = m_Length;
            for (int i = firstAdded; i < newCapacity; ++i)
            {
                newBlocks[i].nextID = (i < newCapacity - 1) ? (i + 1) : InvalidID;
                newBlocks[i].prevID = InvalidID;
            }
            int oldFirst = m_FirstFree;
            m_FirstFree = firstAdded;
            if (newCapacity - 1 >= 0)
                newBlocks[newCapacity - 1].nextID = oldFirst;

            m_FreeCount += (newCapacity - m_Length);

            if (m_Blocks != null)
                UnsafeUtility.Free(m_Blocks, Allocator.Persistent);

            m_Blocks = newBlocks;
            m_Length = newCapacity;
            return true;
        }
    }

    internal unsafe struct SmallBlockAllocator : IDisposable
    {
        private readonly int maxInstances;
        private FreeBlockAllocator blockPool;
        private int freeListHeadID;
        private int allocCursorID;
        private int maxFreeBlock;
        private bool maxDirty;
        private int totalFree;

        private const int InvalidID = -1;

        private const int CursorLgSize = 6;
        private const int CursorSize = 1 << CursorLgSize;
        private const int CursorMask = CursorSize - 1;

        private const int BucketShift = 5;

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorCell
        {
            public uint key;
            public int prev;
        }

        private CursorCell* cursor;

        public SmallBlockAllocator(int maxInstances)
        {
            this.maxInstances = maxInstances;

            blockPool = new FreeBlockAllocator(128);

            int root = blockPool.AllocateNode();
            var nodes = blockPool.GetUnsafePtr();
            nodes[root].offset = 0;
            nodes[root].count = maxInstances;
            nodes[root].nextID = InvalidID;
            nodes[root].prevID = InvalidID;

            freeListHeadID = root;
            allocCursorID = freeListHeadID;

            totalFree = maxInstances;
            maxFreeBlock = maxInstances;
            maxDirty = false;

            long csz = (long)UnsafeUtility.SizeOf<CursorCell>() * CursorSize;
            cursor = (CursorCell*)UnsafeUtility.Malloc(csz, UnsafeUtility.AlignOf<CursorCell>(), Allocator.Persistent);
            UnsafeUtility.MemClear(cursor, csz);
            for (int i = 0; i < CursorSize; ++i)
            {
                cursor[i].key = 0xFFFFFFFFu;
                cursor[i].prev = InvalidID;
            }
        }

        public void Dispose()
        {
            if (cursor != null)
            {
                UnsafeUtility.Free(cursor, Allocator.Persistent);
                cursor = null;
            }
            blockPool.Dispose();
        }

        public bool IsCreated => blockPool.m_Blocks != null;

        public int Allocate(int count)
        {

            if (count <= 0) return -1;

            if (totalFree < count)
                return -1;

            if (!maxDirty && maxFreeBlock < count)
                return -1;

            var nodes = blockPool.GetUnsafePtr();
            if (freeListHeadID == InvalidID)
                return -1;

            int observedMax = 0;

            int cur = allocCursorID;
            while (cur != InvalidID)
            {
                if (nodes[cur].count > observedMax) observedMax = nodes[cur].count;

                if (nodes[cur].count >= count)
                {

                    int start = nodes[cur].offset;
                    nodes[cur].offset += count;
                    nodes[cur].count -= count;

                    totalFree -= count;
                    maxDirty = true;

                    if (nodes[cur].count == 0)
                    {

                        int prev = nodes[cur].prevID;
                        int next = nodes[cur].nextID;

                        if (prev == InvalidID) freeListHeadID = next;
                        else nodes[prev].nextID = next;
                        if (next != InvalidID) nodes[next].prevID = prev;

                        blockPool.FreeNode(cur);
                        allocCursorID = next;
                    }
                    else
                    {
                        allocCursorID = cur;
                        if (nodes[cur].count > maxFreeBlock)
                            maxFreeBlock = nodes[cur].count;
                    }
                    return start;
                }

                cur = nodes[cur].nextID;
            }

            cur = freeListHeadID;
            while (cur != InvalidID && cur != allocCursorID)
            {

                if (nodes[cur].count > observedMax) observedMax = nodes[cur].count;

                if (nodes[cur].count >= count)
                {

                    int start = nodes[cur].offset;
                    nodes[cur].offset += count;
                    nodes[cur].count -= count;

                    totalFree -= count;
                    maxDirty = true;

                    if (nodes[cur].count == 0)
                    {

                        int prev = nodes[cur].prevID;
                        int next = nodes[cur].nextID;

                        if (prev == InvalidID) freeListHeadID = next;
                        else nodes[prev].nextID = next;
                        if (next != InvalidID) nodes[next].prevID = prev;

                        blockPool.FreeNode(cur);
                        allocCursorID = next;
                    }
                    else
                    {

                        allocCursorID = cur;

                        if (nodes[cur].count > maxFreeBlock)
                            maxFreeBlock = nodes[cur].count;
                    }
                    return start;
                }

                cur = nodes[cur].nextID;
            }

            maxFreeBlock = observedMax;
            maxDirty = false;
            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void FixCursorOnRemove(int removedId, int fallbackId)
        {
            if (allocCursorID == removedId)
                allocCursorID = (fallbackId != InvalidID) ? fallbackId : freeListHeadID;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Mix32(uint x)
        {
            x ^= x >> 16;
            x *= 0x85EBCA6Bu;
            x ^= x >> 13;
            x *= 0xC2B2AE35u;
            x ^= x >> 16;
            return x;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int IndexOf(uint key) => (int)(key & CursorMask);

        public void Deallocate(int offset, int count)
        {

            if (count <= 0 || offset < 0 || offset > maxInstances || offset + count > maxInstances)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError($"SmallBlockAllocator.Deallocate invalid range [{offset},{offset + count}) (max={maxInstances}).");
#endif
                return;
            }

            var nodes = blockPool.GetUnsafePtr();
            bool wasEmpty = (freeListHeadID == InvalidID);

            int prev = InvalidID;
            int cur = InvalidID;

            uint key = Mix32((uint)offset);
            int idx = IndexOf(key);

            if (cursor != null && cursor[idx].key == key)
            {
                prev = cursor[idx].prev;

                if (prev != InvalidID && nodes[prev].offset >= offset)
                {
                    prev = InvalidID;
                    cur = freeListHeadID;
                }
                else
                {
                    cur = (prev == InvalidID) ? freeListHeadID : nodes[prev].nextID;
                }

                while (cur != InvalidID && nodes[cur].offset < offset)
                {
                    prev = cur;
                    cur = nodes[cur].nextID;
                }
            }
            else
            {

                cur = freeListHeadID;
                while (cur != InvalidID && nodes[cur].offset < offset)
                {
                    prev = cur;
                    cur = nodes[cur].nextID;
                }
            }

            if (prev != InvalidID && nodes[prev].offset + nodes[prev].count > offset)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError("SmallBlockAllocator.Deallocate: overlap with previous block (double free or split-free).");
#endif
                return;
            }
            if (cur != InvalidID && offset + count > nodes[cur].offset)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError("SmallBlockAllocator.Deallocate: overlap with next block (double free or split-free).");
#endif
                return;
            }

            bool prevAdj = (prev != InvalidID) && (nodes[prev].offset + nodes[prev].count == offset);
            bool nextAdj = (cur != InvalidID) && (offset + count == nodes[cur].offset);

            if (prevAdj && nextAdj)
            {
                nodes[prev].count += count + nodes[cur].count;

                int next = nodes[cur].nextID;
                nodes[prev].nextID = next;
                if (next != InvalidID) nodes[next].prevID = prev;

                blockPool.FreeNode(cur);
                FixCursorOnRemove(  cur,  prev);

                totalFree += count;
                maxDirty = true;
                if (nodes[prev].count > maxFreeBlock) maxFreeBlock = nodes[prev].count;

                if (cursor != null)
                {
                    cursor[idx].key = key;
                    cursor[idx].prev = prev;
                }

                if (wasEmpty) allocCursorID = freeListHeadID;
                return;
            }

            if (prevAdj)
            {
                nodes[prev].count += count;

                totalFree += count;
                maxDirty = true;
                if (nodes[prev].count > maxFreeBlock) maxFreeBlock = nodes[prev].count;

                if (cursor != null)
                {
                    cursor[idx].key = key;
                    cursor[idx].prev = prev;
                }

                if (wasEmpty) allocCursorID = freeListHeadID;
                return;
            }

            if (nextAdj)
            {
                nodes[cur].offset = offset;
                nodes[cur].count += count;

                totalFree += count;
                maxDirty = true;
                if (nodes[cur].count > maxFreeBlock) maxFreeBlock = nodes[cur].count;

                if (cursor != null)
                {

                    cursor[idx].key = key;
                    cursor[idx].prev = nodes[cur].prevID;
                }

                if (wasEmpty) allocCursorID = freeListHeadID;
                return;
            }

            if (!blockPool.EnsureFreeNode())
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError("SmallBlockAllocator: EnsureFreeNode failed (node pool growth).");
#endif
                return;
            }
            nodes = blockPool.GetUnsafePtr();

            int nid = blockPool.AllocateNode();
            if (nid == InvalidID)
            {
#if ENABLE_UNITY_COLLECTIONS_CHECKS
                Debug.LogError("SmallBlockAllocator: AllocateNode failed after EnsureFreeNode.");
#endif
                return;
            }

            ref var nb = ref nodes[nid];
            nb.offset = offset;
            nb.count = count;
            nb.prevID = prev;
            nb.nextID = cur;

            if (prev == InvalidID) freeListHeadID = nid;
            else nodes[prev].nextID = nid;
            if (cur != InvalidID) nodes[cur].prevID = nid;

            if (wasEmpty) allocCursorID = freeListHeadID;

            totalFree += count;
            maxDirty = true;
            if (nb.count > maxFreeBlock) maxFreeBlock = nb.count;

            if (cursor != null)
            {
                cursor[idx].key = key;
                cursor[idx].prev = prev;
            }
        }
    }
}

