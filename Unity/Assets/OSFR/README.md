# OSFR Unity assets

Everything under this directory is owned by the OSFR project. Template and render-pipeline assets remain outside this directory until deliberately adopted or replaced.

The runtime assembly contains the analytical footprint reference and the OSFR URP Render Graph measurement feature. The feature samples scene depth and exposes raw depth, linear eye depth, reconstructed world position, major/minor footprint axes, footprint area, anisotropy, neighbor validity, Gaussian-pyramid mips, and first-band energy.

Add `MeasurementRendererFeature` to the active Universal Renderer Data asset. The default PC renderer is configured with it in this project. Change **Debug View** on the renderer feature to inspect each measurement. A zero output-resolution override follows the camera output; a non-zero override makes resolution-scaling experiments explicit.

The quantitative HDR modes are intended for EXR capture and automated readback rather than direct viewing. `Linear Eye Depth Data` stores metric eye depth, `World Position Data` stores unmapped world coordinates, and `Footprint Data` stores R = major axis, G = minor axis, B = area in squared world units, A = neighbor validity.

`Motion Vectors Data` exposes URP's signed forward motion in screen-UV units for interactive/frame-driven validation. The offline temporal runner instead reprojects the GPU world-position buffer through the previous camera matrix because its multiple manual renders occur inside one editor frame; that deterministic fallback covers static-scene camera motion but deliberately does not claim to represent animated-object motion.

Open `Assets/OSFR/Validation/Scenes/V0_PlanarValidation.unity` for the deterministic planar harness. Scrub **Frame Index** on the camera rail to reproduce an exact pose, or switch among the forward, lateral, and micro-motion paths. The inactive plane groups provide the planned angle and distance sweeps without overlapping the default analytical reference.

Use **OSFR > Capture > Run V0 Smoke Matrix** to verify capture output quickly, or **Run V0 Primary Matrix** for the report's 720p, 1080p, 1440p, and 2160p distance/angle matrix. Every run creates a new immutable folder under the workspace-level `Captures/V0` directory containing a manifest and per-condition metadata, metrics, depth, world-position, and footprint EXRs.

## V1 frequency measurement

Select `Pyramid Mip`, `Fine Band Energy`, `Fine Band Energy Data`, `Fine Frequency Ratio`, or `Fine Frequency Ratio Data` on the installed renderer feature. The V1 branch copies the pre-post-process camera color into a linear-HDR Gaussian pyramid, applies a 3x3 binomial low pass before each 2x downsample, and measures adjacent Laplacian bands as `Ll - upsample(Ll+1)`. Energy is squared compressed luminance. The normalized ratio reports the configurable finest-band sum divided by all measured contrast-band energy; the coarsest DC level is excluded. No image filtering or frequency-dependent shading is applied yet.

`Alias Risk` combines the fine-frequency ratio with a smooth world-space footprint response and the existing depth-neighbor confidence. `Alias Risk Data` exposes R = combined risk, G = fine-frequency ratio, B = footprint weight, and A = boundary confidence. The footprint start/end values are experiment parameters in world units per output pixel; they must be calibrated to the scene's unit scale. This remains a heuristic measurement view, not a claim that post-raster aliasing can be classified exactly.

`Bilateral Filtered Color` is the first active reconstruction mode. It applies a fixed 5x5 joint bilateral kernel to linear-HDR L0 and blends it with the unfiltered signal by the alias-risk estimate. Spatial distance, relative linear eye depth, and world-normal agreement form the kernel weight. `Depth Rejection`, `Normal Rejection`, and `Bilateral Weight Sum` expose the guidance behavior. Material ID and roughness are not yet part of the guidance, so coplanar material boundaries remain an explicit limitation.

The provisional `V1_5x5_Balanced720p_01` default uses strength 0.75, spatial sigma 1.75 pixels, and the 0.002–0.02 world-unit footprint-risk range. It was selected from the recorded 27-preset 720p sweep for a better balance between reference error and retained fine-band energy. Treat it as an evidence-backed starting point, not a universal optimum; additional frames, resolutions, and signal classes still need validation.

Open `Assets/OSFR/Validation/Scenes/V1_ScreenSpaceValidation.unity` for the first report-driven torture cases. The checkerboard wall contains point-sampled 5, 10, 20, and 40 mm cells, while the depth-discontinuity case places a 100 mm diameter pole near the camera against a detailed far wall. The scene uses the deterministic 600-frame lateral rail, fixed 60-degree FOV, no AA, and no post processing.

Use **OSFR > Capture > Run V1 Smoke Matrix** for a small raw-versus-filtered dataset or **Run V1 Primary Snapshot Matrix** for all four report resolutions and five deterministic checkerboard frames. Each immutable condition folder contains linear-HDR raw and bilateral EXRs, alias-risk data, rejection/support diagnostics, metadata, and summary statistics. These snapshot matrices establish spatial behavior; the later temporal and native-AA sections document the completed 600-frame sequence work.

## V1 ground-truth references

Use **OSFR > Capture > Run V1 Reference Smoke** to validate the reference pipeline, or **Run V1 Selected 720p References** for the checkerboard and depth-boundary evidence frames. The reference renderer divides the full output into 128-pixel tiles, applies a projection-correct sub-frustum to each tile, renders smoke references at 4x and 8x and selected evidence references through 16x linear resolution per axis with no AA or tone mapping, and box-averages every supersample block in linear HDR. Tiling bounds peak render-target memory and avoids the device texture-size limit without changing the effective full-frame projection or texture derivatives.

Each reference condition stores supersampled EXRs plus a convergence CSV containing mean, RMS, maximum, and relative luminance error for each adjacent factor pair. Smoke runs also compare the tiled 8x result with a one-shot 8x render to catch projection, seam, and orientation errors. A result is a candidate ground truth only after convergence to the next sampled factor is reviewed.

## V1 spatial metrics

Use **OSFR > Capture > Run V1 720p Spatial Metrics** to capture the checkerboard and depth-boundary evidence frames as raw, bilateral, 8x reference, and 16x reference linear-HDR EXRs. The run records RGB MSE and PSNR against the 16x candidate reference, using the reference's maximum absolute RGB value as the explicit HDR peak. It also builds identical five-level luminance pyramids for each result and the reference, then reports Laplacian-band error and energy at every level plus the coarsest residual. Reference convergence remains a separate recorded input to interpretation rather than being hidden by the result metric.

Use **OSFR > Capture > Run V1 720p Parameter Sweep** to evaluate 27 combinations of bilateral strength, spatial sigma, and footprint-risk range on both evidence cases. The runner preserves every filtered EXR, writes per-condition metrics, identifies the Pareto frontier between normalized reference error and checkerboard fine-band energy mismatch, and recommends the closest balanced frontier point subject to no more than a 2% RGB-MSE regression on the depth-pole case. The ranking rule is recorded in the run manifest so the selected preset remains auditable rather than becoming an unexplained magic constant.

## V1 temporal metrics

Use **OSFR > Capture > Run V1 Temporal Smoke** for a 24-frame, 320x180 pipeline check, or **Run V1 720p Temporal Sequence** for the complete 600-frame lateral rail. The primary run compares raw and `V1_5x5_Balanced720p_01` output with a per-frame 4x spatial reference, persists the complete linear-HDR sequence, and writes per-frame residual and motion-compensated temporal-error rows.

The temporal metric warps the previous candidate and reference into the current frame, rejects background and depth-inconsistent samples, then evaluates the difference between their luminance deltas. `temporal_summary.csv` records full-reference residual RMSE, tRMSE, valid temporal coverage, and a project-specific 2–30 Hz DFT of the per-frame residual-RMSE envelope. Treat the short smoke run as plumbing validation only: its 24 samples do not have enough low-frequency resolution to interpret the shimmer ratio. The 4x temporal reference is also not a replacement for the separately recorded 8x/16x spatial convergence audit.

The first complete run, `temporal_720p_20260916_111307_673Z`, captured all 600 checkerboard frames with 599 valid transitions. Relative to raw rendering, the promoted bilateral preset reduced aggregate reference residual RMSE from 0.12908 to 0.05712 (55.75%), motion-compensated tRMSE from 0.60811 to 0.24625 (59.51%), and absolute 2–30 Hz residual-envelope power from 3.9881e-7 to 2.1238e-7 (46.75%). Valid temporal coverage was 4.38% of the full frame because background and disocclusions are excluded. Total AC envelope power increased from 5.9006e-5 to 7.5906e-5, so the result supports reduced high-frequency shimmer but does not justify a claim that every form of temporal variation improved. The native-AA section below records the completed baseline comparison.

## V1 native AA baselines

Use **OSFR > Capture > Run V1 Native AA Baseline Smoke** for a 24-frame check or **Run V1 720p Native AA Baselines** for the complete matrix. This runner enters Play Mode so each candidate advances exactly one engine frame per sample. TAA resets history once, warms up for eight frames at the starting pose, and then owns URP's native jitter and history. SMAA uses High quality. MSAA uses a separate 4x multisampled HDR target and resolve. All candidates reuse the same reference, depth, and static-scene motion streams from the completed temporal run.

The complete `native_aa_720p_20260916_113932_904Z` result contains 600 frames and 599 transitions for every method with no engine-frame discontinuities:

| Method | Reference residual RMSE | Motion-compensated tRMSE | 2–30 Hz power | Total AC power |
|---|---:|---:|---:|---:|
| Raw | 0.12908 | 0.60811 | 3.9881e-7 | 5.9006e-5 |
| Bilateral | **0.05712** | 0.24625 | 2.1238e-7 | 7.5906e-5 |
| SMAA | 0.08662 | 0.35075 | **1.8216e-7** | 1.5303e-5 |
| TAA | 0.07125 | **0.20432** | 1.3947e-6 | **1.3260e-5** |
| MSAA 4x | 0.12858 | 0.60666 | 3.8689e-7 | 5.9335e-5 |

On this interior material-frequency case, Bilateral improves on SMAA by 34.06% in spatial residual and 29.79% in tRMSE, satisfying the V1 report criterion relative to Raw/SMAA. TAA has the best tRMSE and lowest total AC envelope power, but its spatial residual is 24.74% worse than Bilateral and its 2–30 Hz envelope power is 6.57x higher. MSAA is effectively Raw, as expected for a signal that is not a silhouette-coverage problem. Disocclusions are excluded from the current tRMSE, so a depth-boundary ghosting metric remains necessary before treating TAA as fully characterized.

## V1 TAA disocclusion control

Use **OSFR > Capture > Run V1 TAA Disocclusion Smoke** for a 24-frame 320x180 pipeline check or **Run V1 720p TAA Disocclusion** for the 128-frame evidence window. The runner first captures unjittered Raw, Bilateral, 4x reference, linear-depth, and static-camera-motion frames for the depth-pole case. It then enters Play Mode, resets native URP TAA history once, warms up for eight real engine frames, and preserves native jitter/history across the sequence.

A current pixel is age zero when its linear eye depth is more than 2% farther than the motion-warped previous depth; nearer mismatches are new occlusions and are not counted. The mask follows the revealed surface for eight depth-consistent frames. Metrics report full-reference luminance RMSE/MAE at each age. Age zero also reports the least-squares projection of candidate error toward the warped old reference (0 means current reference, 1 means old history) and the fraction closer to old history than current truth.

The primary run, `taa_disocclusion_720p_20260916_232129_887Z`, covered frames 236–363 with 127 consecutive engine-frame transitions and 781,839 recently disoccluded pixel samples. TAA's aggregate recent-disocclusion RMSE was 2.21559 versus 0.61682 Raw and 0.60953 Bilateral: 3.59x Raw. At age zero it was 3.13209 versus 1.00385 Raw (3.12x). The TAA/Raw RMSE ratios remained 8.84x, 4.24x, and 3.65x at ages 1–3, then fell below Raw from age 4 onward. Aggregate age-zero history retention was only 0.0238, but individual transitions were episodic: frame 296 reached 3.10 retention, 89% closer-to-history pixels, and 67.6x Raw RMSE. The result therefore demonstrates a short, severe disocclusion instability with localized old-history ghosts, rather than uniform history retention across the whole rail.

This closes the remaining V1 native-TAA control. V2 should now move the band limit upstream into material/texture sampling while retaining both the checkerboard temporal sequence and this depth-boundary test as regression controls.

## V2 normal-resultant to roughness prototype

Open `Assets/OSFR/Validation/Scenes/V2_UpstreamFiltering.unity` or rebuild it with **OSFR > Setup > Create or Rebuild V2 Upstream Filtering Scene**. The scene crosses procedural normal fields at 16, 64, and 256 cycles/m with base GGX microfacet alpha values of 0.03, 0.1, and 0.3. The material evaluates the raw procedural normal or a 3x3 output-pixel-footprint mean before BRDF shading. It preserves the mean-normal length and applies the report's aggressive proxy `v = (1 - |mean|) / max(|mean|, epsilon)` followed by `alpha_eff = sqrt(alpha_base^2 + gain * v)`. This is explicitly Toksvig-inspired and is not presented as the Toksvig formula or a complete NDF filter.

The shader includes URP `DepthOnly` and `DepthNormalsOnly` passes so the panels participate in the camera depth/normal textures used by deterministic reprojection and later renderer integration.

Use **OSFR > Capture > Run V2 Normal Roughness Smoke** for a 320x180 2x/4x reference check, **Run V2 720p Normal Roughness Snapshot** for a 720p 8x/16x capture at the promoted gain, or **Run V2 720p Normal Roughness Gain Sweep** to repeat calibration. Each run writes Raw, filtered, resultant-length, and effective-alpha EXRs, full-frame and per-panel metrics, reference convergence, and immutable metadata under `Captures/V2`.

The first qualified run at gain 0.5, `normal_roughness_720p_20260916_233929_723Z`, reduced full-frame luminance RMSE by 65.22% but increased luminance MAE by 59.52%. The follow-up 23-point sweep, `normal_roughness_gain_sweep_720p_20260917_121203_289Z`, used Raw only as a non-selectable anchor, excluded filtered candidates with more than 2% RMSE regression on any panel, recorded the RMSE/MAE Pareto frontier, and selected the smallest gain within 5% of both eligible minima. That saturation-aware rule promotes gain 64 rather than the tested ceiling of 128.

At gain 64, full-frame luminance RMSE fell from 0.27964 to 0.02831 (89.88%), luminance MAE from 0.01078 to 0.00681 (36.86%), and RGB MSE from 0.08129 to 0.000833 (98.98%). Every panel improved in RMSE; the weakest panel still improved by 59.62%. Gain 128 only improves RMSE by another 3.71% and MAE by 4.04% relative to gain 64. The 8x-to-16x reference RMSE remains 0.04920, so the trend is decisive but the absolute error ordering of singular glossy highlights is reference-limited.

Use **OSFR > Capture > Run V2 Normal Roughness Temporal Smoke** for a 24-frame 320x180 check or **Run V2 720p Normal Roughness Temporal Sequence** for the complete 600-frame lateral rail. The runner uses the same static-scene world-position reprojection, depth rejection, tRMSE, and residual-envelope DFT conventions as V1. It explicitly resets the measurement view to color before every tiled reference capture and rejects any frame with zero foreground depth coverage.

The qualified run, `normal_roughness_temporal_720p_20260917_124518_484Z`, contains 600 frames and 599 valid transitions at 720p with a per-frame 4x reference. Gain 64 reduced aggregate residual RMSE from 0.30221 to 0.08081 (73.26%), motion-compensated tRMSE from 1.22327 to 0.33144 (72.91%), 2–30 Hz residual-envelope power from 0.0036005 to 0.00005186 (98.56%), and total AC envelope power from 0.0039705 to 0.00007114 (98.21%). It improved every frame residual and every temporal transition, with identical 5.50% valid temporal coverage for both candidates.

The scalar roughness-transfer baseline has passed its temporal qualification.

## V2 LEAN-like slope-moment prototype

Open `Assets/OSFR/Validation/Scenes/V2_LeanFiltering.unity` or rebuild it with **OSFR > Setup > Create or Rebuild V2 LEAN Filtering Scene**. The scene retains the 16, 64, and 256 cycles/m by alpha 0.03, 0.1, and 0.3 matrix, but replaces the isotropic two-axis pattern with a one-dimensional normal wave oriented 35 degrees in tangent space. This produces a rank-one directional slope distribution that can distinguish covariance-aware filtering from scalar roughness broadening.

The LEAN-like shader path samples a 3x3 output-pixel footprint, stores `E[slope]`, `E[slope.x^2]`, `E[slope.y^2]`, and `E[slope.x*slope.y]`, subtracts the outer product of the mean to form a positive-semidefinite 2x2 covariance, and diagonalizes it. The eigenvectors orient the anisotropic NDF, while `alpha_major^2 = alpha_base^2 + gain * variance_major` and the corresponding minor-axis equation transfer the eigenvalues into GGX roughness. Gain 1 is used as the direct unit slope-variance mapping. `_LeanVisibilityMode` selects either the promoted geometric-mean visibility control or height-correlated anisotropic Smith-GGX visibility using tangent-frame view and light projections.

Use **OSFR > Capture > Run V2 LEAN Comparison Smoke** for a 320x180 2x/4x check or **Run V2 720p LEAN Comparison** for the 720p 8x/16x evidence capture. Each run records Raw, scalar gain 64, and LEAN-like color; effective-alpha, anisotropy, and principal-axis diagnostics; full-frame and per-panel metrics; reference convergence; and immutable metadata.

The qualified run, `lean_comparison_720p_20260917_213403_567Z`, reduced luminance RMSE from 0.01339 Raw and 0.02321 scalar to 0.00375: a 71.97% improvement over Raw and 83.83% over scalar. Luminance MAE improved by 70.71% and 88.29%, while RGB MSE improved by 92.14% and 97.38%. LEAN-like filtering beat Raw on all nine panels and scalar gain 64 on eight. The exception was the low-frequency, alpha 0.3 panel, where scalar RMSE was 19.74% lower. Reference convergence was strong at 0.000204 RMSE between 8x and 16x, well below every full-frame candidate error.

Use **OSFR > Capture > Run V2 LEAN Temporal Smoke** for the 24-frame pipeline check or **Run V2 720p LEAN Temporal Sequence** for the complete 600-frame rail. This runner evaluates Raw, scalar gain 64, and LEAN-like output against the same per-frame spatial reference and with the same static-camera reprojection, depth rejection, tRMSE, and residual-envelope DFT conventions used by the earlier temporal qualifications.

The qualified temporal run, `lean_temporal_720p_20260918_101233_263Z`, recorded all 600 frames and 599 transitions with identical 5.50% valid temporal coverage for every candidate. LEAN-like output beat Raw and scalar on every frame residual and every transition. Against Raw it reduced aggregate residual RMSE from 0.02219 to 0.00580 (73.85%), tRMSE from 0.07516 to 0.02396 (68.12%), 2–30 Hz residual-envelope power by 95.57%, and total AC power by 94.75%. Against scalar it reduced residual RMSE by 73.91% and tRMSE by 64.27%. Scalar had the lowest envelope power, but its residual RMSE was 0.21% worse than Raw and it beat Raw on only 193 of 600 frames and 218 of 599 transitions. The scalar stability advantage is therefore recorded as overfiltering rather than a reconstruction win.

The Smith spatial A-B, `lean_smith_ab_720p_20260918_103728_735Z`, found that exact anisotropic visibility improved six of nine panels, including a 37.36% RMSE reduction on the 256 cycles/m, alpha 0.3 panel. It nevertheless raised full-frame RMSE by 17.65%, MAE by 12.55%, and RGB MSE by 38.37% because the 16 and 64 cycles/m rough panels regressed by 22.39% and 41.44%. Reference convergence remained strong at 0.000204 RMSE.

The complete temporal A-B, `lean_smith_temporal_ab_720p_20260918_103925_452Z`, recorded all 600 frames and 599 transitions. Relative to geometric-mean visibility, anisotropic Smith raised aggregate residual RMSE from 0.00580 to 0.00652 (12.36%), tRMSE from 0.02396 to 0.02738 (14.28%), 2–30 Hz residual-envelope power by 30.76%, and total AC power by 31.57%. Smith won 95 frames and 91 transitions; the geometric-mean control won the remaining 505 frames and 508 transitions. Full Smith is therefore retained as an experimental mode but not promoted as the default.

The Smith rejection is also a scope boundary: further tuning one normal-filtering branch would not yet make OSFR a rendering pipeline. The research plan instead calls for distinct upstream filters for texture footprint, normal/NDF distribution, material frequency, and geometry/coverage, followed by BRDF evaluation and only then residual spatial/temporal reconstruction.

## V2 roughness-distribution material branch

Open `Assets/OSFR/Validation/Scenes/V2_RoughnessFiltering.unity` or rebuild it with **OSFR > Setup > Create or Rebuild V2 Roughness Filtering Scene**. The scene is the report's roughness-frequency torture case: 1, 2, and 4 mm alternating roughness bands crossed with alpha pairs 0.03–0.30, 0.10–0.50, and 0.30–0.80. The bands are procedural in world space and rotated 25 degrees so the result is not tied to a screen axis.

The shader estimates the output-pixel material distribution over a 3x3 world-position footprint before nonlinear GGX evaluation. `Raw` center-samples alpha. `AlphaSquaredMoment` collapses the footprint to one lobe with `alpha = sqrt(E[alpha^2])`. `DistributionMixture` averages nine BRDF responses over the sampled alpha distribution while holding geometry, normal, view, and light fixed. The mixture is deliberately an expensive correctness control: it tests whether distribution-preserving pre-BRDF integration solves a failure that a filtered scalar cannot, rather than proposing nine BRDF evaluations as the shipping implementation. Mean alpha, variance, and relative-variance diagnostics expose the material-frequency risk signal.

Use **OSFR > Capture > Run V2 Roughness Distribution Smoke** for a 320x180 2x/4x check or **Run V2 720p Roughness Distribution Comparison** for the 720p 8x/16x evidence capture. The qualified run, `roughness_distribution_720p_20260918_112052_432Z`, used a converged reference: 8x-to-16x luminance RMSE was 0.00602, versus 0.04310 for the best candidate. Distribution mixture beat both alternatives on all nine panels. Relative to Raw, it reduced full-frame luminance RMSE from 0.12975 to 0.04310 (66.78%), luminance MAE from 0.05393 to 0.01272 (76.42%), and RGB MSE from 0.01750 to 0.00193 (88.96%). The one-lobe alpha-squared moment was not an adequate representation: it increased RMSE by 52.15%, MAE by 49.75%, and RGB MSE by 131.50% versus Raw.

This result establishes a second, independent OSFR signal class beside normal-slope covariance. It supports the pipeline invariant that footprint distributions must be filtered in the domain consumed by the nonlinear shading operation; a universal scene-color blur or one scalar roughness cannot represent both.

Use **OSFR > Capture > Run V2 Roughness Distribution Temporal Smoke** for the 24-frame 320x180 check or **Run V2 720p Roughness Distribution Temporal Sequence** for the complete 600-frame micro-motion qualification. It uses the same per-frame supersampled reference, static-scene world-position reprojection, relative-depth rejection, tRMSE, and residual-envelope DFT conventions as the earlier temporal branches.

The qualified run, `roughness_distribution_temporal_720p_20260918_232333_554Z`, recorded all 600 frames and 599 transitions with identical 33.49% valid temporal coverage. Distribution mixture beat Raw on every frame and every transition, reducing aggregate reference-residual RMSE from 0.11742 to 0.03658 (68.85%) and motion-compensated tRMSE from 0.20595 to 0.05760 (72.03%). However, its absolute 2–30 Hz residual-envelope power increased by 260.91% and total AC power by 703.85%; the shimmer fraction fell from 33.11% to 14.86% because most added modulation lies below 2 Hz. The one-lobe moment control reduced absolute power by about 82% but increased residual RMSE by 67.77% and tRMSE by 75.81%, losing all 600 frames and 599 transitions. Its apparent stability is overfiltering, not reconstruction quality.

The nine-sample mixture therefore remains the spatial/transition correctness oracle but is not temporally promoted. Its point samples cross binary band boundaries discontinuously under subpixel motion. The next material-frequency step is a phase-stable footprint integral that computes continuous low/high material coverage and shades a weighted two-lobe mixture. That representation should preserve the nonlinear distribution result with two BRDF evaluations while directly targeting the temporal-power failure, after which the project can proceed to the remaining texture-footprint and geometry/coverage branches.
