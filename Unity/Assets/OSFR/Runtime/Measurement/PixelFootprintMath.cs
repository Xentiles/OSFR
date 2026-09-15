using System;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Analytical projected-footprint calculations used to validate the GPU implementation.
    /// Distances are expressed in the caller's world-space unit (normally metres).
    /// </summary>
    public static class PixelFootprintMath
    {
        public static float VerticalWorldUnitsPerOutputPixel(
            float linearDepth,
            float verticalFieldOfViewDegrees,
            int outputHeight)
        {
            ValidateProjectionInputs(linearDepth, verticalFieldOfViewDegrees, outputHeight);

            float halfFieldOfViewRadians = 0.5f * verticalFieldOfViewDegrees * Mathf.Deg2Rad;
            return 2.0f * linearDepth * Mathf.Tan(halfFieldOfViewRadians) / outputHeight;
        }

        public static float FeatureWorldSizeForProjectedPixels(
            float projectedPixelCount,
            float linearDepth,
            float verticalFieldOfViewDegrees,
            int outputHeight)
        {
            if (!float.IsFinite(projectedPixelCount) || projectedPixelCount < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(projectedPixelCount),
                    projectedPixelCount,
                    "Projected pixel count must be finite and non-negative.");
            }

            return projectedPixelCount * VerticalWorldUnitsPerOutputPixel(
                linearDepth,
                verticalFieldOfViewDegrees,
                outputHeight);
        }

        public static float RenderPixelFootprintToOutputPixelFootprint(
            float worldUnitsPerRenderPixel,
            int renderDimension,
            int outputDimension)
        {
            if (!float.IsFinite(worldUnitsPerRenderPixel) || worldUnitsPerRenderPixel < 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(worldUnitsPerRenderPixel),
                    worldUnitsPerRenderPixel,
                    "The render-pixel footprint must be finite and non-negative.");
            }

            if (renderDimension <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(renderDimension));
            }

            if (outputDimension <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputDimension));
            }

            return worldUnitsPerRenderPixel * renderDimension / outputDimension;
        }

        private static void ValidateProjectionInputs(
            float linearDepth,
            float verticalFieldOfViewDegrees,
            int outputHeight)
        {
            if (!float.IsFinite(linearDepth) || linearDepth <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(linearDepth),
                    linearDepth,
                    "Linear depth must be finite and greater than zero.");
            }

            if (!float.IsFinite(verticalFieldOfViewDegrees)
                || verticalFieldOfViewDegrees <= 0.0f
                || verticalFieldOfViewDegrees >= 180.0f)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(verticalFieldOfViewDegrees),
                    verticalFieldOfViewDegrees,
                    "Vertical field of view must be finite and between 0 and 180 degrees.");
            }

            if (outputHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(outputHeight));
            }
        }
    }
}
