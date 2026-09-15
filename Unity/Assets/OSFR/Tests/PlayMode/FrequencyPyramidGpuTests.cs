using NUnit.Framework;
using OSFR.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OSFR.Tests.PlayMode
{
    public sealed class FrequencyPyramidGpuTests
    {
        [Test]
        public void PyramidMip_PreservesLinearHdrRange()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            int previousLevel = feature.FeatureSettings.displayedPyramidLevel;

            try
            {
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                feature.FeatureSettings.displayedPyramidLevel = 3;
                Color[] pixels = RenderOutput(null, new Color(4.0f, 1.0f, 0.25f, 1.0f), 64);
                Color center = pixels[32 * 64 + 32];

                TestContext.WriteLine($"L3 center HDR color = {center}");
                // Camera background colors are converted from project color space before the
                // HDR target is cleared, so validate range/order rather than encoded input values.
                Assert.That(center.r, Is.GreaterThan(1.0f));
                Assert.That(center.r, Is.GreaterThan(center.g));
                Assert.That(center.g, Is.GreaterThan(center.b));
                Assert.That(float.IsNaN(center.r) || float.IsInfinity(center.r), Is.False);
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                feature.FeatureSettings.displayedPyramidLevel = previousLevel;
            }
        }

        [Test]
        public void ConstantLinearHdrColor_HasNearZeroFineBandEnergy()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;

            try
            {
                feature.FeatureSettings.debugView = MeasurementDebugView.FineBandEnergyData;
                Color[] pixels = RenderOutput(null, new Color(4.0f, 1.0f, 0.25f, 1.0f), 64);
                float maximum = MaximumEnergy(pixels);

                TestContext.WriteLine($"Maximum constant-field energy = {maximum:R}");
                Assert.That(maximum, Is.LessThan(1e-6f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
            }
        }

        [Test]
        public void HardSilhouette_ProducesPositiveFineBandEnergy()
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
                material.SetColor("_BaseColor", Color.white * 4.0f);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                feature.FeatureSettings.debugView = MeasurementDebugView.FineBandEnergyData;
                Color[] pixels = RenderOutput(cube, Color.black, 64);
                float maximum = MaximumEnergy(pixels);

                TestContext.WriteLine($"Maximum silhouette energy = {maximum:R}");
                Assert.That(maximum, Is.GreaterThan(1e-4f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void HardSilhouette_FineFrequencyRatioIsFiniteAndNormalized()
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
                material.SetColor("_BaseColor", Color.white * 4.0f);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                feature.FeatureSettings.debugView = MeasurementDebugView.FineFrequencyRatioData;
                Color[] pixels = RenderOutput(cube, Color.black, 64);
                float maximum = 0.0f;

                foreach (Color pixel in pixels)
                {
                    Assert.That(float.IsNaN(pixel.r) || float.IsInfinity(pixel.r), Is.False);
                    Assert.That(pixel.r, Is.InRange(0.0f, 1.0001f));
                    maximum = Mathf.Max(maximum, pixel.r);
                }

                TestContext.WriteLine($"Maximum silhouette fine-frequency ratio = {maximum:R}");
                Assert.That(maximum, Is.GreaterThan(0.01f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(cube);
            }
        }

        [Test]
        public void AliasRiskData_IsFactorizedAndFallsAtHigherResolution()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            float previousStart = feature.FeatureSettings.riskFootprintStartWorld;
            float previousEnd = feature.FeatureSettings.riskFootprintEndWorld;
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Material material = null;

            try
            {
                Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
                Assert.That(unlitShader, Is.Not.Null);

                material = new Material(unlitShader);
                material.SetColor("_BaseColor", Color.white * 4.0f);
                cube.GetComponent<MeshRenderer>().sharedMaterial = material;
                cube.transform.position = new Vector3(0.0f, 0.0f, 5.0f);
                cube.transform.localScale = Vector3.one * 2.0f;

                feature.FeatureSettings.debugView = MeasurementDebugView.AliasRiskData;
                feature.FeatureSettings.riskFootprintStartWorld = 0.002f;
                feature.FeatureSettings.riskFootprintEndWorld = 0.02f;

                Color[] lowResolution = RenderOutput(cube, Color.black, 64);
                Color[] highResolution = RenderOutput(cube, Color.black, 512);
                float lowMaximum = ValidateAndFindMaximumRisk(lowResolution);
                float highMaximum = ValidateAndFindMaximumRisk(highResolution);

                TestContext.WriteLine(
                    $"Maximum alias risk: 64px = {lowMaximum:R}, 512px = {highMaximum:R}");
                Assert.That(lowMaximum, Is.GreaterThan(0.01f));
                Assert.That(highMaximum, Is.LessThan(lowMaximum));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                feature.FeatureSettings.riskFootprintStartWorld = previousStart;
                feature.FeatureSettings.riskFootprintEndWorld = previousEnd;
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(cube);
            }
        }

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            Assert.That(pipeline, Is.Not.Null, "The GPU validation requires the configured URP asset.");

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

        private static Color[] RenderOutput(GameObject geometry, Color clearColor, int resolution)
        {
            GameObject cameraObject = new GameObject("OSFR Frequency Pyramid Test Camera");
            RenderTexture target = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGBFloat);
            Texture2D readback = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false, true);
            Camera camera = null;
            RenderTexture previousTarget = RenderTexture.active;

            try
            {
                camera = cameraObject.AddComponent<Camera>();
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = clearColor;
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

        private static float MaximumEnergy(Color[] pixels)
        {
            float maximum = 0.0f;
            foreach (Color pixel in pixels)
            {
                maximum = Mathf.Max(maximum, pixel.r);
            }

            return maximum;
        }

        private static float ValidateAndFindMaximumRisk(Color[] pixels)
        {
            float maximum = 0.0f;
            foreach (Color pixel in pixels)
            {
                bool finite = !(float.IsNaN(pixel.r) || float.IsInfinity(pixel.r));
                bool normalized = pixel.r >= 0.0f && pixel.r <= 1.0001f
                    && pixel.g >= 0.0f && pixel.g <= 1.0001f
                    && pixel.b >= 0.0f && pixel.b <= 1.0001f
                    && pixel.a >= 0.0f && pixel.a <= 1.0001f;
                bool factorized = pixel.r <= pixel.g + 0.0001f
                    && pixel.r <= pixel.b + 0.0001f
                    && pixel.r <= pixel.a + 0.0001f;
                if (!finite || !normalized || !factorized)
                {
                    Assert.Fail($"Invalid alias-risk payload: {pixel}");
                }

                maximum = Mathf.Max(maximum, pixel.r);
            }

            return maximum;
        }
    }
}
