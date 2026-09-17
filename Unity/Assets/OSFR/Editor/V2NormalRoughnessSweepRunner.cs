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
    /// Calibrates the V2 normal-variance gain against a 16x spatial reference.
    /// Selection is constrained by per-panel regression, reports the RMSE/MAE
    /// Pareto frontier, and chooses the smallest gain within 5% of the best
    /// eligible value on both objectives.
    /// </summary>
    public static class V2NormalRoughnessSweepRunner
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int Frame = 300;
        private const int ReferenceFactor = 16;
        private const int LowerReferenceFactor = 8;
        private const int TargetTileEdge = 128;
        private const int PyramidLevels = 5;
        private const int Seed = 12345;
        private const float MaximumPanelRegression = 0.02f;
        private const float NearIdealTolerance = 0.05f;
        private static readonly float[] Gains =
            { 0.025f, 0.05f, 0.1f, 0.2f, 0.35f, 0.5f, 0.75f, 1.0f, 1.25f, 1.5f, 2.0f,
                3.0f, 4.0f, 6.0f, 8.0f, 12.0f, 16.0f, 24.0f, 32.0f, 48.0f, 64.0f,
                96.0f, 128.0f };

        [MenuItem("OSFR/Capture/Run V2 720p Normal Roughness Gain Sweep")]
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

        private static string Run(bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR V2 gain calibration requires a real graphics device.");
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
            Renderer[] renderers = root == null ? Array.Empty<Renderer>() : root.GetComponentsInChildren<Renderer>();
            Material[] materials = renderers
                .Select(renderer => renderer.sharedMaterial)
                .Where(material => material != null)
                .Distinct()
                .ToArray();
            if (camera == null || rail == null || feature == null || renderers.Length != 9 || materials.Length != 9)
            {
                Debug.LogError("The V2 normal-frequency scene is incomplete.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"normal_roughness_gain_sweep_720p_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
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
            float[] previousGain = materials.Select(material => material.GetFloat("_VarianceGain")).ToArray();

            try
            {
                rail.enabled = false;
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                rail.ApplyFrame(Frame);
                UnityEngine.Random.InitState(Seed);
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                feature.FeatureSettings.displayedPyramidLevel = 0;
                feature.FeatureSettings.outputResolutionOverride = new Vector2Int(Width, Height);

                SetMaterialMode(materials, filterEnabled: false, gain: 0.0f, debugMode: 0);
                Color[] raw = CaptureBuffer(camera, feature, Path.Combine(outputRoot, "color_raw.exr"));
                TiledLinearHdrReferenceRenderer.Result lowerReference = TiledLinearHdrReferenceRenderer.Capture(
                    camera, Width, Height, LowerReferenceFactor, TargetTileEdge,
                    Path.Combine(outputRoot, $"reference_{LowerReferenceFactor}x.exr"));
                TiledLinearHdrReferenceRenderer.Result reference = TiledLinearHdrReferenceRenderer.Capture(
                    camera, Width, Height, ReferenceFactor, TargetTileEdge,
                    Path.Combine(outputRoot, $"reference_{ReferenceFactor}x.exr"));

                FullReferenceMetricMath.Result rawMetrics = FullReferenceMetricMath.Evaluate(
                    raw, reference.Pixels, Width, Height, PyramidLevels);
                PanelMetric[] rawPanels = EvaluatePanels(camera, renderers, raw, reference.Pixels);
                var candidates = new List<CandidateResult>(Gains.Length + 1)
                {
                    new CandidateResult("Raw", 0.0f, rawMetrics, rawPanels, 0.0f)
                };

                foreach (float gain in Gains)
                {
                    SetMaterialMode(materials, filterEnabled: true, gain: gain, debugMode: 0);
                    string gainName = GainName(gain);
                    Color[] pixels = CaptureBuffer(
                        camera,
                        feature,
                        Path.Combine(outputRoot, $"color_gain_{gainName}.exr"));
                    FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                        pixels, reference.Pixels, Width, Height, PyramidLevels);
                    PanelMetric[] panels = EvaluatePanels(camera, renderers, pixels, reference.Pixels);
                    float worstRegression = panels
                        .Select((panel, index) => panel.LuminanceRmse
                            / Mathf.Max(rawPanels[index].LuminanceRmse, 0.000000001f) - 1.0f)
                        .Max();
                    candidates.Add(new CandidateResult(
                        $"Gain_{gainName}", gain, metrics, panels, worstRegression));
                }

                CalibrationSweepMath.Candidate[] filteredCandidates = candidates.Skip(1)
                    .Select(candidate => new CalibrationSweepMath.Candidate(
                        candidate.Gain,
                        candidate.Metrics.LuminanceRootMeanSquareError,
                        candidate.Metrics.LuminanceMeanAbsoluteError,
                        candidate.WorstPanelRegression))
                    .ToArray();
                CalibrationSweepMath.Ranking[] paretoRankings = CalibrationSweepMath.Rank(
                    filteredCandidates, MaximumPanelRegression);
                int selectedFilteredIndex = CalibrationSweepMath.SelectSmallestNearIdeal(
                    filteredCandidates, MaximumPanelRegression, NearIdealTolerance);
                CalibrationSweepMath.Ranking[] filteredRankings = paretoRankings
                    .Select((ranking, index) => new CalibrationSweepMath.Ranking(
                        ranking.Candidate,
                        ranking.Eligible,
                        ranking.Pareto,
                        ranking.IdealDistance,
                        index == selectedFilteredIndex))
                    .ToArray();
                CalibrationSweepMath.Ranking[] rankings = new[]
                    {
                        new CalibrationSweepMath.Ranking(
                            new CalibrationSweepMath.Candidate(
                                0.0f,
                                rawMetrics.LuminanceRootMeanSquareError,
                                rawMetrics.LuminanceMeanAbsoluteError,
                                0.0f),
                            eligible: true,
                            pareto: false,
                            idealDistance: float.PositiveInfinity,
                            recommended: false)
                    }
                    .Concat(filteredRankings)
                    .ToArray();
                int recommendedIndex = Array.FindIndex(rankings, ranking => ranking.Recommended);
                CandidateResult recommended = candidates[recommendedIndex];

                SetMaterialMode(materials, filterEnabled: recommended.Gain > 0.0f,
                    gain: recommended.Gain, debugMode: 1);
                CaptureBuffer(camera, feature, Path.Combine(outputRoot, "recommended_resultant_length.exr"));
                SetMaterialMode(materials, filterEnabled: recommended.Gain > 0.0f,
                    gain: recommended.Gain, debugMode: 2);
                CaptureBuffer(camera, feature, Path.Combine(outputRoot, "recommended_effective_alpha.exr"));

                WriteSweepSummary(outputRoot, candidates, rankings, rawMetrics);
                WritePanelMetrics(outputRoot, candidates, rawPanels);
                WriteReferenceConvergence(outputRoot, lowerReference, reference);
                WriteMetadata(outputRoot, runId, recommended, lowerReference, reference);
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
                    materials[index].SetFloat("_VarianceGain", previousGain[index]);
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
                Debug.Log($"Completed OSFR V2 normal-roughness gain sweep at {outputRoot}.");
            }

            return outputRoot;
        }

        private static PanelMetric[] EvaluatePanels(
            Camera camera,
            IEnumerable<Renderer> renderers,
            Color[] candidate,
            Color[] reference)
        {
            return renderers
                .OrderBy(renderer => renderer.name)
                .Select(renderer =>
                {
                    RectInt roi = ProjectRenderer(renderer, camera);
                    Color[] candidateRoi = Crop(candidate, roi);
                    Color[] referenceRoi = Crop(reference, roi);
                    FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                        candidateRoi, referenceRoi, roi.width, roi.height, PyramidLevels);
                    return new PanelMetric(
                        renderer.name,
                        renderer.sharedMaterial.GetFloat("_NormalFrequency"),
                        renderer.sharedMaterial.GetFloat("_BaseAlpha"),
                        metrics.LuminanceRootMeanSquareError,
                        metrics.LuminanceMeanAbsoluteError);
                })
                .ToArray();
        }

        private static Color[] CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            string outputPath)
        {
            var target = new RenderTexture(
                Width, Height, 24, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var readback = new Texture2D(Width, Height, TextureFormat.RGBAFloat, false, true);
            RenderTexture previousActive = RenderTexture.active;
            try
            {
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                camera.targetTexture = target;
                camera.ResetAspect();
                camera.ResetProjectionMatrix();
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, Width, Height), 0, 0, false);
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

        private static RectInt ProjectRenderer(Renderer renderer, Camera camera)
        {
            Vector3[] corners =
            {
                new Vector3(-0.5f, -0.5f, 0.0f), new Vector3(0.5f, -0.5f, 0.0f),
                new Vector3(-0.5f, 0.5f, 0.0f), new Vector3(0.5f, 0.5f, 0.0f)
            };
            float minX = 1.0f;
            float minY = 1.0f;
            float maxX = 0.0f;
            float maxY = 0.0f;
            foreach (Vector3 corner in corners)
            {
                Vector3 viewport = camera.WorldToViewportPoint(renderer.transform.TransformPoint(corner));
                minX = Mathf.Min(minX, viewport.x);
                minY = Mathf.Min(minY, viewport.y);
                maxX = Mathf.Max(maxX, viewport.x);
                maxY = Mathf.Max(maxY, viewport.y);
            }

            int xMin = Mathf.Clamp(Mathf.CeilToInt(minX * Width) + 1, 0, Width - 1);
            int yMin = Mathf.Clamp(Mathf.CeilToInt(minY * Height) + 1, 0, Height - 1);
            int xMax = Mathf.Clamp(Mathf.FloorToInt(maxX * Width) - 1, xMin + 1, Width);
            int yMax = Mathf.Clamp(Mathf.FloorToInt(maxY * Height) - 1, yMin + 1, Height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static Color[] Crop(Color[] source, RectInt roi)
        {
            var result = new Color[roi.width * roi.height];
            for (int y = 0; y < roi.height; y++)
            {
                Array.Copy(source, (roi.y + y) * Width + roi.x, result, y * roi.width, roi.width);
            }

            return result;
        }

        private static void WriteSweepSummary(
            string folder,
            IReadOnlyList<CandidateResult> candidates,
            IReadOnlyList<CalibrationSweepMath.Ranking> rankings,
            FullReferenceMetricMath.Result raw)
        {
            string csv = "algorithm,variance_gain,luminance_rmse,luminance_mae,rgb_mse,psnr_db,"
                + "rmse_vs_raw,mae_vs_raw,worst_panel_rmse_regression,eligible,pareto,ideal_distance,recommended\n";
            for (int index = 0; index < candidates.Count; index++)
            {
                CandidateResult candidate = candidates[index];
                CalibrationSweepMath.Ranking ranking = rankings[index];
                csv += string.Join(",", new[]
                {
                    candidate.Name,
                    Format(candidate.Gain),
                    Format(candidate.Metrics.LuminanceRootMeanSquareError),
                    Format(candidate.Metrics.LuminanceMeanAbsoluteError),
                    Format(candidate.Metrics.RgbMeanSquareError),
                    Format(candidate.Metrics.PsnrDecibels),
                    Format(candidate.Metrics.LuminanceRootMeanSquareError
                        / Mathf.Max(raw.LuminanceRootMeanSquareError, 0.000000001f)),
                    Format(candidate.Metrics.LuminanceMeanAbsoluteError
                        / Mathf.Max(raw.LuminanceMeanAbsoluteError, 0.000000001f)),
                    Format(candidate.WorstPanelRegression),
                    ranking.Eligible ? "true" : "false",
                    ranking.Pareto ? "true" : "false",
                    float.IsPositiveInfinity(ranking.IdealDistance) ? string.Empty : Format(ranking.IdealDistance),
                    ranking.Recommended ? "true" : "false"
                }) + "\n";
            }

            File.WriteAllText(Path.Combine(folder, "sweep_summary.csv"), csv);
        }

        private static void WritePanelMetrics(
            string folder,
            IEnumerable<CandidateResult> candidates,
            IReadOnlyList<PanelMetric> rawPanels)
        {
            string csv = "algorithm,variance_gain,panel,frequency_cycles_per_m,base_alpha,"
                + "luminance_rmse,luminance_mae,rmse_vs_raw\n";
            foreach (CandidateResult candidate in candidates)
            {
                for (int index = 0; index < candidate.Panels.Length; index++)
                {
                    PanelMetric panel = candidate.Panels[index];
                    csv += string.Join(",", new[]
                    {
                        candidate.Name,
                        Format(candidate.Gain),
                        panel.Name.Replace(',', '_'),
                        Format(panel.Frequency),
                        Format(panel.BaseAlpha),
                        Format(panel.LuminanceRmse),
                        Format(panel.LuminanceMae),
                        Format(panel.LuminanceRmse / Mathf.Max(rawPanels[index].LuminanceRmse, 0.000000001f))
                    }) + "\n";
                }
            }

            File.WriteAllText(Path.Combine(folder, "panel_metrics.csv"), csv);
        }

        private static void WriteReferenceConvergence(
            string folder,
            TiledLinearHdrReferenceRenderer.Result lower,
            TiledLinearHdrReferenceRenderer.Result upper)
        {
            SupersampleReferenceMath.LuminanceComparison comparison =
                SupersampleReferenceMath.CompareLuminance(lower.Pixels, upper.Pixels);
            string csv = "lower_factor,upper_factor,luminance_rmse,luminance_mae,lower_render_ms,upper_render_ms\n"
                + string.Join(",", new[]
                {
                    lower.Factor.ToString(CultureInfo.InvariantCulture),
                    upper.Factor.ToString(CultureInfo.InvariantCulture),
                    Format(comparison.RootMeanSquareError),
                    Format(comparison.MeanAbsoluteError),
                    Format(lower.ElapsedMilliseconds),
                    Format(upper.ElapsedMilliseconds)
                }) + "\n";
            File.WriteAllText(Path.Combine(folder, "reference_convergence.csv"), csv);
        }

        private static void WriteMetadata(
            string folder,
            string runId,
            CandidateResult recommended,
            TiledLinearHdrReferenceRenderer.Result lower,
            TiledLinearHdrReferenceRenderer.Result upper)
        {
            var metadata = new Metadata
            {
                schemaVersion = 1,
                runId = runId,
                matrix = "720p_normal_roughness_variance_gain_sweep",
                scene = "V2_UpstreamFiltering",
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                output = new[] { Width, Height },
                frame = Frame,
                candidateGains = Gains,
                maximumPanelRegression = MaximumPanelRegression,
                nearIdealTolerance = NearIdealTolerance,
                rankingRule = "Keep Raw as a non-selectable anchor. Exclude filtered candidates with >2% RMSE regression on any panel; report their full-frame luminance RMSE/MAE Pareto frontier; select the smallest gain within 5% of the best eligible RMSE and MAE.",
                recommendedGain = recommended.Gain,
                recommendedAlgorithm = recommended.Name,
                lowerReferenceFactor = lower.Factor,
                referenceFactor = upper.Factor,
                limitation = "Scalar isotropic roughness transfer only; reference convergence and temporal behavior remain separate qualifications."
            };
            File.WriteAllText(
                Path.Combine(folder, "metadata.json"),
                JsonUtility.ToJson(metadata, true) + Environment.NewLine);
        }

        private static void SetMaterialMode(
            IEnumerable<Material> materials,
            bool filterEnabled,
            float gain,
            int debugMode)
        {
            foreach (Material material in materials)
            {
                material.SetFloat("_FilterEnabled", filterEnabled ? 1.0f : 0.0f);
                material.SetFloat("_VarianceGain", gain);
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

        private static string GainName(float gain)
        {
            return gain.ToString("0.###", CultureInfo.InvariantCulture).Replace('.', 'p');
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

        private sealed class CandidateResult
        {
            public CandidateResult(
                string name,
                float gain,
                FullReferenceMetricMath.Result metrics,
                PanelMetric[] panels,
                float worstPanelRegression)
            {
                Name = name;
                Gain = gain;
                Metrics = metrics;
                Panels = panels;
                WorstPanelRegression = worstPanelRegression;
            }

            public string Name { get; }
            public float Gain { get; }
            public FullReferenceMetricMath.Result Metrics { get; }
            public PanelMetric[] Panels { get; }
            public float WorstPanelRegression { get; }
        }

        private readonly struct PanelMetric
        {
            public PanelMetric(string name, float frequency, float baseAlpha, float rmse, float mae)
            {
                Name = name;
                Frequency = frequency;
                BaseAlpha = baseAlpha;
                LuminanceRmse = rmse;
                LuminanceMae = mae;
            }

            public string Name { get; }
            public float Frequency { get; }
            public float BaseAlpha { get; }
            public float LuminanceRmse { get; }
            public float LuminanceMae { get; }
        }

        [Serializable]
        private sealed class Metadata
        {
            public int schemaVersion;
            public string runId;
            public string matrix;
            public string scene;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int[] output;
            public int frame;
            public float[] candidateGains;
            public float maximumPanelRegression;
            public float nearIdealTolerance;
            public string rankingRule;
            public float recommendedGain;
            public string recommendedAlgorithm;
            public int lowerReferenceFactor;
            public int referenceFactor;
            public string limitation;
        }
    }
}
