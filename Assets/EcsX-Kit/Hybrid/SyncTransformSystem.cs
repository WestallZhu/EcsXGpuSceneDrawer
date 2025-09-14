using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Jobs;

namespace Unity.Entities
{

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(StructuralChangePresentationSystemGroup))]
    public partial class SyncTransformSystem : LuaEntitySystemBase
    {
        private TransformAccessChunk transformAccessChunk;

        PreUpdateBarrierSystem preUpdateBarrierSystem;
        protected override void OnCreate()
        {
            transformAccessChunk = new TransformAccessChunk(128);
            preUpdateBarrierSystem = World.GetExistingSystemManaged<PreUpdateBarrierSystem>();
        }

        public TransformAccessEntity AddSyncTransform(in Entity entity, Transform transform)
        {
            return transformAccessChunk.AddTransformEntity(transform, entity);
        }

        public void SetSyncTransformDirty(in TransformAccessEntity tEntity)
        {
            transformAccessChunk.SetDirty(tEntity);
        }

        public void RemoveSyncTransform(in TransformAccessEntity tEntity)
        {
            transformAccessChunk.RemoveTransformEntity(tEntity);
        }
        protected override JobHandle OnUpdate(JobHandle dependency)
        {
            if (preUpdateBarrierSystem == null)
            {
                preUpdateBarrierSystem = World.GetExistingSystemManaged<PreUpdateBarrierSystem>();
                if (preUpdateBarrierSystem == null)
                    return dependency;
            }

            var ecb = preUpdateBarrierSystem.CreateCommandBuffer();

            var job = new SyncTransformJob
            {
                entities = transformAccessChunk.m_Entities.AsArray(),
                previousTransforms = transformAccessChunk.m_PreviousTransforms.AsArray(),
                ecb = ecb.AsParallelWriter(),
            };

            jobHandle = job.Schedule(transformAccessChunk.m_TransformAccessArray, dependency);

            preUpdateBarrierSystem?.AddBarrierJob(jobHandle);
            return jobHandle;
        }

        JobHandle jobHandle;
        protected override void OnDestroy()
        {
            jobHandle.Complete();
            transformAccessChunk.Dispose();
        }
    }

    [BurstCompile]
    public struct SyncTransformJob : IJobParallelForTransform
    {
        [ReadOnly] public NativeArray<Entity> entities;
        public EntityCommandBuffer.ParallelWriter ecb;
        public NativeArray<LocalTransform> previousTransforms;

        public void Execute(int index, TransformAccess transform)
        {
            Entity entity = entities[index];
            LocalTransform current = LocalTransform.FromMatrix(transform.localToWorldMatrix);

            LocalTransform previous = previousTransforms[index];

            bool3 changed;
            changed.x = math.distancesq(current.Position, previous.Position) > 0.00001f;
            changed.y = math.any(math.abs(current.Rotation.value - previous.Rotation.value) > 0.00001f);
            changed.z = math.abs(current.Scale - previous.Scale) > 0.00001f;

            if (math.any(changed))
            {
                ecb.SetComponent(index, entity, current);
                previousTransforms[index] = current;
            }

        }
    }

}