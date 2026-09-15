# OSFR Unity assets

Everything under this directory is owned by the OSFR project. Template and render-pipeline assets remain outside this directory until deliberately adopted or replaced.

The runtime assembly contains the analytical footprint reference and the OSFR URP Render Graph measurement feature. The feature samples scene depth and exposes raw depth, linear eye depth, reconstructed world position, major/minor footprint axes, footprint area, anisotropy, neighbor validity, Gaussian-pyramid mips, and first-band energy.

Add `MeasurementRendererFeature` to the active Universal Renderer Data asset. The default PC renderer is configured with it in this project. Change **Debug View** on the renderer feature to inspect each measurement. A zero output-resolution override follows the camera output; a non-zero override makes resolution-scaling experiments explicit.

The quantitative HDR modes are intended for EXR capture and automated readback rather than direct viewing. `Linear Eye Depth Data` stores metric eye depth, `World Position Data` stores unmapped world coordinates, and `Footprint Data` stores R = major axis, G = minor axis, B = area in squared world units, A = neighbor validity.

Open `Assets/OSFR/Validation/Scenes/V0_PlanarValidation.unity` for the deterministic planar harness. Scrub **Frame Index** on the camera rail to reproduce an exact pose, or switch among the forward, lateral, and micro-motion paths. The inactive plane groups provide the planned angle and distance sweeps without overlapping the default analytical reference.

Use **OSFR > Capture > Run V0 Smoke Matrix** to verify capture output quickly, or **Run V0 Primary Matrix** for the report's 720p, 1080p, 1440p, and 2160p distance/angle matrix. Every run creates a new immutable folder under the workspace-level `Captures/V0` directory containing a manifest and per-condition metadata, metrics, depth, world-position, and footprint EXRs.

## V1 frequency measurement

Select `Pyramid Mip`, `Fine Band Energy`, `Fine Band Energy Data`, `Fine Frequency Ratio`, or `Fine Frequency Ratio Data` on the installed renderer feature. The V1 branch copies the pre-post-process camera color into a linear-HDR Gaussian pyramid, applies a 3x3 binomial low pass before each 2x downsample, and measures adjacent Laplacian bands as `Ll - upsample(Ll+1)`. Energy is squared compressed luminance. The normalized ratio reports the configurable finest-band sum divided by all measured contrast-band energy; the coarsest DC level is excluded. No image filtering or frequency-dependent shading is applied yet.

`Alias Risk` combines the fine-frequency ratio with a smooth world-space footprint response and the existing depth-neighbor confidence. `Alias Risk Data` exposes R = combined risk, G = fine-frequency ratio, B = footprint weight, and A = boundary confidence. The footprint start/end values are experiment parameters in world units per output pixel; they must be calibrated to the scene's unit scale. This remains a heuristic measurement view, not a claim that post-raster aliasing can be classified exactly.

`Bilateral Filtered Color` is the first active reconstruction mode. It applies a fixed 5x5 joint bilateral kernel to linear-HDR L0 and blends it with the unfiltered signal by the alias-risk estimate. Spatial distance, relative linear eye depth, and world-normal agreement form the kernel weight. `Depth Rejection`, `Normal Rejection`, and `Bilateral Weight Sum` expose the guidance behavior. Material ID and roughness are not yet part of the guidance, so coplanar material boundaries remain an explicit limitation.

The provisional `V1_5x5_Balanced720p_01` default uses strength 0.75, spatial sigma 1.75 pixels, and the 0.002–0.02 world-unit footprint-risk range. It was selected from the recorded 27-preset 720p sweep for a better balance between reference error and retained fine-band energy. Treat it as an evidence-backed starting point, not a universal optimum; additional frames, resolutions, and signal classes still need validation.

Open `Assets/OSFR/Validation/Scenes/V1_ScreenSpaceValidation.unity` for the first report-driven torture cases. The checkerboard wall contains point-sampled 5, 10, 20, and 40 mm cells, while the depth-discontinuity case places a 100 mm diameter pole near the camera against a detailed far wall. The scene uses the deterministic 600-frame lateral rail, fixed 60-degree FOV, no AA, and no post processing.

Use **OSFR > Capture > Run V1 Smoke Matrix** for a small raw-versus-filtered dataset or **Run V1 Primary Snapshot Matrix** for all four report resolutions and five deterministic checkerboard frames. Each immutable condition folder contains linear-HDR raw and bilateral EXRs, alias-risk data, rejection/support diagnostics, metadata, and summary statistics. These snapshot matrices establish spatial behavior; complete 600-frame temporal sequences remain a separate follow-on task.

## V1 ground-truth references

Use **OSFR > Capture > Run V1 Reference Smoke** to validate the reference pipeline, or **Run V1 Selected 720p References** for the checkerboard and depth-boundary evidence frames. The reference renderer divides the full output into 128-pixel tiles, applies a projection-correct sub-frustum to each tile, renders smoke references at 4x and 8x and selected evidence references through 16x linear resolution per axis with no AA or tone mapping, and box-averages every supersample block in linear HDR. Tiling bounds peak render-target memory and avoids the device texture-size limit without changing the effective full-frame projection or texture derivatives.

Each reference condition stores supersampled EXRs plus a convergence CSV containing mean, RMS, maximum, and relative luminance error for each adjacent factor pair. Smoke runs also compare the tiled 8x result with a one-shot 8x render to catch projection, seam, and orientation errors. A result is a candidate ground truth only after convergence to the next sampled factor is reviewed.

## V1 spatial metrics

Use **OSFR > Capture > Run V1 720p Spatial Metrics** to capture the checkerboard and depth-boundary evidence frames as raw, bilateral, 8x reference, and 16x reference linear-HDR EXRs. The run records RGB MSE and PSNR against the 16x candidate reference, using the reference's maximum absolute RGB value as the explicit HDR peak. It also builds identical five-level luminance pyramids for each result and the reference, then reports Laplacian-band error and energy at every level plus the coarsest residual. Reference convergence remains a separate recorded input to interpretation rather than being hidden by the result metric.

Use **OSFR > Capture > Run V1 720p Parameter Sweep** to evaluate 27 combinations of bilateral strength, spatial sigma, and footprint-risk range on both evidence cases. The runner preserves every filtered EXR, writes per-condition metrics, identifies the Pareto frontier between normalized reference error and checkerboard fine-band energy mismatch, and recommends the closest balanced frontier point subject to no more than a 2% RGB-MSE regression on the depth-pole case. The ranking rule is recorded in the run manifest so the selected preset remains auditable rather than becoming an unexplained magic constant.
