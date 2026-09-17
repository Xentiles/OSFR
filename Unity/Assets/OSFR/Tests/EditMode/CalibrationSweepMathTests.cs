using System.Linq;
using NUnit.Framework;
using OSFR.Measurement;

namespace OSFR.Tests.EditMode
{
    public sealed class CalibrationSweepMathTests
    {
        [Test]
        public void Rank_SelectsBalancedParetoKnee()
        {
            var candidates = new[]
            {
                new CalibrationSweepMath.Candidate(0.0f, 1.0f, 0.1f, 0.0f),
                new CalibrationSweepMath.Candidate(0.2f, 0.6f, 0.12f, 0.0f),
                new CalibrationSweepMath.Candidate(0.5f, 0.4f, 0.2f, 0.0f),
                new CalibrationSweepMath.Candidate(0.8f, 0.5f, 0.25f, 0.0f)
            };

            CalibrationSweepMath.Ranking[] result = CalibrationSweepMath.Rank(candidates);

            Assert.That(result.Single(item => item.Recommended).Candidate.Parameter, Is.EqualTo(0.2f));
            Assert.That(result[3].Pareto, Is.False);
        }

        [Test]
        public void Rank_ExcludesCandidateThatFailsRegressionGuard()
        {
            var candidates = new[]
            {
                new CalibrationSweepMath.Candidate(0.0f, 1.0f, 0.1f, 0.0f),
                new CalibrationSweepMath.Candidate(0.2f, 0.6f, 0.12f, 0.0f),
                new CalibrationSweepMath.Candidate(0.5f, 0.1f, 0.11f, 0.03f)
            };

            CalibrationSweepMath.Ranking[] result = CalibrationSweepMath.Rank(candidates, 0.02f);

            Assert.That(result[2].Eligible, Is.False);
            Assert.That(result[2].Pareto, Is.False);
            Assert.That(result[2].Recommended, Is.False);
        }

        [Test]
        public void Rank_RejectsNonFiniteInput()
        {
            var candidates = new[]
            {
                new CalibrationSweepMath.Candidate(0.0f, float.NaN, 0.1f, 0.0f)
            };

            Assert.Throws<System.ArgumentException>(() => CalibrationSweepMath.Rank(candidates));
        }

        [Test]
        public void SelectSmallestNearIdeal_PicksStartOfSaturationPlateau()
        {
            var candidates = new[]
            {
                new CalibrationSweepMath.Candidate(1.0f, 0.20f, 0.10f, 0.0f),
                new CalibrationSweepMath.Candidate(2.0f, 0.15f, 0.08f, 0.0f),
                new CalibrationSweepMath.Candidate(4.0f, 0.145f, 0.078f, 0.0f)
            };

            int selected = CalibrationSweepMath.SelectSmallestNearIdeal(candidates);

            Assert.That(selected, Is.EqualTo(1));
        }

        [Test]
        public void SelectSmallestNearIdeal_RespectsRegressionGuard()
        {
            var candidates = new[]
            {
                new CalibrationSweepMath.Candidate(1.0f, 0.20f, 0.10f, 0.0f),
                new CalibrationSweepMath.Candidate(2.0f, 0.10f, 0.05f, 0.03f)
            };

            int selected = CalibrationSweepMath.SelectSmallestNearIdeal(candidates, 0.02f);

            Assert.That(selected, Is.EqualTo(0));
        }
    }
}
