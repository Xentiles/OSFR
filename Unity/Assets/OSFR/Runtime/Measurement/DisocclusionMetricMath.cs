using System;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Full-reference metrics for pixels revealed behind a foreground occluder.
    /// Age zero marks a new disocclusion; positive ages follow that revealed
    /// surface through motion-compensated, depth-consistent pixels.
    /// </summary>
    public static class DisocclusionMetricMath
    {
        public readonly struct AgeBinResult
        {
            public AgeBinResult(
                int age,
                int pixelCount,
                double squaredErrorSum,
                double absoluteErrorSum,
                int historyEligiblePixelCount,
                double historyProjectionNumerator,
                double historyProjectionDenominator,
                int closerToHistoryPixelCount)
            {
                Age = age;
                PixelCount = pixelCount;
                SquaredErrorSum = squaredErrorSum;
                AbsoluteErrorSum = absoluteErrorSum;
                HistoryEligiblePixelCount = historyEligiblePixelCount;
                HistoryProjectionNumerator = historyProjectionNumerator;
                HistoryProjectionDenominator = historyProjectionDenominator;
                CloserToHistoryPixelCount = closerToHistoryPixelCount;
            }

            public int Age { get; }
            public int PixelCount { get; }
            public double SquaredErrorSum { get; }
            public double AbsoluteErrorSum { get; }
            public int HistoryEligiblePixelCount { get; }
            public double HistoryProjectionNumerator { get; }
            public double HistoryProjectionDenominator { get; }
            public int CloserToHistoryPixelCount { get; }

            public float RootMeanSquareError => PixelCount == 0
                ? 0.0f
                : (float)Math.Sqrt(SquaredErrorSum / PixelCount);

            public float MeanAbsoluteError => PixelCount == 0
                ? 0.0f
                : (float)(AbsoluteErrorSum / PixelCount);

            /// <summary>
            /// Least-squares coefficient along the old-history direction.
            /// Zero matches the current reference; one matches warped history.
            /// Values are deliberately not clamped so overshoot stays visible.
            /// </summary>
            public float HistoryRetentionCoefficient => HistoryProjectionDenominator <= 0.0
                ? 0.0f
                : (float)(HistoryProjectionNumerator / HistoryProjectionDenominator);

            public float CloserToHistoryFraction => HistoryEligiblePixelCount == 0
                ? 0.0f
                : CloserToHistoryPixelCount / (float)HistoryEligiblePixelCount;
        }

        public sealed class SequenceAccumulator
        {
            private readonly MutableBin[] m_AgeBins;
            private readonly MutableBin m_AllRecent = new MutableBin(-1);

            public SequenceAccumulator(int maximumAge)
            {
                if (maximumAge < 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(maximumAge));
                }

                m_AgeBins = new MutableBin[maximumAge + 1];
                for (int age = 0; age <= maximumAge; age++)
                {
                    m_AgeBins[age] = new MutableBin(age);
                }
            }

            public int MaximumAge => m_AgeBins.Length - 1;

            public void Add(AgeBinResult[] frameBins)
            {
                if (frameBins == null || frameBins.Length != m_AgeBins.Length)
                {
                    throw new ArgumentException("Frame bins must match the configured age range.", nameof(frameBins));
                }

                for (int age = 0; age < frameBins.Length; age++)
                {
                    if (frameBins[age].Age != age)
                    {
                        throw new ArgumentException("Frame bins must be ordered by age.", nameof(frameBins));
                    }

                    m_AgeBins[age].Add(frameBins[age]);
                    m_AllRecent.Add(frameBins[age]);
                }
            }

            public AgeBinResult[] BuildAgeSummary()
            {
                var result = new AgeBinResult[m_AgeBins.Length];
                for (int age = 0; age < result.Length; age++)
                {
                    result[age] = m_AgeBins[age].Build();
                }

                return result;
            }

            public AgeBinResult BuildAllRecentSummary()
            {
                return m_AllRecent.Build();
            }
        }

        /// <summary>
        /// Reprojects the previous age map into the current frame. A current
        /// surface that is farther than the warped previous surface is a newly
        /// revealed background pixel. Nearer surfaces are occlusions, not
        /// disocclusions, and are discarded.
        /// </summary>
        public static int[] UpdateAges(
            int[] previousAges,
            Color[] currentMotionVectors,
            Color[] previousLinearDepth,
            Color[] currentLinearDepth,
            int width,
            int height,
            int maximumAge,
            float relativeDepthThreshold = 0.02f)
        {
            int expectedLength = ValidateFrames(
                currentMotionVectors,
                previousLinearDepth,
                currentLinearDepth,
                width,
                height,
                maximumAge,
                relativeDepthThreshold);
            if (previousAges != null && previousAges.Length != expectedLength)
            {
                throw new ArgumentException("The previous age map must match the supplied dimensions.", nameof(previousAges));
            }

            var ages = new int[expectedLength];
            Array.Fill(ages, -1);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    float currentDepth = currentLinearDepth[index].r;
                    if (currentDepth <= 0.0f)
                    {
                        continue;
                    }

                    if (!TryPreviousUv(currentMotionVectors[index], x, y, width, height, out Vector2 previousUv))
                    {
                        continue;
                    }

                    float previousDepth = SampleBilinear(previousLinearDepth, width, height, previousUv).r;
                    if (previousDepth <= 0.0f)
                    {
                        continue;
                    }

                    float relativeDifference = (currentDepth - previousDepth)
                        / Mathf.Max(currentDepth, 0.000001f);
                    if (relativeDifference > relativeDepthThreshold)
                    {
                        ages[index] = 0;
                        continue;
                    }

                    if (Mathf.Abs(relativeDifference) > relativeDepthThreshold || previousAges == null)
                    {
                        continue;
                    }

                    int previousAge = SampleNearest(previousAges, width, height, previousUv);
                    if (previousAge >= 0 && previousAge < maximumAge)
                    {
                        ages[index] = previousAge + 1;
                    }
                }
            }

            return ages;
        }

        public static AgeBinResult[] EvaluateFrame(
            Color[] candidate,
            Color[] currentReference,
            Color[] previousReference,
            Color[] currentMotionVectors,
            int[] disocclusionAges,
            int width,
            int height,
            int maximumAge,
            float minimumHistoryContrast = 0.02f)
        {
            if (minimumHistoryContrast < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumHistoryContrast));
            }

            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            int expectedLength = checked(width * height);

            if (maximumAge < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAge));
            }

            if (candidate == null || currentReference == null || previousReference == null
                || currentMotionVectors == null || disocclusionAges == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            if (candidate.Length != expectedLength || currentReference.Length != expectedLength
                || previousReference.Length != expectedLength || currentMotionVectors.Length != expectedLength
                || disocclusionAges.Length != expectedLength)
            {
                throw new ArgumentException("Every frame and the age map must match the supplied dimensions.");
            }

            var bins = new MutableBin[maximumAge + 1];
            for (int age = 0; age <= maximumAge; age++)
            {
                bins[age] = new MutableBin(age);
            }

            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    int age = disocclusionAges[index];
                    if (age < 0 || age > maximumAge)
                    {
                        continue;
                    }

                    float candidateValue = Luminance(candidate[index]);
                    float currentValue = Luminance(currentReference[index]);
                    float error = candidateValue - currentValue;
                    bins[age].AddError(error);

                    // Only age zero has a directly observed occluder sample in the
                    // immediately preceding reference frame.
                    if (age != 0 || !TryPreviousUv(
                            currentMotionVectors[index], x, y, width, height, out Vector2 previousUv))
                    {
                        continue;
                    }

                    float oldValue = Luminance(SampleBilinear(previousReference, width, height, previousUv));
                    float oldDirection = oldValue - currentValue;
                    if (Mathf.Abs(oldDirection) < minimumHistoryContrast)
                    {
                        continue;
                    }

                    bins[age].AddHistory(
                        error,
                        oldDirection,
                        Mathf.Abs(candidateValue - oldValue) < Mathf.Abs(error));
                }
            }

            var results = new AgeBinResult[bins.Length];
            for (int age = 0; age < bins.Length; age++)
            {
                results[age] = bins[age].Build();
            }

            return results;
        }

        private static int ValidateFrames(
            Color[] motion,
            Color[] previousDepth,
            Color[] currentDepth,
            int width,
            int height,
            int maximumAge,
            float relativeDepthThreshold)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (maximumAge < 0 || relativeDepthThreshold < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(maximumAge));
            }

            int expectedLength = checked(width * height);
            if (motion == null || previousDepth == null || currentDepth == null)
            {
                throw new ArgumentNullException(nameof(motion));
            }

            if (motion.Length != expectedLength || previousDepth.Length != expectedLength
                || currentDepth.Length != expectedLength)
            {
                throw new ArgumentException("Every frame must match the supplied dimensions.");
            }

            return expectedLength;
        }

        private static bool TryPreviousUv(
            Color motion,
            int x,
            int y,
            int width,
            int height,
            out Vector2 previousUv)
        {
            previousUv = new Vector2(
                (x + 0.5f) / width - motion.r,
                (y + 0.5f) / height - motion.g);
            return previousUv.x >= 0.0f && previousUv.x <= 1.0f
                && previousUv.y >= 0.0f && previousUv.y <= 1.0f;
        }

        private static Color SampleBilinear(Color[] source, int width, int height, Vector2 uv)
        {
            float x = uv.x * width - 0.5f;
            float y = uv.y * height - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fractionX = x - x0;
            float fractionY = y - y0;
            int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
            int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
            x0 = Mathf.Clamp(x0, 0, width - 1);
            y0 = Mathf.Clamp(y0, 0, height - 1);
            Color lower = Color.LerpUnclamped(source[y0 * width + x0], source[y0 * width + x1], fractionX);
            Color upper = Color.LerpUnclamped(source[y1 * width + x0], source[y1 * width + x1], fractionX);
            return Color.LerpUnclamped(lower, upper, fractionY);
        }

        private static int SampleNearest(int[] source, int width, int height, Vector2 uv)
        {
            int x = Mathf.Clamp(Mathf.FloorToInt(uv.x * width), 0, width - 1);
            int y = Mathf.Clamp(Mathf.FloorToInt(uv.y * height), 0, height - 1);
            return source[y * width + x];
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }

        private sealed class MutableBin
        {
            private readonly int m_Age;
            private int m_PixelCount;
            private double m_SquaredErrorSum;
            private double m_AbsoluteErrorSum;
            private int m_HistoryEligiblePixelCount;
            private double m_HistoryProjectionNumerator;
            private double m_HistoryProjectionDenominator;
            private int m_CloserToHistoryPixelCount;

            public MutableBin(int age)
            {
                m_Age = age;
            }

            public void AddError(float error)
            {
                m_PixelCount++;
                m_SquaredErrorSum += error * error;
                m_AbsoluteErrorSum += Math.Abs(error);
            }

            public void AddHistory(float error, float oldDirection, bool closerToHistory)
            {
                m_HistoryEligiblePixelCount++;
                m_HistoryProjectionNumerator += error * oldDirection;
                m_HistoryProjectionDenominator += oldDirection * oldDirection;
                if (closerToHistory)
                {
                    m_CloserToHistoryPixelCount++;
                }
            }

            public void Add(AgeBinResult result)
            {
                m_PixelCount += result.PixelCount;
                m_SquaredErrorSum += result.SquaredErrorSum;
                m_AbsoluteErrorSum += result.AbsoluteErrorSum;
                m_HistoryEligiblePixelCount += result.HistoryEligiblePixelCount;
                m_HistoryProjectionNumerator += result.HistoryProjectionNumerator;
                m_HistoryProjectionDenominator += result.HistoryProjectionDenominator;
                m_CloserToHistoryPixelCount += result.CloserToHistoryPixelCount;
            }

            public AgeBinResult Build()
            {
                return new AgeBinResult(
                    m_Age,
                    m_PixelCount,
                    m_SquaredErrorSum,
                    m_AbsoluteErrorSum,
                    m_HistoryEligiblePixelCount,
                    m_HistoryProjectionNumerator,
                    m_HistoryProjectionDenominator,
                    m_CloserToHistoryPixelCount);
            }
        }
    }
}
