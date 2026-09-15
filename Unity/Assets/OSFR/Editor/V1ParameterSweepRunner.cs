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
    /// Searches a bounded V1 parameter grid against a 16x candidate reference.
    /// Ranking balances reference error with fine-band energy retention and keeps
    /// depth-boundary regression as an explicit constraint.
    /// </summary>
    public static class V1ParameterSweepRunner
    {
        private const int Width = 1280;
        private const int Height = 720;
        private const int Frame = 300;
        private const int ReferenceFactor = 16;
        private const int TargetTileEdge = 128;
        private const int Seed = 12345;
        private const float MaximumPoleMseRatio = 1.02f;
        private const float ExistingStrength = 1.0f;
        private const float ExistingSpatialSigma = 1.25f;
        private const float ExistingRiskStart = 0.002f;
        private const float ExistingRiskEnd = 0.02f;

        [MenuItem("OSFR/Capture/Run V1 720p Parameter Sweep")]
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
                Debug.LogError("OSFR parameter sweep requires a real graphics device.");
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
                Debug.LogError("The V1 sweep scene is missing its camera, rail, feature, or case roots.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"parameter_sweep_720p_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            Directory.CreateDirectory(outputRoot);

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
                UnityEngine.Random.InitState(Seed);
                rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                rail.ApplyFrame(Frame);
                settings.displayedPyramidLevel = 0;
                settings.outputResolutionOverride = new Vector2Int(Width, Height);

                CaseData checker = CaptureBaseline(
                    camera,
                    feature,
                    outputRoot,
                    "CheckerboardWall",
                    checkerRoot,
                    boundaryRoot);
                CaseData pole = CaptureBaseline(
                    camera,
                    feature,
                    outputRoot,
                    "DepthDiscontinuityPole",
                    checkerRoot,
                    boundaryRoot);

                List<SweepPreset> presets = BuildPresets();
                var evaluations = new List<PresetEvaluation>(presets.Count);
                foreach (SweepPreset preset in presets)
                {
                    settings.bilateralStrength = preset.strength;
                    settings.bilateralSpatialSigmaPixels = preset.spatialSigma;
                    settings.riskFootprintStartWorld = preset.riskStart;
                    settings.riskFootprintEndWorld = preset.riskEnd;

                    string presetFolder = Path.Combine(outputRoot, "presets", preset.id);
                    Directory.CreateDirectory(presetFolder);
                    WriteJson(Path.Combine(presetFolder, "preset.json"), new PresetMetadata
                    {
                        id = preset.id,
                        bilateralStrength = preset.strength,
                        bilateralSpatialSigmaPixels = preset.spatialSigma,
                        riskFootprintStartWorld = preset.riskStart,
                        riskFootprintEndWorld = preset.riskEnd
                    });

                    CaseEvaluation checkerEvaluation = EvaluatePreset(
                        camera,
                        feature,
                        checker,
                        checkerRoot,
                        boundaryRoot,
                        Path.Combine(presetFolder, "checkerboardwall_bilateral.exr"));
                    CaseEvaluation poleEvaluation = EvaluatePreset(
                        camera,
                        feature,
                        pole,
                        checkerRoot,
                        boundaryRoot,
                        Path.Combine(presetFolder, "depthdiscontinuitypole_bilateral.exr"));
                    evaluations.Add(new PresetEvaluation(preset, checkerEvaluation, poleEvaluation));
                }

                Rank(evaluations);
                PresetEvaluation recommended = evaluations.First(result => result.recommended);
                WriteBaselines(outputRoot, checker, pole);
                WriteConditionMetrics(outputRoot, evaluations);
                WriteRanking(outputRoot, evaluations);
                WriteJson(Path.Combine(outputRoot, "manifest.json"), new RunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = "v1_720p_parameter_sweep",
                    scene = "V1_ScreenSpaceValidation",
                    createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    output = new[] { Width, Height },
                    frame = Frame,
                    referenceFactor = ReferenceFactor,
                    candidateCount = evaluations.Count,
                    paretoCount = evaluations.Count(result => result.pareto),
                    poleMseGuard = MaximumPoleMseRatio,
                    recommendedPresetId = recommended.preset.id,
                    rankingPolicy = "Pareto over mean normalized RGB MSE and checker fine-energy log2 error; balanced normalized distance; pole MSE <= 1.02x raw; exact ties prefer minimum change from the existing preset."
                });

                if (logSuccess)
                {
                    Debug.Log(
                        $"Completed OSFR V1 parameter sweep at {outputRoot}. "
                        + $"Recommended preset: {recommended.preset.id}.");
                }
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                return null;
            }
            finally
            {
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

            return outputRoot;
        }

        private static CaseData CaptureBaseline(
            Camera camera,
            MeasurementRendererFeature feature,
            string outputRoot,
            string caseName,
            GameObject checkerRoot,
            GameObject boundaryRoot)
        {
            SetCase(caseName, checkerRoot, boundaryRoot);
            string folder = Path.Combine(outputRoot, "baselines", caseName.ToLowerInvariant());
            Directory.CreateDirectory(folder);
            Color[] raw = CaptureBuffer(
                camera,
                feature,
                MeasurementDebugView.PyramidMip,
                Path.Combine(folder, "color_raw.exr"));
            feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
            TiledLinearHdrReferenceRenderer.Result reference = TiledLinearHdrReferenceRenderer.Capture(
                camera,
                Width,
                Height,
                ReferenceFactor,
                TargetTileEdge,
                Path.Combine(folder, $"reference_{ReferenceFactor}x.exr"));
            FullReferenceMetricMath.Result rawMetrics = FullReferenceMetricMath.Evaluate(
                raw,
                reference.Pixels,
                Width,
                Height,
                pyramidLevels: 2);
            return new CaseData(caseName, reference.Pixels, rawMetrics);
        }

        private static CaseEvaluation EvaluatePreset(
            Camera camera,
            MeasurementRendererFeature feature,
            CaseData caseData,
            GameObject checkerRoot,
            GameObject boundaryRoot,
            string outputPath)
        {
            SetCase(caseData.name, checkerRoot, boundaryRoot);
            Color[] filtered = CaptureBuffer(
                camera,
                feature,
                MeasurementDebugView.BilateralFilteredColor,
                outputPath);
            FullReferenceMetricMath.Result metrics = FullReferenceMetricMath.Evaluate(
                filtered,
                caseData.reference,
                Width,
                Height,
                pyramidLevels: 2);
            return new CaseEvaluation(caseData, metrics);
        }

        private static void Rank(List<PresetEvaluation> evaluations)
        {
            foreach (PresetEvaluation candidate in evaluations)
            {
                candidate.pareto = !evaluations.Any(other =>
                    !ReferenceEquals(candidate, other)
                    && other.meanNormalizedMse <= candidate.meanNormalizedMse
                    && other.checkerFineEnergyLogError <= candidate.checkerFineEnergyLogError
                    && (other.meanNormalizedMse < candidate.meanNormalizedMse
                        || other.checkerFineEnergyLogError < candidate.checkerFineEnergyLogError));
            }

            float minimumMse = evaluations.Min(result => result.meanNormalizedMse);
            float maximumMse = evaluations.Max(result => result.meanNormalizedMse);
            float minimumDetail = evaluations.Min(result => result.checkerFineEnergyLogError);
            float maximumDetail = evaluations.Max(result => result.checkerFineEnergyLogError);
            foreach (PresetEvaluation result in evaluations)
            {
                float normalizedMse = Mathf.InverseLerp(minimumMse, maximumMse, result.meanNormalizedMse);
                float normalizedDetail = Mathf.InverseLerp(
                    minimumDetail,
                    maximumDetail,
                    result.checkerFineEnergyLogError);
                result.balancedScore = Mathf.Sqrt(
                    normalizedMse * normalizedMse + normalizedDetail * normalizedDetail);
            }

            PresetEvaluation recommended = evaluations
                .Where(result => result.pareto && result.pole.mseVsRawRatio <= MaximumPoleMseRatio)
                .OrderBy(result => result.balancedScore)
                .ThenBy(result => result.parameterChangeDistance)
                .ThenBy(result => result.meanNormalizedMse)
                .FirstOrDefault();
            if (recommended == null)
            {
                recommended = evaluations
                    .Where(result => result.pareto)
                    .OrderBy(result => result.balancedScore)
                    .ThenBy(result => result.parameterChangeDistance)
                    .First();
            }

            recommended.recommended = true;
        }

        private static List<SweepPreset> BuildPresets()
        {
            float[] strengths = { 0.5f, 0.75f, 1.0f };
            float[] spatialSigmas = { 0.75f, 1.25f, 1.75f };
            var riskRanges = new[]
            {
                new Vector2(0.001f, 0.01f),
                new Vector2(0.002f, 0.02f),
                new Vector2(0.004f, 0.04f)
            };
            var presets = new List<SweepPreset>(27);
            int index = 0;
            foreach (float strength in strengths)
            {
                foreach (float sigma in spatialSigmas)
                {
                    foreach (Vector2 riskRange in riskRanges)
                    {
                        presets.Add(new SweepPreset(
                            $"p{index:000}",
                            strength,
                            sigma,
                            riskRange.x,
                            riskRange.y));
                        index++;
                    }
                }
            }

            return presets;
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
                // Tiled reference capture uses explicit sub-frustum matrices. Reset
                // aspect and projection before every ordinary full-frame capture so
                // the target owns both and candidates remain pixel-aligned.
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

        private static void SetCase(
            string caseName,
            GameObject checkerRoot,
            GameObject boundaryRoot)
        {
            checkerRoot.SetActive(caseName == "CheckerboardWall");
            boundaryRoot.SetActive(caseName == "DepthDiscontinuityPole");
        }

        private static void WriteBaselines(string outputRoot, CaseData checker, CaseData pole)
        {
            string csv = "case,algorithm,rgb_mse,psnr_db,luminance_mae,luminance_rmse,"
                + "fine_band_error,fine_band_energy,reference_fine_band_energy,fine_energy_ratio\n"
                + BaselineRow(checker)
                + BaselineRow(pole);
            File.WriteAllText(Path.Combine(outputRoot, "baselines.csv"), csv);
        }

        private static string BaselineRow(CaseData data)
        {
            FullReferenceMetricMath.Result metrics = data.rawMetrics;
            FullReferenceMetricMath.SpatialBand fine = metrics.Bands[0];
            return string.Join(",", new[]
            {
                data.name,
                "Raw",
                Format(metrics.RgbMeanSquareError),
                Format(metrics.PsnrDecibels),
                Format(metrics.LuminanceMeanAbsoluteError),
                Format(metrics.LuminanceRootMeanSquareError),
                Format(fine.ErrorMeanSquare),
                Format(fine.CandidateEnergy),
                Format(fine.ReferenceEnergy),
                Format(fine.CandidateEnergy / Mathf.Max(fine.ReferenceEnergy, 0.000000000001f))
            }) + "\n";
        }

        private static void WriteConditionMetrics(
            string outputRoot,
            IReadOnlyList<PresetEvaluation> evaluations)
        {
            string csv = "preset_id,strength,spatial_sigma,risk_start,risk_end,case,rgb_mse,"
                + "psnr_db,luminance_mae,luminance_rmse,fine_band_error,fine_band_energy,"
                + "reference_fine_band_energy,fine_energy_ratio,mse_vs_raw_ratio\n";
            foreach (PresetEvaluation evaluation in evaluations)
            {
                csv += ConditionRow(evaluation.preset, evaluation.checker);
                csv += ConditionRow(evaluation.preset, evaluation.pole);
            }

            File.WriteAllText(Path.Combine(outputRoot, "condition_metrics.csv"), csv);
        }

        private static string ConditionRow(SweepPreset preset, CaseEvaluation evaluation)
        {
            FullReferenceMetricMath.Result metrics = evaluation.metrics;
            FullReferenceMetricMath.SpatialBand fine = metrics.Bands[0];
            return string.Join(",", new[]
            {
                preset.id,
                Format(preset.strength),
                Format(preset.spatialSigma),
                Format(preset.riskStart),
                Format(preset.riskEnd),
                evaluation.caseData.name,
                Format(metrics.RgbMeanSquareError),
                Format(metrics.PsnrDecibels),
                Format(metrics.LuminanceMeanAbsoluteError),
                Format(metrics.LuminanceRootMeanSquareError),
                Format(fine.ErrorMeanSquare),
                Format(fine.CandidateEnergy),
                Format(fine.ReferenceEnergy),
                Format(evaluation.fineEnergyRatio),
                Format(evaluation.mseVsRawRatio)
            }) + "\n";
        }

        private static void WriteRanking(
            string outputRoot,
            IEnumerable<PresetEvaluation> evaluations)
        {
            string csv = "rank,preset_id,strength,spatial_sigma,risk_start,risk_end,pareto,recommended,"
                + "balanced_score,parameter_change_distance,mean_normalized_mse,checker_mse_ratio,pole_mse_ratio,"
                + "checker_fine_band_error_ratio,checker_fine_energy_ratio,"
                + "checker_fine_energy_log2_error\n";
            int rank = 1;
            foreach (PresetEvaluation result in evaluations
                .OrderBy(item => item.balancedScore)
                .ThenBy(item => item.parameterChangeDistance)
                .ThenBy(item => item.meanNormalizedMse))
            {
                csv += string.Join(",", new[]
                {
                    rank.ToString(CultureInfo.InvariantCulture),
                    result.preset.id,
                    Format(result.preset.strength),
                    Format(result.preset.spatialSigma),
                    Format(result.preset.riskStart),
                    Format(result.preset.riskEnd),
                    result.pareto ? "true" : "false",
                    result.recommended ? "true" : "false",
                    Format(result.balancedScore),
                    Format(result.parameterChangeDistance),
                    Format(result.meanNormalizedMse),
                    Format(result.checker.mseVsRawRatio),
                    Format(result.pole.mseVsRawRatio),
                    Format(result.checkerFineBandErrorRatio),
                    Format(result.checker.fineEnergyRatio),
                    Format(result.checkerFineEnergyLogError)
                }) + "\n";
                rank++;
            }

            File.WriteAllText(Path.Combine(outputRoot, "ranking.csv"), csv);
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

        private readonly struct SweepPreset
        {
            public SweepPreset(string id, float strength, float spatialSigma, float riskStart, float riskEnd)
            {
                this.id = id;
                this.strength = strength;
                this.spatialSigma = spatialSigma;
                this.riskStart = riskStart;
                this.riskEnd = riskEnd;
            }

            public readonly string id;
            public readonly float strength;
            public readonly float spatialSigma;
            public readonly float riskStart;
            public readonly float riskEnd;
        }

        private sealed class CaseData
        {
            public CaseData(
                string name,
                Color[] reference,
                FullReferenceMetricMath.Result rawMetrics)
            {
                this.name = name;
                this.reference = reference;
                this.rawMetrics = rawMetrics;
            }

            public readonly string name;
            public readonly Color[] reference;
            public readonly FullReferenceMetricMath.Result rawMetrics;
        }

        private sealed class CaseEvaluation
        {
            public CaseEvaluation(CaseData caseData, FullReferenceMetricMath.Result metrics)
            {
                this.caseData = caseData;
                this.metrics = metrics;
                mseVsRawRatio = metrics.RgbMeanSquareError
                    / Mathf.Max(caseData.rawMetrics.RgbMeanSquareError, 0.000000000001f);
                FullReferenceMetricMath.SpatialBand fine = metrics.Bands[0];
                fineEnergyRatio = fine.CandidateEnergy
                    / Mathf.Max(fine.ReferenceEnergy, 0.000000000001f);
            }

            public readonly CaseData caseData;
            public readonly FullReferenceMetricMath.Result metrics;
            public readonly float mseVsRawRatio;
            public readonly float fineEnergyRatio;
        }

        private sealed class PresetEvaluation
        {
            public PresetEvaluation(
                SweepPreset preset,
                CaseEvaluation checker,
                CaseEvaluation pole)
            {
                this.preset = preset;
                this.checker = checker;
                this.pole = pole;
                meanNormalizedMse = 0.5f * (checker.mseVsRawRatio + pole.mseVsRawRatio);
                checkerFineEnergyLogError = Mathf.Abs(
                    Mathf.Log(Mathf.Max(checker.fineEnergyRatio, 0.000000001f), 2.0f));
                checkerFineBandErrorRatio = checker.metrics.Bands[0].ErrorMeanSquare
                    / Mathf.Max(checker.caseData.rawMetrics.Bands[0].ErrorMeanSquare, 0.000000000001f);
                parameterChangeDistance = Mathf.Abs(preset.strength - ExistingStrength) / 0.5f
                    + Mathf.Abs(preset.spatialSigma - ExistingSpatialSigma)
                    + Mathf.Abs(Mathf.Log(preset.riskStart / ExistingRiskStart, 2.0f))
                    + Mathf.Abs(Mathf.Log(preset.riskEnd / ExistingRiskEnd, 2.0f));
            }

            public readonly SweepPreset preset;
            public readonly CaseEvaluation checker;
            public readonly CaseEvaluation pole;
            public readonly float meanNormalizedMse;
            public readonly float checkerFineEnergyLogError;
            public readonly float checkerFineBandErrorRatio;
            public readonly float parameterChangeDistance;
            public bool pareto;
            public bool recommended;
            public float balancedScore;
        }

        [Serializable]
        private sealed class PresetMetadata
        {
            public string id;
            public float bilateralStrength;
            public float bilateralSpatialSigmaPixels;
            public float riskFootprintStartWorld;
            public float riskFootprintEndWorld;
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
            public int[] output;
            public int frame;
            public int referenceFactor;
            public int candidateCount;
            public int paretoCount;
            public float poleMseGuard;
            public string recommendedPresetId;
            public string rankingPolicy;
        }
    }
}
