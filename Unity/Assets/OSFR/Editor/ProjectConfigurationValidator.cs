using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using OSFR.Rendering;

namespace OSFR.Editor
{
    public static class ProjectConfigurationValidator
    {
        [MenuItem("OSFR/Validate Project Configuration")]
        public static void ValidateFromMenu()
        {
            bool isValid = Validate(logSuccess: true);

            if (!isValid)
            {
                Debug.LogError("OSFR project configuration validation failed. Review the preceding errors.");
            }
        }

        public static bool Validate(bool logSuccess)
        {
            bool isValid = true;

            if (PlayerSettings.colorSpace != ColorSpace.Linear)
            {
                Debug.LogError("OSFR requires Linear color space.");
                isValid = false;
            }

            RenderPipelineAsset renderPipeline = GraphicsSettings.defaultRenderPipeline;
            if (renderPipeline == null)
            {
                Debug.LogError("OSFR requires a render pipeline asset.");
                isValid = false;
            }
            else if (!(renderPipeline is UniversalRenderPipelineAsset))
            {
                Debug.LogError($"OSFR requires URP, but the default pipeline is {renderPipeline.GetType().Name}.");
                isValid = false;
            }

            ScriptableRendererData rendererData = AssetDatabase.LoadAssetAtPath<ScriptableRendererData>(
                "Assets/Settings/PC_Renderer.asset");
            if (rendererData == null || !rendererData.TryGetRendererFeature(out MeasurementRendererFeature _))
            {
                Debug.LogError("OSFR requires the V0 Measurement Renderer Feature on PC_Renderer.");
                isValid = false;
            }

            if (isValid && logSuccess)
            {
                Debug.Log("OSFR project configuration is valid: Linear color space, URP, and the V0 measurement feature are active.");
            }

            return isValid;
        }
    }
}
