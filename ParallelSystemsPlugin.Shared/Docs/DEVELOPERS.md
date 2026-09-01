# Developer Notes

## Current Internal Build

- Revit plugin version: **1.17.8**
- Release: **Internal / Unreleased**
- Monitoring architecture generation: **V2**
- Production tracker schema: **3**
- Backend tracker compatibility: **2 and 3**
- Supported Revit adapters: **2021 through 2026**
- Native STEP export: **Revit 2025 and 2026**

The monitoring architecture V2 label is not the Revit plugin release number. Backend and Frontend package metadata can remain 2.0.0 as architecture/application metadata. The user-facing Revit product, Revit assemblies, About screen, and production tracker pluginVersion must identify the plugin as 1.17.8.

## Authoritative Version Sources

`ParallelSystems.Plugin/Directory.Build.props` sets:

```text
Version                 1.17.8
AssemblyVersion         1.17.8.0
FileVersion             1.17.8.0
InformationalVersion    1.17.8
```

`IncludeSourceRevisionInInformationalVersion` is false.

`AboutDialog.xaml.cs` reads AssemblyInformationalVersion first, falls back to file/assembly version, removes any +sourceRevision suffix, and trims a final .0 field.

`TimesheetTracker.cs` uses the same assembly informational version, strips a +suffix, trims .0, and sends 1.17.8 in TrackerCheckpointRequest.PluginVersion. It sends SchemaVersion 3 when the tracker is enabled.

## Release Focus in 1.17.8

- Development Mode: environment-variable gating, a per-process password challenge, explicit opt-out behavior, no timesheet tracker startup, and a persistent ribbon indicator.
- Development tools: enable/disable command files and an internal Word operating guide.

## Release Focus in 1.17.7

- Procurement: active-view `Filter Items`, Excel/PDF selection, reusable-offcut threshold, corrected Cut List and Pipe Report calculations, package-aware Fitting and Field Material output, bounded Victaulic coupling traversal to a pipe assembly using the Assembly Register naming rule, and standardized workbook layout.
- Fabrication: continuous-top capability diagnostics, expanded minified JSON diagnostics, branch topology/surface validation, additional geometry fallbacks, and current shaped-branch connection and selection handling.

## Fabrication STEP Performance in 1.17.6

`FabricationStepService.Performance.cs` owns a run-scoped cache that exists only during one STEP generation command. It caches Revit data that is repeatedly requested by independent rules:

- normalized source classification inputs and classification results;
- family/type elements;
- a bounded cache of extracted source solids;
- source bounding boxes, centres, and extents;
- element display names;
- successful and failed pipe-dimension resolution;
- straight-pipe axis data used by pipe generation and spatial branch resolution;
- resolved connections across transparent weld/non-connector helpers.

The cache is thread-local and document-scoped. It is disposed before the command returns, so no geometry, element wrappers, or parameter values are reused after document changes.

`BuildDocumentNominalDimensionMap` still uses the selected-scope bounding envelope, but now performs full dimension resolution only for pipe nominal sizes requested by selected fitting connectors. Do not remove the selected-scope or nominal-size filters.

Do not parallelize Revit collectors, connector access, geometry extraction, Boolean operations, DirectShape creation, or STEP export. Revit API objects are document-bound and must remain on the Revit API thread.

The largest remaining cost for large valid packages is expected to be actual solid generation, Boolean subtraction, DirectShape creation, Revit regeneration, and native STEP export. Those stages cannot be safely cached across runs without risking stale or incorrect fabrication output.

## Solution Layout

- `RevitPlugin/` - Revit adapters, shared ribbon commands, reports, configuration, Fabrication STEP, authorization, and silent timesheet tracking.
- `ParallelSystemsPlugin.Shared/Timesheets/TrackerCheckpointModels.cs` - plugin-owned checkpoint transport models. The server maintains its own matching API models.
- `Backend/` - ASP.NET Core API, authorization, ingestion, PostgreSQL access, Task Mapping, billing, and Timesheets.
- `Frontend/` - React monitoring application.
- `scripts/` - local stack, tracker configuration, test checkpoint, and failed-message recovery.
- `docs/` - architecture, mapping, policy, deployment, validation, migration, and tests.

Do not reintroduce MSMQ, the old Activity Queue Service, or SQLite as the central database.

## Active Revit Ribbon

`App.BuildRibbon` creates these panels:

- Property Mapping
- Procurement
- Fabrication
- Settings
- Tools
- About

Active commands are:

```text
Property Mapping
- Map Pipe End Prep
- Clear Pipe End Prep
- Map Fittings End Prep
- Clear Fittings End Prep
- Pipe Weight
- Clear Pipe Weight

Procurement
- Export BOM
- Filter Items
- Publish BOM

Fabrication
- Fabrication STEP
- Export Diagnostics
- Show Ready

Settings
- Configurations

Tools
- Elevation Check
- Renaming / Import CSV
- Renaming / Export CSV
- Sheet Number Check
- Pipe Length Check / Apply Pipe Filter
- Pipe Length Check / Clear Pipe Filter
- End Prep Check / Apply End Prep Filter
- End Prep Check / Clear End Prep Filter
- BOM Check

About
- About and Manual
- About Us
```

Header ND and Detailing command classes remain in source but are not built onto the active ribbon. Documentation must not present them as available commands.

## Configuration UI

`UI/Dialogs/Configurations.xaml` contains five active TabItems:

- Pipe End Prep
- Fittings End Prep
- Pipe Weight
- Procurement
- Tools

The Procurement tab provides an Excel/PDF radio choice plus stock length, blade thickness, negative allowance, and offcut-threshold inputs. New configuration defaults select Excel, exclude site-measured work, and use a 2500 mm reusable-offcut threshold.

Any documentation update should be checked against this XAML and its code-behind rather than copied from an older manual.

## Authorization Flow

### Optional development bypass

`App.IsDevelopmentServerBypassEnabled()` first checks whether the `PARALLEL_SYSTEMS_DEVELOPMENT_MODE` environment variable is exactly `1`. When it is, the plugin prompts for the password embedded in `App.cs` and caches the result only for the current Revit process. An incorrect password leaves the prompt open for another attempt. Choosing `Do not enable development mode` or closing the prompt disables development mode for the remainder of that Revit session. A successful entry skips authorization, enables protected-command checks through `IsUserAuthorized`, and deliberately leaves `_timesheetTracker` null. Closing and reopening Revit clears the cached result and requires the password again. `ApiSettings.json` cannot enable this mode.

After development mode is unlocked, `App.BuildRibbon` adds a disabled `DEVELOPMENT MODE ACTIVE / NO TRACKING` indicator panel using the Parallel Systems logo. The indicator is informational and cannot toggle the mode during the session.

Use `Development Tools/Enable-DevelopmentMode.cmd` and `Development Tools/Disable-DevelopmentMode.cmd` to add or remove the per-user environment variable. The disable command first broadcasts an empty value before removing the registry entry so Windows Explorer refreshes its inherited environment. Restart Revit and any IDE or terminal used to launch it after running either command.

- Ribbon creation occurs during `OnStartup`.
- ApiSettings.json is loaded from the current user's Revit Addins folder for the active Revit year.
- Authorization begins from Idling.
- HTTP work is asynchronous and HttpClient has no global timeout.
- Each authorization request uses a 75-second linked cancellation timeout.
- Temporary failures schedule another authorization cycle.
- Explicit `Allowed=false` denies access.
- `StartAuthorizedServices` starts TimesheetTracker only after successful authorization.
- Protected commands check `App.IsUserAuthorized`.
- Diagnostics are appended to the installed add-in `Logs/Authorization.log`.

Authorization and tracker ingestion use different configuration files and different endpoints. Do not treat a successful API health check as proof that the workstation authorization BaseUrl and Revit username are correct.

## Timesheet Flow

1. Authorized Revit events reach `TimesheetTracker`.
2. `AutomaticContextResolver` derives project and view context.
3. `EvidenceAccumulator` stores compact summarized evidence.
4. `WindowsActivityDetector` reports foreground process and last-input age.
5. Tracker time totals are cumulative for a session.
6. A schema-version 3 checkpoint is written locally first.
7. `LocalOutboxClient` sends pending files oldest-first.
8. Backend MessageId idempotency and cumulative upsert logic prevent normal retry duplication.
9. PostgreSQL stores compact work sessions.
10. Task Mapping assigns Task Category through a rule or manual review.
11. Client assignment remains a separate manual administrator action.
12. Timesheets summarizes client-assigned Engaged Project Time by Task Category.

Drafters do not enter Area, Level, Zone, System, Task Category, Status, client, or timesheet information.

## Tracker Defaults

```text
SamplingIntervalSeconds          5
CheckpointIntervalSeconds       60
ActiveInputThresholdSeconds     90
EngagedGraceSeconds            300
MaxElementsInspectedPerChange  250
MaxPendingMessages            5000
```

Tracker settings:

```text
C:\ProgramData\Parallel Systems\Timesheet\tracker.settings.json
```

Environment overrides:

```text
PARALLEL_TIMESHEET_API_URL
PARALLEL_TIMESHEET_API_KEY
```

## Local Outbox

Default root:

```text
%LOCALAPPDATA%\Parallel Systems\Timesheet
```

- `Outbox/` contains pending JSON checkpoints.
- `Failed/` contains permanent failures and malformed files.
- `Overflow/` receives oldest pending files after the configured queue limit is exceeded.
- `tracker.log` contains sender diagnostics.
- `installation.id` is the stable workstation installation identity.

Schema-version rejection remains queued because it can be caused by backend/plugin deployment order. Unauthorized, forbidden, not-found, conflict, and malformed payloads are quarantined.

The local Outbox improves resilience but is not guaranteed lossless under disk, profile, corruption, or workstation failure.

## Task Mapping and Client Billing Rules

Mapping rules assign Task Category only.

Never infer or seed a client from a Task Category. The same Task Category can belong to different clients. Manual classification and manual client assignment must remain independent and administrator-controlled.

Do not add hardcoded sample clients or sample billing categories to production schema initialization.

## Fabrication Diagnostics

`Export Diagnostics` is intentionally separate from STEP generation and must remain read-only.

- Command: `Commands/ExportFabricationDiagnosticsCommand.cs`
- Exporter: `Fabrication/FabricationDiagnosticsExporter.cs`
- UI registration: `UI/FabricationMenu.cs`
- Output: one minified JSON file named from the opened sheet where available
- Scope: selected components/assembly members plus connector context to depth 3, capped at 2,000 additional context elements
- Geometry: full face/edge/mesh data for selected elements and summary geometry for connection-context elements
- Safety: no transactions, no element modification, no checkout, no Reload Latest requirement

Use the JSON when a family-specific fabrication rule cannot be solved reliably from screenshots. Do not add diagnostic-only geometry assumptions to the production STEP builders.

## Fabrication STEP Architecture

The fabrication implementation is split by responsibility instead of keeping the entire subsystem in one 8,000-line class.

```text
Commands/GenerateFabricationStepCommand.cs
Commands/FabricationStatusCommands.cs
Fabrication/FabricationPreflightModels.cs
Fabrication/FabricationPreflightService.cs
Fabrication/FabricationProcessedRegistry.cs
Fabrication/FabricationStepModels.cs
Fabrication/FabricationStepService.cs
Fabrication/FabricationStepService.Selection.cs
Fabrication/FabricationStepService.Status.cs
Fabrication/FabricationStepService.Generation.cs
Fabrication/FabricationStepService.Performance.cs
Fabrication/FabricationStepService.Dimensions.cs
Fabrication/FabricationStepService.Connections.cs
Fabrication/FabricationStepService.StepTopology.cs
Fabrication/FabricationStepService.PipeGeometry.cs
Fabrication/FabricationStepService.FittingGeometry.cs
Fabrication/FabricationStepService.ReducerGeometry.cs
Fabrication/FabricationStepService.ChamferGeometry.cs
Fabrication/FabricationStepService.BranchGeometry.cs
Fabrication/FabricationStepService.Classification.cs
Fabrication/FabricationStepService.GeometryUtilities.cs
Fabrication/FabricationStepService.Diagnostics.cs
Fabrication/ContinuousTopCapabilityDiagnostic.cs
UI/AppDialog.cs
UI/FabricationMenu.cs
```

`FabricationStepService` remains a partial static class so existing private geometry helpers and fitting rules can be separated without changing their calling semantics. New geometry rules belong in the matching domain file rather than being added to the core file.

### Worksharing preflight

Before generation, `FabricationPreflightService` checks only the selected source elements:

- `UpdatedInCentral` and `DeletedInCentral` are blocking. The user must Reload Latest or Synchronize with Central.
- `OwnedByOtherUser` is shown through the custom `AppDialog.ConfirmDetailed` dialog. Continuing is read-only and exports the current local snapshot.
- A read-only document or an inability to verify central status/ownership is blocking.
- The command never automatically checks out source elements or starts a synchronization.

Show Ready separately checks the active view. If that view is stale or owned by another user, temporary isolation is blocked before Revit attempts to borrow the view.

### Inspection and export transaction model

The generator:

1. resolves and validates only the selected source scope without modifying source elements;
2. creates DirectShapes and a dedicated Fine-detail 3D inspection view inside a `TransactionGroup`;
3. builds the section box from the generated DirectShapes;
4. hides non-fabrication categories and only nearby existing Generic Models in that new view;
5. exports STEP to a private staging directory;
6. verifies that the STEP exists and is non-empty;
7. rolls back the transaction group on any generation/export failure; and
8. assimilates the group only after verified success, opens the retained inspection view, and frames the generated DirectShapes.

Do not restore the old `HideGeneratedElementsInOtherViews` loop. It attempted to edit nearly every project view and caused Revit to request checkout of thousands of view worksets in workshared models.

The retained DirectShapes are generated only from `FabricationSelection.SourceElementIds`. Connected elements may be traversed for dimensions and relationship classification, but they are not generated unless selected.

### SET-ON shaped-branch geometry contract

The final shaped-branch workflow is intentionally split between connection resolution, header opening, fitting trimming, and fitting bore cleanup:

1. `FabricationStepService.Connections.cs` resolves the physical branch outlet axis and finds the selected header through axis-to-cylinder intersection when the family header connector is missing or unconnected.
2. Outlet dimensions come from a selected/connected branch pipe first. Explicit `STD WT-CS` adjustable families may use the controlled standard-weight carbon-steel table only after classification and nominal-size validation.
3. Connected carbon flanges may inherit the resolved branch outlet bore when the flange family does not provide its own ID.
4. `FabricationStepService.BranchGeometry.cs` creates the full branch opening and limits its depth so only the near-side header wall is removed. Do not restore a rectangular clipping slab that collapses the opening into a slit.
5. `FabricationStepService.FittingGeometry.cs` removes shaped-branch material inside the analytical header outside cylinder so the branch terminates flush and does not protrude into the main bore.
6. A final coaxial post-trim cleanup cutter removes saddle lips exposed by multi-solid adjustable families and preserves a circular branch-side bore.

A successful controlled `STD WT-CS` lookup is an `Information` issue, not a `Warning`. It remains visible in the validation audit but does not require corrective action. Missing classification, nominal size, ID/OD, wall thickness, or a safe header intersection remains blocking.

### Large-model performance

- `BuildDocumentNominalDimensionMap` is bounded to a 1 m padded outline around the selected source scope instead of resolving every pipe in the document.
- Directly connected network traversal remains the primary dimension resolver.
- Inspection-view Generic Model filtering uses `FilteredElementCollector(doc, view.Id)` after the generated section box is active.
- The inspection view is framed with `UIDocument.ShowElements` and `UIView.ZoomToFit`.
- Do not replace these scoped operations with document-wide collectors unless a measured requirement proves they are necessary.

After the user saves the verified STEP, `FabricationProcessedRegistry` records the source `UniqueId` values under:

```text
%LOCALAPPDATA%\Parallel Systems\FabricationStep\Processed
```

Show Ready reads this local registry and also recognises legacy DirectShape metadata for backward compatibility. The local registry is intentionally outside the Revit model; it avoids source-element ownership and central-model writes.

Fabrication geometry is safety-sensitive. Do not replace blocking validation with guessed dimensions. Reducers, tees, crosses, reducing elbows, custom fittings, chamfers, flange joints, shaped branches, side couplings, and capillary fittings require downstream section and dimension testing.

## About and Documentation Packaging

`AboutDialog.xaml.cs` resolves documentation beside the loaded Revit assembly:

```text
Docs/UserManual.md
Docs/CHANGELOG.md
Docs/DEVELOPERS.md
```

`ParallelSystemsPlugin.Shared.projitems` includes `Docs/**/*` as Content with CopyToOutputDirectory=PreserveNewest.

The in-dialog Markdown renderer supports headings beginning with `#`, `##`, and `###`; bullet lists; numbered lists; fenced code; bold text; backtick emphasis; and links. Avoid level-four headings because the current renderer treats them as ordinary paragraphs.

## Deployment

- Backend and Frontend can be built and deployed independently from the Revit plugin.
- Deploy backend schema compatibility before rolling out a newer Revit tracker producer.
- Keep secrets in Render or local secure configuration, not source control.
- Build only the Revit adapter matching the installed Revit year for a local test.
- Revit 2021-2024 cannot perform the native Fabrication STEP export.

## Validation Requirements

Before release:

- Build the target Revit adapter on Windows with the installed Revit API assemblies.
- Open About and confirm `Version: 1.17.8` with no Git suffix.
- Confirm installed Docs files load.
- Exercise every active ribbon panel.
- Verify authorization allow, deny, slow-server, and temporary-failure behaviour.
- Test schema 2 and 3 ingestion.
- Test offline Outbox retry, Failed, and Overflow handling.
- Run the Fabrication STEP geometry test in Revit 2025 and inspect the STEP downstream.
- Test at least the 200 mm and 250 mm SET-ON header cases, including an 80 mm branch where applicable. Inspect the header from both ends and the branch from its outlet.
- Confirm the header opening does not reach the opposite wall, the main bore is smooth end to end, the branch terminates flush, and the branch-side bore is circular without saddle lips.
- Confirm a valid `STD WT-CS` fallback reports Information with zero warnings and that an unresolved family still blocks export.
- Build the Frontend production bundle.
- Build Backend and run API/PostgreSQL tests.

## Coding Boundaries

- Preserve existing non-timesheet plugin behaviour unless a change is explicitly requested.
- Tracking and authorization exceptions must not crash Revit.
- Use Revit owner handles for modal WPF dialogs.
- Keep shared source compatible with all six adapters.
- Keep Task Category mapping independent from client assignment.
- Do not upload screenshots, keystroke contents, unrelated application contents, or permanent element-level activity logs.
- Do not claim the local Outbox is lossless under every failure mode.
