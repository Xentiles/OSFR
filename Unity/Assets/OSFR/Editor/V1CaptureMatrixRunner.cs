using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
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
    /// Captures immutable raw/filtered/diagnostic V1 datasets. The smoke matrix is
    /// deliberately small; the primary snapshot matrix spans all report resolutions.
    /// Full 600-frame temporal sequences are a separate follow-on workload.
    /// </summary>
    public static class V1CaptureMatrixRunner
    {
        private const float FixedDelta = 1.0f / 60.0f;
        private const int Seed = 12345;

        private static readonly OutputResolution[] ReportResolutions =
        {
            new OutputResolution(1280, 720),
            new OutputResolution(1920, 1080),
            new OutputResolution(2560, 1440),
            new OutputResolution(3840, 2160)
        };

        [MenuItem("OSFR/Capture/Run V1 Smoke Matrix")]
        public static void RunSmokeFromMenu()
        {
            RunSmoke(logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V1 Primary Snapshot Matrix")]
        public static void RunPrimarySnapshotsFromMenu()
        {
            RunPrimarySnapshots(logSuccess: true);
        }

        public static void RunSmokeFromCommandLine()
        {
            if (RunSmoke(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static void RunPrimarySnapshotsFromCommandLine()
        {
            if (RunPrimarySnapshots(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static string RunSmoke(bool logSuccess)
        {
            var resolutions = new[]
            {
                new OutputResolution(320, 180),
                new OutputResolution(640, 360)
            };
            return Run("smoke", BuildConditions(resolutions, new[] { 0, 300, 599 }, includeBoundaryCase: true), logSuccess);
        }

        public static string RunPrimarySnapshots(bool logSuccess)
        {
            return Run(
                "primary_snapshots",
                BuildConditions(ReportResolutions, new[] { 0, 150, 300, 450, 599 }, includeBoundaryCase: true),
                logSuccess);
        }

        private static string Run(string matrixName, IReadOnlyList<CaptureCondition> conditions, bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR V1 capture requires a real graphics device.");
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
                Debug.LogError("The V1 capture scene is missing its camera, rail, feature, or case roots.");
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
            RenderTexture previousTarget = camera.targetTexture;
            float previousFixedDelta = Time.fixedDeltaTime;
            UnityEngine.Random.State previousRandomState = UnityEngine.Random.state;
            var manifestEntries = new List<ManifestEntry>(conditions.Count);

            try
            {
                rail.enabled = false;
                Time.fixedDeltaTime = FixedDelta;
                UnityEngine.Random.InitState(Seed);
                feature.FeatureSettings.displayedPyramidLevel = 0;

                foreach (CaptureCondition condition in conditions)
                {
                    checkerRoot.SetActive(condition.caseName == "CheckerboardWall");
                    boundaryRoot.SetActive(condition.caseName == "DepthDiscontinuityPole");
                    rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
                    rail.ApplyFrame(condition.frame);
                    manifestEntries.Add(CaptureConditionSet(camera, feature, outputRoot, condition));
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
                    conditionCount = manifestEntries.Count,
                    conditions = manifestEntries.ToArray()
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
                Time.fixedDeltaTime = previousFixedDelta;
                UnityEngine.Random.state = previousRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V1 {matrixName} matrix: {conditions.Count} conditions at {outputRoot}.");
            }

            return outputRoot;
        }

        private static ManifestEntry CaptureConditionSet(
            Camera camera,
            MeasurementRendererFeature feature,
            string outputRoot,
            CaptureCondition condition)
        {
            string conditionId = $"{condition.caseName.ToLowerInvariant()}_lateral_f{condition.frame:000}_"
                + $"{condition.resolution.width}x{condition.resolution.height}";
            string folder = Path.Combine(outputRoot, conditionId);
            Directory.CreateDirectory(folder);
            feature.FeatureSettings.outputResolutionOverride = new Vector2Int(
                condition.resolution.width,
                condition.resolution.height);

            CaptureResult raw = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.PyramidMip,
                Path.Combine(folder, "color_raw_000000.exr"));
            CaptureResult filtered = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.BilateralFilteredColor,
                Path.Combine(folder, "color_bilateral_000000.exr"));
            CaptureResult risk = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.AliasRiskData,
                Path.Combine(folder, "alias_risk_000000.exr"));
            CaptureResult depthRejection = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.DepthRejection,
                Path.Combine(folder, "depth_rejection_000000.exr"));
            CaptureResult normalRejection = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.NormalRejection,
                Path.Combine(folder, "normal_rejection_000000.exr"));
            CaptureResult weightSum = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.BilateralWeightSum,
                Path.Combine(folder, "bilateral_weight_sum_000000.exr"));

            var metadata = new CaptureMetadata
            {
                schemaVersion = 1,
                scene = condition.caseName,
                cameraPath = "Lateral",
                output = new[] { condition.resolution.width, condition.resolution.height },
                render = new[] { condition.resolution.width, condition.resolution.height },
                fovY = 60.0f,
                aa = "None",
                bandLimit = true,
                filterPreset = "V1_5x5_Balanced720p_01",
                fixedDelta = FixedDelta,
                seed = Seed,
                frame = condition.frame,
                buffers = new[]
                {
                    "color_raw_000000.exr:linear HDR unfiltered L0",
                    "color_bilateral_000000.exr:linear HDR risk-driven 5x5 bilateral",
                    "alias_risk_000000.exr:R=risk,G=fine ratio,B=footprint weight,A=edge confidence",
                    "depth_rejection_000000.exr:RGB=relative-depth rejection",
                    "normal_rejection_000000.exr:RGB=normal rejection",
                    "bilateral_weight_sum_000000.exr:RGB=normalized joint support"
                }
            };
            WriteJson(Path.Combine(folder, "metadata.json"), metadata);

            string metrics = "frame,width,height,raw_mean_luminance,raw_luminance_variance,"
                + "filtered_mean_luminance,filtered_luminance_variance,risk_mean,risk_max,"
                + "depth_rejection_max,normal_rejection_max,weight_sum_mean,cpu_capture_ms\n"
                + string.Join(",", new[]
                {
                    condition.frame.ToString(CultureInfo.InvariantCulture),
                    condition.resolution.width.ToString(CultureInfo.InvariantCulture),
                    condition.resolution.height.ToString(CultureInfo.InvariantCulture),
                    Format(raw.meanLuminance),
                    Format(raw.luminanceVariance),
                    Format(filtered.meanLuminance),
                    Format(filtered.luminanceVariance),
                    Format(risk.meanRed),
                    Format(risk.maximumRed),
                    Format(depthRejection.maximumRed),
                    Format(normalRejection.maximumRed),
                    Format(weightSum.meanRed),
                    Format(raw.elapsedMilliseconds + filtered.elapsedMilliseconds + risk.elapsedMilliseconds
                        + depthRejection.elapsedMilliseconds + normalRejection.elapsedMilliseconds
                        + weightSum.elapsedMilliseconds)
                }) + "\n";
            File.WriteAllText(Path.Combine(folder, "metrics.csv"), metrics);

            return new ManifestEntry
            {
                id = conditionId,
                metadata = $"{conditionId}/metadata.json",
                metrics = $"{conditionId}/metrics.csv"
            };
        }

        private static CaptureResult CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            OutputResolution resolution,
            MeasurementDebugView view,
            string outputPath)
        {
            var target = new RenderTexture(
                resolution.width,
                resolution.height,
                24,
                RenderTextureFormat.ARGBFloat,
                RenderTextureReadWrite.Linear);
            var readback = new Texture2D(
                resolution.width,
                resolution.height,
                TextureFormat.RGBAFloat,
                false,
                true);
            RenderTexture previousActive = RenderTexture.active;
            var stopwatch = Stopwatch.StartNew();

            try
            {
                feature.FeatureSettings.debugView = view;
                camera.targetTexture = target;
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0, 0, resolution.width, resolution.height), 0, 0, false);
                readback.Apply(false, false);
                Color[] pixels = readback.GetPixels();
                byte[] exr = readback.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                File.WriteAllBytes(outputPath, exr);
                stopwatch.Stop();
                return Measure(pixels, (float)stopwatch.Elapsed.TotalMilliseconds);
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(target);
            }
        }

        private static CaptureResult Measure(Color[] pixels, float elapsedMilliseconds)
        {
            double luminanceSum = 0.0;
            double luminanceSquaredSum = 0.0;
            double redSum = 0.0;
            float maximumRed = float.MinValue;
            foreach (Color pixel in pixels)
            {
                float luminance = 0.2126f * pixel.r + 0.7152f * pixel.g + 0.0722f * pixel.b;
                luminanceSum += luminance;
                luminanceSquaredSum += luminance * luminance;
                redSum += pixel.r;
                maximumRed = Mathf.Max(maximumRed, pixel.r);
            }

            double inverseCount = 1.0 / pixels.Length;
            double meanLuminance = luminanceSum * inverseCount;
            return new CaptureResult(
                (float)meanLuminance,
                (float)(luminanceSquaredSum * inverseCount - meanLuminance * meanLuminance),
                (float)(redSum * inverseCount),
                maximumRed,
                elapsedMilliseconds);
        }

        private static List<CaptureCondition> BuildConditions(
            IReadOnlyList<OutputResolution> resolutions,
            IReadOnlyList<int> checkerFrames,
            bool includeBoundaryCase)
        {
            var conditions = new List<CaptureCondition>();
            foreach (OutputResolution resolution in resolutions)
            {
                foreach (int frame in checkerFrames)
                {
                    conditions.Add(new CaptureCondition("CheckerboardWall", resolution, frame));
                }

                if (includeBoundaryCase)
                {
                    conditions.Add(new CaptureCondition("DepthDiscontinuityPole", resolution, 300));
                }
            }

            return conditions;
        }

        private static MeasurementRendererFeature FindMeasurementFeature()
        {
            UniversalRenderPipelineAsset pipeline = GraphicsSettings.defaultRenderPipeline as UniversalRenderPipelineAsset;
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

        private readonly struct OutputResolution
        {
            public OutputResolution(int width, int height)
            {
                this.width = width;
                this.height = height;
            }

            public readonly int width;
            public readonly int height;
        }

        private readonly struct CaptureCondition
        {
            public CaptureCondition(string caseName, OutputResolution resolution, int frame)
            {
                this.caseName = caseName;
                this.resolution = resolution;
                this.frame = frame;
            }

            public readonly string caseName;
            public readonly OutputResolution resolution;
            public readonly int frame;
        }

        private readonly struct CaptureResult
        {
            public CaptureResult(
                float meanLuminance,
                float luminanceVariance,
                float meanRed,
                float maximumRed,
                float elapsedMilliseconds)
            {
                this.meanLuminance = meanLuminance;
                this.luminanceVariance = luminanceVariance;
                this.meanRed = meanRed;
                this.maximumRed = maximumRed;
                this.elapsedMilliseconds = elapsedMilliseconds;
            }

            public readonly float meanLuminance;
            public readonly float luminanceVariance;
            public readonly float meanRed;
            public readonly float maximumRed;
            public readonly float elapsedMilliseconds;
        }

        [Serializable]
        private sealed class CaptureMetadata
        {
            public int schemaVersion;
            public string scene;
            public string cameraPath;
            public int[] output;
            public int[] render;
            public float fovY;
            public string aa;
            public bool bandLimit;
            public string filterPreset;
            public float fixedDelta;
            public int seed;
            public int frame;
            public string[] buffers;
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

        [Serializable]
        private sealed class ManifestEntry
        {
            public string id;
            public string metadata;
            public string metrics;
        }
    }
}
