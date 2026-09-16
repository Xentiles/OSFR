using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OSFR.Measurement;
using OSFR.Rendering;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace OSFR.Validation
{
    /// <summary>
    /// Captures native URP AA baselines over real engine frames. In particular,
    /// TAA is allowed to own its normal jitter and history rather than being
    /// approximated by repeated manual Camera.Render calls in one editor frame.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class V1NativeAaBaselineCaptureController : MonoBehaviour
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int RailFrameCount = 600;
        private const float FrameRate = 60.0f;
        private const float RelativeDepthThreshold = 0.02f;
        private const float ShimmerMinimumHz = 2.0f;
        private const float ShimmerMaximumHz = 30.0f;
        private const int TaaWarmupFrames = 8;
        private const int Seed = 12345;

        private enum Baseline
        {
            Smaa,
            Taa,
            Msaa4x
        }

        [SerializeField]
        private string m_SourceRunFolder;

        [SerializeField]
        private string m_OutputRoot;

        [SerializeField]
        private string m_RunId;

        [SerializeField]
        private int m_StartFrame;

        [SerializeField]
        private int m_FrameCount;

        [SerializeField]
        private int m_PersistedFrameStride = 1;

        [SerializeField]
        private bool m_ExitEditorWhenComplete;

        private Camera m_Camera;
        private UniversalAdditionalCameraData m_CameraData;
        private DeterministicCameraRail m_Rail;
        private MeasurementRendererFeature m_Feature;
        private UniversalRenderPipelineAsset m_Pipeline;
        private RenderTexture m_Target;
        private RenderTexture m_Resolved;
        private Texture2D m_Readback;
        private MeasurementDebugView m_PreviousView;
        private Vector2Int m_PreviousOutputOverride;
        private int m_PreviousMsaaSamples;
        private int m_PreviousCaptureFramerate;
        private int m_PreviousVSyncCount;
        private RenderTexture m_PreviousTarget;
        private bool m_PreviousAllowMsaa;
        private bool m_PreviousAllowDynamicResolution;
        private bool m_PreviousPostProcessing;
        private AntialiasingMode m_PreviousAntialiasing;
        private AntialiasingQuality m_PreviousAntialiasingQuality;
        private bool m_CleanupComplete;

        public void Configure(
            string sourceRunFolder,
            string outputRoot,
            string runId,
            int startFrame,
            int frameCount,
            int persistedFrameStride,
            bool exitEditorWhenComplete)
        {
            m_SourceRunFolder = sourceRunFolder;
            m_OutputRoot = outputRoot;
            m_RunId = runId;
            m_StartFrame = startFrame;
            m_FrameCount = frameCount;
            m_PersistedFrameStride = Mathf.Max(1, persistedFrameStride);
            m_ExitEditorWhenComplete = exitEditorWhenComplete;
        }

        private IEnumerator Start()
        {
            bool success = false;
            try
            {
                Initialize();
                var summaries = new List<AlgorithmSummary>(5);
                if (m_StartFrame == 0 && m_FrameCount == RailFrameCount)
                {
                    LoadCompleteSourceSummaries(summaries);
                }
                else
                {
                    yield return EvaluateSourceSubset(summaries);
                }
                foreach (Baseline baseline in new[] { Baseline.Smaa, Baseline.Taa, Baseline.Msaa4x })
                {
                    yield return CaptureBaseline(baseline, summaries);
                }

                WriteComparisonSummary(summaries);
                WriteManifest(summaries);
                success = true;
                Debug.Log($"Completed OSFR native AA baselines at {m_OutputRoot}.");
            }
            finally
            {
                Cleanup();
            }

#if UNITY_EDITOR
            if (m_ExitEditorWhenComplete)
            {
                EditorApplication.Exit(success ? 0 : 1);
            }
            else
            {
                EditorApplication.isPlaying = false;
            }
#endif
        }

        private void Initialize()
        {
            if (string.IsNullOrEmpty(m_SourceRunFolder)
                || string.IsNullOrEmpty(m_OutputRoot)
                || m_FrameCount <= 0
                || m_StartFrame < 0
                || m_StartFrame + m_FrameCount > RailFrameCount)
            {
                throw new InvalidOperationException("The native AA baseline controller was not configured correctly.");
            }

            string sourceSequence = SourceSequenceFolder();
            if (!Directory.Exists(sourceSequence)
                || !File.Exists(Path.Combine(sourceSequence, "reference_000000.exr")))
            {
                throw new DirectoryNotFoundException(
                    $"The required completed temporal reference sequence was not found at {sourceSequence}.");
            }

            m_Camera = FindFirstObjectByType<Camera>();
            m_Rail = FindFirstObjectByType<DeterministicCameraRail>();
            m_Feature = FindMeasurementFeature();
            m_Pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (m_Camera == null || m_Rail == null || m_Feature == null || m_Pipeline == null)
            {
                throw new InvalidOperationException("The V1 camera, rail, renderer feature, or URP asset is missing.");
            }

            m_CameraData = m_Camera.GetUniversalAdditionalCameraData();
            if (m_CameraData == null)
            {
                throw new InvalidOperationException("The V1 camera has no UniversalAdditionalCameraData.");
            }

            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            GameObject checkerRoot = roots.FirstOrDefault(
                root => root.name == V1ValidationNames.CheckerboardRootName);
            GameObject boundaryRoot = roots.FirstOrDefault(
                root => root.name == V1ValidationNames.DepthBoundaryRootName);
            if (checkerRoot == null || boundaryRoot == null)
            {
                throw new InvalidOperationException("The V1 validation case roots are missing.");
            }

            checkerRoot.SetActive(true);
            boundaryRoot.SetActive(false);
            m_Rail.enabled = false;
            m_Rail.Configure(DeterministicCameraRail.RailPath.Lateral, RailFrameCount, FrameRate);
            UnityEngine.Random.InitState(Seed);

            m_PreviousView = m_Feature.FeatureSettings.debugView;
            m_PreviousOutputOverride = m_Feature.FeatureSettings.outputResolutionOverride;
            m_PreviousMsaaSamples = m_Pipeline.msaaSampleCount;
            m_PreviousCaptureFramerate = Time.captureFramerate;
            m_PreviousVSyncCount = QualitySettings.vSyncCount;
            m_PreviousTarget = m_Camera.targetTexture;
            m_PreviousAllowMsaa = m_Camera.allowMSAA;
            m_PreviousAllowDynamicResolution = m_Camera.allowDynamicResolution;
            m_PreviousPostProcessing = m_CameraData.renderPostProcessing;
            m_PreviousAntialiasing = m_CameraData.antialiasing;
            m_PreviousAntialiasingQuality = m_CameraData.antialiasingQuality;

            m_Feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
            m_Feature.FeatureSettings.displayedPyramidLevel = 0;
            m_Feature.FeatureSettings.outputResolutionOverride = new Vector2Int(Width, Height);
            m_Camera.allowHDR = true;
            m_Camera.allowDynamicResolution = false;
            Time.captureFramerate = (int)FrameRate;
            QualitySettings.vSyncCount = 0;
            Directory.CreateDirectory(m_OutputRoot);
        }

        private void LoadCompleteSourceSummaries(ICollection<AlgorithmSummary> summaries)
        {
            string path = Path.Combine(SourceSequenceFolder(), "temporal_summary.csv");
            string[] lines = File.ReadAllLines(path);
            if (lines.Length < 3)
            {
                throw new InvalidDataException($"Source temporal summary is incomplete: {path}");
            }

            summaries.Add(ParseSummaryRow(lines[1]));
            summaries.Add(ParseSummaryRow(lines[2]));
        }

        private static AlgorithmSummary ParseSummaryRow(string row)
        {
            string[] columns = row.Split(',');
            if (columns.Length != 10)
            {
                throw new InvalidDataException($"Unexpected temporal summary row: {row}");
            }

            float bandPower = ParseFloat(columns[7]);
            float totalPower = ParseFloat(columns[8]);
            var summary = new TemporalMetricMath.SequenceSummary(
                int.Parse(columns[1], CultureInfo.InvariantCulture),
                int.Parse(columns[2], CultureInfo.InvariantCulture),
                ParseFloat(columns[3]),
                ParseFloat(columns[4]),
                ParseFloat(columns[5]),
                new TemporalMetricMath.SpectrumResult(bandPower, totalPower));
            return new AlgorithmSummary(columns[0], summary);
        }

        private IEnumerator EvaluateSourceSubset(ICollection<AlgorithmSummary> summaries)
        {
            var rawAccumulator = new TemporalMetricMath.SequenceAccumulator();
            var bilateralAccumulator = new TemporalMetricMath.SequenceAccumulator();
            Color[] previousRaw = null;
            Color[] previousBilateral = null;
            Color[] previousReference = null;
            Color[] previousDepth = null;

            for (int localFrame = 0; localFrame < m_FrameCount; localFrame++)
            {
                int railFrame = m_StartFrame + localFrame;
                Color[] raw = LoadExr(SourcePath("color_raw", railFrame));
                Color[] bilateral = LoadExr(SourcePath("color_bilateral", railFrame));
                Color[] reference = LoadExr(SourcePath("reference", railFrame));
                Color[] depth = LoadExr(SourcePath("depth", railFrame));
                Color[] motion = LoadExr(SourcePath("motion", railFrame));

                TemporalMetricMath.FrameResult rawFrame =
                    TemporalMetricMath.EvaluateFrame(raw, reference);
                TemporalMetricMath.FrameResult bilateralFrame =
                    TemporalMetricMath.EvaluateFrame(bilateral, reference);
                rawAccumulator.AddFrame(rawFrame);
                bilateralAccumulator.AddFrame(bilateralFrame);
                if (previousRaw != null)
                {
                    TemporalMetricMath.TransitionResult rawTransition =
                        TemporalMetricMath.EvaluateMotionCompensatedTransition(
                            previousRaw,
                            raw,
                            previousReference,
                            reference,
                            motion,
                            previousDepth,
                            depth,
                            Width,
                            Height,
                            RelativeDepthThreshold);
                    TemporalMetricMath.TransitionResult bilateralTransition =
                        TemporalMetricMath.EvaluateMotionCompensatedTransition(
                            previousBilateral,
                            bilateral,
                            previousReference,
                            reference,
                            motion,
                            previousDepth,
                            depth,
                            Width,
                            Height,
                            RelativeDepthThreshold);
                    rawAccumulator.AddTransition(rawTransition);
                    bilateralAccumulator.AddTransition(bilateralTransition);
                }

                previousRaw = raw;
                previousBilateral = bilateral;
                previousReference = reference;
                previousDepth = depth;
                if (localFrame % 16 == 15)
                {
                    yield return null;
                }
            }

            summaries.Add(new AlgorithmSummary(
                "Raw",
                rawAccumulator.BuildSummary(FrameRate, ShimmerMinimumHz, ShimmerMaximumHz)));
            summaries.Add(new AlgorithmSummary(
                "Bilateral",
                bilateralAccumulator.BuildSummary(FrameRate, ShimmerMinimumHz, ShimmerMaximumHz)));
        }

        private IEnumerator CaptureBaseline(
            Baseline baseline,
            ICollection<AlgorithmSummary> summaries)
        {
            ConfigureBaseline(baseline);
            string algorithm = BaselineName(baseline);
            string folder = Path.Combine(m_OutputRoot, algorithm.ToLowerInvariant());
            Directory.CreateDirectory(folder);

            m_Rail.ApplyFrame(m_StartFrame);
            int warmupCount = baseline == Baseline.Taa ? TaaWarmupFrames : 1;
            for (int warmup = 0; warmup < warmupCount; warmup++)
            {
                yield return null;
            }

            var accumulator = new TemporalMetricMath.SequenceAccumulator();
            var rows = new List<string>(m_FrameCount);
            Color[] previousCandidate = null;
            Color[] previousReference = null;
            Color[] previousDepth = null;
            int firstEngineFrame = -1;
            int previousEngineFrame = -1;
            int engineFrameDiscontinuities = 0;

            for (int localFrame = 0; localFrame < m_FrameCount; localFrame++)
            {
                int railFrame = m_StartFrame + localFrame;
                m_Rail.ApplyFrame(railFrame);
                yield return null;

                int engineFrame = Time.frameCount;
                if (firstEngineFrame < 0)
                {
                    firstEngineFrame = engineFrame;
                }
                else if (engineFrame <= previousEngineFrame)
                {
                    throw new InvalidOperationException(
                        $"{algorithm} did not advance Unity engine frames; temporal history would be invalid.");
                }
                else if (engineFrame != previousEngineFrame + 1)
                {
                    engineFrameDiscontinuities++;
                }

                previousEngineFrame = engineFrame;
                bool persist = localFrame == 0
                    || localFrame == m_FrameCount - 1
                    || localFrame % m_PersistedFrameStride == 0;
                string suffix = railFrame.ToString("000000", CultureInfo.InvariantCulture);
                Color[] candidate = ReadCandidate(
                    persist ? Path.Combine(folder, $"color_{algorithm.ToLowerInvariant()}_{suffix}.exr") : null);
                Color[] reference = LoadExr(SourcePath("reference", railFrame));
                Color[] depth = LoadExr(SourcePath("depth", railFrame));
                Color[] motion = LoadExr(SourcePath("motion", railFrame));

                TemporalMetricMath.FrameResult frame =
                    TemporalMetricMath.EvaluateFrame(candidate, reference);
                accumulator.AddFrame(frame);
                TemporalMetricMath.TransitionResult? transition = null;
                if (previousCandidate != null)
                {
                    transition = TemporalMetricMath.EvaluateMotionCompensatedTransition(
                        previousCandidate,
                        candidate,
                        previousReference,
                        reference,
                        motion,
                        previousDepth,
                        depth,
                        Width,
                        Height,
                        RelativeDepthThreshold);
                    accumulator.AddTransition(transition.Value);
                }

                rows.Add(FrameRow(
                    algorithm,
                    railFrame,
                    engineFrame,
                    frame,
                    transition,
                    persist));
                previousCandidate = candidate;
                previousReference = reference;
                previousDepth = depth;
            }

            TemporalMetricMath.SequenceSummary summary = accumulator.BuildSummary(
                FrameRate,
                ShimmerMinimumHz,
                ShimmerMaximumHz);
            WriteFrameMetrics(folder, rows);
            WriteAlgorithmMetadata(
                folder,
                baseline,
                firstEngineFrame,
                previousEngineFrame,
                engineFrameDiscontinuities);
            summaries.Add(new AlgorithmSummary(algorithm, summary));
            ReleaseTargets();
        }

        private void ConfigureBaseline(Baseline baseline)
        {
            ReleaseTargets();
            bool msaa = baseline == Baseline.Msaa4x;
            m_Pipeline.msaaSampleCount = msaa ? 4 : 1;
            m_Camera.allowMSAA = msaa;
            m_CameraData.renderPostProcessing = !msaa;
            m_CameraData.antialiasing = baseline switch
            {
                Baseline.Smaa => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                Baseline.Taa => AntialiasingMode.TemporalAntiAliasing,
                _ => AntialiasingMode.None
            };
            m_CameraData.antialiasingQuality = AntialiasingQuality.High;

            var descriptor = new RenderTextureDescriptor(
                Width,
                Height,
                RenderTextureFormat.ARGBFloat,
                24)
            {
                msaaSamples = msaa ? 4 : 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false
            };
            m_Target = new RenderTexture(descriptor)
            {
                name = $"OSFR {BaselineName(baseline)} Target",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            m_Target.Create();
            m_Resolved = new RenderTexture(
                Width,
                Height,
                0,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear)
            {
                name = $"OSFR {BaselineName(baseline)} Resolved",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            m_Resolved.Create();
            m_Readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            m_Camera.targetTexture = m_Target;
            m_Camera.ResetAspect();
            m_Camera.ResetProjectionMatrix();
            if (baseline == Baseline.Taa)
            {
                m_CameraData.resetHistory = true;
            }
        }

        private Color[] ReadCandidate(string outputPath)
        {
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(m_Target, m_Resolved);
                RenderTexture.active = m_Resolved;
                m_Readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
                m_Readback.Apply(false, false);
                Color[] pixels = m_Readback.GetPixels();
                if (!string.IsNullOrEmpty(outputPath))
                {
                    byte[] exr = m_Readback.EncodeToEXR(
                        Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                    File.WriteAllBytes(outputPath, exr);
                }

                return pixels;
            }
            finally
            {
                RenderTexture.active = previousActive;
            }
        }

        private Color[] LoadExr(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("A source temporal frame is missing.", path);
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            try
            {
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)
                    || texture.width != Width
                    || texture.height != Height)
                {
                    throw new InvalidDataException($"Could not decode the expected {Width}x{Height} EXR: {path}");
                }

                return texture.GetPixels();
            }
            finally
            {
                Destroy(texture);
            }
        }

        private string SourceSequenceFolder()
        {
            return Path.Combine(m_SourceRunFolder, "checkerboard_lateral");
        }

        private string SourcePath(string kind, int frame)
        {
            return Path.Combine(
                SourceSequenceFolder(),
                $"{kind}_{frame:000000}.exr");
        }

        private void WriteFrameMetrics(string folder, IReadOnlyList<string> rows)
        {
            string csv = "algorithm,frame,engine_frame,residual_rmse,mean_signed_residual,"
                + "mean_absolute_residual,valid_temporal_pixels,valid_temporal_fraction,"
                + "temporal_rmse,persisted\n"
                + string.Concat(rows);
            File.WriteAllText(Path.Combine(folder, "frame_metrics.csv"), csv);
        }

        private static string FrameRow(
            string algorithm,
            int frame,
            int engineFrame,
            TemporalMetricMath.FrameResult frameResult,
            TemporalMetricMath.TransitionResult? transition,
            bool persisted)
        {
            return string.Join(",", new[]
            {
                algorithm,
                frame.ToString(CultureInfo.InvariantCulture),
                engineFrame.ToString(CultureInfo.InvariantCulture),
                Format(frameResult.RootMeanSquareError),
                Format(frameResult.MeanSignedError),
                Format(frameResult.MeanAbsoluteError),
                transition?.ValidPixelCount.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                transition.HasValue ? Format(transition.Value.ValidFraction) : string.Empty,
                transition.HasValue ? Format(transition.Value.RootMeanSquareError) : string.Empty,
                persisted ? "true" : "false"
            }) + "\n";
        }

        private void WriteAlgorithmMetadata(
            string folder,
            Baseline baseline,
            int firstEngineFrame,
            int lastEngineFrame,
            int engineFrameDiscontinuities)
        {
            var metadata = new AlgorithmMetadata
            {
                schemaVersion = 1,
                algorithm = BaselineName(baseline),
                aa = baseline == Baseline.Msaa4x ? "MSAA 4x" : BaselineName(baseline),
                antialiasingQuality = baseline == Baseline.Smaa ? "High" : "N/A",
                taaWarmupFrames = baseline == Baseline.Taa ? TaaWarmupFrames : 0,
                taaHistoryReset = baseline == Baseline.Taa,
                taaJitter = baseline == Baseline.Taa ? "URP native deterministic jitter" : "None",
                msaaSamples = baseline == Baseline.Msaa4x ? 4 : 1,
                firstEngineFrame = firstEngineFrame,
                lastEngineFrame = lastEngineFrame,
                engineFrameDiscontinuities = engineFrameDiscontinuities,
                sourceRunId = Path.GetFileName(m_SourceRunFolder),
                reference = "Per-frame 4x spatial reference from the completed raw/bilateral temporal run.",
                motion = "Static-scene forward UV reprojection stored by the source temporal run.",
                disocclusionRule = $"Reject background and relative warped depth difference > {Format(RelativeDepthThreshold)}."
            };
            WriteJson(Path.Combine(folder, "metadata.json"), metadata);
        }

        private void WriteComparisonSummary(IReadOnlyList<AlgorithmSummary> summaries)
        {
            string csv = "algorithm,frames,transitions,residual_rmse,motion_compensated_temporal_rmse,"
                + "valid_temporal_fraction,shimmer_band_hz,shimmer_band_power,total_ac_power,shimmer_fraction\n";
            foreach (AlgorithmSummary summary in summaries)
            {
                csv += SummaryRow(summary.Name, summary.Summary);
            }

            File.WriteAllText(Path.Combine(m_OutputRoot, "comparison_summary.csv"), csv);
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

        private void WriteManifest(IReadOnlyList<AlgorithmSummary> summaries)
        {
            var manifest = new RunManifest
            {
                schemaVersion = 1,
                runId = m_RunId,
                matrix = m_FrameCount == RailFrameCount
                    ? "600_frame_720p_native_aa_baselines"
                    : $"{m_FrameCount}_frame_720p_native_aa_smoke",
                scene = "V1_ScreenSpaceValidation",
                caseName = "CheckerboardWall",
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                output = new[] { Width, Height },
                startFrame = m_StartFrame,
                frameCount = m_FrameCount,
                sourceRunId = Path.GetFileName(m_SourceRunFolder),
                persistedFrameStride = m_PersistedFrameStride,
                algorithms = summaries.Select(summary => summary.Name).ToArray(),
                comparisonSummary = "comparison_summary.csv",
                taaHistoryPolicy = "Reset once, warm up for eight real engine frames at the starting pose, then preserve native history and jitter across the sequence.",
                limitation = "Disocclusions are excluded from tRMSE. A separate ghosting/disocclusion metric remains required for TAA."
            };
            WriteJson(Path.Combine(m_OutputRoot, "manifest.json"), manifest);
        }

        private void Cleanup()
        {
            if (m_CleanupComplete)
            {
                return;
            }

            m_CleanupComplete = true;
            ReleaseTargets();
            if (m_Feature != null)
            {
                m_Feature.FeatureSettings.debugView = m_PreviousView;
                m_Feature.FeatureSettings.outputResolutionOverride = m_PreviousOutputOverride;
            }

            if (m_Pipeline != null)
            {
                m_Pipeline.msaaSampleCount = m_PreviousMsaaSamples;
            }

            if (m_Camera != null)
            {
                m_Camera.targetTexture = m_PreviousTarget;
                m_Camera.allowMSAA = m_PreviousAllowMsaa;
                m_Camera.allowDynamicResolution = m_PreviousAllowDynamicResolution;
            }

            if (m_CameraData != null)
            {
                m_CameraData.renderPostProcessing = m_PreviousPostProcessing;
                m_CameraData.antialiasing = m_PreviousAntialiasing;
                m_CameraData.antialiasingQuality = m_PreviousAntialiasingQuality;
            }

            Time.captureFramerate = m_PreviousCaptureFramerate;
            QualitySettings.vSyncCount = m_PreviousVSyncCount;
        }

        private void OnDestroy()
        {
            Cleanup();
        }

        private void ReleaseTargets()
        {
            if (m_Camera != null && m_Camera.targetTexture == m_Target)
            {
                m_Camera.targetTexture = null;
            }

            if (m_Readback != null)
            {
                Destroy(m_Readback);
                m_Readback = null;
            }

            if (m_Resolved != null)
            {
                m_Resolved.Release();
                Destroy(m_Resolved);
                m_Resolved = null;
            }

            if (m_Target != null)
            {
                m_Target.Release();
                Destroy(m_Target);
                m_Target = null;
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

        private static string BaselineName(Baseline baseline)
        {
            return baseline switch
            {
                Baseline.Smaa => "SMAA",
                Baseline.Taa => "TAA",
                Baseline.Msaa4x => "MSAA4x",
                _ => baseline.ToString()
            };
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static float ParseFloat(string value)
        {
            return float.Parse(value, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, JsonUtility.ToJson(value, prettyPrint: true) + Environment.NewLine);
        }

        private readonly struct AlgorithmSummary
        {
            public AlgorithmSummary(string name, TemporalMetricMath.SequenceSummary summary)
            {
                Name = name;
                Summary = summary;
            }

            public string Name { get; }

            public TemporalMetricMath.SequenceSummary Summary { get; }
        }

        [Serializable]
        private sealed class AlgorithmMetadata
        {
            public int schemaVersion;
            public string algorithm;
            public string aa;
            public string antialiasingQuality;
            public int taaWarmupFrames;
            public bool taaHistoryReset;
            public string taaJitter;
            public int msaaSamples;
            public int firstEngineFrame;
            public int lastEngineFrame;
            public int engineFrameDiscontinuities;
            public string sourceRunId;
            public string reference;
            public string motion;
            public string disocclusionRule;
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
            public string sourceRunId;
            public int persistedFrameStride;
            public string[] algorithms;
            public string comparisonSummary;
            public string taaHistoryPolicy;
            public string limitation;
        }
    }

    /// <summary>
    /// Runtime-safe names shared by the play-mode baseline controller and editor builders.
    /// </summary>
    public static class V1ValidationNames
    {
        public const string CheckerboardRootName = "CASE - Checkerboard Wall";
        public const string DepthBoundaryRootName = "CASE - Depth Discontinuity Pole";
    }
}
