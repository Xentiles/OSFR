using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    /// Evaluates the promoted V1 bilateral preset over a deterministic camera
    /// sequence. Previous-frame samples are warped by static-scene camera motion,
    /// depth-inconsistent samples are excluded, and legitimate reference motion is
    /// subtracted before temporal error is accumulated.
    /// </summary>
    public static class V1TemporalMetricRunner
    {
        private const int Seed = 12345;
        private const int RailFrameCount = 600;
        private const float FrameRate = 60.0f;
        private const float RelativeDepthThreshold = 0.02f;
        private const float ShimmerMinimumHz = 2.0f;
        private const float ShimmerMaximumHz = 30.0f;

        [MenuItem("OSFR/Capture/Run V1 Temporal Smoke")]
        public static void RunSmokeFromMenu()
        {
            Run(RunConfiguration.Smoke, logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V1 720p Temporal Sequence")]
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
                Debug.LogError("OSFR temporal metrics require a real graphics device.");
                return null;
            }

            SceneSetup[] originalSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            if (!OpenOrCreateValidationScene())
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

            if (camera == null || rail == null || feature == null
                || checkerRoot == null || boundaryRoot == null)
            {
                Debug.LogError("The V1 temporal scene is missing its camera, rail, feature, or case roots.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"{configuration.RunName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            string sequenceFolder = Path.Combine(outputRoot, "checkerboard_lateral");
            Directory.CreateDirectory(sequenceFolder);

            MeasurementRendererFeature.Settings settings = feature.FeatureSettings;
            MeasurementDebugView previousView = settings.debugView;
            int previousMip = settings.displayedPyramidLevel;
            Vector2Int previousOverride = settings.outputResolutionOverride;
            float previousStrength = settings.bilateralStrength;
            float previousSpatialSigma = settings.bilateralSpatialSigmaPixels;
            float previousRiskStart = settings.riskFootprintStartWorld;
            float previousRiskEnd = settings.riskFootprintEndWorld;
            bool previousRailEnabled = rail.enabled;
            RenderTexture previousTarget = camera.targetTexture;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;

            try
            {
                rail.enabled = false;
                checkerRoot.SetActive(true);
                boundaryRoot.SetActive(false);
                UnityEngine.Random.InitState(Seed);
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, RailFrameCount, FrameRate);

                settings.displayedPyramidLevel = 0;
                settings.outputResolutionOverride = new Vector2Int(
                    configuration.Width,
                    configuration.Height);
                ApplyPromotedPreset(settings);

                SequenceResult sequence = CaptureSequence(
                    camera,
                    rail,
                    feature,
                    sequenceFolder,
                    configuration);
                WriteJson(Path.Combine(sequenceFolder, "metadata.json"), BuildMetadata(
                    feature,
                    configuration));

                var manifest = new RunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = configuration.MatrixName,
                    scene = "V1_ScreenSpaceValidation",
                    caseName = "CheckerboardWall",
                    createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    output = new[] { configuration.Width, configuration.Height },
                    startFrame = configuration.StartFrame,
                    frameCount = configuration.FrameCount,
                    referenceFactor = configuration.ReferenceFactor,
                    persistedFrameStride = configuration.PersistedFrameStride,
                    metadata = "checkerboard_lateral/metadata.json",
                    frameMetrics = "checkerboard_lateral/frame_metrics.csv",
                    temporalSummary = "checkerboard_lateral/temporal_summary.csv",
                    elapsedMilliseconds = sequence.ElapsedMilliseconds
                };
                WriteJson(Path.Combine(outputRoot, "manifest.json"), manifest);
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning($"OSFR temporal capture was cancelled. Partial output remains at {outputRoot}.");
                return null;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                settings.debugView = previousView;
                settings.displayedPyramidLevel = previousMip;
                settings.outputResolutionOverride = previousOverride;
                settings.bilateralStrength = previousStrength;
                settings.bilateralSpatialSigmaPixels = previousSpatialSigma;
                settings.riskFootprintStartWorld = previousRiskStart;
                settings.riskFootprintEndWorld = previousRiskEnd;
                rail.enabled = previousRailEnabled;
                camera.targetTexture = previousTarget;
                UnityEngine.Random.state = previousRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V1 temporal metrics at {outputRoot}.");
            }

            return outputRoot;
        }

        private static SequenceResult CaptureSequence(
            Camera camera,
            DeterministicCameraRail rail,
            MeasurementRendererFeature feature,
            string sequenceFolder,
            RunConfiguration configuration)
        {
            var rawAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var bilateralAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var frameRows = new List<string>(configuration.FrameCount * 2);
            Color[] previousRaw = null;
            Color[] previousBilateral = null;
            Color[] previousReference = null;
            Color[] previousDepth = null;
            Matrix4x4 previousGpuViewProjection = Matrix4x4.identity;
            var stopwatch = Stopwatch.StartNew();

            for (int localFrame = 0; localFrame < configuration.FrameCount; localFrame++)
            {
                int railFrame = configuration.StartFrame + localFrame;
                if (!Application.isBatchMode
                    && EditorUtility.DisplayCancelableProgressBar(
                        "OSFR V1 temporal sequence",
                        $"Capturing frame {localFrame + 1}/{configuration.FrameCount}",
                        (localFrame + 0.5f) / configuration.FrameCount))
                {
                    throw new OperationCanceledException();
                }

                rail.ApplyFrame(railFrame);
                bool persist = localFrame == 0
                    || localFrame == configuration.FrameCount - 1
                    || localFrame % configuration.PersistedFrameStride == 0;
                string suffix = railFrame.ToString("000000", CultureInfo.InvariantCulture);

                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                TiledLinearHdrReferenceRenderer.Result referenceResult =
                    TiledLinearHdrReferenceRenderer.Capture(
                        camera,
                        configuration.Width,
                        configuration.Height,
                        configuration.ReferenceFactor,
                        configuration.TargetTileEdge,
                        persist ? Path.Combine(sequenceFolder, $"reference_{suffix}.exr") : null);

                Color[] raw = CaptureBuffer(
                    camera,
                    feature,
                    MeasurementDebugView.PyramidMip,
                    configuration.Width,
                    configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_raw_{suffix}.exr") : null);
                Color[] bilateral = CaptureBuffer(
                    camera,
                    feature,
                    MeasurementDebugView.BilateralFilteredColor,
                    configuration.Width,
                    configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_bilateral_{suffix}.exr") : null);
                Color[] worldPositions = CaptureBuffer(
                    camera,
                    feature,
                    MeasurementDebugView.WorldPositionData,
                    configuration.Width,
                    configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"world_{suffix}.exr") : null);
                Color[] depth = CaptureBuffer(
                    camera,
                    feature,
                    MeasurementDebugView.LinearEyeDepthData,
                    configuration.Width,
                    configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"depth_{suffix}.exr") : null);

                Color[] motion = previousRaw == null
                    ? new Color[configuration.Width * configuration.Height]
                    : TemporalMetricMath.BuildStaticCameraMotionVectors(
                        worldPositions,
                        depth,
                        previousGpuViewProjection,
                        configuration.Width,
                        configuration.Height);
                if (persist)
                {
                    WritePixels(Path.Combine(sequenceFolder, $"motion_{suffix}.exr"), motion,
                        configuration.Width, configuration.Height);
                }

                TemporalMetricMath.FrameResult rawFrame =
                    TemporalMetricMath.EvaluateFrame(raw, referenceResult.Pixels);
                TemporalMetricMath.FrameResult bilateralFrame =
                    TemporalMetricMath.EvaluateFrame(bilateral, referenceResult.Pixels);
                rawAccumulator.AddFrame(rawFrame);
                bilateralAccumulator.AddFrame(bilateralFrame);

                TemporalMetricMath.TransitionResult? rawTransition = null;
                TemporalMetricMath.TransitionResult? bilateralTransition = null;
                if (previousRaw != null)
                {
                    rawTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousRaw,
                        raw,
                        previousReference,
                        referenceResult.Pixels,
                        motion,
                        previousDepth,
                        depth,
                        configuration.Width,
                        configuration.Height,
                        RelativeDepthThreshold);
                    bilateralTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousBilateral,
                        bilateral,
                        previousReference,
                        referenceResult.Pixels,
                        motion,
                        previousDepth,
                        depth,
                        configuration.Width,
                        configuration.Height,
                        RelativeDepthThreshold);
                    rawAccumulator.AddTransition(rawTransition.Value);
                    bilateralAccumulator.AddTransition(bilateralTransition.Value);
                }

                frameRows.Add(FrameRow(
                    "Raw",
                    railFrame,
                    rawFrame,
                    rawTransition,
                    referenceResult.ElapsedMilliseconds,
                    persist));
                frameRows.Add(FrameRow(
                    "Bilateral",
                    railFrame,
                    bilateralFrame,
                    bilateralTransition,
                    referenceResult.ElapsedMilliseconds,
                    persist));

                previousRaw = raw;
                previousBilateral = bilateral;
                previousReference = referenceResult.Pixels;
                previousDepth = depth;
                previousGpuViewProjection = GL.GetGPUProjectionMatrix(
                    camera.projectionMatrix,
                    true) * camera.worldToCameraMatrix;
            }

            stopwatch.Stop();
            WriteFrameMetrics(sequenceFolder, frameRows);
            TemporalMetricMath.SequenceSummary rawSummary = rawAccumulator.BuildSummary(
                FrameRate,
                ShimmerMinimumHz,
                ShimmerMaximumHz);
            TemporalMetricMath.SequenceSummary bilateralSummary = bilateralAccumulator.BuildSummary(
                FrameRate,
                ShimmerMinimumHz,
                ShimmerMaximumHz);
            WriteTemporalSummary(sequenceFolder, rawSummary, bilateralSummary);
            return new SequenceResult((float)stopwatch.Elapsed.TotalMilliseconds);
        }

        private static Color[] CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            MeasurementDebugView view,
            int width,
            int height,
            string outputPath)
        {
            var target = new RenderTexture(
                width,
                height,
                24,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readback = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousActive = RenderTexture.active;

            try
            {
                feature.FeatureSettings.debugView = view;
                camera.targetTexture = target;
                camera.ResetAspect();
                camera.ResetProjectionMatrix();
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                if (!string.IsNullOrEmpty(outputPath))
                {
                    WriteTexture(outputPath, readback);
                }

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

        private static void WritePixels(string path, Color[] pixels, int width, int height)
        {
            var texture = new Texture2D(width, height, TextureFormat.RGBAFloat, false, true);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                WriteTexture(path, texture);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(texture);
            }
        }

        private static void WriteTexture(string path, Texture2D texture)
        {
            byte[] exr = texture.EncodeToEXR(
                Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
            File.WriteAllBytes(path, exr);
        }

        private static void WriteFrameMetrics(string folder, IReadOnlyList<string> rows)
        {
            string csv = "algorithm,frame,residual_rmse,mean_signed_residual,mean_absolute_residual,"
                + "valid_temporal_pixels,valid_temporal_fraction,temporal_rmse,reference_render_ms,persisted\n"
                + string.Concat(rows);
            File.WriteAllText(Path.Combine(folder, "frame_metrics.csv"), csv);
        }

        private static string FrameRow(
            string algorithm,
            int frame,
            TemporalMetricMath.FrameResult frameResult,
            TemporalMetricMath.TransitionResult? transition,
            float referenceMilliseconds,
            bool persisted)
        {
            return string.Join(",", new[]
            {
                algorithm,
                frame.ToString(CultureInfo.InvariantCulture),
                Format(frameResult.RootMeanSquareError),
                Format(frameResult.MeanSignedError),
                Format(frameResult.MeanAbsoluteError),
                transition?.ValidPixelCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                transition.HasValue ? Format(transition.Value.ValidFraction) : string.Empty,
                transition.HasValue ? Format(transition.Value.RootMeanSquareError) : string.Empty,
                Format(referenceMilliseconds),
                persisted ? "true" : "false"
            }) + "\n";
        }

        private static void WriteTemporalSummary(
            string folder,
            TemporalMetricMath.SequenceSummary raw,
            TemporalMetricMath.SequenceSummary bilateral)
        {
            string csv = "algorithm,frames,transitions,residual_rmse,motion_compensated_temporal_rmse,"
                + "valid_temporal_fraction,shimmer_band_hz,shimmer_band_power,total_ac_power,shimmer_fraction\n"
                + SummaryRow("Raw", raw)
                + SummaryRow("Bilateral", bilateral);
            File.WriteAllText(Path.Combine(folder, "temporal_summary.csv"), csv);
        }

        private static string SummaryRow(
            string algorithm,
            TemporalMetricMath.SequenceSummary summary)
        {
            return string.Join(",", new[]
            {
                algorithm,
                summary.FrameCount.ToString(CultureInfo.InvariantCulture),
                summary.TransitionCount.ToString(CultureInfo.InvariantCulture),
                Format(summary.ResidualRootMeanSquare),
                Format(summary.TemporalRootMeanSquare),
                Format(summary.ValidTemporalFraction),
                $"{Format(ShimmerMinimumHz)}-{Format(ShimmerMaximumHz)}",
                Format(summary.ShimmerSpectrum.BandPower),
                Format(summary.ShimmerSpectrum.TotalAlternatingPower),
                Format(summary.ShimmerSpectrum.BandFraction)
            }) + "\n";
        }

        private static TemporalMetadata BuildMetadata(
            MeasurementRendererFeature feature,
            RunConfiguration configuration)
        {
            return new TemporalMetadata
            {
                schemaVersion = 1,
                scene = "CheckerboardWall",
                cameraPath = "Lateral",
                output = new[] { configuration.Width, configuration.Height },
                render = new[] { configuration.Width, configuration.Height },
                startFrame = configuration.StartFrame,
                frameCount = configuration.FrameCount,
                frameRate = FrameRate,
                fovY = 60.0f,
                aa = "None",
                fixedSeed = Seed,
                colorSpace = "Linear HDR",
                filterPreset = "V1_5x5_Balanced720p_01",
                bilateralStrength = feature.FeatureSettings.bilateralStrength,
                bilateralSpatialSigmaPixels = feature.FeatureSettings.bilateralSpatialSigmaPixels,
                riskFootprintStartWorld = feature.FeatureSettings.riskFootprintStartWorld,
                riskFootprintEndWorld = feature.FeatureSettings.riskFootprintEndWorld,
                referenceFactor = configuration.ReferenceFactor,
                referenceQualification = "Temporal comparison reference. Spatial 8x/16x convergence remains a separate recorded qualification.",
                motionConvention = "Forward screen-UV motion; previousUv = currentUv - motionUv.",
                motionSource = "Static-scene reprojection of the GPU world-position buffer through the previous GPU view-projection matrix. This remains deterministic across multiple manual camera renders per editor frame; object motion is not represented.",
                disocclusionRule = $"Reject when relative warped linear-eye-depth difference exceeds {Format(RelativeDepthThreshold)} or either depth is background.",
                temporalMetric = "Motion-compensated luminance delta error: (current - warped previous) candidate minus the same reference delta.",
                shimmerMetric = "Project-specific DFT of the per-frame full-reference luminance-RMSE envelope after DC removal.",
                shimmerBandHz = new[] { ShimmerMinimumHz, ShimmerMaximumHz },
                persistedFrameStride = configuration.PersistedFrameStride
            };
        }

        private static void ApplyPromotedPreset(MeasurementRendererFeature.Settings settings)
        {
            settings.bilateralStrength = 0.75f;
            settings.bilateralSpatialSigmaPixels = 1.75f;
            settings.riskFootprintStartWorld = 0.002f;
            settings.riskFootprintEndWorld = 0.02f;
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

        private static bool OpenOrCreateValidationScene()
        {
            SceneAsset existing = AssetDatabase.LoadAssetAtPath<SceneAsset>(
                V1ValidationSceneBuilder.ScenePath);
            if (existing == null)
            {
                return V1ValidationSceneBuilder.CreateOrRebuild(logSuccess: false);
            }

            Scene scene = EditorSceneManager.OpenScene(
                V1ValidationSceneBuilder.ScenePath,
                OpenSceneMode.Single);
            if (!scene.IsValid())
            {
                Debug.LogError($"Could not open {V1ValidationSceneBuilder.ScenePath}.");
                return false;
            }

            return true;
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

        private readonly struct RunConfiguration
        {
            public RunConfiguration(
                string runName,
                string matrixName,
                int width,
                int height,
                int startFrame,
                int frameCount,
                int referenceFactor,
                int targetTileEdge,
                int persistedFrameStride)
            {
                RunName = runName;
                MatrixName = matrixName;
                Width = width;
                Height = height;
                StartFrame = startFrame;
                FrameCount = frameCount;
                ReferenceFactor = referenceFactor;
                TargetTileEdge = targetTileEdge;
                PersistedFrameStride = persistedFrameStride;
            }

            public static RunConfiguration Smoke => new RunConfiguration(
                "temporal_smoke",
                "24_frame_320x180_temporal_smoke",
                320,
                180,
                288,
                24,
                2,
                128,
                8);

            public static RunConfiguration Primary => new RunConfiguration(
                "temporal_720p",
                "600_frame_720p_temporal_sequence",
                1280,
                720,
                0,
                600,
                4,
                128,
                1);

            public string RunName { get; }
            public string MatrixName { get; }
            public int Width { get; }
            public int Height { get; }
            public int StartFrame { get; }
            public int FrameCount { get; }
            public int ReferenceFactor { get; }
            public int TargetTileEdge { get; }
            public int PersistedFrameStride { get; }
        }

        private readonly struct SequenceResult
        {
            public SequenceResult(float elapsedMilliseconds)
            {
                ElapsedMilliseconds = elapsedMilliseconds;
            }

            public float ElapsedMilliseconds { get; }
        }

        [Serializable]
        private sealed class TemporalMetadata
        {
            public int schemaVersion;
            public string scene;
            public string cameraPath;
            public int[] output;
            public int[] render;
            public int startFrame;
            public int frameCount;
            public float frameRate;
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
            public string motionConvention;
            public string motionSource;
            public string disocclusionRule;
            public string temporalMetric;
            public string shimmerMetric;
            public float[] shimmerBandHz;
            public int persistedFrameStride;
        }

        [Serializable]
        private sealed class RunManifest
        {
            public int schemaVersion;
            public string runId;
            public string matrix;
            public string scene;
            public string caseName;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int[] output;
            public int startFrame;
            public int frameCount;
            public int referenceFactor;
            public int persistedFrameStride;
            public string metadata;
            public string frameMetrics;
            public string temporalSummary;
            public float elapsedMilliseconds;
        }
    }
}
