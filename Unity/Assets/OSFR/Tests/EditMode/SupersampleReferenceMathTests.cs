using System;
using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class SupersampleReferenceMathTests
    {
        [Test]
        public void BuildTileProjection_MapsTileCenterToNdcOrigin()
        {
            Matrix4x4 fullProjection = Matrix4x4.Perspective(60.0f, 16.0f / 9.0f, 0.1f, 200.0f);
            var tile = new RectInt(50, 25, 25, 50);
            Matrix4x4 tileProjection = SupersampleReferenceMath.BuildTileProjection(
                fullProjection,
                tile,
                100,
                100);

            float centerX = 2.0f * (tile.x + 0.5f * tile.width) / 100.0f - 1.0f;
            float centerY = 2.0f * (tile.y + 0.5f * tile.height) / 100.0f - 1.0f;
            Vector4 fullClip = new Vector4(centerX, centerY, 0.0f, 1.0f);
            Vector4 cameraPoint = fullProjection.inverse * fullClip;
            Vector4 tileClip = tileProjection * cameraPoint;

            Assert.That(tileClip.x / tileClip.w, Is.EqualTo(0.0f).Within(0.00001f));
            Assert.That(tileClip.y / tileClip.w, Is.EqualTo(0.0f).Within(0.00001f));
        }

        [Test]
        public void BuildTileProjection_MapsTileEdgesToNdcEdges()
        {
            Matrix4x4 fullProjection = Matrix4x4.Perspective(60.0f, 1.0f, 0.1f, 200.0f);
            var tile = new RectInt(25, 25, 50, 50);
            Matrix4x4 tileProjection = SupersampleReferenceMath.BuildTileProjection(
                fullProjection,
                tile,
                100,
                100);

            AssertNdc(fullProjection, tileProjection, -0.5f, -0.5f, -1.0f, -1.0f);
            AssertNdc(fullProjection, tileProjection, 0.5f, 0.5f, 1.0f, 1.0f);
        }

        [Test]
        public void CompareLuminance_ReportsExpectedErrors()
        {
            var candidate = new[] { Color.black, Color.white };
            var reference = new[] { Color.black, new Color(0.5f, 0.5f, 0.5f, 1.0f) };

            SupersampleReferenceMath.LuminanceComparison comparison =
                SupersampleReferenceMath.CompareLuminance(candidate, reference);

            Assert.That(comparison.MeanAbsoluteError, Is.EqualTo(0.25f).Within(0.00001f));
            Assert.That(comparison.RootMeanSquareError, Is.EqualTo(Mathf.Sqrt(0.125f)).Within(0.00001f));
            Assert.That(comparison.MaximumAbsoluteError, Is.EqualTo(0.5f).Within(0.00001f));
            Assert.That(comparison.RelativeRootMeanSquareError, Is.EqualTo(1.0f).Within(0.00001f));
        }

        [Test]
        public void InvalidTile_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                SupersampleReferenceMath.BuildTileProjection(
                    Matrix4x4.identity,
                    new RectInt(90, 0, 20, 10),
                    100,
                    100));
        }

        private static void AssertNdc(
            Matrix4x4 fullProjection,
            Matrix4x4 tileProjection,
            float fullX,
            float fullY,
            float expectedTileX,
            float expectedTileY)
        {
            Vector4 cameraPoint = fullProjection.inverse * new Vector4(fullX, fullY, 0.0f, 1.0f);
            Vector4 tileClip = tileProjection * cameraPoint;
            Assert.That(tileClip.x / tileClip.w, Is.EqualTo(expectedTileX).Within(0.00001f));
            Assert.That(tileClip.y / tileClip.w, Is.EqualTo(expectedTileY).Within(0.00001f));
        }
    }
}
