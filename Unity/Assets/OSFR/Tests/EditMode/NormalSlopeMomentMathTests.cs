using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class NormalSlopeMomentMathTests
    {
        [Test]
        public void ConstantSlope_PreservesMeanAndBaseRoughness()
        {
            Vector2[] slopes = { new Vector2(0.4f, -0.2f), new Vector2(0.4f, -0.2f) };

            NormalSlopeMomentMath.Result result =
                NormalSlopeMomentMath.Evaluate(slopes, 0.1f);

            Assert.That(result.MeanSlope.x, Is.EqualTo(0.4f).Within(0.000001f));
            Assert.That(result.MeanSlope.y, Is.EqualTo(-0.2f).Within(0.000001f));
            Assert.That(result.MajorVariance, Is.Zero.Within(0.000001f));
            Assert.That(result.MinorVariance, Is.Zero.Within(0.000001f));
            Assert.That(result.AlphaMajor, Is.EqualTo(0.1f).Within(0.000001f));
            Assert.That(result.AlphaMinor, Is.EqualTo(0.1f).Within(0.000001f));
        }

        [Test]
        public void HorizontalVariation_BroadensOnlyMajorAxis()
        {
            Vector2[] slopes = { new Vector2(-1.0f, 0.0f), new Vector2(1.0f, 0.0f) };

            NormalSlopeMomentMath.Result result =
                NormalSlopeMomentMath.Evaluate(slopes, 0.1f, 0.5f);

            Assert.That(result.MajorVariance, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(result.MinorVariance, Is.Zero.Within(0.000001f));
            Assert.That(result.PrincipalAngleRadians, Is.Zero.Within(0.000001f));
            Assert.That(result.AlphaMajor, Is.EqualTo(Mathf.Sqrt(0.51f)).Within(0.000001f));
            Assert.That(result.AlphaMinor, Is.EqualTo(0.1f).Within(0.000001f));
        }

        [Test]
        public void DiagonalVariation_PreservesOffDiagonalCovarianceAndOrientation()
        {
            float component = 1.0f / Mathf.Sqrt(2.0f);
            Vector2[] slopes =
            {
                new Vector2(-component, -component),
                new Vector2(component, component)
            };

            NormalSlopeMomentMath.Result result =
                NormalSlopeMomentMath.Evaluate(slopes, 0.1f);

            Assert.That(result.Covariance.x, Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(result.Covariance.y, Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(result.Covariance.z, Is.EqualTo(0.5f).Within(0.000001f));
            Assert.That(result.MajorVariance, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(result.MinorVariance, Is.Zero.Within(0.000001f));
            Assert.That(result.PrincipalAngleRadians, Is.EqualTo(Mathf.PI * 0.25f).Within(0.000001f));
        }

        [Test]
        public void Evaluate_RejectsNonFiniteSlope()
        {
            Vector2[] slopes = { new Vector2(float.NaN, 0.0f) };

            Assert.Throws<System.ArgumentException>(() =>
                NormalSlopeMomentMath.Evaluate(slopes, 0.1f));
        }
    }
}
