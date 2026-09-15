using System;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Projection and convergence helpers shared by the tiled reference renderer and tests.
    /// </summary>
    public static class SupersampleReferenceMath
    {
        public readonly struct LuminanceComparison
        {
            public LuminanceComparison(
                float meanAbsoluteError,
                float rootMeanSquareError,
                float maximumAbsoluteError,
                float relativeRootMeanSquareError)
            {
                MeanAbsoluteError = meanAbsoluteError;
                RootMeanSquareError = rootMeanSquareError;
                MaximumAbsoluteError = maximumAbsoluteError;
                RelativeRootMeanSquareError = relativeRootMeanSquareError;
            }

            public float MeanAbsoluteError { get; }

            public float RootMeanSquareError { get; }

            public float MaximumAbsoluteError { get; }

            public float RelativeRootMeanSquareError { get; }
        }

        public static Matrix4x4 BuildTileProjection(
            Matrix4x4 fullProjection,
            RectInt tile,
            int fullWidth,
            int fullHeight)
        {
            if (fullWidth <= 0 || fullHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(fullWidth), "Full dimensions must be positive.");
            }

            if (tile.width <= 0 || tile.height <= 0
                || tile.x < 0 || tile.y < 0
                || tile.xMax > fullWidth || tile.yMax > fullHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(tile), "Tile must have positive area inside the full image.");
            }

            float halfSpanX = tile.width / (float)fullWidth;
            float halfSpanY = tile.height / (float)fullHeight;
            float centerX = 2.0f * (tile.x + 0.5f * tile.width) / fullWidth - 1.0f;
            float centerY = 2.0f * (tile.y + 0.5f * tile.height) / fullHeight - 1.0f;

            Matrix4x4 tileProjection = fullProjection;
            tileProjection.SetRow(0, (fullProjection.GetRow(0) - centerX * fullProjection.GetRow(3)) / halfSpanX);
            tileProjection.SetRow(1, (fullProjection.GetRow(1) - centerY * fullProjection.GetRow(3)) / halfSpanY);
            return tileProjection;
        }

        public static LuminanceComparison CompareLuminance(Color[] candidate, Color[] reference)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (candidate.Length == 0 || candidate.Length != reference.Length)
            {
                throw new ArgumentException("Images must be non-empty and have matching pixel counts.");
            }

            double absoluteSum = 0.0;
            double squaredSum = 0.0;
            double referenceSquaredSum = 0.0;
            float maximumAbsolute = 0.0f;

            for (int index = 0; index < candidate.Length; index++)
            {
                float candidateLuminance = Luminance(candidate[index]);
                float referenceLuminance = Luminance(reference[index]);
                float difference = candidateLuminance - referenceLuminance;
                float absolute = Mathf.Abs(difference);
                absoluteSum += absolute;
                squaredSum += difference * difference;
                referenceSquaredSum += referenceLuminance * referenceLuminance;
                maximumAbsolute = Mathf.Max(maximumAbsolute, absolute);
            }

            double inverseCount = 1.0 / candidate.Length;
            float rmse = (float)Math.Sqrt(squaredSum * inverseCount);
            float referenceRms = (float)Math.Sqrt(referenceSquaredSum * inverseCount);
            float relativeRmse = rmse / Mathf.Max(referenceRms, 0.000001f);
            return new LuminanceComparison(
                (float)(absoluteSum * inverseCount),
                rmse,
                maximumAbsolute,
                relativeRmse);
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }
    }
}
