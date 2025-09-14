using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_2022_2_OR_NEWER

namespace Unity.Rendering
{

    internal struct ArchetypePropertyOverride : IEquatable<ArchetypePropertyOverride>, IComparable<ArchetypePropertyOverride>
    {

        public int NameID;

        public int TypeIndex;

        public short SizeBytesCPU;

        public short SizeBytesGPU;

        public bool Equals(ArchetypePropertyOverride other)
        {
            return CompareTo(other) == 0;
        }

        public int CompareTo(ArchetypePropertyOverride other)
        {
            int cmp_NameID = NameID.CompareTo(other.NameID);
            int cmp_TypeIndex = TypeIndex.CompareTo(other.TypeIndex);

            if (cmp_NameID != 0) return cmp_NameID;
            return cmp_TypeIndex;
        }
    }

    internal unsafe struct GraphicsArchetype : IDisposable, IEquatable<GraphicsArchetype>, IComparable<GraphicsArchetype>
    {

        public UnsafeList<ArchetypePropertyOverride> PropertyComponents;

        public GraphicsArchetype Clone(Allocator allocator)
        {
            var overrides = new UnsafeList<ArchetypePropertyOverride>(PropertyComponents.Length, allocator);
            overrides.AddRangeNoResize(PropertyComponents);

            return new GraphicsArchetype
            {
                PropertyComponents = overrides,
            };
        }

        public int MaxEntitiesPerBatch
        {
            get
            {
                int fixedBytes = 0;
                int bytesPerEntity = 0;

                for (int i = 0; i < PropertyComponents.Length; ++i)
                    bytesPerEntity += PropertyComponents[i].SizeBytesGPU;

                int maxBytes = EntitiesGraphicsSystem.MaxBytesPerBatch;
                int maxBytesForEntities = maxBytes - fixedBytes;

                return maxBytesForEntities / math.max(1, bytesPerEntity);
            }
        }

        public int MaxEntitiesPerCBufferBatch
        {
            get
            {
                int fixedBytes = 0;
                int bytesPerEntity = 0;

                for (int i = 0; i < PropertyComponents.Length; ++i)
                    bytesPerEntity += PropertyComponents[i].SizeBytesGPU;

                int maxBytes = EntitiesGraphicsSystem.MaxBytesPerCBuffer;
                int maxBytesForEntities = maxBytes - fixedBytes;

                return (maxBytesForEntities - 16 * PropertyComponents.Length) / math.max(1, bytesPerEntity);
            }
        }

        public bool Equals(GraphicsArchetype other)
        {
            return CompareTo(other) == 0;
        }

        public int CompareTo(GraphicsArchetype other)
        {
            int numA = PropertyComponents.Length;
            int numB = other.PropertyComponents.Length;

            if (numA < numB) return -1;
            if (numA > numB) return 1;

            return UnsafeUtility.MemCmp(
                PropertyComponents.Ptr,
                other.PropertyComponents.Ptr,
                numA * UnsafeUtility.SizeOf<ArchetypePropertyOverride>());
        }

        public override int GetHashCode()
        {
            return (int)xxHash3.Hash64(
                PropertyComponents.Ptr,
                PropertyComponents.Length * UnsafeUtility.SizeOf<ArchetypePropertyOverride>()).x;
        }

        public void Dispose()
        {
            if (PropertyComponents.IsCreated) PropertyComponents.Dispose();
        }

        public struct MetadataValueComparer : IComparer<MetadataValue>
        {
            public int Compare(MetadataValue x, MetadataValue y)
            {
                return x.NameID.CompareTo(y.NameID);
            }
        }
    }

    internal struct EntitiesGraphicsArchetypes : IDisposable
    {
        private NativeParallelHashMap<EntityArchetype, int> m_GraphicsArchetypes;
        private NativeParallelHashMap<GraphicsArchetype, int> m_GraphicsArchetypeDeduplication;
        private NativeList<GraphicsArchetype> m_GraphicsArchetypeList;

        public EntitiesGraphicsArchetypes(int capacity)
        {
            m_GraphicsArchetypes = new NativeParallelHashMap<EntityArchetype, int>(capacity, Allocator.Persistent);
            m_GraphicsArchetypeDeduplication =
                new NativeParallelHashMap<GraphicsArchetype, int>(capacity, Allocator.Persistent);
            m_GraphicsArchetypeList = new NativeList<GraphicsArchetype>(capacity, Allocator.Persistent);
        }

        public void Dispose()
        {
            for (int i = 0; i < m_GraphicsArchetypeList.Length; ++i)
                m_GraphicsArchetypeList[i].Dispose();

            m_GraphicsArchetypes.Dispose();
            m_GraphicsArchetypeDeduplication.Dispose();
            m_GraphicsArchetypeList.Dispose();
        }

        public GraphicsArchetype GetGraphicsArchetype(int index) => m_GraphicsArchetypeList[index];

        public int GetGraphicsArchetypeIndex(
            EntityArchetype archetype,
            NativeParallelHashMap<int, MaterialPropertyType> typeIndexToMaterialProperty, ref MaterialPropertyType failureProperty)
        {
            int archetypeIndex;
            if (m_GraphicsArchetypes.TryGetValue(archetype, out archetypeIndex))
                return archetypeIndex;

            var types = archetype.GetComponentTypes(Allocator.Temp);

            var overrides = new UnsafeList<ArchetypePropertyOverride>(types.Length, Allocator.Temp);
            bool AddOverrideForType(ComponentType type)
            {
                if (typeIndexToMaterialProperty.TryGetValue(type.TypeIndex, out var property))
                {
                    if (type.TypeIndex != property.TypeIndex)
                        return false;

                    overrides.Add(new ArchetypePropertyOverride
                    {
                        NameID = property.NameID,
                        TypeIndex = property.TypeIndex,
                        SizeBytesCPU = property.SizeBytesCPU,
                        SizeBytesGPU = property.SizeBytesGPU,
                    });
                }

                return true;

            }

            for (int i = 0; i < types.Length; ++i)
            {
                if (!AddOverrideForType(types[i]))
                {
                    typeIndexToMaterialProperty.TryGetValue(types[i].TypeIndex, out failureProperty);
                    return -1;
                }
            }

            AddOverrideForType(ComponentType.ReadOnly<Entity>());

            overrides.Sort();

            GraphicsArchetype graphicsArchetype = new GraphicsArchetype
            {
                PropertyComponents = overrides,
            };

            if (m_GraphicsArchetypeDeduplication.TryGetValue(graphicsArchetype, out archetypeIndex))
            {
                graphicsArchetype.Dispose();
                return archetypeIndex;
            }

            else
            {
                archetypeIndex = m_GraphicsArchetypeList.Length;
                graphicsArchetype = graphicsArchetype.Clone(Allocator.Persistent);
                overrides.Dispose();

                m_GraphicsArchetypeDeduplication[graphicsArchetype] = archetypeIndex;
                m_GraphicsArchetypeList.Add(graphicsArchetype);
                return archetypeIndex;
            }
        }
    }

}

#endif