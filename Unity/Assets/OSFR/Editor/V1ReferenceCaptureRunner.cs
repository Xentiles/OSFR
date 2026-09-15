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
    /// Produces tiled 4x and 8x linear-HDR spatial references. The 8x result is
    /// treated as the candidate reference only after its delta from 4x is recorded.
    /// </summary>
    public static class V1ReferenceCaptureRunner
    {
        private const int TargetTileEdge = 128;
        private const int Seed = 12345;

        [MenuItem("OSFR/Capture/Run V1 Reference Smoke")]
        public static void RunSmokeFromMenu()
        {
            RunReferenceSmoke(logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V1 Selected 720p References")]
        public static void RunSelected720pFromMenu()
        {
            RunSelected720p(logSuccess: true);
        }

        public static void RunSmokeFromCommandLine()
        {
            if (RunReferenceSmoke(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static void RunSelected720pFromCommandLine()
        {
            if (RunSelected720p(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static string RunReferenceSmoke(bool logSuccess)
        {
            var conditions = new[]
            {
                new ReferenceCondition("CheckerboardWall", 160, 90, 300),
                new ReferenceCondition("DepthDiscontinuityPole", 160, 90, 300)
            };
            return Run(
                "reference_smoke",
                conditions,
                validateUntiled: true,
                include16x: false,
                logSuccess: logSuccess);
        }

        public static string RunSelected720p(bool logSuccess)
        {
            var conditions = new[]
            {
                new ReferenceCondition("CheckerboardWall", 1280, 720, 300),
                new ReferenceCondition("DepthDiscontinuityPole", 1280, 720, 300)
            };
            return Run(
                "selected_720p_references",
                conditions,
                validateUntiled: false,
                include16x: true,
                logSuccess: logSuccess);
        }

        private static string Run(
            string matrixName,
            IReadOnlyList<ReferenceCondition> conditions,
            bool validateUntiled,
            bool include16x,
            bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR reference capture requires a real graphics device.");
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
                Debug.LogError("The V1 reference scene is missing its camera, rail, feature, or case roots.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"{matrixName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            Directory.CreateDirectory(outputRoot);

            MeasurementDebugView previousView = feature.FeatureSettings.debugView;
            int previousMip = feature.FeatureSettings.displayedPyramidLevel;
            Vector2Int previousOverride = feature.FeatureSettings.outputResolutionOverride;
            bool previousRailEnabled = rail.enabled;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;
            var entries = new List<ManifestEntry>(conditions.Count);

            try
            {
                rail.enabled = false;
                UnityEngine.Random.InitState(Seed);
                feature.FeatureSettings.debugView = MeasurementDebugView.PyramidMip;
                feature.FeatureSettings.displayedPyramidLevel = 0;

                foreach (ReferenceCondition condition in conditions)
                {
                    checkerRoot.SetActive(condition.caseName == "CheckerboardWall");
                    boundaryRoot.SetActive(condition.caseName == "DepthDiscontinuityPole");
                    rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                    rail.ApplyFrame(condition.frame);
                    feature.FeatureSettings.outputResolutionOverride =
                        new Vector2Int(condition.width, condition.height);
                    entries.Add(CaptureCondition(
                        camera,
                        outputRoot,
                        condition,
                        validateUntiled,
                        include16x));
                }

                var manifest = new RunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = matrixName,
                    scene = "V1_ScreenSpaceValidation",
                    createdUtc = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                    unityVersion = Application.unityVersion,
                    graphicsDevice = SystemInfo.graphicsDeviceName,
                    targetTileEdge = TargetTileEdge,
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
                UnityEngine.Random.state = previousRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V1 {matrixName}: {conditions.Count} conditions at {outputRoot}.");
            }

            return outputRoot;
        }

        private static ManifestEntry CaptureCondition(
            Camera camera,
            string outputRoot,
            ReferenceCondition condition,
            bool validateUntiled,
            bool include16x)
        {
            string id = $"{condition.caseName.ToLowerInvariant()}_lateral_f{condition.frame:000}_"
                + $"{condition.width}x{condition.height}";
            string folder = Path.Combine(outputRoot, id);
            Directory.CreateDirectory(folder);

            TiledLinearHdrReferenceRenderer.Result reference4x = TiledLinearHdrReferenceRenderer.Capture(
                camera,
                condition.width,
                condition.height,
                4,
                TargetTileEdge,
                Path.Combine(folder, "reference_4x_000000.exr"));
            TiledLinearHdrReferenceRenderer.Result reference8x = TiledLinearHdrReferenceRenderer.Capture(
                camera,
                condition.width,
                condition.height,
                8,
                TargetTileEdge,
                Path.Combine(folder, "reference_8x_000000.exr"));
            SupersampleReferenceMath.LuminanceComparison convergence =
                SupersampleReferenceMath.CompareLuminance(reference4x.Pixels, reference8x.Pixels);

            TiledLinearHdrReferenceRenderer.Result reference16x = null;
            SupersampleReferenceMath.LuminanceComparison convergence8To16 = default;
            if (include16x)
            {
                reference16x = TiledLinearHdrReferenceRenderer.Capture(
                    camera,
                    condition.width,
                    condition.height,
                    16,
                    TargetTileEdge,
                    Path.Combine(folder, "reference_16x_000000.exr"));
                convergence8To16 = SupersampleReferenceMath.CompareLuminance(
                    reference8x.Pixels,
                    reference16x.Pixels);
            }

            string tilingValidationPath = null;
            string untiledReferencePath = null;
            if (validateUntiled)
            {
                const string untiledFileName = "reference_8x_untiled_validation.exr";
                TiledLinearHdrReferenceRenderer.Result untiled8x = TiledLinearHdrReferenceRenderer.Capture(
                    camera,
                    condition.width,
                    condition.height,
                    8,
                    Mathf.Max(condition.width, condition.height),
                    Path.Combine(folder, untiledFileName));
                SupersampleReferenceMath.LuminanceComparison tilingComparison =
                    SupersampleReferenceMath.CompareLuminance(reference8x.Pixels, untiled8x.Pixels);
                const string validationFileName = "tiling_validation.csv";
                string validation = "frame,width,height,comparison,mean_absolute_luminance_error,"
                    + "rmse_luminance,max_absolute_luminance_error,relative_rmse,"
                    + "tiled_tiles,untiled_tiles\n"
                    + string.Join(",", new[]
                    {
                        condition.frame.ToString(CultureInfo.InvariantCulture),
                        condition.width.ToString(CultureInfo.InvariantCulture),
                        condition.height.ToString(CultureInfo.InvariantCulture),
                        "8x_tiled_vs_8x_untiled",
                        Format(tilingComparison.MeanAbsoluteError),
                        Format(tilingComparison.RootMeanSquareError),
                        Format(tilingComparison.MaximumAbsoluteError),
                        Format(tilingComparison.RelativeRootMeanSquareError),
                        reference8x.TileCount.ToString(CultureInfo.InvariantCulture),
                        untiled8x.TileCount.ToString(CultureInfo.InvariantCulture)
                    }) + "\n";
                File.WriteAllText(Path.Combine(folder, validationFileName), validation);
                tilingValidationPath = $"{id}/{validationFileName}";
                untiledReferencePath = $"{id}/{untiledFileName}";
            }

            var metadata = new ReferenceMetadata
            {
                schemaVersion = 1,
                scene = condition.caseName,
                cameraPath = "Lateral",
                output = new[] { condition.width, condition.height },
                fovY = 60.0f,
                aa = "None",
                bandLimit = false,
                toneMapping = "None",
                colorSpace = "Linear HDR",
                seed = Seed,
                frame = condition.frame,
                supersampleFactors = include16x ? new[] { 4, 8, 16 } : new[] { 4, 8 },
                targetTileEdge = TargetTileEdge,
                referencePolicy = include16x
                    ? "Use the highest factor only after reviewing both convergence intervals."
                    : "Use 8x only after reviewing 4x-to-8x convergence; verify selected cases at 16x."
            };
            WriteJson(Path.Combine(folder, "metadata.json"), metadata);

            string metrics = "frame,width,height,comparison,candidate_factor,reference_factor,"
                + "mean_absolute_luminance_error,rmse_luminance,max_absolute_luminance_error,"
                + "relative_rmse,candidate_ms,reference_ms,candidate_tiles,reference_tiles,"
                + "peak_render_width,peak_render_height\n"
                + ConvergenceRow(condition, "4x_vs_8x", reference4x, reference8x, convergence);
            if (reference16x != null)
            {
                metrics += ConvergenceRow(
                    condition,
                    "8x_vs_16x",
                    reference8x,
                    reference16x,
                    convergence8To16);
            }

            File.WriteAllText(Path.Combine(folder, "convergence.csv"), metrics);

            return new ManifestEntry
            {
                id = id,
                metadata = $"{id}/metadata.json",
                convergence = $"{id}/convergence.csv",
                reference4x = $"{id}/reference_4x_000000.exr",
                reference8x = $"{id}/reference_8x_000000.exr",
                reference16x = reference16x == null ? null : $"{id}/reference_16x_000000.exr",
                tilingValidation = tilingValidationPath,
                untiledReference8x = untiledReferencePath
            };
        }

        private static string ConvergenceRow(
            ReferenceCondition condition,
            string comparisonName,
            TiledLinearHdrReferenceRenderer.Result candidate,
            TiledLinearHdrReferenceRenderer.Result reference,
            SupersampleReferenceMath.LuminanceComparison comparison)
        {
            return string.Join(",", new[]
            {
                condition.frame.ToString(CultureInfo.InvariantCulture),
                condition.width.ToString(CultureInfo.InvariantCulture),
                condition.height.ToString(CultureInfo.InvariantCulture),
                comparisonName,
                candidate.Factor.ToString(CultureInfo.InvariantCulture),
                reference.Factor.ToString(CultureInfo.InvariantCulture),
                Format(comparison.MeanAbsoluteError),
                Format(comparison.RootMeanSquareError),
                Format(comparison.MaximumAbsoluteError),
                Format(comparison.RelativeRootMeanSquareError),
                Format(candidate.ElapsedMilliseconds),
                Format(reference.ElapsedMilliseconds),
                candidate.TileCount.ToString(CultureInfo.InvariantCulture),
                reference.TileCount.ToString(CultureInfo.InvariantCulture),
                Mathf.Max(candidate.PeakRenderWidth, reference.PeakRenderWidth)
                    .ToString(CultureInfo.InvariantCulture),
                Mathf.Max(candidate.PeakRenderHeight, reference.PeakRenderHeight)
                    .ToString(CultureInfo.InvariantCulture)
            }) + "\n";
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

        private readonly struct ReferenceCondition
        {
            public ReferenceCondition(string caseName, int width, int height, int frame)
            {
                this.caseName = caseName;
                this.width = width;
                this.height = height;
                this.frame = frame;
            }

            public readonly string caseName;
            public readonly int width;
            public readonly int height;
            public readonly int frame;
        }

        [Serializable]
        private sealed class ReferenceMetadata
        {
            public int schemaVersion;
            public string scene;
            public string cameraPath;
            public int[] output;
            public float fovY;
            public string aa;
            public bool bandLimit;
            public string toneMapping;
            public string colorSpace;
            public int seed;
            public int frame;
            public int[] supersampleFactors;
            public int targetTileEdge;
            public string referencePolicy;
        }

        [Serializable]
        private sealed class ManifestEntry
        {
            public string id;
            public string metadata;
            public string convergence;
            public string reference4x;
            public string reference8x;
            public string reference16x;
            public string tilingValidation;
            public string untiledReference8x;
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
            public int targetTileEdge;
            public int conditionCount;
            public ManifestEntry[] conditions;
        }
    }
}
