Shader "Hidden/OSFR/MeasurementDebug"
{
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite Off
        ZTest Always

        Pass
        {
            Name "OSFR Measurement Debug"

            HLSLPROGRAM
            #pragma target 3.5
            #pragma vertex Vert
            #pragma fragment Frag

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

            TEXTURE2D_X(_MotionVectorTexture);

            int _OSFRDebugMode;
            float4 _OSFRRenderSize;
            float4 _OSFROutputSize;
            float _OSFRLinearDepthRange;
            float2 _OSFRFootprintRange;
            float _OSFRRelativeDepthThreshold;
            float _OSFRWorldPositionPeriod;

            struct PixelFootprint
            {
                float majorWorld;
                float minorWorld;
                float areaWorld2;
                float anisotropy;
                float validity;
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

            PixelFootprint EstimateFootprint(float2 uv, float rawDepth, float linearDepth)
            {
                float xDirection = uv.x < 0.5 ? 1.0 : -1.0;
                float yDirection = uv.y < 0.5 ? 1.0 : -1.0;
                float2 uvX = uv + float2(xDirection * _OSFRRenderSize.z, 0.0);
                float2 uvY = uv + float2(0.0, yDirection * _OSFRRenderSize.w);

                float rawDepthX = SampleSceneDepth(uvX);
                float rawDepthY = SampleSceneDepth(uvY);
                float depthX = LinearEyeDepthOSFR(rawDepthX);
                float depthY = LinearEyeDepthOSFR(rawDepthY);
                bool validX = IsValidNeighbor(linearDepth, depthX, rawDepthX);
                bool validY = IsValidNeighbor(linearDepth, depthY, rawDepthY);

                float3 position = ReconstructWorld(uv, rawDepth);
                float3 positionX = ReconstructWorld(uvX, validX ? rawDepthX : rawDepth);
                float3 positionY = ReconstructWorld(uvY, validY ? rawDepthY : rawDepth);
                float3 dx = (positionX - position) * (_OSFRRenderSize.x / _OSFROutputSize.x);
                float3 dy = (positionY - position) * (_OSFRRenderSize.y / _OSFROutputSize.y);

                float a = dot(dx, dx);
                float b = dot(dx, dy);
                float c = dot(dy, dy);
                float discriminant = sqrt(max(0.0, (a - c) * (a - c) + 4.0 * b * b));
                float lambdaMaximum = 0.5 * (a + c + discriminant);
                float lambdaMinimum = 0.5 * (a + c - discriminant);

                PixelFootprint result;
                result.majorWorld = sqrt(max(lambdaMaximum, 0.0));
                result.minorWorld = sqrt(max(lambdaMinimum, 0.0));
                result.areaWorld2 = length(cross(dx, dy));
                result.anisotropy = result.majorWorld / max(result.minorWorld, 0.000001);
                result.validity = 0.5 * ((validX ? 1.0 : 0.0) + (validY ? 1.0 : 0.0));
                return result;
            }

            float3 FootprintColor(float worldUnits)
            {
                float minimumLog = log2(max(_OSFRFootprintRange.x, 0.000001));
                float maximumLog = log2(max(_OSFRFootprintRange.y, _OSFRFootprintRange.x + 0.000001));
                float value = saturate((log2(max(worldUnits, 0.000001)) - minimumLog) / (maximumLog - minimumLog));

                float3 blue = float3(0.05, 0.15, 1.0);
                float3 cyan = float3(0.0, 0.9, 0.8);
                float3 yellow = float3(1.0, 0.9, 0.05);
                float3 red = float3(1.0, 0.05, 0.02);
                return value < 0.5
                    ? lerp(blue, cyan, value * 2.0)
                    : (value < 0.8
                        ? lerp(cyan, yellow, (value - 0.5) / 0.3)
                        : lerp(yellow, red, (value - 0.8) / 0.2));
            }

            float4 Frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                float2 uv = UnityStereoTransformScreenSpaceTex(input.texcoord);

                if (_OSFRDebugMode == 22)
                {
                    float2 motion = SAMPLE_TEXTURE2D_X_LOD(
                        _MotionVectorTexture,
                        sampler_PointClamp,
                        uv,
                        0).xy;
                    return float4(motion, 0.0, 1.0);
                }

                float rawDepth = SampleSceneDepth(uv);

                if (_OSFRDebugMode == 0)
                {
                    return float4(rawDepth.xxx, 1.0);
                }

                if (IsBackgroundDepth(rawDepth))
                {
                    return float4(0.0, 0.0, 0.0, 1.0);
                }

                float linearDepth = LinearEyeDepthOSFR(rawDepth);

                if (_OSFRDebugMode == 10)
                {
                    return float4(linearDepth.xxx, 1.0);
                }

                if (_OSFRDebugMode == 1)
                {
                    return float4(saturate(linearDepth / _OSFRLinearDepthRange).xxx, 1.0);
                }

                float3 worldPosition = ReconstructWorld(uv, rawDepth);
                if (_OSFRDebugMode == 9)
                {
                    return float4(worldPosition, 1.0);
                }

                if (_OSFRDebugMode == 2)
                {
                    return float4(frac(worldPosition / _OSFRWorldPositionPeriod), 1.0);
                }

                PixelFootprint footprint = EstimateFootprint(uv, rawDepth, linearDepth);
                if (_OSFRDebugMode == 3)
                {
                    return float4(FootprintColor(footprint.majorWorld), 1.0);
                }

                if (_OSFRDebugMode == 4)
                {
                    return float4(FootprintColor(footprint.minorWorld), 1.0);
                }

                if (_OSFRDebugMode == 5)
                {
                    return float4(FootprintColor(sqrt(max(footprint.areaWorld2, 0.0))), 1.0);
                }

                if (_OSFRDebugMode == 6)
                {
                    float anisotropy = saturate(log2(max(footprint.anisotropy, 1.0)) / 4.0);
                    return float4(anisotropy, 1.0 - anisotropy, 0.0, 1.0);
                }

                if (_OSFRDebugMode == 8)
                {
                    return float4(
                        footprint.majorWorld,
                        footprint.minorWorld,
                        footprint.areaWorld2,
                        footprint.validity);
                }

                float3 invalid = float3(1.0, 0.05, 0.02);
                float3 partial = float3(1.0, 0.65, 0.0);
                float3 valid = float3(0.0, 0.8, 0.2);
                return float4(footprint.validity < 0.25 ? invalid : (footprint.validity < 0.75 ? partial : valid), 1.0);
            }
            ENDHLSL
        }
    }
}
