using NUnit.Framework;
using UnityEngine;

namespace OSFR.Tests.EditMode
{
    public sealed class V2NormalRoughnessShaderTests
    {
        [Test]
        public void Shader_ProvidesUrpDepthParticipationPasses()
        {
            Shader shader = Shader.Find("OSFR/V2 Normal Roughness");

            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            try
            {
                Assert.That(material.FindPass("DepthOnly"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.FindPass("DepthNormalsOnly"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.HasProperty("_FilterMode"), Is.True);
                Assert.That(material.HasProperty("_LeanVarianceGain"), Is.True);
                Assert.That(material.HasProperty("_LeanVisibilityMode"), Is.True);
                Assert.That(material.HasProperty("_PatternAnisotropy"), Is.True);
            }
            finally
            {
                Object.DestroyImmediate(material);
            }
        }
    }
}
