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
    /// Captures raw and V1 bilateral outputs beside 8x/16x references, then
    /// evaluates linear-HDR PSNR and per-level Laplacian-band error.
    /// </summary>
    public static class V1SpatialMetricRunner
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int Frame = 300;
        private const int TargetTileEdge = 128;
        private const int PyramidLevels = 5;
        private const int Seed = 12345;

        [MenuItem("OSFR/Capture/Run V1 720p Spatial Metrics")]
        public static void RunFromMenu()
        {
            Run(logSuccess: true);
        }

        public static void RunFromCommandLine()
        {
            if (Run(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static string Run(bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR spatial metrics require a real graphics device.");
                return null;
            }

            SceneSetup[] originalSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            if (!V1ValidationSceneBuilder.CreateOrRebuild(logSuccess: false))
            {
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            Camera camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            DeterministicCameraRail rail = UnityEngine.Object.FindFirstObjectByType<DeterministicCameraRail>();
            MeasurementRendererFeature feature = FindMeasurementFeature();
            Scene scene = SceneManager.GetActiveScene();
            GameObject checkerRoot = scene.GetRootGameObjects()
                .FirstOrDefault(root => root.name == V1ValidationSceneBuilder.CheckerboardRootName);
            GameObject boundaryRoot = scene.GetRootGameObjects()
                .FirstOrDefault(root => root.name == V1ValidationSceneBuilder.DepthBoundaryRootName);

            if (camera == null || rail == null || feature == null || checkerRoot == null || boundaryRoot == null)
            {
                Debug.LogError("The V1 metric scene is missing its camera, rail, feature, or case roots.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"spatial_metrics_720p_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            Directory.CreateDirectory(outputRoot);

            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            int previousMip = feature.FeatureSettings.displayedPyramidLevel;
            Vector2Int previousOverride = feature.FeatureSettings.outputResolutionOverride;
            bool previousRailEnabled = rail.enabled;
            RenderTexture previousTarget = camera.targetTexture;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;
            var entries = new List<ManifestEntry>(2);

            try
            {
                rail.enabled = false;
                UnityEngine.Random.InitState(Seed);
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                rail.ApplyFrame(Frame);
                feature.FeatureSettings.displayedPyramidLevel = 0;
                feature.FeatureSettings.outputResolutionOverride = new Vector2Int(Width, Height);

                entries.Add(CaptureAndMeasureCase(
                    camera,
                    feature,
                    outputRoot,
                    "CheckerboardWall",
                    checkerRoot,
                    boundaryRoot));
                entries.Add(CaptureAndMeasureCase(
                    camera,
                    feature,
                    outputRoot,
                    "DepthDiscontinuityPole",
                    checkerRoot,
                    boundaryRoot));

                var manifest = new RunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = "selected_720p_spatial_metrics",
                    scene = "V1_ScreenSpaceValidation",
                    createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    conditionCount = entries.Count,
                    conditions = entries.ToArray()
                };
                WriteJson(Path.Combine(outputRoot, "manifest.json"), manifest);
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
            finally
            {
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
                Debug.Log($"Completed OSFR V1 720p spatial metrics at {outputRoot}.");
            }

            return outputRoot;
        }

        private static ManifestEntry CaptureAndMeasureCase(
            Camera camera,
            MeasurementRendererFeature feature,
            string outputRoot,
            string caseName,
            GameObject checkerRoot,
            GameObject boundaryRoot)
        {
            checkerRoot.SetActive(caseName == "CheckerboardWall");
            boundaryRoot.SetActive(caseName == "DepthDiscontinuityPole");
            string id = $"{caseName.ToLowerInvariant()}_lateral_f{Frame:000}_{Width}x{Height}";
            string folder = Path.Combine(outputRoot, id);
            Directory.CreateDirectory(folder);

            Color[] raw = CaptureBuffer(
                camera,
                feature,
                MeasurementDebugView.PyramidMip,
                Path.Combine(folder, "color_raw_000000.exr"));
            Color[] bilateral = CaptureBuffer(
                camera,
                feature,
                MeasurementDebugView.BilateralFilteredColor,
                Path.Combine(folder, "color_bilateral_000000.exr"));

            feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
            TiledLinearHdrReferenceRenderer.Result reference8x = TiledLinearHdrReferenceRenderer.Capture(
                camera,
                Width,
                Height,
                8,
                TargetTileEdge,
                Path.Combine(folder, "reference_8x_000000.exr"));
            TiledLinearHdrReferenceRenderer.Result reference16x = TiledLinearHdrReferenceRenderer.Capture(
                camera,
                Width,
                Height,
                16,
                TargetTileEdge,
                Path.Combine(folder, "reference_16x_000000.exr"));

            FullReferenceMetricMath.Result rawMetrics = FullReferenceMetricMath.Evaluate(
                raw,
                reference16x.Pixels,
                Width,
                Height,
                PyramidLevels);
            FullReferenceMetricMath.Result bilateralMetrics = FullReferenceMetricMath.Evaluate(
                bilateral,
                reference16x.Pixels,
                Width,
                Height,
                PyramidLevels);
            SupersampleReferenceMath.LuminanceComparison referenceConvergence =
                SupersampleReferenceMath.CompareLuminance(reference8x.Pixels, reference16x.Pixels);

            WriteSummaryMetrics(folder, rawMetrics, bilateralMetrics);
            WriteSpatialBands(folder, "Raw", rawMetrics);
            WriteSpatialBands(folder, "Bilateral", bilateralMetrics, append: true);
            WriteReferenceConvergence(folder, reference8x, reference16x, referenceConvergence);

            var metadata = new MetricMetadata
            {
                schemaVersion = 1,
                scene = caseName,
                cameraPath = "Lateral",
                output = new[] { Width, Height },
                render = new[] { Width, Height },
                frame = Frame,
                fovY = 60.0f,
                aa = "None",
                fixedSeed = Seed,
                colorSpace = "Linear HDR",
                filterPreset = "V1_5x5_Balanced720p_01",
                bilateralStrength = feature.FeatureSettings.bilateralStrength,
                bilateralSpatialSigmaPixels = feature.FeatureSettings.bilateralSpatialSigmaPixels,
                riskFootprintStartWorld = feature.FeatureSettings.riskFootprintStartWorld,
                riskFootprintEndWorld = feature.FeatureSettings.riskFootprintEndWorld,
                referenceFactor = 16,
                referenceQualification = "Candidate reference; inspect reference_convergence.csv before use.",
                psnrPeakPolicy = "Maximum absolute RGB component in the 16x reference.",
                spatialPyramid = "Linear luminance; 3x3 binomial low pass; 2x decimation; bilinear reconstruction; four Laplacian bands plus residual."
            };
            WriteJson(Path.Combine(folder, "metadata.json"), metadata);

            return new ManifestEntry
            {
                id = id,
                metadata = $"{id}/metadata.json",
                metrics = $"{id}/metrics.csv",
                spatialBands = $"{id}/spatial_bands.csv",
                referenceConvergence = $"{id}/reference_convergence.csv"
            };
        }

        private static Color[] CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            MeasurementDebugView view,
            string outputPath)
        {
            var target = new RenderTexture(
                Width,
                Height,
                24,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                feature.FeatureSettings.debugView = view;
                camera.targetTexture = target;
                camera.ResetAspect();
                camera.ResetProjectionMatrix();
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                byte[] exr = readback.EncodeToEXR(
                    Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                File.WriteAllBytes(outputPath, exr);
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

        private static void WriteSummaryMetrics(
            string folder,
            FullReferenceMetricMath.Result raw,
            FullReferenceMetricMath.Result bilateral)
        {
            string csv = "algorithm,reference,reference_peak_rgb,rgb_mse,psnr_db,"
                + "luminance_mae,luminance_rmse,luminance_relative_rmse\n"
                + SummaryRow("Raw", raw)
                + SummaryRow("Bilateral", bilateral);
            File.WriteAllText(Path.Combine(folder, "metrics.csv"), csv);
        }

        private static string SummaryRow(string algorithm, FullReferenceMetricMath.Result result)
        {
            return string.Join(",", new[]
            {
                algorithm,
                "16x_spatial_supersample",
                Format(result.ReferencePeak),
                Format(result.RgbMeanSquareError),
                Format(result.PsnrDecibels),
                Format(result.LuminanceMeanAbsoluteError),
                Format(result.LuminanceRootMeanSquareError),
                Format(result.LuminanceRelativeRootMeanSquareError)
            }) + "\n";
        }

        private static void WriteSpatialBands(
            string folder,
            string algorithm,
            FullReferenceMetricMath.Result result,
            bool append = false)
        {
            string path = Path.Combine(folder, "spatial_bands.csv");
            string csv = append
                ? string.Empty
                : "algorithm,level,kind,width,height,error_mse,candidate_energy,"
                    + "reference_energy,candidate_to_reference_energy\n";
            foreach (FullReferenceMetricMath.SpatialBand band in result.Bands)
            {
                csv += BandRow(algorithm, band, "laplacian");
            }

            csv += BandRow(algorithm, result.Residual, "residual");
            if (append)
            {
                File.AppendAllText(path, csv);
            }
            else
            {
                File.WriteAllText(path, csv);
            }
        }

        private static string BandRow(
            string algorithm,
            FullReferenceMetricMath.SpatialBand band,
            string kind)
        {
            float energyRatio = band.CandidateEnergy / Mathf.Max(band.ReferenceEnergy, 0.000000000001f);
            return string.Join(",", new[]
            {
                algorithm,
                band.Level.ToString(CultureInfo.InvariantCulture),
                kind,
                band.Width.ToString(CultureInfo.InvariantCulture),
                band.Height.ToString(CultureInfo.InvariantCulture),
                Format(band.ErrorMeanSquare),
                Format(band.CandidateEnergy),
                Format(band.ReferenceEnergy),
                Format(energyRatio)
            }) + "\n";
        }

        private static void WriteReferenceConvergence(
            string folder,
            TiledLinearHdrReferenceRenderer.Result reference8x,
            TiledLinearHdrReferenceRenderer.Result reference16x,
            SupersampleReferenceMath.LuminanceComparison comparison)
        {
            string csv = "comparison,mean_absolute_luminance_error,rmse_luminance,"
                + "max_absolute_luminance_error,relative_rmse,reference_8x_ms,reference_16x_ms\n"
                + string.Join(",", new[]
                {
                    "8x_vs_16x",
                    Format(comparison.MeanAbsoluteError),
                    Format(comparison.RootMeanSquareError),
                    Format(comparison.MaximumAbsoluteError),
                    Format(comparison.RelativeRootMeanSquareError),
                    Format(reference8x.ElapsedMilliseconds),
                    Format(reference16x.ElapsedMilliseconds)
                }) + "\n";
            File.WriteAllText(Path.Combine(folder, "reference_convergence.csv"), csv);
        }

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline =
                GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null)
            {
                return null;
            }

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

        private static void RestoreSceneSetup(SceneSetup[] sceneSetup)
        {
            if (sceneSetup != null && sceneSetup.Length > 0)
            {
                EditorSceneManager.RestoreSceneManagerSetup(sceneSetup);
            }
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, JsonUtility.ToJson(value, prettyPrint: true) + Environment.NewLine);
        }

        [Serializable]
        private sealed class MetricMetadata
        {
            public int schemaVersion;
            public string scene;
            public string cameraPath;
            public int[] output;
            public int[] render;
            public int frame;
            public float fovY;
            public string aa;
            public int fixedSeed;
            public string colorSpace;
            public string filterPreset;
            public float bilateralStrength;
            public float bilateralSpatialSigmaPixels;
            public float riskFootprintStartWorld;
            public float riskFootprintEndWorld;
            public int referenceFactor;
            public string referenceQualification;
            public string psnrPeakPolicy;
            public string spatialPyramid;
        }

        [Serializable]
        private sealed class ManifestEntry
        {
            public string id;
            public string metadata;
            public string metrics;
            public string spatialBands;
            public string referenceConvergence;
        }

        [Serializable]
        private sealed class RunManifest
        {
            public int schemaVersion;
            public string runId;
            public string matrix;
            public string scene;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int conditionCount;
            public ManifestEntry[] conditions;
        }
    }
}
