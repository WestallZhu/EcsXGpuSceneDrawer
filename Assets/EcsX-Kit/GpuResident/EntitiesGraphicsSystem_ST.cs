#if UNITY_2022_2_OR_NEWER && UNITY_WEBGL && TUANJIE_1_6_OR_NEWER
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Entities;
using Unity.Entities.Graphics;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine.Profiling;
using UnityEngine.Rendering;

namespace Unity.Rendering
{
    public unsafe partial class EntitiesGraphicsSystem
    {
        private unsafe struct STBins
        {
            public UnsafeParallelHashMap<DrawCommandSettings, int> Map;
            public IndirectList<DrawCommandSettings> Keys;
            public UnsafeList<int> Counts, Offsets, Cursors;

            public void Init(RewindableAllocator* alloc, int cap)
            {
                var h = alloc->Handle;
                Map = new UnsafeParallelHashMap<DrawCommandSettings, int>(math.max(16, cap), Allocator.TempJob);
                Keys = new IndirectList<DrawCommandSettings>(math.max(16, cap), alloc);
                Counts = new UnsafeList<int>(math.max(16, cap), h, NativeArrayOptions.ClearMemory);
                Offsets = new UnsafeList<int>(math.max(16, cap), h, NativeArrayOptions.ClearMemory);
                Cursors = new UnsafeList<int>(math.max(16, cap), h, NativeArrayOptions.ClearMemory);
            }

            public int AddOrGet(ref DrawCommandSettings s)
            {
                s.ComputeHashCode();
                if (!Map.TryGetValue(s, out int idx))
                {
                    idx = Keys.Length; Keys.Add(s);
                    Counts.Add(0); Offsets.Add(0); Cursors.Add(0);
                    Map.Add(s, idx);
                }
                return idx;
            }

            public void Dispose()
            {
                if (Map.IsCreated) Map.Dispose();
                if (Counts.IsCreated) Counts.Dispose();
                if (Offsets.IsCreated) Offsets.Dispose();
                if (Cursors.IsCreated) Cursors.Dispose();
            }
        }

        private JobHandle OnPerformCulling(BatchRendererGroup rg, BatchCullingContext ctx, BatchCullingOutput output, IntPtr userCtx)
        {
            Profiler.BeginSample("OnPerformCulling_ST_NoBitmap");

            if (ctx.projectionType == BatchCullingProjectionType.Orthographic)
            {
                Profiler.EndSample();
                return default;
            }

            int chunkCount;
            try { chunkCount = m_EntitiesGraphicsRenderedQueryRO.CalculateChunkCountWithoutFiltering(); }
            catch (ObjectDisposedException) { ZeroOut(ref output); return default; }
            if (chunkCount == 0 || !ShouldRunSystem()) { ZeroOut(ref output); return default; }

            var chunks = m_EntitiesGraphicsRenderedQueryRO.ToArchetypeChunkArray(Allocator.Temp);
            var hInfo = GetComponentTypeHandle<EntitiesGraphicsChunkInfo>(true);
            var hMMI = GetComponentTypeHandle<MaterialMeshInfo>(true);
            var hFilter = GetSharedComponentTypeHandle<RenderFilterSettings>();
            var hRMA = GetSharedComponentTypeHandle<RenderMeshArray>();
            var hChunkBounds = GetComponentTypeHandle<ChunkWorldRenderBounds>(true);
            var hChunkSimpleLODs = GetComponentTypeHandle<ChunkSimpleLOD>(true);

            var brgMap = World.GetExistingSystemManaged<RegisterMaterialsAndMeshesSystem>()?.BRGRenderMeshArrays
                         ?? new NativeParallelHashMap<int, BRGRenderMeshArray>();

            var chunkVisible = new NativeArray<byte>(chunks.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);

            var split = ctx.cullingSplits[0];
            var planes = ctx.cullingPlanes.GetSubArray(split.cullingPlaneOffset, split.cullingPlaneCount);

            var bins = new STBins();
            bins.Init(m_ThreadLocalAllocators.GeneralAllocator, chunkCount * 2);
            int totalInstances = 0;

            for (int c = 0; c < chunks.Length; ++c)
            {
                var ch = chunks[c];
                var info = ch.GetChunkComponentData(ref hInfo);
                if (!info.Valid) continue;

                var cwrb = ch.GetChunkComponentData(ref hChunkBounds);
                if (!IsChunkVisibleAABB(cwrb, planes)) continue;

                if (ch.Has(ref hChunkSimpleLODs))
                {
                    var chunkSimpleLod = ch.GetChunkComponentData(ref hChunkSimpleLODs);
                    if (chunkSimpleLod.Value != m_SimpleChunkLOD) continue;
                }

                chunkVisible[c] = 1;

                int filterIndex = ch.GetSharedComponentIndex(hFilter);
                int sortingOrder = m_SortingOrders.TryGetValue(filterIndex, out var so) ? so : 0;
                var mmis = ch.GetNativeArray(ref hMMI);
                int len = mmis.Length;
                if (len == 0) continue;

                int batchIndex = info.BatchIndex;
                var batchID = new BatchID { value = (uint)batchIndex };

                BRGRenderMeshArray brg = default;
                int rmaIndex = ch.GetSharedComponentIndex(hRMA);
                if (rmaIndex < 0 || brgMap.IsEmpty || !brgMap.TryGetValue(rmaIndex, out brg)) continue;
                int materialsCount = brg.UniqueMaterials.IsCreated ? brg.UniqueMaterials.Length : 0;
                int meshesCount = brg.UniqueMeshes.IsCreated ? brg.UniqueMeshes.Length : 0;
                if (materialsCount <= 0 || meshesCount <= 0) continue;

                if (materialsCount == 1 && meshesCount == 1)
                {
                    ushort sub = (ushort)mmis[0].SubMesh;
                    var s0 = new DrawCommandSettings
                    {
                        FilterIndex = filterIndex,
                        SortingOrder = sortingOrder,
                        Flags = 0,
                        MaterialID = brg.UniqueMaterials[0],
                        MeshID = brg.UniqueMeshes[0],
                        SplitMask = 0,
                        SubMeshIndex = sub,
                        BatchID = batchID
                    };
                    int b0 = bins.AddOrGet(ref s0);
                    bins.Counts[b0] += len;
                    totalInstances += len;
                    continue;
                }

                int count = materialsCount * meshesCount;
                int* counts = stackalloc int[count];
                ushort* submesh = stackalloc ushort[count];
                for (int k = 0; k < count; ++k) submesh[k] = 0xFFFF;

                for (int i = 0; i < len; ++i)
                {
                    var mmi = mmis[i];
                    int mi = mmi.MaterialArrayIndex;
                    int xi = mmi.MeshArrayIndex;
                    if ((uint)mi >= (uint)materialsCount || (uint)xi >= (uint)meshesCount) continue;
                    int k = mi * meshesCount + xi;
                    if (++counts[k] == 1) submesh[k] = (ushort)mmi.SubMesh;
                }

                for (int k = 0; k < count; ++k)
                {
                    int n = counts[k];
                    if (n == 0) continue;
                    int matIdx = k / meshesCount;
                    int mshIdx = k % meshesCount;

                    var s = new DrawCommandSettings
                    {
                        FilterIndex = filterIndex,
                        SortingOrder = sortingOrder,
                        Flags = 0,
                        MaterialID = brg.UniqueMaterials[matIdx],
                        MeshID = brg.UniqueMeshes[mshIdx],
                        SplitMask = 0,
                        SubMeshIndex = submesh[k],
                        BatchID = batchID
                    };
                    int b = bins.AddOrGet(ref s);
                    bins.Counts[b] += n;
                    totalInstances += n;
                }
            }

            int binCount = bins.Keys.Length;
            var sorted = new NativeArray<int>(binCount, Allocator.Temp);
            for (int i = 0; i < binCount; ++i) sorted[i] = i;
            var cmp = new KeyIndexComparer { Keys = bins.Keys.List->Ptr };
            NativeSortExtension.Sort((int*)sorted.GetUnsafePtr(), binCount, cmp);

            int commandsNeeded = 0;
            for (int i = 0; i < binCount; ++i)
            {
                int n = bins.Counts[sorted[i]];
                int per = EntitiesGraphicsTuningConstants.kMaxInstancesPerDrawCommand;
                commandsNeeded += (n + per - 1) / per;
            }

            ref BatchCullingOutputDrawCommands out0 = ref *(BatchCullingOutputDrawCommands*)output.drawCommands.GetUnsafePtr();
            out0.visibleInstanceCount = totalInstances;
            out0.visibleInstances = totalInstances > 0 ? ChunkDrawCommandOutput.Malloc<int>(totalInstances) : null;
            out0.instanceSortingPositionFloatCount = 0; out0.instanceSortingPositions = null;
            out0.drawCommandCount = commandsNeeded;
            out0.drawCommands = commandsNeeded > 0 ? ChunkDrawCommandOutput.Malloc<BatchDrawCommand>(commandsNeeded) : null;
            out0.drawCommandPickingInstanceIDs = null;
            out0.drawRangeCount = commandsNeeded;
            out0.drawRanges = commandsNeeded > 0 ? ChunkDrawCommandOutput.Malloc<BatchDrawRange>(commandsNeeded) : null;

            int running = 0;
            for (int i = 0; i < binCount; ++i)
            {
                int b = sorted[i];
                bins.Offsets[b] = running;
                bins.Cursors[b] = 0;
                running += bins.Counts[b];
            }

            int* vi = out0.visibleInstances;
            for (int c = 0; c < chunks.Length; ++c)
            {
                if (chunkVisible[c] == 0) continue;

                var ch = chunks[c];
                var info = ch.GetChunkComponentData(ref hInfo);

                int filterIndex = ch.GetSharedComponentIndex(hFilter);
                int sortingOrder = m_SortingOrders.TryGetValue(filterIndex, out var so) ? so : 0;
                var mmis = ch.GetNativeArray(ref hMMI);
                int len = mmis.Length;
                if (len == 0) continue;

                int chunkStart = info.CullingData.ChunkOffsetInBatch;
                int batchIndex = info.BatchIndex;
                var batchID = new BatchID { value = (uint)batchIndex };

                BRGRenderMeshArray brg = default;
                int rmaIndex = ch.GetSharedComponentIndex(hRMA);
                if (rmaIndex < 0 || brgMap.IsEmpty || !brgMap.TryGetValue(rmaIndex, out brg)) continue;
                int materialsCount = brg.UniqueMaterials.IsCreated ? brg.UniqueMaterials.Length : 0;
                int meshesCount = brg.UniqueMeshes.IsCreated ? brg.UniqueMeshes.Length : 0;
                if (materialsCount <= 0 || meshesCount <= 0) continue;

                if (materialsCount == 1 && meshesCount == 1)
                {
                    ushort sub = (ushort)mmis[0].SubMesh;

                    var s0 = new DrawCommandSettings
                    {
                        FilterIndex = filterIndex,
                        SortingOrder = sortingOrder,
                        Flags = 0,
                        MaterialID = brg.UniqueMaterials[0],
                        MeshID = brg.UniqueMeshes[0],
                        SplitMask = 0,
                        SubMeshIndex = sub,
                        BatchID = batchID
                    };
                    s0.ComputeHashCode();
                    if (!bins.Map.TryGetValue(s0, out int b)) continue;

                    int dst = bins.Offsets[b] + bins.Cursors[b];
                    for (int i = 0; i < len; ++i) vi[dst + i] = chunkStart + i;
                    bins.Cursors[b] += len;
                    continue;
                }

                int count = materialsCount * meshesCount;

                int* counts = stackalloc int[count];
                ushort* submesh = stackalloc ushort[count];
                for (int k = 0; k < count; ++k) submesh[k] = 0xFFFF;

                for (int i = 0; i < len; ++i)
                {
                    var mmi = mmis[i];
                    int mi = mmi.MaterialArrayIndex;
                    int xi = mmi.MeshArrayIndex;
                    if ((uint)mi >= (uint)materialsCount || (uint)xi >= (uint)meshesCount) continue;
                    int k = mi * meshesCount + xi;
                    if (++counts[k] == 1) submesh[k] = (ushort)mmi.SubMesh;
                }

                int* binIndexOfK = stackalloc int[count];
                int* writePtr = stackalloc int[count];
                for (int k = 0; k < count; ++k) { binIndexOfK[k] = -1; writePtr[k] = 0; }

                for (int k = 0; k < count; ++k)
                {
                    int n = counts[k];
                    if (n == 0) continue;

                    int matIdx = k / meshesCount;
                    int mshIdx = k % meshesCount;
                    var s = new DrawCommandSettings
                    {
                        FilterIndex = filterIndex,
                        SortingOrder = sortingOrder,
                        Flags = 0,
                        MaterialID = brg.UniqueMaterials[matIdx],
                        MeshID = brg.UniqueMeshes[mshIdx],
                        SplitMask = 0,
                        SubMeshIndex = submesh[k],
                        BatchID = batchID
                    };
                    s.ComputeHashCode();
                    if (!bins.Map.TryGetValue(s, out int b)) continue;
                    binIndexOfK[k] = b;
                    writePtr[k] = bins.Offsets[b] + bins.Cursors[b];
                }

                for (int i = 0; i < len; ++i)
                {
                    var mmi = mmis[i];
                    int mi = mmi.MaterialArrayIndex;
                    int xi = mmi.MeshArrayIndex;
                    if ((uint)mi >= (uint)materialsCount || (uint)xi >= (uint)meshesCount) continue;
                    int k = mi * meshesCount + xi;
                    int b = binIndexOfK[k];
                    if (b < 0) continue;
                    int p = writePtr[k]++;
                    vi[p] = chunkStart + i;
                }

                for (int k = 0; k < count; ++k)
                {
                    int b = binIndexOfK[k];
                    if (b < 0) continue;
                    bins.Cursors[b] = writePtr[k] - bins.Offsets[b];
                }
            }

            int dc = 0;
            for (int i = 0; i < binCount; ++i)
            {
                int b = sorted[i];
                var key = bins.Keys.ElementAt(b);
                int n = bins.Counts[b];
                int baseOff = bins.Offsets[b];

                int left = n, local = 0;
                while (left > 0)
                {
                    int take = math.min(EntitiesGraphicsTuningConstants.kMaxInstancesPerDrawCommand, left);
                    out0.drawCommands[dc] = new BatchDrawCommand
                    {
                        visibleOffset = (uint)(baseOff + local),
                        visibleCount = (uint)take,
                        batchID = key.BatchID,
                        materialID = key.MaterialID,
                        meshID = key.MeshID,
                        submeshIndex = (ushort)key.SubMeshIndex,
                        splitVisibilityMask = key.SplitMask,
                        flags = key.Flags,
                        sortingPosition = key.SortingOrder
                    };

                    var fs = m_FilterSettings.TryGetValue(key.FilterIndex, out var f) ? f : MakeFilterSettings(RenderFilterSettings.Default);
                    fs.allDepthSorted = false;
                    out0.drawRanges[dc] = new BatchDrawRange
                    {
                        filterSettings = fs,
                        drawCommandsBegin = (uint)dc,
                        drawCommandsCount = 1
                    };
                    dc++; local += take; left -= take;
                }
            }

            output.drawCommands[0] = out0;
            sorted.Dispose();
            bins.Dispose();
            chunks.Dispose();
            chunkVisible.Dispose();

            Profiler.EndSample();
            return m_CullingJobDependency;
        }

        private unsafe struct KeyIndexComparer : IComparer<int>
        {
            public DrawCommandSettings* Keys;
            public int Compare(int x, int y) => Keys[x].CompareTo(Keys[y]);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsChunkVisibleAABB(ChunkWorldRenderBounds cwrb, in NativeArray<UnityEngine.Plane> planes)
        {
            float3 center = cwrb.Value.Center;
            float3 extents = cwrb.Value.Extents;
            for (int i = 0; i < planes.Length; ++i)
            {
                var p = planes[i];
                float3 n = p.normal;
                float d = p.distance;
                float dist = math.dot(n, center) + d;
                float r = math.dot(extents, math.abs(n));
                if (dist + r < 0f) return false;
            }
            return true;
        }

        private static void ZeroOut(ref BatchCullingOutput o)
        {
            var d = o.drawCommands[0];
            d.visibleInstanceCount = 0; d.visibleInstances = null;
            d.instanceSortingPositionFloatCount = 0; d.instanceSortingPositions = null;
            d.drawCommandCount = 0; d.drawCommands = null; d.drawCommandPickingInstanceIDs = null;
            d.drawRangeCount = 0; d.drawRanges = null;
            o.drawCommands[0] = d;
        }
    }
}
#endif
