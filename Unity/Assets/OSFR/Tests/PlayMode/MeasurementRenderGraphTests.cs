using NUnit.Framework;
using UnityEngine;

namespace OSFR.Tests.PlayMode
{
    public sealed class MeasurementRenderGraphTests
    {
        [Test]
        public void LinearDepthView_RendersKnownGeometryThroughUrp()
        {
            GameObject cameraObject = new GameObject("OSFR Measurement Test Camera");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            RenderTexture target = new RenderTexture(64, 64, 24, RenderTextureFormat.ARGBFloat);
            Texture2D readback = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            Material material = null;
            Camera camera = null;
            RenderTexture previousTarget = RenderTexture.active;

            try
            {
                Shader litShader = Shader.Find("Universal Render Pipeline/Lit");
                Assert.That(litShader, Is.Not.Null);

                material = new Material(litShader);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 100.0f;
                camera.allowHDR = true;
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(32.0f, 32.0f, 1.0f, 1.0f), 0, 0, false);
                readback.Apply(false, false);
                Color center = readback.GetPixel(0, 0);

                Assert.That(center.r, Is.InRange(0.03f, 0.06f));
                Assert.That(center.g, Is.EqualTo(center.r).Within(0.002f));
                Assert.That(center.b, Is.EqualTo(center.r).Within(0.002f));
            }
            finally
            {
                RenderTexture.active = previousTarget;
                if (camera != null)
                {
                    camera.targetTexture = null;
                }

                Object.DestroyImmediate(readback);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(cube);
                Object.DestroyImmediate(cameraObject);
            }
        }
    }
}
