Shader "OSFR/V2 Roughness Distribution"
{
    Properties
    {
        _BaseColor ("Base Color", Color) = (0.025, 0.025, 0.025, 1)
        _SpecularColor ("Specular Color", Color) = (1, 0.82, 0.55, 1)
        _BandWidthMillimeters ("Roughness Band Width (mm)", Float) = 2
        _AlphaLow ("Low Microfacet Alpha", Range(0.01, 1)) = 0.03
        _AlphaHigh ("High Microfacet Alpha", Range(0.01, 1)) = 0.3
        _PatternAngleDegrees ("Pattern Angle", Range(0, 180)) = 25
        [Enum(Raw,0,AlphaSquaredMoment,1,DistributionMixture,2)] _FilterMode ("Filter Mode", Float) = 0
        [Enum(Shaded,0,MeanAlpha,1,Variance,2,RelativeVariance,3)] _DebugMode ("Debug Mode", Float) = 0
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
                float _BandWidthMillimeters;
                float _AlphaLow;
                float _AlphaHigh;
                float _PatternAngleDegrees;
                float _FilterMode;
                float _DebugMode;
            CBUFFER_END

            static const float OSFR_PI = 3.14159265359;

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS : TEXCOORD1;
            };

            Varyings Vert(Attributes input)
            {
                Varyings output;
                VertexPositionInputs positionInputs = GetVertexPositionInputs(input.positionOS.xyz);
                output.positionCS = positionInputs.positionCS;
                output.positionWS = positionInputs.positionWS;
                output.normalWS = TransformObjectToWorldNormal(input.normalOS);
                return output;
            }

            float ProceduralAlpha(float3 positionWS)
            {
                float angle = radians(_PatternAngleDegrees);
                float2 direction = float2(cos(angle), sin(angle));
                float bandWidth = max(_BandWidthMillimeters * 0.001, 0.000001);
                float phase = frac(dot(positionWS.xy, direction) / (2.0 * bandWidth));
                return phase < 0.5 ? _AlphaLow : _AlphaHigh;
            }

            void RoughnessMoments(
                Varyings input,
                out float meanAlpha,
                out float meanAlphaSquared,
                out float variance)
            {
                float3 footprintX = ddx(input.positionWS);
                float3 footprintY = ddy(input.positionWS);
                float first = 0.0;
                float second = 0.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 offset = float2(x, y) * 0.5;
                        float alpha = ProceduralAlpha(
                            input.positionWS + footprintX * offset.x + footprintY * offset.y);
                        first += alpha;
                        second += alpha * alpha;
                    }
                }

                meanAlpha = first / 9.0;
                meanAlphaSquared = second / 9.0;
                variance = max(0.0, meanAlphaSquared - meanAlpha * meanAlpha);
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
                float safeAlpha = max(alpha, 0.0001);
                float alphaSquared = safeAlpha * safeAlpha;
                float denominator = nDotH * nDotH * (alphaSquared - 1.0) + 1.0;
                float distribution = alphaSquared / max(
                    OSFR_PI * denominator * denominator, 0.000001);
                float visibility = SmithVisibility(nDotV, nDotL, safeAlpha);
                float3 fresnel = _SpecularColor.rgb
                    + (1.0 - _SpecularColor.rgb) * pow(1.0 - vDotH, 5.0);
                float3 specular = distribution * visibility * fresnel
                    / max(4.0 * nDotV * nDotL, 0.000001);
                float3 diffuse = _BaseColor.rgb * (1.0 / OSFR_PI);
                return (diffuse + specular) * nDotL * 2.0;
            }

            float3 ShadeDistributionMixture(Varyings input, float3 normalWS)
            {
                float3 footprintX = ddx(input.positionWS);
                float3 footprintY = ddy(input.positionWS);
                float3 shading = 0.0;
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    [unroll]
                    for (int x = -1; x <= 1; x++)
                    {
                        float2 offset = float2(x, y) * 0.5;
                        float alpha = ProceduralAlpha(
                            input.positionWS + footprintX * offset.x + footprintY * offset.y);
                        shading += ShadeGgx(normalWS, input.positionWS, alpha);
                    }
                }

                return shading / 9.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS = normalize(input.normalWS);
                float meanAlpha;
                float meanAlphaSquared;
                float variance;
                RoughnessMoments(input, meanAlpha, meanAlphaSquared, variance);

                if (_DebugMode > 0.5)
                {
                    if (_DebugMode > 2.5)
                    {
                        float relativeVariance = variance / max(meanAlphaSquared, 0.000001);
                        return float4(saturate(relativeVariance).xxx, 1.0);
                    }

                    if (_DebugMode > 1.5)
                    {
                        float range = max(abs(_AlphaHigh - _AlphaLow), 0.000001);
                        float normalizedVariance = variance / (0.25 * range * range);
                        return float4(saturate(normalizedVariance).xxx, 1.0);
                    }

                    return float4(meanAlpha.xxx, 1.0);
                }

                float3 shaded;
                if (_FilterMode > 1.5)
                {
                    shaded = ShadeDistributionMixture(input, normalWS);
                }
                else if (_FilterMode > 0.5)
                {
                    shaded = ShadeGgx(normalWS, input.positionWS, sqrt(meanAlphaSquared));
                }
                else
                {
                    shaded = ShadeGgx(
                        normalWS, input.positionWS, ProceduralAlpha(input.positionWS));
                }

                return float4(shaded, 1.0);
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

            struct DepthAttributes { float4 positionOS : POSITION; };
            struct DepthVaryings { float4 positionCS : SV_POSITION; };

            DepthVaryings DepthVert(DepthAttributes input)
            {
                DepthVaryings output;
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                return output;
            }

            float4 DepthFrag() : SV_Target { return 0.0; }
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
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

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
                float3 normalWS = normalize(input.normalWS);
                return float4(normalWS * 0.5 + 0.5, 1.0);
            }
            ENDHLSL
        }
    }
}
