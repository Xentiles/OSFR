using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class NormalRoughnessMathTests
    {
        [Test]
        public void IdenticalNormals_PreserveBaseAlpha()
        {
            Vector3[] normals = { Vector3.forward, Vector3.forward, Vector3.forward };

            NormalRoughnessMath.Result result =
                NormalRoughnessMath.Evaluate(normals, 0.1f, 0.5f);

            Assert.That(result.ResultantLength, Is.EqualTo(1.0f).Within(0.000001f));
            Assert.That(result.Variance, Is.Zero.Within(0.000001f));
            Assert.That(result.EffectiveAlpha, Is.EqualTo(0.1f).Within(0.000001f));
        }

        [Test]
        public void OpposingNormalVariation_BroadensEffectiveAlpha()
        {
            Vector3[] normals =
            {
                new Vector3(0.8f, 0.0f, 0.6f),
                new Vector3(-0.8f, 0.0f, 0.6f)
            };

            NormalRoughnessMath.Result result =
                NormalRoughnessMath.Evaluate(normals, 0.1f, 0.5f);

            Assert.That(result.ResultantLength, Is.EqualTo(0.6f).Within(0.000001f));
            Assert.That(result.Variance, Is.EqualTo(2.0f / 3.0f).Within(0.000001f));
            Assert.That(result.EffectiveAlpha, Is.GreaterThan(0.1f));
            Assert.That(result.FilteredDirection, Is.EqualTo(Vector3.forward));
        }

        [Test]
        public void SimpleVariance_IsLostResultantLength()
        {
            Assert.That(
                NormalRoughnessMath.VarianceFromResultant(0.75f, aggressive: false),
                Is.EqualTo(0.25f).Within(0.000001f));
        }

        [Test]
        public void EffectiveAlpha_UsesQuadratureAndClampsToOne()
        {
            float alpha = NormalRoughnessMath.EffectiveAlpha(0.3f, 0.2f, 0.5f);
            float saturated = NormalRoughnessMath.EffectiveAlpha(0.8f, 1.0f, 1.0f);

            Assert.That(alpha, Is.EqualTo(Mathf.Sqrt(0.19f)).Within(0.000001f));
            Assert.That(saturated, Is.EqualTo(1.0f));
        }
    }
}
