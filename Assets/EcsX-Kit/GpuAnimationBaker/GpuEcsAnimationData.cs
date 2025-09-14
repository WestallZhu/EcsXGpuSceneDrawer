using System;
using UnityEngine;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Collections;

namespace GPUAnimationBaker.Engine
{

    public enum GpuAnimatorControlStates
    {

        Start,

        Stop,

        KeepCurrentState
    }

    public struct GpuAnimatorControl : IComponentData
    {

        public int animationID;

        public float blendFactor;

        public float speedFactor;

        public int attackID;

        public float attackLoopInterval;

        public float interval;

        public bool useAttackLoopInterval;
    }

    public struct GpuAnimatorStateComponent : IComponentData
    {

        public float currentNormalizedTime;

        public float previousNormalizedTime;

        public bool stoppedCurrent;

        public bool stoppedPrevious;
    }

    [MaterialProperty("_AnimationState")]
    public struct GpuAnimatorState : IComponentData
    {

        public float4 Value;
    }

    public struct GpuAnimatorPlayComponent
    {

        public float startPlayTime;

        public uint startFrameIndex;

        public uint nbrOfFramesPerSample;

        public uint intervalNbrOfFrames;

        public uint nextStartFrameIndex;

        public uint nextNbrOfFramesPerSample;

        public byte loop;

        public GpuAnimatorState GetEncodeState()
        {
            GpuAnimatorState animatorState = new GpuAnimatorState();
            animatorState.Value.x = startPlayTime;
            uint packed0 = (uint)startFrameIndex | ((uint)nbrOfFramesPerSample << 10) | ((uint)intervalNbrOfFrames << 20) | ((uint)loop << 30);
            animatorState.Value.y = UnsafeUtility.As<uint, float>(ref packed0);
            uint packed1 = (uint)nextStartFrameIndex | ((uint)nextNbrOfFramesPerSample << 10);
            animatorState.Value.z = UnsafeUtility.As<uint, float>(ref packed1);
            return animatorState;
        }
    }

    [MaterialProperty("_SpeedFactor")]
    public struct GpuAnimatorSpeedFactor : IComponentData
    {

        public float value;
    }

    public class GlobalConstants
    {

        public const float SampleFrameRate = 30f;
    }

    public class AnimationMatricesTexture
    {

        public SkinnedMeshRenderer skinnedMeshRenderer;

        public Texture2D texture;
    }

    [Serializable]
    public struct GpuAnimationData
    {
        public int startFrameIndex;
        public int nbrOfFramesPerSample;
        public int nbrOfInBetweenSamples;
        public bool loop;
        public int nextStateIndex;
    }


    public struct IndexAndCount : IComponentData
    {

        public int index;

        public int count;
    }

    public unsafe class GpuAnimationDataArray
    {

        const int Max = 64;

        public NativeList<GpuAnimationData> animDataList;

        NativeArray<int> freeCountAtIndex;

        int freeCount;

        int allocatedCount;

        public NativeParallelHashMap<int, IndexAndCount> rangeHashMap;

        public GpuAnimationDataArray()
        {
            animDataList = new NativeList<GpuAnimationData>(128, Allocator.Persistent);
            rangeHashMap = new NativeParallelHashMap<int, IndexAndCount>(128, Allocator.Persistent);
            freeCountAtIndex = new NativeArray<int>(Max, Allocator.Persistent);

            freeCount = 0;
            allocatedCount = 0;
            for (int i = 0; i < Max; i++)
            {
                freeCountAtIndex[i] = -1;
            }

        }

        public bool GetElement(int hash, out IndexAndCount indexAndCount)
        {
            bool ret = rangeHashMap.TryGetValue(hash, out indexAndCount);
            if (!ret)
            {
                indexAndCount.count = 0;
                indexAndCount.index = -1;
            }
            return ret;
        }

        public IndexAndCount GetOrCreateElement(int hash, GpuAnimationData[] shareGpuAnimationData)
        {

            if (!GetElement(hash, out var indexAndCount))
            {
                indexAndCount = AllocateElement(shareGpuAnimationData.Length);
                rangeHashMap.TryAdd(hash, indexAndCount);
            }

            for (int i = 0; i < indexAndCount.count; i++)
            {
                animDataList[i + indexAndCount.index] = shareGpuAnimationData[i];
            }

            return indexAndCount;
        }

        public IndexAndCount AllocateElement(int count)
        {
            int index = -1;
            if (freeCountAtIndex[count] >= 0)
            {
                index = freeCountAtIndex[count];
                freeCountAtIndex[count] = animDataList[index].nextStateIndex;
                freeCount -= count;
            }
            else
            {
                index = animDataList.Length;
                animDataList.ResizeUninitialized(index + count);
            }
            allocatedCount += count;

            return new IndexAndCount { index = index, count = count };
        }

        public void FreeNode(ref IndexAndCount renderer)
        {
            int index = renderer.index;
            int count = renderer.count;

            if (index < 0 || count <= 0)
            {
                return;
            }

            allocatedCount -= count;
            freeCount += count;

            GpuAnimationData animData = animDataList[index];
            animData.nextStateIndex = freeCountAtIndex[count];
            animDataList[index] = animData;
            freeCountAtIndex[count] = index;

            renderer.count = 0;
            renderer.index = -1;

        }

        public void Dispose()
        {
            if (animDataList.IsCreated) animDataList.Dispose();
            if (rangeHashMap.IsCreated) rangeHashMap.Dispose();
            if (freeCountAtIndex.IsCreated) freeCountAtIndex.Dispose();
        }

    }

    public class GpuAnimatorMonoAsset : ScriptableObject
    {
        public int totalNbrOfFrames;
        public GpuAnimationData[] animations;
        public string[] stateNames;
    }
}