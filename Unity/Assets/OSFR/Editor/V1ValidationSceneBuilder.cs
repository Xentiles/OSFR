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
    /// <summary>
    /// Builds the first intentionally pathological V1 scene from the research plan:
    /// known-frequency checkerboards and a near-pole/far-wall depth discontinuity.
    /// </summary>
    public static class V1ValidationSceneBuilder
    {
        public const string ScenePath = "Assets/OSFR/Validation/Scenes/V1_ScreenSpaceValidation.unity";
        public const string CheckerboardRootName = "CASE - Checkerboard Wall";
        public const string DepthBoundaryRootName = "CASE - Depth Discontinuity Pole";

        private const string TexturePath = "Assets/OSFR/Validation/Textures/ProceduralChecker.asset";
        private const string MaterialFolder = "Assets/OSFR/Validation/Materials/V1";

        [MenuItem("OSFR/Setup/Create or Rebuild V1 Screen-Space Validation Scene")]
        public static void CreateOrRebuildFromMenu()
        {
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
            {
                return;
            }

            CreateOrRebuild(logSuccess: true);
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

            EnsureFolder("Assets/OSFR", "Validation");
            EnsureFolder("Assets/OSFR/Validation", "Materials");
            EnsureFolder("Assets/OSFR/Validation/Materials", "V1");
            EnsureFolder("Assets/OSFR/Validation", "Textures");
            EnsureFolder("Assets/OSFR/Validation", "Scenes");

            Texture2D checker = GetOrCreateCheckerTexture();
            Shader unlitShader = Shader.Find("Universal Render Pipeline/Unlit");
            if (checker == null || unlitShader == null)
            {
                Debug.LogError("Could not create the V1 checker source or locate the URP Unlit shader.");
                return false;
            }

            Scene scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            scene.name = "V1_ScreenSpaceValidation";
            CreateCamera();
            CreateCheckerboardCase(checker, unlitShader);
            CreateDepthBoundaryCase(checker, unlitShader);

            if (!EditorSceneManager.SaveScene(scene, ScenePath))
            {
                Debug.LogError($"Failed to save the V1 validation scene at {ScenePath}.");
                return false;
            }

            AddSceneToBuildSettings();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            if (logSuccess)
            {
                Debug.Log($"Created OSFR V1 validation scene at {ScenePath}.");
            }

            return true;
        }

        private static void CreateCamera()
        {
            GameObject cameraObject = new GameObject("V1 Validation Camera (deterministic lateral rail)");
            Camera camera = cameraObject.AddComponent<Camera>();
            camera.fieldOfView = 60.0f;
            camera.nearClipPlane = 0.1f;
            camera.farClipPlane = 200.0f;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.black;
            camera.allowHDR = true;

            UniversalAdditionalCameraData cameraData = cameraObject.AddComponent<UniversalAdditionalCameraData>();
            cameraData.renderPostProcessing = false;
            cameraData.antialiasing = AntialiasingMode.None;

            DeterministicCameraRail rail = cameraObject.AddComponent<DeterministicCameraRail>();
            rail.Configure(DeterministicCameraRail.RailPath.Lateral, 600, 60.0f);
            rail.ApplyFrame(300);
            cameraObject.tag = "MainCamera";
        }

        private static void CreateCheckerboardCase(Texture2D checker, Shader shader)
        {
            GameObject root = new GameObject(CheckerboardRootName);
            float[] cellSizes = { 0.005f, 0.01f, 0.02f, 0.04f };
            float[] xPositions = { -6.0f, -2.0f, 2.0f, 6.0f };
            const float width = 3.5f;
            const float height = 6.0f;

            for (int index = 0; index < cellSizes.Length; index++)
            {
                float cellSize = cellSizes[index];
                Material material = GetOrCreateCheckerMaterial(
                    checker,
                    shader,
                    $"Checker_{cellSize * 1000.0f:000}mm_{width:0.0}x{height:0.0}",
                    cellSize,
                    width,
                    height,
                    Color.white);
                CreateQuad(
                    root.transform,
                    $"Checker Panel - {cellSize * 1000.0f:0} mm cells",
                    new Vector3(xPositions[index], 1.6f, 45.0f),
                    new Vector2(width, height),
                    material);
            }
        }

        private static void CreateDepthBoundaryCase(Texture2D checker, Shader shader)
        {
            GameObject root = new GameObject(DepthBoundaryRootName);
            Material wallMaterial = GetOrCreateCheckerMaterial(
                checker,
                shader,
                "DepthWall_020mm_14x10",
                0.02f,
                14.0f,
                10.0f,
                new Color(0.8f, 0.8f, 0.8f, 1.0f));
            CreateQuad(
                root.transform,
                "Detailed Wall - 50 m",
                new Vector3(0.0f, 1.6f, 50.0f),
                new Vector2(14.0f, 10.0f),
                wallMaterial);

            Material poleMaterial = GetOrCreateSolidMaterial(
                shader,
                "DepthPole_White",
                new Color(4.0f, 4.0f, 4.0f, 1.0f));
            GameObject pole = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
            pole.name = "Depth Discontinuity Pole - 5 m from lateral rail center";
            pole.transform.SetParent(root.transform);
            pole.transform.SetPositionAndRotation(new Vector3(0.0f, 1.6f, 30.0f), Quaternion.identity);
            pole.transform.localScale = new Vector3(0.05f, 3.0f, 0.05f);
            pole.GetComponent<MeshRenderer>().sharedMaterial = poleMaterial;

            root.SetActive(false);
        }

        private static GameObject CreateQuad(
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
            return quad;
        }

        private static Texture2D GetOrCreateCheckerTexture()
        {
            Texture2D texture = AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (texture == null)
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, true)
                {
                    name = "Procedural Checker Source"
                };
                AssetDatabase.CreateAsset(texture, TexturePath);
            }

            texture.filterMode = FilterMode.Point;
            texture.wrapMode = TextureWrapMode.Repeat;
            texture.SetPixels(new[] { Color.black, Color.white, Color.white, Color.black });
            texture.Apply(false, false);
            EditorUtility.SetDirty(texture);
            return texture;
        }

        private static Material GetOrCreateCheckerMaterial(
            Texture2D checker,
            Shader shader,
            string name,
            float cellSizeWorld,
            float widthWorld,
            float heightWorld,
            Color tint)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetTexture("_BaseMap", checker);
            material.SetColor("_BaseColor", tint);
            material.SetTextureScale(
                "_BaseMap",
                new Vector2(widthWorld / (2.0f * cellSizeWorld), heightWorld / (2.0f * cellSizeWorld)));
            material.SetTextureOffset("_BaseMap", Vector2.zero);
            material.SetFloat("_Cull", (float)CullMode.Off);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material GetOrCreateSolidMaterial(Shader shader, string name, Color color)
        {
            string path = $"{MaterialFolder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }

            material.shader = shader;
            material.SetTexture("_BaseMap", null);
            material.SetColor("_BaseColor", color);
            material.SetFloat("_Cull", (float)CullMode.Off);
            EditorUtility.SetDirty(material);
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
