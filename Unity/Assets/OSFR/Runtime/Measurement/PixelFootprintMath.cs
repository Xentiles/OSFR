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

        /// <summary>
        /// Calculates the principal axes of the 3D Jacobian formed by two neighboring
        /// world-position differences. Inputs must already represent one output pixel.
        /// </summary>
        public static PixelFootprint FromWorldDerivatives(Vector3 dx, Vector3 dy)
        {
            if (!IsFinite(dx))
            {
                throw new ArgumentOutOfRangeException(nameof(dx), "The X derivative must be finite.");
            }

            if (!IsFinite(dy))
            {
                throw new ArgumentOutOfRangeException(nameof(dy), "The Y derivative must be finite.");
            }

            float a = Vector3.Dot(dx, dx);
            float b = Vector3.Dot(dx, dy);
            float c = Vector3.Dot(dy, dy);
            float discriminant = Mathf.Sqrt(Mathf.Max(0.0f, (a - c) * (a - c) + 4.0f * b * b));
            float lambdaMaximum = 0.5f * (a + c + discriminant);
            float lambdaMinimum = 0.5f * (a + c - discriminant);
            float major = Mathf.Sqrt(Mathf.Max(lambdaMaximum, 0.0f));
            float minor = Mathf.Sqrt(Mathf.Max(lambdaMinimum, 0.0f));

            return new PixelFootprint(
                major,
                minor,
                Vector3.Cross(dx, dy).magnitude,
                major / Mathf.Max(minor, 1e-6f));
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

        private static bool IsFinite(Vector3 value)
        {
            return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
        }
    }
}
