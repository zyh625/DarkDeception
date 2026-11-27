Shader "DarkDeception/UI_MiniMapRadialFade"
{
    Properties
    {
        [PerRendererData] _MainTex ("Sprite Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)

        // Root-Canvas local space elliptical/circular soft-fade clip updated by LevelOneMiniMapHUD
        _ClipCenter ("Clip Center (Canvas XY)", Vector) = (0, 0, 0, 0)
        _ClipRadii  ("Clip Radii (X=rx, Y=ry, Z=innerFadeStart)", Vector) = (0, 0, 0.56, 0)
        _ClipAxes   ("Clip Inverse Axes (XY=right/rx, ZW=up/ry)", Vector) = (0, 0, 0, 0)

        _StencilComp ("Stencil Comparison", Float) = 8
        _Stencil ("Stencil ID", Float) = 0
        _StencilOp ("Stencil Operation", Float) = 0
        _StencilWriteMask ("Stencil Write Mask", Float) = 255
        _StencilReadMask ("Stencil Read Mask", Float) = 255
        _ColorMask ("Color Mask", Float) = 15
    }

    SubShader
    {
        Tags
        {
            "Queue"="Transparent"
            "IgnoreProjector"="True"
            "RenderType"="Transparent"
            "PreviewType"="Plane"
            "CanUseSpriteAtlas"="True"
        }

        Stencil
        {
            Ref [_Stencil]
            Comp [_StencilComp]
            Pass [_StencilOp]
            ReadMask [_StencilReadMask]
            WriteMask [_StencilWriteMask]
        }

        Cull Off
        Lighting Off
        ZWrite Off
        ZTest [unity_GUIZTestMode]
        Blend SrcAlpha OneMinusSrcAlpha
        ColorMask [_ColorMask]

        Pass
        {
            Name "MiniMapRadialFade"
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_local _ UNITY_UI_CLIP_RECT
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 vertex   : POSITION;
                float4 color    : COLOR;
                float2 texcoord : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float4 color       : COLOR;
                float2 uv          : TEXCOORD0;
                float2 canvasPos   : TEXCOORD1;
            };

            TEXTURE2D(_MainTex);
            SAMPLER(sampler_MainTex);

            // Unity CanvasRenderer / RectMask2D sets these outside UnityPerMaterial
            int _UIVertexColorAlwaysGammaSpace;
            float4 _ClipRect;

            CBUFFER_START(UnityPerMaterial)
                float4 _MainTex_ST;
                float4 _Color;
                float4 _ClipCenter;
                float4 _ClipRadii;
                float4 _ClipAxes;
            CBUFFER_END

            half3 MiniMapGammaToLinear(half3 srgb)
            {
                half3 lo = srgb / 12.92h;
                half3 hi = pow(max((srgb + 0.055h) / 1.055h, 0.0h), 2.4h);
                return lerp(lo, hi, step(0.04045h, srgb));
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                float3 posWS = TransformObjectToWorld(IN.vertex.xyz);
                OUT.positionHCS = TransformWorldToHClip(posWS);
                OUT.uv = TRANSFORM_TEX(IN.texcoord, _MainTex);

                half4 vcol = IN.color;
                #ifndef UNITY_COLORSPACE_GAMMA
                if (_UIVertexColorAlwaysGammaSpace)
                {
                    vcol.rgb = MiniMapGammaToLinear(vcol.rgb);
                }
                #endif

                OUT.color = vcol * _Color;
                // 关键：直接使用与 RectMask2D._ClipRect 完全一致的 Root-Canvas 本地坐标 IN.vertex.xy！
                OUT.canvasPos = IN.vertex.xy;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                half4 texCol = SAMPLE_TEXTURE2D(_MainTex, sampler_MainTex, IN.uv) * IN.color;
                float innerStart = clamp(_ClipRadii.z, 0.1, 0.95);

                #ifdef UNITY_UI_CLIP_RECT
                // RectMask2D 兜底裁切：绝不允许任何像素溢出 MapViewport 矩形
                float2 inside = step(_ClipRect.xy, IN.canvasPos) * step(IN.canvasPos, _ClipRect.zw);
                texCol.a *= inside.x * inside.y;
                #endif

                // 优先使用 LevelOneMiniMapHUD 传入的精确（支持旋转/缩放）Canvas 空间圆形渐隐
                if (_ClipRadii.x > 1.0 && _ClipRadii.y > 1.0)
                {
                    float2 delta = IN.canvasPos - _ClipCenter.xy;
                    float2 rel;
                    if (dot(_ClipAxes.xy, _ClipAxes.xy) > 1e-10 && dot(_ClipAxes.zw, _ClipAxes.zw) > 1e-10)
                    {
                        rel = float2(dot(delta, _ClipAxes.xy), dot(delta, _ClipAxes.zw));
                    }
                    else
                    {
                        rel = delta / _ClipRadii.xy;
                    }
                    float dist = length(rel);
                    float radialAlpha = 1.0 - smoothstep(innerStart, 1.0, dist);
                    texCol.a *= radialAlpha;
                }
                else
                {
                    #ifdef UNITY_UI_CLIP_RECT
                    // Edit Mode 预览：直接基于 RectMask2D 的 _ClipRect 渲染中间圆形渐隐
                    float2 rectCenter = (_ClipRect.xy + _ClipRect.zw) * 0.5;
                    float2 rectHalf = max((_ClipRect.zw - _ClipRect.xy) * 0.5, 1.0);
                    float dist = length((IN.canvasPos - rectCenter) / rectHalf);
                    texCol.a *= 1.0 - smoothstep(innerStart, 1.0, dist);
                    #endif
                }

                clip(texCol.a - 0.002);
                return texCol;
            }
            ENDHLSL
        }
    }
}
