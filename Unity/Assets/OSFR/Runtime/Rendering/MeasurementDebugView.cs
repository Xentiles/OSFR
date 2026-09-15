namespace OSFR.Rendering
{
    /// <summary>
    /// Full-screen diagnostics produced by the OSFR measurement pass.
    /// </summary>
    public enum MeasurementDebugView
    {
        RawDeviceDepth = 0,
        LinearEyeDepth = 1,
        WorldPosition = 2,
        MajorFootprintAxis = 3,
        MinorFootprintAxis = 4,
        FootprintArea = 5,
        Anisotropy = 6,
        NeighborValidity = 7,

        /// <summary>
        /// Unmapped floating-point data for captures and automated validation:
        /// R = major axis, G = minor axis, B = footprint area, A = neighbor validity.
        /// </summary>
        FootprintData = 8,

        /// <summary>
        /// Unmapped world position in RGB for floating-point capture.
        /// </summary>
        WorldPositionData = 9,

        /// <summary>
        /// Metric linear eye depth replicated in RGB for floating-point capture.
        /// </summary>
        LinearEyeDepthData = 10,

        /// <summary>
        /// Displays one level from the linear-HDR Gaussian pyramid.
        /// </summary>
        PyramidMip = 11,

        /// <summary>
        /// Heat-map visualization of squared compressed-luminance energy in L0 - upsample(L1).
        /// </summary>
        FineBandEnergy = 12,

        /// <summary>
        /// Unmapped squared compressed-luminance fine-band energy replicated in RGB.
        /// </summary>
        FineBandEnergyData = 13,

        /// <summary>
        /// Heat-map visualization of the fraction of local contrast energy in the finest bands.
        /// </summary>
        FineFrequencyRatio = 14,

        /// <summary>
        /// Unmapped fine-frequency ratio replicated in RGB for floating-point capture.
        /// </summary>
        FineFrequencyRatioData = 15,

        /// <summary>
        /// Heat-map visualization of fine-frequency ratio weighted by footprint and edge confidence.
        /// </summary>
        AliasRisk = 16,

        /// <summary>
        /// Raw risk payload: R = risk, G = fine ratio, B = footprint weight, A = edge confidence.
        /// </summary>
        AliasRiskData = 17,

        /// <summary>
        /// Linear-HDR 5x5 joint bilateral reconstruction blended by alias risk.
        /// </summary>
        BilateralFilteredColor = 18,

        /// <summary>
        /// Rejection caused by relative eye-depth disagreement in the bilateral kernel.
        /// </summary>
        DepthRejection = 19,

        /// <summary>
        /// Rejection caused by world-normal disagreement in the bilateral kernel.
        /// </summary>
        NormalRejection = 20,

        /// <summary>
        /// Joint bilateral support normalized by the spatial-only kernel sum.
        /// </summary>
        BilateralWeightSum = 21
    }

    internal static class MeasurementDebugViewExtensions
    {
        public static bool IsFrequencyPyramidView(this MeasurementDebugView view)
        {
            return view >= MeasurementDebugView.PyramidMip;
        }

        public static bool RequiresDepth(this MeasurementDebugView view)
        {
            return view >= MeasurementDebugView.AliasRisk;
        }

        public static bool RequiresNormals(this MeasurementDebugView view)
        {
            return view >= MeasurementDebugView.BilateralFilteredColor;
        }
    }
}
