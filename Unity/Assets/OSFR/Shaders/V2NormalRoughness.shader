Shader "OSFR/V2 Normal Roughness"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.04, 0.04, 0.04, 1)
        _SpecularColor ("Specular Color", Color) = (1, 0.9, 0.75, 1)
        _NormalFrequency ("Normal Frequency (cycles/m)", Float) = 64
        _NormalAmplitude ("Normal Slope Amplitude", Range(0, 2)) = 0.9
        _BaseAlpha ("Base Microfacet Alpha", Range(0.01, 1)) = 0.1
        _VarianceGain ("Normal Variance Gain", Range(0, 128)) = 64
        [Toggle] _FilterEnabled ("Toksvig-Inspired Filter", Float) = 0
        [Enum(Shaded,0,Resultant,1,EffectiveAlpha,2)] _DebugMode ("Debug Mode", Float) = 0
    }

    SubShader
    {
        Tags
        {
            "RenderPipeline" = "UniversalPipeline"
            "RenderType" = "Opaque"
            "UniversalMaterialType" = "Unlit"
            "IgnoreProjector" = "True"
            "Queue" = "Geometry"
        }

        Pass
        {
            Name "Forward"
            Tags { "LightMode" = "UniversalForward" }
            Cull Off
            ZWrite On

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float4 _BaseColor;
                float4 _SpecularColor;
                float _NormalFrequency;
                float _NormalAmplitude;
                float _BaseAlpha;
                float _VarianceGain;
                float _FilterEnabled;
                float _DebugMode;
            CBUFFER_END

            static const float OSFR_PI = 3.14159265359;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
                float4 tangentOS : TANGENT;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
                float3 tangentWS : TEXCOORD2;
                float3 bitangentWS : TEXCOORD3;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                VertexNormalInputs normalInputs = GetVertexNormalInputs(input.normalOS, input.tangentOS);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = normalInputs.normalWS;
                output.tangentWS = normalInputs.tangentWS;
                output.bitangentWS = normalInputs.bitangentWS;
                return output;
            }

            float3 ProceduralNormal(float3 positionWS, float3 baseNormal, float3 tangent, float3 bitangent)
            {
                float phaseX = 2.0 * OSFR_PI * _NormalFrequency * positionWS.x;
                float phaseY = 2.0 * OSFR_PI * _NormalFrequency * positionWS.y;
                float2 slope = _NormalAmplitude * float2(sin(phaseX), sin(phaseY));
                return normalize(baseNormal + tangent * slope.x + bitangent * slope.y);
            }

            void FilterNormal(
                Varyings input,
                out float3 filteredNormal,
                out float resultantLength,
                out float effectiveAlpha)
            {
                float3 baseNormal = normalize(input.normalWS);
                float3 tangent = normalize(input.tangentWS);
                float3 bitangent = normalize(input.bitangentWS);
                float3 centerNormal = ProceduralNormal(input.positionWS, baseNormal, tangent, bitangent);
                filteredNormal = centerNormal;
                resultantLength = 1.0;
                effectiveAlpha = _BaseAlpha;

                if (_FilterEnabled < 0.5)
                {
                    return;
                }

                float3 footprintX = ddx(input.positionWS);
                float3 footprintY = ddy(input.positionWS);
                float3 meanNormal = 0.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 offset = float2(x, y) * 0.5;
                        float3 samplePosition = input.positionWS
                            + footprintX * offset.x
                            + footprintY * offset.y;
                        meanNormal += ProceduralNormal(samplePosition, baseNormal, tangent, bitangent);
                    }
                }

                meanNormal *= 1.0 / 9.0;
                resultantLength = saturate(length(meanNormal));
                filteredNormal = meanNormal / max(resultantLength, 0.000001);
                float normalVariance = max(0.0, 1.0 - resultantLength)
                    / max(resultantLength, 0.000001);
                effectiveAlpha = saturate(sqrt(
                    _BaseAlpha * _BaseAlpha + _VarianceGain * normalVariance));
            }

            float SmithVisibility(float nDotV, float nDotL, float alpha)
            {
                float k = (alpha + 1.0) * (alpha + 1.0) * 0.125;
                float visibilityV = nDotV / lerp(nDotV, 1.0, k);
                float visibilityL = nDotL / lerp(nDotL, 1.0, k);
                return visibilityV * visibilityL;
            }

            float3 ShadeGgx(float3 normalWS, float3 positionWS, float alpha)
            {
                float3 viewDirection = normalize(GetCameraPositionWS() - positionWS);
                float3 lightDirection = normalize(float3(-0.35, 0.55, -0.9));
                float3 halfVector = normalize(viewDirection + lightDirection);
                float nDotV = saturate(dot(normalWS, viewDirection));
                float nDotL = saturate(dot(normalWS, lightDirection));
                float nDotH = saturate(dot(normalWS, halfVector));
                float vDotH = saturate(dot(viewDirection, halfVector));
                float alpha2 = max(alpha * alpha, 0.000001);
                float denominator = nDotH * nDotH * (alpha2 - 1.0) + 1.0;
                float distribution = alpha2 / max(OSFR_PI * denominator * denominator, 0.000001);
                float visibility = SmithVisibility(nDotV, nDotL, alpha);
                float3 fresnel = _SpecularColor.rgb
                    + (1.0 - _SpecularColor.rgb) * pow(1.0 - vDotH, 5.0);
                float3 specular = distribution * visibility * fresnel
                    / max(4.0 * nDotV * nDotL, 0.000001);
                float3 diffuse = _BaseColor.rgb * (1.0 / OSFR_PI);
                return (diffuse + specular) * nDotL * 2.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS;
                float resultant;
                float alpha;
                FilterNormal(input, normalWS, resultant, alpha);
                if (_DebugMode > 1.5)
                {
                    return float4(alpha.xxx, 1.0);
                }

                if (_DebugMode > 0.5)
                {
                    return float4(resultant.xxx, 1.0);
                }

                return float4(ShadeGgx(normalWS, input.positionWS, alpha), 1.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            Cull Off
            ZWrite On
            ZTest LEqual
            ColorMask R

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthVert
            #pragma fragment DepthFrag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct DepthAttributes
            {
                float4 positionOS : POSITION;
            };

            struct DepthVaryings
            {
                float4 positionCS : SV_POSITION;
            };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 DepthFrag() : SV_Target
            {
                return 0.0;
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormalsOnly"
            Tags { "LightMode" = "DepthNormalsOnly" }
            Cull Off
            ZWrite On
            ZTest LEqual

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex DepthNormalsVert
            #pragma fragment DepthNormalsFrag
            #pragma multi_compile_fragment _ _GBUFFER_NORMALS_OCT

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/ShaderLibrary/Packing.hlsl"

            struct DepthNormalsAttributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct DepthNormalsVaryings
            {
                float4 positionCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
            };

            DepthNormalsVaryings DepthNormalsVert(DepthNormalsAttributes input)
            {
                DepthNormalsVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float4 DepthNormalsFrag(DepthNormalsVaryings input) : SV_Target
            {
                float3 normalWS = NormalizeNormalPerPixel(input.normalWS);
            #if defined(_GBUFFER_NORMALS_OCT)
                float2 octNormalWS = PackNormalOctQuadEncode(normalWS);
                float2 remappedOctNormalWS = saturate(octNormalWS * 0.5 + 0.5);
                return float4(PackFloat2To888(remappedOctNormalWS), 0.0);
            #else
                return float4(normalWS, 0.0);
            #endif
            }
            ENDHLSL
        }
    }
}
