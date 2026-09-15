using System.Collections;
using NUnit.Framework;
using OSFR.Measurement;
using UnityEngine.TestTools;

namespace OSFR.Tests.PlayMode
{
    public sealed class PixelFootprintPlayModeTests
    {
        [UnityTest]
        public IEnumerator RuntimeMeasurementAssembly_LoadsInPlayMode()
        {
            float footprint = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(10.0f, 60.0f, 1080);

            Assert.That(footprint, Is.GreaterThan(0.0f));
            yield return null;
        }
    }
}
