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

        // Windowing (ConstantBuffer or Texture page)
        private bool m_UseConstantBuffer;
        private int  m_WindowSizeInFloat4;

        // Texture path
        private NativeArray<IntPtr> m_TextureBuffer;
        private int m_TextureWidth;
        private int m_TextureHeight;

        // Whether to enforce window boundary (true for CB/Textures; false for UAV buffer)
        private bool m_EnforceWindowBoundary;

        private const int kBatchUploadThreshold = 8;
        private const int kMaxMergeDistance     = 64;
        private const int kBytesPerTexel        = 16; // float4

        public bool UseConstantBuffer  => m_UseConstantBuffer;
        public int  WindowSizeInFloat4 => m_WindowSizeInFloat4;

        public DirectUploader(
            GraphicsBuffer gpuBuffer,
            NativeArray<float4> systemBuffer,
            long totalBufferBytes,
            NativeArray<IntPtr> textureBuffer,
            int texPageWidth = 0,
            int texPageHeight = 0)
        {
            m_GPUBuffer            = gpuBuffer;
            m_SystemBuffer         = systemBuffer;
            m_PendingUploads       = new NativeList<UploadRequest>(128, Allocator.Persistent);

            m_TextureBuffer        = textureBuffer;
            m_TextureWidth         = texPageWidth;
            m_TextureHeight        = texPageHeight;

            m_UseConstantBuffer    = BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

            bool hasTextures       = textureBuffer.IsCreated && textureBuffer.Length > 0;
            if (hasTextures)
            {
                // One window == one texture page
                m_WindowSizeInFloat4    = texPageWidth * texPageHeight;
                m_EnforceWindowBoundary = true;
            }
            else if (m_UseConstantBuffer)
            {
                int windowSizeInBytes   = BatchRendererGroup.GetConstantBufferMaxWindowSize();
                m_WindowSizeInFloat4    = windowSizeInBytes / 16;
                m_EnforceWindowBoundary = true;
            }
            else
            {
                // UAV buffer: whole buffer is one big "window"
                m_WindowSizeInFloat4    = (int)(totalBufferBytes / 16);
                m_EnforceWindowBoundary = false;
            }
        }

        public NativeList<UploadRequest>.ParallelWriter AsParallelWriter() => m_PendingUploads.AsParallelWriter();
        public void EnsureCapacity(int capacity) => m_PendingUploads.SetCapacity(capacity);


        public void QueueUpload(int sourceOffsetInFloat4, int destOffsetInFloat4, int sizeInFloat4)
        {
            if (sizeInFloat4 <= 0) return;

#if UNITY_COLLECTIONS_CHECKS || DEVELOPMENT_BUILD
            if (sourceOffsetInFloat4 != destOffsetInFloat4)
                Debug.LogWarning("DirectUploader assumes Src==Dst; forcing SourceOffset=DestinationOffset.");
#endif

            if (m_EnforceWindowBoundary)
                QueueUploadWithWindowSplit_DstOnly(destOffsetInFloat4, sizeInFloat4);
            else
                m_PendingUploads.Add(new UploadRequest
                {
                    SourceOffset      = destOffsetInFloat4,   // Src == Dst
                    DestinationOffset = destOffsetInFloat4,
                    SizeInFloat4      = sizeInFloat4
                });
        }

        private void QueueUploadWithWindowSplit_DstOnly(int destOffset, int size)
        {
            while (size > 0)
            {
                int offsetInWindow    = destOffset % m_WindowSizeInFloat4;
                int remainingInWindow = m_WindowSizeInFloat4 - offsetInWindow;
                int chunk             = math.min(size, remainingInWindow);

                m_PendingUploads.Add(new UploadRequest
                {
                    SourceOffset      = destOffset, // Src == Dst
                    DestinationOffset = destOffset,
                    SizeInFloat4      = chunk
                });

                destOffset += chunk;
                size       -= chunk;
            }
        }

        public void ExecuteUploads()
        {
            if (m_PendingUploads.Length == 0) return;

            // Sort by destination so merges are correct
            m_PendingUploads.Sort();

            bool hasTextures = m_TextureBuffer.IsCreated && m_TextureBuffer.Length > 0;
            if (hasTextures)
            {
                ExecuteTextureUploadsMerged();
            }
            else
            {
                if (m_GPUBuffer == null) return;

                if (m_PendingUploads.Length >= kBatchUploadThreshold)
                    ExecuteOptimizedBatch();
                else
                    ExecuteSimpleBatch();
            }

            m_PendingUploads.Clear();
        }

        // ========= UAV / GraphicsBuffer path =========
        private void ExecuteOptimizedBatch()
        {
            var merged = MergeByGapWithinWindow(checkWindow: m_EnforceWindowBoundary);
            for (int i = 0; i < merged.Length; ++i)
            {
                var u = merged[i];
                // Mirror model: copy [dst, dst+len) from system buffer into GPU buffer
                m_GPUBuffer.SetData(m_SystemBuffer, u.DestinationOffset, u.DestinationOffset, u.SizeInFloat4);
            }
            merged.Dispose();
        }

        private void ExecuteSimpleBatch()
        {
            for (int i = 0; i < m_PendingUploads.Length; ++i)
            {
                var u = m_PendingUploads[i];
                m_GPUBuffer.SetData(m_SystemBuffer, u.DestinationOffset, u.DestinationOffset, u.SizeInFloat4);
            }
        }

        private NativeList<UploadRequest> MergeByGapWithinWindow(bool checkWindow)
        {
            var merged = new NativeList<UploadRequest>(m_PendingUploads.Length, Allocator.Temp);
            if (m_PendingUploads.Length == 0) return merged;

            var run = m_PendingUploads[0];
            int runEnd = run.DestinationOffset + run.SizeInFloat4;

            for (int i = 1; i < m_PendingUploads.Length; ++i)
            {
                var next = m_PendingUploads[i];
                int nextEnd = next.DestinationOffset + next.SizeInFloat4;

                bool sameWindow = !checkWindow ||
                    (run.DestinationOffset / m_WindowSizeInFloat4) == ((nextEnd - 1) / m_WindowSizeInFloat4);

                int gap = next.DestinationOffset - runEnd;  // <0 overlap, 0 adjacent, >0 hole
                if (sameWindow && gap <= kMaxMergeDistance)
                {
                    runEnd = math.max(runEnd, nextEnd);
                    run.SizeInFloat4 = runEnd - run.DestinationOffset;
                }
                else
                {
                    merged.Add(run);
                    run = next;
                    runEnd = nextEnd;
                }
            }

            merged.Add(run);
            return merged;
        }

        private unsafe void ExecuteTextureUploadsMerged()
        {
            var merged = MergeByGapWithinWindow(checkWindow: true);


            byte* basePtr = (byte*)NativeArrayUnsafeUtility.GetUnsafePtr(m_SystemBuffer);
            int   winSize = m_WindowSizeInFloat4;

            for (int i = 0; i < merged.Length; ++i)
            {
                int dst  = merged[i].DestinationOffset;
                int left = merged[i].SizeInFloat4;

                while (left > 0)
                {
                    int winIdx       = dst / winSize;
                    int local        = dst % winSize;
                    int canThisWin   = math.min(left, winSize - local);

                    // Expand to full rows in this window
                    int startRow         =  local / m_TextureWidth;
                    int endElemExclusive =  local + canThisWin;
                    int endRowExclusive  = (endElemExclusive + m_TextureWidth - 1) / m_TextureWidth; // ceildiv
                    int rows             =  endRowExclusive - startRow;

                    // Pointer to the first pixel of startRow (xoffset==0)
                    int rowBaseFloat4    = winIdx * winSize + startRow * m_TextureWidth;
                    IntPtr texPtr        = m_TextureBuffer[winIdx];
                    IntPtr dataPtr       = (IntPtr)(basePtr + (long)rowBaseFloat4 * kBytesPerTexel);

#if UNITY_COLLECTIONS_CHECKS || DEVELOPMENT_BUILD
                    if (winIdx < 0 || winIdx >= m_TextureBuffer.Length)
                        Debug.LogError($"Texture index {winIdx} out of range.");
                    if (startRow < 0 || rows <= 0 || (startRow + rows) > m_TextureHeight)
                        Debug.LogError("Row range out of texture bounds.");
                    if ((rowBaseFloat4 + rows * m_TextureWidth) > m_SystemBuffer.Length)
                        Debug.LogError("SystemBuffer out of range for texture upload.");
#endif
                    // xoffset = 0; width = texture width; height = row count
                    RenderingPluginAPI.UpdateTexture2DSub(
                        texPtr,
                        0,
                        startRow,
                        m_TextureWidth,
                        rows,
                        kBytesPerTexel,
                        dataPtr);

                    dst  += canThisWin;
                    left -= canThisWin;
                }
            }

            merged.Dispose();
        }

        public void Dispose()
        {
            if (m_PendingUploads.IsCreated)
                m_PendingUploads.Dispose();
        }
    }
}
#endif