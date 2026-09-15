using NUnit.Framework;
using OSFR.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OSFR.Tests.PlayMode
{
    public sealed class BilateralReconstructionGpuTests
    {
        [Test]
        public void RiskDrivenBilateralFilter_ReducesCoplanarHighFrequencyVariance()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            int previousMip = feature.FeatureSettings.displayedPyramidLevel;
            float previousStrength = feature.FeatureSettings.bilateralStrength;
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Texture2D checker = CreateCheckerTexture(8);
            Material material = null;

            try
            {
                Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.That(unlitShader, Is.Not.Null);

                material = new Material(unlitShader);
                material.SetColor("_BaseColor", Color.white);
                material.SetTexture("_BaseMap", checker);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                feature.FeatureSettings.displayedPyramidLevel = 0;
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                Color[] unfiltered = RenderOutput(cube, 64);

                feature.FeatureSettings.bilateralStrength = 1.0f;
                feature.FeatureSettings.debugView = MeasurementDebugView.BilateralFilteredColor;
                Color[] filtered = RenderOutput(cube, 64);

                float unfilteredVariance = LuminanceVariance(unfiltered, 64, 24, 40, 24, 40);
                float filteredVariance = LuminanceVariance(filtered, 64, 24, 40, 24, 40);
                TestContext.WriteLine(
                    $"Checker luminance variance: raw = {unfilteredVariance:R}, bilateral = {filteredVariance:R}");

                Assert.That(unfilteredVariance, Is.GreaterThan(0.01f));
                Assert.That(filteredVariance, Is.LessThan(unfilteredVariance * 0.85f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                feature.FeatureSettings.displayedPyramidLevel = previousMip;
                feature.FeatureSettings.bilateralStrength = previousStrength;
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(checker);
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void BilateralDiagnostics_ReportDepthBoundaryRejectionAndInteriorSupport()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = null;

            try
            {
                Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.That(unlitShader, Is.Not.Null);
                material = new Material(unlitShader);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                feature.FeatureSettings.debugView = MeasurementDebugView.DepthRejection;
                Color[] rejection = RenderOutput(cube, 64);
                float maximumRejection = MaximumRed(rejection);

                feature.FeatureSettings.debugView = MeasurementDebugView.NormalRejection;
                Color[] normalRejection = RenderOutput(cube, 64);
                float maximumNormalRejection = MaximumRed(normalRejection);

                feature.FeatureSettings.debugView = MeasurementDebugView.BilateralWeightSum;
                Color[] support = RenderOutput(cube, 64);
                float centerSupport = support[32 * 64 + 32].r;

                TestContext.WriteLine(
                    $"Depth rejection maximum = {maximumRejection:R}; "
                    + $"normal rejection maximum = {maximumNormalRejection:R}; "
                    + $"center joint support = {centerSupport:R}");
                Assert.That(maximumRejection, Is.GreaterThan(0.05f));
                Assert.That(maximumNormalRejection, Is.GreaterThan(0.05f));
                Assert.That(centerSupport, Is.GreaterThan(0.95f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(cube);
            }
        }

        private static Texture2D CreateCheckerTexture(int size)
        {
            Texture2D texture = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Repeat
            };
            Color[] pixels = new Color[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    pixels[y * size + x] = ((x + y) & 1) == 0 ? Color.black : Color.white * 4.0f;
                }
            }

            texture.SetPixels(pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            Assert.That(pipeline, Is.Not.Null);
            foreach (ScriptableRendererData rendererData in pipeline.rendererDataList)
            {
                if (rendererData != null
                    && rendererData.TryGetRendererFeature(out MeasurementRendererFeature feature))
                {
                    return feature;
                }
            }

            Assert.Fail("The active URP asset does not contain MeasurementRendererFeature.");
            return null;
        }

        private static Color[] RenderOutput(GameObject geometry, int resolution)
        {
            GameObject cameraObject = new GameObject("OSFR Bilateral Reconstruction Test Camera");
            RenderTexture target = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGBFloat);
            Texture2D readback = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousTarget = RenderTexture.active;
            Camera camera = null;

            try
            {
                camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.nearClipPlane = 0.3f;
                camera.farClipPlane = 100.0f;
                camera.fieldOfView = 60.0f;
                camera.allowHDR = true;
                camera.targetTexture = target;
                camera.cullingMask = geometry == null ? 0 : ~0;
                camera.Render();

                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0.0f, 0.0f, resolution, resolution), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels();
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
                Object.DestroyImmediate(cameraObject);
            }
        }

        private static float LuminanceVariance(
            Color[] pixels,
            int width,
            int minimumX,
            int maximumX,
            int minimumY,
            int maximumY)
        {
            float sum = 0.0f;
            float sumSquared = 0.0f;
            int count = 0;
            for (int y = minimumY; y < maximumY; y++)
            {
                for (int x = minimumX; x < maximumX; x++)
                {
                    Color color = pixels[y * width + x];
                    float luminance = Vector3.Dot(
                        new Vector3(color.r, color.g, color.b),
                        new Vector3(0.2126f, 0.7152f, 0.0722f));
                    sum += luminance;
                    sumSquared += luminance * luminance;
                    count++;
                }
            }

            float mean = sum / count;
            return sumSquared / count - mean * mean;
        }

        private static float MaximumRed(Color[] pixels)
        {
            float maximum = 0.0f;
            foreach (Color pixel in pixels)
            {
                maximum = Mathf.Max(maximum, pixel.r);
            }

            return maximum;
        }
    }
}
