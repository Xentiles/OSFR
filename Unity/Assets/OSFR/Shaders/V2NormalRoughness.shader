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
        _LeanVarianceGain ("LEAN-like Slope Variance Gain", Range(0, 8)) = 1
        [Enum(GeometricMeanApproximation,0,AnisotropicSmith,1)] _LeanVisibilityMode ("LEAN Visibility", Float) = 0
        [Enum(LegacyControl,0,Scalar,1,LEANLike,2)] _FilterMode ("Filter Mode", Float) = 0
        [Toggle] _FilterEnabled ("Toksvig-Inspired Filter", Float) = 0
        [Toggle] _PatternAnisotropy ("Directional Normal Pattern", Float) = 0
        _PatternAngleDegrees ("Pattern Angle", Range(0, 180)) = 35
        [Enum(Shaded,0,Resultant,1,EffectiveAlpha,2,Anisotropy,3,PrincipalAxis,4)] _DebugMode ("Debug Mode", Float) = 0
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
                float _LeanVarianceGain;
                float _LeanVisibilityMode;
                float _FilterMode;
                float _FilterEnabled;
                float _PatternAnisotropy;
                float _PatternAngleDegrees;
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

            float2 ProceduralSlope(float3 positionWS)
            {
                if (_PatternAnisotropy > 0.5)
                {
                    float angle = radians(_PatternAngleDegrees);
                    float2 direction = float2(cos(angle), sin(angle));
                    float phase = 2.0 * OSFR_PI * _NormalFrequency
                        * dot(positionWS.xy, direction);
                    return _NormalAmplitude * sin(phase) * direction;
                }

                float phaseX = 2.0 * OSFR_PI * _NormalFrequency * positionWS.x;
                float phaseY = 2.0 * OSFR_PI * _NormalFrequency * positionWS.y;
                return _NormalAmplitude * float2(sin(phaseX), sin(phaseY));
            }

            float3 ProceduralNormal(float3 positionWS, float3 baseNormal, float3 tangent, float3 bitangent)
            {
                float2 slope = ProceduralSlope(positionWS);
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

                if (_FilterEnabled < 0.5 && _FilterMode < 0.5)
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

            void FilterLeanLike(
                Varyings input,
                out float3 filteredNormal,
                out float3 principalTangent,
                out float resultantLength,
                out float alphaMajor,
                out float alphaMinor,
                out float anisotropy,
                out float2 principalAxis)
            {
                float3 baseNormal = normalize(input.normalWS);
                float3 tangent = normalize(input.tangentWS);
                float3 bitangent = normalize(input.bitangentWS);
                float3 footprintX = ddx(input.positionWS);
                float3 footprintY = ddy(input.positionWS);
                float2 firstMoment = 0.0;
                float3 secondMoment = 0.0;
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
                        float2 slope = ProceduralSlope(samplePosition);
                        firstMoment += slope;
                        secondMoment += float3(
                            slope.x * slope.x,
                            slope.y * slope.y,
                            slope.x * slope.y);
                        meanNormal += normalize(
                            baseNormal + tangent * slope.x + bitangent * slope.y);
                    }
                }

                firstMoment *= 1.0 / 9.0;
                secondMoment *= 1.0 / 9.0;
                meanNormal *= 1.0 / 9.0;
                resultantLength = saturate(length(meanNormal));
                filteredNormal = normalize(
                    baseNormal + tangent * firstMoment.x + bitangent * firstMoment.y);

                float covarianceX = max(0.0, secondMoment.x - firstMoment.x * firstMoment.x);
                float covarianceY = max(0.0, secondMoment.y - firstMoment.y * firstMoment.y);
                float covarianceLimit = sqrt(max(covarianceX * covarianceY, 0.0));
                float covarianceXY = clamp(
                    secondMoment.z - firstMoment.x * firstMoment.y,
                    -covarianceLimit,
                    covarianceLimit);
                float trace = covarianceX + covarianceY;
                float discriminant = sqrt(max(
                    (covarianceX - covarianceY) * (covarianceX - covarianceY)
                    + 4.0 * covarianceXY * covarianceXY,
                    0.0));
                float majorVariance = max(0.0, 0.5 * (trace + discriminant));
                float minorVariance = max(0.0, 0.5 * (trace - discriminant));
                float principalAngle = 0.5 * atan2(
                    2.0 * covarianceXY,
                    covarianceX - covarianceY);
                principalAxis = float2(cos(principalAngle), sin(principalAngle));
                principalTangent = tangent * principalAxis.x + bitangent * principalAxis.y;
                principalTangent = normalize(principalTangent
                    - filteredNormal * dot(principalTangent, filteredNormal));

                float baseVariance = _BaseAlpha * _BaseAlpha;
                alphaMajor = saturate(sqrt(
                    baseVariance + _LeanVarianceGain * majorVariance));
                alphaMinor = saturate(sqrt(
                    baseVariance + _LeanVarianceGain * minorVariance));
                anisotropy = 1.0 - alphaMinor / max(alphaMajor, 0.000001);
            }

            float SmithVisibility(float nDotV, float nDotL, float alpha)
            {
                float k = (alpha + 1.0) * (alpha + 1.0) * 0.125;
                float visibilityV = nDotV / lerp(nDotV, 1.0, k);
                float visibilityL = nDotL / lerp(nDotL, 1.0, k);
                return visibilityV * visibilityL;
            }

            // Height-correlated anisotropic Smith-GGX visibility. This is the
            // tangent-frame form derived by Heitz and used by Filament.
            float AnisotropicSmithVisibility(
                float3 viewDirection,
                float3 lightDirection,
                float3 normalWS,
                float3 tangentWS,
                float3 bitangentWS,
                float alphaX,
                float alphaY,
                float nDotV,
                float nDotL)
            {
                float tDotV = dot(tangentWS, viewDirection);
                float bDotV = dot(bitangentWS, viewDirection);
                float tDotL = dot(tangentWS, lightDirection);
                float bDotL = dot(bitangentWS, lightDirection);
                float lambdaV = nDotL * length(float3(
                    alphaX * tDotV,
                    alphaY * bDotV,
                    nDotV));
                float lambdaL = nDotV * length(float3(
                    alphaX * tDotL,
                    alphaY * bDotL,
                    nDotL));
                return 0.5 / max(lambdaV + lambdaL, 0.000001);
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

            float3 ShadeAnisotropicGgx(
                float3 normalWS,
                float3 tangentWS,
                float3 positionWS,
                float alphaX,
                float alphaY)
            {
                float3 bitangentWS = normalize(cross(normalWS, tangentWS));
                float3 viewDirection = normalize(GetCameraPositionWS() - positionWS);
                float3 lightDirection = normalize(float3(-0.35, 0.55, -0.9));
                float3 halfVector = normalize(viewDirection + lightDirection);
                float nDotV = saturate(dot(normalWS, viewDirection));
                float nDotL = saturate(dot(normalWS, lightDirection));
                float nDotH = saturate(dot(normalWS, halfVector));
                float tDotH = dot(tangentWS, halfVector);
                float bDotH = dot(bitangentWS, halfVector);
                float vDotH = saturate(dot(viewDirection, halfVector));
                float safeAlphaX = max(alphaX, 0.0001);
                float safeAlphaY = max(alphaY, 0.0001);
                float denominator = tDotH * tDotH / (safeAlphaX * safeAlphaX)
                    + bDotH * bDotH / (safeAlphaY * safeAlphaY)
                    + nDotH * nDotH;
                float distribution = 1.0 / max(
                    OSFR_PI * safeAlphaX * safeAlphaY * denominator * denominator,
                    0.000001);
                float visibility;
                if (_LeanVisibilityMode > 0.5)
                {
                    visibility = AnisotropicSmithVisibility(
                        viewDirection,
                        lightDirection,
                        normalWS,
                        tangentWS,
                        bitangentWS,
                        safeAlphaX,
                        safeAlphaY,
                        nDotV,
                        nDotL);
                }
                else
                {
                    float visibilityAlpha = sqrt(safeAlphaX * safeAlphaY);
                    float masking = SmithVisibility(nDotV, nDotL, visibilityAlpha);
                    visibility = masking / max(4.0 * nDotV * nDotL, 0.000001);
                }
                float3 fresnel = _SpecularColor.rgb
                    + (1.0 - _SpecularColor.rgb) * pow(1.0 - vDotH, 5.0);
                float3 specular = distribution * visibility * fresnel;
                float3 diffuse = _BaseColor.rgb * (1.0 / OSFR_PI);
                return (diffuse + specular) * nDotL * 2.0;
            }

            float4 Frag(Varyings input) : SV_Target
            {
                float3 normalWS;
                float resultant;
                float alphaMajor;
                float alphaMinor;
                float anisotropy = 0.0;
                float2 principalAxis = float2(1.0, 0.0);
                float3 principalTangent = normalize(input.tangentWS);
                float filterMode = _FilterMode;
                if (filterMode < 0.5 && _FilterEnabled > 0.5)
                {
                    filterMode = 1.0;
                }

                if (filterMode > 1.5)
                {
                    FilterLeanLike(
                        input,
                        normalWS,
                        principalTangent,
                        resultant,
                        alphaMajor,
                        alphaMinor,
                        anisotropy,
                        principalAxis);
                }
                else
                {
                    float alpha;
                    FilterNormal(input, normalWS, resultant, alpha);
                    if (filterMode < 0.5)
                    {
                        float3 baseNormal = normalize(input.normalWS);
                        normalWS = ProceduralNormal(
                            input.positionWS,
                            baseNormal,
                            normalize(input.tangentWS),
                            normalize(input.bitangentWS));
                        resultant = 1.0;
                        alpha = _BaseAlpha;
                    }

                    alphaMajor = alpha;
                    alphaMinor = alpha;
                    principalTangent = normalize(principalTangent
                        - normalWS * dot(principalTangent, normalWS));
                }

                if (_DebugMode > 1.5)
                {
                    if (_DebugMode > 3.5)
                    {
                        return float4(principalAxis * 0.5 + 0.5, anisotropy, 1.0);
                    }

                    if (_DebugMode > 2.5)
                    {
                        return float4(anisotropy.xxx, 1.0);
                    }

                    float effectiveAlpha = sqrt(alphaMajor * alphaMinor);
                    return float4(effectiveAlpha.xxx, 1.0);
                }

                if (_DebugMode > 0.5)
                {
                    return float4(resultant.xxx, 1.0);
                }

                float3 shaded = filterMode > 1.5
                    ? ShadeAnisotropicGgx(
                        normalWS,
                        principalTangent,
                        input.positionWS,
                        alphaMajor,
                        alphaMinor)
                    : ShadeGgx(normalWS, input.positionWS, alphaMajor);
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
