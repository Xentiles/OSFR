using System;
using System.IO;
using OSFR.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using Debug = UnityEngine.Debug;

namespace OSFR.Editor
{
    /// <summary>
    /// Captures an unjittered depth-boundary source sequence, then enters Play
    /// Mode to evaluate native URP TAA over real engine frames.
    /// </summary>
    public static class V1TaaDisocclusionRunner
    {
        private const int MaximumTrackedAge = 8;

        [MenuItem("OSFR/Capture/Run V1 TAA Disocclusion Smoke")]
        public static void RunSmokeFromMenu()
        {
            StartRun(primary: false, exitWhenComplete: false);
        }

        [MenuItem("OSFR/Capture/Run V1 720p TAA Disocclusion")]
        public static void RunPrimaryFromMenu()
        {
            StartRun(primary: true, exitWhenComplete: false);
        }

        public static void RunSmokeFromCommandLine()
        {
            StartRun(primary: false, exitWhenComplete: true);
        }

        public static void RunPrimaryFromCommandLine()
        {
            StartRun(primary: true, exitWhenComplete: true);
        }

        private static void StartRun(bool primary, bool exitWhenComplete)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Fail("Exit Play Mode before starting an OSFR TAA disocclusion run.", exitWhenComplete);
                return;
            }

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Fail("OSFR TAA disocclusion metrics require a real graphics device.", exitWhenComplete);
                return;
            }

            string sourceRun = V1TemporalMetricRunner.RunDisocclusionSource(primary, logSuccess: true);
            if (sourceRun == null)
            {
                Fail("The depth-boundary source sequence could not be captured.", exitWhenComplete);
                return;
            }

            if (!OpenValidationScene())
            {
                Fail("The V1 validation scene could not be opened.", exitWhenComplete);
                return;
            }

            int width = primary ? 1280 : 320;
            int height = primary ? 720 : 180;
            int startFrame = primary ? 236 : 288;
            int frameCount = primary ? 128 : 24;
            int persistedFrameStride = primary ? 8 : 4;
            string prefix = primary ? "taa_disocclusion_720p" : "taa_disocclusion_smoke";
            string runId = $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            Directory.CreateDirectory(outputRoot);

            var controllerObject = new GameObject("OSFR V1 TAA Disocclusion Controller");
            V1TaaDisocclusionCaptureController controller =
                controllerObject.AddComponent<V1TaaDisocclusionCaptureController>();
            controller.Configure(
                sourceRun,
                outputRoot,
                runId,
                width,
                height,
                startFrame,
                frameCount,
                MaximumTrackedAge,
                persistedFrameStride,
                exitWhenComplete);

            Debug.Log($"Starting OSFR TAA disocclusion run at {outputRoot} using {sourceRun}.");
            EditorApplication.isPlaying = true;
        }

        private static void Fail(string message, bool exitWhenComplete)
        {
            Debug.LogError(message);
            if (exitWhenComplete)
            {
                EditorApplication.Exit(1);
            }
        }

        private static bool OpenValidationScene()
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
            return scene.IsValid();
        }

        private static string WorkspaceRoot()
        {
            DirectoryInfo unityProject = Directory.GetParent(Application.dataPath);
            return unityProject?.Parent?.FullName ?? unityProject?.FullName ?? Application.dataPath;
        }
    }
}
