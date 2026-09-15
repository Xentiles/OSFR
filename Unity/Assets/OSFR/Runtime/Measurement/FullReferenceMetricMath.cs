using System;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Deterministic linear-HDR full-reference metrics. PSNR uses the recorded
    /// peak RGB value of the reference rather than assuming an SDR range.
    /// </summary>
    public static class FullReferenceMetricMath
    {
        public readonly struct SpatialBand
        {
            public SpatialBand(
                int level,
                int width,
                int height,
                float errorMeanSquare,
                float candidateEnergy,
                float referenceEnergy)
            {
                Level = level;
                Width = width;
                Height = height;
                ErrorMeanSquare = errorMeanSquare;
                CandidateEnergy = candidateEnergy;
                ReferenceEnergy = referenceEnergy;
            }

            public int Level { get; }

            public int Width { get; }

            public int Height { get; }

            public float ErrorMeanSquare { get; }

            public float CandidateEnergy { get; }

            public float ReferenceEnergy { get; }
        }

        public sealed class Result
        {
            public Result(
                float referencePeak,
                float rgbMeanSquareError,
                float psnrDecibels,
                float luminanceMeanAbsoluteError,
                float luminanceRootMeanSquareError,
                float luminanceRelativeRootMeanSquareError,
                SpatialBand[] bands,
                SpatialBand residual)
            {
                ReferencePeak = referencePeak;
                RgbMeanSquareError = rgbMeanSquareError;
                PsnrDecibels = psnrDecibels;
                LuminanceMeanAbsoluteError = luminanceMeanAbsoluteError;
                LuminanceRootMeanSquareError = luminanceRootMeanSquareError;
                LuminanceRelativeRootMeanSquareError = luminanceRelativeRootMeanSquareError;
                Bands = bands;
                Residual = residual;
            }

            public float ReferencePeak { get; }

            public float RgbMeanSquareError { get; }

            public float PsnrDecibels { get; }

            public float LuminanceMeanAbsoluteError { get; }

            public float LuminanceRootMeanSquareError { get; }

            public float LuminanceRelativeRootMeanSquareError { get; }

            public SpatialBand[] Bands { get; }

            public SpatialBand Residual { get; }
        }

        public static Result Evaluate(
            Color[] candidate,
            Color[] reference,
            int width,
            int height,
            int pyramidLevels = 5)
        {
            ValidateImages(candidate, reference, width, height, pyramidLevels);

            var candidateLuminance = new float[candidate.Length];
            var referenceLuminance = new float[reference.Length];
            double rgbSquaredError = 0.0;
            double luminanceAbsoluteError = 0.0;
            double luminanceSquaredError = 0.0;
            double referenceLuminanceSquared = 0.0;
            float referencePeak = 0.0f;

            for (int index = 0; index < candidate.Length; index++)
            {
                Color actual = candidate[index];
                Color expected = reference[index];
                float redDifference = actual.r - expected.r;
                float greenDifference = actual.g - expected.g;
                float blueDifference = actual.b - expected.b;
                rgbSquaredError += redDifference * redDifference
                    + greenDifference * greenDifference
                    + blueDifference * blueDifference;
                referencePeak = Mathf.Max(
                    referencePeak,
                    Mathf.Max(Mathf.Abs(expected.r), Mathf.Max(Mathf.Abs(expected.g), Mathf.Abs(expected.b))));

                float actualLuminance = Luminance(actual);
                float expectedLuminance = Luminance(expected);
                float luminanceDifference = actualLuminance - expectedLuminance;
                candidateLuminance[index] = actualLuminance;
                referenceLuminance[index] = expectedLuminance;
                luminanceAbsoluteError += Math.Abs(luminanceDifference);
                luminanceSquaredError += luminanceDifference * luminanceDifference;
                referenceLuminanceSquared += expectedLuminance * expectedLuminance;
            }

            double inversePixelCount = 1.0 / candidate.Length;
            float rgbMse = (float)(rgbSquaredError / (3.0 * candidate.Length));
            float safePeak = Mathf.Max(referencePeak, 0.000001f);
            float psnr = rgbMse <= 0.0f
                ? float.PositiveInfinity
                : 10.0f * Mathf.Log10(safePeak * safePeak / rgbMse);
            float luminanceRmse = (float)Math.Sqrt(luminanceSquaredError * inversePixelCount);
            float referenceLuminanceRms =
                (float)Math.Sqrt(referenceLuminanceSquared * inversePixelCount);

            BuildSpatialBands(
                candidateLuminance,
                referenceLuminance,
                width,
                height,
                pyramidLevels,
                out SpatialBand[] bands,
                out SpatialBand residual);

            return new Result(
                referencePeak,
                rgbMse,
                psnr,
                (float)(luminanceAbsoluteError * inversePixelCount),
                luminanceRmse,
                luminanceRmse / Mathf.Max(referenceLuminanceRms, 0.000001f),
                bands,
                residual);
        }

        private static void BuildSpatialBands(
            float[] candidate,
            float[] reference,
            int width,
            int height,
            int pyramidLevels,
            out SpatialBand[] bands,
            out SpatialBand residual)
        {
            int bandCount = Mathf.Min(pyramidLevels - 1, MaximumDownsampleCount(width, height));
            bands = new SpatialBand[bandCount];
            float[] candidateLevel = candidate;
            float[] referenceLevel = reference;
            int levelWidth = width;
            int levelHeight = height;

            for (int level = 0; level < bandCount; level++)
            {
                int coarseWidth = Mathf.Max(1, levelWidth / 2);
                int coarseHeight = Mathf.Max(1, levelHeight / 2);
                float[] candidateCoarse = DownsampleBinomial(
                    candidateLevel,
                    levelWidth,
                    levelHeight,
                    coarseWidth,
                    coarseHeight);
                float[] referenceCoarse = DownsampleBinomial(
                    referenceLevel,
                    levelWidth,
                    levelHeight,
                    coarseWidth,
                    coarseHeight);

                bands[level] = CompareBand(
                    level,
                    candidateLevel,
                    referenceLevel,
                    levelWidth,
                    levelHeight,
                    candidateCoarse,
                    referenceCoarse,
                    coarseWidth,
                    coarseHeight);

                candidateLevel = candidateCoarse;
                referenceLevel = referenceCoarse;
                levelWidth = coarseWidth;
                levelHeight = coarseHeight;
            }

            residual = CompareResidual(
                bandCount,
                candidateLevel,
                referenceLevel,
                levelWidth,
                levelHeight);
        }

        private static SpatialBand CompareBand(
            int level,
            float[] candidate,
            float[] reference,
            int width,
            int height,
            float[] candidateCoarse,
            float[] referenceCoarse,
            int coarseWidth,
            int coarseHeight)
        {
            double errorSum = 0.0;
            double candidateEnergy = 0.0;
            double referenceEnergy = 0.0;

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float candidateBand = candidate[index]
                        - SampleBilinear(candidateCoarse, coarseWidth, coarseHeight, x, y, width, height);
                    float referenceBand = reference[index]
                        - SampleBilinear(referenceCoarse, coarseWidth, coarseHeight, x, y, width, height);
                    float difference = candidateBand - referenceBand;
                    errorSum += difference * difference;
                    candidateEnergy += candidateBand * candidateBand;
                    referenceEnergy += referenceBand * referenceBand;
                }
            }

            double inverseCount = 1.0 / (width * height);
            return new SpatialBand(
                level,
                width,
                height,
                (float)(errorSum * inverseCount),
                (float)(candidateEnergy * inverseCount),
                (float)(referenceEnergy * inverseCount));
        }

        private static SpatialBand CompareResidual(
            int level,
            float[] candidate,
            float[] reference,
            int width,
            int height)
        {
            double errorSum = 0.0;
            double candidateEnergy = 0.0;
            double referenceEnergy = 0.0;
            for (int index = 0; index < candidate.Length; index++)
            {
                float difference = candidate[index] - reference[index];
                errorSum += difference * difference;
                candidateEnergy += candidate[index] * candidate[index];
                referenceEnergy += reference[index] * reference[index];
            }

            double inverseCount = 1.0 / candidate.Length;
            return new SpatialBand(
                level,
                width,
                height,
                (float)(errorSum * inverseCount),
                (float)(candidateEnergy * inverseCount),
                (float)(referenceEnergy * inverseCount));
        }

        private static float[] DownsampleBinomial(
            float[] source,
            int width,
            int height,
            int destinationWidth,
            int destinationHeight)
        {
            var destination = new float[destinationWidth * destinationHeight];
            int[] kernel = { 1, 2, 1 };
            for (int y = 0; y < destinationHeight; y++)
            {
                int sourceY = Mathf.Min(height - 1, y * 2);
                for (int x = 0; x < destinationWidth; x++)
                {
                    int sourceX = Mathf.Min(width - 1, x * 2);
                    float sum = 0.0f;
                    for (int offsetY = -1; offsetY <= 1; offsetY++)
                    {
                        int sampleY = Mathf.Clamp(sourceY + offsetY, 0, height - 1);
                        for (int offsetX = -1; offsetX <= 1; offsetX++)
                        {
                            int sampleX = Mathf.Clamp(sourceX + offsetX, 0, width - 1);
                            sum += source[sampleY * width + sampleX]
                                * kernel[offsetX + 1]
                                * kernel[offsetY + 1];
                        }
                    }

                    destination[y * destinationWidth + x] = sum * (1.0f / 16.0f);
                }
            }

            return destination;
        }

        private static float SampleBilinear(
            float[] source,
            int width,
            int height,
            int destinationX,
            int destinationY,
            int destinationWidth,
            int destinationHeight)
        {
            float sourceX = (destinationX + 0.5f) * width / destinationWidth - 0.5f;
            float sourceY = (destinationY + 0.5f) * height / destinationHeight - 0.5f;
            int x0 = Mathf.FloorToInt(sourceX);
            int y0 = Mathf.FloorToInt(sourceY);
            float fractionX = sourceX - x0;
            float fractionY = sourceY - y0;
            int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
            int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
            x0 = Mathf.Clamp(x0, 0, width - 1);
            y0 = Mathf.Clamp(y0, 0, height - 1);

            float lower = Mathf.Lerp(source[y0 * width + x0], source[y0 * width + x1], fractionX);
            float upper = Mathf.Lerp(source[y1 * width + x0], source[y1 * width + x1], fractionX);
            return Mathf.Lerp(lower, upper, fractionY);
        }

        private static int MaximumDownsampleCount(int width, int height)
        {
            int count = 0;
            while ((width > 1 || height > 1) && count < 30)
            {
                width = Mathf.Max(1, width / 2);
                height = Mathf.Max(1, height / 2);
                count++;
            }

            return count;
        }

        private static void ValidateImages(
            Color[] candidate,
            Color[] reference,
            int width,
            int height,
            int pyramidLevels)
        {
            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (reference == null)
            {
                throw new ArgumentNullException(nameof(reference));
            }

            if (width <= 0 || height <= 0 || candidate.Length != width * height
                || reference.Length != candidate.Length)
            {
                throw new ArgumentException("Images must match the supplied positive dimensions.");
            }

            if (pyramidLevels < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(pyramidLevels));
            }
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }
    }
}
