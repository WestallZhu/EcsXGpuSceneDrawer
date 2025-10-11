Shader "Custom/SpriteEcs"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _SpriteColor ("Sprite Color", Float) = 0
        _SpriteFlipX ("Sprite Flip X", Float) = 0
        _SpriteFlipY ("Sprite Flip Y", Float) = 0
        _SpriteDrawMode ("Sprite Draw Mode", Float) = 0
        _SpriteTileSize ("Sprite Tile Size", Float) = 1
        _SpriteAnimationSpeed ("Sprite Animation Speed", Float) = 1
        _SpriteRectParam ("Sprite Rect Param", Vector) = (1,1,1,1)
        _SpritePivotScale ("Sprite Pivot Scale", Vector) = (0,0,1,1)
        _SpriteFadeStartTime ("Sprite Fade Start Time", Float) = 0
        _SpriteBillBord("BillBordParam", Vector) = (0,0,0,0)
        _SpriteFitDynamicScaleX ("Sprite Fit Dynamic Scale X", Float) = 1
    }

    SubShader
    {
        Tags
        { 
            "Queue"="Transparent" 
            "IgnoreProjector"="True" 
            "RenderType"="Transparent" 
            "PreviewType"="Plane" "RenderPipeline"="UniversalPipeline"
        }

        Cull Off
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        HLSLINCLUDE
        #undef UNITY_INSTANCED_SH
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"


        CBUFFER_START(GlobalFrameData)
            float4 _BillBordParam;
        CBUFFER_END

        CBUFFER_START(UnityPerMaterial)
            float _SpriteColor;
            float _SpriteFlipX;
            float _SpriteFlipY;
            float _SpriteDrawMode;
            float _SpriteTileSize;
            float _SpriteAnimationSpeed;
            
            float4 _SpriteRectParam;
            float4 _SpritePivotScale; // xy: pivot, zw: scale
            float2 _SpriteBillBord;
			float _SpriteFadeStartTime;
            float _SpriteFitDynamicScaleX;
        CBUFFER_END

        ENDHLSL

        Pass
        {
            HLSLPROGRAM
            #include_with_pragmas "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DOTS.hlsl"
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #pragma multi_compile USE_VTF
            #pragma enable_d3d11_debug_symbols

            
            #ifdef UNITY_DOTS_INSTANCING_ENABLED
            UNITY_DOTS_INSTANCING_START(MaterialPropertyMetadata)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteColor)
                UNITY_DOTS_INSTANCED_PROP(float, _SpriteFlipX)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteFlipY)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteDrawMode)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteTileSize)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteAnimationSpeed)
                UNITY_DOTS_INSTANCED_PROP(float4 , _SpriteRectParam)
                UNITY_DOTS_INSTANCED_PROP(float4 , _SpritePivotScale)
				UNITY_DOTS_INSTANCED_PROP(float2 , _SpriteBillBord)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteFadeStartTime)
                UNITY_DOTS_INSTANCED_PROP(float , _SpriteFitDynamicScaleX)
            UNITY_DOTS_INSTANCING_END(MaterialPropertyMetadata)

            static float unity_DOTS_Sampled_SpriteColor;
            static float  unity_DOTS_Sampled_SpriteFlipX;
            static float  unity_DOTS_Sampled_SpriteFlipY;
            static float  unity_DOTS_Sampled_SpriteDrawMode;
            static float  unity_DOTS_Sampled_SpriteTileSize;
            static float  unity_DOTS_Sampled_SpriteAnimationSpeed;
            static float4  unity_DOTS_Sampled_SpriteRectParam;
            static float4  unity_DOTS_Sampled_SpritePivotScale;
            static float unity_DOTS_Sampled_SpriteFadeStartTime;
            static float2 unity_DOTS_Sampled_SpriteBillBord;
            static float unity_DOTS_Sampled_SpriteFitDynamicScaleX;

            void SetupDOTSGPUAnimMaterialPropertyCaches()
            {
                unity_DOTS_Sampled_SpriteColor       = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpriteColor);
                unity_DOTS_Sampled_SpriteFlipX     = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _SpriteFlipX);
                unity_DOTS_Sampled_SpriteFlipY        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpriteFlipY);
                unity_DOTS_Sampled_SpriteDrawMode        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpriteDrawMode);
                unity_DOTS_Sampled_SpriteTileSize        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpriteTileSize);
                unity_DOTS_Sampled_SpriteAnimationSpeed        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float , _SpriteAnimationSpeed);
                unity_DOTS_Sampled_SpriteRectParam        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _SpriteRectParam);
                unity_DOTS_Sampled_SpritePivotScale        = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float4 , _SpritePivotScale);
                unity_DOTS_Sampled_SpriteFadeStartTime = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _SpriteFadeStartTime);
                unity_DOTS_Sampled_SpriteBillBord = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float2, _SpriteBillBord);
                unity_DOTS_Sampled_SpriteFitDynamicScaleX = UNITY_ACCESS_DOTS_INSTANCED_PROP_WITH_DEFAULT(float, _SpriteFitDynamicScaleX);
            }

            #undef UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES
            #define UNITY_SETUP_DOTS_MATERIAL_PROPERTY_CACHES() SetupDOTSGPUAnimMaterialPropertyCaches()

            #define _SpriteColor            unity_DOTS_Sampled_SpriteColor
            #define _SpriteFlipX            unity_DOTS_Sampled_SpriteFlipX
            #define _SpriteFlipY            unity_DOTS_Sampled_SpriteFlipY
            #define _SpriteDrawMode         unity_DOTS_Sampled_SpriteDrawMode
            #define _SpriteTileSize         unity_DOTS_Sampled_SpriteTileSize
            #define _SpriteAnimationSpeed   unity_DOTS_Sampled_SpriteAnimationSpeed
            #define _SpriteRectParam        unity_DOTS_Sampled_SpriteRectParam
            #define _SpritePivotScale       unity_DOTS_Sampled_SpritePivotScale
            #define _SpriteFadeStartTime    unity_DOTS_Sampled_SpriteFadeStartTime
            #define _SpriteBillBord    unity_DOTS_Sampled_SpriteBillBord
            #define _SpriteFitDynamicScaleX unity_DOTS_Sampled_SpriteFitDynamicScaleX
            #endif 



            #define luaViewScaleFactor _BillBordParam.x
            #define viewLodLevel _BillBordParam.y
            #define canvasHeight _BillBordParam.z
            #define hubOrthSize  _BillBordParam.w

            struct appdata_t
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct v2f
            {
                float4 pos   : SV_POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
                float4 uvData   : TEXCOORD1; // drawMode, tiledWidth, tiledHeight, unused
                float4 rect     : TEXCOORD2; // rect.x, rect.y, rect.z, rect.w
                float4 animSpeed: TEXCOORD3; // animationSpeed.x, y, z, w
                float maskFlag  : TEXCOORD4; // 新增，传递maskFlag
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            float4 DecodeFloat4(float encoded)
            {
                uint encodedInt = asuint(encoded);
                float4 result;
                result.x = ((encodedInt >> 24) & 0xFF) / 255.0;
                result.y = ((encodedInt >> 16) & 0xFF) / 255.0;
                result.z = ((encodedInt >>  8) & 0xFF) / 255.0;
                result.w = (encodedInt        & 0xFF) / 255.0;
                return result;
            }

            v2f vert(appdata_t v)
            {
                v2f o;
                UNITY_SETUP_INSTANCE_ID(v);
                UNITY_TRANSFER_INSTANCE_ID(v, o);
                o.texcoord = v.texcoord;

                float drawModeBits = _SpriteDrawMode;
                float maskFlag = floor(drawModeBits / 2.0 + 0.01); // 高位
                float drawMode = fmod(drawModeBits, 2.0); // 低位
                float tiledWidth = _SpriteTileSize;
                float animationSpeed = _SpriteAnimationSpeed;
                float2 pivot = _SpritePivotScale.xy;
                float2 scale = _SpritePivotScale.zw;
                float4 rect = _SpriteRectParam;
                float4 decodedColor = DecodeFloat4(_SpriteColor);

                // 顶点TRS
                float3 localVertex = v.vertex;
                localVertex.xy = localVertex.xy - pivot;
                localVertex.xy = localVertex.xy * scale;
                localVertex.x *= _SpriteFitDynamicScaleX;
                if (drawMode > 0.99f) {
                    localVertex.x *= tiledWidth;
                    localVertex.y *= 1.0f;
                }

   
                 float3 wpos;

                uint2 billBordWord = asuint(_SpriteBillBord.xy);
                if( billBordWord.x != 0 || billBordWord.y != 0)
                {
                    // Mask values for decoding
                    const uint signScreenOffsetLevelX = 1;
                    const uint signScreenOffsetLevelY = 2;
                    const uint signTargetOffsetX = 4;
                    const uint signTargetOffsetY = 8;
                    const uint needScaleFlag = 16;
                    const uint needOffsetScaleFlag = 32;
                    const uint cCanvasMask = 1 << (51-30);
                    const uint cHaveParentMask = 1 << (52-30);

                    // Decode screenOffsetLevel 
                    int screenOffsetLevel = (billBordWord.x >> 6) & 0x7;


                    // Decode needScale and needOffsetScale (from the encoded value)
                    bool needScale = (billBordWord.x & needScaleFlag) != 0;
                    bool needOffsetScale = (billBordWord.x & needOffsetScaleFlag) != 0;

                        bool bCanvas = (billBordWord.y & cCanvasMask) != 0;
                        float scaleFactor = 1.0;
                    bool4 isPositive = bool4((billBordWord.x & signScreenOffsetLevelX) == 0,
                            (billBordWord.x & signScreenOffsetLevelY) == 0, (billBordWord.x & signTargetOffsetX) == 0,
                            (billBordWord.x & signTargetOffsetY) == 0);
                    float4 screenOrWorldOffset = float4( (billBordWord.x >> 10) & 0x3FF,
                        (billBordWord.x >> 20) & 0x3FF, 
                        (billBordWord.y & 0x3FF) / 10.0,
                        ((billBordWord.y >> 10) & 0x3FF) / 10.0);

                    screenOrWorldOffset = lerp(-screenOrWorldOffset, screenOrWorldOffset, isPositive);

                    float3 position = float3(UNITY_MATRIX_M[0][3], UNITY_MATRIX_M[1][3], UNITY_MATRIX_M[2][3]);
                    float distanceToCamera = distance(_WorldSpaceCameraPos, position);
                    float tanHalfFovY = abs(1.0 / UNITY_MATRIX_P._m11);
                    float cameraHeight = 2.0f * distanceToCamera * tanHalfFovY;

                   
                    if (viewLodLevel > 1)
                    {
                        if ((billBordWord.y & cHaveParentMask) != 0 && viewLodLevel > screenOffsetLevel)
                        {
                            screenOrWorldOffset.xy = screenOrWorldOffset.zw;
                            needOffsetScale = true;
                        }

                        screenOrWorldOffset.xy *=  cameraHeight / _ScreenParams.y;

                        if ((viewLodLevel > screenOffsetLevel || screenOffsetLevel == 6) && !needOffsetScale)
                        {
                            position.xz += screenOrWorldOffset.zw;
                        }
                        else
                        {
                            if (needOffsetScale)
                                screenOrWorldOffset.xy *= luaViewScaleFactor;
                            float3 cameraWorldUp = unity_MatrixInvV._m01_m11_m21;
                            position += cameraWorldUp * screenOrWorldOffset.y + float3(1, 0, 0) * screenOrWorldOffset.x;
                        }
                    }

                    scaleFactor = bCanvas ? cameraHeight / canvasHeight : cameraHeight / hubOrthSize;
                    if (needScale)
                        scaleFactor *= luaViewScaleFactor;

                    float4x4 worldMatrix =  unity_MatrixInvV;
                    worldMatrix[0][3] = position.x;
                    worldMatrix[1][3] = position.y;
                    worldMatrix[2][3] = position.z;

                   
                    wpos = mul(worldMatrix,  float4(mul((float3x3)UNITY_MATRIX_M, scaleFactor * localVertex), 1.0));
                }
                else
                {
                    wpos = mul(UNITY_MATRIX_M, float4(localVertex,1.0) ).xyz;
                }
                o.pos = mul(UNITY_MATRIX_VP, float4(wpos, 1.0));

                // UV翻转
                float2 uv = v.texcoord;
                if (_SpriteFlipX > 0.5f) uv.x = 1.0 - uv.x;
                if (_SpriteFlipY > 0.5f) uv.y = 1.0 - uv.y;
                o.texcoord = uv;

                o.rect = rect;
                o.uvData = float4(drawMode, tiledWidth, 1.0, 1.0);
                o.animSpeed = float4(animationSpeed, 0, 0, 0);
                o.color = v.color * decodedColor;
                o.maskFlag = maskFlag;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(i);
                float2 tiledUV = i.texcoord;
                if (i.uvData.x > 0.99f) {
                    tiledUV = frac(i.texcoord * i.uvData.yz);
                    if(i.animSpeed.x > 0) {
                        float2 uvOffset = frac(_Time.y * float2(-i.animSpeed.x,0.0f));
                        tiledUV = frac(tiledUV + uvOffset);
                    }
                }
                float2 remappedUV = i.rect.xy + tiledUV * i.rect.zw;
                float4 col = tex2D(_MainTex, remappedUV) * i.color;
                float fade = saturate((_Time.y - _SpriteFadeStartTime) / 0.3f);
                col.a *= fade;
                if (i.maskFlag > 0.5) {
                    float radius = 0.22; // 圆角半径
                    float2 uv = tiledUV;
                    float2 rect = float2(0.5, 0.5) - radius;
                    float2 d = abs(uv - 0.5) - rect;
                    float dist = length(max(d, 0.0)) - radius; // <0 在矩形内
                    // --- 抗锯齿过渡带 ---------------------------------------------------------
                    float  aa     = fwidth(dist) * 0.5;             // 建议乘 0.5 细一点
                    float  alpha  = 1.0 - smoothstep(0.0, aa, dist);// dist ≤0 ⇒ α≈1，dist≥aa ⇒ α≈0
                    col.a *= alpha;
                }
                return col;
            }
            ENDHLSL
        }
    }
}