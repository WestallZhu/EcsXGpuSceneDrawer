
#ifndef GPU_ECS_SKIN_INCLUDED
#define GPU_ECS_SKIN_INCLUDED


#ifdef UNITY_DOTS_INSTANCING_ENABLED

UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
    UNITY_DOTS_INSTANCED_PROP(float4 , _AnimationState)  // Animation state data (time, frame indices, blend factors)
    UNITY_DOTS_INSTANCED_PROP(float, _Color32)           // Compressed color data for character variations
    UNITY_DOTS_INSTANCED_PROP(float , _SpeedFactor)      // Dynamic speed control for animation playback
UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)


static float4  unity_DOTS_Sampled_AnimationState;  // Cached animation state for current instance
static float unity_DOTS_Sampled_Color32;           // Cached color data for current instance
static float  unity_DOTS_Sampled_SpeedFactor;      // Cached speed factor for current instance


void SetupDOTSGPUAnimMaterialPropertyCaches()
{
    unity_DOTS_Sampled_AnimationState = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _AnimationState);
    unity_DOTS_Sampled_Color32        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _Color32);
    unity_DOTS_Sampled_SpeedFactor    = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpeedFactor);
}


#undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
#define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSGPUAnimMaterialPropertyCaches()


#define _AnimationState   unity_DOTS_Sampled_AnimationState
#define _Color32          unity_DOTS_Sampled_Color32
#define _SpeedFactor      unity_DOTS_Sampled_SpeedFactor

#endif


half4 loadBoneMatrixTexture(Texture2D<float4> animatedBoneMatrices, int frameIndex, int boneIndex, int i)
{
    return animatedBoneMatrices.Load(int3((boneIndex * 3) + i, frameIndex, 0));
}

#define EQUAL(x,y) !(x-y)


#define FRAME_INTERPOLATION 0  // Disable frame interpolation for maximum GPU performance
#define SKIN_WEIGHTS 3         // 3-bone skinning for optimal mobile GPU performance
#define NEED_NORMAL 1          // Enable normal transformation for proper lighting
#define STATE_BLEND 0          // Disable state blending for simplified animation pipeline


void GetSkinEx(half3 position, half3 normal, half3 tangent,
    Texture2D<float4> animatedBoneMatrices, 
    half4 boneWeights, int4 boneIndexs, int frameIndex,
    out half3 positionOut, out half3 normalOut, out half3 tangentOut)
{
#if !(SKIN_WEIGHTS-3)
    boneWeights.xyz = boneWeights.xyz / (boneWeights.x + boneWeights.y + boneWeights.z);
#endif
 

    half4 v_11 = loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.x, 0) * boneWeights.x;  // Matrix row 0
    half4 v_12 = loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.x, 1) * boneWeights.x;  // Matrix row 1
    half4 v_13 = loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.x, 2) * boneWeights.x;  // Matrix row 2
    
    if (boneWeights.y > 0)
    {
        v_11 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.y, 0) * boneWeights.y;
        v_12 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.y, 1) * boneWeights.y;
        v_13 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.y, 2) * boneWeights.y;
        
        if (boneWeights.z > 0)
        {
            v_11 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.z, 0) * boneWeights.z;
            v_12 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.z, 1) * boneWeights.z;
            v_13 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.z, 2) * boneWeights.z;
            
            #if (SKIN_WEIGHTS-3)
                if (boneWeights.w > 0)
                {
                    v_11 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.w, 0) * boneWeights.w;
                    v_12 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.w, 1) * boneWeights.w;
                    v_13 += loadBoneMatrixTexture(animatedBoneMatrices, frameIndex, boneIndexs.w, 2) * boneWeights.w;
                }
            #endif
            
        }
        
    }
    
   
    positionOut.x = dot(v_11.xyz, position.xyz) + v_11.w;
    positionOut.y = dot(v_12.xyz, position.xyz) + v_12.w;
    positionOut.z = dot(v_13.xyz, position.xyz) + v_13.w;

    // Transform normal using 3x3 rotation matrix only (no translation)
    normalOut.x = dot(v_11.xyz, normal.xyz);
    normalOut.y = dot(v_12.xyz, normal.xyz);
    normalOut.z = dot(v_13.xyz, normal.xyz);

    // Transform tangent using 3x3 rotation matrix only (no translation)
    tangentOut.x = dot(v_11.xyz, tangent.xyz);
    tangentOut.y = dot(v_12.xyz, tangent.xyz);
    tangentOut.z = dot(v_13.xyz, tangent.xyz);
    
}



float3 _DQ_Rot_Vec3(const float4 quat, const float3 v)
{
    float3 r2, r3;
    r2 = cross(quat.xyz, v);      // First cross product: q.xyz × v (mul+mad GPU instruction)
    r2 = quat.w * v + r2;         // Add scaled original vector (mad GPU instruction)
    r3 = cross(quat.xyz, r2);     // Second cross product: q.xyz × result (mul+mad GPU instruction)
    r3 = r3 * 2.0 + v;           // Final combination with original vector (mad GPU instruction)
    return r3;
}

void GetSimpleSkin3(half3 position, half3 normal, half3 tangent,
    Texture2D<float4> animatedBoneMatrices,
    half4 boneWeights, int4 boneIndexs, int frameIndex,
    out half3 positionOut, out half3 normalOut, out half3 tangentOut)
{
    /// <summary>Weight Normalization - Ensure bone weights sum to 1.0 for proper dual quaternion blending</summary>
    boneWeights.xyz = boneWeights.xyz / (boneWeights.x + boneWeights.y + boneWeights.z);
    
    /// <summary>
    /// Dual Quaternion Storage - Efficient bone representation using quaternion pairs.
    /// 
    /// DUAL QUATERNION FORMAT:
    /// - blend_dq0: Real quaternion (rotation component)
    /// - blend_dq1: Dual quaternion (translation component)
    /// - Each bone requires 2 float4 values (8 floats total vs 12 for matrices)
    /// - blend_dq0_org: Reference quaternion for sign correction during blending
    /// </summary>
    float4 blend_dq0_org;  // Original first bone quaternion for sign correction
    float4 blend_dq0;      // Current bone's real quaternion (rotation)
    float4 blend_dq1;      // Current bone's dual quaternion (translation)

    /// <summary>
    /// Primary Bone Dual Quaternion Loading - Load first bone's dual quaternion data.
    /// Bone index is multiplied by 2 because each bone stores 2 quaternions (real + dual).
    /// </summary>
    int boneIdx0 = boneIndexs[0] * 2;
    blend_dq0 = animatedBoneMatrices.Load(int3(boneIdx0, frameIndex, 0));       // Load real quaternion
    blend_dq0_org = blend_dq0;                                                  // Store for sign correction
    blend_dq1 = animatedBoneMatrices.Load(int3(boneIdx0 + 1, frameIndex, 0));  // Load dual quaternion

    /// <summary>
    /// Primary Bone Transformation - Apply first bone's dual quaternion transformation.
    /// 
    /// DUAL QUATERNION TRANSFORMATION PROCESS:
    /// 1. Scale position by dual quaternion w component (uniform scaling)
    /// 2. Rotate scaled position using real quaternion
    /// 3. Add translation from dual quaternion xyz components
    /// 4. Apply bone weight to final result
    /// 
    /// Normal and tangent only require rotation (no translation or scaling).
    /// </summary>
    positionOut  = (_DQ_Rot_Vec3(blend_dq0, position.xyz * blend_dq1.w) + blend_dq1.xyz) * boneWeights.x;
    normalOut    = _DQ_Rot_Vec3(blend_dq0, normal.xyz) * boneWeights.x;
    tangentOut   = _DQ_Rot_Vec3(blend_dq0, tangent.xyz) * boneWeights.x;
	
    /// <summary>
    /// Secondary Bone Processing - Conditional processing with quaternion sign correction.
    /// </summary>
    int boneIdx = boneIndexs[1] * 2;
    if (boneWeights.y > 0)
    {
        blend_dq0 = animatedBoneMatrices.Load(int3(boneIdx, frameIndex, 0));
        /// <summary>
        /// Quaternion Sign Correction - Critical for proper dual quaternion blending.
        /// 
        /// QUATERNION BLENDING MATHEMATICS:
        /// - Quaternions q and -q represent the same rotation
        /// - For proper blending, all quaternions must have consistent signs
        /// - sign(dot(q1, q2)) determines if quaternions are in same hemisphere
        /// - Flip quaternion sign if dot product is negative
        /// 
        /// This ensures smooth blending without sudden flips during animation.
        /// </summary>
        blend_dq0 = sign(dot(blend_dq0_org, blend_dq0)) * blend_dq0;  // Correct quaternion sign for blending
        blend_dq1 = animatedBoneMatrices.Load(int3(boneIdx + 1, frameIndex, 0));

        positionOut += (_DQ_Rot_Vec3(blend_dq0, position.xyz * blend_dq1.w) + blend_dq1.xyz) * boneWeights.y;
        normalOut += _DQ_Rot_Vec3(blend_dq0, normal.xyz) * boneWeights.y;
        tangentOut += _DQ_Rot_Vec3(blend_dq0, tangent.xyz) * boneWeights.y;

        boneIdx = boneIndexs[2] * 2;
        if (boneWeights.z > 0)
        {
            blend_dq0 = animatedBoneMatrices.Load(int3(boneIdx, frameIndex, 0));
            blend_dq0 = sign(dot(blend_dq0_org, blend_dq0)) * blend_dq0;
            blend_dq1 = animatedBoneMatrices.Load(int3(boneIdx + 1, frameIndex, 0));

            positionOut += (_DQ_Rot_Vec3(blend_dq0, position.xyz * blend_dq1.w) + blend_dq1.xyz) * boneWeights.z;
            normalOut += _DQ_Rot_Vec3(blend_dq0, normal.xyz) * boneWeights.z;
            tangentOut += _DQ_Rot_Vec3(blend_dq0, tangent.xyz) * boneWeights.z;
        }
    }

    
}

void Animate_half(half3 position, half3 normal, half3 tangent,
    half4 boneWeights, Texture2D<float4> animatedBoneMatrices, half4 animationState,
    out float3 positionOut, out half3 normalOut, out half3 tangentOut)
{
    positionOut = half3(0, 0, 0);
    normalOut = half3(0, 0, 0);
    tangentOut = half3(0, 0, 0);

    half blendFactor = animationState[0];
    half frameIndex = animationState[2];
    {
#if FRAME_INTERPOLATION
        half transitionNextFrame = frac(frameIndex) * (1.0 / 0.9);
        half prevFrameFrac = 1.0 - transitionNextFrame;
        half3 posOutBefore, posOutAfter, normalOutBefore, normalOutAfter, tangentOutBefore, tangentOutAfter;

        half4 weights = frac(boneWeights) * (1.0 / 0.9);
        int4 boneIndexs = boneWeights;

        GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex, posOutBefore, normalOutBefore, tangentOutBefore);
        GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex + 1, posOutAfter, normalOutAfter, tangentOutAfter);
        positionOut = (prevFrameFrac * posOutBefore + transitionNextFrame * posOutAfter);
        normalOut = (prevFrameFrac * normalOutBefore + transitionNextFrame * normalOutAfter);
        tangentOut = (prevFrameFrac * tangentOutBefore + transitionNextFrame * tangentOutAfter);
#else

        half4 weights = frac(boneWeights) * (1.0 / 0.9);
        int4 boneIndexs = boneWeights;
        GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex, positionOut, normalOut, tangentOut);
#endif

    }

}


void AnimateBlend_half(half3 position, half3 normal, half3 tangent, 
    half4 boneWeights, Texture2D<float4> animatedBoneMatrices, half4 animationState,
    out float3 positionOut, out half3 normalOut, out half3 tangentOut) 
{
#if STATE_BLEND 
        positionOut = half3(0, 0, 0);
        normalOut = half3(0, 0, 0);
        tangentOut = half3(0, 0, 0);
    
        [unroll]
        for(int blendIndex = 0; blendIndex < 2; blendIndex++)
        {
            half blendFactor = animationState[blendIndex * 2];
            half frameIndex = animationState[blendIndex * 2 + 1];
            if(blendFactor > 0)
            {
    #if FRAME_INTERPOLATION
                half transitionNextFrame = frac(frameIndex) * (1.0/0.9);
                half prevFrameFrac = 1.0 - transitionNextFrame;
                half3 posOutBefore, posOutAfter, normalOutBefore, normalOutAfter, tangentOutBefore, tangentOutAfter;
            
               half4 weights = frac(boneWeights) * (1.0 / 0.9);
               int4 boneIndexs = boneWeights;
            
                GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex, posOutBefore, normalOutBefore, tangentOutBefore);
                GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex + 1, posOutAfter, normalOutAfter, tangentOutAfter);
                positionOut += blendFactor * (prevFrameFrac * posOutBefore + transitionNextFrame * posOutAfter);
                normalOut += blendFactor * (prevFrameFrac * normalOutBefore + transitionNextFrame * normalOutAfter);
                tangentOut += blendFactor * (prevFrameFrac * tangentOutBefore + transitionNextFrame * tangentOutAfter);
    #else

                half3 posOutBefore, posOutAfter, normalOutBefore, normalOutAfter, tangentOutBefore, tangentOutAfter;
                half4 weights = frac(boneWeights) * (1.0 / 0.9);
                int4 boneIndexs = boneWeights;
                GetSimpleSkin3(position, normal, tangent, animatedBoneMatrices, weights, boneIndexs, frameIndex, posOutBefore, normalOutBefore, tangentOutBefore);
                positionOut += blendFactor * posOutBefore;
                normalOut += blendFactor * normalOutBefore;
                tangentOut += blendFactor * tangentOutBefore;
    #endif

            }
        }
#else
    Animate_half(position, normal, tangent, boneWeights, animatedBoneMatrices, animationState, positionOut, normalOut, tangentOut);
#endif
    
}


half4 PlayAnimationState(float4 value)
{
    float startPlayTime = value.x;
    /// <summary>Bit Unpacking - Extract animation parameters from packed uint32</summary>
    uint raw1 = asuint(value.y);
    uint startFrameIndex      = raw1 & 0x3FF;          // Extract bits 0-9: Start frame (0-1023)
    uint nbrOfFramesPerSample = (raw1 >> 10) & 0x3FF;  // Extract bits 10-19: Frame count (0-1023)
    uint intervalNbrOfFrames  = (raw1 >> 20) & 0x3FF;  // Extract bits 20-29: Interval frames (0-1023)
    uint loop                 = (raw1 >> 30) & 0x3;    // Extract bits 30-31: Loop mode (0-3)

    /// <summary>Next Animation State Unpacking - Extract next animation parameters</summary>
    uint raw2 = asuint(value.z);
    uint nextStartFrameIndex      = raw2 & 0x3FF;          // Next animation start frame
    uint nextNbrOfFramesPerSample = (raw2 >> 10) & 0x3FF;  // Next animation frame count
    float speedFactor             = (raw2 >> 20) / 100.0;  // Speed factor (percentage encoded)


    const float SampleFrameRate = 30;  // Animation sampling rate (30 FPS)
    uint frames = (_Time.y - startPlayTime) * speedFactor * SampleFrameRate;
    uint iframe;  // Final frame index to be calculated


    uint cycle = intervalNbrOfFrames + nbrOfFramesPerSample;  // Total cycle length for interval animations
    bool hasInterval = (intervalNbrOfFrames > 0);             // Check for complex interval animation

    if (hasInterval) {
        /// <summary> Interval Animation Processing - Handle complex animation sequences with pauses</summary>
        uint modFrames = (loop == 0) ? frames : frames % cycle;  // Handle looping with proper cycle calculation
        bool inFirstPart = (modFrames < nbrOfFramesPerSample);   // Determine if in main animation or interval
        iframe = inFirstPart
            ? (modFrames + startFrameIndex)  // Main animation sequence
            : ((modFrames - nbrOfFramesPerSample) % nextNbrOfFramesPerSample + nextStartFrameIndex);  // Interval sequence
    }
    else {
        /// <summary>Simple Animation Processing - Handle standard animation sequences</summary>
        bool shouldClamp = (loop == 0) && (frames >= nbrOfFramesPerSample);  // Check for end-of-animation clamping
        iframe = shouldClamp
            ? (nbrOfFramesPerSample - 1 + startFrameIndex)      // Clamp to last frame (no loop)
            : (frames % nbrOfFramesPerSample + startFrameIndex);  // Normal playback with optional looping
    }

    return float4(0, 0, iframe, 0);
}

half4 PlayAnimationState(float4 value, float speedFactor)
{
    float startPlayTime = value.x;
    uint raw1 = asuint(value.y);
    uint startFrameIndex = raw1 & 0x3FF;
    uint nbrOfFramesPerSample = (raw1 >> 10) & 0x3FF;
    uint intervalNbrOfFrames = (raw1 >> 20) & 0x3FF;
    uint loop = (raw1 >> 30) & 0x3;

    uint raw2 = asuint(value.z);
    uint nextStartFrameIndex = raw2 & 0x3FF;
    uint nextNbrOfFramesPerSample = (raw2 >> 10) & 0x3FF;

    const float SampleFrameRate = 30;
    uint frames = (_Time.y - startPlayTime) * speedFactor * SampleFrameRate;
    uint iframe;

    // Branch optimization using precomputed values and ternary operators
    uint cycle = intervalNbrOfFrames + nbrOfFramesPerSample;
    bool hasInterval = (intervalNbrOfFrames > 0);

    if (hasInterval)
    {
        uint modFrames = (loop == 0) ? frames : frames % cycle;
        bool inFirstPart = (modFrames < nbrOfFramesPerSample);
        iframe = inFirstPart
            ? (modFrames + startFrameIndex)
            : ((modFrames - nbrOfFramesPerSample) % nextNbrOfFramesPerSample + nextStartFrameIndex);
    }
    else
    {
        bool shouldClamp = (loop == 0) && (frames >= nbrOfFramesPerSample);
        iframe = shouldClamp
            ? (nbrOfFramesPerSample - 1 + startFrameIndex)
            : (frames % nbrOfFramesPerSample + startFrameIndex);
    }

    return float4(0, 0, iframe, 0);
}

#endif //GPU_ECS_SKIN_INCLUDED