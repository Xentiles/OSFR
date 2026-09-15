using NUnit.Framework;
using UnityEngine;

namespace OSFR.Tests.PlayMode
{
    public sealed class LinearHdrBoxDownsampleGpuTests
    {
        [Test]
        public void FourByFourResolve_PreservesLinearHdrMean()
        {
            Shader shader = Shader.Find("Hidden/OSFR/LinearHdrBoxDownsample");
            Assert.That(shader, Is.Not.Null);

            var source = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            var material = new Material(shader);
            var destination = new RenderTexture(
                1,
                1,
                0,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                var pixels = new Color[16];
                for (int index = 0; index < pixels.Length; index++)
                {
                    float value = index % 2 == 0 ? 0.0f : 4.0f;
                    pixels[index] = new Color(value, value, value, 1.0f);
                }

                source.SetPixels(pixels);
                source.Apply(false, false);
                material.SetInt("_OSFRSupersampleFactor", 4);
                Graphics.Blit(source, destination, material, 0);
                RenderTexture.active = destination;
                readback.ReadPixels(new Rect(0, 0, 1, 1), 0, 0, false);
                readback.Apply(false, false);

                Color resolved = readback.GetPixel(0, 0);
                Assert.That(resolved.r, Is.EqualTo(2.0f).Within(0.0001f));
                Assert.That(resolved.g, Is.EqualTo(2.0f).Within(0.0001f));
                Assert.That(resolved.b, Is.EqualTo(2.0f).Within(0.0001f));
            }
            finally
            {
                RenderTexture.active = previousActive;
                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(destination);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(source);
            }
        }
    }
}
