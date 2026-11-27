Shader "DarkDeception/PortalOffsetMirror"
{
    Properties
    {
        _NoiseTex ("Flow Distortion Noise (Linear, Repeat)", 2D) = "gray" {}
        _MaskTex ("Rift Glow Mask (Linear, Clamp)", 2D) = "black" {}
        _BaseColor ("Mirror Deep Base Color", Color) = (0.03, 0.05, 0.09, 1)
        _SwirlColor ("Mirror Swirl Tint", Color) = (0.10, 0.34, 0.44, 1)
        _RiftEdgeColor ("Rift Outer Glow Color", Color) = (0.28, 0.78, 1.0, 1)
        _RiftCoreColor ("Rift Inner Core Color", Color) = (0.86, 0.98, 1.0, 1)
        _FresnelColor ("Mirror Glass Rim Tint", Color) = (0.35, 0.76, 0.90, 1)
        _NoiseScale ("Noise Tiling", Vector) = (1.6, 2.4, 0, 0)
        _FlowSpeed1 ("Flow Layer 1 Speed", Vector) = (0.08, 0.22, 0, 0)
        _FlowSpeed2 ("Flow Layer 2 Speed", Vector) = (-0.06, -0.15, 0, 0)
        _DistortionStrength ("Rift Distortion Strength", Range(0, 0.15)) = 0.038
        _SwirlStrength ("Mirror Swirl Intensity", Range(0, 2)) = 0.55
        _GlowIntensity ("Rift Glow Intensity", Range(0, 6)) = 2.4
        _PulseSpeed ("Rift Pulse Speed", Range(0, 8)) = 2.2
        _FresnelPower ("Fresnel Power", Range(1, 8)) = 3.2
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "PortalMirrorForward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            ZWrite On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float2 uv          : TEXCOORD0;
                float3 normalWS    : TEXCOORD1;
                float3 viewDirWS   : TEXCOORD2;
            };

            TEXTURE2D(_NoiseTex);
            SAMPLER(sampler_NoiseTex);
            TEXTURE2D(_MaskTex);
            SAMPLER(sampler_MaskTex);

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _SwirlColor;
                float4 _RiftEdgeColor;
                float4 _RiftCoreColor;
                float4 _FresnelColor;
                float4 _NoiseScale;
                float4 _FlowSpeed1;
                float4 _FlowSpeed2;
                float  _DistortionStrength;
                float  _SwirlStrength;
                float  _GlowIntensity;
                float  _PulseSpeed;
                float  _FresnelPower;
            CBUFFER_END

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                VertexPositionInputs posInputs = GetVertexPositionInputs(IN.positionOS.xyz);
                VertexNormalInputs normInputs = GetVertexNormalInputs(IN.normalOS);
                OUT.positionHCS = posInputs.positionCS;
                OUT.uv = IN.uv;
                OUT.normalWS = normInputs.normalWS;
                OUT.viewDirWS = GetWorldSpaceViewDir(posInputs.positionWS);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                float2 uv = IN.uv;
                float t = _Time.y;

                // 双层反向流动噪声：驱动镜面内部液态扭曲，不外溢到外框
                float2 nUV1 = uv * _NoiseScale.xy + _FlowSpeed1.xy * t;
                float2 nUV2 = uv * (_NoiseScale.xy * 1.35) + _FlowSpeed2.xy * t;
                float n1 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, nUV1).r;
                float n2 = SAMPLE_TEXTURE2D(_NoiseTex, sampler_NoiseTex, nUV2).r;
                float flowNoise = saturate(n1 * 0.6 + n2 * 0.4);

                // 边缘平滑衰减，确保扭曲与发光严格留在镜面内部
                float2 borderDist = min(uv, 1.0 - uv);
                float innerBounds = smoothstep(0.0, 0.08, min(borderDist.x, borderDist.y));

                float2 distortion = float2(n1 - 0.5, n2 - 0.5) * (_DistortionStrength * innerBounds);
                float2 warpedUV = saturate(uv + distortion);

                // 裂隙遮罩（Clamp 线性数据）控制镜面内部发光区域
                float riftMask = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, warpedUV).r * innerBounds;
                float riftHalo = SAMPLE_TEXTURE2D(_MaskTex, sampler_MaskTex, saturate(uv + distortion * 1.45)).r * innerBounds;

                // 镜面暗色底 + 流动幽光纹理 + 玻璃菲涅尔微反光
                float3 N = normalize(IN.normalWS);
                float3 V = normalize(IN.viewDirWS);
                float fresnel = pow(1.0 - saturate(abs(dot(N, V))), _FresnelPower);

                float swirlBand = smoothstep(0.32, 0.85, flowNoise) * innerBounds;
                float3 mirrorCol = _BaseColor.rgb
                    + _SwirlColor.rgb * (swirlBand * _SwirlStrength)
                    + _FresnelColor.rgb * (fresnel * 0.32 * innerBounds);

                // 裂隙脉动发光核心
                float pulse = 0.88 + 0.12 * sin(t * _PulseSpeed + flowNoise * 3.14159);
                float edgeGlow = saturate(riftHalo * 1.15 - riftMask * 0.45);
                float coreGlow = smoothstep(0.35, 0.92, riftMask);
                float3 riftCol = (_RiftEdgeColor.rgb * (edgeGlow + riftMask * 0.65)
                                + _RiftCoreColor.rgb * coreGlow) * (_GlowIntensity * pulse);

                float3 finalCol = mirrorCol + riftCol;
                return half4(finalCol, 1.0);
            }
            ENDHLSL
        }
    }
    FallBack Off
}
