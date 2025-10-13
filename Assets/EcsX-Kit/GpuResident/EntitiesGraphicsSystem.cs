#if UNITY_2022_2_OR_NEWER

#if UNITY_EDITOR
#define USE_PROPERTY_ASSERTS
#endif

#if UNITY_EDITOR
#define DEBUG_PROPERTY_NAMES
#endif

#if UNITY_EDITOR && !DISABLE_HYBRID_RENDERER_PICKING
#define ENABLE_PICKING
#endif

#if (ENABLE_UNITY_COLLECTIONS_CHECKS || DEVELOPMENT_BUILD) && !DISABLE_MATERIALMESHINFO_BOUNDS_CHECKING
#define ENABLE_MATERIALMESHINFO_BOUNDS_CHECKING
#endif

#define ENABLE_BATCH_OPTIMIZATION

using System;
using System.Collections.Generic;
using System.Text;
using Unity.Assertions;
using Unity.Burst;
using Unity.Burst.Intrinsics;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Jobs;
using Unity.Jobs.LowLevel.Unsafe;
using Unity.Mathematics;
using Unity.Profiling;
using Unity.Entities.Graphics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

#if URP_10_0_0_OR_NEWER && UNITY_EDITOR
using System.Reflection;
using UnityEngine.Rendering.Universal;
using static Unity.Rendering.GpuUploadOperation;
using System.Security.Cryptography;

#endif

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Unity.Rendering
{

    internal struct NamedPropertyMapping
    {
        public string Name;
        public short SizeCPU;
        public short SizeGPU;
    }

    internal struct EntitiesGraphicsTuningConstants
    {
        public const int kMaxInstancesPerDrawCommand = 4096;
        public const int kMaxInstancesPerDrawRange   = 4096;
        public const int kMaxDrawCommandsPerDrawRange = 512;
    }

    internal struct BatchCreateInfo : IEquatable<BatchCreateInfo>, IComparable<BatchCreateInfo>
    {

        public int GraphicsArchetypeIndex;
        public ArchetypeChunk Chunk;

        public bool Equals(BatchCreateInfo other)
        {
            return CompareTo(other) == 0;
        }

        public int CompareTo(BatchCreateInfo other) => GraphicsArchetypeIndex.CompareTo(other.GraphicsArchetypeIndex);
    }

    internal struct BatchCreateInfoFactory
    {
        public EntitiesGraphicsArchetypes GraphicsArchetypes;
        public NativeParallelHashMap<int, MaterialPropertyType> TypeIndexToMaterialProperty;

        public BatchCreateInfo Create(ArchetypeChunk chunk, ref MaterialPropertyType failureProperty)
        {
            return new BatchCreateInfo
            {
                GraphicsArchetypeIndex =
                    GraphicsArchetypes.GetGraphicsArchetypeIndex(chunk.Archetype, TypeIndexToMaterialProperty, ref failureProperty),
                Chunk = chunk,
            };
        }
    }

#if ENABLE_BATCH_OPTIMIZATION
    internal struct BatchInfo
    {
        public HeapBlock GPUMemoryAllocation;
        public SmallBlockAllocator SubbatchAllocator;
        public int HeadSubBatch;
        public int GraphicsArchetypeIndex;

        public int NextSameArch;
        public int PrevSameArch;
    }

#else
    internal struct BatchInfo
    {
        public HeapBlock GPUMemoryAllocation;
        public HeapBlock ChunkMetadataAllocation;
    }
#endif

    internal struct BatchMaterialMeshSubMesh
    {
        public BatchMaterialID Material;
        public BatchMeshID Mesh;
        public int SubMeshIndex;
    }

    internal struct BRGRenderMeshArray
    {
        public int Version;
        public UnsafeList<BatchMaterialID> UniqueMaterials;
        public UnsafeList<BatchMeshID> UniqueMeshes;
        public UnsafeList<BatchMaterialMeshSubMesh> MaterialMeshSubMeshes;
        public uint4 Hash128;

        public BatchMaterialID GetMaterialID(MaterialMeshInfo materialMeshInfo)
        {

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                if (!MaterialMeshSubMeshes.IsCreated)
                    return BatchMaterialID.Null;

                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                return MaterialMeshSubMeshes[range.start].Material;
            }
            else
            {
                if (!UniqueMaterials.IsCreated)
                    return BatchMaterialID.Null;

                int materialIndex = materialMeshInfo.MaterialArrayIndex;
                if (materialIndex == -1 || materialIndex >= UniqueMaterials.Length)
                    return BatchMaterialID.Null;

                return UniqueMaterials[materialIndex];
            }
        }

        public BatchMeshID GetMeshID(MaterialMeshInfo materialMeshInfo)
        {

            if (materialMeshInfo.HasMaterialMeshIndexRange)
            {
                if (!MaterialMeshSubMeshes.IsCreated)
                    return BatchMeshID.Null;

                RangeInt range = materialMeshInfo.MaterialMeshIndexRange;
                Assert.IsTrue(range.length > 0);

                return MaterialMeshSubMeshes[range.start].Mesh;
            }
            else
            {
                if (!UniqueMeshes.IsCreated)
                    return BatchMeshID.Null;

                int meshIndex = materialMeshInfo.MeshArrayIndex;
                if (meshIndex == -1 || meshIndex >= UniqueMeshes.Length)
                    return BatchMeshID.Null;

                return UniqueMeshes[meshIndex];
            }
        }
    }

    [BurstCompile]
    internal struct InitializeUnreferencedIndicesScatterJob : IJobParallelFor
    {
        [ReadOnly] public NativeArray<int> ExistingBatchIndices;
        public NativeArray<long> UnreferencedBatchIndices;

        public unsafe void Execute(int index)
        {
            int batchIndex = ExistingBatchIndices[index];

            AtomicHelpers.IndexToQwIndexAndMask(batchIndex, out int qw, out long mask);

            Assert.IsTrue(qw < UnreferencedBatchIndices.Length, "Batch index out of bounds");

            AtomicHelpers.AtomicOr((long*)UnreferencedBatchIndices.GetUnsafePtr(), qw, mask);
        }
    }

    internal struct BatchCreationTypeHandles
    {
        public ComponentTypeHandle<RootLODRange> RootLODRange;
        public ComponentTypeHandle<LODRange> LODRange;
        public ComponentTypeHandle<PerInstanceCullingTag> PerInstanceCulling;
        public ComponentTypeHandle<ChunkSimpleLOD> ChunkSimpleLOD;

        public BatchCreationTypeHandles(ComponentSystemBase componentSystemBase)
        {
            RootLODRange = componentSystemBase.GetComponentTypeHandle<RootLODRange>(true);
            LODRange = componentSystemBase.GetComponentTypeHandle<LODRange>(true);
            PerInstanceCulling = componentSystemBase.GetComponentTypeHandle<PerInstanceCullingTag>(true);
            ChunkSimpleLOD = componentSystemBase.GetComponentTypeHandle<ChunkSimpleLOD>(true);
        }
    }

    internal struct ChunkProperty
    {
        public int ComponentTypeIndex;
        public int ValueSizeBytesCPU;
        public int ValueSizeBytesGPU;
        public int GPUDataBegin;
    }

    internal struct MaterialPropertyType
    {
        public int TypeIndex;
        public int NameID;
        public short SizeBytesCPU;
        public short SizeBytesGPU;

        public string TypeName => EntitiesGraphicsSystem.TypeIndexToName(TypeIndex);
        public string PropertyName => EntitiesGraphicsSystem.NameIDToName(NameID);
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateBefore(typeof(EntitiesGraphicsSystem))]
    [CreateAfter(typeof(EntitiesGraphicsSystem))]
    partial class RegisterMaterialsAndMeshesSystem : SystemBase
    {

        private List<RenderMeshArray> m_RenderMeshArrays = new List<RenderMeshArray>();
        private List<int> m_SharedComponentIndices = new List<int>();
        private List<int> m_SharedComponentVersions = new List<int>();

        NativeParallelHashMap<int, BRGRenderMeshArray> m_BRGRenderMeshArrays;
        internal NativeParallelHashMap<int, BRGRenderMeshArray> BRGRenderMeshArrays => m_BRGRenderMeshArrays;

        EntitiesGraphicsSystem m_RendererSystem;

        protected override void OnCreate()
        {
            if (!EntitiesGraphicsSystem.EntitiesGraphicsEnabled)
            {
                Enabled = false;
                return;
            }

            m_BRGRenderMeshArrays = new NativeParallelHashMap<int, BRGRenderMeshArray>(256, Allocator.Persistent);
            m_RendererSystem = World.GetExistingSystemManaged<EntitiesGraphicsSystem>();

        }

        protected override void OnUpdate()
        {
            Profiler.BeginSample("RegisterMaterialsAndMeshes");
            Dependency = RegisterMaterialsAndMeshes(Dependency);
            Profiler.EndSample();
        }

        protected override void OnDestroy()
        {
            if (!EntitiesGraphicsSystem.EntitiesGraphicsEnabled) return;

            var brgRenderArrays = m_BRGRenderMeshArrays.GetValueArray(Allocator.Temp);
            for (int i = 0; i < brgRenderArrays.Length; ++i)
            {
                var brgRenderArray = brgRenderArrays[i];
                UnregisterMaterialsMeshes(brgRenderArray);
                brgRenderArray.UniqueMaterials.Dispose();
                brgRenderArray.UniqueMeshes.Dispose();
                brgRenderArray.MaterialMeshSubMeshes.Dispose();
            }
            m_BRGRenderMeshArrays.Dispose();
        }

        private void UnregisterMaterialsMeshes(in BRGRenderMeshArray brgRenderArray)
        {
            foreach (var id in brgRenderArray.UniqueMaterials)
            {
                m_RendererSystem.UnregisterMaterial(id);
            }

            foreach (var id in brgRenderArray.UniqueMeshes)
            {
                m_RendererSystem.UnregisterMesh(id);
            }
        }

        private void GetFilteredRenderMeshArrays(out List<RenderMeshArray> renderArrays, out List<int> sharedIndices, out List<int> sharedVersions)
        {
            m_RenderMeshArrays.Clear();
            m_SharedComponentIndices.Clear();
            m_SharedComponentVersions.Clear();

            renderArrays = m_RenderMeshArrays;
            sharedIndices = m_SharedComponentIndices;
            sharedVersions = m_SharedComponentVersions;

            EntityManager.GetAllUniqueSharedComponentsManaged<RenderMeshArray>(renderArrays, sharedIndices, sharedVersions);

            var discardedIndices = new NativeList<int>(renderArrays.Count, Allocator.Temp);

            for (int i = renderArrays.Count - 1; i >= 0; --i)
            {
                var array = renderArrays[i];
                if (array.MaterialReferences == null || array.MeshReferences == null)
                {
                    discardedIndices.Add(i);
                }
            }

            foreach (var i in discardedIndices)
            {
                renderArrays.RemoveAt(i);
                sharedIndices.RemoveAt(i);
                sharedVersions.RemoveAt(i);
            }

            discardedIndices.Dispose();
        }

        private JobHandle RegisterMaterialsAndMeshes(JobHandle inputDeps)
        {
            GetFilteredRenderMeshArrays(out var renderArrays, out var sharedIndices, out var sharedVersions);

            var brgArraysToDispose = new NativeList<BRGRenderMeshArray>(renderArrays.Count, Allocator.Temp);

            var sortedKeys = m_BRGRenderMeshArrays.GetKeyArray(Allocator.Temp);
            sortedKeys.Sort();

            for (int i = 0, j = 0; i < sortedKeys.Length; i++)
            {
                var oldKey = sortedKeys[i];
                while ((j < renderArrays.Count) && (sharedIndices[j] < oldKey))
                {
                    j++;
                }

                bool notFound = j == renderArrays.Count || oldKey != sharedIndices[j];
                if (notFound)
                {
                    var brgRenderArray = m_BRGRenderMeshArrays[oldKey];
                    brgArraysToDispose.Add(brgRenderArray);

                    m_BRGRenderMeshArrays.Remove(oldKey);
                }
            }
            sortedKeys.Dispose();

            for (int ri = 0; ri < renderArrays.Count; ++ri)
            {
                var renderArray = renderArrays[ri];
                if (renderArray.MaterialReferences == null || renderArray.MeshReferences == null)
                {
                    Debug.LogError("This loop should not process null RenderMeshArray components");
                    continue;
                }

                var sharedIndex = sharedIndices[ri];
                var sharedVersion = sharedVersions[ri];
                var materialCount = renderArray.MaterialReferences.Length;
                var meshCount = renderArray.MeshReferences.Length;
                var matMeshIndexCount = renderArray.MaterialMeshIndices != null ? renderArray.MaterialMeshIndices.Length : 0;
                uint4 hash128 = renderArray.GetHash128();

                bool update = false;
                BRGRenderMeshArray brgRenderArray;
                if (m_BRGRenderMeshArrays.TryGetValue(sharedIndex, out brgRenderArray))
                {

                    if ((brgRenderArray.Version != sharedVersion) ||
                        math.any(brgRenderArray.Hash128 != hash128))
                    {
                        brgArraysToDispose.Add(brgRenderArray);
                        update = true;

#if DEBUG_LOG_BRG_MATERIAL_MESH
                        Debug.Log($"BRG Material Mesh : RenderMeshArray version change | SharedIndex ({sharedIndex}) | SharedVersion ({brgRenderArray.Version}) -> ({sharedVersion})");
#endif
                    }
                }
                else
                {
                    brgRenderArray = new BRGRenderMeshArray();
                    update = true;

#if DEBUG_LOG_BRG_MATERIAL_MESH
                    Debug.Log($"BRG Material Mesh : New RenderMeshArray found | SharedIndex ({sharedIndex})");
#endif
                }

                if (update)
                {
                    brgRenderArray.Version = sharedVersion;
                    brgRenderArray.Hash128 = hash128;
                    brgRenderArray.UniqueMaterials = new UnsafeList<BatchMaterialID>(materialCount, Allocator.Persistent);
                    brgRenderArray.UniqueMeshes = new UnsafeList<BatchMeshID>(meshCount, Allocator.Persistent);
                    brgRenderArray.MaterialMeshSubMeshes = new UnsafeList<BatchMaterialMeshSubMesh>(matMeshIndexCount, Allocator.Persistent);

                    for (int i = 0; i < materialCount; ++i)
                    {
                        var material = renderArray.MaterialReferences[i];
                        var id = m_RendererSystem.RegisterMaterial(material);
                        if (id == BatchMaterialID.Null)
                        {
                            Debug.LogWarning($"Registering material {(material ? material.Value.ToString() : "null")} at index {i} inside a RenderMeshArray failed.");
                        }

                        brgRenderArray.UniqueMaterials.Add(id);
                    }

                    for (int i = 0; i < meshCount; ++i)
                    {
                        var mesh = renderArray.MeshReferences[i];
                        var id = m_RendererSystem.RegisterMesh(mesh);
                        if (id == BatchMeshID.Null)
                            Debug.LogWarning($"Registering mesh {(mesh ? mesh.Value.ToString() : "null")} at index {i} inside a RenderMeshArray failed.");

                        brgRenderArray.UniqueMeshes.Add(id);
                    }

                    for (int i = 0; i < matMeshIndexCount; ++i)
                    {
                        MaterialMeshIndex matMeshIndex = renderArray.MaterialMeshIndices[i];

                        BatchMaterialID materialID = BatchMaterialID.Null;
                        if (matMeshIndex.MaterialIndex != -1)
                            materialID = brgRenderArray.UniqueMaterials[matMeshIndex.MaterialIndex];

                        BatchMeshID meshID = BatchMeshID.Null;
                        if (matMeshIndex.MeshIndex != -1)
                            meshID = brgRenderArray.UniqueMeshes[matMeshIndex.MeshIndex];

                        brgRenderArray.MaterialMeshSubMeshes.Add(new BatchMaterialMeshSubMesh
                        {
                            Material = materialID,
                            Mesh = meshID,
                            SubMeshIndex = matMeshIndex.SubMeshIndex,
                        });
                    }

                    m_BRGRenderMeshArrays[sharedIndex] = brgRenderArray;
                }
            }

            for (int i = 0; i < brgArraysToDispose.Length; ++i)
            {
                var brgRenderArray = brgArraysToDispose[i];
                UnregisterMaterialsMeshes(brgRenderArray);
                brgRenderArray.UniqueMaterials.Dispose();
                brgRenderArray.UniqueMeshes.Dispose();
                brgRenderArray.MaterialMeshSubMeshes.Dispose();
            }
            return default;
        }
    }

    [WorldSystemFilter(WorldSystemFilterFlags.Default | WorldSystemFilterFlags.Editor)]
    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(UpdatePresentationSystemGroup))]
    [BurstCompile]
    public unsafe partial class EntitiesGraphicsSystem : SystemBase
    {

#if HYBRID_RENDERER_DISABLED
        public static bool EntitiesGraphicsEnabled => false;
#else
        public static bool EntitiesGraphicsEnabled => EntitiesGraphicsUtils.IsEntitiesGraphicsSupportedOnSystem();
#endif

#if !DISABLE_HYBRID_RENDERER_ERROR_LOADING_SHADER
        private static bool ErrorShaderEnabled => true;
#else
        private static bool ErrorShaderEnabled => false;
#endif

#if UNITY_EDITOR && !DISABLE_HYBRID_RENDERER_ERROR_LOADING_SHADER
        private static bool LoadingShaderEnabled => true;
#else
        private static bool LoadingShaderEnabled => false;
#endif

        private long m_PersistentInstanceDataSize;

        private uint m_LastSystemVersionAtLastUpdate;

        private EntityQuery m_CullingJobDependencyGroup;
        private EntityQuery m_EntitiesGraphicsRenderedQuery;
        private EntityQuery m_EntitiesGraphicsRenderedQueryRO;
        private EntityQuery m_LodSelectGroup;
        private EntityQuery m_PlanarShadowQuery;
        private EntityQuery m_ChangedTransformQuery;
        private EntityQuery m_MetaEntitiesForHybridRenderableChunksQuery;

        const int kInitialMaxBatchCount = 1 * 1024;

        const float kMaxBatchGrowFactor = 2f;

        const int kNumNewChunksPerThread = 1;

        const int kNumScatteredIndicesPerThread = 8;

        const int kMaxCullingPassesWithoutAllocatorRewind = 1024;

        const int kMaxChunkMetadata = 1 * 32 * 1024;

        const ulong kMaxGPUAllocatorMemory = 1024 * 1024 * 1024;

        const long kGPUBufferSizeInitial = 2 * 1024 * 1024;

        const long kGPUBufferSizeMax = 1023 * 1024 * 1024;

        const int kGPUUploaderChunkSize = 4 * 1024 * 1024;

        private JobHandle m_CullingJobDependency;
        private JobHandle m_CullingJobReleaseDependency;
        private JobHandle m_UpdateJobDependency;
        private JobHandle m_LODDependency;
        private JobHandle m_ReleaseDependency;
        private BatchRendererGroup m_BatchRendererGroup;
        private ThreadedBatchContext m_ThreadedBatchContext;

        private bool m_UseTextureScene;
        private List<Texture2D> m_TextureSceneList;
        private int m_TextureScenePageSize;
        private int m_TextureScenePageWidth;
        private int m_TextureScenePageHeight;

        private GraphicsBuffer m_GPUPersistentInstanceData;

        private GraphicsBufferHandle m_GPUPersistentInstanceBufferHandle;

#if ENABLE_BATCH_OPTIMIZATION
        private FixedSizeAllocator m_GPUPersistentAllocator;
        private SubBatchAllocator m_SubBatchAllocator;
        private NativeList<int> m_ArchHead;
        private SegregatedUnitAllocator m_ChunkMetadataAllocator;
#else
        private HeapAllocator m_GPUPersistentAllocator;
        private HeapAllocator m_ChunkMetadataAllocator;
#endif

        private HeapBlock m_SharedZeroAllocation;

        private NativeList<BatchInfo> m_BatchInfos;
        private NativeArray<ChunkProperty> m_ChunkProperties;
        private NativeParallelHashSet<int> m_ExistingBatchIndices;
#if ENABLE_BATCH_OPTIMIZATION
        private NativeParallelHashSet<int> m_ExistingSubBatchIndices;
        private int m_MaxBatchIdPlusOne;
#else
        private SortedSet<int> m_SortedBatchIds;
#endif
        private ComponentTypeCache m_ComponentTypeCache;

        private NativeList<ValueBlitDescriptor> m_ValueBlits;

        NativeList<byte> m_ForceLowLOD;

        int m_SimpleChunkLOD;
#if UNITY_EDITOR
        float m_CamMoveDistance;
#endif
        int m_NumberOfCullingPassesAccumulatedWithoutAllocatorRewind;
#if UNITY_EDITOR
        private EntitiesGraphicsPerThreadStats* m_PerThreadStats = null;
        private EntitiesGraphicsStats m_Stats;
        public EntitiesGraphicsStats Stats => m_Stats;

        private void ComputeStats()
        {
            Profiler.BeginSample("ComputeStats");

#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif

            var result = default(EntitiesGraphicsStats);
            for (int i = 0; i < maxThreadCount; ++i)
            {
                ref var s = ref m_PerThreadStats[i];

                result.ChunkTotal                   += s.ChunkTotal;
                result.ChunkCountAnyLod             += s.ChunkCountAnyLod;
                result.ChunkCountInstancesProcessed += s.ChunkCountInstancesProcessed;
                result.ChunkCountFullyIn            += s.ChunkCountFullyIn;
                result.InstanceTests                += s.InstanceTests;
                result.LodTotal                     += s.LodTotal;
                result.LodNoRequirements            += s.LodNoRequirements;
                result.LodChanged                   += s.LodChanged;
                result.LodChunksTested              += s.LodChunksTested;

                result.RenderedInstanceCount        += s.RenderedEntityCount;
                result.DrawCommandCount             += s.DrawCommandCount;
                result.DrawRangeCount               += s.DrawRangeCount;
            }

            result.CameraMoveDistance = m_CamMoveDistance;

            result.BatchCount = m_ExistingBatchIndices.Count();
            if(!m_UseDirectUpload)
            {

            }

            m_Stats = result;

            Profiler.EndSample();
        }

#endif

        private DirectUploader m_DirectUploader;

        private NativeArray<float4> m_SystemMemoryBuffer;

        private bool m_UseDirectUpload = true;

        private bool m_ResetLod;

        LODGroupExtensions.LODParams m_PrevLODParams;
        float3 m_PrevCameraPos;
        float m_PrevLodDistanceScale;

        NativeParallelMultiHashMap<int, MaterialPropertyType> m_NameIDToMaterialProperties;
        NativeParallelHashMap<int, MaterialPropertyType> m_TypeIndexToMaterialProperty;

        static Dictionary<Type, NamedPropertyMapping> s_TypeToPropertyMappings = new Dictionary<Type, NamedPropertyMapping>();

#if DEBUG_PROPERTY_NAMES
        internal static Dictionary<int, string> s_NameIDToName = new Dictionary<int, string>();
        internal static Dictionary<int, string> s_TypeIndexToName = new Dictionary<int, string>();
#endif

        private bool m_FirstFrameAfterInit;

        private EntitiesGraphicsArchetypes m_GraphicsArchetypes;

        private NativeParallelHashMap<int, BatchFilterSettings> m_FilterSettings;
        private NativeParallelHashMap<int, int> m_SortingOrders;
#if ENABLE_PICKING
        Material m_PickingMaterial;
#endif

        Material m_LoadingMaterial;
        Material m_ErrorMaterial;

        private List<RenderFilterSettings> m_RenderFilterSettings = new List<RenderFilterSettings>();
        private List<int> m_SharedComponentIndices = new List<int>();

        private ThreadLocalAllocator m_ThreadLocalAllocators;

        private float m_PlanarShadowCullDist = 100;

        static int sVtfStrideID = Shader.PropertyToID("unity_VTFStride");
        protected override void OnCreate()
        {

#if UNITY_WEBGL && TUANJIE_1_6_OR_NEWER
            m_UseTextureScene = true;
            m_TextureSceneList = new List<Texture2D>();
#endif

            if (!EntitiesGraphicsEnabled)
            {
                Enabled = false;
                Debug.Log("No SRP present, no compute shader support, or running with -nographics. Entities Graphics package disabled");
                return;
            }

            m_FirstFrameAfterInit = true;

            m_PersistentInstanceDataSize = kGPUBufferSizeInitial;

            m_CullingJobDependencyGroup = GetEntityQuery(
                ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                ComponentType.ReadOnly<RootLODRange>(),
                ComponentType.ReadOnly<RootLODWorldReferencePoint>(),
                ComponentType.ReadOnly<LODRange>(),
                ComponentType.ReadOnly<LODWorldReferencePoint>(),
                ComponentType.ReadOnly<WorldRenderBounds>(),
                ComponentType.ReadOnly<ChunkHeader>(),
                ComponentType.ChunkComponentReadOnly<EntitiesGraphicsChunkInfo>()
            );

            m_EntitiesGraphicsRenderedQuery = GetEntityQuery(EntitiesGraphicsUtils.GetEntitiesGraphicsRenderedQueryDesc());
            m_EntitiesGraphicsRenderedQueryRO = GetEntityQuery(EntitiesGraphicsUtils.GetEntitiesGraphicsRenderedQueryDescReadOnly());

            m_LodSelectGroup = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadWrite<EntitiesGraphicsChunkInfo>(),
                    ComponentType.ReadOnly<ChunkHeader>()
                },
            });

            m_PlanarShadowQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                    {
                        ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>(),
                        ComponentType.ReadOnly<WorldRenderBounds>(),
                        ComponentType.ReadOnly<PlanarShadow>(),
                    },
            });

            m_ChangedTransformQuery = GetEntityQuery(new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>(),
                },
            });
            m_ChangedTransformQuery.AddChangedVersionFilter(ComponentType.ReadOnly<LocalToWorld>());
            m_ChangedTransformQuery.AddOrderVersionFilter();

            m_BatchRendererGroup = new BatchRendererGroup(this.OnPerformCulling, IntPtr.Zero);

            m_BatchRendererGroup.SetEnabledViewTypes(new BatchCullingViewType[]
            {
                BatchCullingViewType.Camera,
                BatchCullingViewType.Light,
                BatchCullingViewType.Picking,
                BatchCullingViewType.SelectionOutline
            });
            if(!m_UseTextureScene)
                m_ThreadedBatchContext = m_BatchRendererGroup.GetThreadedBatchContext();
            m_ForceLowLOD = NewNativeListResized<byte>(kInitialMaxBatchCount, Allocator.Persistent, NativeArrayOptions.ClearMemory);

            m_ResetLod = true;
            m_SimpleChunkLOD = -1;
#if ENABLE_BATCH_OPTIMIZATION
            m_GPUPersistentAllocator = new FixedSizeAllocator(MaxBytesPerCBuffer, (int)m_PersistentInstanceDataSize / MaxBytesPerCBuffer);
            m_SubBatchAllocator = new SubBatchAllocator(kInitialMaxBatchCount * 4);

            m_ArchHead = new NativeList<int>(256, Allocator.Persistent);
            m_ArchHead.Resize(256, NativeArrayOptions.UninitializedMemory);
            for (int i = 0; i < 256; ++i) m_ArchHead[i] = -1;

            m_ChunkMetadataAllocator = new SegregatedUnitAllocator(1, 256, 256);
#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"BATCH SYSTEM INITIALIZED: MaxBatches={kInitialMaxBatchCount}, MaxSubBatches={kInitialMaxBatchCount * 4}, ENABLE_BATCH_OPTIMIZATION=true");
#endif
#else
            m_GPUPersistentAllocator = new HeapAllocator(kMaxGPUAllocatorMemory, 16);
            m_ChunkMetadataAllocator = new HeapAllocator(kMaxChunkMetadata);
#endif

            m_BatchInfos = NewNativeListResized<BatchInfo>(kInitialMaxBatchCount, Allocator.Persistent);
            m_ChunkProperties = new NativeArray<ChunkProperty>(kMaxChunkMetadata, Allocator.Persistent);
            m_ExistingBatchIndices = new NativeParallelHashSet<int>(128, Allocator.Persistent);
#if ENABLE_BATCH_OPTIMIZATION
            m_ExistingSubBatchIndices = new NativeParallelHashSet<int>(128, Allocator.Persistent);
#endif
            m_ComponentTypeCache = new ComponentTypeCache(128);

            m_ValueBlits = new NativeList<ValueBlitDescriptor>(Allocator.Persistent);

#if ENABLE_BATCH_OPTIMIZATION
            m_SharedZeroAllocation = m_GPUPersistentAllocator.Allocate();
#else
            m_SharedZeroAllocation = m_GPUPersistentAllocator.Allocate((ulong)sizeof(float4x4));
#endif
            Assert.IsTrue(!m_SharedZeroAllocation.Empty, "Allocation of constant-zero data failed");

            m_ValueBlits.Add(new ValueBlitDescriptor
            {
                Value = float4x4.zero,
                DestinationOffset = (uint)m_SharedZeroAllocation.begin,
                ValueSizeBytes = (uint)sizeof(float4x4),
                Count = 1,
            });
            Assert.IsTrue(m_SharedZeroAllocation.begin == 0, "Global zero allocation should have zero address");

            ResetIds();

            m_MetaEntitiesForHybridRenderableChunksQuery = GetEntityQuery(
                new EntityQueryDesc
                {
                    All = new[]
                    {
                        ComponentType.ReadWrite<EntitiesGraphicsChunkInfo>(),
                        ComponentType.ReadOnly<ChunkHeader>(),
                    },
                });

#if UNITY_EDITOR
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif

            m_PerThreadStats = (EntitiesGraphicsPerThreadStats*)Memory.Unmanaged.Allocate(maxThreadCount * sizeof(EntitiesGraphicsPerThreadStats),
                64, Allocator.Persistent);

#endif

            m_NameIDToMaterialProperties = new NativeParallelMultiHashMap<int, MaterialPropertyType>(256, Allocator.Persistent);
            m_TypeIndexToMaterialProperty = new NativeParallelHashMap<int, MaterialPropertyType>(256, Allocator.Persistent);

            m_GraphicsArchetypes = new EntitiesGraphicsArchetypes(256);

            m_FilterSettings = new NativeParallelHashMap<int, BatchFilterSettings>(256, Allocator.Persistent);
            m_SortingOrders = new NativeParallelHashMap<int, int>(256, Allocator.Persistent);

            RegisterMaterialPropertyType<LocalToWorld>("unity_ObjectToWorld", 4 * 4 * 3);
            RegisterMaterialPropertyType<WorldToLocal_Tag>("unity_WorldToObject", overrideTypeSizeGPU: 4 * 4 * 3);

#if ENABLE_PICKING
            RegisterMaterialPropertyType(typeof(Entity), "unity_EntityId");
#endif

            foreach (var typeInfo in TypeManager.AllTypes)
            {
                var type = typeInfo.Type;

                bool isComponent = typeof(IComponentData).IsAssignableFrom(type);
                if (isComponent)
                {
                    var attributes = type.GetCustomAttributes(typeof(MaterialPropertyAttribute), false);
                    if (attributes.Length > 0)
                    {
                        var propertyAttr = (MaterialPropertyAttribute)attributes[0];

                        RegisterMaterialPropertyType(type, propertyAttr.Name, propertyAttr.OverrideSizeGPU);
                    }
                }
            }

            bool useConstantBuffer = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;
            if (m_UseTextureScene)
            {
                DecomposePowerOfTwo((int)m_PersistentInstanceDataSize / 16, out int height, out int width);
                var textureScene = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
                m_TextureScenePageWidth = width;
                m_TextureScenePageHeight = height;
                m_TextureScenePageSize = m_TextureScenePageWidth * m_TextureScenePageHeight * 16;
                m_PersistentInstanceDataSize = m_TextureScenePageSize;
                m_TextureSceneList.Add(textureScene);
            }
            else if (useConstantBuffer && m_UseDirectUpload)
            {
                m_GPUPersistentInstanceData = new GraphicsBuffer(
                    GraphicsBuffer.Target.Constant,
                    (int)m_PersistentInstanceDataSize / 16,
                    16);
            }
            else
            {
                m_GPUPersistentInstanceData = new GraphicsBuffer(
                    GraphicsBuffer.Target.Raw,
                    (int)m_PersistentInstanceDataSize / 4,
                    4);
            }

            m_GPUPersistentInstanceBufferHandle = m_UseTextureScene ? new GraphicsBufferHandle() : m_GPUPersistentInstanceData.bufferHandle;

            if (m_UseDirectUpload)
            {

                m_SystemMemoryBuffer = new NativeArray<float4>(
                    (int)m_PersistentInstanceDataSize / 16,
                    Allocator.Persistent,
                    NativeArrayOptions.ClearMemory);

                m_DirectUploader = new DirectUploader(
                    m_GPUPersistentInstanceData,
                    m_SystemMemoryBuffer,
                    m_PersistentInstanceDataSize,
                    GetNativeTexturePtrs(m_TextureSceneList),
                    m_TextureScenePageWidth,
                    m_TextureScenePageHeight);

                Debug.Log("EntitiesGraphicsSystem: Using DirectUploader for GPU uploads");
            }
            else
            {

            }

            m_ThreadLocalAllocators = new ThreadLocalAllocator(-1);

            m_NumberOfCullingPassesAccumulatedWithoutAllocatorRewind = 0;

            if (ErrorShaderEnabled)
            {
                m_ErrorMaterial = EntitiesGraphicsUtils.LoadErrorMaterial();
                if (m_ErrorMaterial != null)
                {
                    m_BatchRendererGroup.SetErrorMaterial(m_ErrorMaterial);
                }
            }

            if (LoadingShaderEnabled)
            {
                m_LoadingMaterial = EntitiesGraphicsUtils.LoadLoadingMaterial();
                if (m_LoadingMaterial != null)
                {
                    m_BatchRendererGroup.SetLoadingMaterial(m_LoadingMaterial);
                }
            }

#if ENABLE_PICKING
            m_PickingMaterial = EntitiesGraphicsUtils.LoadPickingMaterial();
            if (m_PickingMaterial != null)
            {
                m_BatchRendererGroup.SetPickingMaterial(m_PickingMaterial);
            }
#endif
        }

        static int DecomposePowerOfTwo(int num, out int height, out int width)
        {
            System.Diagnostics.Debug.Assert(num > 0 && (num & (num - 1)) == 0);

            int n = 0;
            int x = num;
            while ((x >>= 1) != 0) n++; // n = floor(log2(num))

            height = 1 << (n / 2);
            width = 1 << ((n + 1) / 2);

            return width - height;
        }
        static NativeArray<IntPtr> GetNativeTexturePtrs(List<Texture2D> textureList)
        {
            if (textureList == null)
                return new NativeArray<IntPtr>();
            NativeArray<IntPtr> texturePtrs = new NativeArray<IntPtr>(textureList.Count, Allocator.Persistent);
            for (int i = 0; i < texturePtrs.Length; i++)
            {
                texturePtrs[i] = textureList[i].GetNativeTexturePtr();
            }
            return texturePtrs;
        }

        internal static readonly bool UseConstantBuffers = EntitiesGraphicsUtils.UseHybridConstantBufferMode();
        internal static readonly int MaxBytesPerCBuffer = EntitiesGraphicsUtils.MaxBytesPerCBuffer;
        internal static readonly uint BatchAllocationAlignment = (uint)EntitiesGraphicsUtils.BatchAllocationAlignment;

        internal const int kMaxBytesPerBatchRawBuffer = 16 * 1024 * 1024;

        public static int MaxBytesPerBatch => UseConstantBuffers
            ? MaxBytesPerCBuffer
            : kMaxBytesPerBatchRawBuffer;

        public static void RegisterMaterialPropertyType(Type type, string propertyName, short overrideTypeSizeGPU = -1)
        {
            Assert.IsTrue(type != null, "type must be non-null");
            Assert.IsTrue(!string.IsNullOrEmpty(propertyName), "Property name must be valid");

            short typeSizeCPU = (short)UnsafeUtility.SizeOf(type);
            if (overrideTypeSizeGPU == -1)
                overrideTypeSizeGPU = typeSizeCPU;

            if (s_TypeToPropertyMappings.ContainsKey(type))
            {
                string prevPropertyName = s_TypeToPropertyMappings[type].Name;
                Assert.IsTrue(propertyName.Equals(prevPropertyName),
                    $"Attempted to register type {type.Name} with multiple different property names. Registered with \"{propertyName}\", previously registered with \"{prevPropertyName}\".");
            }
            else
            {
                var pm = new NamedPropertyMapping();
                pm.Name = propertyName;
                pm.SizeCPU = typeSizeCPU;
                pm.SizeGPU = overrideTypeSizeGPU;
                s_TypeToPropertyMappings[type] = pm;
            }
        }

        public static void RegisterMaterialPropertyType<T>(string propertyName, short overrideTypeSizeGPU = -1)
            where T : IComponentData
        {
            RegisterMaterialPropertyType(typeof(T), propertyName, overrideTypeSizeGPU);
        }

        private void InitializeMaterialProperties()
        {
            m_NameIDToMaterialProperties.Clear();

            foreach (var kv in s_TypeToPropertyMappings)
            {
                Type type = kv.Key;
                string propertyName = kv.Value.Name;

                short sizeBytesCPU = kv.Value.SizeCPU;
                short sizeBytesGPU = kv.Value.SizeGPU;
                int typeIndex = TypeManager.GetTypeIndex(type);
                int nameID = Shader.PropertyToID(propertyName);

                var materialPropertyType =
                    new MaterialPropertyType
                    {
                        TypeIndex = typeIndex,
                        NameID = nameID,
                        SizeBytesCPU = sizeBytesCPU,
                        SizeBytesGPU = sizeBytesGPU,
                    };

                m_TypeIndexToMaterialProperty.Add(typeIndex, materialPropertyType);
                m_NameIDToMaterialProperties.Add(nameID, materialPropertyType);

#if DEBUG_PROPERTY_NAMES
                s_TypeIndexToName[typeIndex] = type.Name;
                s_NameIDToName[nameID] = propertyName;
#endif

#if DEBUG_LOG_MATERIAL_PROPERTY_TYPES
                Debug.Log($"Type \"{type.Name}\" ({sizeBytesCPU} bytes) overrides material property \"{propertyName}\" (nameID: {nameID}, typeIndex: {typeIndex})");
#endif

                m_ComponentTypeCache.UseType(typeIndex);
            }
        }

        public void PruneUploadBufferPool(int maxMemoryToRetainInUploadPoolBytes)
        {

        }

        public float PlanarShadowCullDist { set { m_PlanarShadowCullDist = value; } }

        public int SimpleChunkLOD { set { m_SimpleChunkLOD = value; } }

        protected override void OnDestroy()
        {
            if (!EntitiesGraphicsEnabled) return;
            CompleteJobs(true);

            if (m_UseDirectUpload)
            {
                m_DirectUploader.Dispose();
                if (m_SystemMemoryBuffer.IsCreated)
                    m_SystemMemoryBuffer.Dispose();
            }
            else
            {

            }

            Dispose();
        }

        private JobHandle UpdateEntitiesGraphicsBatches(JobHandle inputDependencies)
        {
            JobHandle done = default;
            Profiler.BeginSample("UpdateAllBatches");
            if (!m_EntitiesGraphicsRenderedQuery.IsEmptyIgnoreFilter)
            {
                done = UpdateAllBatches(inputDependencies);
            }

            Profiler.EndSample();

            return done;
        }

        private void OnFirstFrame()
        {
            InitializeMaterialProperties();

#if DEBUG_LOG_HYBRID_RENDERER

            var mode = UseConstantBuffers
                ? $"UBO mode (UBO max size: {MaxBytesPerCBuffer}, alignment: {BatchAllocationAlignment}, globals: {m_GlobalWindowSize})"
                : "SSBO mode";
            Debug.Log(
                $"Entities Graphics active, MaterialProperty component type count {m_ComponentTypeCache.UsedTypeCount} / {ComponentTypeCache.BurstCompatibleTypeArray.kMaxTypes}, {mode}");
#endif
        }

        private JobHandle UpdateFilterSettings(JobHandle inputDeps)
        {
            m_RenderFilterSettings.Clear();
            m_SharedComponentIndices.Clear();

            EntityManager.GetAllUniqueSharedComponentsManaged(m_RenderFilterSettings, m_SharedComponentIndices);

            m_FilterSettings.Clear();
            for (int i = 0; i < m_SharedComponentIndices.Count; ++i)
            {
                int sharedIndex = m_SharedComponentIndices[i];
                m_FilterSettings[sharedIndex] = MakeFilterSettings(m_RenderFilterSettings[i]);
                m_SortingOrders[sharedIndex] = m_RenderFilterSettings[i].sortingOrder;
            }

            m_RenderFilterSettings.Clear();
            m_SharedComponentIndices.Clear();

            return new JobHandle();
        }

        private static BatchFilterSettings MakeFilterSettings(RenderFilterSettings filterSettings)
        {
            return new BatchFilterSettings
            {
                layer = (byte) filterSettings.Layer,
                renderingLayerMask = filterSettings.RenderingLayerMask,
                motionMode = filterSettings.MotionMode,
                shadowCastingMode = filterSettings.ShadowCastingMode,
                receiveShadows = filterSettings.ReceiveShadows,
                staticShadowCaster = filterSettings.StaticShadowCaster,
                allDepthSorted = false,
            };
        }

        protected override void OnUpdate()
        {
            JobHandle inputDeps = Dependency;

            RewindThreadLocalAllocator();

            m_LastSystemVersionAtLastUpdate = LastSystemVersion;

            if (m_FirstFrameAfterInit)
            {
                OnFirstFrame();
                m_FirstFrameAfterInit = false;
            }

            Profiler.BeginSample("CompleteJobs");
            inputDeps.Complete();
            CompleteJobs();
            ResetLod();
            Profiler.EndSample();

#if UNITY_EDITOR
            ComputeStats();
#endif

            Profiler.BeginSample("UpdateFilterSettings");
            var updateFilterSettingsHandle = UpdateFilterSettings(inputDeps);
            Profiler.EndSample();

            inputDeps = JobHandle.CombineDependencies(inputDeps, updateFilterSettingsHandle);

            var done = new JobHandle();
            try
            {
                Profiler.BeginSample("UpdateEntitiesGraphicsBatches");
                done = UpdateEntitiesGraphicsBatches(inputDeps);
                Profiler.EndSample();

                Profiler.BeginSample("EndUpdate");
                EndUpdate();
                Profiler.EndSample();
            }
            finally
            {

            }

            Dependency = done;
        }

        private void ResetIds()
        {
#if ENABLE_BATCH_OPTIMIZATION
            m_MaxBatchIdPlusOne = 0;
#else
            m_SortedBatchIds = new SortedSet<int>();
#endif
            m_ExistingBatchIndices.Clear();
        }

        private void EnsureHaveSpaceForNewBatch()
        {
            int currentCapacity = m_BatchInfos.Length;
            int neededCapacity = BatchIndexRange;

            if (currentCapacity >= neededCapacity) return;

            Assert.IsTrue(kMaxBatchGrowFactor >= 1f,
                "Grow factor should always be greater or equal to 1");

            var newCapacity = (int)(kMaxBatchGrowFactor * neededCapacity);

            m_ForceLowLOD.Resize(newCapacity, NativeArrayOptions.ClearMemory);
            m_BatchInfos.Resize(newCapacity, NativeArrayOptions.ClearMemory);

#if ENABLE_BATCH_OPTIMIZATION
            var ptr = m_BatchInfos.GetUnsafePtr();
            for (int id = currentCapacity; id < newCapacity; ++id)
            {
                ref var bi = ref UnsafeUtility.AsRef<BatchInfo>(ptr + id);
                bi = default;
                bi.GraphicsArchetypeIndex = InvalidIndex;
                bi.NextSameArch = InvalidIndex;
                bi.PrevSameArch = InvalidIndex;
                bi.HeadSubBatch = InvalidIndex;
            }
#endif
        }

#if ENABLE_BATCH_OPTIMIZATION
        private void AddBatchIndex(int id)
        {

            Assert.IsTrue(!m_ExistingBatchIndices.Contains(id), "New batch ID already marked as used");
            m_ExistingBatchIndices.Add(id);
            if (id + 1 > m_MaxBatchIdPlusOne)
                m_MaxBatchIdPlusOne = id + 1;

            EnsureHaveSpaceForNewBatch();
        }

        private void RemoveBatchIndex(int id)
        {
            if (!m_ExistingBatchIndices.Contains(id))
                Assert.IsTrue(false, $"Attempted to release an unused id {id}");
            m_ExistingBatchIndices.Remove(id);
        }

        private void AddSubBatchIndex(int id)
        {
            Assert.IsTrue(!m_ExistingSubBatchIndices.Contains(id), "New SubBatch ID already marked as used");
            m_ExistingSubBatchIndices.Add(id);
        }
        private void RemoveSubBatch(int subBatchIndex)
        {
            if (subBatchIndex == SubBatchAllocator.InvalidBatchNumber)
                return;

            SubBatch* subBatchPool = m_SubBatchAllocator.GetUnsafePtr();
            SubBatch* subBatch = subBatchPool + subBatchIndex;

            if (subBatch->BatchID == SubBatchAllocator.InvalidBatchNumber &&
                subBatch->ChunkOffsetInBatch.Empty &&
                subBatch->ChunkMetadataAllocation.Empty)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogWarning($"SubBatch {subBatchIndex} already freed, skipping duplicate removal");
#endif
                return;
            }

            m_ExistingSubBatchIndices.Remove(subBatchIndex);

            var batchIndex = subBatch->BatchID;

            BatchInfo* batchInfo = m_BatchInfos.GetUnsafePtr() + batchIndex;

            if (!subBatch->ChunkOffsetInBatch.Empty)
            {
                batchInfo->SubbatchAllocator.Deallocate((int)subBatch->ChunkOffsetInBatch.begin, (int)subBatch->ChunkOffsetInBatch.Length);
            }
            if (!subBatch->ChunkMetadataAllocation.Empty)
            {
                var metadataAddress = subBatch->ChunkMetadataAllocation;
                for (int i = (int)metadataAddress.begin; i < (int)metadataAddress.end; i++)
                    m_ChunkProperties[i] = default;

                m_ChunkMetadataAllocator.Free(ref subBatch->ChunkMetadataAllocation);
            }

            if (subBatch->PrevID != SubBatchAllocator.InvalidBatchNumber)
            {
                subBatchPool[subBatch->PrevID].NextID = subBatch->NextID;
            }
            else
            {
                batchInfo->HeadSubBatch = subBatch->NextID;
            }

            if (subBatch->NextID != SubBatchAllocator.InvalidBatchNumber)
            {
                subBatchPool[subBatch->NextID].PrevID = subBatch->PrevID;
            }

            m_SubBatchAllocator.Dealloc(subBatchIndex);

            if (batchInfo->HeadSubBatch == SubBatchAllocator.InvalidBatchNumber)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.Log($"SUBBATCH REMOVING COMPLETED: ID={subBatchIndex}, triggering BatchRemoval for BatchID={batchIndex} (no more SubBatches)");
#endif
                RemoveBatch(batchIndex);
            }
            else
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.Log($"SUBBATCH REMOVING COMPLETED: ID={subBatchIndex}, BatchID={batchIndex} still has SubBatches (HeadSubBatch={batchInfo->HeadSubBatch})");
#endif
            }

        }
        const int InvalidIndex = -1;
        private void RemoveBatch(int batchIndex)
        {
            ref var p = ref m_BatchInfos.GetUnsafePtr()[batchIndex];
            int oldArch = p.GraphicsArchetypeIndex;

            if (oldArch != InvalidIndex)
            {
                UnlinkBatchFromArchList(batchIndex, oldArch);
            }

#if DEBUG_LOG_BATCH_DELETION
            Debug.Log($"BATCH REMOVED: ID={batchIndex}");
#endif

            RemoveBatchIndex(batchIndex);

            if (!p.GPUMemoryAllocation.Empty)
            {
                m_GPUPersistentAllocator.Dealloc(p.GPUMemoryAllocation);
                p.GPUMemoryAllocation = default;
#if DEBUG_LOG_MEMORY_USAGE
                Debug.Log($"RELEASE; {batchInfo.GPUMemoryAllocation.Length}");
#endif
            }

            if (p.SubbatchAllocator.IsCreated)
            {
                p.SubbatchAllocator.Dispose();
            }

            p.GraphicsArchetypeIndex = InvalidIndex;

            if (m_UseTextureScene)
                m_BatchRendererGroup.RemoveBatch(new BatchID { value = (uint)batchIndex });
            else
                m_ThreadedBatchContext.RemoveBatch(new BatchID { value = (uint)batchIndex });
        }

         private void EnsureArchetypeIndex(int arch)
         {
             if (arch< 0) return;
             if (!m_ArchHead.IsCreated)
             {
                 m_ArchHead = new NativeList<int>(math.max(256, arch + 1), Allocator.Persistent);
                 m_ArchHead.Resize(math.max(256, arch + 1), NativeArrayOptions.UninitializedMemory);
                 for (int i = 0; i<m_ArchHead.Length; ++i) m_ArchHead[i] = -1;
                 return;
             }
             if (m_ArchHead.Length <= arch)
             {
                 int old = m_ArchHead.Length;
                 m_ArchHead.Resize(arch + 1, NativeArrayOptions.UninitializedMemory);
                 for (int i = old; i<m_ArchHead.Length; ++i) m_ArchHead[i] = -1;
             }
         }

        private void UnlinkBatchFromArchList(int batchIndex, int arch)
        {
            var batchInfos = m_BatchInfos.GetUnsafePtr();
            ref var bi = ref UnsafeUtility.AsRef<BatchInfo>(batchInfos + batchIndex);
            int prev = bi.PrevSameArch;
            int next = bi.NextSameArch;
            if (prev != -1) UnsafeUtility.AsRef<BatchInfo>(batchInfos + prev).NextSameArch = next;
            else m_ArchHead[arch] = next;
            if (next != -1) UnsafeUtility.AsRef<BatchInfo>(batchInfos + next).PrevSameArch = prev;
            bi.PrevSameArch = -1; bi.NextSameArch = -1;
        }

#else
        private void AddBatchIndex(int id)
        {
            Assert.IsTrue(!m_SortedBatchIds.Contains(id), "New batch ID already marked as used");
            m_SortedBatchIds.Add(id);
            m_ExistingBatchIndices.Add(id);
            EnsureHaveSpaceForNewBatch();
        }

        private void RemoveBatchIndex(int id)
        {
            if (!m_SortedBatchIds.Contains(id))
                Assert.IsTrue(false, $"Attempted to release an unused id {id}");
            m_SortedBatchIds.Remove(id);
            m_ExistingBatchIndices.Remove(id);
        }
        private void RemoveBatch(int batchIndex)
        {
            var batchInfo = m_BatchInfos[batchIndex];
            m_BatchInfos[batchIndex] = default;

#if DEBUG_LOG_BATCH_DELETION
            Debug.Log($"BATCH REMOVED: ID={batchIndex}, ArchetypeIndex={batchInfo.GraphicsArchetypeIndex}, SubbatchAllocator.IsCreated={batchInfo.SubbatchAllocator.IsCreated}");
#endif

            RemoveBatchIndex(batchIndex);

            if (!batchInfo.GPUMemoryAllocation.Empty)
            {
                m_GPUPersistentAllocator.Release(batchInfo.GPUMemoryAllocation);
#if DEBUG_LOG_MEMORY_USAGE
                Debug.Log($"RELEASE; {batchInfo.GPUMemoryAllocation.Length}");
#endif
            }

            var metadataAllocation = batchInfo.ChunkMetadataAllocation;
            if (!metadataAllocation.Empty)
            {
                for (ulong j = metadataAllocation.begin; j < metadataAllocation.end; ++j)
                    m_ChunkProperties[(int)j] = default;

                m_ChunkMetadataAllocator.Release(metadataAllocation);
            }

            m_ThreadedBatchContext.RemoveBatch(new BatchID { value = (uint) batchIndex });
        }
#endif

#if ENABLE_BATCH_OPTIMIZATION
        private int BatchIndexRange => m_MaxBatchIdPlusOne;
#else
        private int BatchIndexRange => m_SortedBatchIds.Max + 1;
#endif
        private void Dispose()
        {

            m_GPUPersistentInstanceData?.Dispose();

            if(m_TextureSceneList != null)
            {
                foreach(var texture in m_TextureSceneList)
                    UnityEngine.Object.DestroyImmediate(texture);
            }
#if UNITY_EDITOR
            Memory.Unmanaged.Free(m_PerThreadStats, Allocator.Persistent);

            m_PerThreadStats = null;
#endif

            if (ErrorShaderEnabled)
                Material.DestroyImmediate(m_ErrorMaterial);

            if (LoadingShaderEnabled)
                Material.DestroyImmediate(m_LoadingMaterial);

#if ENABLE_PICKING
            Material.DestroyImmediate(m_PickingMaterial);
#endif

            m_BatchRendererGroup.Dispose();
            m_ThreadedBatchContext.batchRendererGroup = IntPtr.Zero;

            m_ForceLowLOD.Dispose();
            m_ResetLod = true;
            m_NameIDToMaterialProperties.Dispose();
            m_TypeIndexToMaterialProperty.Dispose();
            m_GPUPersistentAllocator.Dispose();
#if ENABLE_BATCH_OPTIMIZATION
            m_SubBatchAllocator.Dispose();
            if (m_ArchHead.IsCreated) m_ArchHead.Dispose();
#endif
            m_ChunkMetadataAllocator.Dispose();

            m_BatchInfos.Dispose();
            m_ChunkProperties.Dispose();
            m_ExistingBatchIndices.Dispose();
            m_ValueBlits.Dispose();
            m_ComponentTypeCache.Dispose();

            m_GraphicsArchetypes.Dispose();

            m_FilterSettings.Dispose();
            m_SortingOrders.Dispose();
            m_CullingJobReleaseDependency.Complete();
            m_ReleaseDependency.Complete();
            m_ThreadLocalAllocators.Dispose();
        }

        private void ResetLod()
        {
            m_PrevLODParams = new LODGroupExtensions.LODParams();
            m_ResetLod = true;
        }

        private void RewindThreadLocalAllocator()
        {
            m_CullingJobReleaseDependency.Complete();
            m_CullingJobReleaseDependency = default;
            m_ReleaseDependency.Complete();
            m_ReleaseDependency = default;
            m_ThreadLocalAllocators.Rewind();
            m_NumberOfCullingPassesAccumulatedWithoutAllocatorRewind = 0;
        }

        static IncludeExcludeListFilter GetPickingIncludeExcludeListFilterForCurrentCullingCallback(EntityManager entityManager, in BatchCullingContext cullingContext)
        {
#if ENABLE_PICKING && !DISABLE_INCLUDE_EXCLUDE_LIST_FILTERING
            PickingIncludeExcludeList includeExcludeList = default;

            if (cullingContext.viewType == BatchCullingViewType.Picking)
            {
                includeExcludeList = HandleUtility.GetPickingIncludeExcludeList(Allocator.Temp);
            }
            else if (cullingContext.viewType == BatchCullingViewType.SelectionOutline)
            {
                includeExcludeList = HandleUtility.GetSelectionOutlineIncludeExcludeList(Allocator.Temp);
            }

            NativeArray<int> emptyArray = new NativeArray<int>(0, Allocator.Temp);

            NativeArray<int> includeEntityIndices = includeExcludeList.IncludeEntities;
            if (cullingContext.viewType == BatchCullingViewType.SelectionOutline)
            {

                if (!includeEntityIndices.IsCreated)
                    includeEntityIndices = emptyArray;
            }
            else if (includeEntityIndices.Length == 0)
            {
                includeEntityIndices = default;
            }

            NativeArray<int> excludeEntityIndices = includeExcludeList.ExcludeEntities;
            if (excludeEntityIndices.Length == 0)
                excludeEntityIndices = default;

            IncludeExcludeListFilter includeExcludeListFilter = new IncludeExcludeListFilter(
                entityManager,
                includeEntityIndices,
                excludeEntityIndices,
                Allocator.TempJob);

            includeExcludeList.Dispose();
            emptyArray.Dispose();

            return includeExcludeListFilter;
#else
            return default;
#endif
        }

        private JobHandle OnPerformCulling(BatchRendererGroup rendererGroup, BatchCullingContext cullingContext, BatchCullingOutput cullingOutput, IntPtr userContext)
        {
            Profiler.BeginSample("OnPerformCulling");

            if (cullingContext.projectionType == BatchCullingProjectionType.Orthographic)
            {
                Profiler.EndSample();
                return default;
            }

            int chunkCount;
            try
            {
                chunkCount = m_EntitiesGraphicsRenderedQueryRO.CalculateChunkCountWithoutFiltering();
            }
            catch (ObjectDisposedException)
            {

                Profiler.EndSample();
                return default;
            }

            if (chunkCount == 0 || !ShouldRunSystem())
            {
                Profiler.EndSample();
                return default;
            }

            IncludeExcludeListFilter includeExcludeListFilter = GetPickingIncludeExcludeListFilterForCurrentCullingCallback(EntityManager, cullingContext);

            if (includeExcludeListFilter.IsIncludeEnabled && includeExcludeListFilter.IsIncludeEmpty)
            {
                includeExcludeListFilter.Dispose();
                Profiler.EndSample();
                return m_CullingJobDependency;
            }

            if (m_NumberOfCullingPassesAccumulatedWithoutAllocatorRewind == kMaxCullingPassesWithoutAllocatorRewind)
            {
                RewindThreadLocalAllocator();
            }
            ++m_NumberOfCullingPassesAccumulatedWithoutAllocatorRewind;

            var lodParams = LODGroupExtensions.CalculateLODParams(cullingContext.lodParameters);

            JobHandle cullingDependency;
            var resetLod = m_ResetLod || (!lodParams.Equals(m_PrevLODParams));
            if (resetLod)
            {

                var lodJobDependency = JobHandle.CombineDependencies(m_CullingJobDependency,
                    m_CullingJobDependencyGroup.GetDependency());

                float cameraMoveDistance = math.length(m_PrevCameraPos - lodParams.cameraPos);
                var lodDistanceScaleChanged = lodParams.distanceScale != m_PrevLodDistanceScale;

#if UNITY_EDITOR

                m_CamMoveDistance = cameraMoveDistance;
#endif

                var selectLodEnabledJob = new SelectLodEnabled
                {
                    SimpleChunkLOD = m_SimpleChunkLOD,
                    ForceLowLOD = m_ForceLowLOD,
                    LODParams = lodParams,
                    RootLODRanges = GetComponentTypeHandle<RootLODRange>(true),
                    RootLODReferencePoints = GetComponentTypeHandle<RootLODWorldReferencePoint>(true),
                    LODRanges = GetComponentTypeHandle<LODRange>(true),
                    LODReferencePoints = GetComponentTypeHandle<LODWorldReferencePoint>(true),
                    EntitiesGraphicsChunkInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(),
                    ChunkHeader = GetComponentTypeHandle<ChunkHeader>(),
                    ChunkSimpleLODs = GetComponentTypeHandle<ChunkSimpleLOD>(),
                    CameraMoveDistanceFixed16 =
                        Fixed16CamDistance.FromFloatCeil(cameraMoveDistance * lodParams.distanceScale),
                    DistanceScale = lodParams.distanceScale,
                    DistanceScaleChanged = lodDistanceScaleChanged,
                    MaximumLODLevelMask = 1 << QualitySettings.maximumLODLevel,
#if UNITY_EDITOR
                    Stats = m_PerThreadStats,
#endif
                };

                cullingDependency = m_LODDependency = selectLodEnabledJob.ScheduleParallel(m_LodSelectGroup, lodJobDependency);

                m_PrevLODParams = lodParams;
                m_PrevLodDistanceScale = lodParams.distanceScale;
                m_PrevCameraPos = lodParams.cameraPos;
                m_ResetLod = false;
#if UNITY_EDITOR
#if UNITY_2022_2_14F1_OR_NEWER
                int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
                int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif
                UnsafeUtility.MemClear(m_PerThreadStats, sizeof(EntitiesGraphicsPerThreadStats) * maxThreadCount);
#endif
            }
            else
            {

                cullingDependency = JobHandle.CombineDependencies(
                    m_LODDependency,
                    m_CullingJobDependency,
                    m_CullingJobDependencyGroup.GetDependency());
            }

            var visibilityItems = new IndirectList<ChunkVisibilityItem>(
                chunkCount,
                m_ThreadLocalAllocators.GeneralAllocator);

            bool cullLightmapShadowCasters = (cullingContext.cullingFlags & BatchCullingFlags.CullLightmappedShadowCasters) != 0;

            var planarShadowSelect = new PlanarShadowSelect
            {
                BoundsComponent = GetComponentTypeHandle<WorldRenderBounds>(true),
                EntitiesGraphicsChunkInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(),
                cameraPos = lodParams.cameraPos,
                maxDistSq = m_PlanarShadowCullDist*m_PlanarShadowCullDist,
            };

            var planarShadowCullingHandle = planarShadowSelect.ScheduleParallel(m_PlanarShadowQuery, cullingDependency);

            var frustumCullingJob = new FrustumCullingJob
            {
                Splits = CullingSplits.Create(&cullingContext, QualitySettings.shadowProjection, m_ThreadLocalAllocators.GeneralAllocator->Handle),
                CullingViewType = cullingContext.viewType,
                EntitiesGraphicsChunkInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(true),
                ChunkWorldRenderBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true),
                BoundsComponent = GetComponentTypeHandle<WorldRenderBounds>(true),
                EntityHandle = GetEntityTypeHandle(),
                IncludeExcludeListFilter = includeExcludeListFilter,
                VisibilityItems = visibilityItems,
                ThreadLocalAllocator = m_ThreadLocalAllocators,
                CullLightmapShadowCasters = cullLightmapShadowCasters,

#if UNITY_EDITOR
                Stats = m_PerThreadStats,
#endif
            };

            var frustumCullingJobHandle = frustumCullingJob.ScheduleParallel(m_EntitiesGraphicsRenderedQueryRO, planarShadowCullingHandle);
            var disposeFrustumCullingHandle = frustumCullingJob.IncludeExcludeListFilter.Dispose(frustumCullingJobHandle);
            DidScheduleCullingJob(frustumCullingJobHandle);

            int binCountEstimate = 1;
            var chunkDrawCommandOutput = new ChunkDrawCommandOutput(
                binCountEstimate,
                m_ThreadLocalAllocators,
                cullingOutput);

            var brgRenderMeshArrays =
                World.GetExistingSystemManaged<RegisterMaterialsAndMeshesSystem>()?.BRGRenderMeshArrays
                ?? new NativeParallelHashMap<int, BRGRenderMeshArray>();

            var emitDrawCommandsJob = new EmitDrawCommandsJob
            {
                UseSplitMask = cullingContext.viewType == BatchCullingViewType.Light,
                VisibilityItems = visibilityItems,
                EntitiesGraphicsChunkInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(true),
                MaterialMeshInfo = GetComponentTypeHandle<MaterialMeshInfo>(true),
                LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true),
                DepthSorted = GetComponentTypeHandle<DepthSorted_Tag>(true),
                RenderFilterSettings = GetSharedComponentTypeHandle<RenderFilterSettings>(),
                FilterSettings = m_FilterSettings,
                SortingOrders = m_SortingOrders,
                CullingLayerMask = cullingContext.cullingLayerMask,
                RenderMeshArray = GetSharedComponentTypeHandle<RenderMeshArray>(),
                BRGRenderMeshArrays = brgRenderMeshArrays,
#if UNITY_EDITOR
                EditorDataComponentHandle = GetSharedComponentTypeHandle<EditorRenderData>(),
#endif
                DrawCommandOutput = chunkDrawCommandOutput,
                SceneCullingMask = cullingContext.sceneCullingMask,
                CameraPosition = lodParams.cameraPos,
                LastSystemVersion = m_LastSystemVersionAtLastUpdate,

                ProfilerEmitChunk = new ProfilerMarker("EmitChunk"),
            };

            var allocateWorkItemsJob = new AllocateWorkItemsJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
            };

            var collectWorkItemsJob = new CollectWorkItemsJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
                ProfileCollect = new ProfilerMarker("Collect"),
                ProfileWrite = new ProfilerMarker("Write"),
            };

            var flushWorkItemsJob = new FlushWorkItemsJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
            };

            var allocateInstancesJob = new AllocateInstancesJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
            };

            var allocateDrawCommandsJob = new AllocateDrawCommandsJob
            {
                DrawCommandOutput = chunkDrawCommandOutput
            };

            var expandInstancesJob = new ExpandVisibleInstancesJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
            };

            var generateDrawCommandsJob = new GenerateDrawCommandsJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
#if UNITY_EDITOR
                Stats = m_PerThreadStats,
                ViewType = cullingContext.viewType,
#endif
            };

            var generateDrawRangesJob = new GenerateDrawRangesJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
                FilterSettings = m_FilterSettings,
#if UNITY_EDITOR
                Stats = m_PerThreadStats,
#endif
            };

            var emitDrawCommandsDependency = emitDrawCommandsJob.ScheduleWithIndirectList(visibilityItems, 1, m_CullingJobDependency);

            var collectGlobalBinsDependency =
                chunkDrawCommandOutput.BinCollector.ScheduleFinalize(emitDrawCommandsDependency);
            var sortBinsDependency = DrawBinSort.ScheduleBinSort(
                m_ThreadLocalAllocators.GeneralAllocator,
                chunkDrawCommandOutput.SortedBins,
                chunkDrawCommandOutput.UnsortedBins,
                collectGlobalBinsDependency);

            var allocateWorkItemsDependency = allocateWorkItemsJob.Schedule(collectGlobalBinsDependency);
            var collectWorkItemsDependency = collectWorkItemsJob.ScheduleWithIndirectList(
                chunkDrawCommandOutput.UnsortedBins, 1, allocateWorkItemsDependency);

            var flushWorkItemsDependency =
                flushWorkItemsJob.Schedule(ChunkDrawCommandOutput.NumThreads, 1, collectWorkItemsDependency);

            var allocateInstancesDependency = allocateInstancesJob.Schedule(flushWorkItemsDependency);

            var allocateDrawCommandsDependency = allocateDrawCommandsJob.Schedule(
                JobHandle.CombineDependencies(sortBinsDependency, flushWorkItemsDependency));

            var allocationsDependency = JobHandle.CombineDependencies(
                allocateInstancesDependency,
                allocateDrawCommandsDependency);

            var expandInstancesDependency = expandInstancesJob.ScheduleWithIndirectList(
                chunkDrawCommandOutput.WorkItems,
                1,
                allocateInstancesDependency);
            var generateDrawCommandsDependency = generateDrawCommandsJob.ScheduleWithIndirectList(
                chunkDrawCommandOutput.SortedBins,
                1,
                allocationsDependency);
            var generateDrawRangesDependency = generateDrawRangesJob.Schedule(allocateDrawCommandsDependency);

            var expansionDependency = JobHandle.CombineDependencies(
                expandInstancesDependency,
                generateDrawCommandsDependency,
                generateDrawRangesDependency);

#if DEBUG_VALIDATE_DRAW_COMMAND_SORT
            expansionDependency = new DebugValidateSortJob
            {
                DrawCommandOutput = chunkDrawCommandOutput,
            }.Schedule(expansionDependency);
#endif

#if DEBUG_LOG_DRAW_COMMANDS || DEBUG_LOG_DRAW_COMMANDS_VERBOSE
            DebugDrawCommands(expansionDependency, cullingOutput);
#endif

            m_CullingJobReleaseDependency = JobHandle.CombineDependencies(
                m_CullingJobReleaseDependency,
                disposeFrustumCullingHandle,
                chunkDrawCommandOutput.Dispose(expansionDependency));

            DidScheduleCullingJob(emitDrawCommandsDependency);
            DidScheduleCullingJob(expansionDependency);

            Profiler.EndSample();
            return m_CullingJobDependency;
        }

        private void DebugDrawCommands(JobHandle drawCommandsDependency, BatchCullingOutput cullingOutput)
        {
            drawCommandsDependency.Complete();

            var drawCommands = cullingOutput.drawCommands[0];

            Debug.Log($"Draw Command summary: visibleInstanceCount: {drawCommands.visibleInstanceCount} drawCommandCount: {drawCommands.drawCommandCount} drawRangeCount: {drawCommands.drawRangeCount}");

#if DEBUG_LOG_DRAW_COMMANDS_VERBOSE
            bool verbose = true;
#else
            bool verbose = false;
#endif
            if (verbose)
            {
                for (int i = 0; i < drawCommands.drawCommandCount; ++i)
                {
                    var cmd = drawCommands.drawCommands[i];
                    DrawCommandSettings settings = new DrawCommandSettings
                    {
                        BatchID = cmd.batchID,
                        MaterialID = cmd.materialID,
                        MeshID = cmd.meshID,
                        SubMeshIndex = cmd.submeshIndex,
                        Flags = cmd.flags,
                    };
                    Debug.Log($"Draw Command #{i}: {settings} visibleOffset: {cmd.visibleOffset} visibleCount: {cmd.visibleCount}");
                    StringBuilder sb = new StringBuilder((int)cmd.visibleCount * 30);
                    bool hasSortingPosition = settings.HasSortingPosition;
                    for (int j = 0; j < cmd.visibleCount; ++j)
                    {
                        sb.Append(drawCommands.visibleInstances[cmd.visibleOffset + j]);
                        if (hasSortingPosition)
                            sb.AppendFormat(" ({0:F3} {1:F3} {2:F3})",
                                drawCommands.instanceSortingPositions[cmd.sortingPosition + 0],
                                drawCommands.instanceSortingPositions[cmd.sortingPosition + 1],
                                drawCommands.instanceSortingPositions[cmd.sortingPosition + 2]);
                        sb.Append(", ");
                    }
                    Debug.Log($"Draw Command #{i} instances: [{sb}]");
                }
            }
        }

        private JobHandle UpdateAllBatches(JobHandle inputDependencies)
        {
            Profiler.BeginSample("GetComponentTypes");
#if UNITY_2022_2_14F1_OR_NEWER
            int maxThreadCount = JobsUtility.ThreadIndexCount;
#else
            int maxThreadCount = JobsUtility.MaxJobThreadCount;
#endif

            var entitiesGraphicsRenderedChunkType= GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(false);
            var entitiesGraphicsRenderedChunkTypeRO = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(true);
            var chunkHeadersRO = GetComponentTypeHandle<ChunkHeader>(true);
            var chunkWorldRenderBoundsRO = GetComponentTypeHandle<ChunkWorldRenderBounds>(true);
            var localToWorldsRO = GetComponentTypeHandle<LocalToWorld>(true);
            var lodRangesRO = GetComponentTypeHandle<LODRange>(true);
            var rootLodRangesRO = GetComponentTypeHandle<RootLODRange>(true);
            var materialMeshInfosRO = GetComponentTypeHandle<MaterialMeshInfo>(true);

            m_ComponentTypeCache.FetchTypeHandles(this);

            Profiler.EndSample();

            var numNewChunksArray = new NativeArray<int>(1, Allocator.TempJob);
            int totalChunksWithNormalQuery = m_EntitiesGraphicsRenderedQuery.CalculateChunkCountWithoutFiltering();

            int totalChunksWithMetaEntityQuery = m_MetaEntitiesForHybridRenderableChunksQuery.CalculateEntityCountWithoutFiltering();

            int totalChunks = math.max(totalChunksWithNormalQuery, totalChunksWithMetaEntityQuery);
            var newChunks = new NativeArray<ArchetypeChunk>(
                totalChunks,
                Allocator.TempJob,
                NativeArrayOptions.UninitializedMemory);

            var classifyNewChunksJob = new ClassifyNewChunksJob
                {
                    EntitiesGraphicsChunkInfo = entitiesGraphicsRenderedChunkTypeRO,
                    ChunkHeader = chunkHeadersRO,
                    NumNewChunks = numNewChunksArray,
                    NewChunks = newChunks
                }
                .ScheduleParallel(m_MetaEntitiesForHybridRenderableChunksQuery, inputDependencies);

            JobHandle entitiesGraphicsCompleted = new JobHandle();

            const int kNumBitsPerLong = sizeof(long) * 8;
            var unreferencedBatchIndices = new NativeArray<long>(
                (BatchIndexRange + kNumBitsPerLong) / kNumBitsPerLong,
                Allocator.TempJob,
                NativeArrayOptions.ClearMemory);

            JobHandle initializedUnreferenced = default;
#if ENABLE_BATCH_OPTIMIZATION
            var existingKeys = m_ExistingSubBatchIndices.ToNativeArray(Allocator.TempJob);
#else
            var existingKeys = m_ExistingBatchIndices.ToNativeArray(Allocator.TempJob);
#endif
            initializedUnreferenced = new InitializeUnreferencedIndicesScatterJob
            {
                ExistingBatchIndices = existingKeys,
                UnreferencedBatchIndices = unreferencedBatchIndices,
            }.Schedule(existingKeys.Length, kNumScatteredIndicesPerThread);

            const int kNumDisposeJobHandles = 5;
            int numDisposeJobHandles = 0;
            var disposeJobHandles = new NativeArray<JobHandle>(kNumDisposeJobHandles, Allocator.Temp);
            disposeJobHandles[numDisposeJobHandles++] = existingKeys.Dispose(initializedUnreferenced);

            inputDependencies = JobHandle.CombineDependencies(inputDependencies, initializedUnreferenced);

            int conservativeMaximumGpuUploads = totalChunks * m_ComponentTypeCache.UsedTypeCount;
            var gpuUploadOperations = new NativeList<GpuUploadOperation>(
                conservativeMaximumGpuUploads,
                Allocator.TempJob);

            uint lastSystemVersion = LastSystemVersion;

            classifyNewChunksJob.Complete();
            int numNewChunks = numNewChunksArray[0];

            var maxBatchCount = math.max(kInitialMaxBatchCount, BatchIndexRange + numNewChunks);

            var maxBatchLongCount = (maxBatchCount + kNumBitsPerLong - 1) / kNumBitsPerLong;

            var entitiesGraphicsChunkUpdater = new EntitiesGraphicsChunkUpdater
            {
                ComponentTypes = m_ComponentTypeCache.ToBurstCompatible(Allocator.TempJob),
                UnreferencedBatchIndices = unreferencedBatchIndices,
                ChunkProperties = m_ChunkProperties,
                LastSystemVersion = lastSystemVersion,

                GpuUploadOperationsWriter = gpuUploadOperations.AsParallelWriter(),

                LocalToWorldType = TypeManager.GetTypeIndex<LocalToWorld>(),
                WorldToLocalType = TypeManager.GetTypeIndex<WorldToLocal_Tag>(),

                ThreadIndex = 0,

#if PROFILE_BURST_JOB_INTERNALS
                ProfileAddUpload = new ProfilerMarker("AddUpload"),
#endif
            };

            var updateOldJob = new UpdateOldEntitiesGraphicsChunksJob
            {
                EntitiesGraphicsChunkInfo = entitiesGraphicsRenderedChunkType,
                ChunkWorldRenderBounds = chunkWorldRenderBoundsRO,
                ChunkHeader = chunkHeadersRO,
                LocalToWorld = localToWorldsRO,
                LodRange = lodRangesRO,
                RootLodRange = rootLodRangesRO,
                MaterialMeshInfo = materialMeshInfosRO,
                EntitiesGraphicsChunkUpdater = entitiesGraphicsChunkUpdater,
            };

            JobHandle updateOldDependencies = inputDependencies;

            updateOldJob.ScheduleParallel(m_MetaEntitiesForHybridRenderableChunksQuery, updateOldDependencies).Complete();

            Profiler.BeginSample("GarbageCollectUnreferencedBatches");
            int numRemoved = GarbageCollectUnreferencedBatches(unreferencedBatchIndices);
            Profiler.EndSample();

            if (numNewChunks > 0)
            {
                Profiler.BeginSample("AddNewChunks");
                int numValidNewChunks = AddNewChunks(newChunks.GetSubArray(0, numNewChunks));
                Profiler.EndSample();

                entitiesGraphicsChunkUpdater.ChunkProperties = m_ChunkProperties;
                var updateNewChunksJob = new UpdateNewEntitiesGraphicsChunksJob
                {
                    NewChunks = newChunks,
                    EntitiesGraphicsChunkInfo = entitiesGraphicsRenderedChunkTypeRO,
                    ChunkWorldRenderBounds = chunkWorldRenderBoundsRO,
                    EntitiesGraphicsChunkUpdater = entitiesGraphicsChunkUpdater,
                };

#if DEBUG_LOG_INVALID_CHUNKS
                if (numValidNewChunks != numNewChunks)
                    Debug.Log($"Tried to add {numNewChunks} new chunks, but only {numValidNewChunks} were valid, {numNewChunks - numValidNewChunks} were invalid");
#endif

                entitiesGraphicsCompleted = updateNewChunksJob.Schedule(numValidNewChunks, kNumNewChunksPerThread);
            }

            disposeJobHandles[numDisposeJobHandles++] = entitiesGraphicsChunkUpdater.ComponentTypes.Dispose(entitiesGraphicsCompleted);
            disposeJobHandles[numDisposeJobHandles++] = newChunks.Dispose(entitiesGraphicsCompleted);
            disposeJobHandles[numDisposeJobHandles++] = numNewChunksArray.Dispose(entitiesGraphicsCompleted);

            var drawCommandFlagsUpdated = new UpdateDrawCommandFlagsJob
            {
                LocalToWorld = GetComponentTypeHandle<LocalToWorld>(true),
                RenderFilterSettings = GetSharedComponentTypeHandle<RenderFilterSettings>(),
                EntitiesGraphicsChunkInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(),
                FilterSettings = m_FilterSettings,
                DefaultFilterSettings = MakeFilterSettings(RenderFilterSettings.Default),
            }.ScheduleParallel(m_ChangedTransformQuery, entitiesGraphicsCompleted);
            DidScheduleUpdateJob(drawCommandFlagsUpdated);

            if (m_UseDirectUpload)
            {
                CheckGPUPersistentResize();

                m_DirectUploader.EnsureCapacity(totalChunks * m_ComponentTypeCache.UsedTypeCount * 2);

                unsafe
                {
                    var copyJob = new CopyDirectUploadDataJob
                    {
                        Operations = gpuUploadOperations.AsDeferredJobArray(),
                        SystemMemoryPtr = m_SystemMemoryBuffer.GetUnsafePtr(),
                        PendingUploads = m_DirectUploader.AsParallelWriter(),
                        UseConstantBuffer = m_DirectUploader.UseConstantBuffer,
                        WindowSizeInFloat4 = m_DirectUploader.WindowSizeInFloat4,
                    };

                    entitiesGraphicsCompleted = copyJob.ScheduleByRef(gpuUploadOperations, 16, entitiesGraphicsCompleted);
                }

            }

            entitiesGraphicsCompleted.Complete();

            int numGpuUploadOperations = gpuUploadOperations.Length;
            Assert.IsTrue(numGpuUploadOperations <= gpuUploadOperations.Length, "Maximum GPU upload operation count exceeded");

            ComputeUploadSizeRequirements(
                numGpuUploadOperations, gpuUploadOperations.AsArray(),
                out int numOperations, out int totalUploadBytes, out int biggestUploadBytes);

#if DEBUG_LOG_UPLOADS
            if (numOperations > 0)
            {
                Debug.Log($"GPU upload operations: {numOperations}, GPU upload bytes: {totalUploadBytes}");
            }
#endif
            if (m_UseDirectUpload)
            {

                Profiler.BeginSample("UploadAllBlitsDirect");
                UploadAllBlitsDirect();
                Profiler.EndSample();

                gpuUploadOperations.Dispose();
            }
            else
            {

            }

            unreferencedBatchIndices.Dispose();

            m_ReleaseDependency = JobHandle.CombineDependencies(
                    disposeJobHandles.Slice(0, numDisposeJobHandles));
            JobHandle outputDeps = JobHandle.CombineDependencies(

                drawCommandFlagsUpdated,
                m_ReleaseDependency);

            disposeJobHandles.Dispose();

            return outputDeps;
        }

        [BurstCompile]
        internal struct CopyDirectUploadDataJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<GpuUploadOperation> Operations;
            [NativeDisableUnsafePtrRestriction] public unsafe void* SystemMemoryPtr;

            public NativeList<DirectUploader.UploadRequest>.ParallelWriter PendingUploads;
            public bool UseConstantBuffer;
            public int WindowSizeInFloat4;

            public unsafe void Execute(int index)
            {
                var operation = Operations[index];

                if (operation.Kind == GpuUploadOperation.UploadOperationKind.SOAMatrixUpload3x4)
                {
                    var srcLocal = (byte*)operation.Src;
                    var dstLocal = (byte*)SystemMemoryPtr + operation.DstOffset;

                    for (int k = 0; k < operation.Size; ++k)
                    {
                        for (int j = 0; j < 4; ++j)
                        {
                            UnsafeUtility.MemCpy(dstLocal, srcLocal, 12);
                            dstLocal += 12;
                            srcLocal += 16;
                        }
                    }
                }
                else
                {
                    UnsafeUtility.MemCpy((byte*)SystemMemoryPtr + operation.DstOffset, operation.Src, operation.Size);
                }

                int destOffsetInFloat4 = operation.DstOffset / 16;
                int sizeInFloat4 = (operation.BytesRequiredInUploadBuffer + 15) / 16;

                if (sizeInFloat4 <= 0) return;

                if (UseConstantBuffer)
                {
                    int sourceOffset = destOffsetInFloat4;
                    int destOffset = destOffsetInFloat4;
                    int size = sizeInFloat4;

                    while (size > 0)
                    {
                        int offsetInWindow = destOffset % WindowSizeInFloat4;
                        int remainingInWindow = WindowSizeInFloat4 - offsetInWindow;
                        int chunkSize = math.min(size, remainingInWindow);

                        PendingUploads.AddNoResize(new DirectUploader.UploadRequest
                        {
                            SourceOffset = sourceOffset,
                            DestinationOffset = destOffset,
                            SizeInFloat4 = chunkSize
                        });

                        sourceOffset += chunkSize;
                        destOffset += chunkSize;
                        size -= chunkSize;
                    }
                }
                else
                {
                    PendingUploads.AddNoResize(new DirectUploader.UploadRequest
                    {
                        SourceOffset = destOffsetInFloat4,
                        DestinationOffset = destOffsetInFloat4,
                        SizeInFloat4 = sizeInFloat4
                    });
                }
            }
        }

        private void UploadAllBlitsDirect()
        {

            for (int i = 0; i < m_ValueBlits.Length; i++)
            {
                var blit = m_ValueBlits[i];
                int destOffset = (int)blit.DestinationOffset;

                unsafe
                {

                    byte* destPtr = (byte*)m_SystemMemoryBuffer.GetUnsafePtr() + destOffset;
                    UnsafeUtility.MemCpy(destPtr, &blit.Value, blit.BytesRequiredInUploadBuffer);
                }

                int destOffsetInFloat4 = destOffset / 16;
                int totalSizeInFloat4 = (blit.BytesRequiredInUploadBuffer + 15) / 16;
                m_DirectUploader.QueueUpload(destOffsetInFloat4, destOffsetInFloat4, totalSizeInFloat4);
            }

            m_DirectUploader.ExecuteUploads();
            m_ValueBlits.Clear();
        }

        private void ComputeUploadSizeRequirements(
            int numGpuUploadOperations, NativeArray<GpuUploadOperation> gpuUploadOperations,
            out int numOperations, out int totalUploadBytes, out int biggestUploadBytes)
        {
            numOperations = numGpuUploadOperations + m_ValueBlits.Length;
            totalUploadBytes = 0;
            biggestUploadBytes = 0;

            for (int i = 0; i < numGpuUploadOperations; ++i)
            {
                var numBytes = gpuUploadOperations[i].BytesRequiredInUploadBuffer;
                totalUploadBytes += numBytes;
                biggestUploadBytes = math.max(biggestUploadBytes, numBytes);
            }

            for (int i = 0; i < m_ValueBlits.Length; ++i)
            {
                var numBytes = m_ValueBlits[i].BytesRequiredInUploadBuffer;
                totalUploadBytes += numBytes;
                biggestUploadBytes = math.max(biggestUploadBytes, numBytes);
            }
        }

        private int GarbageCollectUnreferencedBatches(NativeArray<long> unreferencedBatchIndices)
        {
            int numRemoved = 0;

            int firstInQw = 0;
            for (int i = 0; i < unreferencedBatchIndices.Length; ++i)
            {
                long qw = unreferencedBatchIndices[i];
                while (qw != 0)
                {
                    int setBit = math.tzcnt(qw);
                    long mask = ~(1L << setBit);
#if ENABLE_BATCH_OPTIMIZATION
                    int subbatchIndex = firstInQw + setBit;

                    if (subbatchIndex >= 0 && subbatchIndex < m_SubBatchAllocator.m_Length)
                    {
                        SubBatch* subBatch = m_SubBatchAllocator.GetUnsafePtr() + subbatchIndex;

                        if (subBatch->BatchID != SubBatchAllocator.InvalidBatchNumber)
                        {
                            RemoveSubBatch(subbatchIndex);
                            ++numRemoved;
                        }
#if DEBUG_LOG_BATCH_CREATION
                        else
                        {
                            Debug.LogWarning($"GC: SubBatch {subbatchIndex} already freed, skipping");
                        }
#endif
                    }
#if DEBUG_LOG_BATCH_CREATION
                    else
                    {
                        Debug.LogError($"GC: Invalid subbatchIndex {subbatchIndex}, range [0, {m_SubBatchAllocator.m_Length-1}]");
                    }
#endif
#else
                    int batchIndex = firstInQw + setBit;

                    RemoveBatch(batchIndex);
                    ++numRemoved;
#endif

                    qw &= mask;
                }

                firstInQw += (int)AtomicHelpers.kNumBitsInLong;
            }

#if DEBUG_LOG_GARBAGE_COLLECTION
            Debug.Log($"GarbageCollectUnreferencedBatches(removed: {numRemoved})");
#endif

            return numRemoved;
        }

        static int NumInstancesInChunk(ArchetypeChunk chunk) => chunk.Capacity;

        [BurstCompile]
        static void CreateBatchCreateInfo(
            ref BatchCreateInfoFactory batchCreateInfoFactory,
            ref NativeArray<ArchetypeChunk> newChunks,
            ref NativeArray<BatchCreateInfo> sortedNewChunks,
            out MaterialPropertyType failureProperty
        )
        {
            failureProperty = default;
            failureProperty.TypeIndex = -1;
            for (int i = 0; i < newChunks.Length; ++i)
            {
                sortedNewChunks[i] = batchCreateInfoFactory.Create(newChunks[i], ref failureProperty);
                if (failureProperty.TypeIndex >= 0)
                {
                    return;
                }
            }
            sortedNewChunks.Sort();
        }

        private int AddNewChunks(NativeArray<ArchetypeChunk> newChunks)
        {
            int numValidNewChunks = 0;

            Assert.IsTrue(newChunks.Length > 0, "Attempted to add new chunks, but list of new chunks was empty");

            var batchCreationTypeHandles = new BatchCreationTypeHandles(this);

            var batchCreateInfoFactory = new BatchCreateInfoFactory
            {
                GraphicsArchetypes = m_GraphicsArchetypes,
                TypeIndexToMaterialProperty = m_TypeIndexToMaterialProperty,
            };

            var sortedNewChunks = new NativeArray<BatchCreateInfo>(newChunks.Length, Allocator.Temp);
            CreateBatchCreateInfo(ref batchCreateInfoFactory, ref newChunks, ref sortedNewChunks, out var failureProperty);
            if (failureProperty.TypeIndex >= 0)
            {
                Assert.IsTrue(false, $"TypeIndex mismatch between key and stored property, Type: {failureProperty.TypeName} ({failureProperty.TypeIndex:x8}), Property: {failureProperty.PropertyName} ({failureProperty.NameID:x8})");
            }

            int batchBegin = 0;
            int numInstances = NumInstancesInChunk(sortedNewChunks[0].Chunk);
#if ENABLE_BATCH_OPTIMIZATION
            int maxEntitiesPerBatch = m_GraphicsArchetypes
                .GetGraphicsArchetype(sortedNewChunks[0].GraphicsArchetypeIndex)
                .MaxEntitiesPerCBufferBatch;
#else
            int maxEntitiesPerBatch = m_GraphicsArchetypes
                .GetGraphicsArchetype(sortedNewChunks[0].GraphicsArchetypeIndex)
                .MaxEntitiesPerBatch;
#endif

            for (int i = 1; i <= sortedNewChunks.Length; ++i)
            {
                int instancesInChunk = 0;
                bool breakBatch = false;

                if (i < sortedNewChunks.Length)
                {
                    var cur = sortedNewChunks[i];
                    breakBatch = !sortedNewChunks[batchBegin].Equals(cur);
                    instancesInChunk = NumInstancesInChunk(cur.Chunk);
                }
                else
                {
                    breakBatch = true;
                }

                if (numInstances + instancesInChunk > maxEntitiesPerBatch)
                    breakBatch = true;

                if (breakBatch)
                {
                    int numChunks = i - batchBegin;

                    bool valid = AddNewBatch(
                        batchCreationTypeHandles,
                        sortedNewChunks.GetSubArray(batchBegin, numChunks),
                        numInstances);

                    if (valid)
                        numValidNewChunks += numChunks;
                    else
                        return numValidNewChunks;

                    batchBegin = i;
                    numInstances = instancesInChunk;

                    if (batchBegin < sortedNewChunks.Length)
#if ENABLE_BATCH_OPTIMIZATION
                        maxEntitiesPerBatch = m_GraphicsArchetypes
.GetGraphicsArchetype(sortedNewChunks[batchBegin].GraphicsArchetypeIndex)
.MaxEntitiesPerCBufferBatch;
#else
                        maxEntitiesPerBatch = m_GraphicsArchetypes
                            .GetGraphicsArchetype(sortedNewChunks[batchBegin].GraphicsArchetypeIndex)
                            .MaxEntitiesPerBatch;
#endif
                }
                else
                {
                    numInstances += instancesInChunk;
                }
            }

            sortedNewChunks.Dispose();

            return numValidNewChunks;
        }

        private static int NextAlignedBy16(int size)
        {
            return ((size + 15) >> 4) << 4;
        }


        private static int Ctz(int v)
        {
            int r = 0;
            while ((v >>= 1) != 0) r++;
            return r;
        }
        internal static MetadataValue CreateMetadataValue(int nameID, int gpuAddress, bool isOverridden)
        {
            const uint kPerInstanceDataBit = 0x80000000;

            return new MetadataValue
            {
                NameID = nameID,
                Value = (uint) gpuAddress
                        | (isOverridden ? kPerInstanceDataBit : 0),
            };
        }
#if ENABLE_BATCH_OPTIMIZATION

        private (int batchIndex, int subBatchIndex, int offset) TryFindAvailableBatchForArchetype(
            int graphicsArchetypeIndex, int requiredInstances)
        {
            var ga = m_GraphicsArchetypes.GetGraphicsArchetype(graphicsArchetypeIndex);
            int maxPerBatch = ga.MaxEntitiesPerCBufferBatch;

            if (requiredInstances <= 0 || requiredInstances > maxPerBatch)
                return (-1, -1, -1);

            int reservedSub = m_SubBatchAllocator.Allocate();
            if (reservedSub == -1)
                return (-1, -1, -1);

            int want = requiredInstances;
            if (want > maxPerBatch)
            {
                m_SubBatchAllocator.Dealloc(reservedSub);
                return (-1, -1, -1);
            }

            EnsureArchetypeIndex(graphicsArchetypeIndex);

            var infos = m_BatchInfos.GetUnsafePtr();

            for (int b = m_ArchHead[graphicsArchetypeIndex]; b != -1;)
            {
                BatchInfo* bi = (infos + b);
                int nextB = bi->NextSameArch;

                if (bi->GraphicsArchetypeIndex == graphicsArchetypeIndex && bi->SubbatchAllocator.IsCreated)
                {
                    int off = bi->SubbatchAllocator.Allocate(want);
                    if (off != -1)
                    {

                        if (off + want > maxPerBatch)
                        {
                            bi->SubbatchAllocator.Deallocate(off, want);
                        }
                        else
                        {

                            if (b != m_ArchHead[graphicsArchetypeIndex])
                            {
                                int prev = bi->PrevSameArch;
                                int nxt = bi->NextSameArch;
                                int oldHead = m_ArchHead[graphicsArchetypeIndex];

                                if (prev != InvalidIndex) UnsafeUtility.AsRef<BatchInfo>(infos + prev).NextSameArch = nxt;
                                else m_ArchHead[graphicsArchetypeIndex] = nxt;
                                if (nxt != InvalidIndex) UnsafeUtility.AsRef<BatchInfo>(infos + nxt).PrevSameArch = prev;
                                bi->PrevSameArch = InvalidIndex;
                                bi->NextSameArch = oldHead;
                                if (oldHead != InvalidIndex) UnsafeUtility.AsRef<BatchInfo>(infos + oldHead).PrevSameArch = b;
                                m_ArchHead[graphicsArchetypeIndex] = b;
                            }

                            return (b, reservedSub, off);
                        }
                    }
                }
                b = nextB;
            }

            m_SubBatchAllocator.Dealloc(reservedSub);
            return (-1, -1, -1);
        }

        private bool AddSubBatchToExistingBatch(
            int batchIndex, int subBatchIndex, int offset,
            BatchCreationTypeHandles typeHandles,
            NativeArray<BatchCreateInfo> batchChunks,
            int numInstances)
        {
            var graphicsArchetypeIndex = batchChunks[0].GraphicsArchetypeIndex;
            var graphicsArchetype = m_GraphicsArchetypes.GetGraphicsArchetype(graphicsArchetypeIndex);
            var overrides = graphicsArchetype.PropertyComponents;
            int numProperties = overrides.Length;
            int batchTotalChunkMetadata = numProperties * batchChunks.Length;

            int maxEntitiesPerBatch = graphicsArchetype.MaxEntitiesPerCBufferBatch;
            if (offset < 0 || numInstances <= 0 || offset + numInstances > maxEntitiesPerBatch)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL BOUNDARY ERROR: Invalid offset/instance range - offset: {offset}, instances: {numInstances}, max capacity: {maxEntitiesPerBatch}, total: {offset + numInstances}");
#endif

                m_SubBatchAllocator.Dealloc(subBatchIndex);
                return false;
            }

            var batchInfo = m_BatchInfos.GetUnsafePtr() + batchIndex;
            if (!batchInfo->SubbatchAllocator.IsCreated || batchInfo->GraphicsArchetypeIndex != graphicsArchetypeIndex)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL ERROR: Invalid batch state - batch {batchIndex}, archetype mismatch or allocator not created");
#endif

                m_SubBatchAllocator.Dealloc(subBatchIndex);
                return false;
            }

            var subBatchPool = m_SubBatchAllocator.GetUnsafePtr();
            var subBatch = subBatchPool + subBatchIndex;

            ulong offsetBegin = (ulong)offset;
            ulong offsetEnd = (ulong)(offset + numInstances);
            if (offsetEnd <= offsetBegin || offsetEnd > (ulong)maxEntitiesPerBatch)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL HEAP BLOCK ERROR: Invalid range [{offsetBegin}, {offsetEnd}), max: {maxEntitiesPerBatch}");
#endif

                m_SubBatchAllocator.Dealloc(subBatchIndex);
                return false;
            }

            subBatch->ChunkOffsetInBatch = new HeapBlock(offsetBegin, offsetEnd);
            subBatch->BatchID = batchIndex;

            subBatch->NextID = batchInfo->HeadSubBatch;
            subBatch->PrevID = SubBatchAllocator.InvalidBatchNumber;

            if (batchInfo->HeadSubBatch != SubBatchAllocator.InvalidBatchNumber)
            {

                if (batchInfo->HeadSubBatch >= 0 && batchInfo->HeadSubBatch < m_SubBatchAllocator.m_Length)
                {
                    subBatchPool[batchInfo->HeadSubBatch].PrevID = subBatchIndex;
                }
                else
                {
#if DEBUG_LOG_BATCH_CREATION
                    Debug.LogError($"CRITICAL CHAIN ERROR: Invalid existing HeadSubBatch {batchInfo->HeadSubBatch} for batch {batchIndex}");
#endif

                    m_SubBatchAllocator.Dealloc(subBatchIndex);
                    return false;
                }
            }

            batchInfo->HeadSubBatch = subBatchIndex;

            AddSubBatchIndex(subBatchIndex);

            subBatch->ChunkMetadataAllocation = m_ChunkMetadataAllocator.Allocate((ulong)batchTotalChunkMetadata);
            if (subBatch->ChunkMetadataAllocation.Empty)
            {
                Debug.LogWarning($"Out of memory in chunk metadata buffer for sub-batch");
                return false;
            }

            var overrideStreamBegin = new NativeArray<int>(overrides.Length, Allocator.Temp);
            int allocationBegin = (int)batchInfo->GPUMemoryAllocation.begin;

            overrideStreamBegin[0] = allocationBegin;
            for (int i = 1; i < numProperties; ++i)
            {
                int sizeBytesComponent = NextAlignedBy16(overrides[i-1].SizeBytesGPU * graphicsArchetype.MaxEntitiesPerCBufferBatch);
                overrideStreamBegin[i] = overrideStreamBegin[i - 1] + sizeBytesComponent;
            }

            var args = new SetBatchChunkDataArgs
            {
                BatchChunks = batchChunks,
                BatchIndex = batchIndex,
                SubBatchIndex = subBatchIndex,
                ChunkProperties = m_ChunkProperties,
                EntityManager = EntityManager,
                NumProperties = numProperties,
                TypeHandles = typeHandles,
                ChunkMetadataBegin = (int)subBatch->ChunkMetadataAllocation.begin,
                ChunkOffsetInBatch = offset,
                OverrideStreamBegin = overrideStreamBegin
            };

            SetBatchChunkData(ref args, ref overrides);

            if (args.ChunkOffsetInBatch != offset + numInstances)
            {
#if DEBUG_LOG_BATCH_CREATION
                Debug.LogError($"CRITICAL INSTANCE COUNT MISMATCH: Expected {offset + numInstances}, got {args.ChunkOffsetInBatch}");
#endif
                return false;
            }

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"✓ BATCH REUSE: Added sub-batch {subBatchIndex} to existing batch {batchIndex}, " +
                     $"GraphicsArchetypeIndex: {graphicsArchetypeIndex}, offset: {offset}, instances: {numInstances}");
#endif

            overrideStreamBegin.Dispose();
            return true;
        }
#endif

        private bool AddNewBatch(
            BatchCreationTypeHandles typeHandles,
            NativeArray<BatchCreateInfo> batchChunks,
            int numInstances)
        {
            int graphicsArchetypeIndex = batchChunks[0].GraphicsArchetypeIndex;
            var graphicsArchetype = m_GraphicsArchetypes.GetGraphicsArchetype(graphicsArchetypeIndex);

            Assert.IsTrue(numInstances > 0, "No instances, expected at least one");
            Assert.IsTrue(batchChunks.Length > 0, "No chunks, expected at least one");

#if ENABLE_BATCH_OPTIMIZATION

            var (existingBatchIndex, subBatchIndex, offset) = TryFindAvailableBatchForArchetype(
                graphicsArchetypeIndex, numInstances);

            if (existingBatchIndex != -1)
            {
                return AddSubBatchToExistingBatch(existingBatchIndex, subBatchIndex, offset,
                    typeHandles, batchChunks, numInstances);
            }
#endif

            var overrides = graphicsArchetype.PropertyComponents;
            var overrideSizes = new NativeArray<int>(overrides.Length, Allocator.Temp);

            int numProperties = overrides.Length;

            Assert.IsTrue(numProperties > 0, "No overridden properties, expected at least one");

            int batchSizeBytes = 0;

            int batchTotalChunkMetadata = numProperties * batchChunks.Length;

#if ENABLE_BATCH_OPTIMIZATION

            for (int i = 0; i < overrides.Length; ++i)
            {

                int sizeBytesComponent = NextAlignedBy16(overrides[i].SizeBytesGPU * graphicsArchetype.MaxEntitiesPerCBufferBatch);
                overrideSizes[i] = sizeBytesComponent;
                batchSizeBytes += sizeBytesComponent;
            }

            BatchInfo batchInfo = default;
            batchInfo.HeadSubBatch = SubBatchAllocator.InvalidBatchNumber;
            batchInfo.NextSameArch = InvalidIndex;
            batchInfo.PrevSameArch = InvalidIndex;
            batchInfo.GPUMemoryAllocation = m_GPUPersistentAllocator.Allocate();
            if (batchInfo.GPUMemoryAllocation.Empty)
            {
                m_GPUPersistentAllocator.Resize(m_GPUPersistentAllocator.MaxBlockCount * 2);
                batchInfo.GPUMemoryAllocation = m_GPUPersistentAllocator.Allocate();

                if (batchInfo.GPUMemoryAllocation.Empty)
                {
                    Debug.LogError($"Out of memory in the Entities Graphics GPU instance data buffer after resize.");
                    return false;
                }
                if (m_UseTextureScene)
                    CheckGPUPersistentResize();
            }
            batchInfo.SubbatchAllocator = new SmallBlockAllocator(graphicsArchetype.MaxEntitiesPerCBufferBatch);

            int allocationBegin = (int)batchInfo.GPUMemoryAllocation.begin;

            uint bindOffset = UseConstantBuffers && !m_UseTextureScene
                ? (uint)allocationBegin
                : 0;
            uint bindWindowSize = UseConstantBuffers && ! m_UseTextureScene
                ? (uint)MaxBytesPerCBuffer
                : 0;

            var overrideStreamBegin = new NativeArray<int>(overrides.Length, Allocator.Temp);
            overrideStreamBegin[0] = allocationBegin;
            for (int i = 1; i < numProperties; ++i)
                overrideStreamBegin[i] = overrideStreamBegin[i - 1] + overrideSizes[i - 1];

            int numMetadata = numProperties + (m_UseTextureScene ? 1 : 0);
            var overrideMetadata = new NativeArray<MetadataValue>(numMetadata, Allocator.Temp);

            int metadataIndex = 0;
            for (int i = 0; i < numProperties; ++i)
            {
                int gpuAddress = overrideStreamBegin[i] - (int)bindOffset;
                overrideMetadata[metadataIndex] = CreateMetadataValue(overrides[i].NameID, gpuAddress, true);
                ++metadataIndex;

#if DEBUG_LOG_PROPERTY_ALLOCATIONS
                Debug.Log($"Property Allocation: Property: {NameIDFormatted(overrides[i].NameID)} Type: {TypeIndexFormatted(overrides[i].TypeIndex)} Metadata: {overrideMetadata[i].Value:x8} Allocation: {overrideStreamBegin[i]}");
#endif
            }
            BatchID batchID;
            if (m_UseTextureScene)
            {
                int pageIdx = allocationBegin / m_TextureScenePageSize;
                int bitCount = Ctz(m_TextureSceneList[pageIdx].width);
                int vtfStridePacked = (bitCount & 0xFF) << 24;
                overrideMetadata[metadataIndex] = CreateMetadataValue(sVtfStrideID, vtfStridePacked, false);
#if UNITY_WEBGL && TUANJIE_1_6_OR_NEWER
                batchID = m_BatchRendererGroup.AddBatch(overrideMetadata, m_GPUPersistentInstanceBufferHandle, bindOffset, bindWindowSize, m_TextureSceneList[pageIdx]);
#else
                batchID = default;
#endif
            }
            else
                batchID = m_ThreadedBatchContext.AddBatch(overrideMetadata, m_GPUPersistentInstanceBufferHandle, bindOffset, bindWindowSize);
            int batchIndex = (int)batchID.value;

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"BATCH CREATED: ID={batchIndex}, ArchetypeIndex={graphicsArchetypeIndex}, chunks={batchChunks.Length}, properties={numProperties}, instances={numInstances}, size={batchSizeBytes}B, buffer={m_GPUPersistentInstanceBufferHandle.value}");
#endif
            Assert.IsTrue(batchIndex != 0, "Failed to add new BatchRendererGroup batch.");

            AddBatchIndex(batchIndex);

            int subBatchID = m_SubBatchAllocator.Allocate();
            if (subBatchID == -1)
            {
                Debug.LogError("Out of sub-batch indices in SubBatchAllocator");
                return false;
            }

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"SUBBATCH ALLOCATED: ID={subBatchID}, BatchID={batchIndex}, ArchetypeIndex={graphicsArchetypeIndex}");
#endif

            var subBatch = m_SubBatchAllocator.GetUnsafePtr() + subBatchID;
            offset = batchInfo.SubbatchAllocator.Allocate(numInstances);

            if (offset == -1)
            {
                Debug.LogError($"Failed to allocate {numInstances} instances in sub-batch allocator");
                m_SubBatchAllocator.Dealloc(subBatchID);
                return false;
            }

            subBatch->ChunkOffsetInBatch = new HeapBlock((ulong)offset, (ulong)(offset + numInstances));
            subBatch->BatchID = batchIndex;
            batchInfo.HeadSubBatch = subBatchID;
            subBatch->ChunkMetadataAllocation = m_ChunkMetadataAllocator.Allocate((ulong)batchTotalChunkMetadata);
            if (subBatch->ChunkMetadataAllocation.Empty)
            {
                Debug.LogWarning($"Out of memory in the Entities Graphics chunk metadata buffer. Attempted to allocate {batchTotalChunkMetadata} elements, buffer size: ");
                return false;
            }

            AddSubBatchIndex(subBatchID);
            EnsureArchetypeIndex(graphicsArchetypeIndex);

            batchInfo.GraphicsArchetypeIndex = graphicsArchetypeIndex;
            batchInfo.PrevSameArch = InvalidIndex;
            batchInfo.NextSameArch = m_ArchHead[graphicsArchetypeIndex];
            m_BatchInfos[batchIndex] = batchInfo;
            if (batchInfo.NextSameArch != InvalidIndex)
            {
                m_BatchInfos.GetUnsafePtr()[batchInfo.NextSameArch].PrevSameArch = batchIndex;
            }
            m_ArchHead[graphicsArchetypeIndex] = batchIndex;

            var args = new SetBatchChunkDataArgs
            {
                BatchChunks = batchChunks,
                BatchIndex = batchIndex,
                SubBatchIndex = subBatchID,
                ChunkProperties = m_ChunkProperties,
                EntityManager = EntityManager,
                NumProperties = numProperties,
                TypeHandles = typeHandles,
                ChunkMetadataBegin = (int)subBatch->ChunkMetadataAllocation.begin,
                ChunkOffsetInBatch = (int)subBatch->ChunkOffsetInBatch.begin,
                OverrideStreamBegin = overrideStreamBegin
            };
            SetBatchChunkData(ref args, ref overrides);

            Assert.IsTrue(args.ChunkOffsetInBatch == (int)subBatch->ChunkOffsetInBatch.begin + numInstances,
                         "Batch instance count mismatch");

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"✓ NEW BATCH: Created batch {batchIndex} with sub-batch {subBatchID}, " +
                     $"GraphicsArchetypeIndex: {graphicsArchetypeIndex}, instances: {numInstances}, " +
                     $"bindOffset: {bindOffset}, bindWindowSize: {bindWindowSize}, " +
                     $"allocationBegin: {allocationBegin}, batchSize: {batchSizeBytes}");
#endif

#else

            for (int i = 0; i < overrides.Length; ++i)
            {

                int sizeBytesComponent = NextAlignedBy16(overrides[i].SizeBytesGPU * numInstances);
                overrideSizes[i] = sizeBytesComponent;
                batchSizeBytes += sizeBytesComponent;
            }

            BatchInfo batchInfo = default;

            batchInfo.ChunkMetadataAllocation = m_ChunkMetadataAllocator.Allocate((ulong)batchTotalChunkMetadata);
            while (batchInfo.ChunkMetadataAllocation.Empty)
            {
                Debug.LogWarning($"Out of memory in the Entities Graphics chunk metadata buffer. Attempted to allocate {batchTotalChunkMetadata} elements, buffer size: {m_ChunkMetadataAllocator.Size}, free size left: {m_ChunkMetadataAllocator.FreeSpace}.");
                int size = m_ChunkProperties.Length * 2;
                while(size < m_ChunkProperties.Length + batchTotalChunkMetadata)
                {
                    size *= 2;
                }
                m_ChunkProperties.ResizeArray(size);
                m_ChunkMetadataAllocator.Resize((ulong)size);

                batchInfo.ChunkMetadataAllocation = m_ChunkMetadataAllocator.Allocate((ulong)batchTotalChunkMetadata);

            }

            batchInfo.GPUMemoryAllocation = m_GPUPersistentAllocator.Allocate((ulong)batchSizeBytes, BatchAllocationAlignment);
            if (batchInfo.GPUMemoryAllocation.Empty)
            {
                Assert.IsTrue(false, $"Out of memory in the Entities Graphics GPU instance data buffer. Attempted to allocate {batchSizeBytes}, buffer size: {m_GPUPersistentAllocator.Size}, free size left: {m_GPUPersistentAllocator.FreeSpace}.");
                return false;
            }

            int allocationBegin = (int)batchInfo.GPUMemoryAllocation.begin;

            uint bindOffset = UseConstantBuffers
                ? (uint)allocationBegin
                : 0;
            uint bindWindowSize = UseConstantBuffers
                ? (uint)MaxBytesPerBatch
                : 0;

            var overrideStreamBegin = new NativeArray<int>(overrides.Length, Allocator.Temp);
            overrideStreamBegin[0] = allocationBegin;
            for (int i = 1; i < numProperties; ++i)
                overrideStreamBegin[i] = overrideStreamBegin[i - 1] + overrideSizes[i - 1];

            int numMetadata = numProperties;
            var overrideMetadata = new NativeArray<MetadataValue>(numMetadata, Allocator.Temp);

            int metadataIndex = 0;
            for (int i = 0; i < numProperties; ++i)
            {
                int gpuAddress = overrideStreamBegin[i] - (int)bindOffset;
                overrideMetadata[metadataIndex] = CreateMetadataValue(overrides[i].NameID, gpuAddress, true);
                ++metadataIndex;

#if DEBUG_LOG_PROPERTY_ALLOCATIONS
                Debug.Log($"Property Allocation: Property: {NameIDFormatted(overrides[i].NameID)} Type: {TypeIndexFormatted(overrides[i].TypeIndex)} Metadata: {overrideMetadata[i].Value:x8} Allocation: {overrideStreamBegin[i]}");
#endif
            }

            var batchID = m_ThreadedBatchContext.AddBatch(overrideMetadata, m_GPUPersistentInstanceBufferHandle,
                bindOffset, bindWindowSize);
            int batchIndex = (int)batchID.value;

#if DEBUG_LOG_BATCH_CREATION
            Debug.Log($"BATCH CREATED: ID={batchIndex}, ArchetypeIndex={graphicsArchetypeIndex}, chunks={batchChunks.Length}, properties={numProperties}, instances={numInstances}, size={batchSizeBytes}B, buffer={m_GPUPersistentInstanceBufferHandle.value}");
#endif
            Assert.IsTrue(batchIndex!=0, "Failed to add new BatchRendererGroup batch.");

            AddBatchIndex(batchIndex);
            m_BatchInfos[batchIndex] = batchInfo;

            var args = new SetBatchChunkDataArgs
            {
                BatchChunks = batchChunks,
                BatchIndex = batchIndex,
                ChunkProperties = m_ChunkProperties,
                EntityManager = EntityManager,
                NumProperties = numProperties,
                TypeHandles = typeHandles,
                ChunkMetadataBegin = (int)batchInfo.ChunkMetadataAllocation.begin,
                ChunkOffsetInBatch = 0,
                OverrideStreamBegin = overrideStreamBegin
            };
            SetBatchChunkData(ref args, ref overrides);

            Assert.IsTrue(args.ChunkOffsetInBatch == numInstances, "Batch instance count mismatch");
#endif
            return true;
        }

        struct SetBatchChunkDataArgs
        {
            public int ChunkMetadataBegin;
            public int ChunkOffsetInBatch;
            public NativeArray<BatchCreateInfo> BatchChunks;
            public int BatchIndex;
            public int SubBatchIndex;
            public int NumProperties;
            public BatchCreationTypeHandles TypeHandles;
            public EntityManager EntityManager;
            public NativeArray<ChunkProperty> ChunkProperties;
            public NativeArray<int> OverrideStreamBegin;
        }

        [BurstCompile]
        static void SetBatchChunkData(ref SetBatchChunkDataArgs args, ref UnsafeList<ArchetypePropertyOverride> overrides)
        {
            var batchChunks = args.BatchChunks;
            int numProperties = args.NumProperties;
            var overrideStreamBegin = args.OverrideStreamBegin;
            int chunkOffsetInBatch = args.ChunkOffsetInBatch;
            int chunkMetadataBegin = args.ChunkMetadataBegin;
            for (int i = 0; i < batchChunks.Length; ++i)
            {
                var chunk = batchChunks[i].Chunk;
                var entitiesGraphicsChunkInfo = new EntitiesGraphicsChunkInfo
                {
                    Valid = true,
                    BatchIndex = args.BatchIndex,
#if ENABLE_BATCH_OPTIMIZATION
                    SubBatchIndex = args.SubBatchIndex,
#endif
                    ChunkTypesBegin = chunkMetadataBegin,
                    ChunkTypesEnd = chunkMetadataBegin + numProperties,
                    CullingData = new EntitiesGraphicsChunkCullingData
                    {
                        Flags = ComputeCullingFlags(chunk, args.TypeHandles),
                        InstanceLodEnableds = default,
                        ChunkOffsetInBatch = chunkOffsetInBatch,
                    },
                };

                args.EntityManager.SetChunkComponentData(chunk, entitiesGraphicsChunkInfo);
                for (int j = 0; j < numProperties; ++j)
                {
                    var propertyOverride = overrides[j];
                    var chunkProperty = new ChunkProperty
                    {
                        ComponentTypeIndex = propertyOverride.TypeIndex,
                        GPUDataBegin = overrideStreamBegin[j] + chunkOffsetInBatch * propertyOverride.SizeBytesGPU,
                        ValueSizeBytesCPU = propertyOverride.SizeBytesCPU,
                        ValueSizeBytesGPU = propertyOverride.SizeBytesGPU,
                    };

                    args.ChunkProperties[chunkMetadataBegin + j] = chunkProperty;
                }

                chunkOffsetInBatch += NumInstancesInChunk(chunk);
                chunkMetadataBegin += numProperties;
            }

            args.ChunkOffsetInBatch = chunkOffsetInBatch;
            args.ChunkMetadataBegin = chunkMetadataBegin;
        }

        static byte ComputeCullingFlags(ArchetypeChunk chunk, BatchCreationTypeHandles typeHandles)
        {
            bool hasLodData = chunk.Has(ref typeHandles.RootLODRange) &&
                              chunk.Has(ref typeHandles.LODRange);

            bool hasPerInstanceCulling = !hasLodData || chunk.Has(ref typeHandles.PerInstanceCulling);

            byte flags = 0;

            if (hasLodData) flags |= EntitiesGraphicsChunkCullingData.kFlagHasLodData;
            if (hasPerInstanceCulling) flags |= EntitiesGraphicsChunkCullingData.kFlagInstanceCulling;

            if (chunk.HasChunkComponent(ref typeHandles.ChunkSimpleLOD)) flags |= EntitiesGraphicsChunkCullingData.kFlagHasChunkLodData;

            return flags;
        }

        private void CompleteJobs(bool completeEverything = false)
        {
            m_CullingJobDependency.Complete();
            m_CullingJobDependencyGroup.CompleteDependency();
            m_CullingJobReleaseDependency.Complete();
            m_ReleaseDependency.Complete();

            if (completeEverything)
            {
                m_EntitiesGraphicsRenderedQuery.CompleteDependency();
                m_LodSelectGroup.CompleteDependency();
                m_ChangedTransformQuery.CompleteDependency();
            }

            m_UpdateJobDependency.Complete();
            m_UpdateJobDependency = new JobHandle();
        }

        private void DidScheduleCullingJob(JobHandle job)
        {
            m_CullingJobDependency = JobHandle.CombineDependencies(job, m_CullingJobDependency);
            m_CullingJobDependencyGroup.AddDependency(job);
        }

        private void DidScheduleUpdateJob(JobHandle job)
        {
            m_UpdateJobDependency = JobHandle.CombineDependencies(job, m_UpdateJobDependency);
        }

        private void CheckGPUPersistentResize()
        {
#if ENABLE_BATCH_OPTIMIZATION
            var persistentBytes = (ulong)(m_GPUPersistentAllocator.MaxBlockCount * MaxBytesPerCBuffer);
#else
            var persistentBytes = m_GPUPersistentAllocator.OnePastHighestUsedAddress;
#endif
            if (persistentBytes > (ulong)m_PersistentInstanceDataSize)
            {
                if(m_UseTextureScene)
                {
                    DecomposePowerOfTwo((int)m_TextureScenePageSize / 16, out int height, out int width);
                    Texture2D textureScene = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
                    m_TextureScenePageSize = width * height * 16;
                    m_PersistentInstanceDataSize += m_TextureScenePageSize;

                    var newSystemBuffer = new NativeArray<float4>(
                   (int)m_PersistentInstanceDataSize / 16,
                   Allocator.Persistent,
                   NativeArrayOptions.ClearMemory);

                    if (m_SystemMemoryBuffer.IsCreated)
                    {
                        NativeArray<float4>.Copy(m_SystemMemoryBuffer, newSystemBuffer, m_SystemMemoryBuffer.Length);
                        m_SystemMemoryBuffer.Dispose();
                    }

                    m_TextureSceneList.Add(textureScene);

                    m_SystemMemoryBuffer = newSystemBuffer;

                    m_DirectUploader.Dispose();
                    m_DirectUploader = new DirectUploader(null, m_SystemMemoryBuffer, m_PersistentInstanceDataSize, GetNativeTexturePtrs(m_TextureSceneList), m_TextureScenePageWidth, m_TextureScenePageHeight);
                }
                else
                {
                    while ((ulong)m_PersistentInstanceDataSize < persistentBytes)
                    {
                        m_PersistentInstanceDataSize *= 2;
                    }

                    if (m_PersistentInstanceDataSize > kGPUBufferSizeMax)
                    {
                        m_PersistentInstanceDataSize = kGPUBufferSizeMax;
                    }

                    if (persistentBytes > kGPUBufferSizeMax)
                        Debug.LogError("Entities Graphics: Current loaded scenes need more than 1GiB of persistent GPU memory.");

                    GraphicsBuffer newBuffer;
                    if (BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer)
                    {
                        newBuffer = new GraphicsBuffer(
                            GraphicsBuffer.Target.Constant,
                            (int)m_PersistentInstanceDataSize / 16,
                            16);
                    }
                    else
                    {
                        newBuffer = new GraphicsBuffer(
                            GraphicsBuffer.Target.Raw,
                            (int)m_PersistentInstanceDataSize / 4,
                            4);
                    }
                    newBuffer.SetData(m_SystemMemoryBuffer, 0, 0, m_SystemMemoryBuffer.Length);

                    var newSystemBuffer = new NativeArray<float4>(
                        (int)m_PersistentInstanceDataSize / 16,
                        Allocator.Persistent,
                        NativeArrayOptions.ClearMemory);

                    if (m_SystemMemoryBuffer.IsCreated)
                    {
                        NativeArray<float4>.Copy(m_SystemMemoryBuffer, newSystemBuffer, m_SystemMemoryBuffer.Length);
                        m_SystemMemoryBuffer.Dispose();
                    }

                    m_SystemMemoryBuffer = newSystemBuffer;

                    m_DirectUploader.Dispose();
                    m_DirectUploader = new DirectUploader(newBuffer, m_SystemMemoryBuffer, m_PersistentInstanceDataSize, new NativeArray<IntPtr>());


                    m_GPUPersistentInstanceBufferHandle = newBuffer.bufferHandle;
                    UpdateBatchBufferHandles();

                    if (m_GPUPersistentInstanceData != null)
                        m_GPUPersistentInstanceData.Dispose();
                    m_GPUPersistentInstanceData = newBuffer;
                }
              
            }

        }

        private void UpdateBatchBufferHandles()
        {
            foreach (var b in m_ExistingBatchIndices)
            {
                m_BatchRendererGroup.SetBatchBuffer(new BatchID { value = (uint)b }, m_GPUPersistentInstanceBufferHandle);
            }
        }

#if DEBUG_LOG_MEMORY_USAGE
        private static ulong PrevUsedSpace = 0;
#endif

        private void EndUpdate()
        {

#if DEBUG_LOG_MEMORY_USAGE
    if (m_GPUPersistentAllocator.UsedSpace != PrevUsedSpace)
    {
        Debug.Log($"GPU memory: {m_GPUPersistentAllocator.UsedSpace / 1024.0 / 1024.0:F4} / {m_GPUPersistentAllocator.Size / 1024.0 / 1024.0:F4}");
        PrevUsedSpace = m_GPUPersistentAllocator.UsedSpace;
    }
#endif

        }

        internal static NativeList<T> NewNativeListResized<T>(int length, Allocator allocator, NativeArrayOptions resizeOptions = NativeArrayOptions.ClearMemory) where T : unmanaged
        {
            var list = new NativeList<T>(length, allocator);
            list.Resize(length, resizeOptions);

            return list;
        }

        public BatchMaterialID RegisterMaterial(Material material) => m_BatchRendererGroup.RegisterMaterial(material);

        public BatchMeshID RegisterMesh(Mesh mesh) => m_BatchRendererGroup.RegisterMesh(mesh);

        public void UnregisterMaterial(BatchMaterialID material) => m_BatchRendererGroup.UnregisterMaterial(material);

        public void UnregisterMesh(BatchMeshID mesh) => m_BatchRendererGroup.UnregisterMesh(mesh);

        public Mesh GetMesh(BatchMeshID mesh) => m_BatchRendererGroup.GetRegisteredMesh(mesh);

        public Material GetMaterial(BatchMaterialID material) => m_BatchRendererGroup.GetRegisteredMaterial(material);

        internal static string TypeIndexToName(int typeIndex)
        {
#if DEBUG_PROPERTY_NAMES
            if (s_TypeIndexToName.TryGetValue(typeIndex, out var name))
                return name;
            else
                return "<unknown type>";
#else
            return null;
#endif
        }

        internal static string NameIDToName(int nameID)
        {
#if DEBUG_PROPERTY_NAMES
            if (s_NameIDToName.TryGetValue(nameID, out var name))
                return name;
            else
                return "<unknown property>";
#else
            return null;
#endif
        }

        internal static string TypeIndexFormatted(int typeIndex)
        {
            return $"{TypeIndexToName(typeIndex)} ({typeIndex:x8})";
        }

        internal static string NameIDFormatted(int nameID)
        {
            return $"{NameIDToName(nameID)} ({nameID:x8})";
        }
    }
}

#endif
