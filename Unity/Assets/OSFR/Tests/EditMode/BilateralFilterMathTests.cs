using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class BilateralFilterMathTests
    {
        [Test]
        public void CenterSampleWithMatchingGuidesHasUnitWeight()
        {
            float weight = BilateralFilterMath.JointWeight(
                Vector2.zero,
                10.0f,
                10.0f,
                Vector3.forward,
                Vector3.forward,
                1.25f,
                0.02f,
                0.1f);

            Assert.That(weight, Is.EqualTo(1.0f).Within(0.000001f));
        }

        [Test]
        public void RelativeDepthWeightIsScaleInvariant()
        {
            float near = BilateralFilterMath.RelativeDepthWeight(1.0f, 1.01f, 0.02f);
            float far = BilateralFilterMath.RelativeDepthWeight(100.0f, 101.0f, 0.02f);
            Assert.That(near, Is.EqualTo(far).Within(0.000001f));
        }

        [Test]
        public void OrthogonalNormalIsStronglyRejected()
        {
            float matching = BilateralFilterMath.NormalWeight(Vector3.forward, Vector3.forward, 0.1f);
            float orthogonal = BilateralFilterMath.NormalWeight(Vector3.forward, Vector3.up, 0.1f);
            Assert.That(matching, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(orthogonal, Is.LessThan(0.0001f));
        }

        [Test]
        public void DepthDiscontinuityIsStronglyRejected()
        {
            float matching = BilateralFilterMath.RelativeDepthWeight(5.0f, 5.0f, 0.02f);
            float discontinuity = BilateralFilterMath.RelativeDepthWeight(5.0f, 10.0f, 0.02f);
            Assert.That(matching, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(discontinuity, Is.LessThan(0.0001f));
        }
    }
}
