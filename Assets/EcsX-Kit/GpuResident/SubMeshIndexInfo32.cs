using System;
using System.Runtime.CompilerServices;
using Unity.Assertions;
using UnityEngine;

namespace Unity.Rendering
{

    internal struct SubMeshIndexInfo32
    {

        uint m_Value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public SubMeshIndexInfo32(ushort subMeshIndex) => m_Value = subMeshIndex;

        public SubMeshIndexInfo32(ushort rangeStartIndex, byte rangeLength)
        {
            Assert.IsTrue(rangeLength < (1 << 7), $"{nameof(rangeLength)} must be 7bits or less");

            var rangeStartIndexU32 = (uint)rangeStartIndex;
            var rangeLengthU32 = (uint)rangeLength;

            var rangeStartIndexMask = rangeStartIndexU32 & 0xfffff;
            var rangeLengthMask = (rangeLengthU32 & 0x7f) << 20;
            var infoMask = 0x80000000;

            m_Value = rangeStartIndexMask | rangeLengthMask | infoMask;
        }

        public ushort SubMesh
        {
            get => ExtractSubMeshIndex();
            set => m_Value = new SubMeshIndexInfo32(value).m_Value;
        }

        public (ushort start, byte length) MaterialMeshIndexRange =>
        (
            ExtractMaterialMeshIndexRangeStart(),
            ExtractMaterialMeshIndexRangeLength()
        );

        public RangeInt MaterialMeshIndexRangeAsInt => new RangeInt()
        {
            start = ExtractMaterialMeshIndexRangeStart(),
            length = ExtractMaterialMeshIndexRangeLength(),
        };

        public bool HasMaterialMeshIndexRange => HasMaterialMeshIndexRangeBit();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ushort ExtractSubMeshIndex()
        {
            return (ushort)(m_Value & 0xff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        ushort ExtractMaterialMeshIndexRangeStart()
        {
            Assert.IsTrue(HasMaterialMeshIndexRangeBit(), "MaterialMeshIndexRange is only valid when HasMaterialMeshIndexRange is true");
            return (ushort)(m_Value & 0xfffff);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        byte ExtractMaterialMeshIndexRangeLength()
        {
            Assert.IsTrue(HasMaterialMeshIndexRangeBit(), "MaterialMeshIndexRange is only valid when HasMaterialMeshIndexRange is true");
            return (byte)((m_Value >> 20) & 0x7f);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        bool HasMaterialMeshIndexRangeBit()
        {
            return (m_Value & 0x80000000) != 0;
        }

        public bool Equals(SubMeshIndexInfo32 other) => m_Value == other.m_Value;

        public override bool Equals(object obj) => obj is SubMeshIndexInfo32 other && Equals(other);

        public override int GetHashCode() => (int)m_Value;

        public static bool operator ==(SubMeshIndexInfo32 left, SubMeshIndexInfo32 right) => left.Equals(right);

        public static bool operator !=(SubMeshIndexInfo32 left, SubMeshIndexInfo32 right) => !left.Equals(right);

        public override string ToString() => HasMaterialMeshIndexRangeBit()
            ? $"MaterialMeshIndexRange: From: {MaterialMeshIndexRange.start}, To: {MaterialMeshIndexRange.start + MaterialMeshIndexRange.length}"
            : $"SubMesh: {SubMesh}";
    }
}
