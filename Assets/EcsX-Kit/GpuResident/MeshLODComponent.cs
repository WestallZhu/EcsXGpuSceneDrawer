using Unity.Entities;
using Unity.Mathematics;

namespace Unity.Rendering
{

    public struct MeshLODGroupComponent : IComponentData
    {

        public Entity    ParentGroup;

        public int       ParentMask;

        public float4    LODDistances0;

        public float4    LODDistances1;

        public float3    LocalReferencePoint;
    }

    internal struct LODGroupWorldReferencePoint : IComponentData
    {

        public float3 Value;
    }

    internal struct SkipLODGroupWorldReferencePointUpdate : IComponentData
    {

    }

    public struct MeshLODComponent : IComponentData
    {

        public Entity   Group;

        public Entity   ParentGroup;

        public int      LODMask;
    }
}
