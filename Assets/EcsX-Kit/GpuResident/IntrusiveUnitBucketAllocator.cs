using System;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;

namespace Unity.Rendering
{

    public unsafe class IntrusiveUnitBucketAllocator<TUnit> : IDisposable
    where TUnit : unmanaged
    {
        const int INVALID = -1;

        readonly int m_UnitBytes;
        readonly int m_MaxUnits;

        internal NativeList<TUnit> m_Buffer;
        NativeArray<int> m_BucketHead;
        int m_UnitsTop;
        long m_FreeUnits;

        public IntrusiveUnitBucketAllocator(
            int maxUnits = 64,
            int initialUnits = 0,
            Allocator allocator = Allocator.Persistent)
        {
            m_UnitBytes = UnsafeUtility.SizeOf<TUnit>();
            if (m_UnitBytes < 4)
                throw new ArgumentException("sizeof(TUnit) must be >= 4 to store int next in the first unit of a free block.");

            if (maxUnits <= 0) throw new ArgumentOutOfRangeException(nameof(maxUnits));

            m_MaxUnits = maxUnits;
            m_Buffer = new NativeList<TUnit>(Math.Max(16, initialUnits), allocator);
            if (initialUnits > 0) m_Buffer.ResizeUninitialized(initialUnits);

            m_BucketHead = new NativeArray<int>(m_MaxUnits + 1, allocator);
            for (int i = 0; i < m_BucketHead.Length; i++) m_BucketHead[i] = INVALID;

            m_UnitsTop = initialUnits;
            m_FreeUnits = 0;
        }

        public bool IsCreated => m_Buffer.IsCreated && m_BucketHead.IsCreated;
        public int UnitBytes => m_UnitBytes;
        public int MaxUnits => m_MaxUnits;
        public int CapacityUnits => m_UnitsTop;
        public ulong CapacityBytes => (ulong)m_UnitsTop * (ulong)m_UnitBytes;
        public ulong ActiveBytes => (ulong)(m_UnitsTop - (int)m_FreeUnits) * (ulong)m_UnitBytes;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int BytesToUnits(ulong bytes) => (int)((bytes + (ulong)(m_UnitBytes - 1)) / (ulong)m_UnitBytes);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ulong UnitsToBytes(int units) => (ulong)units * (ulong)m_UnitBytes;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void EnsureCapacityUnits(int units)
        {
            if (units > m_Buffer.Length)
                m_Buffer.ResizeUninitialized(units);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int* NextPtrAtStartUnit(int startUnit)
        {

            byte* basePtr = (byte*)m_Buffer.GetUnsafePtr();
            return (int*)(basePtr + (nint)startUnit * m_UnitBytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        int ReadNext(int startUnit) => *NextPtrAtStartUnit(startUnit);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void WriteNext(int startUnit, int next) => *NextPtrAtStartUnit(startUnit) = next;

        public bool TryAllocateBytes(ulong bytes, out HeapBlock block)
        {
            int units = BytesToUnits(bytes);
            return TryAllocateUnits(units, out block);
        }

        public bool TryAllocateUnits(int units, out HeapBlock block)
        {
            block = new HeapBlock();
            if (units <= 0 || units > m_MaxUnits) return false;

            int headStartUnit = m_BucketHead[units];
            int startUnit;

            if (headStartUnit != INVALID)
            {

                startUnit = headStartUnit;
                int next = ReadNext(headStartUnit);
                m_BucketHead[units] = next;
                m_FreeUnits -= units;
            }
            else
            {

                startUnit = m_UnitsTop;
                m_UnitsTop += units;
                EnsureCapacityUnits(m_UnitsTop);
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
                throw new InvalidOperationException("Freed block size exceeds MaxUnits.");

            WriteNext(startUnit, m_BucketHead[units]);
            m_BucketHead[units] = startUnit;
            m_FreeUnits += units;

            block = new HeapBlock();
        }

        public void Clear()
        {
            for (int i = 0; i < m_BucketHead.Length; i++) m_BucketHead[i] = INVALID;
            m_UnitsTop = 0;
            m_FreeUnits = 0;

        }

        public void Dispose()
        {
            if (m_Buffer.IsCreated) m_Buffer.Dispose();
            if (m_BucketHead.IsCreated) m_BucketHead.Dispose();
        }
    }
}
