using System;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Burst;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;
#if UNITY_2022_2_OR_NEWER

namespace Unity.Rendering
{
    internal enum OperationType : int
    {
        Upload = 0,
        Matrix_4x4 = 1,
        Matrix_Inverse_4x4 = 2,
        Matrix_3x4 = 3,
        Matrix_Inverse_3x4 = 4,
        StridedUpload = 5,
    }

    public enum MatrixType
    {

        MatrixType4x4,

        MatrixType3x4,
    }

    internal unsafe struct GpuUploadOperation
    {
        public enum UploadOperationKind
        {
            Memcpy,
            SOAMatrixUpload3x4,
            SOAMatrixUpload4x4,

        }

        public UploadOperationKind Kind;

        public MatrixType SrcMatrixType;

        public void* Src;

        public int DstOffset;

        public int DstOffsetInverse;

        public int Size;

        public int BytesRequiredInUploadBuffer => (Kind == UploadOperationKind.Memcpy)
            ? Size
            : (Size * UnsafeUtility.SizeOf<float3x4>());
    }

    internal struct ValueBlitDescriptor
    {
        public float4x4 Value;
        public uint DestinationOffset;
        public uint ValueSizeBytes;
        public uint Count;

        public int BytesRequiredInUploadBuffer => (int)(ValueSizeBytes * Count);
    }

    [BurstCompile]
    internal unsafe struct DirectUploader : IDisposable
    {
        public struct UploadRequest : IComparable<UploadRequest>
        {
            public int SourceOffset;
            public int DestinationOffset;
            public int SizeInFloat4;

            public int CompareTo(UploadRequest other) => DestinationOffset.CompareTo(other.DestinationOffset);
        }

        private GraphicsBuffer m_GPUBuffer;
        private NativeArray<float4> m_SystemBuffer;
        private NativeList<UploadRequest> m_PendingUploads;

        private bool m_UseConstantBuffer;
        private int m_WindowSizeInFloat4;

        private const int kBatchUploadThreshold = 8;
        private const int kMaxMergeDistance = 64;

        public bool UseConstantBuffer => m_UseConstantBuffer;
        public int WindowSizeInFloat4 => m_WindowSizeInFloat4;

        public DirectUploader(GraphicsBuffer gpuBuffer, NativeArray<float4> systemBuffer, long totalBufferSize)
        {
            m_GPUBuffer = gpuBuffer;
            m_SystemBuffer = systemBuffer;
            m_PendingUploads = new NativeList<UploadRequest>(128, Allocator.Persistent);

            m_UseConstantBuffer = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

            if (m_UseConstantBuffer)
            {
                int windowSizeInBytes = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                m_WindowSizeInFloat4 = windowSizeInBytes / 16;
#if DEBUG_LOG_UPLOADS
                Debug.Log($"ConstantBuffer mode: WindowSize={windowSizeInBytes} bytes ({m_WindowSizeInFloat4} float4s)");
#endif
            }
            else
            {
                m_WindowSizeInFloat4 = (int)(totalBufferSize / 16);
            }
        }

        public NativeList<UploadRequest>.ParallelWriter AsParallelWriter() => m_PendingUploads.AsParallelWriter();

        public void EnsureCapacity(int capacity)
        {
            m_PendingUploads.SetCapacity(capacity);
        }

        public void QueueUpload(int sourceOffsetInFloat4, int destOffsetInFloat4, int sizeInFloat4)
        {
            if (sizeInFloat4 <= 0) return;

            if (m_UseConstantBuffer)
            {

                QueueUploadWithWindowSplit(sourceOffsetInFloat4, destOffsetInFloat4, sizeInFloat4);
            }
            else
            {

                m_PendingUploads.Add(new UploadRequest
                {
                    SourceOffset = sourceOffsetInFloat4,
                    DestinationOffset = destOffsetInFloat4,
                    SizeInFloat4 = sizeInFloat4
                });
            }
        }

        private void QueueUploadWithWindowSplit(int sourceOffset, int destOffset, int size)
        {
            while (size > 0)
            {
                int offsetInWindow = destOffset % m_WindowSizeInFloat4;
                int remainingInWindow = m_WindowSizeInFloat4 - offsetInWindow;
                int chunkSize = math.min(size, remainingInWindow);

                m_PendingUploads.Add(new UploadRequest
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

        public void ExecuteUploads()
        {
            if (m_PendingUploads.Length == 0) return;

            m_PendingUploads.Sort();

            if (m_PendingUploads.Length >= kBatchUploadThreshold)
            {
                ExecuteOptimizedBatch();
            }
            else
            {
                ExecuteSimpleBatch();
            }

            m_PendingUploads.Clear();
        }

        private void ExecuteOptimizedBatch()
        {
            var mergedUploads = MergeConsecutiveUploadsOptimized();

            foreach (var upload in mergedUploads)
            {
                m_GPUBuffer.SetData(
                    m_SystemBuffer,
                    upload.SourceOffset,
                    upload.DestinationOffset,
                    upload.SizeInFloat4);
            }

            mergedUploads.Dispose();
        }

        private void ExecuteSimpleBatch()
        {
            for (int i = 0; i < m_PendingUploads.Length; i++)
            {
                var upload = m_PendingUploads[i];
                m_GPUBuffer.SetData(
                    m_SystemBuffer,
                    upload.SourceOffset,
                    upload.DestinationOffset,
                    upload.SizeInFloat4);
            }
        }

        private NativeList<UploadRequest> MergeConsecutiveUploadsOptimized()
        {
            var merged = new NativeList<UploadRequest>(m_PendingUploads.Length, Allocator.Temp);
            if (m_PendingUploads.Length == 0) return merged;

            var current = m_PendingUploads[0];

            for (int i = 1; i < m_PendingUploads.Length; i++)
            {
                var next = m_PendingUploads[i];

                int gapSize = next.DestinationOffset - (current.DestinationOffset + current.SizeInFloat4);
                bool isConsecutive = gapSize == 0;
                bool isCloseEnough = gapSize > 0 && gapSize <= kMaxMergeDistance;

                bool canMerge = (isConsecutive || isCloseEnough) &&
                               (current.SourceOffset + current.SizeInFloat4 == next.SourceOffset || isCloseEnough);

                if (canMerge && m_UseConstantBuffer)
                {
                    int startWindow = current.DestinationOffset / m_WindowSizeInFloat4;
                    int endWindow = (next.DestinationOffset + next.SizeInFloat4 - 1) / m_WindowSizeInFloat4;
                    canMerge = (startWindow == endWindow);
                }

                if (canMerge)
                {

                    current.SizeInFloat4 = (next.DestinationOffset + next.SizeInFloat4) - current.DestinationOffset;
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        private NativeList<UploadRequest> MergeConsecutiveUploads()
        {
            var merged = new NativeList<UploadRequest>(m_PendingUploads.Length, Allocator.Temp);
            if (m_PendingUploads.Length == 0) return merged;

            var current = m_PendingUploads[0];

            for (int i = 1; i < m_PendingUploads.Length; i++)
            {
                var next = m_PendingUploads[i];

                bool canMerge = (current.DestinationOffset + current.SizeInFloat4 == next.DestinationOffset) &&
                               (current.SourceOffset + current.SizeInFloat4 == next.SourceOffset);

                if (canMerge && m_UseConstantBuffer)
                {
                    int startWindow = current.DestinationOffset / m_WindowSizeInFloat4;
                    int endWindow = (next.DestinationOffset + next.SizeInFloat4 - 1) / m_WindowSizeInFloat4;
                    canMerge = (startWindow == endWindow);
                }

                if (canMerge)
                {
                    current.SizeInFloat4 += next.SizeInFloat4;
                }
                else
                {
                    merged.Add(current);
                    current = next;
                }
            }

            merged.Add(current);
            return merged;
        }

        public void GetStats(out int uploadCount, out int totalFloat4s, out int windowCount, out float avgUploadSize)
        {
            uploadCount = m_PendingUploads.Length;
            totalFloat4s = 0;
            foreach (var upload in m_PendingUploads)
                totalFloat4s += upload.SizeInFloat4;

            windowCount = m_UseConstantBuffer ?
                (int)((long)m_SystemBuffer.Length * 16 + BatchRendererGroup.GetConstantBufferMaxWindowSize() - 1) / BatchRendererGroup.GetConstantBufferMaxWindowSize() : 1;

            avgUploadSize = uploadCount > 0 ? (float)totalFloat4s / uploadCount : 0f;
        }

        public void Dispose()
        {
            if (m_PendingUploads.IsCreated)
                m_PendingUploads.Dispose();
        }
    }
}

#endif