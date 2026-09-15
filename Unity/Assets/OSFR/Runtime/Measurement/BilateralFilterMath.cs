using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for the V1 joint bilateral kernel weights.
    /// </summary>
    public static class BilateralFilterMath
    {
        public static float SpatialWeight(Vector2 offsetPixels, float sigmaPixels)
        {
            float sigma = Mathf.Max(0.5f, sigmaPixels);
            return Mathf.Exp(-offsetPixels.sqrMagnitude / (2.0f * sigma * sigma));
        }

        public static float RelativeDepthWeight(float centerEyeDepth, float neighborEyeDepth, float sigma)
        {
            float safeCenter = Mathf.Max(centerEyeDepth, 0.000001f);
            float safeSigma = Mathf.Max(sigma, 0.0001f);
            float difference = Mathf.Abs(neighborEyeDepth - centerEyeDepth) / safeCenter;
            return Mathf.Exp(-(difference * difference) / (2.0f * safeSigma * safeSigma));
        }

        public static float NormalWeight(Vector3 centerNormal, Vector3 neighborNormal, float sigma)
        {
            float safeSigma = Mathf.Max(sigma, 0.0001f);
            float dot = Mathf.Max(0.0f, Vector3.Dot(centerNormal.normalized, neighborNormal.normalized));
            return Mathf.Exp(-(1.0f - dot) / safeSigma);
        }

        public static float JointWeight(
            Vector2 offsetPixels,
            float centerEyeDepth,
            float neighborEyeDepth,
            Vector3 centerNormal,
            Vector3 neighborNormal,
            float spatialSigmaPixels,
            float relativeDepthSigma,
            float normalSigma)
        {
            return SpatialWeight(offsetPixels, spatialSigmaPixels)
                * RelativeDepthWeight(centerEyeDepth, neighborEyeDepth, relativeDepthSigma)
                * NormalWeight(centerNormal, neighborNormal, normalSigma);
        }
    }
}
