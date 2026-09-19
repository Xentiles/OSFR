using System;
using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class AnisotropicSmithMathTests
    {
        [Test]
        public void IsotropicAxes_MatchCorrelatedSmithVisibility()
        {
            Vector3 view = new Vector3(0.35f, 0.2f, 0.915f).normalized;
            Vector3 light = new Vector3(-0.25f, 0.5f, 0.83f).normalized;
            const float alpha = 0.37f;
            float actual = AnisotropicSmithMath.Evaluate(
                Vector3.forward, Vector3.right, view, light, alpha, alpha);
            float nDotV = view.z;
            float nDotL = light.z;
            float alphaSquared = alpha * alpha;
            float lambdaV = nDotL * Mathf.Sqrt(
                nDotV * nDotV * (1.0f - alphaSquared) + alphaSquared);
            float lambdaL = nDotV * Mathf.Sqrt(
                nDotL * nDotL * (1.0f - alphaSquared) + alphaSquared);
            float expected = 0.5f / (lambdaV + lambdaL);

            Assert.That(actual, Is.EqualTo(expected).Within(1e-6f));
        }

        [Test]
        public void DirectionalAxes_ChangeVisibilityWhenFrameRotates()
        {
            Vector3 direction = new Vector3(0.8f, 0.0f, 0.6f).normalized;
            float alongRoughAxis = AnisotropicSmithMath.Evaluate(
                Vector3.forward, Vector3.right, direction, direction, 0.8f, 0.1f);
            float alongSmoothAxis = AnisotropicSmithMath.Evaluate(
                Vector3.forward, Vector3.up, direction, direction, 0.8f, 0.1f);

            Assert.That(alongRoughAxis, Is.LessThan(alongSmoothAxis));
        }

        [Test]
        public void GrazingDirections_RemainFiniteAndPositive()
        {
            Vector3 view = new Vector3(1.0f, 0.0f, 0.00001f).normalized;
            Vector3 light = new Vector3(0.0f, 1.0f, 0.00001f).normalized;
            float visibility = AnisotropicSmithMath.Evaluate(
                Vector3.forward, Vector3.right, view, light, 0.9f, 0.05f);

            Assert.That(float.IsFinite(visibility), Is.True);
            Assert.That(visibility, Is.GreaterThan(0.0f));
        }

        [Test]
        public void InvalidRoughness_IsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                AnisotropicSmithMath.Evaluate(
                    Vector3.forward,
                    Vector3.right,
                    Vector3.forward,
                    Vector3.forward,
                    0.0f,
                    0.2f));
        }
    }
}
