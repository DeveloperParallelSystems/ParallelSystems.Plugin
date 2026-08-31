# Fabrication STEP Source Layout

The fabrication implementation is organised as partial `FabricationStepService` files so geometry rules remain private to one service while each responsibility stays reviewable.

| File | Responsibility |
|---|---|
| `FabricationPreflightService.cs` | Worksharing freshness and ownership checks |
| `FabricationProcessedRegistry.cs` | Local successful-export status used by Show Ready |
| `FabricationStepService.Selection.cs` | Selection and supported-source filtering |
| `FabricationStepService.Status.cs` | Show Ready and temporary isolation |
| `FabricationStepService.Generation.cs` | Orchestration, retained inspection view on success, rollback on failure, STEP staging, validation report |
| `FabricationStepService.StepTopology.cs` | STEP topology helpers and reusable topology construction |
| `FabricationStepService.Diagnostics.cs` | Command-scoped geometry, topology, fallback, and validation diagnostics |
| `FabricationStepService.Dimensions.cs` | Pipe/fitting ID, OD, wall, and geometry inference |
| `FabricationStepService.Connections.cs` | Connector traversal, weld skipping, branch/coupling relationships, physical branch-axis resolution, controlled STD WT-CS dimensions, and adjacent fitting dimension overrides |
| `FabricationStepService.PipeGeometry.cs` | Pipe solids and pipe-end preparation |
| `FabricationStepService.FittingGeometry.cs` | Generic fitting bores and fitting solids, shaped-branch flush trimming, and post-trim saddle-bore cleanup |
| `FabricationStepService.ReducerGeometry.cs` | Butt-weld and capillary concentric reducer construction |
| `FabricationStepService.ChamferGeometry.cs` | 30-degree bevel generation and verification |
| `FabricationStepService.BranchGeometry.cs` | SET-ON branch and side-coupling openings/continuity, near-side wall protection, and circular outlet-axis bore cleanup |
| `FabricationStepService.Classification.cs` | Family/type/parameter classification rules |
| `FabricationStepService.GeometryUtilities.cs` | Shared Revit solid and curve helpers |
| `FabricationStepService.Performance.cs` | One-command caches for repeated Revit classification, dimensions, geometry, bounds, display names, and transparent-connection resolution |
| `FabricationDiagnosticsExporter.cs` | Read-only minified JSON capture for selected fabrication sources and bounded connector context |
| `ContinuousTopCapabilityDiagnostic.cs` | Developer analysis of continuous-top shaped-branch surface capability |

## Rules

- Generate geometry only for `FabricationSelection.SourceElementIds`; network traversal is for dimension and connection resolution, not automatic scope expansion.
- Keep document fallbacks bounded to the selected fabrication scope. Do not scan every pipe or every Generic Model in a large project.
- Keep the run cache command-scoped. Never persist Revit `Element`, `Solid`, connector, bounding-box, or dimension objects between exports.
- Filter bounded fallback pipes by nominal sizes requested by selected fitting connectors before resolving full pipe dimensions.
- Do not parallelize Revit API collectors, connectors, geometry, Boolean operations, DirectShape creation, or export.
- Build the inspection section box from generated DirectShapes and frame it with `ShowElements`/`ZoomToFit`.
- Do not add geometry methods back into `FabricationStepService.cs`; it contains shared constants only.
- Do not reintroduce project-wide view hiding. Roll back on failure; retain only the dedicated inspection view and its generated DirectShapes after verified success.
- Do not automatically synchronize, reload, or check out user-owned source elements.
- Worksharing messages must use `AppDialog`, including detailed blocking and confirmation dialogs.
- Do not guess missing fabrication dimensions. Add a family-specific resolver or report a blocking issue.
- Preserve the verified-file staging sequence: generate, validate, verify non-empty, then allow the user to save.

## SET-ON Shaped-Branch Contract

- Resolve the branch outlet from its physical connector axis. Use spatial fallback only when the family does not expose a usable outlet direction.
- Resolve the selected large header from the physical axis-to-cylinder intersection when the family header connector is missing or unconnected.
- Cut the full circular branch cutter through the near-side header wall only. The curved header surface naturally displays a circle or ellipse; do not flatten it with a rectangular clipping slab.
- Preserve the opposite header wall for every supported header size.
- Trim the shaped-branch fitting against the analytical header outside cylinder so no branch sleeve protrudes into the main bore.
- Run the final coaxial post-trim cleanup after the flush trim, because trimming a multi-solid adjustable family can expose a saddle lip that did not exist before the trim.
- Use a selected/connected branch pipe as the first dimension authority. The explicit `STD WT-CS` controlled table is a deterministic fallback only for correctly classified families with a valid nominal size.
- Record a successful controlled fallback as Information. Do not suppress blocking issues when classification or dimensions remain unsafe.
- A connected carbon flange may inherit the resolved shaped-branch outlet bore; do not invent an unrelated flange ID.
