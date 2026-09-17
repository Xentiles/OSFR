using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for the first V2 Toksvig-inspired experiment. The length
    /// of the unnormalized mean normal is preserved as a variation signal and
    /// transferred into microfacet alpha before BRDF evaluation.
    /// </summary>
    public static class NormalRoughnessMath
    {
        public readonly struct Result
        {
            public Result(Vector3 meanNormal, float resultantLength, float variance, float effectiveAlpha)
            {
                MeanNormal = meanNormal;
                ResultantLength = resultantLength;
                Variance = variance;
                EffectiveAlpha = effectiveAlpha;
            }

            public Vector3 MeanNormal { get; }
            public float ResultantLength { get; }
            public float Variance { get; }
            public float EffectiveAlpha { get; }
            public Vector3 FilteredDirection => MeanNormal.sqrMagnitude <= 0.000000000001f
                ? Vector3.forward
                : MeanNormal.normalized;
        }

        public static Result Evaluate(
            IReadOnlyList<Vector3> normals,
            float baseAlpha,
            float varianceGain,
            bool aggressiveVariance = true)
        {
            if (normals == null)
            {
                throw new ArgumentNullException(nameof(normals));
            }

            if (normals.Count == 0)
            {
                throw new ArgumentException("At least one normal is required.", nameof(normals));
            }

            if (baseAlpha < 0.0f || baseAlpha > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(baseAlpha));
            }

            if (varianceGain < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(varianceGain));
            }

            Vector3 sum = Vector3.zero;
            for (int index = 0; index < normals.Count; index++)
            {
                Vector3 normal = normals[index];
                if (normal.sqrMagnitude <= 0.000000000001f)
                {
                    throw new ArgumentException("Normals must have non-zero length.", nameof(normals));
                }

                sum += normal.normalized;
            }

            Vector3 mean = sum / normals.Count;
            float resultant = Mathf.Clamp01(mean.magnitude);
            float variance = VarianceFromResultant(resultant, aggressiveVariance);
            return new Result(
                mean,
                resultant,
                variance,
                EffectiveAlpha(baseAlpha, variance, varianceGain));
        }

        public static float VarianceFromResultant(float resultantLength, bool aggressive)
        {
            if (resultantLength < 0.0f || resultantLength > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(resultantLength));
            }

            float lostLength = Mathf.Max(0.0f, 1.0f - resultantLength);
            return aggressive
                ? lostLength / Mathf.Max(resultantLength, 0.000001f)
                : lostLength;
        }

        public static float EffectiveAlpha(float baseAlpha, float normalVariance, float varianceGain)
        {
            if (baseAlpha < 0.0f || baseAlpha > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(baseAlpha));
            }

            if (normalVariance < 0.0f || varianceGain < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(normalVariance));
            }

            return Mathf.Clamp01(Mathf.Sqrt(
                baseAlpha * baseAlpha + varianceGain * normalVariance));
        }
    }
}
