# Contributing to OSFR

Thank you for helping improve Output-Aware Spatial Frequency Rendering. OSFR is an evidence-driven rendering research project, so a useful contribution includes both implementation quality and a clear measurement claim.

## Before starting

- Read the [project overview](README.md) and [implementation guide](Unity/Assets/OSFR/README.md).
- Search existing issues before opening a new one.
- For a large architectural change, open a research-proposal issue before investing in implementation.
- Keep generated captures, Unity caches, test output, and local analysis intermediates out of Git.

## Development environment

- Unity `6000.3.24f1`
- Universal Render Pipeline `17.3.0`
- Linear color space with Render Graph enabled
- Git LFS installed before cloning

Open the `Unity/` directory as the Unity project.

## Pull-request expectations

A pull request should:

1. State the hypothesis or defect being addressed.
2. Identify the affected signal class: measurement, texture, normal/NDF, material, geometry/coverage, spatial reconstruction, or temporal reconstruction.
3. Include a deterministic smoke case when rendering behavior changes.
4. Compare against the appropriate Raw and existing promoted controls.
5. Record reference convergence when a supersampled result is treated as ground truth.
6. Report regressions and rejected conditions, not only aggregate wins.
7. Update the relevant documentation and tests.

Do not commit the generated `Captures/` directory. Include concise metrics and the runner configuration in the pull-request description so reviewers can reproduce the evidence locally.

## Validation

Run all EditMode and graphics-enabled PlayMode tests before submitting. The current release gate is 69 EditMode and 12 PlayMode tests.

For shader or capture changes, also run the smallest relevant smoke matrix from the **OSFR > Capture** menu. Do not use `-nographics` for capture runs because they require a real graphics device.

## Code style

- Follow the existing C# and HLSL formatting.
- Prefer deterministic, frame-indexed evaluation over elapsed-time integration.
- Keep measurement math isolated and unit-testable where possible.
- Preserve linear-HDR values until an explicitly documented display transform.
- Name experimental controls so that an approximation cannot be mistaken for a promoted production path.

## Commit and PR scope

Keep changes focused. Avoid combining a renderer change with unrelated scene regeneration or formatting. Unity `.meta` files must accompany their assets.

By contributing, you confirm that you have the right to submit the contribution and agree that it may be distributed under the project's [MIT License](LICENSE).
