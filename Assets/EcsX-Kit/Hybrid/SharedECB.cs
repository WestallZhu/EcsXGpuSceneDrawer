using System;
using Unity.Collections;

namespace Unity.Entities
{

    [UpdateInGroup(typeof(InitializationSystemGroup))]
    public partial class ECBInitSystem : SystemBase
    {
        public EntityCommandBuffer UpdateEcb;
        public EntityCommandBuffer LateUpdateEcb;
        public bool afterUpdate;
        protected override void OnCreate()
        {
            EntityHybridUtility.Create();
        }

        public EntityCommandBuffer GetEcb()
        {
            return afterUpdate ? LateUpdateEcb : UpdateEcb;
        }
        protected override void OnUpdate()
        {
            if (UpdateEcb.IsCreated) UpdateEcb.Dispose();
            if (LateUpdateEcb.IsCreated) LateUpdateEcb.Dispose();

            UpdateEcb = new EntityCommandBuffer(Allocator.TempJob);
            LateUpdateEcb = new EntityCommandBuffer(Allocator.TempJob);
            afterUpdate = false;
        }

        protected override void OnDestroy()
        {
            EntityHybridUtility.Dispose();
        }
    }

    [UpdateInGroup(typeof(SimulationSystemGroup))]
    [UpdateAfter(typeof(BeginSimulationEntityCommandBufferSystem))]
    public partial class ECBUpdatePlaybackSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ECBInitSystem = World.GetExistingSystemManaged<ECBInitSystem>();
            if (ECBInitSystem == null)
                return;

            ECBInitSystem.UpdateEcb.Playback(EntityManager);
            ECBInitSystem.UpdateEcb.Dispose();
            ECBInitSystem.afterUpdate = true;
        }
    }

    [UpdateInGroup(typeof(PresentationSystemGroup))]
    [UpdateAfter(typeof(BeginPresentationEntityCommandBufferSystem))]
    public partial class ECBLateUpdatePlaybackSystem : SystemBase
    {
        protected override void OnUpdate()
        {
            var ECBInitSystem = World.GetExistingSystemManaged<ECBInitSystem>();
            if (ECBInitSystem == null)
                return;

            ECBInitSystem.LateUpdateEcb.Playback(EntityManager);
            ECBInitSystem.LateUpdateEcb.Dispose();
        }
    }

}