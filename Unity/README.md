# Output-Aware Spatial Frequency Rendering (OSFR)

OSFR is a rendering research project investigating how projected sampling footprints and output resolution can constrain the spatial-frequency content delivered to the rasterizer and display.

The Unity project is the V0/V1 research platform. It is intentionally kept separate from the source documents and future analysis tooling in the parent workspace.

## Pinned environment

- Unity: `6000.3.24f1`
- Universal Render Pipeline: `17.3.0`
- Unity Pipeline: `0.7.0-exp.1`
- Color space: Linear
- Render Graph compatibility mode: Disabled (Render Graph is active)

## Current milestone: V1 frequency measurement

V0 establishes trustworthy measurements before adding any image filtering:

1. Validate the analytical world-space pixel footprint.
2. Reconstruct world position and linear eye depth in URP.
3. Estimate anisotropic footprints from neighboring reconstructed positions.
4. Visualize footprint axes, area, and rejected discontinuities.
5. Drive deterministic camera rails directly from frame index.

The V0 exit criterion—agreement between measured and analytical planar footprint—is complete. V1 now adds a linear-HDR Gaussian pyramid and Laplacian fine-band energy diagnostics before any filtering policy is enabled.

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

## Command-line checks

Run these commands from this directory:

```powershell
unity -V
unity status
unity test . --mode EditMode --output TestResults/editmode.xml
unity run . -- -executeMethod OSFR.Editor.V0CaptureMatrixRunner.RunSmokeFromCommandLine
```

Do not pass `-nographics` to capture commands: scientific EXR output requires a real graphics device, and the runner rejects Unity's Null Device.

If a new terminal cannot find `unity`, the installed binary is currently located at:

```text
C:\Users\imod2\AppData\Local\Unity\bin\unity.exe
```
