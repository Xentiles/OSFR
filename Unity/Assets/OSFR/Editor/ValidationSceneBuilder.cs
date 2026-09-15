using System.Linq;
using OSFR.Validation;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace OSFR.Editor
{
    public static class ValidationSceneBuilder
    {
        public const string ScenePath = "Assets/OSFR/Validation/Scenes/V0_PlanarValidation.unity";
        private const string MaterialPath = "Assets/OSFR/Validation/Materials/NeutralValidation.mat";

        [MenuItem("OSFR/Setup/Create or Open V0 Planar Validation Scene")]
        public static void CreateOrOpenFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            CreateOrOpen(logSuccess: true);
        }

        public static void CreateFromCommandLine()
        {
            if (!CreateOrOpen(logSuccess: true))
            {
                EditorApplication.Exit(1);
            }
        }

        public static bool CreateOrOpen(bool logSuccess)
        {
            if (!MeasurementFeatureInstaller.Install(logAlreadyInstalled: false))
            {
                return false;
            }

            EnsureFolder("Assets/OSFR", "Validation");
            EnsureFolder("Assets/OSFR/Validation", "Materials");
            EnsureFolder("Assets/OSFR/Validation", "Scenes");

            SceneAsset existingScene = AssetDatabase.LoadAssetAtPath<SceneAsset>(ScenePath);
            if (existingScene != null)
            {
                EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
                AddSceneToBuildSettings();
                if (logSuccess)
                {
                    Debug.Log($"Opened existing OSFR V0 planar validation scene at {ScenePath}.", existingScene);
                }

                return true;
            }

            Material neutralMaterial = GetOrCreateNeutralMaterial();
            if (neutralMaterial == null)
            {
                return false;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "V0_PlanarValidation";

            CreateCamera();
            CreateLight();
            CreateValidationPlanes(neutralMaterial);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"Failed to save OSFR validation scene at {ScenePath}.");
                return false;
            }

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();

            if (logSuccess)
            {
                Debug.Log($"Created OSFR V0 planar validation scene at {ScenePath}.");
            }

            return true;
        }

        private static void CreateCamera()
        {
            GameObject cameraObject = new GameObject("V0 Validation Camera (scrub Rail Frame Index)");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60.0f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 250.0f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowHDR = true;

            UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            DeterministicCameraRail rail = cameraObject.AddComponent<DeterministicCameraRail>();
            rail.Configure(DeterministicCameraRail.RailPath.Forward, 600, 60.0f);
            rail.ApplyFrame(0);

            cameraObject.tag = "MainCamera";
        }

        private static void CreateLight()
        {
            GameObject lightObject = new GameObject("Neutral Directional Light");
            Light light = lightObject.AddComponent<Light>();
            light.type = LightType.Directional;
            light.intensity = 1.0f;
            light.color = Color.white;
            lightObject.transform.rotation = Quaternion.Euler(45.0f, -30.0f, 0.0f);
        }

        private static void CreateValidationPlanes(Material material)
        {
            GameObject root = new GameObject("V0 Planar Validation Targets");

            GameObject referenceGroup = new GameObject("ACTIVE - Front-facing analytical reference");
            referenceGroup.transform.SetParent(root.transform);
            CreatePanel(
                referenceGroup.transform,
                material,
                "Reference Plane - world Z 10 - camera frame 0 eye depth 20",
                new Vector3(0.0f, 1.6f, 10.0f),
                0.0f,
                new Vector2(14.0f, 10.0f));

            GameObject angleGroup = new GameObject("DISABLED - Angle sweep 0 30 60 80 degrees");
            angleGroup.transform.SetParent(root.transform);
            float[] angles = { 0.0f, 30.0f, 60.0f, 80.0f };
            float[] xPositions = { -7.5f, -2.5f, 2.5f, 7.5f };
            for (int index = 0; index < angles.Length; index++)
            {
                CreatePanel(
                    angleGroup.transform,
                    material,
                    $"Plane - {angles[index]:0} degrees",
                    new Vector3(xPositions[index], 1.6f, 20.0f),
                    angles[index],
                    new Vector2(4.0f, 6.0f));
            }
            angleGroup.SetActive(false);

            GameObject distanceGroup = new GameObject("DISABLED - Distance sweep 1 2 5 10 20 50 100 metres");
            distanceGroup.transform.SetParent(root.transform);
            float[] distances = { 1.0f, 2.0f, 5.0f, 10.0f, 20.0f, 50.0f, 100.0f };
            for (int index = 0; index < distances.Length; index++)
            {
                float x = index % 2 == 0 ? -5.0f : 5.0f;
                CreatePanel(
                    distanceGroup.transform,
                    material,
                    $"Plane - {distances[index]:0} m from world origin camera",
                    new Vector3(x, 1.6f, distances[index]),
                    0.0f,
                    new Vector2(4.0f, 4.0f));
            }
            distanceGroup.SetActive(false);
        }

        private static void CreatePanel(
            Transform parent,
            Material material,
            string name,
            Vector3 position,
            float yawDegrees,
            Vector2 size)
        {
            GameObject panel = GameObject.CreatePrimitive(PrimitiveType.Quad);
            panel.name = name;
            panel.transform.SetParent(parent);
            panel.transform.SetPositionAndRotation(position, Quaternion.Euler(0.0f, yawDegrees, 0.0f));
            panel.transform.localScale = new Vector3(size.x, size.y, 1.0f);
            panel.GetComponent<MeshRenderer>().sharedMaterial = material;
        }

        private static Material GetOrCreateNeutralMaterial()
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material != null)
            {
                return material;
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                Debug.LogError("Could not locate the URP Lit shader for the V0 validation material.");
                return null;
            }

            material = new Material(shader)
            {
                name = "Neutral Validation",
                color = new Color(0.5f, 0.5f, 0.5f, 1.0f)
            };
            material.SetFloat("_Smoothness", 0.0f);
            material.SetFloat("_Cull", (float)CullMode.Off);
            AssetDatabase.CreateAsset(material, MaterialPath);
            return material;
        }

        private static void AddSceneToBuildSettings()
        {
            EditorBuildSettingsScene[] scenes = EditorBuildSettings.scenes;
            if (scenes.Any(scene => scene.path == ScenePath))
            {
                return;
            }

            EditorBuildSettings.scenes = scenes
                .Concat(new[] { new EditorBuildSettingsScene(ScenePath, true) })
                .ToArray();
        }

        private static void EnsureFolder(string parent, string child)
        {
            string path = $"{parent}/{child}";
            if (!AssetDatabase.IsValidFolder(path))
            {
                AssetDatabase.CreateFolder(parent, child);
            }
        }
    }
}
