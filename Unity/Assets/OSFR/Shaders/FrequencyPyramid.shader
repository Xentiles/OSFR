Shader "Hidden/OSFR/FrequencyPyramid"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off
        ZTest Always
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareNormalsTexture.hlsl"

        TEXTURE2D_X(_OSFRLowTexture);
        TEXTURE2D_X(_OSFRPyramidL0);
        TEXTURE2D_X(_OSFRPyramidL1);
        TEXTURE2D_X(_OSFRPyramidL2);
        TEXTURE2D_X(_OSFRPyramidL3);
        TEXTURE2D_X(_OSFRPyramidL4);
        TEXTURE2D_X(_OSFRPyramidL5);

        int _OSFRFrequencyDebugMode;
        int _OSFRPyramidLevelCount;
        int _OSFRFineBandCount;
        float _OSFRLogLuminanceK;
        float _OSFRFineEnergyScale;
        float _OSFRFrequencyRatioEpsilon;
        float4 _OSFRRenderSize;
        float4 _OSFROutputSize;
        float _OSFRRelativeDepthThreshold;
        float2 _OSFRRiskFootprintRange;
        float4 _OSFRBilateralParameters;

        static const float3 OSFR_LUMINANCE = float3(0.2126, 0.7152, 0.0722);

        struct RiskFootprint
        {
            float majorWorld;
            float minorWorld;
            float confidence;
        };

        bool IsBackgroundDepth(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth <= 0.000001;
        #else
            return rawDepth >= 0.999999;
        #endif
        }

        float DeviceDepthForReconstruction(float rawDepth)
        {
        #if UNITY_REVERSED_Z
            return rawDepth;
        #else
            return lerp(UNITY_NEAR_CLIP_VALUE, 1.0, rawDepth);
        #endif
        }

        float LinearEyeDepthOSFR(float rawDepth)
        {
            return unity_OrthoParams.w == 0.0
                ? LinearEyeDepth(rawDepth, _ZBufferParams)
                : LinearDepthToEyeDepth(rawDepth);
        }

        float3 ReconstructWorld(float2 uv, float rawDepth)
        {
            return ComputeWorldSpacePosition(
                uv,
                DeviceDepthForReconstruction(rawDepth),
                UNITY_MATRIX_I_VP);
        }

        bool IsValidNeighbor(float centerDepth, float neighborDepth, float neighborRawDepth)
        {
            float relativeDifference = abs(neighborDepth - centerDepth) / max(centerDepth, 0.000001);
            return !IsBackgroundDepth(neighborRawDepth)
                && relativeDifference < _OSFRRelativeDepthThreshold;
        }

        RiskFootprint EstimateRiskFootprint(float2 uv, float rawDepth)
        {
            float linearDepth = LinearEyeDepthOSFR(rawDepth);
            float xDirection = uv.x < 0.5 ? 1.0 : -1.0;
            float yDirection = uv.y < 0.5 ? 1.0 : -1.0;
            float2 uvX = uv + float2(xDirection * _OSFRRenderSize.z, 0.0);
            float2 uvY = uv + float2(0.0, yDirection * _OSFRRenderSize.w);

            float rawDepthX = SampleSceneDepth(uvX);
            float rawDepthY = SampleSceneDepth(uvY);
            bool validX = IsValidNeighbor(linearDepth, LinearEyeDepthOSFR(rawDepthX), rawDepthX);
            bool validY = IsValidNeighbor(linearDepth, LinearEyeDepthOSFR(rawDepthY), rawDepthY);

            float3 position = ReconstructWorld(uv, rawDepth);
            float3 positionX = ReconstructWorld(uvX, validX ? rawDepthX : rawDepth);
            float3 positionY = ReconstructWorld(uvY, validY ? rawDepthY : rawDepth);
            float3 dx = (positionX - position) * (_OSFRRenderSize.x / _OSFROutputSize.x);
            float3 dy = (positionY - position) * (_OSFRRenderSize.y / _OSFROutputSize.y);

            float a = dot(dx, dx);
            float b = dot(dx, dy);
            float c = dot(dy, dy);
            float discriminant = sqrt(max(0.0, (a - c) * (a - c) + 4.0 * b * b));

            RiskFootprint result;
            result.majorWorld = sqrt(max(0.5 * (a + c + discriminant), 0.0));
            result.minorWorld = sqrt(max(0.5 * (a + c - discriminant), 0.0));
            result.confidence = 0.5 * ((validX ? 1.0 : 0.0) + (validY ? 1.0 : 0.0));
            return result;
        }

        float FootprintRiskWeight(RiskFootprint footprint)
        {
            float start = max(_OSFRRiskFootprintRange.x, 0.0);
            float end = max(_OSFRRiskFootprintRange.y, start + 1e-6);
            float majorWeight = smoothstep(start, end, footprint.majorWorld);
            float minorWeight = smoothstep(start, end, footprint.minorWorld);
            return saturate(1.0 - (1.0 - majorWeight) * (1.0 - minorWeight));
        }

        float CompressedLuminance(float3 color)
        {
            float luminance = max(dot(color, OSFR_LUMINANCE), 0.0);
            return log(1.0 + max(_OSFRLogLuminanceK, 1e-4) * luminance);
        }

        float3 EnergyHeatMap(float energy)
        {
            float value = saturate(energy * _OSFRFineEnergyScale);
            float3 low = float3(0.02, 0.03, 0.12);
            float3 middle = float3(0.0, 0.85, 1.0);
            float3 high = float3(1.0, 0.2, 0.02);
            return value < 0.5
                ? lerp(low, middle, value * 2.0)
                : lerp(middle, high, value * 2.0 - 1.0);
        }

        float BandEnergy(float fineLuminance, float coarseLuminance)
        {
            float band = fineLuminance - coarseLuminance;
            return band * band;
        }

        float FineFrequencyRatio(float2 uv)
        {
            float luminance0 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL0, sampler_LinearClamp, uv).rgb);
            float luminance1 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL1, sampler_LinearClamp, uv).rgb);
            float luminance2 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL2, sampler_LinearClamp, uv).rgb);
            float luminance3 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL3, sampler_LinearClamp, uv).rgb);
            float luminance4 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL4, sampler_LinearClamp, uv).rgb);
            float luminance5 = CompressedLuminance(SAMPLE_TEXTURE2D_X(_OSFRPyramidL5, sampler_LinearClamp, uv).rgb);

            float energies[5];
            energies[0] = BandEnergy(luminance0, luminance1);
            energies[1] = BandEnergy(luminance1, luminance2);
            energies[2] = BandEnergy(luminance2, luminance3);
            energies[3] = BandEnergy(luminance3, luminance4);
            energies[4] = BandEnergy(luminance4, luminance5);

            int bandCount = clamp(_OSFRPyramidLevelCount - 1, 1, 5);
            int fineBandCount = clamp(_OSFRFineBandCount, 1, bandCount);
            float fineEnergy = 0.0;
            float totalEnergy = 0.0;

            [unroll]
            for (int bandIndex = 0; bandIndex < 5; bandIndex++)
            {
                if (bandIndex < bandCount)
                {
                    float energy = energies[bandIndex];
                    totalEnergy += energy;
                    fineEnergy += bandIndex < fineBandCount ? energy : 0.0;
                }
            }

            return saturate(fineEnergy / (max(_OSFRFrequencyRatioEpsilon, 1e-8) + totalEnergy));
        }

        float3 SafeNormal(float3 normal)
        {
            return normal * rsqrt(max(dot(normal, normal), 1e-8));
        }

        float4 JointBilateralFilter(
            float2 uv,
            float centerDepth,
            float3 centerNormal,
            out float depthRejection,
            out float normalRejection,
            out float normalizedWeightSum)
        {
            float spatialSigma = max(_OSFRBilateralParameters.y, 0.5);
            float depthSigma = max(_OSFRBilateralParameters.z, 1e-4);
            float normalSigma = max(_OSFRBilateralParameters.w, 1e-4);
            float inverseSpatialVariance = 0.5 / (spatialSigma * spatialSigma);
            float inverseDepthVariance = 0.5 / (depthSigma * depthSigma);

            float4 colorSum = 0.0;
            float weightSum = 0.0;
            float spatialSum = 0.0;
            float depthSupport = 0.0;
            float normalSupport = 0.0;

            [unroll]
            for (int y = -2; y <= 2; y++)
            {
                [unroll]
                for (int x = -2; x <= 2; x++)
                {
                    float2 offset = float2(x, y);
                    float2 sampleUv = uv + offset * _OSFRRenderSize.zw;
                    float spatialWeight = exp(-dot(offset, offset) * inverseSpatialVariance);
                    float neighborRawDepth = SampleSceneDepth(sampleUv);
                    float depthWeight = 0.0;
                    float normalWeight = 0.0;

                    if (!IsBackgroundDepth(neighborRawDepth))
                    {
                        float neighborDepth = LinearEyeDepthOSFR(neighborRawDepth);
                        float relativeDepth = abs(neighborDepth - centerDepth) / max(centerDepth, 1e-6);
                        depthWeight = exp(-(relativeDepth * relativeDepth) * inverseDepthVariance);

                        float3 neighborNormal = SafeNormal(SampleSceneNormals(sampleUv));
                        float normalDifference = 1.0 - max(0.0, dot(centerNormal, neighborNormal));
                        normalWeight = exp(-normalDifference / normalSigma);
                    }

                    float jointWeight = spatialWeight * depthWeight * normalWeight;
                    colorSum += SAMPLE_TEXTURE2D_X(_OSFRPyramidL0, sampler_LinearClamp, sampleUv) * jointWeight;
                    weightSum += jointWeight;
                    spatialSum += spatialWeight;
                    depthSupport += spatialWeight * depthWeight;
                    normalSupport += spatialWeight * normalWeight;
                }
            }

            float inverseSpatialSum = rcp(max(spatialSum, 1e-6));
            depthRejection = saturate(1.0 - depthSupport * inverseSpatialSum);
            normalRejection = saturate(1.0 - normalSupport * inverseSpatialSum);
            normalizedWeightSum = saturate(weightSum * inverseSpatialSum);
            return colorSum / max(weightSum, 1e-6);
        }
        ENDHLSL

        Pass
        {
            Name "Gaussian Downsample"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDownsample

            float4 FragDownsample(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
                float2 texel = _BlitTexture_TexelSize.xy;

                // A separable 3x3 binomial kernel ([1 2 1] x [1 2 1]) / 16.
                // The low pass is applied in linear HDR before every 2x downsample.
                float4 sum = 0.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2(-1.0, -1.0));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2( 0.0, -1.0)) * 2.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2( 1.0, -1.0));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2(-1.0,  0.0)) * 2.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv) * 4.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2( 1.0,  0.0)) * 2.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2(-1.0,  1.0));
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2( 0.0,  1.0)) * 2.0;
                sum += SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv + texel * float2( 1.0,  1.0));
                return sum * (1.0 / 16.0);
            }
            ENDHLSL
        }

        Pass
        {
            Name "Frequency Diagnostics"

            HLSLPROGRAM
            #pragma target 4.5
            #pragma vertex Vert
            #pragma fragment FragDiagnostics

            float4 FragDiagnostics(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);
                float4 highSample = SAMPLE_TEXTURE2D_X(_BlitTexture, sampler_LinearClamp, uv);
                float4 lowSample = SAMPLE_TEXTURE2D_X(_OSFRLowTexture, sampler_LinearClamp, uv);

                if (_OSFRFrequencyDebugMode == 11)
                {
                    return lowSample;
                }

                float band = CompressedLuminance(highSample.rgb) - CompressedLuminance(lowSample.rgb);
                float energy = band * band;

                if (_OSFRFrequencyDebugMode == 13)
                {
                    return float4(energy.xxx, 1.0);
                }

                if (_OSFRFrequencyDebugMode == 14 || _OSFRFrequencyDebugMode == 15)
                {
                    float ratio = FineFrequencyRatio(uv);
                    if (_OSFRFrequencyDebugMode == 15)
                    {
                        return float4(ratio.xxx, 1.0);
                    }

                    return float4(EnergyHeatMap(ratio / max(_OSFRFineEnergyScale, 1e-4)), 1.0);
                }

                if (_OSFRFrequencyDebugMode == 16 || _OSFRFrequencyDebugMode == 17)
                {
                    float ratio = FineFrequencyRatio(uv);
                    float rawDepth = SampleSceneDepth(uv);
                    if (IsBackgroundDepth(rawDepth))
                    {
                        return 0.0;
                    }

                    RiskFootprint footprint = EstimateRiskFootprint(uv, rawDepth);
                    float footprintWeight = FootprintRiskWeight(footprint);
                    float risk = saturate(ratio * footprintWeight * footprint.confidence);

                    if (_OSFRFrequencyDebugMode == 17)
                    {
                        return float4(risk, ratio, footprintWeight, footprint.confidence);
                    }

                    return float4(EnergyHeatMap(risk / max(_OSFRFineEnergyScale, 1e-4)), 1.0);
                }

                if (_OSFRFrequencyDebugMode >= 18 && _OSFRFrequencyDebugMode <= 21)
                {
                    float rawDepth = SampleSceneDepth(uv);
                    if (IsBackgroundDepth(rawDepth))
                    {
                        return _OSFRFrequencyDebugMode == 18 ? highSample : 0.0;
                    }

                    float ratio = FineFrequencyRatio(uv);
                    RiskFootprint footprint = EstimateRiskFootprint(uv, rawDepth);
                    float footprintWeight = FootprintRiskWeight(footprint);
                    float risk = saturate(ratio * footprintWeight * footprint.confidence);
                    float centerDepth = LinearEyeDepthOSFR(rawDepth);
                    float3 centerNormal = SafeNormal(SampleSceneNormals(uv));
                    float depthRejection;
                    float normalRejection;
                    float normalizedWeightSum;
                    float4 filtered = JointBilateralFilter(
                        uv,
                        centerDepth,
                        centerNormal,
                        depthRejection,
                        normalRejection,
                        normalizedWeightSum);

                    if (_OSFRFrequencyDebugMode == 18)
                    {
                        float blend = saturate(risk * _OSFRBilateralParameters.x);
                        return lerp(highSample, filtered, blend);
                    }

                    if (_OSFRFrequencyDebugMode == 19)
                    {
                        return float4(depthRejection.xxx, 1.0);
                    }

                    if (_OSFRFrequencyDebugMode == 20)
                    {
                        return float4(normalRejection.xxx, 1.0);
                    }

                    return float4(normalizedWeightSum.xxx, 1.0);
                }

                return float4(EnergyHeatMap(energy), 1.0);
            }
            ENDHLSL
        }
    }
}
