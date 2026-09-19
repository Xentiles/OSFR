using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// CPU reference for the LEAN-like V2 slope-moment experiment. First and
    /// second slope moments form a positive-semidefinite 2x2 covariance whose
    /// eigenvalues broaden the two principal microfacet roughness axes.
    /// </summary>
    public static class NormalSlopeMomentMath
    {
        public readonly struct Result
        {
            public Result(
                Vector2 meanSlope,
                Vector3 secondMoment,
                Vector3 covariance,
                float majorVariance,
                float minorVariance,
                float principalAngleRadians,
                float alphaMajor,
                float alphaMinor)
            {
                MeanSlope = meanSlope;
                SecondMoment = secondMoment;
                Covariance = covariance;
                MajorVariance = majorVariance;
                MinorVariance = minorVariance;
                PrincipalAngleRadians = principalAngleRadians;
                AlphaMajor = alphaMajor;
                AlphaMinor = alphaMinor;
            }

            public Vector2 MeanSlope { get; }

            /// <summary>XX, YY, and XY second moments.</summary>
            public Vector3 SecondMoment { get; }

            /// <summary>XX, YY, and XY covariance entries.</summary>
            public Vector3 Covariance { get; }

            public float MajorVariance { get; }
            public float MinorVariance { get; }
            public float PrincipalAngleRadians { get; }
            public float AlphaMajor { get; }
            public float AlphaMinor { get; }
            public float Anisotropy => 1.0f - AlphaMinor / Mathf.Max(AlphaMajor, 0.000001f);
        }

        public static Result Evaluate(
            IReadOnlyList<Vector2> slopes,
            float baseAlpha,
            float varianceGain = 1.0f)
        {
            if (slopes == null)
            {
                throw new ArgumentNullException(nameof(slopes));
            }

            if (slopes.Count == 0)
            {
                throw new ArgumentException("At least one slope sample is required.", nameof(slopes));
            }

            if (!IsFinite(baseAlpha) || baseAlpha < 0.0f || baseAlpha > 1.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(baseAlpha));
            }

            if (!IsFinite(varianceGain) || varianceGain < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(varianceGain));
            }

            Vector2 first = Vector2.zero;
            Vector3 second = Vector3.zero;
            for (int index = 0; index < slopes.Count; index++)
            {
                Vector2 slope = slopes[index];
                if (!IsFinite(slope.x) || !IsFinite(slope.y))
                {
                    throw new ArgumentException("Slope samples must be finite.", nameof(slopes));
                }

                first += slope;
                second += new Vector3(
                    slope.x * slope.x,
                    slope.y * slope.y,
                    slope.x * slope.y);
            }

            float inverseCount = 1.0f / slopes.Count;
            Vector2 mean = first * inverseCount;
            second *= inverseCount;
            float covarianceX = Mathf.Max(0.0f, second.x - mean.x * mean.x);
            float covarianceY = Mathf.Max(0.0f, second.y - mean.y * mean.y);
            float covarianceLimit = Mathf.Sqrt(covarianceX * covarianceY);
            float covarianceXY = Mathf.Clamp(
                second.z - mean.x * mean.y,
                -covarianceLimit,
                covarianceLimit);

            float trace = covarianceX + covarianceY;
            float discriminant = Mathf.Sqrt(
                Mathf.Max(0.0f,
                    (covarianceX - covarianceY) * (covarianceX - covarianceY)
                    + 4.0f * covarianceXY * covarianceXY));
            float majorVariance = Mathf.Max(0.0f, 0.5f * (trace + discriminant));
            float minorVariance = Mathf.Max(0.0f, 0.5f * (trace - discriminant));
            float angle = 0.5f * Mathf.Atan2(2.0f * covarianceXY, covarianceX - covarianceY);
            float baseVariance = baseAlpha * baseAlpha;
            float alphaMajor = Mathf.Clamp01(Mathf.Sqrt(
                baseVariance + varianceGain * majorVariance));
            float alphaMinor = Mathf.Clamp01(Mathf.Sqrt(
                baseVariance + varianceGain * minorVariance));

            return new Result(
                mean,
                second,
                new Vector3(covarianceX, covarianceY, covarianceXY),
                majorVariance,
                minorVariance,
                angle,
                alphaMajor,
                alphaMinor);
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
