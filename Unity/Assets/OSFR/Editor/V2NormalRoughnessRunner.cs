using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OSFR.Measurement;
using OSFR.Rendering;
using OSFR.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace OSFR.Editor
{
    /// <summary>
    /// Captures the first V2 normal-resultant/roughness experiment against a
    /// spatially supersampled raw-normal reference.
    /// </summary>
    public static class V2NormalRoughnessRunner
    {
        private const int Seed = 12345;
        private const int Frame = 300;
        private const int TargetTileEdge = 128;
        private const int PyramidLevels = 5;

        [MenuItem("OSFR/Capture/Run V2 Normal Roughness Smoke")]
        public static void RunSmokeFromMenu()
        {
            Run(RunConfiguration.Smoke, logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V2 720p Normal Roughness Snapshot")]
        public static void RunPrimaryFromMenu()
        {
            Run(RunConfiguration.Primary, logSuccess: true);
        }

        public static void RunSmokeFromCommandLine()
        {
            if (Run(RunConfiguration.Smoke, logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static void RunPrimaryFromCommandLine()
        {
            if (Run(RunConfiguration.Primary, logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        private static string Run(RunConfiguration configuration, bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR V2 normal-roughness metrics require a real graphics device.");
                return null;
            }

            SceneSetup[] originalSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            if (!V2ValidationSceneBuilder.CreateOrRebuild(logSuccess: false))
            {
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            Camera camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            DeterministicCameraRail rail = UnityEngine.Object.FindFirstObjectByType<DeterministicCameraRail>();
            MeasurementRendererFeature feature = FindMeasurementFeature();
            GameObject root = SceneManager.GetActiveScene().GetRootGameObjects()
                .FirstOrDefault(item => item.name == V2ValidationSceneBuilder.NormalPanelRootName);
            Renderer[] renderers = root == null
                ? Array.Empty<Renderer>()
                : root.GetComponentsInChildren<Renderer>();
            Material[] materials = renderers
                .Select(renderer => renderer.sharedMaterial)
                .Where(material => material != null)
                .Distinct()
                .ToArray();
            if (camera == null || rail == null || feature == null || renderers.Length != 9
                || materials.Length != 9)
            {
                Debug.LogError("The V2 normal-frequency scene is incomplete.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"{configuration.RunName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V2", runId);
            Directory.CreateDirectory(outputRoot);

            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            int previousMip = feature.FeatureSettings.displayedPyramidLevel;
            Vector2Int previousOverride = feature.FeatureSettings.outputResolutionOverride;
            bool previousRailEnabled = rail.enabled;
            RenderTexture previousTarget = camera.targetTexture;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;
            float[] previousFilter = materials.Select(material => material.GetFloat("_FilterEnabled")).ToArray();
            float[] previousDebug = materials.Select(material => material.GetFloat("_DebugMode")).ToArray();

            try
            {
                rail.enabled = false;
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                rail.ApplyFrame(Frame);
                UnityEngine.Random.InitState(Seed);
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                feature.FeatureSettings.displayedPyramidLevel = 0;
                feature.FeatureSettings.outputResolutionOverride =
                    new Vector2Int(configuration.Width, configuration.Height);

                SetMaterialMode(materials, filterEnabled: false, debugMode: 0);
                Color[] raw = CaptureBuffer(
                    camera,
                    feature,
                    configuration,
                    Path.Combine(outputRoot, "color_raw.exr"));
                TiledLinearHdrReferenceRenderer.Result lowerReference =
                    TiledLinearHdrReferenceRenderer.Capture(
                        camera,
                        configuration.Width,
                        configuration.Height,
                        configuration.LowerReferenceFactor,
                        TargetTileEdge,
                        Path.Combine(outputRoot, $"reference_{configuration.LowerReferenceFactor}x.exr"));
                TiledLinearHdrReferenceRenderer.Result reference =
                    TiledLinearHdrReferenceRenderer.Capture(
                        camera,
                        configuration.Width,
                        configuration.Height,
                        configuration.ReferenceFactor,
                        TargetTileEdge,
                        Path.Combine(outputRoot, $"reference_{configuration.ReferenceFactor}x.exr"));

                SetMaterialMode(materials, filterEnabled: true, debugMode: 0);
                Color[] filtered = CaptureBuffer(
                    camera,
                    feature,
                    configuration,
                    Path.Combine(outputRoot, "color_filtered.exr"));
                SetMaterialMode(materials, filterEnabled: true, debugMode: 1);
                CaptureBuffer(camera, feature, configuration, Path.Combine(outputRoot, "resultant_length.exr"));
                SetMaterialMode(materials, filterEnabled: true, debugMode: 2);
                CaptureBuffer(camera, feature, configuration, Path.Combine(outputRoot, "effective_alpha.exr"));

                FullReferenceMetricMath.Result rawMetrics = FullReferenceMetricMath.Evaluate(
                    raw, reference.Pixels, configuration.Width, configuration.Height, PyramidLevels);
                FullReferenceMetricMath.Result filteredMetrics = FullReferenceMetricMath.Evaluate(
                    filtered, reference.Pixels, configuration.Width, configuration.Height, PyramidLevels);
                SupersampleReferenceMath.LuminanceComparison convergence =
                    SupersampleReferenceMath.CompareLuminance(lowerReference.Pixels, reference.Pixels);
                WriteSummary(outputRoot, configuration.ReferenceFactor, rawMetrics, filteredMetrics);
                WritePanelMetrics(
                    outputRoot,
                    camera,
                    renderers,
                    raw,
                    filtered,
                    reference.Pixels,
                    configuration);
                WriteConvergence(
                    outputRoot,
                    configuration,
                    lowerReference,
                    reference,
                    convergence);
                WriteMetadata(outputRoot, runId, configuration, lowerReference, reference);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
            finally
            {
                for (int index = 0; index < materials.Length; index++)
                {
                    materials[index].SetFloat("_FilterEnabled", previousFilter[index]);
                    materials[index].SetFloat("_DebugMode", previousDebug[index]);
                }

                feature.FeatureSettings.debugView = previousView;
                feature.FeatureSettings.displayedPyramidLevel = previousMip;
                feature.FeatureSettings.outputResolutionOverride = previousOverride;
                rail.enabled = previousRailEnabled;
                camera.targetTexture = previousTarget;
                UnityEngine.Random.state = previousRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V2 normal-roughness metrics at {outputRoot}.");
            }

            return outputRoot;
        }

        private static Color[] CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            RunConfiguration configuration,
            string outputPath)
        {
            var target = new RenderTexture(
                configuration.Width,
                configuration.Height,
                24,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readback = new Texture2D(
                configuration.Width,
                configuration.Height,
                TextureFormat.RGBAFloat,
                false,
                true);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                camera.targetTexture = target;
                camera.ResetAspect();
                camera.ResetProjectionMatrix();
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(
                    new Rect(0, 0, configuration.Width, configuration.Height), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                File.WriteAllBytes(outputPath, readback.EncodeToEXR(
                    Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
                return pixels;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static void WriteSummary(
            string folder,
            int referenceFactor,
            FullReferenceMetricMath.Result raw,
            FullReferenceMetricMath.Result filtered)
        {
            string csv = "algorithm,reference,rgb_mse,psnr_db,luminance_mae,luminance_rmse,luminance_relative_rmse\n"
                + MetricRow("Raw", referenceFactor, raw)
                + MetricRow("ToksvigInspired", referenceFactor, filtered);
            File.WriteAllText(Path.Combine(folder, "metrics.csv"), csv);
        }

        private static string MetricRow(
            string algorithm,
            int referenceFactor,
            FullReferenceMetricMath.Result result)
        {
            return string.Join(",", new[]
            {
                algorithm,
                $"{referenceFactor}x_spatial_supersample",
                Format(result.RgbMeanSquareError),
                Format(result.PsnrDecibels),
                Format(result.LuminanceMeanAbsoluteError),
                Format(result.LuminanceRootMeanSquareError),
                Format(result.LuminanceRelativeRootMeanSquareError)
            }) + "\n";
        }

        private static void WritePanelMetrics(
            string folder,
            Camera camera,
            IReadOnlyList<Renderer> renderers,
            Color[] raw,
            Color[] filtered,
            Color[] reference,
            RunConfiguration configuration)
        {
            string csv = "panel,frequency_cycles_per_m,base_alpha,algorithm,pixels,luminance_rmse,"
                + "luminance_mae,rgb_mse,psnr_db\n";
            foreach (Renderer renderer in renderers.OrderBy(item => item.name))
            {
                RectInt roi = ProjectRenderer(renderer, camera, configuration.Width, configuration.Height);
                Color[] rawRoi = Crop(raw, configuration.Width, roi);
                Color[] filteredRoi = Crop(filtered, configuration.Width, roi);
                Color[] referenceRoi = Crop(reference, configuration.Width, roi);
                FullReferenceMetricMath.Result rawMetrics = FullReferenceMetricMath.Evaluate(
                    rawRoi, referenceRoi, roi.width, roi.height, PyramidLevels);
                FullReferenceMetricMath.Result filteredMetrics = FullReferenceMetricMath.Evaluate(
                    filteredRoi, referenceRoi, roi.width, roi.height, PyramidLevels);
                Material material = renderer.sharedMaterial;
                csv += PanelRow(renderer.name, material, "Raw", rawRoi.Length, rawMetrics);
                csv += PanelRow(renderer.name, material, "ToksvigInspired", filteredRoi.Length, filteredMetrics);
            }

            File.WriteAllText(Path.Combine(folder, "panel_metrics.csv"), csv);
        }

        private static string PanelRow(
            string panel,
            Material material,
            string algorithm,
            int pixels,
            FullReferenceMetricMath.Result result)
        {
            return string.Join(",", new[]
            {
                panel.Replace(',', '_'),
                Format(material.GetFloat("_NormalFrequency")),
                Format(material.GetFloat("_BaseAlpha")),
                algorithm,
                pixels.ToString(CultureInfo.InvariantCulture),
                Format(result.LuminanceRootMeanSquareError),
                Format(result.LuminanceMeanAbsoluteError),
                Format(result.RgbMeanSquareError),
                Format(result.PsnrDecibels)
            }) + "\n";
        }

        private static RectInt ProjectRenderer(Renderer renderer, Camera camera, int width, int height)
        {
            Vector3[] corners =
            {
                new Vector3(-0.5f, -0.5f, 0.0f),
                new Vector3(0.5f, -0.5f, 0.0f),
                new Vector3(-0.5f, 0.5f, 0.0f),
                new Vector3(0.5f, 0.5f, 0.0f)
            };
            float minimumX = 1.0f;
            float minimumY = 1.0f;
            float maximumX = 0.0f;
            float maximumY = 0.0f;
            foreach (Vector3 corner in corners)
            {
                Vector3 viewport = camera.WorldToViewportPoint(renderer.transform.TransformPoint(corner));
                minimumX = Mathf.Min(minimumX, viewport.x);
                minimumY = Mathf.Min(minimumY, viewport.y);
                maximumX = Mathf.Max(maximumX, viewport.x);
                maximumY = Mathf.Max(maximumY, viewport.y);
            }

            int xMin = Mathf.Clamp(Mathf.CeilToInt(minimumX * width) + 1, 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.CeilToInt(minimumY * height) + 1, 0, height - 1);
            int xMax = Mathf.Clamp(Mathf.FloorToInt(maximumX * width) - 1, xMin + 1, width);
            int yMax = Mathf.Clamp(Mathf.FloorToInt(maximumY * height) - 1, yMin + 1, height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static Color[] Crop(Color[] source, int sourceWidth, RectInt roi)
        {
            var result = new Color[roi.width * roi.height];
            for (int y = 0; y < roi.height; y++)
            {
                Array.Copy(
                    source,
                    (roi.y + y) * sourceWidth + roi.x,
                    result,
                    y * roi.width,
                    roi.width);
            }

            return result;
        }

        private static void WriteConvergence(
            string folder,
            RunConfiguration configuration,
            TiledLinearHdrReferenceRenderer.Result lower,
            TiledLinearHdrReferenceRenderer.Result reference,
            SupersampleReferenceMath.LuminanceComparison comparison)
        {
            string csv = "lower_factor,upper_factor,luminance_rmse,luminance_mae,"
                + "lower_render_ms,upper_render_ms\n"
                + string.Join(",", new[]
                {
                    configuration.LowerReferenceFactor.ToString(CultureInfo.InvariantCulture),
                    configuration.ReferenceFactor.ToString(CultureInfo.InvariantCulture),
                    Format(comparison.RootMeanSquareError),
                    Format(comparison.MeanAbsoluteError),
                    Format(lower.ElapsedMilliseconds),
                    Format(reference.ElapsedMilliseconds)
                }) + "\n";
            File.WriteAllText(Path.Combine(folder, "reference_convergence.csv"), csv);
        }

        private static void WriteMetadata(
            string folder,
            string runId,
            RunConfiguration configuration,
            TiledLinearHdrReferenceRenderer.Result lower,
            TiledLinearHdrReferenceRenderer.Result reference)
        {
            var metadata = new Metadata
            {
                schemaVersion = 1,
                runId = runId,
                scene = "V2_UpstreamFiltering",
                caseName = "NormalFrequencyPanels",
                matrix = configuration.MatrixName,
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                output = new[] { configuration.Width, configuration.Height },
                frame = Frame,
                frequenciesCyclesPerMeter = new[] { 16.0f, 64.0f, 256.0f },
                baseMicrofacetAlpha = new[] { 0.03f, 0.1f, 0.3f },
                normalSlopeAmplitude = 0.9f,
                varianceGain = V2ValidationSceneBuilder.PromotedVarianceGain,
                filter = "3x3 pixel-footprint normal mean; aggressive variance=(1-|mean|)/max(|mean|,epsilon); alpha_eff=sqrt(alpha_base^2+gain*variance).",
                qualification = "Toksvig-inspired empirical prototype, not the Toksvig formula or a full NDF filter.",
                lowerReferenceFactor = lower.Factor,
                referenceFactor = reference.Factor,
                referencePolicy = "Raw procedural normal field rendered at higher linear resolution and box-averaged in linear HDR."
            };
            File.WriteAllText(
                Path.Combine(folder, "metadata.json"),
                JsonUtility.ToJson(metadata, true) + Environment.NewLine);
        }

        private static void SetMaterialMode(IEnumerable<Material> materials, bool filterEnabled, int debugMode)
        {
            foreach (Material material in materials)
            {
                material.SetFloat("_FilterEnabled", filterEnabled ? 1.0f : 0.0f);
                material.SetFloat("_DebugMode", debugMode);
            }
        }

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return null;
            foreach (ScriptableRendererData rendererData in pipeline.rendererDataList)
            {
                if (rendererData != null
                    && rendererData.TryGetRendererFeature(out MeasurementRendererFeature feature))
                {
                    return feature;
                }
            }

            return null;
        }

        private static string WorkspaceRoot()
        {
            DirectoryInfo unityProject = Directory.GetParent(Application.dataPath);
            return unityProject?.Parent?.FullName ?? unityProject?.FullName ?? Application.dataPath;
        }

        private static void RestoreSceneSetup(SceneSetup[] setup)
        {
            if (setup != null && setup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(setup);
            }
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private readonly struct RunConfiguration
        {
            public RunConfiguration(
                string runName,
                string matrixName,
                int width,
                int height,
                int lowerReferenceFactor,
                int referenceFactor)
            {
                RunName = runName;
                MatrixName = matrixName;
                Width = width;
                Height = height;
                LowerReferenceFactor = lowerReferenceFactor;
                ReferenceFactor = referenceFactor;
            }

            public static RunConfiguration Smoke => new RunConfiguration(
                "normal_roughness_smoke", "320x180_normal_roughness_smoke", 320, 180, 2, 4);
            public static RunConfiguration Primary => new RunConfiguration(
                "normal_roughness_720p", "720p_normal_roughness_snapshot", 1280, 720, 8, 16);
            public string RunName { get; }
            public string MatrixName { get; }
            public int Width { get; }
            public int Height { get; }
            public int LowerReferenceFactor { get; }
            public int ReferenceFactor { get; }
        }

        [Serializable]
        private sealed class Metadata
        {
            public int schemaVersion;
            public string runId;
            public string scene;
            public string caseName;
            public string matrix;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int[] output;
            public int frame;
            public float[] frequenciesCyclesPerMeter;
            public float[] baseMicrofacetAlpha;
            public float normalSlopeAmplitude;
            public float varianceGain;
            public string filter;
            public string qualification;
            public int lowerReferenceFactor;
            public int referenceFactor;
            public string referencePolicy;
        }
    }
}
