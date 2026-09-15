using OSFR.Rendering;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace OSFR.Editor
{
    public static class MeasurementFeatureInstaller
    {
        private const string PcRendererPath = "Assets/Settings/PC_Renderer.asset";

        [MenuItem("OSFR/Setup/Install V0 Measurement Feature")]
        public static void InstallFromMenu()
        {
            Install(logAlreadyInstalled: true);
        }

        public static void InstallFromCommandLine()
        {
            if (!Install(logAlreadyInstalled: true))
            {
                EditorApplication.Exit(1);
            }
        }

        public static bool Install(bool logAlreadyInstalled)
        {
            ScriptableRendererData rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(PcRendererPath);
            if (rendererData == null)
            {
                Debug.LogError($"Could not load the OSFR PC renderer at {PcRendererPath}.");
                return false;
            }

            if (rendererData.TryGetRendererFeature(out MeasurementRendererFeature existingFeature))
            {
                existingFeature.Create();
                SynchronizeFeatureMap(rendererData);
                rendererData.SetDirty();
                EditorUtility.SetDirty(existingFeature);
                EditorUtility.SetDirty(rendererData);
                AssetDatabase.SaveAssets();

                if (logAlreadyInstalled)
                {
                    Debug.Log($"OSFR V0 measurement feature is already installed on {rendererData.name}.", existingFeature);
                }

                return true;
            }

            MeasurementRendererFeature feature = ScriptableObject.CreateInstance<MeasurementRendererFeature>();
            feature.name = "OSFR V0 Measurement";
            feature.SetActive(true);
            AssetDatabase.AddObjectToAsset(feature, rendererData);
            rendererData.rendererFeatures.Add(feature);
            feature.Create();
            SynchronizeFeatureMap(rendererData);
            rendererData.SetDirty();
            EditorUtility.SetDirty(feature);
            EditorUtility.SetDirty(rendererData);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Debug.Log($"Installed OSFR V0 measurement feature on {rendererData.name}.", rendererData);
            return true;
        }

        private static void SynchronizeFeatureMap(ScriptableRendererData rendererData)
        {
            SerializedObject serializedRenderer = new SerializedObject(rendererData);
            serializedRenderer.Update();
            SerializedProperty features = serializedRenderer.FindProperty("m_RendererFeatures");
            SerializedProperty featureMap = serializedRenderer.FindProperty("m_RendererFeatureMap");
            featureMap.arraySize = features.arraySize;

            for (int index = 0; index < features.arraySize; index++)
            {
                Object feature = features.GetArrayElementAtIndex(index).objectReferenceValue;
                long localId = 0;
                if (feature != null)
                {
                    AssetDatabase.TryGetGUIDAndLocalFileIdentifier(feature, out _, out localId);
                }

                featureMap.GetArrayElementAtIndex(index).longValue = localId;
            }

            serializedRenderer.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
