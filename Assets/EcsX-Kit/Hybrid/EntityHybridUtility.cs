using GPUAnimationBaker.Engine;
using System;
using System.Collections.Generic;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities.Graphics;
using Unity.Mathematics;
using Unity.Rendering;
using UnityEngine;
#if USE_LUA
using XLua;
#endif

namespace Unity.Entities
{

    struct AssetUnique : IComponentData
    {
        public int Value;
    }

    public class EntityGameObject : IComponentData
    {
        public GameObject go;
        public Animator []animators;
        public Renderer []renderers;
        public MaterialPropertyBlock propertyBlock;
        public EntityGameObject Clone(int layer)
        {
            var eGO = new EntityGameObject();
            eGO.go = GameObject.Instantiate(go);
            eGO.go.SetActive(true);
            if (animators != null)
            {
                eGO.animators = eGO.go.GetComponentsInChildren<Animator>(true);
            }
            if(renderers != null)
            {
                eGO.renderers = eGO.go.GetComponentsInChildren<Renderer>(true);
                for(int i = 0;i<renderers.Length;i++)
                {
                    eGO.renderers[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    if(layer != 0 )
                    {
                        eGO.renderers[i].gameObject.layer = layer;
                    }
                }
                eGO.propertyBlock = new MaterialPropertyBlock();
            }
            return eGO;
        }
    }

    public class EntiyRefCount
    {
        public Entity entityPrefab;
        public int count;

        public List<string> entityChildNames;
        public List<string> goChildNames;
        public string[] stateNames;
        public EntityGameObject goPart;
        public int layer;
        public int sortOrder;
        public Entity[] entityPrefabVariants;
        public bool hasSprite;
        public bool pureGO;
    }

    public static class EntityHybridUtility
    {
        public static Dictionary<int, EntiyRefCount> EntitiesPrefab;

        public static EntityManager EntityManager;

        public static SyncTransformSystem SyncTransformSystem;

        public static ECBInitSystem ECBInitSystem;


        public static GameObject HybridPrefab;
        public static EntityArchetype PureGameObjectArchetype;

#if USE_LUA
        static LuaEnv LuaEnv = null;
#endif

        static string[] AnimStatesMap = null;

        public static GpuAnimationDataArray gpuAnimationDataArray;
        public static void Create()
        {
            GameObjectConversionUtility.Create();
            EntitiesPrefab = new Dictionary<int, EntiyRefCount>(256);
            EntityManager = World.DefaultGameObjectInjectionWorld.EntityManager;

            SyncTransformSystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<SyncTransformSystem>();
            ECBInitSystem = World.DefaultGameObjectInjectionWorld.GetExistingSystemManaged<ECBInitSystem>();

            PureGameObjectArchetype = EntityManager.CreateArchetype(typeof(Prefab), typeof(AssetUnique), typeof(EntityGameObject));

            gpuAnimationDataArray = new GpuAnimationDataArray();
        }
        #if USE_LUA
        public static void SetLuaEnv(LuaEnv luaEnv)
        {
            LuaEnv = luaEnv;
        }
        #endif

        static bool isDispose = false;
        public static void Dispose()
        {
            SpriteRenderData.Release();
            isDispose = true;
        }
        public static Entity Instantiate(Entity entity)
        {
            var newEntity = EntityManager.Instantiate(entity);

            if (EntityManager.HasComponent< LinkedEntityGroup >(newEntity) )
            {
                DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(newEntity);
                var parent = new Parent { Value = newEntity };
                for (int i=1; i< children.Length; i++)
                {
                    EntityManager.SetComponentData(children[i].Value, parent);
                }
            }

            return newEntity;
        }

        public static void Instantiate(Entity entity, NativeArray<Entity> outputEntities)
        {
            EntityManager.Instantiate(entity, outputEntities);

            for(int j=0; j<outputEntities.Length; j++)
            {
                var newEntity = outputEntities[j];
                if (EntityManager.HasComponent<LinkedEntityGroup>(newEntity))
                {
                    DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(newEntity);
                    var parent = new Parent { Value = newEntity };
                    for (int i = 1; i < children.Length; i++)
                    {
                        EntityManager.SetComponentData(children[i].Value, parent);
                    }
                }
            }
        }

        public static Entity Instantiate(GameObject prefabGameObject, int assetUnique, bool attachToGO)
        {
            if(assetUnique == 0)
            {
                assetUnique = prefabGameObject.GetInstanceID();
            }
            EntiyRefCount entiyRefCount = GetEntityPrefab(assetUnique, prefabGameObject, 0);
            var entityPrefab = entiyRefCount.entityPrefab;
            var entity =  Instantiate(entityPrefab);

            EntityGameObject goPart = null;
            if (EntityManager.HasComponent<EntityGameObject>(entityPrefab))
            {
                goPart = entiyRefCount.goPart.Clone(0);
                EntityManager.SetComponentData<EntityGameObject>(entity, goPart);
            }

            if (attachToGO || (goPart != null && EntityManager.HasComponent<TransformAccessEntity>(entity)) )
            {
                var transEntity = SyncTransformSystem.AddSyncTransform(entity, prefabGameObject.transform);

                EntityManager.SetComponentData(entity, transEntity);
            }
            return entity;
        }

        public static RenderFilterSettings GetRenderFilterSettings(Entity entity)
        {
            if (EntityManager.HasComponent<RenderFilterSettings>(entity))
            {
                return EntityManager.GetSharedComponentManaged<RenderFilterSettings>(entity);
            }
            else
            {
                DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                return EntityManager.GetSharedComponentManaged<RenderFilterSettings>(children[1].Value);
            }
        }
        public static Entity InstantiateVariant(Entity sourcePrefab, int layer, int sortingOrder)
        {
            Entity newEntity = EntityManager.Instantiate(sourcePrefab);

            RenderFilterSettings filterSettings = GetRenderFilterSettings(sourcePrefab);
            if(layer != 0)
                filterSettings.Layer = layer;
            filterSettings.sortingOrder = sortingOrder;

            if (EntityManager.HasComponent<RenderFilterSettings>(newEntity))
            {
                EntityManager.SetSharedComponentManaged(newEntity, filterSettings);
            }

            else if (EntityManager.HasComponent<LinkedEntityGroup>(newEntity))
            {
                var children = EntityManager.GetBuffer<LinkedEntityGroup>(newEntity).ToNativeArray(Allocator.Temp);
                for (int i = 1; i < children.Length; i++)
                {
                    var child = children[i].Value;
                    EntityManager.AddComponent<Prefab>(child);
                    EntityManager.SetSharedComponentManaged(child, filterSettings);
                }
                children.Dispose();
            }

            EntityManager.AddComponent<Prefab>(newEntity);
            return newEntity;
        }

        public static Entity GetPrefabVariant(EntiyRefCount refCount, int layer, int sortingOrder)
        {

            if (refCount.entityPrefabVariants != null)
            {
                foreach (Entity variant in refCount.entityPrefabVariants)
                {
                    RenderFilterSettings settings = GetRenderFilterSettings(variant);
                    if ( (layer == 0 || settings.Layer == layer) && settings.sortingOrder == sortingOrder)
                    {
                        return variant;
                    }
                }
            }

            Entity newVariant = InstantiateVariant(refCount.entityPrefab, layer, sortingOrder);

            if (refCount.entityPrefabVariants == null)
            {
                refCount.entityPrefabVariants = new Entity[] { newVariant };
            }
            else
            {
                Entity[] newVariants = new Entity[refCount.entityPrefabVariants.Length + 1];
                Array.Copy(refCount.entityPrefabVariants, newVariants, refCount.entityPrefabVariants.Length);
                newVariants[newVariants.Length-1] = newVariant;
                refCount.entityPrefabVariants = newVariants;
            }

            return newVariant;
        }
        public static Entity Instantiate(GameObject prefabGameObject, int layer=0, int sortingOrder=0,bool ZTestAlways = false)
        {
            int assetUnique = prefabGameObject.GetInstanceID();
            EntiyRefCount entiyRefCount = GetEntityPrefab(assetUnique, prefabGameObject, layer, sortingOrder, ZTestAlways);
            Entity entityPrefab = Entity.Null;
            if( (layer == entiyRefCount.layer && sortingOrder == entiyRefCount.sortOrder) || entiyRefCount.pureGO)
                entityPrefab  = entiyRefCount.entityPrefab;
            else
            {
                entityPrefab = GetPrefabVariant(entiyRefCount, layer, sortingOrder);
            }

            var entity = Instantiate(entityPrefab);

            EntityGameObject goPart = null;
            if (EntityManager.HasComponent<EntityGameObject>(entityPrefab))
            {
                goPart = entiyRefCount.goPart.Clone(layer);
                EntityManager.SetComponentData<EntityGameObject>(entity, goPart);
            }

            if ((goPart != null && EntityManager.HasComponent<TransformAccessEntity>(entity)))
            {
                var transEntity = SyncTransformSystem.AddSyncTransform(entity, goPart.go.transform);

                EntityManager.SetComponentData(entity, transEntity);
            }

            return entity;
        }

        public static void Instantiate(GameObject prefabGameObject, NativeArray<Entity> outputEntities, int layer=0, int sortingOrder=0,bool ZTestAlways = false)
        {
            int assetUnique = prefabGameObject.GetInstanceID();
            EntiyRefCount entiyRefCount = GetEntityPrefab(assetUnique, prefabGameObject, layer, sortingOrder, ZTestAlways);
            entiyRefCount.count += outputEntities.Length - 1;
            var entityPrefab = entiyRefCount.entityPrefab;
            Instantiate(entityPrefab, outputEntities);

            for(int i=0; i< outputEntities.Length; i++)
            {
                var entity = outputEntities[i];
                EntityGameObject goPart = null;
                if (EntityManager.HasComponent<EntityGameObject>(entityPrefab))
                {
                    goPart = entiyRefCount.goPart.Clone(layer);
                    EntityManager.SetComponentData<EntityGameObject>(entity, goPart);
                }

                if ((goPart != null && EntityManager.HasComponent<TransformAccessEntity>(entity)))
                {
                    var transEntity = SyncTransformSystem.AddSyncTransform(entity, goPart.go.transform);

                    EntityManager.SetComponentData(entity, transEntity);
                }
            }
        }

        #if USE_LUA
        public static int Instantiate(GameObject prefabGameObject, int count, int layer, int sortingOrder,bool ZTestAlways, LuaTable outputEntitiesLua)
        {
            NativeArray<Entity> outputEntities = new NativeArray<Entity>(count, Allocator.Temp);
            Instantiate(prefabGameObject, outputEntities, layer, sortingOrder, ZTestAlways);

            for (int i = 0; i < outputEntities.Length; i++)
            {
                outputEntitiesLua.Set<int, Entity>( i + 1, outputEntities[i]);
            }

            outputEntities.Dispose();

            return outputEntities.Length;
        }
        #endif

        #if USE_LUA
        public static LuaTable GetEntityChildNameIndex(in Entity entity)
        {
            var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;
            LuaTable childNameLuaTable = LuaEnv.NewTable();
            if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
            {
                var childNameList = entiyRefCount.entityChildNames;
                if (childNameList != null)
                {
                    for (int i = 0; i < childNameList.Count; i++)
                    {
                        if(childNameLuaTable.ContainsKey(childNameList[i]))
                            childNameLuaTable.Set<string, int>($"{childNameList[i]}{i}", i + 1);
                        else
                            childNameLuaTable.Set<string, int>(childNameList[i], i + 1);
                    }
                }
            }
            return childNameLuaTable;
        }
        #endif

        #if USE_LUA
        public static LuaTable GetGOChildNameIndex(in Entity entity)
        {
            var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;
            LuaTable childNameLuaTable = LuaEnv.NewTable();
            if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
            {
                var goChildNames = entiyRefCount.goChildNames;
                if (goChildNames != null)
                {
                    for (int i = 0; i < goChildNames.Count; i++)
                    {
                        childNameLuaTable.Set<string, int>(goChildNames[i], i);
                    }
                }
            }
            return childNameLuaTable;
        }
        #endif

        public static Dictionary<string, int> GetEntityChildNameIndexDict(in Entity entity)
        {
            var result = new Dictionary<string, int>();
            var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;

            if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
            {
                var childNameList = entiyRefCount.entityChildNames;
                if (childNameList != null)
                {
                    for (int i = 0; i < childNameList.Count; i++)
                    {
                        result[childNameList[i]] = i + 1;
                    }
                }
            }
            return result;
        }

        #if USE_LUA
        public static LuaTable GetEntityAnimationStates(in Entity entity)
        {
            var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;
            LuaTable stateNameLuaTable = LuaEnv.NewTable();
            if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
            {
                var stateNames = entiyRefCount.stateNames;
                if(stateNames != null)
                {
                    for (int i = 0; i < stateNames.Length; i++)
                    {
                        stateNameLuaTable.Set<string,int>(stateNames[i],i);
                    }
                }
            }
            return stateNameLuaTable;
        }
        #endif

        public static void AttachToTransform(in Entity entity, Transform transform)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var go = EntityManager.GetComponentObject<EntityGameObject>(entity).go;
                if (go != null)
                {
                    go.transform.parent = transform;
                    go.transform.localPosition = Vector3.zero;
                    go.transform.localRotation = Quaternion.identity;
                    go.transform.localScale = Vector3.one;
                }
            }

            if (EntityManager.HasComponent<TransformAccessEntity>(entity))
            {
                var transEntity = EntityManager.GetComponentData<TransformAccessEntity>(entity);
                if (transEntity != TransformAccessEntity.Null)
                    SyncTransformSystem.RemoveSyncTransform(transEntity);
                transEntity = SyncTransformSystem.AddSyncTransform(entity, transform);

                EntityManager.SetComponentData(entity, transEntity);
            }
        }

        public static void DettachFormTransform(in Entity entity)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var go = EntityManager.GetComponentObject<EntityGameObject>(entity).go;
                if(go != null)
                {
                    go.transform.parent = null;
                }
            }

            var transEntity = EntityManager.GetComponentData<TransformAccessEntity>(entity);
            if (transEntity != TransformAccessEntity.Null)
            {
                SyncTransformSystem.RemoveSyncTransform(transEntity);
                EntityManager.SetComponentData(entity, TransformAccessEntity.Null);

            }
        }

        public static EntiyRefCount GetEntityPrefab(int assetUnique, GameObject prefabGameObject, int layer=0, int sortOrder = 0,bool ZTestAlways = false)
        {
            if (!EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
            {
                Animator animator = prefabGameObject.GetComponentInChildren<Animator>();
                if (animator != null && animator.runtimeAnimatorController != null && animator.avatar != null)
                {
                    var entityPrefab = EntityManager.CreateEntity(PureGameObjectArchetype);

                    EntityManager.SetComponentData(entityPrefab, new AssetUnique { Value = assetUnique });
                    entiyRefCount = new EntiyRefCount();
                    entiyRefCount.entityPrefab = entityPrefab;
                    entiyRefCount.count = 1;
                    entiyRefCount.pureGO = true;

                    entiyRefCount.stateNames = AnimStatesMap;

                    entiyRefCount.goPart = new EntityGameObject()
                    {
                        go = prefabGameObject,
                        animators = prefabGameObject.GetComponentsInChildren<Animator>(true),
                        renderers = prefabGameObject.GetComponentsInChildren<Renderer>(true),
                    };
                    EntitiesPrefab.Add(assetUnique, entiyRefCount);
                }
                else
                {
                    List<string> entityChildNames;
                    string[] stateNames;
                    Entity entity;
                    GameObject goPart;
                    List<string> goChildNames;
                    bool hasSprite = false;
                    (entity, entityChildNames, stateNames, goPart, goChildNames, hasSprite) = GameObjectConversionUtility.ConvertGameObjectHierarchyEcb(prefabGameObject, layer, sortOrder, ZTestAlways);

                    EntityManager.AddComponentData(entity, new AssetUnique { Value = assetUnique });

                    entiyRefCount = new EntiyRefCount();
                    entiyRefCount.entityPrefab = entity;
                    entiyRefCount.count = 1;
                    entiyRefCount.goPart = new EntityGameObject() { go = goPart};
                    entiyRefCount.stateNames = stateNames;
                    entiyRefCount.entityChildNames = entityChildNames;
                    entiyRefCount.goChildNames = goChildNames;
                    entiyRefCount.hasSprite = hasSprite;
                    entiyRefCount.layer = layer;
                    entiyRefCount.sortOrder = sortOrder;
                    EntitiesPrefab.Add(assetUnique, entiyRefCount);

                    if (goPart)
                    {
                        if (HybridPrefab == null)
                        {
                            HybridPrefab = new GameObject("HybridPrefab");
                            GameObject.DontDestroyOnLoad(HybridPrefab);
                        }
                        goPart.transform.parent = HybridPrefab.transform;
                        EntityManager.AddComponent(entity, typeof(EntityGameObject));
                    }
                }
            }
            else
            {
                entiyRefCount.count++;
                EntitiesPrefab[assetUnique] = entiyRefCount;
            }
            return entiyRefCount;
        }

        public static int DestroyEntity(Entity entity)
        {
            if(isDispose)
                return 0;

            int safeRemoveGOID= 0;
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                GameObject.Destroy(eGameObject.go);
            }

            var ecb = ECBInitSystem.GetEcb();

            if(EntityManager.HasComponent<AssetUnique>(entity))
            {
                var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;
                if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
                {
                    entiyRefCount.count--;
                    EntitiesPrefab[assetUnique] = entiyRefCount;
                    if (entiyRefCount.count == 0)
                    {
#if UNITY_2022_2_OR_NEWER
                        if (entiyRefCount.hasSprite)
                        {
                            RenderMeshArray renderMeshArray;
                            if(EntityManager.HasComponent<LinkedEntityGroup>(entity))
                            {
                                DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);
                                renderMeshArray = EntityManager.GetSharedComponentManaged<RenderMeshArray>(children[1].Value);
                            }
                            else
                            {
                                renderMeshArray = EntityManager.GetSharedComponentManaged<RenderMeshArray>(entity);

                            }
                            foreach ( var  mat in renderMeshArray.MaterialReferences )
                            {
                                SpriteRenderData.ReleaseMaterial(mat);
                            }
                        }
#endif
                        if (ecb.IsCreated)
                            ecb.DestroyEntity(entiyRefCount.entityPrefab);
                        else
                            EntityManager.DestroyEntity(entiyRefCount.entityPrefab);

                        if(entiyRefCount.entityPrefabVariants != null)
                        {
                            foreach( var varPrefab in  entiyRefCount.entityPrefabVariants )
                            {
                                if (ecb.IsCreated)
                                    ecb.DestroyEntity(varPrefab);
                                else
                                    EntityManager.DestroyEntity(varPrefab);
                            }
                        }
                        EntitiesPrefab.Remove(assetUnique);
                        safeRemoveGOID = assetUnique;

                        if(entiyRefCount.goPart != null && !entiyRefCount.pureGO)
                            GameObject.Destroy(entiyRefCount.goPart.go);
                    }
                }
            }

            if (ecb.IsCreated)
                ecb.DestroyEntity(entity);
            else
                EntityManager.DestroyEntity(entity);

            if (EntityManager.HasComponent<TransformAccessEntity>(entity))
            {
                var transEntity = EntityManager.GetComponentData<TransformAccessEntity>(entity);
                if (transEntity != TransformAccessEntity.Null)
                {
                    SyncTransformSystem.RemoveSyncTransform(transEntity);
                }
            }
            return safeRemoveGOID;
        }

        public static Entity GetChild(Entity entity, int i)
        {
            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            if(i < children.Length)
            {
                return children[i].Value;
            }
            return Entity.Null;
        }

        public static  Transform GetChildTransform(Entity entity, int i)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if(eGameObject.go && i < eGameObject.go.transform.childCount)
                {
                    return eGameObject.go.transform.GetChild(i);
                }
            }
            return null;
        }

        public static Transform GetRootTransform(Entity entity)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.go)
                {
                    return eGameObject.go.transform;
                }
            }
            return null;
        }

        public static Component GetChildComponent(Entity entity, int i, Type type)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.go && i < eGameObject.go.transform.childCount)
                {
                    return eGameObject.go.transform.GetChild(i).GetComponent(type);
                }
            }
            return null;
        }

        public static void ResetAnimationState(Entity entity, int stateIdx)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.animators != null)
                {
                    var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;

                    if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
                    {
                        foreach (var anim in eGameObject.animators)
                        {
                            anim.ResetTrigger(entiyRefCount.stateNames[stateIdx]);
                        }
                    }
                    return;
                }
            }

            PlayAnimationState(entity, 0);
        }

        public static GpuAnimatorState GetGpuAnimatorState(Entity entity, int stateIdx)
        {
            var animDataList = gpuAnimationDataArray.animDataList;
            GpuAnimatorState animatorState = new GpuAnimatorState();
            GpuAnimatorPlayComponent animPlay = new GpuAnimatorPlayComponent();
            var indexAndCount = EntityManager.GetComponentData<IndexAndCount>(entity);
            if (stateIdx >= indexAndCount.count)
                return default;
#if UNITY_2022_1_OR_NEWER
            animPlay.startPlayTime = Time.time;
#else
            animPlay.startPlayTime = Time.timeSinceLevelLoad;
#endif

            GpuAnimationData gpuAnimationData = animDataList[indexAndCount.index + stateIdx];
            animPlay.startFrameIndex = (UInt16)gpuAnimationData.startFrameIndex;
            animPlay.nbrOfFramesPerSample = (UInt16)gpuAnimationData.nbrOfFramesPerSample;
            bool loop = gpuAnimationData.loop;
            animPlay.loop = loop ? (byte)1 : (byte)0;
            int nextid = gpuAnimationData.nextStateIndex;
            if (!loop && nextid >= 0 && nextid != stateIdx)
            {
                animPlay.nextStartFrameIndex = (ushort)animDataList[indexAndCount.index + nextid].startFrameIndex;
                animPlay.nextNbrOfFramesPerSample = (ushort)animDataList[indexAndCount.index + nextid].nbrOfFramesPerSample;
                animPlay.intervalNbrOfFrames = animPlay.nextNbrOfFramesPerSample;
            }
            else
                animPlay.intervalNbrOfFrames = 0;

            animatorState.Value.x = animPlay.startPlayTime;
            uint packed0 = (uint)animPlay.startFrameIndex | ((uint)animPlay.nbrOfFramesPerSample << 10) | ((uint)animPlay.intervalNbrOfFrames << 20) | ((uint)animPlay.loop << 30);
            animatorState.Value.y = UnsafeUtility.As<uint, float>(ref packed0);
            uint packed1 = (uint)animPlay.nextStartFrameIndex | ((uint)animPlay.nextNbrOfFramesPerSample << 10);
            animatorState.Value.z = UnsafeUtility.As<uint, float>(ref packed1);
            return animatorState;
        }

        public static void PlayAnimationState(Entity entity, int stateIdx)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if(eGameObject.animators != null)
                {
                    var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;

                    if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
                    {
                        foreach (var anim in eGameObject.animators)
                        {
                            anim.SetTrigger(entiyRefCount.stateNames[stateIdx]);
                        }
                    }
                    return;
                }
            }

            var ecb = ECBInitSystem.GetEcb();

            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            IndexAndCount indexAndCount = default;

            GpuAnimatorState gpuAnimatorState = default;

            if (EntityManager.HasComponent<GpuAnimatorState>(entity))
            {
                gpuAnimatorState = GetGpuAnimatorState(entity, stateIdx);
                if(gpuAnimatorState.Value.x > 0)
                {
                    if (ecb.IsCreated)
                        ecb.SetComponent(entity, gpuAnimatorState);
                    else
                        EntityManager.SetComponentData(entity, gpuAnimatorState);
                }
                return;
            }

            if (!EntityManager.HasComponent<LinkedEntityGroup>(entity))
                return;

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            for (int i=1; i<children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<GpuAnimatorState>(child))
                    continue;
                if (gpuAnimatorState.Value.x <= 0)
                {
                    gpuAnimatorState = GetGpuAnimatorState(child, stateIdx);
                }

                if(gpuAnimatorState.Value.x > 0)
                {
                    if (ecb.IsCreated)
                        ecb.SetComponent(child, gpuAnimatorState);
                    else
                        EntityManager.SetComponentData(child, gpuAnimatorState);
                }
            }
        }

        public static void PlayStateLoopInterval(Entity entity, int attackID, bool useLoopInterval, float attackInterval)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.animators != null)
                {
                    var assetUnique = EntityManager.GetComponentData<AssetUnique>(entity).Value;

                    if (EntitiesPrefab.TryGetValue(assetUnique, out EntiyRefCount entiyRefCount))
                    {
                        foreach (var anim in eGameObject.animators)
                        {
                            anim.SetTrigger(entiyRefCount.stateNames[attackID]);
                        }
                    }
                    return;
                }
            }

            var world = World.DefaultGameObjectInjectionWorld;

            var ecb = ECBInitSystem.GetEcb();

            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (!EntityManager.HasComponent<LinkedEntityGroup>(entity))
            {
                Debug.LogWarning("LinkedEntityGroup not found on PlayerState");
                return;
            }

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            IndexAndCount indexAndCount = default;
            GpuAnimatorPlayComponent animPlay = new GpuAnimatorPlayComponent();
            var animDataList = gpuAnimationDataArray.animDataList;
            GpuAnimatorState animState = new GpuAnimatorState();

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<GpuAnimatorState>(child))
                    continue;
                if (animPlay.startPlayTime <= 0)
                {
                    indexAndCount = EntityManager.GetComponentData<IndexAndCount>(child);
                    if (attackID >= indexAndCount.count)
                        return;
                    animPlay.startPlayTime = Time.timeSinceLevelLoad;

                    int stateIdx = useLoopInterval ? attackID : 0;

                    GpuAnimationData gpuAnimationData = animDataList[indexAndCount.index + stateIdx];
                    animPlay.startFrameIndex = (UInt16)gpuAnimationData.startFrameIndex;
                    animPlay.nbrOfFramesPerSample = (UInt16)gpuAnimationData.nbrOfFramesPerSample;
                    bool loop = gpuAnimationData.loop;
                    animPlay.loop = loop ? (byte)1 : (byte)0;
                    if (useLoopInterval)
                    {
                        animPlay.intervalNbrOfFrames = (ushort)(attackInterval * GlobalConstants.SampleFrameRate);
                        int nextid = gpuAnimationData.nextStateIndex;

                        animPlay.nextStartFrameIndex = (ushort)animDataList[indexAndCount.index + nextid].startFrameIndex;
                        animPlay.nextNbrOfFramesPerSample = (ushort)animDataList[indexAndCount.index + nextid].nbrOfFramesPerSample;

                    }
                    else
                        animPlay.intervalNbrOfFrames = 0;

                    animState = animPlay.GetEncodeState();
                }
                if (ecb.IsCreated)
                    ecb.SetComponent(child, animState);
                else
                    EntityManager.SetComponentData(child, animState);
            }
        }

        public static void SetEnable(Entity entity, bool enable)
        {
            if (entity == Entity.Null)
                return;
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if(eGameObject.go)
                    eGameObject.go.SetActive(enable);
            }

            EntityCommandBuffer ecb = ECBInitSystem.GetEcb();
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (ecb.IsCreated)
                ecb.SetEnabled(entity, enable);
            else
                EntityManager.SetEnabled(entity, enable);
        }
        public static void SetActive(Entity entity, bool enable)
        {
            SetEnable(entity, enable);
        }

        public static void SetSpeedFactor(Entity entity, float value)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.animators != null)
                {
                    foreach (var anim in eGameObject.animators)
                    {
                        anim.speed = value;
                    }
                    return;
                }
            }

            var ecb = ECBInitSystem.GetEcb();
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }
            var speedFactor = new GpuAnimatorSpeedFactor();
            speedFactor.value = value;

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<GpuAnimatorSpeedFactor>(child))
                    continue;
                if (ecb.IsCreated)
                    ecb.SetComponent(child, speedFactor);
                else
                    EntityManager.SetComponentData(child, speedFactor);

            }

        }

        public static void SetLayer(Entity entity, int layer)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.renderers != null)
                {
                    foreach (var r in eGameObject.renderers)
                    {
                        r.gameObject.layer = layer;
                    }
                    return;
                }
            }
#if UNITY_2022_2_OR_NEWER
            var ecb = ECBInitSystem.GetEcb();

            RenderFilterSettings filterSettings;
            if (EntityManager.HasComponent<RenderFilterSettings>(entity))
            {
                filterSettings = EntityManager.GetSharedComponentManaged<RenderFilterSettings>(entity);
                if (filterSettings.Layer != layer)
                {
                    filterSettings.Layer = layer;
                    ecb.SetSharedComponent(entity, filterSettings);
                }
                return;
            }

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            if (children.Length <= 1)
                return;

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                filterSettings = EntityManager.GetSharedComponentManaged<RenderFilterSettings>(child);
                if (filterSettings.Layer != layer)
                {
                    filterSettings.Layer = layer;
                    ecb.SetSharedComponent(child, filterSettings);
                }

            }

#else
            var ecb = ECBInitSystem.GetEcb();

            if (EntityManager.HasComponent<RenderMesh>(entity))
            {
                var renderMesh = EntityManager.GetSharedComponentManaged<RenderMesh>(entity);
                if (renderMesh.layer != layer)
                {
                    renderMesh.layer = layer;
                    ecb.SetSharedComponent(entity, renderMesh);
                }
                return;
            }

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<RenderMesh>(child))
                    continue;

                var renderMesh = EntityManager.GetSharedComponentManaged<RenderMesh>(child);
                if (renderMesh.layer != layer)
                {
                    renderMesh.layer = layer;
                    ecb.SetSharedComponent(child, renderMesh);
                }

            }
#endif
        }

        public static (float, float, float) GetColliderSize(Entity entity)
        {
            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                var boxCollider = eGameObject.go?.GetComponentInChildren<BoxCollider>();
                if(boxCollider != null)
                {
                    var size = boxCollider.bounds.size;
                    return (size.x, size.y, size.z);
                }
            }

            return (0, 0, 0);
        }
        public static void SetMaterialFloat(Entity entity, int shaderID, double value)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.renderers != null)
                {
                    foreach (var renderer in eGameObject.renderers)
                    {
                        renderer.SetPropertyBlock(eGameObject.propertyBlock);
                    }
                    return;
                }
            }

            var ecb = ECBInitSystem.GetEcb();
            {
                uint packed = (uint)value;
                var color32 = new MaterialColor32 { Value = UnsafeUtility.As<uint, float>(ref packed) };

                if (EntityManager.HasComponent<MaterialColor32>(entity))
                {
                    ecb.SetComponent(entity, color32);
                    return;
                }

                DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

                for (int i = 1; i < children.Length; i++)
                {
                    Entity child = children[i].Value;
                    if (!EntityManager.HasComponent<MaterialColor32>(child))
                        continue;

                    ecb.SetComponent(child, color32);

                }
            }

        }

        static Vector4 paramPack;
        public static void SetMaterialFloat4(Entity entity, int shaderID, float value1,float value2,float value3,float value4)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                if (eGameObject.renderers != null)
                {
                    paramPack.Set(value1, value2, value3, value4);
                    eGameObject.propertyBlock.SetVector(shaderID, paramPack);
                    foreach (var renderer in eGameObject.renderers)
                    {
                        renderer.SetPropertyBlock(eGameObject.propertyBlock);
                    }
                    return;
                }
            }

            var ecb = ECBInitSystem.GetEcb();
            paramPack.Set(value1, value2, value3, value4);
            var materialFloat4 = new MaterialFloat4 { Value = paramPack };
            if (EntityManager.HasComponent<MaterialFloat4>(entity))
            {
                ecb.SetComponent(entity, materialFloat4);
                return;
            }

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<MaterialFloat4>(child))
                    continue;

                ecb.SetComponent(child, materialFloat4);

            }
        }

        public static void SetPositon(Entity entity, float x, float y, float z)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                var t = eGameObject.go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(x, y, z);
                }
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            var localTransform = EntityManager.GetComponentData<LocalTransform>(entity);
            localTransform.Position = new float3(x, y, z);
            ecb.SetComponent(entity, localTransform);
        }

        public static Vector3 UnpackRotation(double packed)
        {
            float yi = (float)(packed % 100000) / 100f;
            float xi = (float)((packed / 100000) % 100000) / 100f;
            float zi = (float)(packed / 10000000000) / 100f;

            return new Vector3(xi, yi, zi);
        }

        public static void SetLocalTransform(Entity entity, float posx, float posy, float posz, double rotatePack, float scale )
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            if (EntityManager.HasComponent<EntityGameObject>(entity))
            {
                var eGameObject = EntityManager.GetComponentObject<EntityGameObject>(entity);
                var t = eGameObject.go.transform;
                if (t != null)
                {
                    t.localPosition = new Vector3(posx, posy, posz);
                    t.localRotation = Quaternion.Euler(UnpackRotation(rotatePack));
                    t.localScale = new Vector3(scale, scale, scale);
                }
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            float x,y, z;
            var localTransfrom = LocalTransform.FromPositionRotationScale(new float3(posx, posy, posz), Quaternion.Euler(UnpackRotation(rotatePack)), scale);
            if (ecb.IsCreated)
                ecb.SetComponent(entity, localTransfrom);
            else
                EntityManager.SetComponentData(entity, localTransfrom);
        }

        public static void SetSpriteColor(Entity entity, Color color)
        {
            if (!EntityManager.HasComponent<SpriteColor>(entity))
            {
                Debug.LogWarning($"SpriteColor component not found on entity {entity.Index}");
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            var spriteColor = new SpriteColor { value = EncodeColorToFloat(color) };

            if (ecb.IsCreated)
                ecb.SetComponent(entity, spriteColor);
            else
                EntityManager.SetComponentData(entity, spriteColor);
        }

        public static void SetSpriteFlipX(Entity entity, bool flipX)
        {
            if (!EntityManager.HasComponent<SpriteFlipX>(entity))
            {
                Debug.LogWarning($"SpriteFlipX component not found on entity {entity.Index}");
                return;
            }
            var ecb = ECBInitSystem.GetEcb();
            var spriteFlipX = new SpriteFlipX { value = flipX ? 1f : 0f };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, spriteFlipX);
            else
                EntityManager.SetComponentData(entity, spriteFlipX);
        }

        public static void SetSpriteFlipY(Entity entity, bool flipY)
        {
            if (!EntityManager.HasComponent<SpriteFlipY>(entity))
            {
                Debug.LogWarning($"SpriteFlipY component not found on entity {entity.Index}");
                return;
            }
            var ecb = ECBInitSystem.GetEcb();
            var spriteFlipY = new SpriteFlipY { value = flipY ? 1f : 0f };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, spriteFlipY);
            else
                EntityManager.SetComponentData(entity, spriteFlipY);
        }

        public static void SetSpriteTileSize(Entity entity, float tiledWidth)
        {
            if (!EntityManager.HasComponent<SpriteTileSize>(entity))
            {
                Debug.LogWarning($"SpriteTileSize component not found on entity {entity.Index}");
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            var spriteTileSize = new SpriteTileSize { value = tiledWidth * 1.95f };

            if (ecb.IsCreated)
                ecb.SetComponent(entity, spriteTileSize);
            else
                EntityManager.SetComponentData(entity, spriteTileSize);
        }

        public static void SetSpriteAnimationSpeed(Entity entity, float animationSpeed)
        {
            if (!EntityManager.HasComponent<SpriteAnimationSpeed>(entity))
            {
                Debug.LogWarning($"SpriteAnimationSpeed component not found on entity {entity.Index}");
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            var spriteAnimationSpeed = new SpriteAnimationSpeed { value = animationSpeed };

            if (ecb.IsCreated)
                ecb.SetComponent(entity, spriteAnimationSpeed);
            else
                EntityManager.SetComponentData(entity, spriteAnimationSpeed);
        }

        public static void SetSpriteBillBord(Entity entity, double value)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"entity not found index {entity.Index} version {entity.Version}");
                return;
            }

            long encode = (long)value;
            uint low = (uint)(encode & ((1 << 30) - 1));
            uint height = (uint)(encode >> 30);
            SpriteBillBord spriteBillBord = new SpriteBillBord
            {
                Value = new float2(UnsafeUtility.As<uint, float>(ref low), UnsafeUtility.As<uint, float>(ref height))
            };
            var ecb = ECBInitSystem.GetEcb();
            if (EntityManager.HasComponent<SpriteBillBord>(entity))
            {
                ecb.SetComponent(entity, spriteBillBord);
                return;
            }

            DynamicBuffer<LinkedEntityGroup> children = EntityManager.GetBuffer<LinkedEntityGroup>(entity);

            for (int i = 1; i < children.Length; i++)
            {
                Entity child = children[i].Value;
                if (!EntityManager.HasComponent<SpriteBillBord>(child))
                    continue;

                ecb.SetComponent(child, spriteBillBord);
            }
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

        public static void SetSpriteRectParam(Entity entity, Sprite sprite)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"SpriteRectParam component not found on entity {entity.Index}");
                return;
            }

            var ecb = ECBInitSystem.GetEcb();
            float texWidth = sprite.texture.width;
            float texHeight = sprite.texture.height;
            var comp = new SpriteRectParam {
                value = new float4(
                    sprite.textureRect.x / texWidth,
                    sprite.textureRect.y / texHeight,
                    sprite.textureRect.width / texWidth,
                    sprite.textureRect.height / texHeight
                )
            };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, comp);
            else
                EntityManager.SetComponentData(entity, comp);
        }
        public static void SetSpriteRectParamSafe(Entity entity, Sprite sprite)
        {
            if (!EntityManager.Exists(entity))
            {
                Debug.LogWarning($"SpriteRectParam component not found on entity {entity.Index}");
                return;
            }
#if UNITY_2022_2_OR_NEWER
            if (!EntityManager.HasComponent<RenderMeshArray>(entity))
                return;

            var RenderMeshArray = EntityManager.GetSharedComponentManaged<RenderMeshArray>(entity);
            var MaterialReferences = RenderMeshArray.MaterialReferences;
            int i = 0;
            var texture = sprite.texture;
            if(texture == null)
                return;
            Material foundMat = null;
            int holeIdx = -1;
            for (; i < MaterialReferences.Length; i++)
            {
                var mat = (Material)MaterialReferences[i];
                if (mat == null)
                {
                    holeIdx = i;
                    continue;
                }
                if (mat.mainTexture == texture)
                {
                    foundMat = mat;
                    break;
                }
            }

            var world = World.DefaultGameObjectInjectionWorld;
            SystemHandle handle = world.GetExistingSystem<MaterialGcSystem>();
            ref MaterialGcSystem materialGcSystem = ref world.Unmanaged.GetUnsafeSystemRef<MaterialGcSystem>(handle);

            int meshIdx = 0;
            var MeshReferences = RenderMeshArray.MeshReferences;
            if (MeshReferences.Length >= 2)
            {
                for (int j = 0; j < MeshReferences.Length; j++)
                {
                    var mesh = (Mesh)MeshReferences[j];
                    if (mesh == SpriteRenderData.SpriteQuad)
                    {
                        meshIdx = j;
                        break;
                    }
                }

            }

            if (i < MaterialReferences.Length){
                int key = texture.GetInstanceID();
                if (materialGcSystem.Exists(key) )
                {
                    if (EntityManager.HasComponent<MaterialRef>(entity))
                        EntityManager.SetComponentData(entity, new MaterialRef { Key = key });
                    else
                        EntityManager.AddComponentData(entity, new MaterialRef { Key = key });

                    EntityManager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(i, meshIdx, 0));
                }
            }
            else{

                var newMat = materialGcSystem.GetOrCreateMaterial(texture);
                if (EntityManager.HasComponent<MaterialRef>(entity))
                    EntityManager.SetComponentData(entity, new MaterialRef { Key = texture.GetInstanceID() });
                else
                    EntityManager.AddComponentData(entity, new MaterialRef { Key = texture.GetInstanceID() });
                int insertIdx  = holeIdx == -1 ? MaterialReferences.Length : holeIdx;
                UnityObjectRef<Material>[] newMaterialReferences;
                if (holeIdx == -1)
                {
                    newMaterialReferences = new UnityObjectRef<Material>[MaterialReferences.Length + 1];

                    Array.Copy(MaterialReferences, newMaterialReferences, MaterialReferences.Length);
                }
                else
                {
                    newMaterialReferences = MaterialReferences;
                }

                newMaterialReferences[insertIdx] = newMat;
                RenderMeshArray.MaterialReferences = newMaterialReferences;
                RenderMeshArray.ResetHash128();

                EntityManager.SetComponentData(entity, MaterialMeshInfo.FromRenderMeshArrayIndices(insertIdx, meshIdx, 0));
                EntityManager.SetSharedComponentManaged(EntityManager.GetChunk(entity), RenderMeshArray);

            }
#endif

            var ecb = ECBInitSystem.GetEcb();
            float texWidth = sprite.texture.width;
            float texHeight = sprite.texture.height;
            var comp = new SpriteRectParam
            {
                value = new float4(
                    sprite.textureRect.x / texWidth,
                    sprite.textureRect.y / texHeight,
                    sprite.textureRect.width / texWidth,
                    sprite.textureRect.height / texHeight
                )
            };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, comp);
            else
                EntityManager.SetComponentData(entity, comp);
        }

        public static void SetSpritePivotScale(Entity entity, Sprite sprite)
        {
            if (!EntityManager.HasComponent<SpritePivotScale>(entity))
            {
                Debug.LogWarning($"SpritePivotScale component not found on entity {entity.Index}");
                return;
            }
            var scale = new float2(sprite.textureRect.width / sprite.pixelsPerUnit, sprite.textureRect.height / sprite.pixelsPerUnit);
            float2 pivot = sprite.pivot / sprite.rect.size;
            var ecb = ECBInitSystem.GetEcb();
            var comp = new SpritePivotScale { value = new float4(pivot.x, pivot.y, scale.x, scale.y) };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, comp);
            else
                EntityManager.SetComponentData(entity, comp);
        }

        public static void SetPlanarShadowCullDist(float dist)
        {
#if UNITY_2022_2_OR_NEWER
            var world = World.DefaultGameObjectInjectionWorld;
            if(world != null)
            {
                var EntitiesGraphicsSystem = world.GetExistingSystemManaged<EntitiesGraphicsSystem>();
                EntitiesGraphicsSystem.PlanarShadowCullDist = dist;
            }

#endif
        }

#if UNITY_2022_2_OR_NEWER
        public static void SetMaterialGcSweepInterval(int sweepInterval)
        {
            var world = World.DefaultGameObjectInjectionWorld;
            SystemHandle handle = world.GetExistingSystem<MaterialGcSystem>();
            ref MaterialGcSystem materialGcSystem = ref world.Unmanaged.GetUnsafeSystemRef<MaterialGcSystem>(handle);
            materialGcSystem.SetSweepInterval(sweepInterval);
        }
#endif
        public static void SetSpriteFadeStartTime(Entity entity, float time)
        {
            if (!EntityManager.HasComponent<SpriteFadeStartTime>(entity))
            {
                Debug.LogWarning($"SpriteFadeStartTime component not found on entity {entity.Index}");
                return;
            }
            var ecb = ECBInitSystem.GetEcb();
            var fadeStart = new SpriteFadeStartTime { time = time };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, fadeStart);
            else
                EntityManager.SetComponentData(entity, fadeStart);
        }

        public static void SetSpriteFitDynamicScaleX(Entity entity, float value)
        {
            if (!EntityManager.HasComponent<SpriteFitDynamicScaleX>(entity))
            {
                UnityEngine.Debug.LogWarning($"SpriteFitDynamicScaleX component not found on entity {entity.Index}");
                return;
            }
            var ecb = ECBInitSystem.GetEcb();
            var comp = new SpriteFitDynamicScaleX { value = value };
            if (ecb.IsCreated)
                ecb.SetComponent(entity, comp);
            else
                EntityManager.SetComponentData(entity, comp);
        }

        public static void SetEntitySortingOrder(Entity entity,int sortingOrder)
        {
            if (!EntityManager.HasComponent<RenderFilterSettings>(entity))
            {
                UnityEngine.Debug.LogWarning($"RenderFilterSettings component not found on entity {entity.Index}");
                return;
            }
            RenderFilterSettings filterSettings = EntityManager.GetSharedComponentManaged<RenderFilterSettings>(entity);
            filterSettings.sortingOrder = sortingOrder;
            EntityManager.SetSharedComponentManaged(entity, filterSettings);
        }

        private static bool sVegetationEcs = false;
        public static void EnableVegetationEcs(bool enable)
        {
            sVegetationEcs = enable;
        }

        public static bool VegetationEcs => sVegetationEcs;

        private static bool sAnimationTexEcs = true;

        public static bool AnimationTexEcs => sAnimationTexEcs;

    }
}
