using System;
using System.Collections;
using Unity.Mathematics;
using UnityEngine;
using Unity.Entities;

namespace GPUAnimationBaker.Engine
{

    public class GpuAnimatorBehaviour : MonoBehaviour
    {


        public GpuAnimationData[] animations;

        public string[] stateNames;

        GpuAnimatorStateComponent gpuAnimatorState;

        int attackID;

        GpuAnimatorControl animControl;



        MaterialPropertyBlock propertyBlock;

        MeshRenderer[] renders;


        public GpuAnimatorMonoAsset gpuAniAsset;

        static int AnimationStateID = Shader.PropertyToID("_AnimationState");
        float4 animationStateParam;

        Entity entity = Entity.Null;
        Entity[] renderEntities;
        TransformAccessEntity[] transformAccessEntities;


        private int m_Layer;


        public void SetAnimatorState(int id)
        {
            if (animations == null || id >= animations.Length || id < 0)
            {
                return;
            }

            if (entity != Entity.Null)
            {
                EntityHybridUtility.PlayAnimationState(entity, id);
            }
            else
            {

                if (animControl.animationID != id)
                {
                    gpuAnimatorState.currentNormalizedTime = 0;
                    animControl.animationID = id;
                }

                gpuAnimatorState.stoppedCurrent = false;
            }
        }


        public void SetRenderPropertiesFloat(string propertie, float value)
        {
            if (propertyBlock != null)
            {
                propertyBlock.SetFloat(propertie, value);
            }
        }

        public void SetRenderPropertiesColor(string propertie, Color color)
        {
            if (propertyBlock != null)
            {
                propertyBlock.SetColor(propertie, color);
            }
        }

        public void SetMaterialFloat(int shaderID, double value)
        {
            if (entity != Entity.Null)
            {
                EntityHybridUtility.SetMaterialFloat(entity, shaderID, value);
            }

        }

        public void Awake()
        {
            animControl.animationID = 0;

            animControl.speedFactor = 1;
            animControl.attackLoopInterval = float.MaxValue;
            propertyBlock = new MaterialPropertyBlock();
            renders = gameObject.GetComponentsInChildren<MeshRenderer>();

            var world = World.DefaultGameObjectInjectionWorld;
            if (world != null && EntityHybridUtility.AnimationTexEcs)
            {
                int hashCode = 31 * renders[0].sharedMaterial.GetInstanceID() + GetInstanceID();
                entity = EntityHybridUtility.Instantiate(this.gameObject, hashCode, true);
                EntityHybridUtility.SetEnable(entity, false);
                foreach (var r in renders)
                {
                    r.enabled = false;
                }

            }
            else
            {
                propertyBlock.SetVector(AnimationStateID, animationStateParam);

                foreach (var r in renders)
                {
                    var mats = r.sharedMaterials;
                    r.SetPropertyBlock(propertyBlock);
                }
            }

        }

        private Coroutine updateCoroutine;

        public void SetLayer(int layer)
        {
            if (World.DefaultGameObjectInjectionWorld != null && entity != Entity.Null)
            {
                if (m_Layer == layer)
                    return;
                EntityHybridUtility.SetLayer(entity, layer);
                m_Layer = layer;
            }
            else
            {
                foreach (var r in renders)
                {
                    r.gameObject.layer = layer;
                }
            }
        }
        public void OnEnable()
        {

            if (World.DefaultGameObjectInjectionWorld != null && entity != Entity.Null)
            {
                EntityHybridUtility.SetEnable(entity, true);
            }
            else
            {
                updateCoroutine = StartCoroutine(UpdateCoroutine());
            }
        }

        public void OnDisable()
        {
            StopUpdate();
            if (World.DefaultGameObjectInjectionWorld != null && entity != Entity.Null)
            {
                EntityHybridUtility.SetEnable(entity, false);
            }
        }

        public void OnDestroy()
        {
            if (World.DefaultGameObjectInjectionWorld != null && entity != Entity.Null)
            {
                EntityHybridUtility.DestroyEntity(entity);
            }
        }
        public void StopUpdate()
        {
            if (updateCoroutine != null)
            {
                StopCoroutine(updateCoroutine);
                updateCoroutine = null;
            }
        }

        public void StartUpdate()
        {
            if (updateCoroutine == null)
            {
                updateCoroutine = StartCoroutine(UpdateCoroutine());
            }
        }

        IEnumerator UpdateCoroutine()
        {
            while (true)
            {
                try
                {
                    CUpdate();

                }
                catch (System.Exception e)
                {
                    Debug.LogError($"gpu animator {gameObject} 动画错误. {animControl.animationID} {animations}\n {e.ToString()}  ", gameObject);
                    StopUpdate();

                }
                yield return null;
            }
        }

        void CUpdate()
        {
            GpuAnimationData animationData = animations[animControl.animationID];
            if (gpuAnimatorState.stoppedCurrent && animationData.nextStateIndex >= 0)
            {
                SetAnimatorState(animationData.nextStateIndex);
                animationData = animations[animControl.animationID];

            }
            if (animControl.useAttackLoopInterval && (animControl.animationID == animControl.attackID || animControl.animationID == animations[animControl.attackID].nextStateIndex))
            {
                var deltaTime = Time.deltaTime;
                animControl.interval += deltaTime;
                if (animControl.interval > animControl.attackLoopInterval)
                {
                    SetAnimatorState(animControl.attackID);
                    animationData = animations[animControl.attackID];
                    animControl.interval = 0;
                }
                gpuAnimatorState.stoppedCurrent = false;
            }

            if (!gpuAnimatorState.stoppedCurrent)
            {
                UpdateAnimatorState(ref gpuAnimatorState.currentNormalizedTime, ref gpuAnimatorState.stoppedCurrent,
                      out float primaryBlendFactor, out float primaryTransitionToNextFrame, out int primaryFrameIndex,
                      false, Time.deltaTime);

                int prevPrimaryFrameIndex = (int)animationStateParam.z;
                animationStateParam = new float4(primaryBlendFactor, primaryTransitionToNextFrame, primaryFrameIndex, 0);
                if (prevPrimaryFrameIndex != primaryFrameIndex)
                {
                    propertyBlock.SetVector(AnimationStateID, animationStateParam);
                }

                foreach (var r in renders)
                {
                    r.SetPropertyBlock(propertyBlock);
                }

            }
        }
        private void UpdateAnimatorState(ref float normalizedTime, ref bool stopped , out float primaryBlendFactor, out float primaryTransitionToNextFrame, out int primaryFrameIndex, bool forPrevious, float deltaTime)
        {
            ref GpuAnimationData animationData = ref animations[animControl.animationID];

            {
                UpdateAnimationNormalizedTime(ref normalizedTime, ref stopped, animControl.speedFactor,
                    animationData, out float transitionToNextFrame, out int relativeFrameIndex, forPrevious, deltaTime);

                primaryBlendFactor = 1;
                primaryTransitionToNextFrame = transitionToNextFrame;
                primaryFrameIndex = animationData.startFrameIndex + relativeFrameIndex;

            }

        }

        private void UpdateAnimationNormalizedTime(ref float normalizedTime, ref bool stopped,
            float speedFactor,
            in GpuAnimationData animationData,
            out float transitionToNextFrame, out int relativeFrameIndex, bool forPrevious, float deltaTime)
        {
            int endFrame = animationData.nbrOfFramesPerSample - 1;
            float animationLength = (float)endFrame / GlobalConstants.SampleFrameRate;
            float currentTime = normalizedTime * animationLength;
            if (!stopped) currentTime += deltaTime * speedFactor;
            float normalizedTimeLastUpdate = normalizedTime;
            normalizedTime = currentTime / animationLength;

            if (!forPrevious && (animationData.loop ) && ((!animControl.useAttackLoopInterval || animControl.animationID != animControl.attackID)))
            {
                normalizedTime = normalizedTime % 1f;
            }
            else
            {
                if (normalizedTime >= 1f)
                {
                    normalizedTime = 1f;
                    stopped = true;
                }
            }

            if (normalizedTime == 1f)
            {
                relativeFrameIndex = endFrame - 1;
                transitionToNextFrame = 1f;
            }
            else
            {
                float relativeFrameIndexFloat = normalizedTime * (float)endFrame;
                relativeFrameIndex = (int)math.floor(relativeFrameIndexFloat);
                transitionToNextFrame = relativeFrameIndexFloat - (float)relativeFrameIndex;
            }

        }

        public float speedFactor
        {
            get
            {
                return animControl.speedFactor;
            }
            set
            {
                if (entity != Entity.Null)
                {
                    EntityHybridUtility.SetSpeedFactor(entity, value);
                }
                else
                {
                    animControl.speedFactor = value;
                }
            }
        }
    }
}
