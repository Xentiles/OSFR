using System;
using NUnit.Framework;
using OSFR.Measurement;

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
    }
}
