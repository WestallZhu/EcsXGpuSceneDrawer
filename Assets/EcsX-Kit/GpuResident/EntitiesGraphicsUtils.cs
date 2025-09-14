using System;
using System.Runtime.CompilerServices;
using Unity.Assertions;
using Unity.Burst.Intrinsics;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Transforms;
using UnityEngine;
using UnityEngine.Rendering;

#if UNITY_2022_2_OR_NEWER

[assembly: InternalsVisibleTo("Unity.Entities.Graphics.Tests")]
namespace Unity.Rendering
{
    internal static class EntitiesGraphicsUtils
    {
        public static EntityQueryDesc GetEntitiesGraphicsRenderedQueryDesc()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<MaterialMeshInfo>(),
                    ComponentType.ChunkComponent<EntitiesGraphicsChunkInfo>(),
                },
            };
        }

        public static EntityQueryDesc GetEntitiesGraphicsRenderedQueryDescReadOnly()
        {
            return new EntityQueryDesc
            {
                All = new[]
                {
                    ComponentType.ChunkComponentReadOnly<ChunkWorldRenderBounds>(),
                    ComponentType.ReadOnly<WorldRenderBounds>(),
                    ComponentType.ReadOnly<LocalToWorld>(),
                    ComponentType.ReadOnly<MaterialMeshInfo>(),
                    ComponentType.ChunkComponentReadOnly<EntitiesGraphicsChunkInfo>(),
                },
            };
        }

        private static bool CheckGLVersion()
        {

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.OpenGLES3)
                return SystemInfo.supportsComputeShaders;

            char[] delimiterChars = { ' ', '.' };
            var arr = SystemInfo.graphicsDeviceVersion.Split(delimiterChars);
            if (arr.Length >= 3)
            {
                var major = Int32.Parse(arr[1]);
                var minor = Int32.Parse(arr[2]);

                return major >= 4 && minor >= 3;
            }

            return false;
        }

        static bool IsScriptableRenderPipelineUsed() => GraphicsSettings.currentRenderPipeline != null;

        public static bool IsEntitiesGraphicsSupportedOnSystem()
        {
            if (!IsScriptableRenderPipelineUsed())
                return false;

            var deviceType = SystemInfo.graphicsDeviceType;

            bool isOpenGL = deviceType == GraphicsDeviceType.OpenGLCore ||
                            deviceType == GraphicsDeviceType.OpenGLES3;

            if (deviceType == GraphicsDeviceType.Null ||
                !SystemInfo.supportsComputeShaders ||
                (isOpenGL && !CheckGLVersion()))
                return false;

            return true;
        }

        public static bool UseHybridConstantBufferMode() =>
            BatchRendererGroup.BufferTarget == BatchBufferTarget.ConstantBuffer;

        private const int kMaxCbufferSize = 64 * 1024;
        public static readonly int BatchAllocationAlignment = math.max(16, SystemInfo.constantBufferOffsetAlignment);
        public static readonly int MaxBytesPerCBuffer = math.min(kMaxCbufferSize, SystemInfo.maxConstantBufferSize);

        static Material LoadMaterialWithHideAndDontSave(string name)
        {
            Shader shader = Shader.Find(name);

            if (shader == null)
            {
                Debug.LogError($"Shader \'{name}\' not found.");
                return null;
            }

            Material material = new Material(shader);

            material.hideFlags = HideFlags.HideAndDontSave;

            return material;
        }

        public static Material LoadErrorMaterial()
        {
#if HDRP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/HDRP/MaterialError");
#elif URP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/Universal Render Pipeline/FallbackError");
#else

            return null;
#endif
        }

        public static Material LoadLoadingMaterial()
        {
#if HDRP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/HDRP/MaterialLoading");
#elif URP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/Universal Render Pipeline/FallbackLoading");
#else

            return null;
#endif
        }

        public static Material LoadPickingMaterial()
        {
#if HDRP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/HDRP/BRGPicking");
#elif URP_10_0_0_OR_NEWER
            return LoadMaterialWithHideAndDontSave("Hidden/Universal Render Pipeline/BRGPicking");
#else

            return null;
#endif
        }

        public static v128 ComputeBitmask(int entityCount)
        {
            Assert.IsTrue(entityCount<=128);
            return EnabledBitUtility.ShiftRight(new v128(ulong.MaxValue), 128 - entityCount);
        }
    }

    internal struct AtomicHelpers
    {
        public const uint kNumBitsInLong = sizeof(long) * 8;

        public static void IndexToQwIndexAndMask(int index, out int qwIndex, out long mask)
        {
            uint i = (uint)index;
            uint qw = i / kNumBitsInLong;
            uint shift = i % kNumBitsInLong;

            qwIndex = (int)qw;
            mask = 1L << (int)shift;
        }

        public static unsafe void AtomicAnd(long* qwords, int index, long value)
        {
#if UNITY_BURST_EXPERIMENTAL_ATOMIC_INTRINSICS
            Burst.Intrinsics.Common.InterlockedAnd(ref qwords[index], value);
#else

            long currentValue = System.Threading.Interlocked.Read(ref qwords[index]);
            for (;;)
            {

                if ((currentValue & value) == currentValue)
                    return;

                long newValue = currentValue & value;
                long prevValue =
                    System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);

                if (prevValue == currentValue)
                    return;

                currentValue = prevValue;
            }
#endif
        }

        public static unsafe void AtomicOr(long* qwords, int index, long value)
        {
#if UNITY_BURST_EXPERIMENTAL_ATOMIC_INTRINSICS
            Unity.Burst.Intrinsics.Common.InterlockedOr(ref qwords[index], value);
#else

            long currentValue = System.Threading.Interlocked.Read(ref qwords[index]);
            for (;;)
            {

                if ((currentValue | value) == currentValue)
                    return;

                long newValue = currentValue | value;
                long prevValue =
                    System.Threading.Interlocked.CompareExchange(ref qwords[index], newValue, currentValue);

                if (prevValue == currentValue)
                    return;

                currentValue = prevValue;
            }
#endif
        }

        public static unsafe float AtomicMin(float* floats, int index, float value)
        {
            float currentValue = floats[index];

            if (float.IsNaN(value))
                return currentValue;

            int* floatsAsInts = (int*) floats;
            int valueAsInt = math.asint(value);

            for (;;)
            {

                if (currentValue <= value)
                    return currentValue;

                int currentValueAsInt = math.asint(currentValue);

                int newValue = valueAsInt;
                int prevValue = System.Threading.Interlocked.CompareExchange(ref floatsAsInts[index], newValue, currentValueAsInt);
                float prevValueAsFloat = math.asfloat(prevValue);

                if (prevValue == currentValueAsInt)
                    return prevValueAsFloat;

                currentValue = prevValueAsFloat;
            }
        }

        public static unsafe float AtomicMax(float* floats, int index, float value)
        {
            float currentValue = floats[index];

            if (float.IsNaN(value))
                return currentValue;

            int* floatsAsInts = (int*) floats;
            int valueAsInt = math.asint(value);

            for (;;)
            {

                if (currentValue >= value)
                    return currentValue;

                int currentValueAsInt = math.asint(currentValue);

                int newValue = valueAsInt;
                int prevValue = System.Threading.Interlocked.CompareExchange(ref floatsAsInts[index], newValue, currentValueAsInt);
                float prevValueAsFloat = math.asfloat(prevValue);

                if (prevValue == currentValueAsInt)
                    return prevValueAsFloat;

                currentValue = prevValueAsFloat;
            }
        }
    }
}

#endif