using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Assertions;
using Unity.Burst.CompilerServices;
using Unity.Burst.Intrinsics;
using Unity.Jobs;
using Unity.Mathematics;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Unity.Entities
{


    [BurstCompile]
    [UpdateInGroup(typeof(TransformSystemGroup))]
    public unsafe partial struct LocalToWorldSystem : ISystem
    {
        [BurstCompile]
        unsafe struct ComputeWorldSpaceLocalToWorldJob : IJobChunk
        {
            [ReadOnly] public ComponentTypeHandle<LocalTransform> LocalTransformTypeHandleRO;
            public ComponentTypeHandle<LocalToWorld> LocalToWorldTypeHandleRW;
            public uint LastSystemVersion;

            public void Execute(in ArchetypeChunk chunk, int unfilteredChunkIndex, bool useEnabledMask, in v128 chunkEnabledMask)
            {
                LocalTransform* chunkLocalTransforms = (LocalTransform*)chunk.GetComponentDataPtrRO(ref LocalTransformTypeHandleRO);
                LocalToWorld* chunkLocalToWorlds = (LocalToWorld*)chunk.GetComponentDataPtrRW(ref LocalToWorldTypeHandleRW);
                {
                    for (int i = 0, chunkEntityCount = chunk.Count; i < chunkEntityCount; ++i)
                    {
                        chunkLocalToWorlds[i].Value = chunkLocalTransforms[i].ToMatrix();
                    }
                }
            }
        }

        [BurstCompile]
        struct ComputeHierarchyLocalToWorldJob : IJobParallelForDefer
        {
            [ReadOnly] public NativeArray<Entity> RootEntities;

            [ReadOnly] public BufferLookup<LinkedEntityGroup> ChildLookupRO;

            [ReadOnly] public ComponentLookup<LocalTransform> LocalTransformLookupRO;

            [NativeDisableContainerSafetyRestriction] public ComponentLookup<LocalToWorld> LocalToWorldLookupRW;

            public uint LastSystemVersion;

            void ChildLocalToWorldFromTransformMatrix(in float3x4 parentLocalToWorld, Entity childEntity, bool updateChildrenTransform)
            {

                updateChildrenTransform = updateChildrenTransform
                                          || LocalTransformLookupRO.DidChange(childEntity, LastSystemVersion);

                float3x4 localToWorld;

                bool hasChildBuffer = ChildLookupRO.TryGetBuffer(childEntity, out DynamicBuffer<LinkedEntityGroup> children);

                if (updateChildrenTransform)
                {

                    var localTransform = LocalTransformLookupRO[childEntity];
                    localToWorld = mathEx.mul(parentLocalToWorld, localTransform.ToMatrix());
                    LocalToWorldLookupRW[childEntity] = new LocalToWorld { Value = localToWorld };

                    if (hasChildBuffer)
                    {

                        for (int i = 1, childCount = children.Length; i < childCount; i++)
                        {

                            ChildLocalToWorldFromTransformMatrix(localToWorld, children[i].Value, true);
                        }
                    }
                }
                else
                {

                    if (hasChildBuffer)
                    {

                        localToWorld = LocalToWorldLookupRW[childEntity].Value;

                        for (int i = 1, childCount = children.Length; i < childCount; i++)
                        {
                            ChildLocalToWorldFromTransformMatrix(localToWorld, children[i].Value, updateChildrenTransform);
                        }
                    }
                }
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void ChildLocalToWorldFromTransformMatrixNoRecursion(in float3x4 parentLocalToWorld, Entity childEntity, bool updateChildrenTransform)
            {

                updateChildrenTransform = updateChildrenTransform
                          || LocalTransformLookupRO.DidChange(childEntity, LastSystemVersion);

                if (updateChildrenTransform)
                {
                    float3x4 localToWorld;

                    var localTransform = LocalTransformLookupRO[childEntity];
                    localToWorld = mathEx.mul(parentLocalToWorld, localTransform.ToMatrix());
                    LocalToWorldLookupRW[childEntity] = new LocalToWorld { Value = localToWorld };
                }
            }

            public void Execute(int index)
            {
                Entity root = RootEntities[index];
                if (ChildLookupRO.TryGetBuffer(root, out DynamicBuffer<LinkedEntityGroup> children))
                {
                    bool updateChildrenTransform = ChildLookupRO.DidChange(root, LastSystemVersion) ||
                                                   LocalToWorldLookupRW.DidChange(root, LastSystemVersion);
                    float3x4 localToWorldMatrix = LocalToWorldLookupRW[root].Value;
                    for (int j = 1, childCount = children.Length; j < childCount; j++)
                    {
                        ChildLocalToWorldFromTransformMatrixNoRecursion(localToWorldMatrix, children[j].Value, updateChildrenTransform);
                    }
                }
            }
        }

        EntityQuery _worldSpaceQuery;
        EntityQuery _hierarchyRootsQuery;

        ComponentTypeHandle<LocalTransform> _localTransformTypeHandleRO;
        ComponentTypeHandle<LocalToWorld> _localToWorldTypeHandleRW;

        BufferLookup<LinkedEntityGroup> _childLookupRO;

        ComponentLookup<LocalTransform> _localTransformLookupRO;

        ComponentLookup<LocalToWorld> _localToWorldLookupRW;

        public void OnCreate(ref SystemState state)
        {
            _worldSpaceQuery = state.GetEntityQuery(new EntityQueryDesc()
            {
                All = new ComponentType[]
                {
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadWrite<LocalToWorld>(),
                },
                None = new ComponentType[]
                {
                    typeof(Parent)
                },
                Options = EntityQueryOptions.FilterWriteGroup | EntityQueryOptions.IncludeDisabledEntities
            });

            _worldSpaceQuery.SetChangedVersionFilter(ComponentType.ReadOnly<LocalTransform>());

            _hierarchyRootsQuery = state.GetEntityQuery(new EntityQueryDesc()
            {
                All = new ComponentType[]
                {
                    typeof(LocalToWorld),
                    ComponentType.ReadOnly<LocalTransform>(),
                    ComponentType.ReadOnly<LinkedEntityGroup>(),
                },
                None = new ComponentType[]
                {
                    typeof(Parent)
                },
                Options = EntityQueryOptions.FilterWriteGroup
            }
            );

            _localTransformTypeHandleRO = state.GetComponentTypeHandle<LocalTransform>(true);
            _localToWorldTypeHandleRW = state.GetComponentTypeHandle<LocalToWorld>(false);
            _childLookupRO = state.GetBufferLookup<LinkedEntityGroup>(true);
            _localTransformLookupRO = state.GetComponentLookup<LocalTransform>(true);
            _localToWorldLookupRW = state.GetComponentLookup<LocalToWorld>(false);
        }

        [BurstCompile]
        public void OnDestroy(ref SystemState state)
        {
        }

        [BurstCompile]
        public void OnUpdate(ref SystemState state)
        {
            _localTransformTypeHandleRO.Update(ref state);
            _localToWorldTypeHandleRW.Update(ref state);
            _childLookupRO.Update(ref state);
            _localTransformLookupRO.Update(ref state);
            _localToWorldLookupRW.Update(ref state);

            var worldSpaceJob = new ComputeWorldSpaceLocalToWorldJob
            {
                LocalTransformTypeHandleRO = _localTransformTypeHandleRO,
                LocalToWorldTypeHandleRW = _localToWorldTypeHandleRW,
                LastSystemVersion = state.LastSystemVersion,
            };
            var worldSpaceJobHandle = worldSpaceJob.ScheduleParallelByRef(_worldSpaceQuery, state.Dependency);
            if (_hierarchyRootsQuery.IsEmptyIgnoreFilter)
            {
                state.Dependency = worldSpaceJobHandle;
            }
            else
            {

                var rootEntityList =
                    _hierarchyRootsQuery.ToEntityListAsync(state.WorldUpdateAllocator, state.Dependency,
                        out JobHandle gatherJobHandle);

                var hierarchyJob = new ComputeHierarchyLocalToWorldJob
                {
                    RootEntities = rootEntityList.AsDeferredJobArray(),
                    ChildLookupRO = _childLookupRO,
                    LocalTransformLookupRO = _localTransformLookupRO,
                    LocalToWorldLookupRW = _localToWorldLookupRW,
                    LastSystemVersion = state.LastSystemVersion,
                };

                state.Dependency = hierarchyJob.ScheduleByRef(rootEntityList, 1,
                    JobHandle.CombineDependencies(worldSpaceJobHandle, gatherJobHandle));
            }
        }
    }

}
