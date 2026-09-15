using System;
using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class FullReferenceMetricMathTests
    {
        [Test]
        public void IdenticalImages_HaveZeroErrorAndInfinitePsnr()
        {
            Color[] image = ConstantImage(8, 8, 0.5f);

            FullReferenceMetricMath.Result metrics =
                FullReferenceMetricMath.Evaluate(image, image, 8, 8, 4);

            Assert.That(metrics.RgbMeanSquareError, Is.EqualTo(0.0f));
            Assert.That(float.IsPositiveInfinity(metrics.PsnrDecibels), Is.True);
            Assert.That(metrics.LuminanceRootMeanSquareError, Is.EqualTo(0.0f));
            foreach (FullReferenceMetricMath.SpatialBand band in metrics.Bands)
            {
                Assert.That(band.ErrorMeanSquare, Is.EqualTo(0.0f));
            }
        }

        [Test]
        public void BlackAgainstWhite_HasZeroDecibelPsnrAtReferencePeak()
        {
            FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                ConstantImage(4, 4, 0.0f),
                ConstantImage(4, 4, 1.0f),
                4,
                4,
                3);

            Assert.That(metrics.ReferencePeak, Is.EqualTo(1.0f));
            Assert.That(metrics.RgbMeanSquareError, Is.EqualTo(1.0f).Within(0.00001f));
            Assert.That(metrics.PsnrDecibels, Is.EqualTo(0.0f).Within(0.00001f));
            Assert.That(metrics.LuminanceRootMeanSquareError, Is.EqualTo(1.0f).Within(0.00001f));
            Assert.That(metrics.Residual.ErrorMeanSquare, Is.EqualTo(1.0f).Within(0.00001f));
        }

        [Test]
        public void ConstantOffset_AppearsInResidualButNotLaplacianBands()
        {
            FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                ConstantImage(8, 8, 0.25f),
                ConstantImage(8, 8, 0.5f),
                8,
                8,
                4);

            foreach (FullReferenceMetricMath.SpatialBand band in metrics.Bands)
            {
                Assert.That(band.ErrorMeanSquare, Is.EqualTo(0.0f).Within(0.000001f));
            }

            Assert.That(metrics.Residual.ErrorMeanSquare, Is.EqualTo(0.0625f).Within(0.00001f));
        }

        [Test]
        public void CheckerDifference_ProducesFineBandError()
        {
            var checker = new Color[64];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    float value = (x + y) % 2;
                    checker[y * 8 + x] = new Color(value, value, value, 1.0f);
                }
            }

            FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                checker,
                ConstantImage(8, 8, 0.5f),
                8,
                8,
                4);

            Assert.That(metrics.Bands[0].ErrorMeanSquare, Is.GreaterThan(0.1f));
            Assert.That(metrics.Bands[0].ErrorMeanSquare, Is.GreaterThan(metrics.Bands[1].ErrorMeanSquare));
        }

        [Test]
        public void MismatchedDimensions_Throw()
        {
            Assert.Throws<ArgumentException>(() => FullReferenceMetricMath.Evaluate(
                new Color[4],
                new Color[3],
                2,
                2));
        }

        private static Color[] ConstantImage(int width, int height, float value)
        {
            var image = new Color[width * height];
            for (int index = 0; index < image.Length; index++)
            {
                image[index] = new Color(value, value, value, 1.0f);
            }

            return image;
        }
    }
}
