using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

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

            if (isValid && logSuccess)
            {
                Debug.Log("OSFR project configuration is valid: Linear color space and URP are active.");
            }

            return isValid;
        }
    }
}
