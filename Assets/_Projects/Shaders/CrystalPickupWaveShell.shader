Shader "DarkDeception/CrystalPickupWaveShell"
{
    Properties
    {
        _PatternTex ("Ring Pattern Texture (RGBA, Clamp)", 2D) = "white" {}
        _TintColor ("Wave Tint Color", Color) = (1, 0.16, 0.20, 1)
        _CoreHighlightColor ("Core Highlight Color", Color) = (1, 0.86, 0.86, 1)
        _Brightness ("Brightness", Range(0, 8)) = 2.6
        _Opacity ("Opacity", Range(0, 2)) = 1.0
        _ArcSharpness ("Arc Edge Sharpness", Range(0.5, 3.0)) = 1.35
        _RingScale ("Ring Projection Scale", Range(0.30, 0.75)) = 0.455
        _ShellFillAlpha ("Volumetric Shell Fill", Range(0, 0.4)) = 0.07
        _FresnelBoost ("Rim Fresnel Accent", Range(0, 1.5)) = 0.32
        _AngleOffset ("Angle Offset (Rad)", Float) = 0.0
        _SpinPhase ("Spin Phase (Rad)", Float) = 0.0
        _NearCamFade ("Near Camera Soft Distance", Float) = 0.05
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "CrystalWaveShellForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha One

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 dirOS       : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 worldPos    : TEXCOORD2;
            };

            TEXTURE2D(_PatternTex);
            SAMPLER(sampler_PatternTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _TintColor;
                float4 _CoreHighlightColor;
                float  _Brightness;
                float  _Opacity;
                float  _ArcSharpness;
                float  _RingScale;
                float  _ShellFillAlpha;
                float  _FresnelBoost;
                float  _AngleOffset;
                float  _SpinPhase;
                float  _NearCamFade;
            CBUFFER_END

            float2 Rotate2D(float2 v, float angle)
            {
                float c = cos(angle);
                float s = sin(angle);
                return float2(v.x * c - v.y * s, v.x * s + v.y * c);
            }

            float4 SampleRingProjection(float2 planeXY, float angle, float ringScale)
            {
                float2 uv = Rotate2D(planeXY, angle) * ringScale + 0.5;
                float4 col = SAMPLE_TEXTURE2D(_PatternTex, sampler_PatternTex, saturate(uv));
                float inBounds = step(0.004, uv.x) * step(uv.x, 0.996) * step(0.004, uv.y) * step(uv.y, 0.996);
                return col * inBounds;
            }

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = posInputs.positionCS;
                OUT.dirOS = normalize(IN.positionOS.xyz);
                OUT.normalWS = normInputs.normalWS;
                OUT.worldPos = posInputs.positionWS;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float3 d = normalize(IN.dirOS);

                // 绕球体竖轴做缓慢相位旋转，同时支持黄水晶第二道波错开刻度角度
                float totalAngle = _AngleOffset + _SpinPhase;
                float ca = cos(totalAngle);
                float sa = sin(totalAngle);
                float3 dr = float3(d.x * ca - d.z * sa, d.y, d.x * sa + d.z * ca);

                // 三正交轴 + 斜向大圆双频球面投影：
                // - 外环 (_RingScale ≈ 0.48~0.52) 在 ~40°~48° 交织成完整 3D 球壳包络（Scene 侧面清晰可见整球扩张）；
                // - 内环 (_RingScale * 1.52 ≈ 0.74) 在 ~22°~29° 完整落入第一人称 60° FOV 视野内，确保玩家身处球内时弧纹与刻度极其清晰，
                //   同时正中心 (<14°) 保持通透只掠过轻薄半透明亮纹，不遮挡中心视线
                float innerScale = _RingScale * 1.52;

                float4 pZ1 = SampleRingProjection(dr.xy, _AngleOffset * 0.50, _RingScale);
                float4 pZ2 = SampleRingProjection(dr.xy, -_AngleOffset * 0.35 + 0.5236, innerScale);
                float  wZ1 = smoothstep(0.16, 0.54, abs(dr.z));
                float  wZ2 = smoothstep(0.58, 0.86, abs(dr.z)) * 0.92;

                float4 pX1 = SampleRingProjection(dr.yz, _AngleOffset * 0.50 + 1.0472, _RingScale);
                float4 pX2 = SampleRingProjection(dr.yz, -_AngleOffset * 0.35 + 1.5708, innerScale);
                float  wX1 = smoothstep(0.16, 0.54, abs(dr.x));
                float  wX2 = smoothstep(0.58, 0.86, abs(dr.x)) * 0.92;

                float4 pY1 = SampleRingProjection(dr.xz, _AngleOffset * 0.50 + 2.0944, _RingScale);
                float4 pY2 = SampleRingProjection(dr.xz, -_AngleOffset * 0.35 + 2.6180, innerScale);
                float  wY1 = smoothstep(0.16, 0.54, abs(dr.y));
                float  wY2 = smoothstep(0.58, 0.86, abs(dr.y)) * 0.92;

                // 斜向补面投影：覆盖八个象限斜角，让旋转或斜看走廊时弧纹连续扫过
                float3 dt = normalize(float3(
                     dr.x * 0.70710678 - dr.y * 0.5 + dr.z * 0.5,
                     dr.x * 0.5 + dr.y * 0.70710678 - dr.z * 0.5,
                     dr.x * 0.5 + dr.y * 0.5 + dr.z * 0.70710678
                ));
                float4 pD = SampleRingProjection(dt.xy, -_AngleOffset * 0.75 + 0.7854, _RingScale * 1.15);
                float  wD = smoothstep(0.22, 0.62, abs(dt.z)) * 0.85;

                float aZ = max(pZ1.a * wZ1, pZ2.a * wZ2);
                float aX = max(pX1.a * wX1, pX2.a * wX2);
                float aY = max(pY1.a * wY1, pY2.a * wY2);
                float aD = pD.a * wD;

                float3 rgbZ = pZ1.rgb * (pZ1.a * wZ1) + pZ2.rgb * (pZ2.a * wZ2);
                float3 rgbX = pX1.rgb * (pX1.a * wX1) + pX2.rgb * (pX2.a * wX2);
                float3 rgbY = pY1.rgb * (pY1.a * wY1) + pY2.rgb * (pY2.a * wY2);
                float3 rgbD = pD.rgb * aD;

                float sumA = (pZ1.a * wZ1 + pZ2.a * wZ2) + (pX1.a * wX1 + pX2.a * wX2) + (pY1.a * wY1 + pY2.a * wY2) + aD;
                float rawMask = saturate(max(max(aZ, aX), max(aY, aD)) + sumA * 0.10);
                float arcMask = pow(rawMask, _ArcSharpness);

                float3 texRgb = (rgbZ + rgbX + rgbY + rgbD) / max(0.0001, sumA);
                float3 coreTint = lerp(_TintColor.rgb, _CoreHighlightColor.rgb, smoothstep(0.55, 0.98, rawMask));
                float3 arcColor = coreTint * lerp(float3(1.0, 1.0, 1.0), texRgb * 1.45, 0.60);

                // 轻薄半透明球壳膜感 + 柔和边缘提亮（内外双面一致，中心靠近球心也同样清晰可见）
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(GetWorldSpaceViewDir(IN.worldPos));
                float ndotv = saturate(abs(dot(N, V)));
                float fresnel = pow(1.0 - ndotv, 2.2);

                float shellVeil = _ShellFillAlpha * (0.58 + 0.42 * (1.0 - ndotv)) + fresnel * _FresnelBoost * 0.26;
                float3 finalRgb = (arcColor * arcMask * 1.30 + _TintColor.rgb * shellVeil) * _Brightness;
                float finalAlpha = saturate((arcMask * 0.96 + shellVeil * 0.85) * _Opacity);

                // 距离相机镜片极近处极短柔和过渡，避免掠过眼前瞬间生硬切屏
                float camDist = length(IN.worldPos - _WorldSpaceCameraPos);
                finalAlpha *= smoothstep(0.008, max(0.02, _NearCamFade), camDist);

                return half4(finalRgb, finalAlpha);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
