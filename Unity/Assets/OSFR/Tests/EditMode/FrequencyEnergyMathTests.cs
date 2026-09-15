using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class FrequencyEnergyMathTests
    {
        [Test]
        public void IdenticalSamplesHaveZeroEnergy()
        {
            Color hdr = new Color(4.0f, 1.0f, 0.25f, 1.0f);
            Assert.That(FrequencyEnergyMath.SquaredBandEnergy(hdr, hdr, 1.0f), Is.EqualTo(0.0f));
        }

        [Test]
        public void ContrastProducesPositiveEnergy()
        {
            float energy = FrequencyEnergyMath.SquaredBandEnergy(Color.white * 8.0f, Color.black, 1.0f);
            Assert.That(energy, Is.GreaterThan(1.0f));
        }

        [Test]
        public void DetectorCompressionRemainsFiniteForInvalidStrength()
        {
            float luminance = FrequencyEnergyMath.CompressedLuminance(Color.white, -10.0f);
            Assert.That(float.IsFinite(luminance), Is.True);
            Assert.That(luminance, Is.GreaterThanOrEqualTo(0.0f));
        }

        [Test]
        public void FineFrequencyRatio_UsesRequestedFinestBands()
        {
            float ratio = FrequencyEnergyMath.FineFrequencyRatio(
                new[] { 4.0f, 2.0f, 3.0f, 1.0f },
                2,
                0.000001f);

            Assert.That(ratio, Is.EqualTo(0.6f).Within(0.000001f));
        }

        [Test]
        public void FineFrequencyRatio_IsZeroForConstantSignal()
        {
            float ratio = FrequencyEnergyMath.FineFrequencyRatio(
                new[] { 0.0f, 0.0f, 0.0f },
                2);

            Assert.That(ratio, Is.EqualTo(0.0f));
        }

        [Test]
        public void FineFrequencyRatio_ClampsInvalidInputs()
        {
            float ratio = FrequencyEnergyMath.FineFrequencyRatio(
                new[] { -1.0f, 1.0f },
                99,
                -1.0f);

            Assert.That(ratio, Is.InRange(0.0f, 1.0f));
        }

        [Test]
        public void FootprintRiskWeight_IncreasesWithProjectedFootprint()
        {
            float small = FrequencyEnergyMath.FootprintRiskWeight(0.002f, 0.002f, 0.002f, 0.02f);
            float medium = FrequencyEnergyMath.FootprintRiskWeight(0.011f, 0.011f, 0.002f, 0.02f);
            float large = FrequencyEnergyMath.FootprintRiskWeight(0.02f, 0.02f, 0.002f, 0.02f);

            Assert.That(small, Is.EqualTo(0.0f).Within(0.000001f));
            Assert.That(medium, Is.GreaterThan(small));
            Assert.That(large, Is.EqualTo(1.0f).Within(0.000001f));
        }

        [Test]
        public void FootprintRiskWeight_RespondsToEitherPrincipalAxis()
        {
            float anisotropic = FrequencyEnergyMath.FootprintRiskWeight(0.02f, 0.002f, 0.002f, 0.02f);
            Assert.That(anisotropic, Is.EqualTo(1.0f).Within(0.000001f));
        }

        [Test]
        public void AliasRisk_IsProductOfClampedFactors()
        {
            Assert.That(FrequencyEnergyMath.AliasRisk(0.8f, 0.5f, 0.25f), Is.EqualTo(0.1f).Within(0.000001f));
            Assert.That(FrequencyEnergyMath.AliasRisk(2.0f, 2.0f, 2.0f), Is.EqualTo(1.0f));
        }
    }
}
