using System;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
using UnityEngine.Rendering;

namespace Unity.Entities
{
    public struct RenderBounds : IComponentData
    {
        public AABB Value;
    }

    public struct WorldRenderBounds : IComponentData
    {
        public AABB Value;
    }

    public struct ChunkWorldRenderBounds : IComponentData
    {
        public AABB Value;
    }

    public struct RenderMesh : ISharedComponentData, IEquatable<RenderMesh>
    {
        public UnityObjectRef<Mesh> mesh;
        public UnityObjectRef<Material> material;

        public int subMesh;
        public int layer;

        public ShadowCastingMode castShadows;

        public bool Equals(RenderMesh other)
        {
            return
                mesh == other.mesh &&
                material == other.material &&
                layer == other.layer &&
                subMesh == other.subMesh &&
            castShadows == other.castShadows;
        }

        public override int GetHashCode()
        {
            int hash = 0;
            if (!ReferenceEquals(mesh, null)) hash ^= mesh.GetHashCode();
            if (!ReferenceEquals(material, null)) hash ^= material.GetHashCode();
            hash ^= layer.GetHashCode();
            hash ^= layer.GetHashCode();
            hash ^= castShadows.GetHashCode();
            return hash;
        }
    }

    public struct NotPerInstanceCullingTag : IComponentData { }

    struct LodRequirement : IComponentData
    {
        public float MinDist;
        public float MaxDist;

    }

    public struct DisableRendering : IComponentData
    {
    }

    [MaterialProperty("_Color32")]
    public struct MaterialColor32 : IComponentData
    {
        public float Value;
    }

    [MaterialProperty("_CbFloat4")]
    public struct MaterialFloat4 : IComponentData
    {
        public float4 Value;
    }
    internal struct EditorRenderData : ISharedComponentData, IEquatable<EditorRenderData>
    {
        public ulong SceneCullingMask;
        public bool Equals(EditorRenderData other) => SceneCullingMask == other.SceneCullingMask;
        public override int GetHashCode() => SceneCullingMask.GetHashCode();
    }
#if UNITY_2022_2_OR_NEWER
    public struct PlanarShadow : IComponentData, IEnableableComponent { }

    public struct ChunkSection : ISharedComponentData, IEquatable<ChunkSection>
    {
        public int Section;
        public bool Equals(ChunkSection other)
        {
            return Section == other.Section;
        }
        public override int GetHashCode() => Section;
    }
#endif

}