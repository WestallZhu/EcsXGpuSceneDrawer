using UnityEngine;
using Unity.Mathematics;
using System.Collections.Generic;
using System;
using UnityEngine.Rendering;
using Unity.Collections.LowLevel.Unsafe;
using GPUAnimationBaker.Engine;
using Unity.Collections;
using System.Runtime.CompilerServices;
using Unity.Entities.Graphics;

#if !UNITY_2022_2_OR_NEWER
using EntitiesGraphicsChunkInfo = Unity.Entities.HybridChunkInfo;
#else
using Unity.Rendering;
#endif

namespace Unity.Entities
{

    public static class EnityConversionUtility
    {

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static double EncodeEntityId(Entity entity)
        {
            return UnsafeUtility.As<Entity, double>(ref entity);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Entity DecodeEntityId(double entityID)
        {
            return UnsafeUtility.As<double, Entity>(ref entityID);
        }

    }

    public static class GameObjectConversionUtility
    {

        public const int EmptyRenderMask = 0;

        public const int RootMask = 1;

        public const int ChildMask = 2;

        public const int SpriteRenderRootMask = 3;

        public const int SpriteRenderChildMask = 4;

        public const int MeshRenderRootMask = 5;

        public const int MeshRenderChildMask = 6;

        public const int SkinMeshRootMask = 7;

        public const int SkinMeshChildMask = 8;

        public static EntityManager EntityManager;

        public static World World;

        private static EntityArchetype[] m_Archetypes = new EntityArchetype[9];

        public static void Create()
        {
            World = World.DefaultGameObjectInjectionWorld;
            EntityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            InitArchetypes();
        }

        private static void InitArchetypes()
        {
            m_Archetypes[EmptyRenderMask] = EntityManager.CreateArchetype();

            m_Archetypes[RootMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(AssetUnique),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(LinkedEntityGroup),
                typeof(TransformAccessEntity)
                );
            m_Archetypes[ChildMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(Parent)
                );

            m_Archetypes[SpriteRenderRootMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(SpriteColor),
                typeof(SpriteFlipX),
                typeof(SpriteFlipY),
                typeof(SpriteEntityDrawMode),
                typeof(SpriteRectParam),
                typeof(SpriteTileSize),
                typeof(SpriteAnimationSpeed),
                typeof(SpritePivotScale),
                typeof(SpriteFadeStartTime),
                typeof(SpriteBillBord),
                typeof(SpriteFitDynamicScaleX),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
#else
                typeof(RenderMesh),
#endif
                typeof(RenderBounds),
                typeof(WorldRenderBounds),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>()
            );

            m_Archetypes[SpriteRenderChildMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(Parent),
                typeof(SpriteColor),
                typeof(SpriteFlipX),
                typeof(SpriteFlipY),
                typeof(SpriteEntityDrawMode),
                typeof(SpriteRectParam),
                typeof(SpriteTileSize),
                typeof(SpriteAnimationSpeed),
                typeof(SpritePivotScale),
                typeof(SpriteFadeStartTime),
                typeof(SpriteBillBord),
                typeof(SpriteFitDynamicScaleX),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
#else
                typeof(RenderMesh),
#endif
                typeof(RenderBounds),
                typeof(WorldRenderBounds),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>()
            );

            m_Archetypes[MeshRenderRootMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(RenderBounds),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
#else
                typeof(RenderMesh),
#endif
                typeof(WorldRenderBounds),
                typeof(MaterialFloat4),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>());

            m_Archetypes[MeshRenderChildMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(Parent),
                typeof(RenderBounds),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
#else
                typeof(RenderMesh),
#endif
                typeof(WorldRenderBounds),
                typeof(MaterialFloat4),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>());

            m_Archetypes[SkinMeshRootMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(IndexAndCount),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(RenderBounds),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
                typeof(PlanarShadow),
#else
                typeof(RenderMesh),
#endif
                typeof(MaterialColor32),
                typeof(GpuAnimatorState),
                typeof(GpuAnimatorSpeedFactor),
                typeof(WorldRenderBounds),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>());

            m_Archetypes[SkinMeshChildMask] = EntityManager.CreateArchetype(
                typeof(Prefab),
                typeof(IndexAndCount),
                typeof(LocalToWorld),
                typeof(LocalTransform),
                typeof(RenderBounds),
#if UNITY_2022_2_OR_NEWER
                typeof(RenderFilterSettings),
                typeof(MaterialMeshInfo),
                typeof(RenderMeshArray),
                 typeof(PlanarShadow),
#else
                typeof(RenderMesh),
#endif
                typeof(MaterialColor32),
                typeof(Parent),
                typeof(GpuAnimatorState),
                typeof(GpuAnimatorSpeedFactor),
                typeof(WorldRenderBounds),
                ComponentType.ChunkComponent<ChunkWorldRenderBounds>(),
                ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>());
        }

        public static EntityArchetype GetEntityArchetype(int type)
        {
            return m_Archetypes[type];
        }

#if UNITY_2022_2_OR_NEWER

        public static RenderMeshArray ConvertMeshRenderersToRenderMeshArray(Renderer[] renderers,bool ZTestAlways)
        {
            var meshList = new List<Mesh>();
            var materialList = new List<Material>();

            foreach (var r in renderers)
            {
                Mesh mesh = null;
                Material material = null;
                var spriteRenderer = r as SpriteRenderer;
                if (spriteRenderer)
                {
                    mesh = SpriteRenderData.SpriteQuad;
                    var tex = spriteRenderer.sprite ?.texture;
                    foreach (var mat in materialList)
                    {
                        if (!mat.shader.name.StartsWith("Custom/SpriteEcs"))
                            continue;

                        if (mat.mainTexture == tex)
                        {
                            material = mat;
                            break;
                        }
                    }
                    if (material == null)
                    {
                        material = SpriteRenderData.GetOrCreateMaterial(tex, ZTestAlways);
                        if(material != null)
                            materialList.Add(material);
                    }
                    mesh = SpriteRenderData.SpriteQuad;

                    if (meshList.IndexOf(mesh) == -1)
                    {
                        meshList.Add(mesh);
                    }
                }
                else
                {
                    var mf = r.GetComponent<MeshFilter>();
                    if (mf == null || mf.sharedMesh == null)
                        continue;

                    mesh = mf.sharedMesh;

                    if (meshList.IndexOf(mesh) == -1)
                    {
                        meshList.Add(mesh);
                    }

                    for (int i = 0; i < r.sharedMaterials.Length; i++)
                    {
                        material = r.sharedMaterials[i];
                        if (material == null) continue;

                        if (materialList.IndexOf(material) == -1)
                        {
                            materialList.Add(material);
                        }
                    }
                }
            }

            return new RenderMeshArray(materialList.ToArray(), meshList.ToArray());
        }

        public static MaterialMeshInfo GetMaterialMeshInfo(RenderMeshArray renderMeshArray, int meshInstanceID, int materialInstanceID, ushort subMeshIndex)
        {
            int materialIndex = -1;
            int meshIndex = -1;

            for (int i = 0; i < renderMeshArray.MaterialReferences.Length; i++)
            {
                if (renderMeshArray.MaterialReferences[i].Id.instanceId == materialInstanceID)
                {
                    materialIndex = i;
                    break;
                }
            }

            for (int i = 0; i < renderMeshArray.MeshReferences.Length; i++)
            {
                if (renderMeshArray.MeshReferences[i].Id.instanceId == meshInstanceID)
                {
                    meshIndex = i;
                    break;
                }
            }

            if (materialIndex == -1 || meshIndex == -1)
            {
                Debug.LogError("Material or Mesh not found in RenderMeshArray.");
                return default;
            }

            return MaterialMeshInfo.FromRenderMeshArrayIndices(materialIndex, meshIndex, subMeshIndex);
        }
#endif

        public static (Entity, List<string>, string[], GameObject, List<string>, bool) ConvertGameObjectHierarchyEcb(GameObject prefabGameObject, int layer, int sortingOrder,bool ZTestAlways)
        {
            EntityCommandBuffer ecb = new EntityCommandBuffer(Allocator.TempJob);

            var (parentEntity, entityChildNames, stateNames, go, goChildNames, hasSprite) = GameObjectConversionUtility.ConvertGameObjectHierarchyEcb(prefabGameObject, ecb, layer, sortingOrder, ZTestAlways);

            if(go != null)
                go.SetActive(false);

            ecb.Playback(EntityManager);
            ecb.Dispose();

            return (parentEntity, entityChildNames, stateNames, go, goChildNames, hasSprite);
        }
        public static (Entity,  List<string>, string[], GameObject, List<string>, bool) ConvertGameObjectHierarchyEcb(GameObject prefabGameObject, EntityCommandBuffer ecb, int layer, int sortingOrder, bool ZTestAlways)
        {

            var renderers = prefabGameObject.GetComponentsInChildren<Renderer>(true);
            var rendererCount = renderers.Length;

            List<string> entityChildNames = new List<string>();
            List<string> goChildNames = null;
            string name = prefabGameObject.name;
            bool keepsocket = false;
            bool keepTransformTree = keepsocket;
            string[] stateNames = null;

            var parentWorldToLocal =  prefabGameObject.transform.worldToLocalMatrix;
            bool hasRoot = true;
            if (rendererCount == 1)
            {
                var r = renderers[0];

                var transform = r.transform;
                bool isDefaultPosition = Mathf.Approximately(r.transform.position.sqrMagnitude, 0);
                bool isDefaultRotation = r.transform.rotation == Quaternion.identity;
                bool isDefaultScale = Mathf.Approximately(r.transform.lossyScale.x, 1f)
                                   && Mathf.Approximately(r.transform.lossyScale.y, 1f)
                                   && Mathf.Approximately(r.transform.lossyScale.z, 1f);
                if(transform == prefabGameObject.transform)
                    hasRoot = false;
                else if (r.sharedMaterials.Length == 1
                    && isDefaultPosition
                    && isDefaultScale
                    && isDefaultRotation)
                {
                    hasRoot = false;
                }
            }

            Entity parentEntity = Entity.Null;
            DynamicBuffer<LinkedEntityGroup> children = default;
            if (hasRoot)
            {
                parentEntity = EntityManager.CreateEntity(m_Archetypes[RootMask]);
                children = ecb.AddBuffer<LinkedEntityGroup>(parentEntity);
                children.Add(new LinkedEntityGroup { Value = parentEntity });
                ecb.SetComponent(parentEntity, LocalTransform.FromPosition(new float3(99999, 99999, 99999)));
            }
            GpuAnimatorMonoAsset lastGpuAniAsset = null;

            int filterLayer = layer == 0 ? renderers[0].gameObject.layer : layer;
#if UNITY_2022_2_OR_NEWER
            var renderer = renderers[0];
            var renderMeshArray = ConvertMeshRenderersToRenderMeshArray(renderers, ZTestAlways);
            var filterSettings = new RenderFilterSettings
            {
                Layer = filterLayer,
                sortingOrder = sortingOrder,
                RenderingLayerMask = renderer.renderingLayerMask,
                ShadowCastingMode = renderer.shadowCastingMode,
                ReceiveShadows = renderer.receiveShadows,
                MotionMode = renderer.motionVectorGenerationMode,
                StaticShadowCaster = renderer.staticShadowCaster,
            };
#endif

            GameObject goPart = null;
            bool hasSprite = false;
            foreach (var r in renderers)
            {
                var transfom = r.transform;
                LocalTransform localTransform = hasRoot ? LocalTransform.FromMatrix(parentWorldToLocal * transfom.localToWorldMatrix) : LocalTransform.FromMatrix(transfom.localToWorldMatrix);

                if (r as MeshRenderer)
                {
                    var con = r.transform.parent?.GetComponent<GpuAnimatorBehaviour>();
                    GpuAnimatorMonoAsset gpuAniAsset = con?.gpuAniAsset;
                    if (gpuAniAsset)
                    {
                        if (lastGpuAniAsset != gpuAniAsset)
                        {
                            var animations = gpuAniAsset.animations;
                            stateNames = gpuAniAsset.stateNames;
                            for (int i = 0; i < animations.Length; i++)
                            {
                                if (stateNames[i] == "Attack")
                                {
                                    animations[i].loop = true;
                                }
                                else if (stateNames[i] == "Dead")
                                {
                                    animations[i].nextStateIndex = -1;
                                }

                            }
                            lastGpuAniAsset = gpuAniAsset;
                        }
                        var gpuAnimationDataArray = EntityHybridUtility.gpuAnimationDataArray;
                        var indexAndCount = gpuAnimationDataArray.GetOrCreateElement(gpuAniAsset.GetHashCode(), gpuAniAsset.animations);
                        var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                        if (mesh == null)
                            continue;

                        GpuAnimatorPlayComponent animPlay = new GpuAnimatorPlayComponent()
                        {
                            startPlayTime = Time.timeSinceLevelLoad,
                            startFrameIndex = (UInt16)gpuAnimationDataArray.animDataList[indexAndCount.index + 0].startFrameIndex,
                            nbrOfFramesPerSample = (UInt16)gpuAnimationDataArray.animDataList[indexAndCount.index + 0].nbrOfFramesPerSample,
                            loop = 1,
                            intervalNbrOfFrames = 0
                        };
                        GpuAnimatorState animState = animPlay.GetEncodeState();
                        var mats = r.sharedMaterials;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var mat = mats[i];
                            if (mat == null)
                                continue;
                            mat.enableInstancing = true;
                            Entity entity;
                            if (parentEntity == Entity.Null)
                            {
                                entity = EntityManager.CreateEntity(m_Archetypes[SkinMeshRootMask]);
                                parentEntity = entity;
                            }
                            else
                            {
                                entity = EntityManager.CreateEntity(m_Archetypes[SkinMeshChildMask]);

                                children.Add(entity);
                                entityChildNames.Add(r.gameObject.name);

                                ecb.SetComponent(entity, new Parent { Value = parentEntity });
                            }
                            var subMesh = mesh.subMeshCount > 0 ? i : 0;
#if UNITY_2022_2_OR_NEWER
                            ecb.SetSharedComponentManaged(entity, renderMeshArray);
                            ecb.SetSharedComponent(entity, filterSettings);
                            ecb.SetComponentEnabled(entity, typeof(PlanarShadow), mat.shader.name == "Comic/FakeShadow");
                            string renderType = mat.GetTag("RenderType", false, "Unknown");
                            if (renderType == "Transparent")
                                ecb.AddComponent(entity, ComponentType.ReadWrite<DepthSorted_Tag>());
                            ecb.SetComponent(entity, GetMaterialMeshInfo(renderMeshArray, mesh.GetInstanceID(), mat.GetInstanceID(), (ushort)subMesh));
#else
                            ecb.SetSharedComponent(entity, new RenderMesh { mesh = mesh, material = mat, layer = filterLayer, subMesh = subMesh });
#endif
                            ecb.SetComponent(entity, indexAndCount);
                            ecb.SetComponent(entity, animState);
                            ecb.SetComponent(entity, new GpuAnimatorSpeedFactor { value = 1 });
                            ecb.SetComponent(entity, localTransform);
                            ecb.SetComponent(entity, new RenderBounds() { Value = mesh.bounds });
                        }

                    }
                    else
                    {
                        if(r.GetComponent<TextMesh>())
                        {
                            if(keepTransformTree)
                                continue;
                            if (goPart == null)
                                goPart = new GameObject(prefabGameObject.name);
                            if(goChildNames == null)
                                goChildNames = new List<string>();
                            goChildNames.Add(r.gameObject.name);
                            GameObject.Instantiate(r.gameObject, goPart.transform, false);

                            continue;
                        }
                        var mats = r.sharedMaterials;
                        for (int i = 0; i < mats.Length; i++)
                        {
                            var mat = mats[i];
                            if (mat == null)
                                continue;

                            mat.enableInstancing = true;
                            Entity entity;
                            if (parentEntity == Entity.Null)
                            {
                                entity = EntityManager.CreateEntity(m_Archetypes[MeshRenderRootMask]);
                                parentEntity = entity;
                            }
                            else
                            {
                                entity = EntityManager.CreateEntity(m_Archetypes[MeshRenderChildMask]);

                                children.Add(entity);
                                entityChildNames.Add(r.gameObject.name);

                                ecb.SetComponent(entity, new Parent { Value = parentEntity });
                            }
                            var mesh = r.GetComponent<MeshFilter>().sharedMesh;
                            if (mesh == null)
                                continue;
                            var transform = r.transform;

                            var subMesh = mesh.subMeshCount > 0 ? i : 0;
#if UNITY_2022_2_OR_NEWER
                            ecb.SetSharedComponentManaged(entity, renderMeshArray);
                            ecb.SetSharedComponent(entity, filterSettings);
                            string renderType = mat.GetTag("RenderType", false, "Unknown");
                            if (renderType == "Transparent")
                                ecb.AddComponent(entity, ComponentType.ReadWrite<DepthSorted_Tag>());
                            ecb.SetComponent(entity, GetMaterialMeshInfo(renderMeshArray, mesh.GetInstanceID(), mat.GetInstanceID(), (ushort)subMesh));
#else
                              ecb.SetSharedComponent(entity, new RenderMesh { mesh = mesh, material = mat, layer = filterLayer, subMesh = subMesh });
#endif
                            ecb.SetComponent(entity, localTransform);
                            ecb.SetComponent(entity, new RenderBounds() { Value = mesh.bounds });
                        }
                    }
                }
                else if (r as SpriteRenderer)
                {
                    hasSprite = true;
                    Entity entity;
                    if (parentEntity == Entity.Null)
                    {
                        entity = EntityManager.CreateEntity(m_Archetypes[SpriteRenderRootMask]);
                        parentEntity = entity;
                    }
                    else
                    {
                        entity = EntityManager.CreateEntity(m_Archetypes[SpriteRenderChildMask]);

                        children.Add(entity);
                        entityChildNames.Add(r.gameObject.name);

                        ecb.SetComponent(entity, new Parent { Value = parentEntity });
                    }
#if UNITY_2022_2_OR_NEWER
                    UpdateSpriteEntityEcb(entity, r as SpriteRenderer, parentWorldToLocal, renderMeshArray, filterSettings, ecb, ZTestAlways);
#else
                    UpdateSpriteEntityEcb(entity, r as SpriteRenderer, parentWorldToLocal, ecb, filterLayer);
#endif
                }
            }

            if(keepTransformTree)
            {
                goPart = GameObject.Instantiate(prefabGameObject);
                var newRenderers = goPart.GetComponentsInChildren<Renderer>(true);
                foreach( var r in newRenderers)
                {
                    if (r.GetComponent<TextMesh>() != null)
                        continue;
                    if (keepsocket)
                    {
                        if (r as SpriteRenderer)
                        {
                            Component.DestroyImmediate(r);
                        }
                        else
                        {
                            GameObject.DestroyImmediate(r.transform.parent);
                        }
                    }
                    else
                    {
                        var mono = r.GetComponent<MonoBehaviour>();
                        if(mono != null)
                            Component.DestroyImmediate(mono);
                        Component.DestroyImmediate(r);
                    }

                }
            }
            else
            {
                var collider = prefabGameObject.GetComponentInChildren<Collider>();
                if (collider != null)
                {
                    if (goPart == null)
                        goPart = new GameObject(prefabGameObject.name);

                    if (goChildNames == null)
                        goChildNames = new List<string>();
                    goChildNames.Add(collider.gameObject.name);
                    var go = GameObject.Instantiate(collider.gameObject, goPart.transform, false);
                    var r = go.GetComponent<Renderer>();
                    if (r != null)
                        Component.DestroyImmediate(r);
                }
            }

            return (parentEntity, entityChildNames, stateNames, goPart, goChildNames, hasSprite);
        }

#if UNITY_2022_2_OR_NEWER
        private static void UpdateSpriteEntityEcb(Entity entity,  SpriteRenderer renderer, in Matrix4x4 parentLocalToWorld , in RenderMeshArray renderMeshArray, in RenderFilterSettings filterSettings, in EntityCommandBuffer ecb,bool ZTestAlways)
#else
        private static void UpdateSpriteEntityEcb(Entity entity,  SpriteRenderer renderer, in Matrix4x4 parentLocalToWorld , in EntityCommandBuffer ecb, int layer)
#endif
        {
            var sprite = renderer.sprite;
            var color = renderer.color;
            bool flipX = renderer.flipX;
            bool flipY = renderer.flipY;

            int drawMode = renderer.drawMode == SpriteDrawMode.Tiled ? 1 : 0;
            int maskFlag = 0;
            if (renderer.maskInteraction == SpriteMaskInteraction.VisibleInsideMask)
            {
                maskFlag = 1;
            }
            int drawModeBits = (maskFlag << 1) | (drawMode & 0x1);
            ecb.SetComponent(entity, new SpriteEntityDrawMode { value = (float)drawModeBits });
            float tiledWidth = renderer.size.x;
            float animationSpeed = 1.0f;
            var gameObject = renderer.gameObject;
            var localScale = gameObject.transform.localScale;
            float maxScale = math.max(localScale.x, math.max(localScale.y, localScale.z));
            float2 scale = new float2(sprite.textureRect.width / sprite.pixelsPerUnit * localScale.x / maxScale, sprite.textureRect.height / sprite.pixelsPerUnit * localScale.y / maxScale);
            float2 pivot = sprite.pivot / sprite.rect.size;

            ecb.SetComponent(entity, new SpriteColor { value = EncodeColorToFloat(color) });
            ecb.SetComponent(entity, new SpriteFlipX { value = flipX ? 1f : 0f });
            ecb.SetComponent(entity, new SpriteFlipY { value = flipY ? 1f : 0f });
            float texWidth = sprite.texture.width;
            float texHeight = sprite.texture.height;
            ecb.SetComponent(entity, new SpriteRectParam
            {
                value = new float4(
                    sprite.textureRect.x / texWidth,
                    sprite.textureRect.y / texHeight,
                    sprite.textureRect.width / texWidth,
                    sprite.textureRect.height / texHeight
                )
            });
            ecb.SetComponent(entity, new SpriteTileSize { value = tiledWidth });
            ecb.SetComponent(entity, new SpriteAnimationSpeed { value = animationSpeed });
            ecb.SetComponent(entity, new SpritePivotScale { value = new float4(pivot.x, pivot.y, scale.x, scale.y) });
            ecb.SetComponent(entity, LocalTransform.FromMatrix(parentLocalToWorld * gameObject.transform.localToWorldMatrix));
            ecb.SetComponent(entity, new SpriteFadeStartTime { time = 0f });
            ecb.SetComponent(entity, new SpriteFitDynamicScaleX { value = 1.0f });

#if UNITY_2022_2_OR_NEWER
            ecb.SetSharedComponentManaged(entity, renderMeshArray);
            ecb.SetSharedComponent(entity, filterSettings);

            ecb.SetComponent(entity, GetMaterialMeshInfo(renderMeshArray, SpriteRenderData.SpriteQuad.GetInstanceID(), SpriteRenderData.GetMaterialInstanceID(sprite.texture, ZTestAlways), 0));
#else
            ecb.SetSharedComponent(entity, new RenderMesh
            {
                mesh = SpriteRenderData.SpriteQuad,
                material = SpriteRenderData.GetOrCreateMaterial(sprite.texture,false),
                layer = layer,
                castShadows = ShadowCastingMode.Off,
            });
#endif

            ecb.SetComponent(entity, new RenderBounds { Value = SpriteRenderData.fixedSpriteAABB });
        }

        private static float EncodeColorToFloat(Color data)
        {
            uint encodedInt =
                ((uint)(data.r * 255.0f) << 24) |
                ((uint)(data.g * 255.0f) << 16) |
                ((uint)(data.b * 255.0f) << 8) |
                ((uint)(data.a * 255.0f));
            return UnsafeUtility.As<uint, float>(ref encodedInt);
        }
    }
}
