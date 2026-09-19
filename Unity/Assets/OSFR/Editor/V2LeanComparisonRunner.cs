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
    /// Compares raw directional normal shading, the promoted scalar roughness
    /// transfer, and the first LEAN-like anisotropic slope-moment prototype.
    /// </summary>
    public static class V2LeanComparisonRunner
    {
        private const int Seed = 12345;
        private const int Frame = 300;
        private const int TargetTileEdge = 128;
        private const int PyramidLevels = 5;

        [MenuItem("OSFR/Capture/Run V2 LEAN Smith A-B Smoke")]
        public static void RunSmokeFromMenu()
        {
            Run(RunConfiguration.Smoke, logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V2 720p LEAN Smith A-B")]
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
                Debug.LogError("OSFR V2 LEAN comparison requires a real graphics device.");
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
            Renderer[] renderers = root == null ? Array.Empty<Renderer>() : root.GetComponentsInChildren<Renderer>();
            Material[] materials = renderers
                .Select(renderer => renderer.sharedMaterial)
                .Where(material => material != null)
                .Distinct()
                .ToArray();
            if (camera == null || rail == null || feature == null
                || renderers.Length != 9 || materials.Length != 9)
            {
                Debug.LogError("The V2 LEAN comparison scene is incomplete.");
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
            float[] previousMode = materials.Select(material => material.GetFloat("_FilterMode")).ToArray();
            float[] previousDebug = materials.Select(material => material.GetFloat("_DebugMode")).ToArray();
            float[] previousLeanGain = materials.Select(material => material.GetFloat("_LeanVarianceGain")).ToArray();
            float[] previousVisibility = materials.Select(material => material.GetFloat("_LeanVisibilityMode")).ToArray();

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

                SetMaterialMode(materials, 0, 0, 1);
                Color[] raw = CaptureBuffer(
                    camera, feature, configuration,
                    Path.Combine(outputRoot, "color_raw.exr"));
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                TiledLinearHdrReferenceRenderer.Result lowerReference =
                    TiledLinearHdrReferenceRenderer.Capture(
                        camera,
                        configuration.Width,
                        configuration.Height,
                        configuration.LowerReferenceFactor,
                        TargetTileEdge,
                        Path.Combine(outputRoot, $"reference_{configuration.LowerReferenceFactor}x.exr"));
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                TiledLinearHdrReferenceRenderer.Result reference =
                    TiledLinearHdrReferenceRenderer.Capture(
                        camera,
                        configuration.Width,
                        configuration.Height,
                        configuration.ReferenceFactor,
                        TargetTileEdge,
                        Path.Combine(outputRoot, $"reference_{configuration.ReferenceFactor}x.exr"));

                SetMaterialMode(materials, 1, 0, 1);
                Color[] scalar = CaptureBuffer(
                    camera, feature, configuration,
                    Path.Combine(outputRoot, "color_scalar_gain64.exr"));
                SetMaterialMode(materials, 2, 0, 0);
                Color[] leanApproximate = CaptureBuffer(
                    camera, feature, configuration,
                    Path.Combine(outputRoot, "color_lean_geometric_mean_visibility.exr"));
                SetMaterialMode(materials, 2, 0, 1);
                Color[] leanSmith = CaptureBuffer(
                    camera, feature, configuration,
                    Path.Combine(outputRoot, "color_lean_anisotropic_smith.exr"));
                SetMaterialMode(materials, 2, 2, 1);
                CaptureBuffer(camera, feature, configuration,
                    Path.Combine(outputRoot, "lean_effective_alpha.exr"));
                SetMaterialMode(materials, 2, 3, 1);
                CaptureBuffer(camera, feature, configuration,
                    Path.Combine(outputRoot, "lean_anisotropy.exr"));
                SetMaterialMode(materials, 2, 4, 1);
                CaptureBuffer(camera, feature, configuration,
                    Path.Combine(outputRoot, "lean_principal_axis.exr"));

                Candidate rawCandidate = Evaluate("Raw", raw, reference.Pixels,
                    camera, renderers, configuration);
                Candidate scalarCandidate = Evaluate("ScalarGain64", scalar, reference.Pixels,
                    camera, renderers, configuration);
                Candidate leanApproximateCandidate = Evaluate(
                    "LEANGeometricMeanVisibility", leanApproximate, reference.Pixels,
                    camera, renderers, configuration);
                Candidate leanSmithCandidate = Evaluate(
                    "LEANAnisotropicSmith", leanSmith, reference.Pixels,
                    camera, renderers, configuration);
                Candidate[] candidates =
                {
                    rawCandidate,
                    scalarCandidate,
                    leanApproximateCandidate,
                    leanSmithCandidate
                };

                WriteMetrics(outputRoot, configuration.ReferenceFactor, candidates);
                WritePanelMetrics(outputRoot, candidates);
                WriteConvergence(outputRoot, lowerReference, reference);
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
                    materials[index].SetFloat("_FilterMode", previousMode[index]);
                    materials[index].SetFloat("_DebugMode", previousDebug[index]);
                    materials[index].SetFloat("_LeanVarianceGain", previousLeanGain[index]);
                    materials[index].SetFloat("_LeanVisibilityMode", previousVisibility[index]);
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
                Debug.Log($"Completed OSFR V2 LEAN comparison at {outputRoot}.");
            }

            return outputRoot;
        }

        private static Candidate Evaluate(
            string name,
            Color[] pixels,
            Color[] reference,
            Camera camera,
            IEnumerable<Renderer> renderers,
            RunConfiguration configuration)
        {
            FullReferenceMetricMath.Result full = FullReferenceMetricMath.Evaluate(
                pixels, reference, configuration.Width, configuration.Height, PyramidLevels);
            PanelMetric[] panels = renderers
                .OrderBy(renderer => renderer.name)
                .Select(renderer =>
                {
                    RectInt roi = ProjectRenderer(
                        renderer, camera, configuration.Width, configuration.Height);
                    FullReferenceMetricMath.Result metric = FullReferenceMetricMath.Evaluate(
                        Crop(pixels, configuration.Width, roi),
                        Crop(reference, configuration.Width, roi),
                        roi.width,
                        roi.height,
                        PyramidLevels);
                    return new PanelMetric(
                        renderer.name,
                        renderer.sharedMaterial.GetFloat("_NormalFrequency"),
                        renderer.sharedMaterial.GetFloat("_BaseAlpha"),
                        roi.width * roi.height,
                        metric);
                })
                .ToArray();
            return new Candidate(name, full, panels);
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

        private static void WriteMetrics(
            string folder,
            int referenceFactor,
            IEnumerable<Candidate> candidates)
        {
            string csv = "algorithm,reference,rgb_mse,psnr_db,luminance_mae,luminance_rmse,luminance_relative_rmse\n";
            foreach (Candidate candidate in candidates)
            {
                csv += string.Join(",", new[]
                {
                    candidate.Name,
                    $"{referenceFactor}x_spatial_supersample",
                    Format(candidate.Full.RgbMeanSquareError),
                    Format(candidate.Full.PsnrDecibels),
                    Format(candidate.Full.LuminanceMeanAbsoluteError),
                    Format(candidate.Full.LuminanceRootMeanSquareError),
                    Format(candidate.Full.LuminanceRelativeRootMeanSquareError)
                }) + "\n";
            }

            File.WriteAllText(Path.Combine(folder, "metrics.csv"), csv);
        }

        private static void WritePanelMetrics(string folder, IEnumerable<Candidate> candidates)
        {
            string csv = "panel,frequency_cycles_per_m,base_alpha,algorithm,pixels,luminance_rmse,"
                + "luminance_mae,rgb_mse,psnr_db\n";
            foreach (Candidate candidate in candidates)
            {
                foreach (PanelMetric panel in candidate.Panels)
                {
                    csv += string.Join(",", new[]
                    {
                        panel.Name.Replace(',', '_'),
                        Format(panel.Frequency),
                        Format(panel.BaseAlpha),
                        candidate.Name,
                        panel.PixelCount.ToString(CultureInfo.InvariantCulture),
                        Format(panel.Metric.LuminanceRootMeanSquareError),
                        Format(panel.Metric.LuminanceMeanAbsoluteError),
                        Format(panel.Metric.RgbMeanSquareError),
                        Format(panel.Metric.PsnrDecibels)
                    }) + "\n";
                }
            }

            File.WriteAllText(Path.Combine(folder, "panel_metrics.csv"), csv);
        }

        private static void WriteConvergence(
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
            RunConfiguration configuration,
            TiledLinearHdrReferenceRenderer.Result lower,
            TiledLinearHdrReferenceRenderer.Result upper)
        {
            var metadata = new Metadata
            {
                schemaVersion = 1,
                runId = runId,
                matrix = configuration.MatrixName,
                scene = "V2_LeanFiltering",
                createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                unityVersion = Application.unityVersion,
                graphicsDevice = SystemInfo.graphicsDeviceName,
                output = new[] { configuration.Width, configuration.Height },
                frame = Frame,
                frequenciesCyclesPerMeter = new[] { 16.0f, 64.0f, 256.0f },
                baseMicrofacetAlpha = new[] { 0.03f, 0.1f, 0.3f },
                normalSlopeAmplitude = 0.9f,
                directionalPatternAngleDegrees = V2LeanValidationSceneBuilder.PatternAngleDegrees,
                scalarVarianceGain = V2ValidationSceneBuilder.PromotedVarianceGain,
                leanVarianceGain = V2LeanValidationSceneBuilder.InitialLeanVarianceGain,
                leanModel = "3x3 output-footprint first/second slope moments; covariance eigenvectors orient alpha_major/alpha_minor; anisotropic GGX NDF.",
                visibilityControls = new[]
                {
                    "Geometric-mean alpha with the legacy separable Smith approximation.",
                    "Height-correlated anisotropic Smith-GGX visibility using tangent-frame view and light projections."
                },
                qualification = "LEAN-like prototype, not a reproduction of the full LEAN mapping storage or BRDF derivation.",
                lowerReferenceFactor = lower.Factor,
                referenceFactor = upper.Factor,
                referencePolicy = "Raw directional procedural normal field rendered at higher linear resolution and box-averaged in linear HDR."
            };
            File.WriteAllText(
                Path.Combine(folder, "metadata.json"),
                JsonUtility.ToJson(metadata, true) + Environment.NewLine);
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
                material.SetFloat("_LeanVarianceGain", V2LeanValidationSceneBuilder.InitialLeanVarianceGain);
                material.SetFloat("_LeanVisibilityMode", visibilityMode);
                material.SetFloat("_DebugMode", debugMode);
            }
        }

        private static RectInt ProjectRenderer(Renderer renderer, Camera camera, int width, int height)
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

            int xMin = Mathf.Clamp(Mathf.CeilToInt(minX * width) + 1, 0, width - 1);
            int yMin = Mathf.Clamp(Mathf.CeilToInt(minY * height) + 1, 0, height - 1);
            int xMax = Mathf.Clamp(Mathf.FloorToInt(maxX * width) - 1, xMin + 1, width);
            int yMax = Mathf.Clamp(Mathf.FloorToInt(maxY * height) - 1, yMin + 1, height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        private static Color[] Crop(Color[] source, int sourceWidth, RectInt roi)
        {
            var result = new Color[roi.width * roi.height];
            for (int y = 0; y < roi.height; y++)
            {
                Array.Copy(source, (roi.y + y) * sourceWidth + roi.x,
                    result, y * roi.width, roi.width);
            }

            return result;
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

        private sealed class Candidate
        {
            public Candidate(string name, FullReferenceMetricMath.Result full, PanelMetric[] panels)
            {
                Name = name;
                Full = full;
                Panels = panels;
            }

            public string Name { get; }
            public FullReferenceMetricMath.Result Full { get; }
            public PanelMetric[] Panels { get; }
        }

        private readonly struct PanelMetric
        {
            public PanelMetric(
                string name,
                float frequency,
                float baseAlpha,
                int pixelCount,
                FullReferenceMetricMath.Result metric)
            {
                Name = name;
                Frequency = frequency;
                BaseAlpha = baseAlpha;
                PixelCount = pixelCount;
                Metric = metric;
            }

            public string Name { get; }
            public float Frequency { get; }
            public float BaseAlpha { get; }
            public int PixelCount { get; }
            public FullReferenceMetricMath.Result Metric { get; }
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
                "lean_smith_ab_smoke", "320x180_lean_smith_ab_smoke",
                320, 180, 2, 4);
            public static RunConfiguration Primary => new RunConfiguration(
                "lean_smith_ab_720p", "720p_lean_smith_ab_comparison",
                1280, 720, 8, 16);

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
            public string matrix;
            public string scene;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int[] output;
            public int frame;
            public float[] frequenciesCyclesPerMeter;
            public float[] baseMicrofacetAlpha;
            public float normalSlopeAmplitude;
            public float directionalPatternAngleDegrees;
            public float scalarVarianceGain;
            public float leanVarianceGain;
            public string leanModel;
            public string[] visibilityControls;
            public string qualification;
            public int lowerReferenceFactor;
            public int referenceFactor;
            public string referencePolicy;
        }
    }
}
