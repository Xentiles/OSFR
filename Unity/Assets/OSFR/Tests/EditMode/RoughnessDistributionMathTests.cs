using System;
using NUnit.Framework;
using OSFR.Measurement;

namespace OSFR.Tests.EditMode
{
    public sealed class RoughnessDistributionMathTests
    {
        [Test]
        public void ConstantRoughness_PreservesAlphaWithoutVariance()
        {
            RoughnessDistributionMath.Result result =
                RoughnessDistributionMath.Evaluate(new[] { 0.2f, 0.2f, 0.2f });

            Assert.That(result.MeanAlpha, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(result.MomentMatchedAlpha, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(result.Variance, Is.EqualTo(0.0f).Within(1e-6f));
        }

        [Test]
        public void BinaryRoughness_TracksFirstAndSecondMoments()
        {
            RoughnessDistributionMath.Result result =
                RoughnessDistributionMath.Evaluate(new[] { 0.1f, 0.5f });

            Assert.That(result.MeanAlpha, Is.EqualTo(0.3f).Within(1e-6f));
            Assert.That(result.MeanAlphaSquared, Is.EqualTo(0.13f).Within(1e-6f));
            Assert.That(result.Variance, Is.EqualTo(0.04f).Within(1e-6f));
            Assert.That(result.MomentMatchedAlpha, Is.GreaterThan(result.MeanAlpha));
        }

        [Test]
        public void RelativeVariance_IsScaleAwareDistributionRisk()
        {
            RoughnessDistributionMath.Result low =
                RoughnessDistributionMath.Evaluate(new[] { 0.1f, 0.3f });
            RoughnessDistributionMath.Result high =
                RoughnessDistributionMath.Evaluate(new[] { 0.4f, 0.6f });

            Assert.That(low.Variance, Is.EqualTo(high.Variance).Within(1e-6f));
            Assert.That(low.RelativeVariance, Is.GreaterThan(high.RelativeVariance));
        }

        [Test]
        public void InvalidRoughness_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                RoughnessDistributionMath.Evaluate(new[] { 0.1f, float.NaN }));
        }
    }
}
