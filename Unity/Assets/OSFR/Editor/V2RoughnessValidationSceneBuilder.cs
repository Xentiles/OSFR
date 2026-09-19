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
    /// Builds the V2 roughness-frequency scene used to compare raw BRDF
    /// parameters with footprint-driven moment and distribution aggregation.
    /// </summary>
    public static class V2RoughnessValidationSceneBuilder
    {
        public const string ScenePath = "Assets/OSFR/Validation/Scenes/V2_RoughnessFiltering.unity";
        public const string PanelRootName = "CASE - Roughness Frequency Panels";
        public const string ShaderName = "OSFR/V2 Roughness Distribution";
        public const float PatternAngleDegrees = 25.0f;
        private const string MaterialFolder = "Assets/OSFR/Validation/Materials/V2Roughness";

        [MenuItem("OSFR/Setup/Create or Rebuild V2 Roughness Filtering Scene")]
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

            EnsureFolder("Assets/OSFR/Validation/Materials", "V2Roughness");
            Shader shader = Shader.Find(ShaderName);
            if (shader == null)
            {
                Debug.LogError($"Could not find the V2 roughness material shader: {ShaderName}");
                return false;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "V2_RoughnessFiltering";
            CreateCamera();
            CreatePanels(shader);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"Failed to save the V2 roughness validation scene at {ScenePath}.");
                return false;
            }

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            if (logSuccess)
            {
                Debug.Log($"Created OSFR V2 roughness validation scene at {ScenePath}.");
            }

            return true;
        }

        private static void CreateCamera()
        {
            var cameraObject = new GameObject("V2 Roughness Camera (deterministic micro-motion rail)");
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
            rail.Configure(DeterministicCameraRail.RailPath.MicroMotion, 600, 60.0f);
            rail.ApplyFrame(300);
            cameraObject.tag = "MainCamera";
        }

        private static void CreatePanels(Shader shader)
        {
            var root = new GameObject(PanelRootName);
            float[] bandWidthsMillimeters = { 1.0f, 2.0f, 4.0f };
            float[] alphaLow = { 0.03f, 0.1f, 0.3f };
            float[] alphaHigh = { 0.3f, 0.5f, 0.8f };
            float[] xPositions = { -3.0f, 0.0f, 3.0f };
            float[] yPositions = { 3.3f, 1.6f, -0.1f };

            for (int row = 0; row < alphaLow.Length; row++)
            {
                for (int column = 0; column < bandWidthsMillimeters.Length; column++)
                {
                    float bandWidth = bandWidthsMillimeters[column];
                    Material material = GetOrCreateMaterial(
                        shader, bandWidth, alphaLow[row], alphaHigh[row]);
                    CreateQuad(
                        root.transform,
                        $"Roughness Panel - {bandWidth:0} mm - alpha {alphaLow[row]:0.00}-{alphaHigh[row]:0.00}",
                        new Vector3(xPositions[column], yPositions[row], -5.0f),
                        new Vector2(2.5f, 1.4f),
                        material);
                }
            }
        }

        private static Material GetOrCreateMaterial(
            Shader shader,
            float bandWidthMillimeters,
            float alphaLow,
            float alphaHigh)
        {
            string bandName = Mathf.RoundToInt(bandWidthMillimeters).ToString("000");
            string lowName = Mathf.RoundToInt(alphaLow * 100.0f).ToString("000");
            string highName = Mathf.RoundToInt(alphaHigh * 100.0f).ToString("000");
            string assetName = $"Roughness_Band_{bandName}_Alpha_{lowName}_{highName}";
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
            material.SetFloat("_BandWidthMillimeters", bandWidthMillimeters);
            material.SetFloat("_AlphaLow", alphaLow);
            material.SetFloat("_AlphaHigh", alphaHigh);
            material.SetFloat("_FilterMode", 0.0f);
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
