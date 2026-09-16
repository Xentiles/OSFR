using System;
using System.Collections.Generic;
using UnityEngine;

namespace OSFR.Measurement
{
    /// <summary>
    /// Full-reference temporal metrics for deterministic linear-HDR sequences.
    /// Unity motion vectors are interpreted as forward UV offsets, so the
    /// previous frame is sampled at currentUv - motionUv.
    /// </summary>
    public static class TemporalMetricMath
    {
        public readonly struct FrameResult
        {
            public FrameResult(
                int pixelCount,
                double squaredErrorSum,
                float rootMeanSquareError,
                float meanSignedError,
                float meanAbsoluteError)
            {
                PixelCount = pixelCount;
                SquaredErrorSum = squaredErrorSum;
                RootMeanSquareError = rootMeanSquareError;
                MeanSignedError = meanSignedError;
                MeanAbsoluteError = meanAbsoluteError;
            }

            public int PixelCount { get; }

            public double SquaredErrorSum { get; }

            public float RootMeanSquareError { get; }

            public float MeanSignedError { get; }

            public float MeanAbsoluteError { get; }
        }

        public readonly struct TransitionResult
        {
            public TransitionResult(
                int candidatePixelCount,
                int validPixelCount,
                double squaredErrorSum,
                float rootMeanSquareError)
            {
                CandidatePixelCount = candidatePixelCount;
                ValidPixelCount = validPixelCount;
                SquaredErrorSum = squaredErrorSum;
                RootMeanSquareError = rootMeanSquareError;
            }

            public int CandidatePixelCount { get; }

            public int ValidPixelCount { get; }

            public double SquaredErrorSum { get; }

            public float RootMeanSquareError { get; }

            public float ValidFraction => CandidatePixelCount == 0
                ? 0.0f
                : ValidPixelCount / (float)CandidatePixelCount;
        }

        public readonly struct SpectrumResult
        {
            public SpectrumResult(float bandPower, float totalAlternatingPower)
            {
                BandPower = bandPower;
                TotalAlternatingPower = totalAlternatingPower;
            }

            public float BandPower { get; }

            public float TotalAlternatingPower { get; }

            public float BandFraction => TotalAlternatingPower <= 0.0f
                ? 0.0f
                : BandPower / TotalAlternatingPower;
        }

        public readonly struct SequenceSummary
        {
            public SequenceSummary(
                int frameCount,
                int transitionCount,
                float residualRootMeanSquare,
                float temporalRootMeanSquare,
                float validTemporalFraction,
                SpectrumResult shimmerSpectrum)
            {
                FrameCount = frameCount;
                TransitionCount = transitionCount;
                ResidualRootMeanSquare = residualRootMeanSquare;
                TemporalRootMeanSquare = temporalRootMeanSquare;
                ValidTemporalFraction = validTemporalFraction;
                ShimmerSpectrum = shimmerSpectrum;
            }

            public int FrameCount { get; }

            public int TransitionCount { get; }

            public float ResidualRootMeanSquare { get; }

            public float TemporalRootMeanSquare { get; }

            public float ValidTemporalFraction { get; }

            public SpectrumResult ShimmerSpectrum { get; }
        }

        public sealed class SequenceAccumulator
        {
            private readonly List<float> m_FrameResidualEnvelope = new List<float>();
            private double m_FrameSquaredErrorSum;
            private long m_FramePixelCount;
            private double m_TransitionSquaredErrorSum;
            private long m_ValidTransitionPixelCount;
            private long m_CandidateTransitionPixelCount;
            private int m_TransitionCount;

            public void AddFrame(FrameResult frame)
            {
                m_FrameSquaredErrorSum += frame.SquaredErrorSum;
                m_FramePixelCount += frame.PixelCount;
                m_FrameResidualEnvelope.Add(frame.RootMeanSquareError);
            }

            public void AddTransition(TransitionResult transition)
            {
                m_TransitionSquaredErrorSum += transition.SquaredErrorSum;
                m_ValidTransitionPixelCount += transition.ValidPixelCount;
                m_CandidateTransitionPixelCount += transition.CandidatePixelCount;
                m_TransitionCount++;
            }

            public SequenceSummary BuildSummary(
                float sampleRate,
                float shimmerMinimumHz = 2.0f,
                float shimmerMaximumHz = 30.0f)
            {
                float residualRms = m_FramePixelCount == 0
                    ? 0.0f
                    : (float)Math.Sqrt(m_FrameSquaredErrorSum / m_FramePixelCount);
                float temporalRms = m_ValidTransitionPixelCount == 0
                    ? 0.0f
                    : (float)Math.Sqrt(m_TransitionSquaredErrorSum / m_ValidTransitionPixelCount);
                float validFraction = m_CandidateTransitionPixelCount == 0
                    ? 0.0f
                    : m_ValidTransitionPixelCount / (float)m_CandidateTransitionPixelCount;

                return new SequenceSummary(
                    m_FrameResidualEnvelope.Count,
                    m_TransitionCount,
                    residualRms,
                    temporalRms,
                    validFraction,
                    EvaluateSpectrum(
                        m_FrameResidualEnvelope,
                        sampleRate,
                        shimmerMinimumHz,
                        shimmerMaximumHz));
            }
        }

        public static FrameResult EvaluateFrame(Color[] candidate, Color[] reference)
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
                throw new ArgumentException("Candidate and reference frames must have the same non-zero length.");
            }

            double squaredError = 0.0;
            double signedError = 0.0;
            double absoluteError = 0.0;
            for (int index = 0; index < candidate.Length; index++)
            {
                float error = Luminance(candidate[index]) - Luminance(reference[index]);
                squaredError += error * error;
                signedError += error;
                absoluteError += Math.Abs(error);
            }

            double inverseCount = 1.0 / candidate.Length;
            return new FrameResult(
                candidate.Length,
                squaredError,
                (float)Math.Sqrt(squaredError * inverseCount),
                (float)(signedError * inverseCount),
                (float)(absoluteError * inverseCount));
        }

        public static TransitionResult EvaluateMotionCompensatedTransition(
            Color[] previousCandidate,
            Color[] currentCandidate,
            Color[] previousReference,
            Color[] currentReference,
            Color[] currentMotionVectors,
            Color[] previousLinearDepth,
            Color[] currentLinearDepth,
            int width,
            int height,
            float relativeDepthThreshold = 0.02f)
        {
            int expectedLength = ValidateTransitionFrames(
                previousCandidate,
                currentCandidate,
                previousReference,
                currentReference,
                currentMotionVectors,
                previousLinearDepth,
                currentLinearDepth,
                width,
                height,
                relativeDepthThreshold);

            int validCount = 0;
            double squaredError = 0.0;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    Vector2 motion = new Vector2(
                        currentMotionVectors[index].r,
                        currentMotionVectors[index].g);
                    float currentU = (x + 0.5f) / width;
                    float currentV = (y + 0.5f) / height;
                    float previousU = currentU - motion.x;
                    float previousV = currentV - motion.y;
                    if (previousU < 0.0f || previousU > 1.0f
                        || previousV < 0.0f || previousV > 1.0f)
                    {
                        continue;
                    }

                    float currentDepth = currentLinearDepth[index].r;
                    float previousDepth = SampleBilinear(
                        previousLinearDepth,
                        width,
                        height,
                        previousU,
                        previousV).r;
                    if (currentDepth <= 0.0f || previousDepth <= 0.0f)
                    {
                        continue;
                    }

                    float relativeDepthDifference = Mathf.Abs(currentDepth - previousDepth)
                        / Mathf.Max(currentDepth, 0.000001f);
                    if (relativeDepthDifference > relativeDepthThreshold)
                    {
                        continue;
                    }

                    float candidateDelta = Luminance(currentCandidate[index])
                        - Luminance(SampleBilinear(
                            previousCandidate,
                            width,
                            height,
                            previousU,
                            previousV));
                    float referenceDelta = Luminance(currentReference[index])
                        - Luminance(SampleBilinear(
                            previousReference,
                            width,
                            height,
                            previousU,
                            previousV));
                    float temporalError = candidateDelta - referenceDelta;
                    squaredError += temporalError * temporalError;
                    validCount++;
                }
            }

            return new TransitionResult(
                expectedLength,
                validCount,
                squaredError,
                validCount == 0 ? 0.0f : (float)Math.Sqrt(squaredError / validCount));
        }

        /// <summary>
        /// Builds forward UV motion for a static scene by projecting the current
        /// frame's reconstructed world positions through the previous camera.
        /// This deterministic path avoids relying on engine frame-history updates
        /// when an editor capture renders several cameras in one editor frame.
        /// </summary>
        public static Color[] BuildStaticCameraMotionVectors(
            Color[] currentWorldPositions,
            Color[] currentLinearDepth,
            Matrix4x4 previousGpuViewProjection,
            int width,
            int height)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            int expectedLength = checked(width * height);
            if (currentWorldPositions == null || currentLinearDepth == null)
            {
                throw new ArgumentNullException(nameof(currentWorldPositions));
            }

            if (currentWorldPositions.Length != expectedLength
                || currentLinearDepth.Length != expectedLength)
            {
                throw new ArgumentException("World-position and depth frames must match the supplied dimensions.");
            }

            var motion = new Color[expectedLength];
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = y * width + x;
                    if (currentLinearDepth[index].r <= 0.0f)
                    {
                        motion[index] = Color.clear;
                        continue;
                    }

                    Color position = currentWorldPositions[index];
                    Vector4 previousClip = previousGpuViewProjection
                        * new Vector4(position.r, position.g, position.b, 1.0f);
                    if (previousClip.w <= 0.000001f)
                    {
                        motion[index] = new Color(2.0f, 2.0f, 0.0f, 1.0f);
                        continue;
                    }

                    Vector2 previousUv = new Vector2(
                        previousClip.x / previousClip.w,
                        previousClip.y / previousClip.w) * 0.5f + Vector2.one * 0.5f;
                    Vector2 currentUv = new Vector2(
                        (x + 0.5f) / width,
                        (y + 0.5f) / height);
                    Vector2 forwardMotion = currentUv - previousUv;
                    motion[index] = new Color(forwardMotion.x, forwardMotion.y, 0.0f, 1.0f);
                }
            }

            return motion;
        }

        public static SpectrumResult EvaluateSpectrum(
            IReadOnlyList<float> samples,
            float sampleRate,
            float minimumHz,
            float maximumHz)
        {
            if (samples == null)
            {
                throw new ArgumentNullException(nameof(samples));
            }

            if (sampleRate <= 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            }

            float nyquist = sampleRate * 0.5f;
            if (minimumHz < 0.0f || maximumHz < minimumHz || maximumHz > nyquist + 0.0001f)
            {
                throw new ArgumentOutOfRangeException(nameof(minimumHz));
            }

            int count = samples.Count;
            if (count < 2)
            {
                return new SpectrumResult(0.0f, 0.0f);
            }

            double mean = 0.0;
            for (int index = 0; index < count; index++)
            {
                mean += samples[index];
            }

            mean /= count;
            double bandPower = 0.0;
            double totalPower = 0.0;
            int maximumBin = count / 2;
            for (int bin = 1; bin <= maximumBin; bin++)
            {
                double real = 0.0;
                double imaginary = 0.0;
                for (int sample = 0; sample < count; sample++)
                {
                    double angle = -2.0 * Math.PI * bin * sample / count;
                    double centered = samples[sample] - mean;
                    real += centered * Math.Cos(angle);
                    imaginary += centered * Math.Sin(angle);
                }

                double power = (real * real + imaginary * imaginary) / (count * count);
                if (bin * 2 < count)
                {
                    power *= 2.0;
                }

                float frequency = bin * sampleRate / count;
                totalPower += power;
                if (frequency >= minimumHz && frequency <= maximumHz)
                {
                    bandPower += power;
                }
            }

            return new SpectrumResult((float)bandPower, (float)totalPower);
        }

        private static int ValidateTransitionFrames(
            Color[] previousCandidate,
            Color[] currentCandidate,
            Color[] previousReference,
            Color[] currentReference,
            Color[] currentMotionVectors,
            Color[] previousLinearDepth,
            Color[] currentLinearDepth,
            int width,
            int height,
            float relativeDepthThreshold)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width));
            }

            if (relativeDepthThreshold < 0.0f)
            {
                throw new ArgumentOutOfRangeException(nameof(relativeDepthThreshold));
            }

            int expectedLength = checked(width * height);
            Color[][] frames =
            {
                previousCandidate,
                currentCandidate,
                previousReference,
                currentReference,
                currentMotionVectors,
                previousLinearDepth,
                currentLinearDepth
            };
            foreach (Color[] frame in frames)
            {
                if (frame == null)
                {
                    throw new ArgumentNullException(nameof(frames), "Transition frames cannot be null.");
                }

                if (frame.Length != expectedLength)
                {
                    throw new ArgumentException("Every transition frame must match the supplied dimensions.");
                }
            }

            return expectedLength;
        }

        private static Color SampleBilinear(
            Color[] source,
            int width,
            int height,
            float u,
            float v)
        {
            float x = u * width - 0.5f;
            float y = v * height - 0.5f;
            int x0 = Mathf.FloorToInt(x);
            int y0 = Mathf.FloorToInt(y);
            float fractionX = x - x0;
            float fractionY = y - y0;
            int x1 = Mathf.Clamp(x0 + 1, 0, width - 1);
            int y1 = Mathf.Clamp(y0 + 1, 0, height - 1);
            x0 = Mathf.Clamp(x0, 0, width - 1);
            y0 = Mathf.Clamp(y0, 0, height - 1);

            Color lower = Color.LerpUnclamped(
                source[y0 * width + x0],
                source[y0 * width + x1],
                fractionX);
            Color upper = Color.LerpUnclamped(
                source[y1 * width + x0],
                source[y1 * width + x1],
                fractionX);
            return Color.LerpUnclamped(lower, upper, fractionY);
        }

        private static float Luminance(Color color)
        {
            return 0.2126f * color.r + 0.7152f * color.g + 0.0722f * color.b;
        }
    }
}
