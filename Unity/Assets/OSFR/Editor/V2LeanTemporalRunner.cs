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
    /// Compares raw directional normals, the promoted scalar roughness transfer,
    /// and LEAN-like slope moments over the established deterministic lateral rail.
    /// </summary>
    public static class V2LeanTemporalRunner
    {
        private const int Seed = 12345;
        private const int RailFrameCount = 600;
        private const float FrameRate = 60.0f;
        private const float RelativeDepthThreshold = 0.02f;
        private const float ShimmerMinimumHz = 2.0f;
        private const float ShimmerMaximumHz = 30.0f;

        [MenuItem("OSFR/Capture/Run V2 LEAN Smith Temporal A-B Smoke")]
        public static void RunSmokeFromMenu()
        {
            Run(RunConfiguration.Smoke, logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V2 720p LEAN Smith Temporal A-B")]
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
                Debug.LogError("OSFR V2 LEAN temporal metrics require a real graphics device.");
                return null;
            }

            SceneSetup[] originalSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            if (!V2LeanValidationSceneBuilder.CreateOrRebuild(logSuccess: false))
            {
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            Camera camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            DeterministicCameraRail rail = UnityEngine.Object.FindFirstObjectByType<DeterministicCameraRail>();
            MeasurementRendererFeature feature = FindMeasurementFeature();
            GameObject root = SceneManager.GetActiveScene().GetRootGameObjects()
                .FirstOrDefault(item => item.name == V2LeanValidationSceneBuilder.PanelRootName);
            Material[] materials = root == null
                ? Array.Empty<Material>()
                : root.GetComponentsInChildren<Renderer>()
                    .Select(renderer => renderer.sharedMaterial)
                    .Where(material => material != null)
                    .Distinct()
                    .ToArray();
            if (camera == null || rail == null || feature == null || materials.Length != 9)
            {
                Debug.LogError("The V2 LEAN temporal scene is missing its camera, rail, feature, or materials.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"{configuration.RunName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V2", runId);
            string sequenceFolder = Path.Combine(outputRoot, "lean_panels_lateral");
            Directory.CreateDirectory(sequenceFolder);

            MeasurementRendererFeature.Settings settings = feature.FeatureSettings;
            MeasurementDebugView previousView = settings.debugView;
            int previousMip = settings.displayedPyramidLevel;
            Vector2Int previousOverride = settings.outputResolutionOverride;
            bool previousRailEnabled = rail.enabled;
            RenderTexture previousTarget = camera.targetTexture;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;
            float[] previousFilter = materials.Select(material => material.GetFloat("_FilterEnabled")).ToArray();
            float[] previousMode = materials.Select(material => material.GetFloat("_FilterMode")).ToArray();
            float[] previousDebug = materials.Select(material => material.GetFloat("_DebugMode")).ToArray();
            float[] previousGain = materials.Select(material => material.GetFloat("_VarianceGain")).ToArray();
            float[] previousLeanGain = materials.Select(material => material.GetFloat("_LeanVarianceGain")).ToArray();
            float[] previousVisibility = materials.Select(material => material.GetFloat("_LeanVisibilityMode")).ToArray();

            try
            {
                rail.enabled = false;
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, RailFrameCount, FrameRate);
                UnityEngine.Random.InitState(Seed);
                settings.displayedPyramidLevel = 0;
                settings.outputResolutionOverride = new Vector2Int(configuration.Width, configuration.Height);
                SetMaterialMode(materials, mode: 0, debugMode: 0, visibilityMode: 1);

                SequenceResult sequence = CaptureSequence(
                    camera, rail, feature, materials, sequenceFolder, configuration);
                WriteJson(Path.Combine(sequenceFolder, "metadata.json"), BuildMetadata(configuration));
                WriteJson(Path.Combine(outputRoot, "manifest.json"), new RunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = configuration.MatrixName,
                    scene = "V2_LeanFiltering",
                    caseName = "LeanDirectionalNormalPanels",
                    createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    output = new[] { configuration.Width, configuration.Height },
                    startFrame = configuration.StartFrame,
                    frameCount = configuration.FrameCount,
                    referenceFactor = configuration.ReferenceFactor,
                    persistedFrameStride = configuration.PersistedFrameStride,
                    metadata = "lean_panels_lateral/metadata.json",
                    frameMetrics = "lean_panels_lateral/frame_metrics.csv",
                    temporalSummary = "lean_panels_lateral/temporal_summary.csv",
                    elapsedMilliseconds = sequence.ElapsedMilliseconds
                });
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning($"OSFR V2 LEAN temporal capture was cancelled. Partial output remains at {outputRoot}.");
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
                for (int index = 0; index < materials.Length; index++)
                {
                    materials[index].SetFloat("_FilterEnabled", previousFilter[index]);
                    materials[index].SetFloat("_FilterMode", previousMode[index]);
                    materials[index].SetFloat("_DebugMode", previousDebug[index]);
                    materials[index].SetFloat("_VarianceGain", previousGain[index]);
                    materials[index].SetFloat("_LeanVarianceGain", previousLeanGain[index]);
                    materials[index].SetFloat("_LeanVisibilityMode", previousVisibility[index]);
                }

                settings.debugView = previousView;
                settings.displayedPyramidLevel = previousMip;
                settings.outputResolutionOverride = previousOverride;
                rail.enabled = previousRailEnabled;
                camera.targetTexture = previousTarget;
                UnityEngine.Random.state = previousRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V2 LEAN temporal metrics at {outputRoot}.");
            }

            return outputRoot;
        }

        private static SequenceResult CaptureSequence(
            Camera camera,
            DeterministicCameraRail rail,
            MeasurementRendererFeature feature,
            Material[] materials,
            string sequenceFolder,
            RunConfiguration configuration)
        {
            var rawAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var scalarAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var leanApproximateAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var leanSmithAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var frameRows = new List<string>(configuration.FrameCount * 4);
            Color[] previousRaw = null;
            Color[] previousScalar = null;
            Color[] previousLeanApproximate = null;
            Color[] previousLeanSmith = null;
            Color[] previousReference = null;
            Color[] previousDepth = null;
            Matrix4x4 previousGpuViewProjection = Matrix4x4.identity;
            var stopwatch = Stopwatch.StartNew();

            for (int localFrame = 0; localFrame < configuration.FrameCount; localFrame++)
            {
                int railFrame = configuration.StartFrame + localFrame;
                if (!Application.isBatchMode
                    && EditorUtility.DisplayCancelableProgressBar(
                        "OSFR V2 LEAN temporal sequence",
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

                SetMaterialMode(materials, mode: 0, debugMode: 0, visibilityMode: 1);
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
                    camera, feature, MeasurementDebugView.PyramidMip,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_raw_{suffix}.exr") : null);

                SetMaterialMode(materials, mode: 1, debugMode: 0, visibilityMode: 1);
                Color[] scalar = CaptureBuffer(
                    camera, feature, MeasurementDebugView.PyramidMip,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_scalar_gain64_{suffix}.exr") : null);

                SetMaterialMode(materials, mode: 2, debugMode: 0, visibilityMode: 0);
                Color[] leanApproximate = CaptureBuffer(
                    camera, feature, MeasurementDebugView.PyramidMip,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_lean_geometric_mean_visibility_{suffix}.exr") : null);

                SetMaterialMode(materials, mode: 2, debugMode: 0, visibilityMode: 1);
                Color[] leanSmith = CaptureBuffer(
                    camera, feature, MeasurementDebugView.PyramidMip,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"color_lean_anisotropic_smith_{suffix}.exr") : null);

                SetMaterialMode(materials, mode: 0, debugMode: 0, visibilityMode: 1);
                Color[] worldPositions = CaptureBuffer(
                    camera, feature, MeasurementDebugView.WorldPositionData,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"world_{suffix}.exr") : null);
                Color[] depth = CaptureBuffer(
                    camera, feature, MeasurementDebugView.LinearEyeDepthData,
                    configuration.Width, configuration.Height,
                    persist ? Path.Combine(sequenceFolder, $"depth_{suffix}.exr") : null);
                int foregroundDepthPixels = depth.Count(pixel => pixel.r > 0.0f);
                if (foregroundDepthPixels == 0)
                {
                    throw new InvalidOperationException(
                        $"V2 LEAN temporal frame {railFrame} contains no foreground depth pixels.");
                }

                if (localFrame == 0)
                {
                    Debug.Log($"OSFR V2 LEAN temporal foreground depth coverage: "
                        + $"{foregroundDepthPixels}/{depth.Length} pixels.");
                }

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
                TemporalMetricMath.FrameResult scalarFrame =
                    TemporalMetricMath.EvaluateFrame(scalar, referenceResult.Pixels);
                TemporalMetricMath.FrameResult leanApproximateFrame =
                    TemporalMetricMath.EvaluateFrame(leanApproximate, referenceResult.Pixels);
                TemporalMetricMath.FrameResult leanSmithFrame =
                    TemporalMetricMath.EvaluateFrame(leanSmith, referenceResult.Pixels);
                rawAccumulator.AddFrame(rawFrame);
                scalarAccumulator.AddFrame(scalarFrame);
                leanApproximateAccumulator.AddFrame(leanApproximateFrame);
                leanSmithAccumulator.AddFrame(leanSmithFrame);

                TemporalMetricMath.TransitionResult? rawTransition = null;
                TemporalMetricMath.TransitionResult? scalarTransition = null;
                TemporalMetricMath.TransitionResult? leanApproximateTransition = null;
                TemporalMetricMath.TransitionResult? leanSmithTransition = null;
                if (previousRaw != null)
                {
                    rawTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousRaw, raw, previousReference, referenceResult.Pixels,
                        motion, previousDepth, depth,
                        configuration.Width, configuration.Height, RelativeDepthThreshold);
                    scalarTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousScalar, scalar, previousReference, referenceResult.Pixels,
                        motion, previousDepth, depth,
                        configuration.Width, configuration.Height, RelativeDepthThreshold);
                    leanApproximateTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousLeanApproximate, leanApproximate,
                        previousReference, referenceResult.Pixels,
                        motion, previousDepth, depth,
                        configuration.Width, configuration.Height, RelativeDepthThreshold);
                    leanSmithTransition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousLeanSmith, leanSmith, previousReference, referenceResult.Pixels,
                        motion, previousDepth, depth,
                        configuration.Width, configuration.Height, RelativeDepthThreshold);
                    rawAccumulator.AddTransition(rawTransition.Value);
                    scalarAccumulator.AddTransition(scalarTransition.Value);
                    leanApproximateAccumulator.AddTransition(leanApproximateTransition.Value);
                    leanSmithAccumulator.AddTransition(leanSmithTransition.Value);
                }

                frameRows.Add(FrameRow(
                    "Raw", railFrame, rawFrame, rawTransition,
                    referenceResult.ElapsedMilliseconds, persist));
                frameRows.Add(FrameRow(
                    "ScalarGain64", railFrame, scalarFrame, scalarTransition,
                    referenceResult.ElapsedMilliseconds, persist));
                frameRows.Add(FrameRow(
                    "LEANGeometricMeanVisibility", railFrame,
                    leanApproximateFrame, leanApproximateTransition,
                    referenceResult.ElapsedMilliseconds, persist));
                frameRows.Add(FrameRow(
                    "LEANAnisotropicSmith", railFrame, leanSmithFrame, leanSmithTransition,
                    referenceResult.ElapsedMilliseconds, persist));

                previousRaw = raw;
                previousScalar = scalar;
                previousLeanApproximate = leanApproximate;
                previousLeanSmith = leanSmith;
                previousReference = referenceResult.Pixels;
                previousDepth = depth;
                previousGpuViewProjection = GL.GetGPUProjectionMatrix(
                    camera.projectionMatrix, true) * camera.worldToCameraMatrix;
            }

            stopwatch.Stop();
            WriteFrameMetrics(sequenceFolder, frameRows);
            WriteTemporalSummary(
                sequenceFolder,
                rawAccumulator.BuildSummary(FrameRate, ShimmerMinimumHz, ShimmerMaximumHz),
                scalarAccumulator.BuildSummary(FrameRate, ShimmerMinimumHz, ShimmerMaximumHz),
                leanApproximateAccumulator.BuildSummary(
                    FrameRate, ShimmerMinimumHz, ShimmerMaximumHz),
                leanSmithAccumulator.BuildSummary(
                    FrameRate, ShimmerMinimumHz, ShimmerMaximumHz));
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
                width, height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
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
            File.WriteAllBytes(path, texture.EncodeToEXR(
                Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
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
            TemporalMetricMath.SequenceSummary scalar,
            TemporalMetricMath.SequenceSummary leanApproximate,
            TemporalMetricMath.SequenceSummary leanSmith)
        {
            string csv = "algorithm,frames,transitions,residual_rmse,motion_compensated_temporal_rmse,"
                + "valid_temporal_fraction,shimmer_band_hz,shimmer_band_power,total_ac_power,shimmer_fraction\n"
                + SummaryRow("Raw", raw)
                + SummaryRow("ScalarGain64", scalar)
                + SummaryRow("LEANGeometricMeanVisibility", leanApproximate)
                + SummaryRow("LEANAnisotropicSmith", leanSmith);
            File.WriteAllText(Path.Combine(folder, "temporal_summary.csv"), csv);
        }

        private static string SummaryRow(string algorithm, TemporalMetricMath.SequenceSummary summary)
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

        private static TemporalMetadata BuildMetadata(RunConfiguration configuration)
        {
            return new TemporalMetadata
            {
                schemaVersion = 1,
                scene = "LeanDirectionalNormalPanels",
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
                candidates = new[]
                {
                    "Raw",
                    "ScalarGain64",
                    "LEANGeometricMeanVisibility",
                    "LEANAnisotropicSmith"
                },
                scalarVarianceGain = V2ValidationSceneBuilder.PromotedVarianceGain,
                leanVarianceGain = V2LeanValidationSceneBuilder.InitialLeanVarianceGain,
                directionalPatternAngleDegrees = V2LeanValidationSceneBuilder.PatternAngleDegrees,
                scalarFilter = "3x3 output-pixel-footprint normal mean; variance=(1-|mean|)/max(|mean|,epsilon); alpha_eff=sqrt(alpha_base^2+gain*variance).",
                leanFilter = "3x3 output-footprint first/second slope moments; covariance eigenvectors orient alpha_major/alpha_minor; anisotropic GGX NDF. Controls compare geometric-mean alpha visibility against height-correlated anisotropic Smith-GGX visibility.",
                referenceFactor = configuration.ReferenceFactor,
                referenceQualification = "Temporal comparison reference. Spatial 8x/16x convergence remains a separate recorded qualification.",
                motionConvention = "Forward screen-UV motion; previousUv = currentUv - motionUv.",
                motionSource = "Static-scene reprojection of the GPU world-position buffer through the previous GPU view-projection matrix.",
                disocclusionRule = $"Reject when relative warped linear-eye-depth difference exceeds {Format(RelativeDepthThreshold)} or either depth is background.",
                temporalMetric = "Motion-compensated luminance delta error: (current - warped previous) candidate minus the same reference delta.",
                shimmerMetric = "Project-specific DFT of the per-frame full-reference luminance-RMSE envelope after DC removal.",
                shimmerBandHz = new[] { ShimmerMinimumHz, ShimmerMaximumHz },
                persistedFrameStride = configuration.PersistedFrameStride
            };
        }

        private static void SetMaterialMode(
            IEnumerable<Material> materials,
            int mode,
            int debugMode,
            int visibilityMode)
        {
            foreach (Material material in materials)
            {
                material.SetFloat("_FilterMode", mode);
                material.SetFloat("_FilterEnabled", mode == 1 ? 1.0f : 0.0f);
                material.SetFloat("_VarianceGain", V2ValidationSceneBuilder.PromotedVarianceGain);
                material.SetFloat("_LeanVarianceGain", V2LeanValidationSceneBuilder.InitialLeanVarianceGain);
                material.SetFloat("_LeanVisibilityMode", visibilityMode);
                material.SetFloat("_DebugMode", debugMode);
            }
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
                "lean_smith_temporal_ab_smoke",
                "24_frame_320x180_lean_smith_temporal_ab_smoke",
                320, 180, 288, 24, 2, 128, 8);

            public static RunConfiguration Primary => new RunConfiguration(
                "lean_smith_temporal_ab_720p",
                "600_frame_720p_lean_smith_temporal_ab_sequence",
                1280, 720, 0, 600, 4, 128, 1);

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
            public string[] candidates;
            public float scalarVarianceGain;
            public float leanVarianceGain;
            public float directionalPatternAngleDegrees;
            public string scalarFilter;
            public string leanFilter;
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
