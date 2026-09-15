using System;
using NUnit.Framework;
using OSFR.Measurement;
using OSFR.Validation;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class PixelFootprintMathTests
    {
        private const float Tolerance = 1e-6f;

        [Test]
        public void VerticalFootprint_MatchesResearchPlanReferenceCase()
        {
            float result = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(
                linearDepth: 10.0f,
                verticalFieldOfViewDegrees: 60.0f,
                outputHeight: 2160);

            Assert.That(result, Is.EqualTo(0.005345836f).Within(Tolerance));
        }

        [Test]
        public void VerticalFootprint_HalvesWhenOutputHeightDoubles()
        {
            float footprint1080p = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(10.0f, 60.0f, 1080);
            float footprint2160p = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(10.0f, 60.0f, 2160);

            Assert.That(footprint2160p, Is.EqualTo(0.5f * footprint1080p).Within(Tolerance));
        }

        [TestCase(0.25f)]
        [TestCase(0.5f)]
        [TestCase(1.0f)]
        [TestCase(2.0f)]
        [TestCase(4.0f)]
        public void FeatureSize_ScalesWithTargetPixelCount(float projectedPixelCount)
        {
            float onePixel = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(25.0f, 60.0f, 1080);
            float featureSize = PixelFootprintMath.FeatureWorldSizeForProjectedPixels(
                projectedPixelCount,
                25.0f,
                60.0f,
                1080);

            Assert.That(featureSize, Is.EqualTo(projectedPixelCount * onePixel).Within(Tolerance));
        }

        [Test]
        public void RenderToOutputFootprint_IsUnchangedAtNativeResolution()
        {
            float result = PixelFootprintMath.RenderPixelFootprintToOutputPixelFootprint(
                worldUnitsPerRenderPixel: 0.01f,
                renderDimension: 1920,
                outputDimension: 1920);

            Assert.That(result, Is.EqualTo(0.01f).Within(Tolerance));
        }

        [Test]
        public void RenderToOutputFootprint_AccountsForUpscaling()
        {
            float result = PixelFootprintMath.RenderPixelFootprintToOutputPixelFootprint(
                worldUnitsPerRenderPixel: 0.01f,
                renderDimension: 1920,
                outputDimension: 3840);

            Assert.That(result, Is.EqualTo(0.005f).Within(Tolerance));
        }

        [Test]
        public void VerticalFootprint_RejectsInvalidProjectionInputs()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(0.0f, 60.0f, 1080));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(10.0f, 180.0f, 1080));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(10.0f, 60.0f, 0));
        }

        [Test]
        public void PrincipalFootprint_MatchesOrthogonalWorldDerivatives()
        {
            PixelFootprint footprint = PixelFootprintMath.FromWorldDerivatives(
                new Vector3(2.0f, 0.0f, 0.0f),
                new Vector3(0.0f, 1.0f, 0.0f));

            Assert.That(footprint.MajorWorld, Is.EqualTo(2.0f).Within(Tolerance));
            Assert.That(footprint.MinorWorld, Is.EqualTo(1.0f).Within(Tolerance));
            Assert.That(footprint.AreaWorld2, Is.EqualTo(2.0f).Within(Tolerance));
            Assert.That(footprint.Anisotropy, Is.EqualTo(2.0f).Within(Tolerance));
        }

        [Test]
        public void PrincipalFootprint_IsInvariantUnderWorldRotation()
        {
            Quaternion rotation = Quaternion.Euler(23.0f, 71.0f, -14.0f);
            PixelFootprint footprint = PixelFootprintMath.FromWorldDerivatives(
                rotation * new Vector3(0.02f, 0.0f, 0.0f),
                rotation * new Vector3(0.0f, 0.005f, 0.0f));

            Assert.That(footprint.MajorWorld, Is.EqualTo(0.02f).Within(Tolerance));
            Assert.That(footprint.MinorWorld, Is.EqualTo(0.005f).Within(Tolerance));
            Assert.That(footprint.AreaWorld2, Is.EqualTo(0.0001f).Within(Tolerance));
            Assert.That(footprint.Anisotropy, Is.EqualTo(4.0f).Within(Tolerance));
        }

        [Test]
        public void PrincipalFootprint_RejectsNonFiniteDerivatives()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => PixelFootprintMath.FromWorldDerivatives(
                    new Vector3(float.NaN, 0.0f, 0.0f),
                    Vector3.up));
        }

        [Test]
        public void ForwardCameraRail_IsDeterministicAtEndpoints()
        {
            GameObject cameraObject = new GameObject("Rail Test");

            try
            {
                DeterministicCameraRail rail = cameraObject.AddComponent<DeterministicCameraRail>();
                rail.Configure(DeterministicCameraRail.RailPath.Forward, 600, 60.0f);

                rail.ApplyFrame(0);
                Assert.That(cameraObject.transform.position, Is.EqualTo(new Vector3(0.0f, 1.6f, -10.0f)));

                rail.ApplyFrame(599);
                Vector3 firstEvaluation = cameraObject.transform.position;
                rail.ApplyFrame(599);

                Assert.That(firstEvaluation, Is.EqualTo(new Vector3(0.0f, 1.6f, 70.0f)));
                Assert.That(cameraObject.transform.position, Is.EqualTo(firstEvaluation));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
