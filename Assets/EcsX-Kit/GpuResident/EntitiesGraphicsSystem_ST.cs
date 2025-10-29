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

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
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

        private struct ChunkKEntry
        {
            public ushort K;
            public ushort Count;
            public ushort SubMesh;
            public int Bin;
        }
        private struct ChunkEntriesIndex
        {
            public int Start;
            public int Length;
            public int SingleBin;
        }

        private JobHandle OnPerformCulling(BatchRendererGroup rg, BatchCullingContext ctx, BatchCullingOutput output, IntPtr userCtx)
        {
            Profiler.BeginSample("OnPerformCulling_ST");

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

            var entries = new UnsafeList<ChunkKEntry>(math.max(256, chunkCount * 4),
                m_ThreadLocalAllocators.GeneralAllocator->Handle, NativeArrayOptions.UninitializedMemory);
            var chunkEntries = new NativeArray<ChunkEntriesIndex>(chunks.Length, Allocator.Temp, NativeArrayOptions.ClearMemory);

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

                int filterIndexLM = ch.GetSharedComponentIndex(hFilter);

                chunkVisible[c] = 1;

                int filterIndex = filterIndexLM;
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
                    chunkEntries[c] = new ChunkEntriesIndex { Start = entries.Length, Length = 0, SingleBin = b0 };
                    continue;
                }

                int count = materialsCount * meshesCount;
                int* counts = stackalloc int[count];
                ushort* submesh = stackalloc ushort[count];
                UnsafeUtility.MemClear(counts, count * sizeof(int));
                UnsafeUtility.MemSet(submesh, 0xFF, count * sizeof(ushort));

                for (int i = 0; i < len; ++i)
                {
                    var mmi = mmis[i];
                    int mi = mmi.MaterialArrayIndex;
                    int xi = mmi.MeshArrayIndex;
                    if ((uint)mi >= (uint)materialsCount || (uint)xi >= (uint)meshesCount) continue;
                    int k = mi * meshesCount + xi;
                    if (++counts[k] == 1) submesh[k] = (ushort)mmi.SubMesh;
                }

                int start = entries.Length;
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

                    entries.Add(new ChunkKEntry
                    {
                        K = (ushort)k,
                        Count = (ushort)n,
                        SubMesh = submesh[k],
                        Bin = b
                    });
                }

                int length = entries.Length - start;
                chunkEntries[c] = new ChunkEntriesIndex { Start = start, Length = length, SingleBin = -1 };
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
            out0.instanceSortingPositionFloatCount = 0; 
            out0.instanceSortingPositions = null;
            out0.drawCommandCount = commandsNeeded;
            out0.drawCommands = commandsNeeded > 0 ? ChunkDrawCommandOutput.Malloc<BatchDrawCommand>(commandsNeeded) : null;
            out0.drawCommandPickingInstanceIDs = null;

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
                var mmis = ch.GetNativeArray(ref hMMI);
                int len = mmis.Length;
                if (len == 0) continue;

                int chunkStart = info.CullingData.ChunkOffsetInBatch;

                BRGRenderMeshArray brg = default;
                int rmaIndex = ch.GetSharedComponentIndex(hRMA);
                if (rmaIndex < 0 || brgMap.IsEmpty || !brgMap.TryGetValue(rmaIndex, out brg)) continue;
                int materialsCount = brg.UniqueMaterials.IsCreated ? brg.UniqueMaterials.Length : 0;
                int meshesCount = brg.UniqueMeshes.IsCreated ? brg.UniqueMeshes.Length : 0;
                if (materialsCount <= 0 || meshesCount <= 0) continue;

                var ce = chunkEntries[c];
                if (ce.SingleBin >= 0)
                {
                    int singleBin = ce.SingleBin;
                    int dst = bins.Offsets[singleBin] + bins.Cursors[singleBin];
                    int src = chunkStart;
                    int l = len;
                    
                    int l8 = l & ~7;
                    int idx = 0;
                    for (; idx < l8; idx += 8)
                    {
                        vi[dst] = src; vi[dst+1] = src+1; vi[dst+2] = src+2; vi[dst+3] = src+3;
                        vi[dst+4] = src+4; vi[dst+5] = src+5; vi[dst+6] = src+6; vi[dst+7] = src+7;
                        dst += 8; src += 8;
                    }
                    for (; idx < l; ++idx) vi[dst++] = src++;
                    bins.Cursors[singleBin] = dst - bins.Offsets[singleBin];
                    continue;
                }

                var slice = chunkEntries[c];
                if (slice.Length <= 0) continue;
                var ePtr = (ChunkKEntry*)entries.Ptr + slice.Start;

                int count = materialsCount * meshesCount;
                int* k2idx = stackalloc int[count];
                UnsafeUtility.MemSet(k2idx, 0xFF, count * sizeof(int));

                int* writePtr = stackalloc int[slice.Length];
                for (int i = 0; i < slice.Length; ++i)
                {
                    k2idx[ePtr[i].K] = i;
                    writePtr[i] = bins.Offsets[ePtr[i].Bin] + bins.Cursors[ePtr[i].Bin];
                }

                for (int i = 0; i < len; ++i)
                {
                    var mmi = mmis[i];
                    int mi = mmi.MaterialArrayIndex, xi = mmi.MeshArrayIndex;
                    if ((uint)mi >= (uint)materialsCount || (uint)xi >= (uint)meshesCount) continue;
                    int k = mi * meshesCount + xi;
                    int idx = k2idx[k];
                    if (idx < 0) continue;
                    int p = writePtr[idx]++;
                    vi[p] = chunkStart + i;
                }

                for (int i = 0; i < slice.Length; ++i)
                {
                    int b = ePtr[i].Bin;
                    bins.Cursors[b] = writePtr[i] - bins.Offsets[b];
                }
            }

            const int MaxInstPerRange = EntitiesGraphicsTuningConstants.kMaxInstancesPerDrawRange;
            const int MaxCmdsPerRange = EntitiesGraphicsTuningConstants.kMaxDrawCommandsPerDrawRange;

            var tmpRanges = new UnsafeList<BatchDrawRange>(
                math.max(32, (bins.Keys.Length + 1) / 2),
                m_ThreadLocalAllocators.GeneralAllocator->Handle,
                NativeArrayOptions.UninitializedMemory);

            int curFilter = -1;
            int curBegin = 0;
            int curCount = 0;
            int curInsts = 0;
            bool curAllDepthSorted = true;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            void FlushRange()
            {
                if (curCount <= 0) return;
                var fs = m_FilterSettings.TryGetValue(curFilter, out var f) ? f : MakeFilterSettings(RenderFilterSettings.Default);
                fs.allDepthSorted = curAllDepthSorted;
                tmpRanges.Add(new BatchDrawRange
                {
                    filterSettings = fs,
                    drawCommandsBegin = (uint)curBegin,
                    drawCommandsCount = (uint)curCount
                });
                curFilter = -1; curBegin = 0; curCount = 0; curInsts = 0; curAllDepthSorted = true;
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

                    int filterIdx = key.FilterIndex;
                    bool hasPos = (out0.drawCommands[dc].flags & BatchDrawCommandFlags.HasSortingPosition) != 0;

                    bool startNew =
                        (curCount == 0) ||
                        (filterIdx != curFilter) ||
                        (curInsts + take > MaxInstPerRange) ||
                        (curCount + 1 > MaxCmdsPerRange);

                    if (startNew)
                    {
                        FlushRange();
                        curFilter = filterIdx;
                        curBegin = dc;
                        curCount = 1;
                        curInsts = take;
                        curAllDepthSorted = hasPos;
                    }
                    else
                    {
                        curCount++;
                        curInsts += take;
                        curAllDepthSorted &= hasPos;
                    }

                    ++dc;
                    local += take;
                    left -= take;
                }
            }
            FlushRange();

            out0.drawRangeCount = tmpRanges.Length;
            out0.drawRanges = (out0.drawRangeCount > 0)
                ? ChunkDrawCommandOutput.Malloc<BatchDrawRange>(out0.drawRangeCount)
                : null;
            if (out0.drawRangeCount > 0)
                UnsafeUtility.MemCpy(out0.drawRanges, tmpRanges.Ptr, out0.drawRangeCount * sizeof(BatchDrawRange));

            output.drawCommands[0] = out0;

            tmpRanges.Dispose();
            sorted.Dispose();
            bins.Dispose();
            chunks.Dispose();
            chunkVisible.Dispose();
            chunkEntries.Dispose();
            entries.Dispose();

            Profiler.EndSample();
            return default;
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
            d.drawCommandCount = 0; d.drawCommands = null;
            d.drawCommandPickingInstanceIDs = null;
            d.drawRangeCount = 0; d.drawRanges = null;
            o.drawCommands[0] = d;
        }
    }
}
#endif
