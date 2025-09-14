using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Collections;
using Unity.Burst;

namespace Unity.Entities
{
    [Serializable]
    public struct LocalToWorld : IComponentData
    {
        public float4x4 Value;

        public float3 Right => new float3(Value.c0.x, Value.c0.y, Value.c0.z);
        public float3 Up => new float3(Value.c1.x, Value.c1.y, Value.c1.z);
        public float3 Forward => new float3(Value.c2.x, Value.c2.y, Value.c2.z);
        public float3 Position => new float3(Value.c3.x, Value.c3.y, Value.c3.z);

        public quaternion Rotation => new quaternion(Value);
    }

    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    [WriteGroup(typeof(LocalToParent))]
    public struct Rotation : IComponentData
    {
        public quaternion Value;
    }

    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    [WriteGroup(typeof(LocalToParent))]
    public struct Scale : IComponentData
    {
        public float Value;
    }

    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    [WriteGroup(typeof(LocalToParent))]
    public struct NonUniformScale : IComponentData
    {
        public float3 Value;
    }

    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    [WriteGroup(typeof(LocalToParent))]
    public struct Translation : IComponentData
    {
        public float3 Value;
    }


    public struct TransformAccessEntity : IComponentData, IEquatable<TransformAccessEntity>
    {
        public int Index;
        public int Version;

        public static TransformAccessEntity Null => new TransformAccessEntity();

        public static bool operator ==(TransformAccessEntity lhs, TransformAccessEntity rhs)
        {
            return lhs.Index == rhs.Index && lhs.Version == rhs.Version;
        }
        public static bool operator !=(TransformAccessEntity lhs, TransformAccessEntity rhs)
        {
            return !(lhs == rhs);
        }
        public override bool Equals(object compare)
        {
            return this == (TransformAccessEntity)compare;
        }
        public bool Equals(TransformAccessEntity entity)
        {
            return entity.Index == Index && entity.Version == Version;
        }
        public override int GetHashCode()
        {
            return Index;
        }
    }

    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    public struct Parent : IComponentData
    {
        public Entity Value;
    }



    [Serializable]
    [InternalBufferCapacity(8)]
    [WriteGroup(typeof(LocalToParent))]
    public struct Child : ICleanupBufferElementData
    {
        public Entity Value;
    }


    [Serializable]
    [WriteGroup(typeof(LocalToWorld))]
    public struct LocalToParent : IComponentData
    {
        public float4x4 Value;

        public float3 Right => new float3(Value.c0.x, Value.c0.y, Value.c0.z);
        public float3 Up => new float3(Value.c1.x, Value.c1.y, Value.c1.z);
        public float3 Forward => new float3(Value.c2.x, Value.c2.y, Value.c2.z);
        public float3 Position => new float3(Value.c3.x, Value.c3.y, Value.c3.z);
    }


    /// <summary>
    /// An optional transformation matrix used to implement non-affine
    /// transformation effects such as non-uniform scale.
    /// </summary>
    /// <remarks>
    /// If this component is present, it is applied to the entity's <see cref="LocalToWorld"/> matrix
    /// by the <see cref="LocalToWorldSystem"/>.
    ///
    /// If a system writes to an entity's <see cref="LocalToWorld"/> using a <see cref="WriteGroupAttribute"/>,
    /// it is also responsible for applying this matrix if it is present.
    /// </remarks>
    public struct PostTransformMatrix : IComponentData
    {
        /// <summary>
        /// The post-transform scale matrix
        /// </summary>
        public float4x4 Value;
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    public partial class TransformSystemGroup : ComponentSystemGroup
    {
    }

}
