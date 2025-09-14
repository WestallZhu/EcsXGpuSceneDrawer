using System;
using System.Runtime.CompilerServices;
using Unity.Collections;

namespace Unity.Rendering
{
    using System;
    using System.Runtime.CompilerServices;
    using Unity.Collections;

    public unsafe class SegregatedUnitAllocator : IDisposable
    {
        const int INVALID = -1;

        readonly int m_UnitBytes;
        readonly int m_MaxUnits;

        NativeArray<int> m_BucketHead;

        struct Node { public int startUnit; public int next; }
        NativeList<Node> m_Nodes;
        int m_FreeNodeHead;

        int m_UnitsTop;
        long m_FreeUnits;

        public SegregatedUnitAllocator(
            int unitBytes = 16,
            int maxUnits = 64,
            int initialUnits = 0,
            Allocator allocator = Allocator.Persistent)
        {
            if (unitBytes <= 0) throw new ArgumentOutOfRangeException(nameof(unitBytes));
            if (maxUnits <= 0) throw new ArgumentOutOfRangeException(nameof(maxUnits));

            m_UnitBytes = unitBytes;
            m_MaxUnits = maxUnits;

            m_BucketHead = new NativeArray<int>(m_MaxUnits + 1, allocator);
            for (int i = 0; i < m_BucketHead.Length; i++) m_BucketHead[i] = INVALID;

            m_Nodes = new NativeList<Node>(128, allocator);
            m_FreeNodeHead = INVALID;

            m_UnitsTop = Math.Max(0, initialUnits);
            m_FreeUnits = 0;
        }

        public bool IsCreated => m_BucketHead.IsCreated;
        public int UnitBytes => m_UnitBytes;
        public int MaxUnits => m_MaxUnits;
        public ulong CapacityBytes => (ulong)m_UnitsTop * (ulong)m_UnitBytes;
        public ulong ActiveBytes => (ulong)(m_UnitsTop - (int)m_FreeUnits) * (ulong)m_UnitBytes;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int BytesToUnits(ulong bytes) => (int)((bytes + (ulong)(m_UnitBytes - 1)) / (ulong)m_UnitBytes);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong UnitsToBytes(int units) => (ulong)units * (ulong)m_UnitBytes;

        int AcquireNodeIndex()
        {
            if (m_FreeNodeHead != INVALID)
            {
                int idx = m_FreeNodeHead;
                m_FreeNodeHead = m_Nodes[idx].next;
                return idx;
            }
            int newIdx = m_Nodes.Length;
            m_Nodes.Add(default);
            return newIdx;
        }

        void ReleaseNodeIndex(int idx)
        {
            m_Nodes[idx] = new Node { startUnit = 0, next = m_FreeNodeHead };
            m_FreeNodeHead = idx;
        }

        public bool TryAllocateBytes(ulong bytes, out HeapBlock block)
        {
            int units = BytesToUnits(bytes);
            return TryAllocateUnits(units, out block);
        }

        public bool TryAllocateUnits(int units, out HeapBlock block)
        {
            block = new HeapBlock();
            if (units <= 0 || units > m_MaxUnits) return false;

            int head = m_BucketHead[units];
            int startUnit;

            if (head != INVALID)
            {
                var node = m_Nodes[head];
                m_BucketHead[units] = node.next;
                m_FreeUnits -= units;
                startUnit = node.startUnit;
                ReleaseNodeIndex(head);
            }
            else
            {
                startUnit = m_UnitsTop;
                m_UnitsTop += units;
            }

            block.m_Begin = (ulong)startUnit * (ulong)m_UnitBytes;
            block.m_End = block.m_Begin + UnitsToBytes(units);
            return true;
        }

        public HeapBlock Allocate(ulong bytes)
        {
            if (!TryAllocateBytes(bytes, out var b))
                throw new InvalidOperationException("Request exceeds MaxUnits (small-block path only).");
            return b;
        }

        public void Free(ref HeapBlock block)
        {
            if (!block.IsValid) return;

            ulong szBytes = block.end - block.begin;
            if (szBytes == 0) { block = new HeapBlock(); return; }

            if ((block.begin % (ulong)m_UnitBytes) != 0 || (szBytes % (ulong)m_UnitBytes) != 0)
                throw new InvalidOperationException("Freed block is not aligned to unit size.");

            int startUnit = (int)(block.begin / (ulong)m_UnitBytes);
            int units = (int)(szBytes / (ulong)m_UnitBytes);
            if (units <= 0 || units > m_MaxUnits)
                throw new InvalidOperationException("Freed block size exceeds the small-block upper bound.");

            int nodeIdx = AcquireNodeIndex();
            m_Nodes[nodeIdx] = new Node { startUnit = startUnit, next = m_BucketHead[units] };
            m_BucketHead[units] = nodeIdx;
            m_FreeUnits += units;

            block = new HeapBlock();
        }

        public void Clear()
        {
            for (int i = 0; i < m_BucketHead.Length; i++) m_BucketHead[i] = INVALID;
            m_Nodes.Clear();
            m_FreeNodeHead = INVALID;
            m_UnitsTop = 0;
            m_FreeUnits = 0;
        }

        public void Dispose()
        {
            if (m_BucketHead.IsCreated) m_BucketHead.Dispose();
            if (m_Nodes.IsCreated) m_Nodes.Dispose();
        }
    }

}
