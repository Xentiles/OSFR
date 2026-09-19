# Output-Aware Spatial Frequency Rendering (OSFR)

OSFR is a rendering research project investigating how projected sampling footprints and output resolution can constrain the spatial-frequency content delivered to the rasterizer and display.

The Unity project is the V0/V1 research platform. It is intentionally kept separate from the source documents and future analysis tooling in the parent workspace.

## Pinned environment

- Unity: `6000.3.24f1`
- Universal Render Pipeline: `17.3.0`
- Unity Pipeline: `0.7.0-exp.1`
- Color space: Linear
- Render Graph compatibility mode: Disabled (Render Graph is active)

## Current milestone: V2 upstream filtering

V0 establishes trustworthy measurements before adding any image filtering:

1. Validate the analytical world-space pixel footprint.
2. Reconstruct world position and linear eye depth in URP.
3. Estimate anisotropic footprints from neighboring reconstructed positions.
4. Visualize footprint axes, area, and rejected discontinuities.
5. Drive deterministic camera rails directly from frame index.

The V0 exit criterion—agreement between measured and analytical planar footprint—is complete. V1 now includes the linear-HDR pyramid, frequency-risk signal, promoted bilateral reconstruction preset, spatial reference metrics, deterministic temporal evaluation, native SMAA/TAA/MSAA baselines, and explicit TAA disocclusion measurement. Bilateral satisfies the report's temporal/reference-error criterion relative to Raw and SMAA on the checkerboard case. The depth-boundary control confirms that native TAA's temporal stability comes with a short, severe reveal penalty. V1 is complete, and V2 has started with upstream normal-distribution-to-roughness filtering before BRDF evaluation.

## Layout

```text
Assets/OSFR/
  Runtime/          Runtime-independent measurement code
  Editor/           Editor tooling and project setup
  Tests/EditMode/   Fast deterministic unit tests
  Tests/PlayMode/   Runtime and scene integration tests
```

The V0 measurement renderer feature and shader now implement depth sampling, linear eye depth, world-position reconstruction, and anisotropic output-pixel footprint diagnostics. Generated captures must remain outside `Assets` so Unity does not import them.

The deterministic validation scene is `Assets/OSFR/Validation/Scenes/V0_PlanarValidation.unity`. Its camera transform is evaluated directly from a 600-frame index, and the `Footprint Data` view supports floating-point GPU readback against the analytical reference.

The V0 capture runner writes versioned datasets to the workspace-level `Captures/V0` directory. Use the Unity menu for either a small smoke matrix or the full report-prescribed 720p-to-4K matrix; captures never enter Unity's import pipeline.

The V1 validation scene and capture runner add report-prescribed checkerboard and depth-discontinuity torture cases. V1 datasets are written under `Captures/V1` with raw/filtered linear-HDR color, alias-risk factors, bilateral guidance diagnostics, immutable metadata, and per-condition summary metrics.

The same menu provides a tiled linear-HDR reference smoke run and selected 720p reference captures. These render projection-correct spatial supersamples through 16x, box-average them before any output transform, and record adjacent-factor convergence rather than assuming that one supersampling factor is sufficient.

The 720p spatial-metric run captures raw and bilateral outputs beside 8x and 16x references. It writes explicit HDR-peak PSNR, luminance error, per-band Laplacian error/energy, and reference-convergence tables for the selected V1 evidence frames.

The 720p parameter sweep searches 27 strength, spatial-sigma, and risk-range combinations against the 16x candidate reference. It retains all candidate EXRs and produces condition and Pareto-ranking CSVs with an explicit depth-boundary regression guard.

The temporal runner adds a fast 24-frame smoke dataset and a complete 600-frame 720p checkerboard sequence. It uses static-scene world-position reprojection plus linear-depth disocclusion rejection to measure motion-compensated temporal delta error against a spatially supersampled reference, and records a project-specific 2–30 Hz residual spectrum. Generated sequences remain under workspace-level `Captures/V1` and are ignored by Git.

The first 600-frame result reduced reference residual RMSE by 55.75%, motion-compensated temporal RMSE by 59.51%, and absolute 2–30 Hz residual-envelope power by 46.75% versus raw rendering. Total AC envelope power increased by 28.64%, so the evidence supports high-frequency stabilization specifically, not a blanket reduction in all temporal variation. The next V1 task is to put the same sequence evaluator around the native SMAA, TAA, and MSAA baselines.

The native-AA matrix is now complete. Bilateral beats SMAA by 34.06% in spatial residual and 29.79% in motion-compensated tRMSE. Native TAA produces the best tRMSE, 17.03% below Bilateral, but has 24.74% higher spatial residual and 6.57x the 2–30 Hz residual-envelope power. MSAA 4x remains within 0.4% of Raw on the interior checkerboard, confirming that this case is material-frequency aliasing rather than coverage aliasing.

The explicit 128-frame 720p depth-boundary control is also complete. Recently disoccluded TAA pixels measured 3.59x Raw RMSE overall and 3.12x Raw at the reveal frame. The penalty remained elevated for three frames and recovered below Raw at age four. Aggregate old-history projection was small, but isolated transitions showed strong old-history alignment, so the result is characterized as short, severe disocclusion instability with episodic ghosting. The evidence run is `Captures/V1/taa_disocclusion_720p_20260916_232129_887Z`; generated captures remain ignored by Git.

The V2 normal-resultant/roughness calibration and temporal qualification are complete. A 23-point gain sweep used Raw only as an audit anchor, rejected filtered candidates with more than 2% RMSE regression on any panel, reported the RMSE/MAE Pareto frontier, and selected the smallest gain within 5% of both eligible minima. Gain 64 reduced snapshot luminance RMSE by 89.88%, luminance MAE by 36.86%, and RGB MSE by 98.98% versus Raw; all nine panels improved by at least 59.62%. The calibration run is `Captures/V2/normal_roughness_gain_sweep_720p_20260917_121203_289Z`.

The corrected 600-frame temporal run, `Captures/V2/normal_roughness_temporal_720p_20260917_124518_484Z`, recorded 599 valid transitions against a per-frame 4x reference. Gain 64 improved all 600 frame residuals and all 599 motion-compensated transitions. Aggregate residual RMSE fell by 73.26%, motion-compensated tRMSE by 72.91%, 2–30 Hz residual-envelope power by 98.56%, and total AC envelope power by 98.21%. The separate 8x-to-16x snapshot reference RMSE remains 0.04920, so singular glossy absolute errors remain reference-limited even though the complete temporal direction is consistent.

The first LEAN-like anisotropic comparison is also complete. A directional 35-degree normal-wave scene filters first and second tangent-space slope moments, diagonalizes their 2x2 covariance, and feeds the principal axes and roughnesses to an anisotropic GGX NDF. Against the 16x reference, the unit-gain prototype reduced luminance RMSE by 71.97% versus Raw and 83.83% versus scalar gain 64, while RGB MSE fell by 92.14% and 97.38% respectively. It beat Raw on all nine panels and the scalar control on eight; scalar remained 19.74% better on the 16 cycles/m, alpha 0.3 panel. The evidence run is `Captures/V2/lean_comparison_720p_20260917_213403_567Z`.

The directional temporal qualification, `Captures/V2/lean_temporal_720p_20260918_101233_263Z`, contains 600 frames and 599 valid transitions against a per-frame 4x reference. LEAN-like filtering improved every frame residual and every motion-compensated transition versus both Raw and scalar gain 64. Relative to Raw, aggregate residual RMSE fell by 73.85%, tRMSE by 68.12%, 2–30 Hz residual-envelope power by 95.57%, and total AC power by 94.75%. Relative to scalar, residual RMSE fell by 73.91% and tRMSE by 64.27%. Scalar produced the lowest envelope power, but slightly worsened aggregate residual RMSE versus Raw and beat Raw on only 193 of 600 frames, identifying its extra stability as an overfiltering tradeoff rather than a superior reconstruction.

The full height-correlated anisotropic Smith path is implemented but was not promoted. In the qualified spatial A-B, `Captures/V2/lean_smith_ab_720p_20260918_103728_735Z`, it improved six of nine panels but regressed the rough 16 and 64 cycles/m panels, raising full-frame RMSE by 17.65%, MAE by 12.55%, and RGB MSE by 38.37% versus the geometric-mean control. The complete temporal A-B, `Captures/V2/lean_smith_temporal_ab_720p_20260918_103925_452Z`, raised residual RMSE by 12.36%, tRMSE by 14.28%, 2–30 Hz power by 30.76%, and total AC power by 31.57%; it won only 95 of 600 frames and 91 of 599 transitions. The geometric-mean visibility remains the default while the exact Smith path stays available for the next covariance-strength guard or hybrid experiment.

The first material-frequency branch is now spatially qualified. `V2_RoughnessFiltering` exercises the report's 1–4 mm alternating-roughness torture case and compares raw alpha, a single `sqrt(E[alpha^2])` lobe, and an explicit nine-sample pre-BRDF distribution mixture. In `Captures/V2/roughness_distribution_720p_20260918_112052_432Z`, the mixture won all nine panels and reduced full-frame luminance RMSE by 66.78%, MAE by 76.42%, and RGB MSE by 88.96% versus Raw. The single moment-matched lobe regressed those metrics by 52.15%, 49.75%, and 131.50%. Reference convergence was 0.00602 RMSE between 8x and 16x, well below the best candidate's 0.04310 error.

The full material-frequency temporal qualification, `Captures/V2/roughness_distribution_temporal_720p_20260918_232333_554Z`, contains 600 frames and 599 transitions. Distribution mixture beat Raw on every frame and transition, reducing aggregate residual RMSE by 68.85% and tRMSE by 72.03%. It nevertheless raised absolute 2–30 Hz residual-envelope power by 260.91% and total AC power by 703.85%. The moment-collapse control reduced power by roughly 82% but worsened RMSE by 67.77%, worsened tRMSE by 75.81%, and never beat Raw; it is overfiltered. The nine-tap mixture remains a correctness oracle rather than a production filter. Next is a phase-stable continuous footprint-coverage integral feeding a weighted two-lobe BRDF mixture, targeting the discrete sampler's temporal-power failure with only two BRDF evaluations.

## Command-line checks

Run these commands from this directory:

```powershell
unity -V
unity status
unity test . --mode EditMode --output TestResults/editmode.xml
unity run . -- -executeMethod OSFR.Editor.V0CaptureMatrixRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V1TemporalMetricRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2NormalRoughnessRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2NormalRoughnessSweepRunner.RunFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2NormalRoughnessTemporalRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2LeanComparisonRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2LeanTemporalRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2RoughnessComparisonRunner.RunSmokeFromCommandLine
unity run . -- -executeMethod OSFR.Editor.V2RoughnessTemporalRunner.RunSmokeFromCommandLine
```

The native-AA runner must remain in Play Mode across real engine frames, so the CLI's `unity run` wrapper cannot launch it because that wrapper quits after the setup method returns. Invoke the pinned editor directly without `-quit`; the controller exits when capture finishes:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.24f1\Editor\Unity.exe' `
  -batchmode -projectPath . `
  -executeMethod OSFR.Editor.V1NativeAaBaselineRunner.RunSmokeFromCommandLine `
  -logFile TestResults/native-aa-smoke.log
```

The TAA depth-boundary control has the same real-frame requirement:

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.3.24f1\Editor\Unity.exe' `
  -batchmode -projectPath . `
  -executeMethod OSFR.Editor.V1TaaDisocclusionRunner.RunSmokeFromCommandLine `
  -logFile TestResults/taa-disocclusion-smoke.log
```

Do not pass `-nographics` to capture commands: scientific EXR output requires a real graphics device, and the runner rejects Unity's Null Device.

If a new terminal cannot find `unity`, the installed binary is currently located at:

```text
C:\Users\imod2\AppData\Local\Unity\bin\unity.exe
```
