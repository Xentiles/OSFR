using NUnit.Framework;
using OSFR.Measurement;
using OSFR.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace OSFR.Tests.PlayMode
{
    public sealed class FootprintMeasurementGpuTests
    {
        private const float FieldOfView = 60.0f;
        private const float SurfaceDepth = 4.0f;

        [Test]
        public void FrontFacingPlane_AgreesWithAnalyticalFootprintAcrossResolutions()
        {
            MeasurementRendererFeature feature = FindMeasurementFeature();
            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            Vector2Int previousOutputOverride = feature.FeatureSettings.outputResolutionOverride;

            try
            {
                feature.FeatureSettings.debugView = MeasurementDebugView.FootprintData;
                feature.FeatureSettings.outputResolutionOverride = Vector2Int.zero;

                Color at64Pixels = RenderCenterPixel(64);
                Color at128Pixels = RenderCenterPixel(128);
                float expected64 = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(
                    SurfaceDepth,
                    FieldOfView,
                    64);
                float expected128 = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(
                    SurfaceDepth,
                    FieldOfView,
                    128);

                TestContext.WriteLine(
                    $"64px measured major/minor/area = {at64Pixels.r:R}, {at64Pixels.g:R}, {at64Pixels.b:R}; "
                    + $"analytical axis = {expected64:R}");
                TestContext.WriteLine(
                    $"128px measured major/minor/area = {at128Pixels.r:R}, {at128Pixels.g:R}, {at128Pixels.b:R}; "
                    + $"analytical axis = {expected128:R}");

                Assert.That(at64Pixels.r, Is.EqualTo(expected64).Within(expected64 * 0.03f));
                Assert.That(at64Pixels.g, Is.EqualTo(expected64).Within(expected64 * 0.03f));
                Assert.That(at64Pixels.b, Is.EqualTo(expected64 * expected64).Within(expected64 * expected64 * 0.06f));
                Assert.That(at64Pixels.a, Is.EqualTo(1.0f).Within(0.001f));

                Assert.That(at128Pixels.r, Is.EqualTo(expected128).Within(expected128 * 0.03f));
                Assert.That(at128Pixels.g, Is.EqualTo(expected128).Within(expected128 * 0.03f));
                Assert.That(at128Pixels.r, Is.EqualTo(at64Pixels.r * 0.5f).Within(expected128 * 0.03f));
            }
            finally
            {
                feature.FeatureSettings.debugView = previousView;
                feature.FeatureSettings.outputResolutionOverride = previousOutputOverride;
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

        private static Color RenderCenterPixel(int resolution)
        {
            GameObject cameraObject = new GameObject($"OSFR {resolution}px Footprint Camera");
            GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            RenderTexture target = new RenderTexture(resolution, resolution, 24, RenderTextureFormat.ARGBFloat);
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
                camera.fieldOfView = FieldOfView;
                camera.allowHDR = true;
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                float center = resolution * 0.5f;
                readback.ReadPixels(new Rect(center, center, 1.0f, 1.0f), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixel(0, 0);
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
