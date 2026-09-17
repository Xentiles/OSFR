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
    /// Measures native URP TAA on newly and recently disoccluded pixels in the
    /// V1 depth-boundary scene. Source reference, raw, bilateral, depth, and
    /// static-camera motion frames are supplied by the editor capture stage.
    /// </summary>
    [DisallowMultipleComponent]
    public sealed class V1TaaDisocclusionCaptureController : MonoBehaviour
    {
        private const float RelativeDepthThreshold = 0.02f;
        private const float MinimumHistoryContrast = 0.02f;
        private const int TaaWarmupFrames = 8;
        private const int Seed = 12345;

        [SerializeField] private string m_SourceRunFolder;
        [SerializeField] private string m_OutputRoot;
        [SerializeField] private string m_RunId;
        [SerializeField] private int m_Width;
        [SerializeField] private int m_Height;
        [SerializeField] private int m_StartFrame;
        [SerializeField] private int m_FrameCount;
        [SerializeField] private int m_MaximumAge;
        [SerializeField] private int m_PersistedFrameStride;
        [SerializeField] private bool m_ExitEditorWhenComplete;

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
            int width,
            int height,
            int startFrame,
            int frameCount,
            int maximumAge,
            int persistedFrameStride,
            bool exitEditorWhenComplete)
        {
            m_SourceRunFolder = sourceRunFolder;
            m_OutputRoot = outputRoot;
            m_RunId = runId;
            m_Width = width;
            m_Height = height;
            m_StartFrame = startFrame;
            m_FrameCount = frameCount;
            m_MaximumAge = maximumAge;
            m_PersistedFrameStride = Mathf.Max(1, persistedFrameStride);
            m_ExitEditorWhenComplete = exitEditorWhenComplete;
        }

        private IEnumerator Start()
        {
            bool success = false;
            try
            {
                Initialize();
                yield return Capture();
                success = true;
                Debug.Log($"Completed OSFR TAA disocclusion evaluation at {m_OutputRoot}.");
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
            if (string.IsNullOrEmpty(m_SourceRunFolder) || string.IsNullOrEmpty(m_OutputRoot)
                || m_Width <= 0 || m_Height <= 0 || m_StartFrame < 0 || m_FrameCount < 2
                || m_MaximumAge < 0)
            {
                throw new InvalidOperationException("The TAA disocclusion controller was not configured correctly.");
            }

            if (!File.Exists(SourcePath("reference", m_StartFrame)))
            {
                throw new DirectoryNotFoundException(
                    $"The depth-boundary source sequence is incomplete: {SourceSequenceFolder()}");
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
            GameObject[] roots = SceneManager.GetActiveScene().GetRootGameObjects();
            GameObject checkerRoot = roots.FirstOrDefault(root => root.name == V1ValidationNames.CheckerboardRootName);
            GameObject boundaryRoot = roots.FirstOrDefault(root => root.name == V1ValidationNames.DepthBoundaryRootName);
            if (m_CameraData == null || checkerRoot == null || boundaryRoot == null)
            {
                throw new InvalidOperationException("The V1 depth-boundary scene is incomplete.");
            }

            checkerRoot.SetActive(false);
            boundaryRoot.SetActive(true);
            m_Rail.enabled = false;
            m_Rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
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
            m_Feature.FeatureSettings.outputResolutionOverride = new Vector2Int(m_Width, m_Height);
            m_Pipeline.msaaSampleCount = 1;
            m_Camera.allowHDR = true;
            m_Camera.allowMSAA = false;
            m_Camera.allowDynamicResolution = false;
            m_CameraData.renderPostProcessing = true;
            m_CameraData.antialiasing = AntialiasingMode.TemporalAntiAliasing;
            m_CameraData.antialiasingQuality = AntialiasingQuality.High;
            m_CameraData.resetHistory = true;
            Time.captureFramerate = 60;
            QualitySettings.vSyncCount = 0;
            CreateTargets();
            Directory.CreateDirectory(m_OutputRoot);
        }

        private IEnumerator Capture()
        {
            m_Rail.ApplyFrame(m_StartFrame);
            for (int warmup = 0; warmup < TaaWarmupFrames; warmup++)
            {
                yield return null;
            }

            var rawAccumulator = new DisocclusionMetricMath.SequenceAccumulator(m_MaximumAge);
            var bilateralAccumulator = new DisocclusionMetricMath.SequenceAccumulator(m_MaximumAge);
            var taaAccumulator = new DisocclusionMetricMath.SequenceAccumulator(m_MaximumAge);
            var rows = new List<string>(m_FrameCount * (m_MaximumAge + 1) * 3);
            Color[] previousReference = null;
            Color[] previousDepth = null;
            int[] previousAges = null;
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
                    throw new InvalidOperationException("TAA capture did not advance Unity engine frames.");
                }
                else if (engineFrame != previousEngineFrame + 1)
                {
                    engineFrameDiscontinuities++;
                }

                previousEngineFrame = engineFrame;
                bool persist = localFrame == 0 || localFrame == m_FrameCount - 1
                    || localFrame % m_PersistedFrameStride == 0;
                string suffix = railFrame.ToString("000000", CultureInfo.InvariantCulture);
                Color[] taa = ReadCandidate(
                    persist ? Path.Combine(m_OutputRoot, $"color_taa_{suffix}.exr") : null);
                Color[] raw = LoadExr(SourcePath("color_raw", railFrame));
                Color[] bilateral = LoadExr(SourcePath("color_bilateral", railFrame));
                Color[] reference = LoadExr(SourcePath("reference", railFrame));
                Color[] depth = LoadExr(SourcePath("depth", railFrame));
                Color[] motion = LoadExr(SourcePath("motion", railFrame));

                if (previousReference != null)
                {
                    int[] ages = DisocclusionMetricMath.UpdateAges(
                        previousAges,
                        motion,
                        previousDepth,
                        depth,
                        m_Width,
                        m_Height,
                        m_MaximumAge,
                        RelativeDepthThreshold);
                    EvaluateAlgorithm("Raw", raw, reference, previousReference, motion, ages,
                        railFrame, engineFrame, rawAccumulator, rows);
                    EvaluateAlgorithm("Bilateral", bilateral, reference, previousReference, motion, ages,
                        railFrame, engineFrame, bilateralAccumulator, rows);
                    EvaluateAlgorithm("TAA", taa, reference, previousReference, motion, ages,
                        railFrame, engineFrame, taaAccumulator, rows);
                    if (persist)
                    {
                        WriteAgeMask(Path.Combine(m_OutputRoot, $"disocclusion_age_{suffix}.exr"), ages);
                    }

                    previousAges = ages;
                }

                previousReference = reference;
                previousDepth = depth;
            }

            WriteFrameMetrics(rows);
            WriteSummary(rawAccumulator, bilateralAccumulator, taaAccumulator);
            WriteManifest(firstEngineFrame, previousEngineFrame, engineFrameDiscontinuities);
        }

        private void EvaluateAlgorithm(
            string algorithm,
            Color[] candidate,
            Color[] reference,
            Color[] previousReference,
            Color[] motion,
            int[] ages,
            int railFrame,
            int engineFrame,
            DisocclusionMetricMath.SequenceAccumulator accumulator,
            ICollection<string> rows)
        {
            DisocclusionMetricMath.AgeBinResult[] bins = DisocclusionMetricMath.EvaluateFrame(
                candidate,
                reference,
                previousReference,
                motion,
                ages,
                m_Width,
                m_Height,
                m_MaximumAge,
                MinimumHistoryContrast);
            accumulator.Add(bins);
            foreach (DisocclusionMetricMath.AgeBinResult bin in bins)
            {
                rows.Add(MetricRow(algorithm, railFrame, engineFrame, bin));
            }
        }

        private void CreateTargets()
        {
            var descriptor = new RenderTextureDescriptor(
                m_Width, m_Height, RenderTextureFormat.ARGBFloat, 24)
            {
                msaaSamples = 1,
                sRGB = false,
                useMipMap = false,
                autoGenerateMips = false
            };
            m_Target = new RenderTexture(descriptor)
            {
                name = "OSFR TAA Disocclusion Target",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            m_Target.Create();
            m_Resolved = new RenderTexture(
                m_Width, m_Height, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear)
            {
                name = "OSFR TAA Disocclusion Resolved",
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp
            };
            m_Resolved.Create();
            m_Readback = new Texture2D(m_Width, m_Height, TextureFormat.RGBAFloat, false, true);
            m_Camera.targetTexture = m_Target;
            m_Camera.ResetAspect();
            m_Camera.ResetProjectionMatrix();
        }

        private Color[] ReadCandidate(string outputPath)
        {
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                Graphics.Blit(m_Target, m_Resolved);
                RenderTexture.active = m_Resolved;
                m_Readback.ReadPixels(new Rect(0, 0, m_Width, m_Height), 0, 0, false);
                m_Readback.Apply(false, false);
                Color[] pixels = m_Readback.GetPixels();
                if (!string.IsNullOrEmpty(outputPath))
                {
                    File.WriteAllBytes(outputPath, m_Readback.EncodeToEXR(
                        Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
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
                throw new FileNotFoundException("A source disocclusion frame is missing.", path);
            }

            var texture = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            try
            {
                if (!ImageConversion.LoadImage(texture, File.ReadAllBytes(path), false)
                    || texture.width != m_Width || texture.height != m_Height)
                {
                    throw new InvalidDataException($"Could not decode the expected {m_Width}x{m_Height} EXR: {path}");
                }

                return texture.GetPixels();
            }
            finally
            {
                Destroy(texture);
            }
        }

        private void WriteAgeMask(string path, int[] ages)
        {
            var pixels = new Color[ages.Length];
            for (int index = 0; index < ages.Length; index++)
            {
                int age = ages[index];
                pixels[index] = age < 0
                    ? Color.clear
                    : new Color(
                        1.0f - age / (float)(m_MaximumAge + 1),
                        age == 0 ? 1.0f : 0.0f,
                        age / (float)Mathf.Max(1, m_MaximumAge),
                        1.0f);
            }

            var texture = new Texture2D(m_Width, m_Height, TextureFormat.RGBAFloat, false, true);
            try
            {
                texture.SetPixels(pixels);
                texture.Apply(false, false);
                File.WriteAllBytes(path, texture.EncodeToEXR(
                    Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP));
            }
            finally
            {
                Destroy(texture);
            }
        }

        private void WriteFrameMetrics(IReadOnlyList<string> rows)
        {
            string csv = "algorithm,frame,engine_frame,age_frames,pixels,rmse,mae,history_eligible_pixels,"
                + "history_retention_coefficient,closer_to_history_fraction\n" + string.Concat(rows);
            File.WriteAllText(Path.Combine(m_OutputRoot, "frame_metrics.csv"), csv);
        }

        private static string MetricRow(
            string algorithm,
            int frame,
            int engineFrame,
            DisocclusionMetricMath.AgeBinResult result)
        {
            return string.Join(",", new[]
            {
                algorithm,
                frame.ToString(CultureInfo.InvariantCulture),
                engineFrame.ToString(CultureInfo.InvariantCulture),
                result.Age.ToString(CultureInfo.InvariantCulture),
                result.PixelCount.ToString(CultureInfo.InvariantCulture),
                Format(result.RootMeanSquareError),
                Format(result.MeanAbsoluteError),
                result.HistoryEligiblePixelCount.ToString(CultureInfo.InvariantCulture),
                Format(result.HistoryRetentionCoefficient),
                Format(result.CloserToHistoryFraction)
            }) + "\n";
        }

        private void WriteSummary(params DisocclusionMetricMath.SequenceAccumulator[] accumulators)
        {
            string[] algorithms = { "Raw", "Bilateral", "TAA" };
            string csv = "algorithm,age_frames,pixels,rmse,mae,history_eligible_pixels,"
                + "history_retention_coefficient,closer_to_history_fraction\n";
            for (int index = 0; index < accumulators.Length; index++)
            {
                csv += SummaryRow(algorithms[index], accumulators[index].BuildAllRecentSummary());
                foreach (DisocclusionMetricMath.AgeBinResult result in accumulators[index].BuildAgeSummary())
                {
                    csv += SummaryRow(algorithms[index], result);
                }
            }

            File.WriteAllText(Path.Combine(m_OutputRoot, "disocclusion_summary.csv"), csv);
        }

        private static string SummaryRow(string algorithm, DisocclusionMetricMath.AgeBinResult result)
        {
            return string.Join(",", new[]
            {
                algorithm,
                result.Age < 0 ? "all_recent" : result.Age.ToString(CultureInfo.InvariantCulture),
                result.PixelCount.ToString(CultureInfo.InvariantCulture),
                Format(result.RootMeanSquareError),
                Format(result.MeanAbsoluteError),
                result.HistoryEligiblePixelCount.ToString(CultureInfo.InvariantCulture),
                Format(result.HistoryRetentionCoefficient),
                Format(result.CloserToHistoryFraction)
            }) + "\n";
        }

        private void WriteManifest(int firstEngineFrame, int lastEngineFrame, int discontinuities)
        {
            var manifest = new RunManifest
            {
                schemaVersion = 1,
                runId = m_RunId,
                matrix = m_Width == 1280 ? "128_frame_720p_taa_disocclusion" : "24_frame_320x180_taa_disocclusion_smoke",
                scene = "V1_ScreenSpaceValidation",
                caseName = "DepthDiscontinuityPole",
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                output = new[] { m_Width, m_Height },
                startFrame = m_StartFrame,
                frameCount = m_FrameCount,
                maximumTrackedAge = m_MaximumAge,
                sourceRunId = Path.GetFileName(m_SourceRunFolder),
                firstEngineFrame = firstEngineFrame,
                lastEngineFrame = lastEngineFrame,
                engineFrameDiscontinuities = discontinuities,
                taaHistoryPolicy = "Reset once, warm up for eight real engine frames at the starting pose, then preserve native URP history and jitter.",
                disocclusionRule = $"Age zero when current linear eye depth is farther than warped previous depth by more than {Format(RelativeDepthThreshold)} relative to current depth; nearer mismatches are occlusions.",
                ghostingMetric = $"Full-reference luminance RMSE/MAE by frames since disocclusion. At age zero, least-squares error projection toward warped old reference is reported when luminance contrast >= {Format(MinimumHistoryContrast)}.",
                summary = "disocclusion_summary.csv",
                frameMetrics = "frame_metrics.csv"
            };
            WriteJson(Path.Combine(m_OutputRoot, "manifest.json"), manifest);
        }

        private string SourceSequenceFolder()
        {
            return Path.Combine(m_SourceRunFolder, "depth_boundary_lateral");
        }

        private string SourcePath(string kind, int frame)
        {
            return Path.Combine(SourceSequenceFolder(), $"{kind}_{frame:000000}.exr");
        }

        private void Cleanup()
        {
            if (m_CleanupComplete)
            {
                return;
            }

            m_CleanupComplete = true;
            if (m_Camera != null && m_Camera.targetTexture == m_Target)
            {
                m_Camera.targetTexture = m_PreviousTarget;
                m_Camera.allowMSAA = m_PreviousAllowMsaa;
                m_Camera.allowDynamicResolution = m_PreviousAllowDynamicResolution;
            }

            if (m_Readback != null) Destroy(m_Readback);
            if (m_Resolved != null) { m_Resolved.Release(); Destroy(m_Resolved); }
            if (m_Target != null) { m_Target.Release(); Destroy(m_Target); }
            if (m_Feature != null)
            {
                m_Feature.FeatureSettings.debugView = m_PreviousView;
                m_Feature.FeatureSettings.outputResolutionOverride = m_PreviousOutputOverride;
            }

            if (m_Pipeline != null) m_Pipeline.msaaSampleCount = m_PreviousMsaaSamples;
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

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
            if (pipeline == null) return null;
            foreach (ScriptableRendererData rendererData in pipeline.rendererDataList)
            {
                if (rendererData != null && rendererData.TryGetRendererFeature(out MeasurementRendererFeature feature))
                {
                    return feature;
                }
            }

            return null;
        }

        private static string Format(float value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, JsonUtility.ToJson(value, true) + Environment.NewLine);
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
            public int maximumTrackedAge;
            public string sourceRunId;
            public int firstEngineFrame;
            public int lastEngineFrame;
            public int engineFrameDiscontinuities;
            public string taaHistoryPolicy;
            public string disocclusionRule;
            public string ghostingMetric;
            public string summary;
            public string frameMetrics;
        }
    }
}
