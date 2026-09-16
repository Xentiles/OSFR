using System;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.RenderGraphModule.Util;
using UnityEngine.Rendering.Universal;

namespace OSFR.Rendering
{
    /// <summary>
    /// URP Render Graph feature that visualizes depth reconstruction and the
    /// world-space footprint represented by an output pixel.
    /// </summary>
    public sealed class MeasurementRendererFeature : ScriptableRendererFeature
    {
        private const string ShaderName = "Hidden/OSFR/MeasurementDebug";
        private const string FrequencyShaderName = "Hidden/OSFR/FrequencyPyramid";

        [Serializable]
        public sealed class Settings
        {
            [Tooltip("Diagnostic written to the camera color target before post processing.")]
            public MeasurementDebugView debugView = MeasurementDebugView.LinearEyeDepth;

            [Min(0.01f), Tooltip("Eye depth, in world units, shown as white in the linear-depth view.")]
            public float linearDepthRange = 100.0f;

            [Min(0.000001f), Tooltip("Small end of the logarithmic footprint color scale.")]
            public float minimumFootprintWorld = 0.001f;

            [Min(0.000001f), Tooltip("Large end of the logarithmic footprint color scale.")]
            public float maximumFootprintWorld = 1.0f;

            [Range(0.0001f, 1.0f), Tooltip("Maximum relative eye-depth difference accepted for a neighboring sample.")]
            public float relativeDepthThreshold = 0.02f;

            [Min(0.0001f), Tooltip("World-position visualization repeats after this many world units.")]
            public float worldPositionPeriod = 10.0f;

            [Tooltip("Optional display resolution. Zero uses the camera output resolution.")]
            public Vector2Int outputResolutionOverride = Vector2Int.zero;

            [Tooltip("Also render diagnostics in the Scene view.")]
            public bool showInSceneView = true;

            [Range(2, 6), Tooltip("Number of linear-HDR Gaussian pyramid levels, including full-resolution L0.")]
            public int pyramidLevels = 5;

            [Range(0, 5), Tooltip("Pyramid level displayed by the Pyramid Mip diagnostic.")]
            public int displayedPyramidLevel = 1;

            [Min(0.0001f), Tooltip("Compression strength used only by the HDR luminance-energy detector: log(1 + kY).")]
            public float logLuminanceK = 1.0f;

            [Min(0.0001f), Tooltip("Display gain for the fine-band energy heat map. Raw data is unaffected.")]
            public float fineEnergyVisualizationScale = 32.0f;

            [Range(1, 5), Tooltip("Number of finest Laplacian bands included in the fine-frequency ratio numerator.")]
            public int fineBandCount = 2;

            [Min(0.00000001f), Tooltip("Denominator floor used by the normalized fine-frequency ratio.")]
            public float frequencyRatioEpsilon = 0.000001f;

            [Min(0.0f), Tooltip("World units per output pixel where footprint-based alias risk starts activating.")]
            public float riskFootprintStartWorld = 0.002f;

            [Min(0.000001f), Tooltip("World units per output pixel where footprint-based alias risk reaches full strength.")]
            public float riskFootprintEndWorld = 0.02f;

            [Range(0.0f, 1.0f), Tooltip("Maximum blend from unfiltered color to bilateral reconstruction.")]
            public float bilateralStrength = 0.75f;

            [Range(0.5f, 4.0f), Tooltip("Spatial Gaussian sigma in render pixels for the fixed 5x5 kernel.")]
            public float bilateralSpatialSigmaPixels = 1.75f;

            [Range(0.0001f, 1.0f), Tooltip("Sigma for relative linear-eye-depth differences.")]
            public float bilateralRelativeDepthSigma = 0.02f;

            [Range(0.0001f, 1.0f), Tooltip("Sigma for one minus the world-normal dot product.")]
            public float bilateralNormalSigma = 0.1f;
        }

        [SerializeField]
        private Settings m_Settings = new Settings();

        [SerializeField, HideInInspector]
        private Shader m_Shader;

        [SerializeField, HideInInspector]
        private Shader m_FrequencyShader;

        private Material m_Material;
        private Material m_FrequencyMaterial;
        private MeasurementPass m_Pass;

        public Settings FeatureSettings => m_Settings;

        public override void Create()
        {
            if (m_Shader == null)
            {
                m_Shader = Shader.Find(ShaderName);
            }

            if (m_FrequencyShader == null)
            {
                m_FrequencyShader = Shader.Find(FrequencyShaderName);
            }

            CoreUtils.Destroy(m_Material);
            CoreUtils.Destroy(m_FrequencyMaterial);
            m_Material = m_Shader == null ? null : CoreUtils.CreateEngineMaterial(m_Shader);
            m_FrequencyMaterial = m_FrequencyShader == null ? null : CoreUtils.CreateEngineMaterial(m_FrequencyShader);
            m_Pass = new MeasurementPass(m_Settings, m_Material, m_FrequencyMaterial)
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing
            };
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            CameraType cameraType = renderingData.cameraData.cameraType;
            bool supportedCamera = cameraType == CameraType.Game
                || (m_Settings.showInSceneView && cameraType == CameraType.SceneView);

            bool frequencyView = m_Settings.debugView.IsFrequencyPyramidView();
            bool hasMaterial = frequencyView ? m_FrequencyMaterial != null : m_Material != null;

            if (supportedCamera && hasMaterial)
            {
                m_Pass.Setup(
                    frequencyView,
                    m_Settings.debugView.RequiresDepth(),
                    m_Settings.debugView.RequiresNormals(),
                    m_Settings.debugView.RequiresMotion());
                renderer.EnqueuePass(m_Pass);
            }
        }

        protected override void Dispose(bool disposing)
        {
            CoreUtils.Destroy(m_Material);
            CoreUtils.Destroy(m_FrequencyMaterial);
            m_Material = null;
            m_FrequencyMaterial = null;
        }

        private sealed class MeasurementPass : ScriptableRenderPass
        {
            private const string PassName = "OSFR V0 Measurement";
            private const string FrequencyPassName = "OSFR V1 Frequency Pyramid";
            private static readonly MaterialPropertyBlock Properties = new MaterialPropertyBlock();
            private static readonly MaterialPropertyBlock FrequencyProperties = new MaterialPropertyBlock();

            private static readonly int DebugModeId = Shader.PropertyToID("_OSFRDebugMode");
            private static readonly int BlitScaleBiasId = Shader.PropertyToID("_BlitScaleBias");
            private static readonly int RenderSizeId = Shader.PropertyToID("_OSFRRenderSize");
            private static readonly int OutputSizeId = Shader.PropertyToID("_OSFROutputSize");
            private static readonly int DepthRangeId = Shader.PropertyToID("_OSFRLinearDepthRange");
            private static readonly int FootprintRangeId = Shader.PropertyToID("_OSFRFootprintRange");
            private static readonly int RelativeDepthThresholdId = Shader.PropertyToID("_OSFRRelativeDepthThreshold");
            private static readonly int WorldPositionPeriodId = Shader.PropertyToID("_OSFRWorldPositionPeriod");
            private static readonly int BlitTextureId = Shader.PropertyToID("_BlitTexture");
            private static readonly int LowTextureId = Shader.PropertyToID("_OSFRLowTexture");
            private static readonly int FrequencyDebugModeId = Shader.PropertyToID("_OSFRFrequencyDebugMode");
            private static readonly int LogLuminanceKId = Shader.PropertyToID("_OSFRLogLuminanceK");
            private static readonly int FineEnergyScaleId = Shader.PropertyToID("_OSFRFineEnergyScale");
            private static readonly int PyramidLevelCountId = Shader.PropertyToID("_OSFRPyramidLevelCount");
            private static readonly int FineBandCountId = Shader.PropertyToID("_OSFRFineBandCount");
            private static readonly int FrequencyRatioEpsilonId = Shader.PropertyToID("_OSFRFrequencyRatioEpsilon");
            private static readonly int RiskFootprintRangeId = Shader.PropertyToID("_OSFRRiskFootprintRange");
            private static readonly int BilateralParametersId = Shader.PropertyToID("_OSFRBilateralParameters");
            private static readonly int PyramidL0Id = Shader.PropertyToID("_OSFRPyramidL0");
            private static readonly int PyramidL1Id = Shader.PropertyToID("_OSFRPyramidL1");
            private static readonly int PyramidL2Id = Shader.PropertyToID("_OSFRPyramidL2");
            private static readonly int PyramidL3Id = Shader.PropertyToID("_OSFRPyramidL3");
            private static readonly int PyramidL4Id = Shader.PropertyToID("_OSFRPyramidL4");
            private static readonly int PyramidL5Id = Shader.PropertyToID("_OSFRPyramidL5");

            private readonly Settings m_Settings;
            private readonly Material m_Material;
            private readonly Material m_FrequencyMaterial;
            private bool m_UseFrequencyPyramid;
            private bool m_RequiresMotion;

            public MeasurementPass(Settings settings, Material material, Material frequencyMaterial)
            {
                m_Settings = settings;
                m_Material = material;
                m_FrequencyMaterial = frequencyMaterial;
            }

            public void Setup(
                bool useFrequencyPyramid,
                bool requiresDepth,
                bool requiresNormals,
                bool requiresMotion)
            {
                m_UseFrequencyPyramid = useFrequencyPyramid;
                m_RequiresMotion = requiresMotion;
                requiresIntermediateTexture = useFrequencyPyramid;
                ScriptableRenderPassInput input = ScriptableRenderPassInput.None;
                if (!useFrequencyPyramid || requiresDepth)
                {
                    input |= ScriptableRenderPassInput.Depth;
                }

                if (requiresNormals)
                {
                    input |= ScriptableRenderPassInput.Normal;
                }

                if (requiresMotion)
                {
                    input |= ScriptableRenderPassInput.Motion;
                }

                ConfigureInput(input);
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                UniversalResourceData resourceData = frameData.Get<UniversalResourceData>();
                UniversalCameraData cameraData = frameData.Get<UniversalCameraData>();

                if (m_UseFrequencyPyramid)
                {
                    RecordFrequencyPyramid(renderGraph, resourceData, cameraData);
                    return;
                }

                if (!resourceData.cameraDepthTexture.IsValid()
                    || !resourceData.activeColorTexture.IsValid()
                    || (m_RequiresMotion && !resourceData.motionVectorColor.IsValid()))
                {
                    return;
                }

                Vector2Int renderSize = new Vector2Int(
                    Mathf.Max(1, cameraData.scaledWidth),
                    Mathf.Max(1, cameraData.scaledHeight));
                Vector2Int outputSize = ResolveOutputSize(cameraData);

                using (var builder = renderGraph.AddRasterRenderPass<PassData>(
                    PassName,
                    out PassData passData,
                    profilingSampler))
                {
                    passData.material = m_Material;
                    passData.debugMode = (int)m_Settings.debugView;
                    passData.renderSize = SizeVector(renderSize);
                    passData.outputSize = SizeVector(outputSize);
                    passData.linearDepthRange = Mathf.Max(0.01f, m_Settings.linearDepthRange);
                    passData.footprintRange = new Vector4(
                        Mathf.Max(0.000001f, m_Settings.minimumFootprintWorld),
                        Mathf.Max(m_Settings.minimumFootprintWorld + 0.000001f, m_Settings.maximumFootprintWorld),
                        0.0f,
                        0.0f);
                    passData.relativeDepthThreshold = Mathf.Max(0.0001f, m_Settings.relativeDepthThreshold);
                    passData.worldPositionPeriod = Mathf.Max(0.0001f, m_Settings.worldPositionPeriod);

                    builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    if (m_RequiresMotion)
                    {
                        builder.UseTexture(resourceData.motionVectorColor, AccessFlags.Read);
                    }

                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (PassData data, RasterGraphContext context) => ExecutePass(data, context));
                }
            }

            private void RecordFrequencyPyramid(
                RenderGraph renderGraph,
                UniversalResourceData resourceData,
                UniversalCameraData cameraData)
            {
                if (!resourceData.activeColorTexture.IsValid() || resourceData.isActiveTargetBackBuffer)
                {
                    return;
                }

                bool requiresDepth = m_Settings.debugView.RequiresDepth();
                bool requiresNormals = m_Settings.debugView.RequiresNormals();
                if (requiresDepth && !resourceData.cameraDepthTexture.IsValid())
                {
                    return;
                }

                if (requiresNormals && !resourceData.cameraNormalsTexture.IsValid())
                {
                    return;
                }

                int levelCount = Mathf.Clamp(m_Settings.pyramidLevels, 2, 6);
                TextureHandle[] levels = new TextureHandle[levelCount];
                RenderTextureDescriptor descriptor = cameraData.cameraTargetDescriptor;
                descriptor.depthBufferBits = 0;
                descriptor.depthStencilFormat = GraphicsFormat.None;
                descriptor.msaaSamples = 1;

                descriptor.width = Mathf.Max(1, cameraData.scaledWidth);
                descriptor.height = Mathf.Max(1, cameraData.scaledHeight);
                levels[0] = UniversalRenderer.CreateRenderGraphTexture(
                    renderGraph,
                    descriptor,
                    "OSFR Pyramid L0",
                    false,
                    FilterMode.Bilinear,
                    TextureWrapMode.Clamp);

                renderGraph.AddBlitPass(
                    resourceData.activeColorTexture,
                    levels[0],
                    Vector2.one,
                    Vector2.zero,
                    passName: "OSFR Copy Linear HDR L0");

                for (int level = 1; level < levelCount; level++)
                {
                    descriptor.width = Mathf.Max(1, descriptor.width / 2);
                    descriptor.height = Mathf.Max(1, descriptor.height / 2);
                    levels[level] = UniversalRenderer.CreateRenderGraphTexture(
                        renderGraph,
                        descriptor,
                        $"OSFR Pyramid L{level}",
                        false,
                        FilterMode.Bilinear,
                        TextureWrapMode.Clamp);

                    var blitParameters = new RenderGraphUtils.BlitMaterialParameters(
                        levels[level - 1],
                        levels[level],
                        m_FrequencyMaterial,
                        0);
                    renderGraph.AddBlitPass(
                        blitParameters,
                        passName: $"OSFR Gaussian Downsample L{level}");
                }

                int selectedLevel = Mathf.Clamp(m_Settings.displayedPyramidLevel, 0, levelCount - 1);
                TextureHandle diagnosticTexture = m_Settings.debugView == MeasurementDebugView.PyramidMip
                    ? levels[selectedLevel]
                    : levels[1];

                using (var builder = renderGraph.AddRasterRenderPass<FrequencyPassData>(
                    FrequencyPassName,
                    out FrequencyPassData passData,
                    profilingSampler))
                {
                    passData.material = m_FrequencyMaterial;
                    passData.fullResolution = levels[0];
                    passData.diagnosticTexture = diagnosticTexture;
                    passData.level0 = levels[0];
                    passData.level1 = levels[Mathf.Min(1, levelCount - 1)];
                    passData.level2 = levels[Mathf.Min(2, levelCount - 1)];
                    passData.level3 = levels[Mathf.Min(3, levelCount - 1)];
                    passData.level4 = levels[Mathf.Min(4, levelCount - 1)];
                    passData.level5 = levels[Mathf.Min(5, levelCount - 1)];
                    passData.debugMode = (int)m_Settings.debugView;
                    passData.logLuminanceK = Mathf.Max(0.0001f, m_Settings.logLuminanceK);
                    passData.fineEnergyScale = Mathf.Max(0.0001f, m_Settings.fineEnergyVisualizationScale);
                    passData.pyramidLevelCount = levelCount;
                    passData.fineBandCount = Mathf.Clamp(m_Settings.fineBandCount, 1, levelCount - 1);
                    passData.frequencyRatioEpsilon = Mathf.Max(0.00000001f, m_Settings.frequencyRatioEpsilon);
                    Vector2Int renderSize = new Vector2Int(
                        Mathf.Max(1, cameraData.scaledWidth),
                        Mathf.Max(1, cameraData.scaledHeight));
                    passData.renderSize = SizeVector(renderSize);
                    passData.outputSize = SizeVector(ResolveOutputSize(cameraData));
                    passData.relativeDepthThreshold = Mathf.Max(0.0001f, m_Settings.relativeDepthThreshold);
                    passData.riskFootprintRange = new Vector4(
                        Mathf.Max(0.0f, m_Settings.riskFootprintStartWorld),
                        Mathf.Max(
                            m_Settings.riskFootprintStartWorld + 0.000001f,
                            m_Settings.riskFootprintEndWorld),
                        0.0f,
                        0.0f);
                    passData.bilateralParameters = new Vector4(
                        Mathf.Clamp01(m_Settings.bilateralStrength),
                        Mathf.Max(0.5f, m_Settings.bilateralSpatialSigmaPixels),
                        Mathf.Max(0.0001f, m_Settings.bilateralRelativeDepthSigma),
                        Mathf.Max(0.0001f, m_Settings.bilateralNormalSigma));

                    // Every level is declared as an input so Render Graph retains the complete pyramid.
                    for (int level = 0; level < levelCount; level++)
                    {
                        builder.UseTexture(levels[level], AccessFlags.Read);
                    }

                    if (requiresDepth)
                    {
                        builder.UseTexture(resourceData.cameraDepthTexture, AccessFlags.Read);
                    }

                    if (requiresNormals)
                    {
                        builder.UseTexture(resourceData.cameraNormalsTexture, AccessFlags.Read);
                    }

                    builder.SetRenderAttachment(resourceData.activeColorTexture, 0, AccessFlags.Write);
                    builder.SetRenderFunc(static (FrequencyPassData data, RasterGraphContext context) =>
                        ExecuteFrequencyPass(data, context));
                }
            }

            private Vector2Int ResolveOutputSize(UniversalCameraData cameraData)
            {
                if (m_Settings.outputResolutionOverride.x > 0 && m_Settings.outputResolutionOverride.y > 0)
                {
                    return m_Settings.outputResolutionOverride;
                }

                Camera camera = cameraData.camera;
                return new Vector2Int(Mathf.Max(1, camera.pixelWidth), Mathf.Max(1, camera.pixelHeight));
            }

            private static Vector4 SizeVector(Vector2Int size)
            {
                return new Vector4(size.x, size.y, 1.0f / size.x, 1.0f / size.y);
            }

            private static void ExecutePass(PassData data, RasterGraphContext context)
            {
                Properties.Clear();
                Properties.SetInt(DebugModeId, data.debugMode);
                Properties.SetVector(RenderSizeId, data.renderSize);
                Properties.SetVector(OutputSizeId, data.outputSize);
                Properties.SetFloat(DepthRangeId, data.linearDepthRange);
                Properties.SetVector(FootprintRangeId, data.footprintRange);
                Properties.SetFloat(RelativeDepthThresholdId, data.relativeDepthThreshold);
                Properties.SetFloat(WorldPositionPeriodId, data.worldPositionPeriod);
                Properties.SetVector(BlitScaleBiasId, new Vector4(1.0f, 1.0f, 0.0f, 0.0f));

                context.cmd.DrawProcedural(
                    Matrix4x4.identity,
                    data.material,
                    0,
                    MeshTopology.Triangles,
                    3,
                    1,
                    Properties);
            }

            private static void ExecuteFrequencyPass(FrequencyPassData data, RasterGraphContext context)
            {
                FrequencyProperties.Clear();
                FrequencyProperties.SetTexture(BlitTextureId, (RTHandle)data.fullResolution);
                FrequencyProperties.SetTexture(LowTextureId, (RTHandle)data.diagnosticTexture);
                FrequencyProperties.SetTexture(PyramidL0Id, (RTHandle)data.level0);
                FrequencyProperties.SetTexture(PyramidL1Id, (RTHandle)data.level1);
                FrequencyProperties.SetTexture(PyramidL2Id, (RTHandle)data.level2);
                FrequencyProperties.SetTexture(PyramidL3Id, (RTHandle)data.level3);
                FrequencyProperties.SetTexture(PyramidL4Id, (RTHandle)data.level4);
                FrequencyProperties.SetTexture(PyramidL5Id, (RTHandle)data.level5);
                FrequencyProperties.SetInt(FrequencyDebugModeId, data.debugMode);
                FrequencyProperties.SetFloat(LogLuminanceKId, data.logLuminanceK);
                FrequencyProperties.SetFloat(FineEnergyScaleId, data.fineEnergyScale);
                FrequencyProperties.SetInt(PyramidLevelCountId, data.pyramidLevelCount);
                FrequencyProperties.SetInt(FineBandCountId, data.fineBandCount);
                FrequencyProperties.SetFloat(FrequencyRatioEpsilonId, data.frequencyRatioEpsilon);
                FrequencyProperties.SetVector(RenderSizeId, data.renderSize);
                FrequencyProperties.SetVector(OutputSizeId, data.outputSize);
                FrequencyProperties.SetFloat(RelativeDepthThresholdId, data.relativeDepthThreshold);
                FrequencyProperties.SetVector(RiskFootprintRangeId, data.riskFootprintRange);
                FrequencyProperties.SetVector(BilateralParametersId, data.bilateralParameters);
                FrequencyProperties.SetVector(BlitScaleBiasId, new Vector4(1.0f, 1.0f, 0.0f, 0.0f));

                context.cmd.DrawProcedural(
                    Matrix4x4.identity,
                    data.material,
                    1,
                    MeshTopology.Triangles,
                    3,
                    1,
                    FrequencyProperties);
            }

            private sealed class PassData
            {
                public Material material;
                public int debugMode;
                public Vector4 renderSize;
                public Vector4 outputSize;
                public float linearDepthRange;
                public Vector4 footprintRange;
                public float relativeDepthThreshold;
                public float worldPositionPeriod;
            }

            private sealed class FrequencyPassData
            {
                public Material material;
                public TextureHandle fullResolution;
                public TextureHandle diagnosticTexture;
                public TextureHandle level0;
                public TextureHandle level1;
                public TextureHandle level2;
                public TextureHandle level3;
                public TextureHandle level4;
                public TextureHandle level5;
                public int debugMode;
                public float logLuminanceK;
                public float fineEnergyScale;
                public int pyramidLevelCount;
                public int fineBandCount;
                public float frequencyRatioEpsilon;
                public Vector4 renderSize;
                public Vector4 outputSize;
                public float relativeDepthThreshold;
                public Vector4 riskFootprintRange;
                public Vector4 bilateralParameters;
            }
        }
    }
}
