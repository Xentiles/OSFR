using System;
using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class TemporalMetricMathTests
    {
        [Test]
        public void IdenticalFrame_HasZeroResidual()
        {
            Color[] frame = Fill(8, new Color(2.0f, 1.0f, 0.5f, 1.0f));

            TemporalMetricMath.FrameResult result =
                TemporalMetricMath.EvaluateFrame(frame, frame);

            Assert.That(result.RootMeanSquareError, Is.Zero);
            Assert.That(result.MeanSignedError, Is.Zero);
            Assert.That(result.MeanAbsoluteError, Is.Zero);
        }

        [Test]
        public void MotionCompensatedTransition_SubtractsLegitimateReferenceMotion()
        {
            const int width = 4;
            const int height = 1;
            Color[] previous = Fill(width, Color.black);
            Color[] current = Fill(width, Color.black);
            previous[1] = Color.white;
            current[2] = Color.white;
            Color[] motion = Fill(width, Color.clear);
            motion[2] = new Color(0.25f, 0.0f, 0.0f, 1.0f);
            Color[] depth = Fill(width, new Color(5.0f, 5.0f, 5.0f, 1.0f));

            TemporalMetricMath.TransitionResult result =
                TemporalMetricMath.EvaluateMotionCompensatedTransition(
                    previous,
                    current,
                    previous,
                    current,
                    motion,
                    depth,
                    depth,
                    width,
                    height);

            Assert.That(result.ValidPixelCount, Is.EqualTo(width));
            Assert.That(result.RootMeanSquareError, Is.EqualTo(0.0f).Within(0.000001f));
        }

        [Test]
        public void MotionCompensatedTransition_RejectsDepthDisocclusion()
        {
            Color[] previous = Fill(2, Color.black);
            Color[] current = Fill(2, Color.white);
            Color[] reference = Fill(2, Color.black);
            Color[] motion = Fill(2, Color.clear);
            Color[] previousDepth = Fill(2, new Color(5.0f, 0.0f, 0.0f, 1.0f));
            Color[] currentDepth =
            {
                new Color(5.0f, 0.0f, 0.0f, 1.0f),
                new Color(10.0f, 0.0f, 0.0f, 1.0f)
            };

            TemporalMetricMath.TransitionResult result =
                TemporalMetricMath.EvaluateMotionCompensatedTransition(
                    previous,
                    current,
                    reference,
                    reference,
                    motion,
                    previousDepth,
                    currentDepth,
                    2,
                    1,
                    0.02f);

            Assert.That(result.ValidPixelCount, Is.EqualTo(1));
            Assert.That(result.ValidFraction, Is.EqualTo(0.5f));
            Assert.That(result.RootMeanSquareError, Is.EqualTo(1.0f).Within(0.000001f));
        }

        [Test]
        public void StaticCameraMotion_ReprojectsCurrentWorldPositionIntoPreviousUv()
        {
            Color[] worldPositions =
            {
                new Color(0.0f, 0.0f, 0.0f, 1.0f),
                new Color(0.0f, 0.0f, 0.0f, 1.0f)
            };
            Color[] depth = Fill(2, new Color(1.0f, 1.0f, 1.0f, 1.0f));

            Color[] motion = TemporalMetricMath.BuildStaticCameraMotionVectors(
                worldPositions,
                depth,
                Matrix4x4.identity,
                2,
                1);

            Assert.That(motion[0].r, Is.EqualTo(-0.25f).Within(0.000001f));
            Assert.That(motion[1].r, Is.EqualTo(0.25f).Within(0.000001f));
            Assert.That(motion[0].g, Is.Zero.Within(0.000001f));
        }

        [Test]
        public void Spectrum_SeparatesInspectionBandFromLowFrequencyDrift()
        {
            const int count = 60;
            var shimmer = new float[count];
            var drift = new float[count];
            for (int index = 0; index < count; index++)
            {
                shimmer[index] = Mathf.Sin(2.0f * Mathf.PI * 10.0f * index / 60.0f);
                drift[index] = Mathf.Sin(2.0f * Mathf.PI * 1.0f * index / 60.0f);
            }

            TemporalMetricMath.SpectrumResult shimmerResult =
                TemporalMetricMath.EvaluateSpectrum(shimmer, 60.0f, 2.0f, 30.0f);
            TemporalMetricMath.SpectrumResult driftResult =
                TemporalMetricMath.EvaluateSpectrum(drift, 60.0f, 2.0f, 30.0f);

            Assert.That(shimmerResult.BandFraction, Is.GreaterThan(0.999f));
            Assert.That(driftResult.BandFraction, Is.LessThan(0.001f));
            Assert.That(shimmerResult.TotalAlternatingPower, Is.EqualTo(0.5f).Within(0.0001f));
        }

        [Test]
        public void SequenceAccumulator_WeightsPixelsAndBuildsTemporalSummary()
        {
            var accumulator = new TemporalMetricMath.SequenceAccumulator();
            accumulator.AddFrame(new TemporalMetricMath.FrameResult(4, 4.0, 1.0f, 1.0f, 1.0f));
            accumulator.AddFrame(new TemporalMetricMath.FrameResult(4, 0.0, 0.0f, 0.0f, 0.0f));
            accumulator.AddTransition(new TemporalMetricMath.TransitionResult(4, 2, 2.0, 1.0f));

            TemporalMetricMath.SequenceSummary summary = accumulator.BuildSummary(60.0f);

            Assert.That(summary.FrameCount, Is.EqualTo(2));
            Assert.That(summary.TransitionCount, Is.EqualTo(1));
            Assert.That(summary.ResidualRootMeanSquare, Is.EqualTo(Mathf.Sqrt(0.5f)).Within(0.000001f));
            Assert.That(summary.TemporalRootMeanSquare, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(summary.ValidTemporalFraction, Is.EqualTo(0.5f));
        }

        private static Color[] Fill(int count, Color value)
        {
            var result = new Color[count];
            Array.Fill(result, value);
            return result;
        }
    }
}
