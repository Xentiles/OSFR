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

        [Test]
        public void DisocclusionAges_ClassifyOnlyNewlyRevealedFarSurface()
        {
            Color[] motion = Fill(2, Color.clear);
            Color[] previousDepth =
            {
                new Color(5.0f, 0.0f, 0.0f, 1.0f),
                new Color(10.0f, 0.0f, 0.0f, 1.0f)
            };
            Color[] currentDepth =
            {
                new Color(10.0f, 0.0f, 0.0f, 1.0f),
                new Color(5.0f, 0.0f, 0.0f, 1.0f)
            };

            int[] ages = DisocclusionMetricMath.UpdateAges(
                null, motion, previousDepth, currentDepth, 2, 1, 8);

            Assert.That(ages[0], Is.EqualTo(0));
            Assert.That(ages[1], Is.EqualTo(-1));
        }

        [Test]
        public void DisocclusionAges_PropagateAcrossDepthConsistentMotion()
        {
            int[] previousAges = { 2, -1 };
            Color[] motion = Fill(2, Color.clear);
            Color[] depth = Fill(2, new Color(10.0f, 0.0f, 0.0f, 1.0f));

            int[] ages = DisocclusionMetricMath.UpdateAges(
                previousAges, motion, depth, depth, 2, 1, 8);

            Assert.That(ages[0], Is.EqualTo(3));
            Assert.That(ages[1], Is.EqualTo(-1));
        }

        [Test]
        public void DisocclusionFrame_OldReferenceProducesUnitHistoryRetention()
        {
            Color[] candidate = { Color.white, Color.black };
            Color[] currentReference = Fill(2, Color.black);
            Color[] previousReference = { Color.white, Color.black };
            Color[] motion = Fill(2, Color.clear);
            int[] ages = { 0, -1 };

            DisocclusionMetricMath.AgeBinResult[] result =
                DisocclusionMetricMath.EvaluateFrame(
                    candidate,
                    currentReference,
                    previousReference,
                    motion,
                    ages,
                    2,
                    1,
                    8);

            Assert.That(result[0].PixelCount, Is.EqualTo(1));
            Assert.That(result[0].RootMeanSquareError, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(result[0].HistoryRetentionCoefficient, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(result[0].CloserToHistoryFraction, Is.EqualTo(1.0f));
        }

        [Test]
        public void DisocclusionSequenceAccumulator_WeightsAgeBinsAndAllRecentPixels()
        {
            var accumulator = new DisocclusionMetricMath.SequenceAccumulator(1);
            accumulator.Add(new[]
            {
                new DisocclusionMetricMath.AgeBinResult(0, 1, 1.0, 1.0, 1, 1.0, 1.0, 1),
                new DisocclusionMetricMath.AgeBinResult(1, 3, 3.0, 3.0, 0, 0.0, 0.0, 0)
            });

            DisocclusionMetricMath.AgeBinResult[] bins = accumulator.BuildAgeSummary();
            DisocclusionMetricMath.AgeBinResult all = accumulator.BuildAllRecentSummary();

            Assert.That(bins[0].RootMeanSquareError, Is.EqualTo(1.0f));
            Assert.That(bins[1].RootMeanSquareError, Is.EqualTo(1.0f));
            Assert.That(all.PixelCount, Is.EqualTo(4));
            Assert.That(all.RootMeanSquareError, Is.EqualTo(1.0f));
        }

        private static Color[] Fill(int count, Color value)
        {
            var result = new Color[count];
            Array.Fill(result, value);
            return result;
        }
    }
}
