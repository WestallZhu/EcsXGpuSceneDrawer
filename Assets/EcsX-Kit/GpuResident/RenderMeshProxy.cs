using System;
using System.Collections.Generic;
using Unity.Assertions;
using Unity.Core;
using Unity.Entities;
using UnityEngine;

namespace Unity.Rendering
{

    [Serializable]

    public struct RenderMeshUnmanaged :IComponentData, IEquatable<RenderMeshUnmanaged>
    {

        public UnityObjectRef<Mesh> mesh;

        internal SubMeshIndexInfo32 subMeshInfo;

        public UnityObjectRef<Material> materialForSubMesh;

        public RenderMeshUnmanaged(
            UnityObjectRef<Mesh> mesh,
            UnityObjectRef<Material> materialForSubMesh = default,
            int subMeshIndex = default)
        {
            Assert.IsTrue(mesh != null, "Must have a non-null Mesh to create RenderMesh.");

            this.mesh = mesh;
            this.materialForSubMesh = materialForSubMesh;
            this.subMeshInfo = new SubMeshIndexInfo32((ushort)subMeshIndex);
        }

        internal RenderMeshUnmanaged(
            UnityObjectRef<Mesh> mesh,
            UnityObjectRef<Material> materialForSubMesh = default,
            SubMeshIndexInfo32 subMeshInfo = default)
        {
            Assert.IsTrue(mesh != null, "Must have a non-null Mesh to create RenderMesh.");

            this.mesh = mesh;
            this.materialForSubMesh = materialForSubMesh;
            this.subMeshInfo = subMeshInfo;
        }

        public bool Equals(RenderMeshUnmanaged other)
        {
            return
                mesh == other.mesh &&
                materialForSubMesh == other.materialForSubMesh &&
                subMeshInfo == other.subMeshInfo;
        }

        public override int GetHashCode()
        {
            int hash = 0;

            unsafe
            {
                var buffer = stackalloc[]
                {
                    ReferenceEquals(mesh, null) ? 0 : mesh.GetHashCode(),
                    ReferenceEquals(materialForSubMesh, null) ? 0 : materialForSubMesh.GetHashCode(),
                    subMeshInfo.GetHashCode(),
                };

                hash = (int)XXHash.Hash32((byte*)buffer, 3 * 4);
            }

            return hash;
        }
    }

}
