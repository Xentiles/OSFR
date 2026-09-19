using System.Linq;
using OSFR.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace OSFR.Editor
{
    /// <summary>
    /// Builds the directional-normal V2 scene used to compare scalar
    /// normal-to-roughness transfer with a LEAN-like slope covariance NDF.
    /// </summary>
    public static class V2LeanValidationSceneBuilder
    {
        public const string ScenePath = "Assets/OSFR/Validation/Scenes/V2_LeanFiltering.unity";
        public const string PanelRootName = "CASE - LEAN Directional Normal Panels";
        public const string ShaderName = "OSFR/V2 Normal Roughness";
        public const float PatternAngleDegrees = 35.0f;
        public const float InitialLeanVarianceGain = 1.0f;
        private const string MaterialFolder = "Assets/OSFR/Validation/Materials/V2Lean";

        [MenuItem("OSFR/Setup/Create or Rebuild V2 LEAN Filtering Scene")]
        public static void CreateOrRebuildFromMenu()
        {
            if (EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                CreateOrRebuild(logSuccess: true);
            }
        }

        public static void CreateFromCommandLine()
        {
            if (!CreateOrRebuild(logSuccess: true))
            {
                EditorApplication.Exit(1);
            }
        }

        public static bool CreateOrRebuild(bool logSuccess)
        {
            if (!MeasurementFeatureInstaller.Install(logAlreadyInstalled: false))
            {
                return false;
            }

            EnsureFolder("Assets/OSFR/Validation/Materials", "V2Lean");
            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"Could not find the V2 material shader: {ShaderName}");
                return false;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "V2_LeanFiltering";
            CreateCamera();
            CreatePanels(shader);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"Failed to save the V2 LEAN validation scene at {ScenePath}.");
                return false;
            }

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (logSuccess)
            {
                Debug.Log($"Created OSFR V2 LEAN validation scene at {ScenePath}.");
            }

            return true;
        }

        private static void CreateCamera()
        {
            var cameraObject = new GameObject("V2 LEAN Camera (deterministic lateral rail)");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60.0f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200.0f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowHDR = true;

            UniversalAdditionalCameraData cameraData =
                cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            DeterministicCameraRail rail = cameraObject.AddComponent<DeterministicCameraRail>();
            rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
            rail.ApplyFrame(300);
            cameraObject.tag = "MainCamera";
        }

        private static void CreatePanels(Shader shader)
        {
            var root = new GameObject(PanelRootName);
            float[] frequencies = { 16.0f, 64.0f, 256.0f };
            float[] alphas = { 0.03f, 0.1f, 0.3f };
            float[] xPositions = { -4.5f, 0.0f, 4.5f };
            float[] yPositions = { 5.0f, 1.6f, -1.8f };

            for (int row = 0; row < alphas.Length; row++)
            {
                for (int column = 0; column < frequencies.Length; column++)
                {
                    float frequency = frequencies[column];
                    float alpha = alphas[row];
                    Material material = GetOrCreateMaterial(shader, frequency, alpha);
                    CreateQuad(
                        root.transform,
                        $"LEAN Panel - {frequency:0} cycles-m - alpha {alpha:0.00}",
                        new Vector3(xPositions[column], yPositions[row], 45.0f),
                        new Vector2(4.0f, 2.8f),
                        material);
                }
            }
        }

        private static Material GetOrCreateMaterial(Shader shader, float frequency, float alpha)
        {
            string alphaName = Mathf.RoundToInt(alpha * 100.0f).ToString("000");
            string assetName = $"Lean_{frequency:000}_Alpha_{alphaName}";
            string path = $"{MaterialFolder}/{assetName}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.name = assetName;
            material.shader = shader;
            material.SetColor("_BaseColor", new Color(0.025f, 0.025f, 0.025f, 1.0f));
            material.SetColor("_SpecularColor", new Color(1.0f, 0.82f, 0.55f, 1.0f));
            material.SetFloat("_NormalFrequency", frequency);
            material.SetFloat("_NormalAmplitude", 0.9f);
            material.SetFloat("_BaseAlpha", alpha);
            material.SetFloat("_VarianceGain", V2ValidationSceneBuilder.PromotedVarianceGain);
            material.SetFloat("_LeanVarianceGain", InitialLeanVarianceGain);
            material.SetFloat("_LeanVisibilityMode", 0.0f);
            material.SetFloat("_FilterMode", 0.0f);
            material.SetFloat("_FilterEnabled", 0.0f);
            material.SetFloat("_PatternAnisotropy", 1.0f);
            material.SetFloat("_PatternAngleDegrees", PatternAngleDegrees);
            material.SetFloat("_DebugMode", 0.0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void CreateQuad(
            Transform parent,
            string name,
            Vector3 position,
            Vector2 size,
            Material material)
        {
            GameObject quad = GameObject.CreatePrimitive(PrimitiveType.Quad);
            quad.name = name;
            quad.transform.SetParent(parent);
            quad.transform.SetPositionAndRotation(position, Quaternion.identity);
            quad.transform.localScale = new Vector3(size.x, size.y, 1.0f);
            quad.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }

        private static void AddSceneToBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes.Any(entry => entry.path == ScenePath))
            {
                return;
            }

            EditorBuildSettings.scenes = scenes
                .Concat(new[] { new EditorBuildSettingsScene(ScenePath, true) })
                .ToArray();
        }
    }
}
