using UnityEngine.Jobs;
using Unity.Jobs;
using UnityEngine;

namespace Unity.Entities
{

    public abstract unsafe class LuaEntitySystemBase : ComponentSystemBase
    {

        static JobHandle s_LastDependency;

        public override void Update()
        {

#if UNITY_2022_2_OR_NEWER && ENABLE_PROFILER
            var state = CheckedState();
            using (state->m_ProfilerMarker.Auto())
#endif
            {
                if (Enabled)
                    s_LastDependency = OnUpdate(s_LastDependency);
            }

        }

        protected abstract JobHandle OnUpdate(JobHandle inputDeps);
    }
}
