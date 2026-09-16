using System;
using System.IO;
using System.Linq;
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
    /// Starts the native-AA baseline controller in Play Mode so TAA receives one
    /// normal history/jitter update per engine frame.
    /// </summary>
    public static class V1NativeAaBaselineRunner
    {
        [MenuItem("OSFR/Capture/Run V1 Native AA Baseline Smoke")]
        public static void RunSmokeFromMenu()
        {
            StartRun(startFrame: 288, frameCount: 24, persistedFrameStride: 8, exitWhenComplete: false);
        }

        [MenuItem("OSFR/Capture/Run V1 720p Native AA Baselines")]
        public static void RunPrimaryFromMenu()
        {
            StartRun(startFrame: 0, frameCount: 600, persistedFrameStride: 1, exitWhenComplete: false);
        }

        public static void RunSmokeFromCommandLine()
        {
            StartRun(startFrame: 288, frameCount: 24, persistedFrameStride: 8, exitWhenComplete: true);
        }

        public static void RunPrimaryFromCommandLine()
        {
            StartRun(startFrame: 0, frameCount: 600, persistedFrameStride: 1, exitWhenComplete: true);
        }

        private static void StartRun(
            int startFrame,
            int frameCount,
            int persistedFrameStride,
            bool exitWhenComplete)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode)
            {
                Debug.LogError("Exit Play Mode before starting an OSFR native AA baseline run.");
                if (exitWhenComplete)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null)
            {
                Debug.LogError("OSFR native AA baselines require a real graphics device.");
                if (exitWhenComplete)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            string sourceRun = FindLatestCompleteTemporalRun();
            if (sourceRun == null)
            {
                Debug.LogError("No complete 600-frame V1 temporal reference run was found.");
                if (exitWhenComplete)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            if (!OpenValidationScene())
            {
                if (exitWhenComplete)
                {
                    EditorApplication.Exit(1);
                }

                return;
            }

            string prefix = frameCount == 600 ? "native_aa_720p" : "native_aa_smoke";
            string runId = $"{prefix}_{DateTime.UtcNow:yyyyMMdd_HHmmss_fff}Z";
            string outputRoot = Path.Combine(WorkspaceRoot(), "Captures", "V1", runId);
            Directory.CreateDirectory(outputRoot);

            var controllerObject = new GameObject("OSFR V1 Native AA Baseline Controller");
            V1NativeAaBaselineCaptureController controller =
                controllerObject.AddComponent<V1NativeAaBaselineCaptureController>();
            controller.Configure(
                sourceRun,
                outputRoot,
                runId,
                startFrame,
                frameCount,
                persistedFrameStride,
                exitWhenComplete);

            Debug.Log($"Starting OSFR native AA baseline run at {outputRoot} using {sourceRun}.");
            EditorApplication.isPlaying = true;
        }

        private static string FindLatestCompleteTemporalRun()
        {
            string root = Path.Combine(WorkspaceRoot(), "Captures", "V1");
            if (!Directory.Exists(root))
            {
                return null;
            }

            return Directory.GetDirectories(root, "temporal_720p_*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(Directory.GetLastWriteTimeUtc)
                .FirstOrDefault(folder =>
                    File.Exists(Path.Combine(folder, "manifest.json"))
                    && File.Exists(Path.Combine(folder, "checkerboard_lateral", "temporal_summary.csv"))
                    && Directory.GetFiles(
                        Path.Combine(folder, "checkerboard_lateral"),
                        "reference_*.exr",
                        SearchOption.TopDirectoryOnly).Length == 600);
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
            if (!scene.IsValid())
            {
                Debug.LogError($"Could not open {V1ValidationSceneBuilder.ScenePath}.");
                return false;
            }

            return true;
        }

        private static string WorkspaceRoot()
        {
            DirectoryInfo unityProject = Directory.GetParent(Application.dataPath);
            return unityProject?.Parent?.FullName ?? unityProject?.FullName ?? Application.dataPath;
        }
    }
}
