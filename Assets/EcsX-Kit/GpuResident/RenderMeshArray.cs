using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Unity.Assertions;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2022_2_OR_NEWER

namespace Unity.Rendering
{

    public struct MaterialMeshIndex
    {

        public int MaterialIndex;

        public int MeshIndex;

        public int SubMeshIndex;
    }

    public struct MaterialMeshInfo : IComponentData, IEnableableComponent
    {

        public int Material;

        public int Mesh;

        SubMeshIndexInfo32 m_SubMeshIndexInfo;

        public ushort SubMesh
        {
            get => m_SubMeshIndexInfo.SubMesh;
            set => m_SubMeshIndexInfo.SubMesh = value;
        }

        public RangeInt MaterialMeshIndexRange => m_SubMeshIndexInfo.MaterialMeshIndexRangeAsInt;

        public bool HasMaterialMeshIndexRange => m_SubMeshIndexInfo.HasMaterialMeshIndexRange;

        [Obsolete("Use SubMesh instead.", true)]
        public sbyte Submesh { get => (sbyte)SubMesh; set => SubMesh = (ushort)value; }

        public static int ArrayIndexToStaticIndex(int index) => (index < 0)
            ? index
            : (-index - 1);

        public static int StaticIndexToArrayIndex(int staticIndex) => math.abs(staticIndex) - 1;

        public static MaterialMeshInfo FromRenderMeshArrayIndices(int materialIndexInRenderMeshArray,
            int meshIndexInRenderMeshArray,
            ushort submeshIndex = 0) =>
            new(
                ArrayIndexToStaticIndex(materialIndexInRenderMeshArray),
                ArrayIndexToStaticIndex(meshIndexInRenderMeshArray),
                new SubMeshIndexInfo32(submeshIndex)
            );

        public static MaterialMeshInfo FromMaterialMeshIndexRange(int rangeStart, int rangeLength)
            => new(0, 0, new SubMeshIndexInfo32((ushort)rangeStart, (byte)rangeLength));

        MaterialMeshInfo(int material, int mesh, SubMeshIndexInfo32 subMeshIndexInfo)
        {
            Material = material;
            Mesh = mesh;
            m_SubMeshIndexInfo = subMeshIndexInfo;
        }

        public MaterialMeshInfo(BatchMaterialID materialID, BatchMeshID meshID, ushort submeshIndex = 0)
            : this((int)materialID.value, (int)meshID.value, new SubMeshIndexInfo32(submeshIndex)) {}

        public BatchMeshID MeshID
        {
            get
            {
                Assert.IsTrue(IsRuntimeMesh);
                return new BatchMeshID { value = (uint)Mesh };
            }

            set => Mesh = (int) value.value;
        }

        public BatchMaterialID MaterialID
        {
            get
            {
                Assert.IsTrue(IsRuntimeMaterial);
                return new BatchMaterialID() { value = (uint)Material };
            }

            set => Material = (int) value.value;
        }

        internal bool IsRuntimeMaterial => !HasMaterialMeshIndexRange && Material >= 0;

        internal bool IsRuntimeMesh => !HasMaterialMeshIndexRange && Mesh >= 0;

        internal int MeshArrayIndex
        {
            get => IsRuntimeMesh ? -1 : StaticIndexToArrayIndex(Mesh);
            set => Mesh = ArrayIndexToStaticIndex(value);
        }

        internal int MaterialArrayIndex
        {
            get => IsRuntimeMaterial ? -1 : StaticIndexToArrayIndex(Material);
            set => Material = ArrayIndexToStaticIndex(value);
        }
    }

    internal struct AssetHash
    {

        public static void UpdateAsset(ref xxHash3.StreamingState hash, UntypedUnityObjectRef asset)
        {

#if UNITY_EDITOR
            bool success = AssetDatabase.TryGetGUIDAndLocalFileIdentifier(asset.instanceId, out string guid, out long localId);
            hash.Update(success);
            if (!success)
            {
                hash.Update(asset.instanceId);
                return;
            }
            var guidBytes = Encoding.UTF8.GetBytes(guid);

            hash.Update(guidBytes.Length);
            for (int j = 0; j < guidBytes.Length; ++j)
                hash.Update(guidBytes[j]);
            hash.Update(localId);
#else

            hash.Update(asset.instanceId);
#endif
        }
    }

    internal static unsafe class CallFromBurstRenderMeshArrayHelper
    {
        struct Functions
        {
            public IntPtr AddTo;
        }
        internal delegate void AddToDelegate(
            EntityManager* em, ArchetypeChunk* chunks, int chunksLength,
            UnityObjectRef<Material>* materialsPtr, int materialsLength,
            UnityObjectRef<Mesh>* meshesPtr, int meshesLength,
            MaterialMeshIndex* materialMeshIndicesPtr, int materialMeshIndicesLength);
        static AddToDelegate s_AddToGCDefeat;
        static readonly SharedStatic<Functions> k_Functions = SharedStatic<Functions>.GetOrCreate<Functions>();

#if UNITY_EDITOR
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [UnityEditor.InitializeOnLoadMethod]
        public static void EditorInitializeOnLoadMethod() => Init();
#else
        [ExcludeFromBurstCompatTesting("References managed engine API")]
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void RuntimeInitialization() => Init();
#endif

        public static void Init()
        {
            s_AddToGCDefeat = RenderMeshArray.AddTo;
            k_Functions.Data.AddTo = Marshal.GetFunctionPointerForDelegate(s_AddToGCDefeat);
        }

        public static void AddRenderMeshArrayTo(
            EntityManager em, NativeArray<ArchetypeChunk> chunks,
            NativeArray<UnityObjectRef<Material>> materials,
            NativeArray<UnityObjectRef<Mesh>> meshes,
            NativeArray<MaterialMeshIndex> materialMeshIndices)
        {
            ((delegate* unmanaged[Cdecl] <
                EntityManager*,
                ArchetypeChunk*, int,
                UnityObjectRef<Material>*, int,
                UnityObjectRef<Mesh>*, int,
                MaterialMeshIndex*, int,
                void>)k_Functions.Data.AddTo)(
                    &em,
                    (ArchetypeChunk*)chunks.GetUnsafeReadOnlyPtr(), chunks.Length,
                    (UnityObjectRef<Material>*)materials.GetUnsafeReadOnlyPtr(), materials.Length,
                    (UnityObjectRef<Mesh>*)meshes.GetUnsafeReadOnlyPtr(), meshes.Length,
                    (MaterialMeshIndex*)materialMeshIndices.GetUnsafeReadOnlyPtr(), materialMeshIndices.Length);
        }
    }

    public struct RenderMeshArray : ISharedComponentData, IEquatable<RenderMeshArray>
    {
        [SerializeField] private UnityObjectRef<Material>[] m_Materials;
        [SerializeField] private UnityObjectRef<Mesh>[] m_Meshes;
        [SerializeField] private MaterialMeshIndex[] m_MaterialMeshIndices;

        [SerializeField] private uint4 m_Hash128;

        public RenderMeshArray(Material[] materials, Mesh[] meshes, MaterialMeshIndex[] materialMeshIndices = null)
        {
            m_Meshes = new UnityObjectRef<Mesh>[meshes.Length];
            for (int i = 0; i < meshes.Length; i++)
                m_Meshes[i] = meshes[i];

            m_Materials = new UnityObjectRef<Material>[materials.Length];
            for (int i = 0; i < materials.Length; i++)
                m_Materials[i] = materials[i];

            m_MaterialMeshIndices = materialMeshIndices?.ToArray();
            m_Hash128 = uint4.zero;
            ResetHash128();
        }

        [MonoPInvokeCallback(typeof(CallFromBurstRenderMeshArrayHelper.AddToDelegate))]
        internal static unsafe void AddTo(EntityManager* em,
            ArchetypeChunk* chunksPtr, int chunksLength,
            UnityObjectRef<Material>* materialsPtr, int materialsLength,
            UnityObjectRef<Mesh>* meshesPtr, int meshesLength,
            MaterialMeshIndex* materialMeshIndicesPtr, int materialMeshIndicesLength)
        {
            var newArray = new RenderMeshArray(
                new ReadOnlySpan<UnityObjectRef<Material>>(materialsPtr, materialsLength),
                new ReadOnlySpan<UnityObjectRef<Mesh>>(meshesPtr, meshesLength),
                new ReadOnlySpan<MaterialMeshIndex>(materialMeshIndicesPtr, materialMeshIndicesLength));

            var chunkArray = NativeArrayUnsafeUtility.ConvertExistingDataToNativeArray<ArchetypeChunk>(chunksPtr, chunksLength, Allocator.None);
#if ENABLE_UNITY_COLLECTIONS_CHECKS
            NativeArrayUnsafeUtility.SetAtomicSafetyHandle(ref chunkArray, AtomicSafetyHandle.GetTempMemoryHandle());
#endif
            (*em).AddSharedComponentManaged(chunkArray, newArray);
        }

        public RenderMeshArray(ReadOnlySpan<UnityObjectRef<Material>> materials, ReadOnlySpan<UnityObjectRef<Mesh>> meshes, ReadOnlySpan<MaterialMeshIndex> materialMeshIndices = default)
        {
            m_Meshes = meshes.ToArray();
            m_Materials = materials.ToArray();
            m_MaterialMeshIndices = materialMeshIndices.ToArray();
            m_Hash128 = uint4.zero;
            ResetHash128();
        }

        public UnityObjectRef<Mesh>[] MeshReferences
        {
            get => m_Meshes;
            set
            {
                m_Hash128 = uint4.zero;
                m_Meshes = value;
            }
        }

        public UnityObjectRef<Material>[] MaterialReferences
        {
            get => m_Materials;
            set
            {
                m_Hash128 = uint4.zero;
                m_Materials = value;
            }
        }

        public MaterialMeshIndex[] MaterialMeshIndices
        {
            get => m_MaterialMeshIndices;
            set
            {
                m_Hash128 = uint4.zero;
                m_MaterialMeshIndices = value;
            }
        }

        [Obsolete("Meshes has been deprecated; use MeshReferences instead.", false)]
        public Mesh[] Meshes
        {
            get
            {
                if (m_Meshes == null)
                    return null;
                var meshesArray = new Mesh[m_Meshes.Length];
                for (int i = 0; i < m_Meshes.Length; i++)
                {
                    meshesArray[i] = m_Meshes[i].Value;
                }
                return meshesArray;
            }
            set
            {
                m_Hash128 = uint4.zero;

                m_Meshes = new UnityObjectRef<Mesh>[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    m_Meshes[i] = value[i];
                }
            }
        }

        [Obsolete("Materials has been deprecated; use MaterialReferences instead.", false)]
        public Material[] Materials
        {
            get
            {
                if (m_Materials == null)
                    return null;
                var materialsArray = new Material[m_Materials.Length];
                for (int i = 0; i < m_Materials.Length; i++)
                {
                    materialsArray[i] = m_Materials[i].Value;
                }
                return materialsArray;
            }
            set
            {
                m_Hash128 = uint4.zero;

                m_Materials = new UnityObjectRef<Material>[value.Length];
                for (int i = 0; i < value.Length; i++)
                {
                    m_Materials[i] = value[i];
                }
            }
        }

        internal Mesh GetMeshWithStaticIndex(int staticMeshIndex)
        {
            Assert.IsTrue(staticMeshIndex <= 0, "Mesh index must be a static index (non-positive)");

            if (staticMeshIndex >= 0)
                return null;

            return m_Meshes[MaterialMeshInfo.StaticIndexToArrayIndex(staticMeshIndex)];
        }

        internal Material GetMaterialWithStaticIndex(int staticMaterialIndex)
        {
            Assert.IsTrue(staticMaterialIndex <= 0, "Material index must be a static index (non-positive)");

            if (staticMaterialIndex >= 0)
                return null;

            return m_Materials[MaterialMeshInfo.StaticIndexToArrayIndex(staticMaterialIndex)];
        }

        public uint4 GetHash128()
        {
            return m_Hash128;
        }

        public void ResetHash128()
        {
            m_Hash128 = ComputeHash128();
        }

        public uint4 ComputeHash128()
        {
            var hash = new xxHash3.StreamingState(false);

            int numMeshes = m_Meshes?.Length ?? 0;
            int numMaterials = m_Materials?.Length ?? 0;
            int numMatMeshIndices = m_MaterialMeshIndices?.Length ?? 0;

            hash.Update(numMeshes);
            hash.Update(numMaterials);
            hash.Update(numMatMeshIndices);

            for (int i = 0; i < numMeshes; ++i)
                AssetHash.UpdateAsset(ref hash, m_Meshes[i].Id);

            for (int i = 0; i < numMaterials; ++i)
                AssetHash.UpdateAsset(ref hash, m_Materials[i].Id);

            for (int i = 0; i < numMatMeshIndices; ++i)
            {
                MaterialMeshIndex matMeshIndex = m_MaterialMeshIndices[i];
                hash.Update(matMeshIndex.MaterialIndex);
                hash.Update(matMeshIndex.MeshIndex);
                hash.Update(matMeshIndex.SubMeshIndex);
            }

            uint4 H = hash.DigestHash128();

            if (math.all(H == uint4.zero))
                return new uint4(1, 0, 0, 0);

            return H;
        }

        public static RenderMeshArray CombineRenderMeshes(List<RenderMeshUnmanaged> renderMeshes)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(renderMeshes.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(renderMeshes.Count);

            foreach (var renderMesh in renderMeshes)
            {
                meshes[renderMesh.mesh] = true;
                if (renderMesh.materialForSubMesh != null)
                    materials[renderMesh.materialForSubMesh] = true;
            }

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        public static RenderMeshArray CombineRenderMeshArrays(List<RenderMeshArray> renderMeshArrays)
        {
            int totalMeshes = 0;
            int totalMaterials = 0;

            foreach (var rma in renderMeshArrays)
            {
                totalMeshes += rma.MeshReferences?.Length ?? 0;
                totalMaterials += rma.MeshReferences?.Length ?? 0;
            }

            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(totalMeshes);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(totalMaterials);

            foreach (var rma in renderMeshArrays)
            {
                foreach (var mesh in rma.MeshReferences)
                {
                    if (mesh.IsValid())
                        meshes[mesh] = true;
                }

                foreach (var material in rma.MaterialReferences)
                {
                    if (material.IsValid())
                        materials[material] = true;
                }
            }

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        public static RenderMeshArray CreateWithDeduplication(
            List<Material> materialsWithDuplicates, List<Mesh> meshesWithDuplicates)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(meshesWithDuplicates.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(materialsWithDuplicates.Count);

            foreach (var mat in materialsWithDuplicates)
                materials[mat] = true;

            foreach (var mesh in meshesWithDuplicates)
                meshes[mesh] = true;

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        public static RenderMeshArray CreateWithDeduplication(
            List<UnityObjectRef<Material>> materialsWithDuplicates, List<UnityObjectRef<Mesh>> meshesWithDuplicates)
        {
            var meshes = new Dictionary<UnityObjectRef<Mesh>, bool>(meshesWithDuplicates.Count);
            var materials = new Dictionary<UnityObjectRef<Material>, bool>(materialsWithDuplicates.Count);

            foreach (var mat in materialsWithDuplicates)
                materials[mat] = true;

            foreach (var mesh in meshesWithDuplicates)
                meshes[mesh] = true;

            return new RenderMeshArray(materials.Keys.ToArray(), meshes.Keys.ToArray());
        }

        public Material GetMaterial(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMaterial)
                return null;

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                int firstMaterialIndex = MaterialMeshIndices[range.start].MaterialIndex;
                return MaterialReferences[firstMaterialIndex];
            }
            else
            {
                return MaterialReferences[materialMeshInfo.MaterialArrayIndex];
            }
        }

        public List<Material> GetMaterials(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMaterial)
                return null;

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                var materials = new List<Material>(range.length);

                for (int i = range.start; i < range.end; i++)
                {
                    int materialIndex = MaterialMeshIndices[i].MaterialIndex;
                    materials.Add(MaterialReferences[materialIndex]);
                }

                return materials;
            }
            else
            {
                var material = MaterialReferences[materialMeshInfo.MaterialArrayIndex];
                return new List<Material> { material };
            }
        }

        public Mesh GetMesh(MaterialMeshInfo materialMeshInfo)
        {
            if (materialMeshInfo.IsRuntimeMesh)
                return null;

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                int firstMeshIndex = MaterialMeshIndices[range.start].MeshIndex;
                return MeshReferences[firstMeshIndex];
            }
            else
            {
                return MeshReferences[materialMeshInfo.MeshArrayIndex];
            }
        }

        public bool Equals(RenderMeshArray other)
        {
            return math.all(GetHash128() == other.GetHash128());
        }

        public override bool Equals(object obj)
        {
            return obj is RenderMeshArray other && Equals(other);
        }

        public override int GetHashCode()
        {
            return (int) GetHash128().x;
        }

        public static bool operator ==(RenderMeshArray left, RenderMeshArray right)
        {
            return left.Equals(right);
        }

        public static bool operator !=(RenderMeshArray left, RenderMeshArray right)
        {
            return !left.Equals(right);
        }
    }
}

#endif