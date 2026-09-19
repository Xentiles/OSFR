using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for resolution-aware roughness-distribution aggregation.
    /// The second alpha moment supplies a one-lobe approximation while the
    /// variance quantifies information lost by collapsing a distribution.
    /// </summary>
    public static class RoughnessDistributionMath
    {
        public readonly struct Result
        {
            public Result(float meanAlpha, float meanAlphaSquared, float variance)
            {
                MeanAlpha = meanAlpha;
                MeanAlphaSquared = meanAlphaSquared;
                Variance = variance;
            }

            public float MeanAlpha { get; }
            public float MeanAlphaSquared { get; }
            public float Variance { get; }
            public float MomentMatchedAlpha => Mathf.Sqrt(MeanAlphaSquared);
            public float RelativeVariance => Variance / Mathf.Max(MeanAlphaSquared, 0.000001f);
        }

        public static Result Evaluate(IReadOnlyList<float> alphas)
        {
            if (alphas == null)
            {
                throw new ArgumentNullException(nameof(alphas));
            }

            if (alphas.Count == 0)
            {
                throw new ArgumentException(
                    "At least one roughness sample is required.", nameof(alphas));
            }

            float first = 0.0f;
            float second = 0.0f;
            for (int index = 0; index < alphas.Count; index++)
            {
                float alpha = alphas[index];
                if (!float.IsFinite(alpha) || alpha <= 0.0f || alpha > 1.0f)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(alphas), "Microfacet alpha must be finite and in (0, 1].");
                }

                first += alpha;
                second += alpha * alpha;
            }

            float inverseCount = 1.0f / alphas.Count;
            float mean = first * inverseCount;
            float meanSquared = second * inverseCount;
            return new Result(
                mean,
                meanSquared,
                Mathf.Max(0.0f, meanSquared - mean * mean));
        }
    }
}
