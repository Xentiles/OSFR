# Output-Aware Spatial Frequency Rendering (OSFR)

OSFR is a rendering research project investigating how projected sampling footprints and output resolution can constrain the spatial-frequency content delivered to the rasterizer and display.

The Unity project is the V0/V1 research platform. It is intentionally kept separate from the source documents and future analysis tooling in the parent workspace.

## Pinned environment

- Unity: `6000.3.24f1`
- Universal Render Pipeline: `17.3.0`
- Unity Pipeline: `0.7.0-exp.1`
- Color space: Linear
- Render Graph compatibility mode: Disabled (Render Graph is active)

## Current milestone: V0 measurement foundation

V0 establishes trustworthy measurements before adding any image filtering:

1. Validate the analytical world-space pixel footprint.
2. Reconstruct world position and linear eye depth in URP.
3. Estimate anisotropic footprints from neighboring reconstructed positions.
4. Visualize footprint axes, area, and rejected discontinuities.
5. Drive deterministic camera rails directly from frame index.

The V0 exit criterion is agreement between the measured footprint and the analytical depth/FOV/output-resolution equation on planar surfaces.

## Layout

```text
Assets/OSFR/
  Runtime/          Runtime-independent measurement code
  Editor/           Editor tooling and project setup
  Tests/EditMode/   Fast deterministic unit tests
  Tests/PlayMode/   Runtime and scene integration tests
```

Rendering features, shaders, experiment scenes, capture code, and metrics will be added as their V0 work begins. Generated captures must remain outside `Assets` so Unity does not import them.

## Command-line checks

Run these commands from this directory:

```powershell
unity -V
unity status
unity test . --mode EditMode --output TestResults/editmode.xml
```

If a new terminal cannot find `unity`, the installed binary is currently located at:

```text
C:\Users\imod2\AppData\Local\Unity\bin\unity.exe
```
