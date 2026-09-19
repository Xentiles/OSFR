using NUnit.Framework;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class V2RoughnessDistributionShaderTests
    {
        [Test]
        public void Shader_ProvidesMaterialDistributionModesAndDepthPasses()
        {
            Shader shader = Shader.Find("OSFR/V2 Roughness Distribution");
            Assert.That(shader, Is.Not.Null);
            Assert.That(shader.isSupported, Is.True);

            var material = new Material(shader);
            try
            {
                Assert.That(material.FindPass("DepthOnly"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.FindPass("DepthNormalsOnly"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.HasProperty("_BandWidthMillimeters"), Is.True);
                Assert.That(material.HasProperty("_AlphaLow"), Is.True);
                Assert.That(material.HasProperty("_AlphaHigh"), Is.True);
                Assert.That(material.HasProperty("_FilterMode"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    }
}
