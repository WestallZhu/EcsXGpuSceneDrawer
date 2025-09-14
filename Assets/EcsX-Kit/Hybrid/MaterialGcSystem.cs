using Unity.Burst;
using Unity.Collections;
using Unity.Entities;
using UnityEngine;

#if UNITY_2022_2_OR_NEWER

namespace Unity.Entities
{

    public struct MaterialRef : IComponentData
    {
        public int Key;
    }

    public partial struct MaterialGcSystem : ISystem
    {
        struct Entry
        {
            public int MatID;
            public byte Epoch;
        }

        NativeParallelHashMap<int, Entry> _map;
        byte _epoch;
        int _frameCounter;
        int _sweepInterval;

        const int kInitCapacity = 64;
        const int kSweepInterval = 1000;

        EntityQuery _materialGcQuery;

        public void OnCreate(ref SystemState state)
        {
            _map = new NativeParallelHashMap<int, Entry>(kInitCapacity, Allocator.Persistent);

            _materialGcQuery = SystemAPI.QueryBuilder().WithAll<MaterialRef>().Build();

            _epoch = 1;
            _frameCounter = 0;
            _sweepInterval = kSweepInterval;
        }

        public void OnDestroy(ref SystemState state)
        {

            foreach (var kv in _map)
            {
                var mat = Resources.InstanceIDToObject(kv.Value.MatID) as Material;
                if (mat) UnityEngine.Object.DestroyImmediate(mat);
            }
            _map.Dispose();
        }

        public void OnUpdate(ref SystemState state)
        {

            if (++_frameCounter < kSweepInterval)
                return;
            _frameCounter = 0;
            _epoch ^= 1;

            var materialRefs = _materialGcQuery.ToComponentDataArray<MaterialRef>(Allocator.Temp);
            foreach (var matRef in materialRefs)
            {
                if (_map.TryGetValue(matRef.Key, out var entry))
                {
                    entry.Epoch = _epoch;
                    _map[matRef.Key] = entry;
                }
            }
            materialRefs.Dispose();

            var kv = _map.GetKeyValueArrays(Allocator.Temp);
            for (int i = 0; i < kv.Length; ++i)
            {
                if (kv.Values[i].Epoch != _epoch)
                {
                    var mat = Resources.InstanceIDToObject(kv.Values[i].MatID) as Material;
                    if (mat) UnityEngine.Object.Destroy(mat);
                    _map.Remove(kv.Keys[i]);
                }
            }
            kv.Dispose();
        }

        public Material GetOrCreateMaterial(Texture2D tex)
        {
            int key = tex.GetInstanceID();
            if (key == 0) return null;

            if (_map.TryGetValue(key, out var entry))
            {
                entry.Epoch = _epoch;
                _map[key] = entry;
                return Resources.InstanceIDToObject(entry.MatID) as Material;
            }

            Material mat = SpriteRenderData.CreateSpriteMaterial(tex, false);
            if (!mat) return null;

            _map.Add(key, new Entry { MatID = mat.GetInstanceID(), Epoch = _epoch });
            return mat;
        }

        public bool Exists(int texKey) => _map.ContainsKey(texKey);

        public void SetSweepInterval(int sweepInterval)
        {
            _sweepInterval = sweepInterval;
        }
    }

}

#endif