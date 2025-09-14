using UnityEngine;
using UnityEngine.Jobs;
using Unity.Jobs;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using System;
using Unity.Mathematics;

namespace Unity.Entities
{

    public unsafe class TransformAccessChunk : IDisposable
    {
        internal TransformAccessArray m_TransformAccessArray;

        int m_NextFreeEntityIndex;

        NativeList<int2> m_EntityInChunByEntity;
        NativeList<TransformAccessEntity> m_TransEntities;
        public NativeList<Entity> m_Entities;
        public NativeList<LocalTransform> m_PreviousTransforms;

        public int Count { get { return m_TransEntities.Length; } }

        public int2* GetEntityToArrayIndices() { return (int2*)m_EntityInChunByEntity.GetUnsafePtr(); }

        public TransformAccessChunk(int capacity)
        {
            m_TransformAccessArray = new TransformAccessArray(capacity);
            m_TransEntities = new NativeList<TransformAccessEntity>(capacity, Allocator.Persistent);
            m_EntityInChunByEntity = new NativeList<int2>(capacity, Allocator.Persistent);
            m_Entities = new NativeList<Entity> (capacity, Allocator.Persistent);
            m_PreviousTransforms = new NativeList<LocalTransform> (capacity, Allocator.Persistent);

            m_NextFreeEntityIndex = -1;
        }
        static internal TransformAccessChunk Create(int newCapacity = 64)
        {
            TransformAccessChunk chunk = new TransformAccessChunk(newCapacity);
            return chunk;
        }

        public TransformAccessEntity AddTransformEntity(Transform transform, in Entity entity)
        {
            int arrayIndex = m_TransEntities.Length;

            int entityIndex;
            int version = 1;
            if (m_NextFreeEntityIndex == -1)
            {
                entityIndex = m_EntityInChunByEntity.Length;
                m_EntityInChunByEntity.Add(new int2(arrayIndex, version));

            }
            else
            {
                entityIndex = m_NextFreeEntityIndex;
                m_NextFreeEntityIndex = m_EntityInChunByEntity[entityIndex].x;
                version = m_EntityInChunByEntity[entityIndex].y + 1;
                m_EntityInChunByEntity[entityIndex] = new int2(arrayIndex, version);
            }

            var tentity = new TransformAccessEntity { Index = entityIndex, Version = version };

            m_TransEntities.Add(tentity);
            m_TransformAccessArray.Add(transform);
            m_Entities.Add(entity);

            m_PreviousTransforms.Add(LocalTransform.Invalid);

            return tentity;
        }

        public void SetDirty(in TransformAccessEntity entity)
        {
            if (entity == TransformAccessEntity.Null)
                return;

            int index = entity.Index;
            if (index >= m_EntityInChunByEntity.Length)
            {
                return;
            }
            int2* entityInChunByEntity = (int2*)m_EntityInChunByEntity.GetUnsafePtr();

            int2* item = entityInChunByEntity + index;
            int arrayIndex = item->x;
            int version = item->y;

            if (version != entity.Version)
                return;
            m_PreviousTransforms[arrayIndex] = LocalTransform.Invalid;
        }

        public void RemoveTransformEntity(in TransformAccessEntity entity)
        {
            if (entity == TransformAccessEntity.Null)
                return;

            int freeIndex = entity.Index;
            if (freeIndex >= m_EntityInChunByEntity.Length )
            {
                return;
            }
            int2* entityInChunByEntity = (int2*)m_EntityInChunByEntity.GetUnsafePtr();

            int2* item = entityInChunByEntity + freeIndex;
            int arrayIndex = item->x;
            int version = item->y;

            if (version != entity.Version)
                return;

            item->x = m_NextFreeEntityIndex;
            item->y = version + 1;

            m_NextFreeEntityIndex = freeIndex;

            if (arrayIndex != Count - 1)
            {
                entityInChunByEntity[m_TransEntities[Count - 1].Index].x = arrayIndex;
            }

            m_TransEntities.RemoveAtSwapBack(arrayIndex);
            m_TransformAccessArray.RemoveAtSwapBack(arrayIndex);
            m_Entities.RemoveAtSwapBack(arrayIndex);
            m_PreviousTransforms.RemoveAtSwapBack(arrayIndex);
        }

        public void Dispose()
        {
            if (m_TransformAccessArray.isCreated)
                m_TransformAccessArray.Dispose();
            if (m_EntityInChunByEntity.IsCreated)
                m_EntityInChunByEntity.Dispose();
            if (m_TransEntities.IsCreated)
                m_TransEntities.Dispose();

            m_Entities.Dispose();
            m_PreviousTransforms.Dispose();
        }

    }
}
