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

The corrected 600-frame temporal run, `Captures/V2/normal_roughness_temporal_720p_20260917_124518_484Z`, recorded 599 valid transitions against a per-frame 4x reference. Gain 64 improved all 600 frame residuals and all 599 motion-compensated transitions. Aggregate residual RMSE fell by 73.26%, motion-compensated tRMSE by 72.91%, 2–30 Hz residual-envelope power by 98.56%, and total AC envelope power by 98.21%. The separate 8x-to-16x snapshot reference RMSE remains 0.04920, so singular glossy absolute errors remain reference-limited even though the complete temporal direction is consistent. The next V2 task is a LEAN-like second-moment normal-distribution material.

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
