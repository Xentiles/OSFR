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

namespace OSFR.Editor
{
    /// <summary>
    /// Produces deterministic, floating-point V0 datasets outside Assets.
    /// The smoke matrix verifies the pipeline quickly; the primary matrix uses
    /// every resolution and planar condition prescribed by the research report.
    /// </summary>
    public static class V0CaptureMatrixRunner
    {
        private const string ReferencePlaneName = "Reference Plane - world Z 10 - camera frame 0 eye depth 20";
        private const float FieldOfView = 60.0f;
        private const float FixedDelta = 1.0f / 60.0f;
        private const int Seed = 12345;

        private static readonly Resolution[] PrimaryResolutions =
        {
            new Resolution(1280, 720),
            new Resolution(1920, 1080),
            new Resolution(2560, 1440),
            new Resolution(3840, 2160)
        };

        private static readonly float[] PrimaryDistances = { 1.0f, 2.0f, 5.0f, 10.0f, 20.0f, 50.0f, 100.0f };
        private static readonly float[] PrimaryAngles = { 30.0f, 60.0f, 80.0f };

        [MenuItem("OSFR/Capture/Run V0 Smoke Matrix")]
        public static void RunSmokeFromMenu()
        {
            RunSmoke(logSuccess: true);
        }

        [MenuItem("OSFR/Capture/Run V0 Primary Matrix")]
        public static void RunPrimaryFromMenu()
        {
            RunPrimary(logSuccess: true);
        }

        public static void RunSmokeFromCommandLine()
        {
            if (RunSmoke(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static void RunPrimaryFromCommandLine()
        {
            if (RunPrimary(logSuccess: true) == null)
            {
                EditorApplication.Exit(1);
            }
        }

        public static string RunSmoke(bool logSuccess)
        {
            Resolution[] resolutions =
            {
                new Resolution(320, 180),
                new Resolution(640, 360)
            };
            float[] distances = { 5.0f, 20.0f };
            return Run("smoke", BuildMatrix(resolutions, distances, Array.Empty<float>()), logSuccess);
        }

        public static string RunPrimary(bool logSuccess)
        {
            return Run(
                "primary",
                BuildMatrix(PrimaryResolutions, PrimaryDistances, PrimaryAngles),
                logSuccess);
        }

        private static string Run(string matrixName, IReadOnlyList<CaptureCondition> conditions, bool logSuccess)
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR capture requires a real graphics device. Run without the -nographics Unity argument.");
                return null;
            }

            SceneSetup[] originalSceneSetup = EditorSceneManager.GetSceneManagerSetup();
            if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return null;
            }

            if (!ValidationSceneBuilder.CreateOrOpen(logSuccess: false))
            {
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            Camera camera = UnityEngine.Object.FindFirstObjectByType<Camera>();
            DeterministicCameraRail rail = UnityEngine.Object.FindFirstObjectByType<DeterministicCameraRail>();
            GameObject referencePlane = GameObject.Find(ReferencePlaneName);
            MeasurementRendererFeature feature = FindMeasurementFeature();
            if (camera == null || rail == null || referencePlane == null || feature == null)
            {
                Debug.LogError("The V0 capture harness is missing its camera, rail, reference plane, or renderer feature.");
                RestoreSceneSetup(originalSceneSetup);
                return null;
            }

            string runId = $"{matrixName}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V0", runId);
            Directory.CreateDirectory(outputRoot);

            Transform cameraTransform = camera.transform;
            Transform planeTransform = referencePlane.transform;
            Vector3 originalCameraPosition = cameraTransform.position;
            Quaternion originalCameraRotation = cameraTransform.rotation;
            Vector3 originalPlanePosition = planeTransform.position;
            Quaternion originalPlaneRotation = planeTransform.rotation;
            Vector3 originalPlaneScale = planeTransform.localScale;
            bool originalRailEnabled = rail.enabled;
            RenderTexture originalTarget = camera.targetTexture;
            float originalFieldOfView = camera.fieldOfView;
            MeasurementDebugView originalView = feature.FeatureSettings.debugView;
            Vector2Int originalOutputOverride = feature.FeatureSettings.outputResolutionOverride;
            float originalFixedDelta = Time.fixedDeltaTime;
            UnityEngine.Random.State originalRandomState = UnityEngine.Random.state;

            var manifestEntries = new List<CaptureManifestEntry>(conditions.Count);
            try
            {
                rail.enabled = false;
                cameraTransform.SetPositionAndRotation(new Vector3(0.0f, 1.6f, 0.0f), Quaternion.identity);
                camera.fieldOfView = FieldOfView;
                Time.fixedDeltaTime = FixedDelta;
                UnityEngine.Random.InitState(Seed);

                foreach (CaptureCondition condition in conditions)
                {
                    CaptureManifestEntry entry = CaptureSingleCondition(
                        camera,
                        referencePlane.transform,
                        feature,
                        outputRoot,
                        condition);
                    manifestEntries.Add(entry);
                }

                var manifest = new CaptureRunManifest
                {
                    schemaVersion = 1,
                    runId = runId,
                    matrix = matrixName,
                    scene = "V0_PlanarValidation",
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
                camera.targetTexture = originalTarget;
                camera.fieldOfView = originalFieldOfView;
                cameraTransform.SetPositionAndRotation(originalCameraPosition, originalCameraRotation);
                planeTransform.SetPositionAndRotation(originalPlanePosition, originalPlaneRotation);
                planeTransform.localScale = originalPlaneScale;
                rail.enabled = originalRailEnabled;
                feature.FeatureSettings.debugView = originalView;
                feature.FeatureSettings.outputResolutionOverride = originalOutputOverride;
                Time.fixedDeltaTime = originalFixedDelta;
                UnityEngine.Random.state = originalRandomState;
                RestoreSceneSetup(originalSceneSetup);
            }

            if (logSuccess)
            {
                Debug.Log($"Completed OSFR V0 {matrixName} capture matrix: {conditions.Count} conditions at {outputRoot}.");
            }

            return outputRoot;
        }

        private static CaptureManifestEntry CaptureSingleCondition(
            Camera camera,
            Transform referencePlane,
            MeasurementRendererFeature feature,
            string outputRoot,
            CaptureCondition condition)
        {
            string conditionId = string.Format(
                CultureInfo.InvariantCulture,
                "front_d{0:000.###}_a{1:00}_{2}x{3}",
                condition.distanceMetres,
                condition.angleDegrees,
                condition.resolution.width,
                condition.resolution.height);
            conditionId = conditionId.Replace('.', '_');
            string folder = Path.Combine(outputRoot, conditionId);
            Directory.CreateDirectory(folder);

            referencePlane.SetPositionAndRotation(
                new Vector3(0.0f, 1.6f, condition.distanceMetres),
                Quaternion.Euler(0.0f, condition.angleDegrees, 0.0f));
            float panelSize = condition.distanceMetres * 0.5f;
            referencePlane.localScale = new Vector3(panelSize, panelSize, 1.0f);

            feature.FeatureSettings.outputResolutionOverride = new Vector2Int(
                condition.resolution.width,
                condition.resolution.height);

            Color linearDepth = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.LinearEyeDepthData,
                Path.Combine(folder, "depth_000000.exr"));
            CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.WorldPositionData,
                Path.Combine(folder, "world_position_000000.exr"));
            Color footprint = CaptureBuffer(
                camera,
                feature,
                condition.resolution,
                MeasurementDebugView.FootprintData,
                Path.Combine(folder, "footprint_000000.exr"));

            float expectedFrontal = PixelFootprintMath.VerticalWorldUnitsPerOutputPixel(
                condition.distanceMetres,
                FieldOfView,
                condition.resolution.height);
            float relativeError = condition.angleDegrees == 0.0f
                ? Mathf.Abs(footprint.r - expectedFrontal) / expectedFrontal
                : float.NaN;

            var metadata = new CaptureMetadata
            {
                schemaVersion = 1,
                scene = "V0_PlanarValidation",
                cameraPath = "StaticPlanar",
                output = new[] { condition.resolution.width, condition.resolution.height },
                render = new[] { condition.resolution.width, condition.resolution.height },
                fovY = FieldOfView,
                aa = "None",
                bandLimit = false,
                filterPreset = "V0_Measurement",
                fixedDelta = FixedDelta,
                seed = Seed,
                frame = 0,
                distanceMetres = condition.distanceMetres,
                angleDegrees = condition.angleDegrees,
                buffers = new[]
                {
                    "depth_000000.exr:R=linear eye depth metres",
                    "world_position_000000.exr:RGB=world position metres",
                    "footprint_000000.exr:R=major,G=minor,B=area,A=validity"
                }
            };
            WriteJson(Path.Combine(folder, "metadata.json"), metadata);

            string metrics = "frame,distance_m,angle_deg,width,height,linear_depth_m,expected_frontal_axis_m,"
                + "measured_major_m,measured_minor_m,measured_area_m2,neighbor_validity,relative_error\n"
                + string.Join(",", new[]
                {
                    "0",
                    Format(condition.distanceMetres),
                    Format(condition.angleDegrees),
                    condition.resolution.width.ToString(CultureInfo.InvariantCulture),
                    condition.resolution.height.ToString(CultureInfo.InvariantCulture),
                    Format(linearDepth.r),
                    Format(expectedFrontal),
                    Format(footprint.r),
                    Format(footprint.g),
                    Format(footprint.b),
                    Format(footprint.a),
                    Format(relativeError)
                }) + "\n";
            File.WriteAllText(Path.Combine(folder, "metrics.csv"), metrics);

            return new CaptureManifestEntry
            {
                id = conditionId,
                metadata = $"{conditionId}/metadata.json",
                metrics = $"{conditionId}/metrics.csv"
            };
        }

        private static Color CaptureBuffer(
            Camera camera,
            MeasurementRendererFeature feature,
            Resolution resolution,
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

            try
            {
                feature.FeatureSettings.debugView = view;
                camera.targetTexture = target;
                camera.Render();

                RenderTexture.active = target;
                readback.ReadPixels(
                    new Rect(0.0f, 0.0f, resolution.width, resolution.height),
                    0,
                    0,
                    false);
                readback.Apply(false, false);
                Color center = readback.GetPixel(resolution.width / 2, resolution.height / 2);
                byte[] exr = readback.EncodeToEXR(Texture2D.EXRFlags.OutputAsFloat | Texture2D.EXRFlags.CompressZIP);
                File.WriteAllBytes(outputPath, exr);
                return center;
            }
            finally
            {
                camera.targetTexture = null;
                RenderTexture.active = previousActive;
                UnityEngine.Object.DestroyImmediate(readback);
                UnityEngine.Object.DestroyImmediate(target);
            }
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

        private static List<CaptureCondition> BuildMatrix(
            IReadOnlyList<Resolution> resolutions,
            IReadOnlyList<float> distances,
            IReadOnlyList<float> additionalAngles)
        {
            var conditions = new List<CaptureCondition>();
            foreach (Resolution resolution in resolutions)
            {
                conditions.AddRange(distances.Select(distance => new CaptureCondition(resolution, distance, 0.0f)));
                conditions.AddRange(additionalAngles.Select(angle => new CaptureCondition(resolution, 10.0f, angle)));
            }

            return conditions;
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
            return float.IsNaN(value) ? string.Empty : value.ToString("R", CultureInfo.InvariantCulture);
        }

        private static void WriteJson(string path, object value)
        {
            File.WriteAllText(path, JsonUtility.ToJson(value, prettyPrint: true) + Environment.NewLine);
        }

        private readonly struct Resolution
        {
            public Resolution(int width, int height)
            {
                this.width = width;
                this.height = height;
            }

            public readonly int width;
            public readonly int height;
        }

        private readonly struct CaptureCondition
        {
            public CaptureCondition(Resolution resolution, float distanceMetres, float angleDegrees)
            {
                this.resolution = resolution;
                this.distanceMetres = distanceMetres;
                this.angleDegrees = angleDegrees;
            }

            public readonly Resolution resolution;
            public readonly float distanceMetres;
            public readonly float angleDegrees;
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
            public float distanceMetres;
            public float angleDegrees;
            public string[] buffers;
        }

        [Serializable]
        private sealed class CaptureRunManifest
        {
            public int schemaVersion;
            public string runId;
            public string matrix;
            public string scene;
            public string createdUtc;
            public string unityVersion;
            public string graphicsDevice;
            public int conditionCount;
            public CaptureManifestEntry[] conditions;
        }

        [Serializable]
        private sealed class CaptureManifestEntry
        {
            public string id;
            public string metadata;
            public string metrics;
        }
    }
}
