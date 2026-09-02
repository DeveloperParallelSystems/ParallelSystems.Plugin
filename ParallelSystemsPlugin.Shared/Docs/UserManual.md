# Parallel Systems Revit Plugin - User Manual

## Current Build

- Version: **1.17.8**
- Release: **Internal / Unreleased**
- Supported Revit adapters in this solution: **2021 through 2026**
- Native Fabrication STEP export: **Revit 2025 and 2026**
- Monitoring architecture generation: **V2**
- Monitoring flow: **Revit Plugin -> HTTPS API -> PostgreSQL -> React Web Application**

The public plugin version is 1.17.8. The V2 label used in monitoring and deployment documentation describes the monitoring architecture generation; it is not the Revit plugin release number.

## What Is New in 1.17.8

- Added password-protected Development Mode for controlled troubleshooting sessions.
- Development Mode is enabled through `PARALLEL_SYSTEMS_DEVELOPMENT_MODE=1`, bypasses authorization and protected-command checks, and prevents timesheet tracker startup for that Revit process.
- Added a topmost retrying password prompt, an explicit opt-out action, and a persistent `DEVELOPMENT MODE ACTIVE / NO TRACKING` ribbon indicator.
- Added Development Mode enable/disable commands and an internal Word guide.
- A successful normal-mode Reconnect now starts timesheet tracking and flushes the local Outbox just like successful startup authorization.
- Procurement configuration now auto-detects Project Number and Project Name when both saved values are empty and uses a larger resizable report-settings layout.
- Report entries can be clicked to reveal report-specific settings. Fitting Report currently provides an `Include Weld` option.
- Fitting Report PDF and Excel output now use Package instead of Material Grade, place `NO PACKAGE ASSIGNED` after every named package, and use `BOM DESCRIPTION NAME` for an equal-size tee when available.
- Loading Report sorts assembly lengths from smallest to largest within each package.
- Company and client logo selection starts in `%ProgramData%\Parallel Systems\Images` when that folder exists. The installer places the bundled logo images there.

The current build also includes the following 1.17.7 procurement and fabrication changes:

- Added `Filter Items` to the Export BOM split button. It groups BOM components visible in the active view and temporarily hides unchecked family/type groups so report scope can be reviewed before export.
- Added an explicit Excel or PDF report-output choice. New configurations default to Excel and exclude site-measured spools and branches.
- Added a reusable-offcut threshold. Pipe Report now separates reusable partial stock into an `OFFCUT` column and subtracts those partial pieces from the full-stock quantity.
- Improved Cut List assembly marks, package grouping, site-measure exclusion, column order, and total-waste reporting.
- Improved Fitting Report Excel grouping and formatting by resolved package and fitting category. Components without a package are shown under `NO PACKAGE ASSIGNED`.
- Improved package lookup for assembly, member, type, readable, and numeric-reference values. Field Material Report uses the Assembly Register naming rule; loose field material can traverse Victaulic rigid/flex couplings to reach a connected pipe's owning assembly.
- Added deeper developer diagnostics for shaped-branch topology, continuous-top capability, geometry fallback, and validation analysis.
- Expanded Fabrication STEP handling for current complex shaped-branch, connection, selection, and topology cases.

The current build also includes the following 1.17.6 Fabrication STEP performance changes:

- Added a command-scoped Fabrication STEP cache for repeated family classification, type lookup, pipe dimensions, a bounded source-solid cache, element bounds, element centres, display names, and transparent-connection traversal.
- Restricted the bounded document pipe fallback so full ID/OD resolution runs only for nominal sizes requested by connectors in the selected assembly.
- Reused straight-pipe axes and shaped-branch connector directions during spatial header resolution instead of recalculating them for every pipe/branch comparison.
- Reused recently extracted family solids and calculated extents during branch, flange, reducer, and fitting processing without changing the resulting geometry.
- Removed the separate Fabrication STEP validation CSV; STEP export now saves only the verified STEP file.
- Kept Revit API processing single-threaded because Autodesk Revit document and geometry APIs are not safe to run in parallel.
- Preserved the verified SET-ON branch, side-coupling, flange, reducer, worksharing, inspection-view, and delayed-save outputs from 1.17.5.

The current build also includes the following 1.17.5 fabrication reliability changes:

- SET-ON shaped branches can now resolve a selected large header even when the family header connector is omitted or left unconnected.
- Header openings are limited to the near-side wall and retain the circular or elliptical shape required by the branch axis.
- The shaped branch is trimmed flush to the header outside surface, so no vertical sleeve remains inside the main pipe.
- A final post-trim bore cleanup removes thin saddle lips and keeps the branch-side opening circular.
- Explicit `STD WT-CS` adjustable branches can use the controlled standard-weight carbon-steel dimension table when no branch pipe is included. This is recorded as Information rather than a Warning after valid OD, ID, and wall thickness are confirmed.
- Carbon flanges connected to a resolved shaped branch can inherit the verified branch bore dimensions.
- Existing worksharing freshness, custom-dialog, selected-scope, inspection-view, validation, and verified STEP-save controls remain active.

## Revit Plugin User Functions

The ParallelSystems ribbon is created when Revit starts. Protected commands become available after the current Revit username is authorized.

### Property Mapping Panel

**Map Pipe End Prep**

- Processes pipe elements visible in the active view.
- Reads the connected elements at both pipe ends.
- Writes the configured End 1, End 2, and Pipe End Prep parameters.
- Uses the configured mapping table, ignored-component rules, and unconnected value.

**Clear Pipe End Prep**

- Clears the configured pipe End 1, End 2, and Pipe End Prep values from visible pipes in the active view.

**Map Fittings End Prep**

- Processes fitting families allowed by Configurations > Fittings End Prep.
- Uses connector and connected-element evidence to populate the configured fitting parameters.
- Applies the allowed-family list, ignored-component list, end-prep mapping table, and unconnected value.

**Clear Fittings End Prep**

- Clears the configured fitting mapping parameters from eligible fittings in the active view.

**Pipe Weight**

- Uses the configured pipe type, size, length, system abbreviation, fluid density, insulation, cladding, and weight tables.
- Writes the configured dry, wet, cladding, insulation, fluid, total, and computed overall-size values.
- Processes supported visible elements according to the current mapping configuration.

**Clear Pipe Weight**

- Clears the configured pipe-weight output parameters from supported elements in the active view.

The Header ND command classes remain in the source, but the Header ND split button is commented out and is not an active ribbon command in this build.

### Procurement Panel

**Export BOM**

- Runs the report types enabled under Configurations > Procurement > Reports.
- Current report options are Assembly Register, Cut List, Fitting Report, Loading Report, Pipe Report, Label Report, and Field Material Report.
- Uses active-view or project content according to the individual report implementation.
- Writes output to the configured target folder.
- Uses the Excel/PDF output selection in Configurations. Only the selected report format is generated.
- Uses the configured stock length, blade thickness, negative allowance, and reusable-offcut threshold for Cut List and Pipe Report calculations.
- Skips an enabled report when no qualifying data exists without cancelling the other selected reports.

**Filter Items**

- Opens from the Export BOM split button.
- Groups BOM components visible in the active view by component family/type and shows their quantities.
- Temporarily hides every matching instance for unchecked groups, allowing report scope to be reviewed before export.
- Changes temporary view visibility only; it does not delete or modify the BOM components.

**Publish BOM**

- Creates the configured drawing-register CSV in the Publish Site location.
- Applies the current sheet publishing rules.
- Can export eligible sheets to PDF and image output when enabled.
- Uses revision and checksum information to avoid unnecessary repeated publishing.
- Supports the existing background cloud-model publishing workflow.

### Fabrication Panel

The Fabrication panel is implemented as one split button with three commands.

**Fabrication STEP**

- Requires authorization.
- Native STEP export is available only in Revit 2025 or newer.
- Accepts one selected Revit assembly or explicitly selected pipe, pipe fitting, and pipe accessory elements.
- Rejects selections containing more than one assembly so separate spools are not combined accidentally.
- Exports only supported members of the selected assembly or the explicitly selected supported elements. Connected elements may be read to resolve dimensions and connection intent, but they are not added to the STEP unless they are part of the selection.
- Runs a worksharing preflight before geometry processing. Elements that are newer or deleted in Central block export until Reload Latest or Synchronize with Central is completed.
- Uses the custom Parallel Systems dialog for preflight blocking details and for confirmation when selected source elements are owned by another user. The command never automatically synchronizes, reloads, or checks out those source elements.
- Reads pipe nominal diameter, outside diameter, inside diameter, and wall thickness from built-in values, supported parameters, directly connected pipes, and family geometry when a family-specific rule permits it.
- Limits final model-size fallback searches to a padded area around the selected fabrication scope instead of resolving every pipe in a large Revit project.
- Creates hollow pipe and fitting geometry and blocks unresolved or unsafe geometry instead of guessing.
- Applies the approved butt-weld rule where applicable: a 30-degree bevel measured from the end face with a 1 mm root face.
- Keeps flange joints, tap-half coupling joints, and copper capillary reducer joints plain-ended.
- Supports flange-to-flange through bores, SET-ON shaped branches, tap-half side couplings, concentric butt-weld reducers, and plain-end copper capillary concentric reducers.
- For SET-ON shaped branches, the physical branch outlet axis is intersected with the selected header cylinder. This supports adjustable families whose header connector is unconnected or omitted.
- Cuts the required branch opening through the near-side header wall only; the opposite wall is preserved. The opening remains circular or elliptical according to the branch angle and curved header surface.
- Trims the shaped branch flush to the header outside surface, then performs a final coaxial bore cleanup so no sleeve, saddle lip, or thin membrane blocks either flow path.
- When no branch pipe supplies outlet dimensions, an explicitly classified `STD WT-CS` family can use the controlled standard-weight carbon-steel table. A successful controlled lookup is reported as Information and does not count as a warning.
- Allows a connected carbon flange to inherit verified outlet dimensions from the shaped branch when the flange family does not expose an independent inside diameter.
- Skips weld-gap and non-connector helper families from the STEP while traversing through them to the actual connected component.
- Creates a dedicated Fine-detail Revit 3D inspection view and one Generic Model DirectShape per generated source element.
- Configures the inspection view from the generated fabrication geometry, not oversized source-family reference geometry.
- Exports only the visible generated DirectShapes in the bounded inspection view.
- Rolls back the inspection model when generation or STEP verification fails.
- Retains and automatically opens the inspection view only after the STEP has been generated and verified successfully.
- Automatically frames and zooms to the generated fabrication elements.
- Shows the final Save dialog only after there are no blocking issues and the temporary STEP exists and is non-empty.
- Saves only the verified STEP file. Validation issues remain available in the custom detailed result dialog.

Typical required data is:

```text
Outside Diameter
Inside Diameter
```

or:

```text
Outside Diameter
Wall Thickness
```

When the required information cannot be resolved, the command reports the affected element and missing value through the custom detailed dialog. Do not release an exported file for fabrication until the retained inspection view and downstream STEP geometry have been sectioned and checked against the approved pipe and fitting specification.


**Export Diagnostics**

- Developer-use command for investigating fabrication families, dimensions, connectors, physical geometry, and connection rules.
- Accepts preselected components or Revit assemblies and expands assembly members into the diagnostic scope.
- Adds direct connector context up to three connection hops without modifying or checking out model elements.
- Exports one compact JSON file containing document and sheet context, source identity, instance and type parameters, connector coordinate systems and references, worksharing state, locations, transforms, materials, bounding boxes, cylindrical-face candidates, edge loops, and triangulated selected-element geometry.
- Uses the opened sheet number and name for the default JSON filename. If the active view is not placed on a sheet, the active-view name is used.
- Is read-only and available in Revit 2021 through 2026.

**Show Ready**

- Uses the current active graphical model view.
- Finds source pipes, fittings, and accessories recorded as successfully exported on the current workstation.
- Stores processed-source status outside the Revit model under `%LOCALAPPDATA%\Parallel Systems\FabricationStep\Processed`.
- Reads retained or legacy Fabrication STEP DirectShape source metadata for compatibility.
- Runs a worksharing preflight against the active view before applying temporary isolation.
- Blocks stale or other-user-owned active views before Revit attempts to modify temporary visibility.
- Restricts the search to supported source elements visible in the active view.
- Replaces any existing temporary isolate and temporarily isolates the processed source elements.
- Does not create or switch to another status view.
- Use Revit's built-in Reset Temporary Hide/Isolate command to restore the full view.
- Sheets, schedules, templates, and non-graphical views are not valid for this command.

### Settings Panel

**Configurations**

The Configurations window contains five active tabs.

**Pipe End Prep**

- Map Parameters: End 1, End 2, Pipe End Prep, unconnected value, and Enable Mapping.
- Ignore Components: name-contains rules.
- Pipe End Prep: name-contains to end-prep value mapping.

**Fittings End Prep**

- Map Parameters for the supported fitting outputs, including the configured Header ND field used by fitting logic.
- Ignore Components.
- Apply Mappings To: allowed fitting family/name rules.
- Fittings End Prep: name-contains to end-prep value mapping.

**Pipe Weight**

- Map Parameters for dry, wet, cladding, insulation, fluid, total, and computed overall-size values.
- Decimal precision.
- Material Properties for cladding thickness, cladding density, and insulation density.
- System Abbreviation and fluid-density mappings.
- Pipe Category table containing size, pipe type, dry weight, and wet weight.

**Procurement**

- Project Details: company logo, client logo, job number, and job name.
- When both job fields are empty as the window opens, Project Details reads the Revit Project Number and Project Name automatically. The Auto Detect button remains available for manual refresh.
- Company and client logo selection starts in `%ProgramData%\Parallel Systems\Images` when that directory exists; otherwise the standard Windows file-picker location is used.
- Publish Details: Publish Site, file name, PDF option, and image option.
- Output Details: target folder, report date, cut-list maximum length, blade thickness, negative allowance, and reusable-offcut threshold.
- Reports: Assembly Register, Cut List, Fitting Report, Loading Report, Pipe Report, Label Report, Field Material Report, Accessory Report, and Include Site Measure. Select Excel or PDF under Output Format.
- Clicking a report reveals its available report-specific settings. Fitting Report currently offers `Include Weld`; when disabled, weld rows are omitted.
- Fitting Report groups PDF and Excel results by resolved package. Named packages are ordered first and `NO PACKAGE ASSIGNED` is always last. Equal-size tees use their `BOM DESCRIPTION NAME` value when populated.
- Loading Report orders rows within each package by length from smallest to largest.

**Tools**

- Elevation Check: allowed slope angles and tolerance.
- Sheet Check and BOM Check: include and exclude text.
- Renaming: CSV file path.
- Pipe Length Check: maximum, too-long, and too-short thresholds.
- End Prep Check: filter values and display colours.

Configuration is stored by the existing plugin configuration module. Restart Revit when a ribbon tooltip must be rebuilt using newly changed parameter names.

### Tools Panel

**Elevation Check**

- Checks pipe slope conditions against the configured allowed angles and tolerance.
- Reports or highlights pipe elements that do not satisfy the configured rules.

**Renaming > Export CSV**

- Exports the supported assembly naming data to the configured CSV workflow.

**Renaming > Import CSV**

- Reads the reviewed CSV and applies the supported assembly naming changes.

**Sheet Number Check**

- Checks sheet numbering using the configured include and exclude text rules.
- Opens the controlled correction workflow for detected sheet-number issues.

**Pipe Length Check > Apply Pipe Filter**

- Creates or applies active-view filters for maximum-length, too-long, and too-short pipe conditions.

**Pipe Length Check > Clear Pipe Filter**

- Removes the plugin-generated pipe-length filters from the active view.

**End Prep Check > Apply End Prep Filter**

- Applies the configured active-view colour filters for end-prep values.

**End Prep Check > Clear End Prep Filter**

- Removes the plugin-generated end-prep filters from the active view.

**BOM Check**

- Checks current project data for duplicate or invalid BOM-related entries using the configured rules.

The Detailing command class remains in the source, but no Detailing panel is created by App.BuildRibbon in this build.

### About Panel

**About & Manual**

- User Manual displays this installed guide.
- What's New displays the installed CHANGELOG.md.
- About displays the product description, Version 1.17.8, Internal / Unreleased status, and assembly build timestamp.
- Open Full Manual opens the installed UserManual.md.
- Open Change Log opens the installed CHANGELOG.md.
- Developer Notes opens the installed DEVELOPERS.md.
- The displayed version removes any generated +GitRevision suffix defensively.

**About Us**

- Opens the Parallel Systems website in the default browser.

## Startup and Authorization

Authorization is separate from timesheet checkpoint delivery. Startup and Reconnect normally use the configured authorization server. If the `PARALLEL_SYSTEMS_DEVELOPMENT_MODE` environment variable is set to `1` when Revit starts, the plugin asks for the development password. An incorrect password keeps the prompt open for another attempt. Choosing `Do not enable development mode` or closing the prompt continues with normal authorization. A successful entry enables protected commands without server authorization and prevents the timesheet tracker from starting for that Revit session. Closing Revit clears the result, so the password is required again the next time development mode is requested. Tracker settings cannot enable development mode.

When development mode is active, the ParallelSystems ribbon displays a disabled `DEVELOPMENT MODE ACTIVE / NO TRACKING` indicator so the session state remains visible.

The normal startup flow is:

1. Revit starts and creates the ribbon.
2. The plugin loads the authorization BaseUrl from:

```text
%PROGRAMDATA%\Parallel Systems\Timesheet\tracker.settings.json
```

3. Authorization begins from Revit Idling.
4. The HTTP request runs asynchronously so a slow or sleeping server does not block the Revit user interface.
5. The current Revit username is checked through the configured authorization endpoint.
6. A successful allow response starts protected commands and the timesheet service.
7. An explicit allow=false response denies access.
8. Temporary timeout, network, or retryable server failures are scheduled for another authorization attempt.
9. Authorization diagnostics are written to:

```text
%APPDATA%\Autodesk\Revit\Addins\<RevitYear>\ParallelSystemPlugin\Logs\Authorization.log
```

The current source uses a 75-second request timeout and schedules another authorization cycle after a temporary failure. If Revit starts completely offline, tracking cannot start until authorization succeeds. Checkpoints created after authorization are protected by the local Outbox when later connectivity is interrupted.

## Silent Timesheet Tracking

### No Drafter Timesheet Form

Drafters do not manually enter Area, Level, Zone, System, Task Category, Status, client, or timesheet details. The tracker collects available Revit evidence automatically. Missing or unclear values remain uncategorized for administrator review.

### Session and Checkpoint Behaviour

A work session starts or changes when the authorized tracker initializes with an open document, a document is opened, the active document changes, or the active view changes.

A meaningful checkpoint is created only after tracked duration or model-change evidence exists. The default checkpoint interval is 60 seconds. Opening a model and immediately checking the web application may not create a row yet.

The production Revit tracker sends:

```text
Tracker schema version: 3
Plugin version: 1.17.8
```

The backend accepts tracker schema versions 2 and 3 for deployment compatibility.

### Time Definitions

- Measured Active Time: Revit is the foreground application and recent user input is within the active threshold. Default: 90 seconds.
- Engaged Project Time: Revit remains in the foreground and activity is within the engaged grace period. Default: 300 seconds.
- Foreground Time: Revit is the foreground Windows application.
- Inactive Time: sampled time that does not qualify as measured active or engaged.

These are monitoring measurements. They are not automatically payroll time and are not automatically approved client invoice hours.

### Automatically Captured Evidence

The tracker can send compact summarized evidence such as:

- installation identity, machine name, Windows username, Revit username, Revit version, and plugin version
- project key, project name, project number, document title, cloud model identity, and hashed document-path evidence
- view ID, view name, view type, sheet number, view template, discipline, and sub-discipline
- detected Area, Level, Zone, System, Activity, Scope, and Status when available
- summarized counts by modified category, Level, System, Area, Zone, Workset, and Revit transaction name
- created, modified, deleted, and uninspected element counts

The tracker does not intentionally upload screenshots, keystroke contents, unrelated application contents, or a permanent list of individual modified element IDs.

### Local Outbox and Offline Behaviour

Every checkpoint is written to a local JSON file before upload.

Default root:

```text
%LOCALAPPDATA%\Parallel Systems\Timesheet
```

Contents:

- Outbox: pending valid checkpoints.
- Failed: permanently rejected, unauthorized, forbidden, not-found, conflict, or malformed requests requiring review.
- Overflow: oldest pending files moved aside when MaxPendingMessages is exceeded.
- tracker.log: synchronization diagnostics.
- installation.id: stable local installation identity.

When connectivity fails after authorization:

- Revit work continues.
- Pending messages stay in Outbox.
- The sender retries pending files oldest-first on later flushes and future Revit starts.
- Accepted and duplicate acknowledgements remove the local message.
- Backend MessageId idempotency and cumulative session values prevent normal retries from duplicating duration.
- A schema-version rejection remains queued because it can be caused by backend/plugin deployment order.
- Failed and Overflow files require deliberate review or recovery; they are not silently deleted.

The Outbox improves offline reliability, but it cannot guarantee recovery from every possible disk failure, profile deletion, file corruption, or workstation loss.

Tracker connection settings are stored at:

```text
C:\ProgramData\Parallel Systems\Timesheet\tracker.settings.json
```

Supported environment overrides:

```text
PARALLEL_TIMESHEET_API_URL
PARALLEL_TIMESHEET_API_KEY
```

## Administrator Web Functions

The React application currently exposes Dashboard, Timesheets, and Task Mapping in the main sidebar. Documents, Forms, and Staff Information routes exist only as placeholder pages and are not current operational modules.

### Dashboard

- Shows project, user, duration, synchronization, and mapping summary information.
- Shows recently synchronized Revit sessions.
- Refreshes periodically while the page is open.
- Highlights sessions that still need Task Mapping.

### Task Mapping

1. Select the project and date range.
2. Filter synchronized Revit evidence.
3. Assign a Task Category manually or create a reusable rule.
4. Rules assign Task Category only.
5. Select categorized sessions.
6. Assign the correct client manually.
7. Clear or change client assignment when required.

Current evidence filters and rule inputs can use source fields supported by the web form and backend, including project/document/view information, view template, discipline, sub-discipline, Area, Level, Zone, System, Activity, Scope, Status, category, Workset, transaction evidence, mapping state, Task Category, and client assignment.

Task Category must never automatically assign a client. The same Task Category can be billed to different clients. Manual classification and manual client assignment remain separate administrator decisions.

### Timesheets

- Requires a project and date range for the intended billing workset.
- Shows client-assigned Engaged Project Time.
- Breaks client totals down by Task Category.
- Shows unassigned Engaged Project Time requiring client assignment.
- Provides billing-session details and additional evidence filters.
- Keeps Time by Person as an internal monitoring view instead of the primary client-facing summary.

## Deployment and Developer Information

The source solution is separated into:

- RevitPlugin: Revit 2021-2026 adapters, ribbon tools, reports, configuration, Fabrication STEP, authorization, and silent tracker.
- Shared: tracker request and response contracts.
- Backend: ASP.NET Core API, authorization, tracker ingestion, PostgreSQL access, Task Mapping, billing, and Timesheets endpoints.
- Frontend: React monitoring application.
- scripts: local start/stop, tracker configuration, test checkpoint, and failed-message recovery.
- docs: architecture, mapping, policy, deployment, validation, migration, and test guidance.

The monitoring architecture is V2, while the Revit plugin release is 1.17.8.

No MSMQ, old Activity Queue Service, or central workstation SQLite database is part of the active architecture.

## Troubleshooting

### Commands Show Authorization Pending or Access Denied

- Confirm tracker.settings.json contains the correct ApiBaseUrl.
- Confirm the URL is reachable from the workstation.
- Allow a sleeping cloud service time to wake.
- Check Authorization.log for URL, timeout, HTTP status, or response errors.
- Confirm the exact Revit username is allowed by the server.
- An explicit server denial is different from a temporary connectivity failure.

### Project Does Not Appear in Task Mapping

- Confirm authorization succeeded.
- Confirm tracking is enabled in tracker.settings.json.
- Work in Revit beyond one checkpoint interval.
- Keep Revit in the foreground during part of the test.
- Change view or save the document to trigger meaningful session activity.
- Confirm the API health endpoint is available.
- Confirm the Tracker API key matches the backend.
- Check tracker.log, Outbox, Failed, and Overflow.
- Confirm the backend accepts schema version 3.

### Outbox Keeps Growing

- Test API health and workstation internet access.
- Verify ApiBaseUrl and TrackerApiKey.
- Check tracker.log for the response status.
- Do not delete Outbox files until the corresponding data is confirmed in PostgreSQL.
- Review Failed and Overflow separately.
- Use the recovery script only after the backend compatibility problem has been corrected.

### Fabrication STEP Is Blocked

- Use Revit 2025 or 2026.
- In a workshared model, run Reload Latest or Synchronize with Central when the preflight reports that a selected source element is updated or deleted in central.
- When another user owns a selected element, continue only when exporting the current local snapshot is acceptable.
- Select one assembly only, or select supported pipes/fittings/accessories directly.
- Review the blocking and warning details in the custom Fabrication STEP result dialog.
- Confirm actual outside diameter and either inside diameter or wall thickness can be resolved.
- Connect fitting-only selections to pipework with an unambiguous pipe specification, or provide approved fitting dimensions.
- Inspect reducers, tees, crosses, elbows, chamfers, flange joints, and internal bores in section before release.
- `Information` entries document a deterministic resolver or family-specific path and do not block export.
- A validated `STD WT-CS` shaped-branch lookup is informational. It becomes blocking only when the classification, nominal size, or resulting dimensions cannot be resolved safely.
- Do not bypass a blocking geometry message by inventing a value.

### Show Ready Finds Nothing

- Open a graphical view containing the original processed source elements.
- Confirm a Fabrication STEP was generated and saved successfully on the current workstation.
- Confirm `%LOCALAPPDATA%\Parallel Systems\FabricationStep\Processed` is writable.
- Legacy DirectShape metadata is still recognised. Successful current exports retain the dedicated inspection view and generated DirectShapes; failed exports roll them back.
- Source elements outside the current view are intentionally ignored.

### About Shows the Wrong Version

- Confirm Revit was closed before deployment.
- Rebuild the adapter matching the installed Revit year.
- Replace both the deployed DLL and PDB.
- Confirm Directory.Build.props contains Version and InformationalVersion 1.17.8.
- Confirm the installed Docs folder was copied beside the DLL.
- Restart Revit.

## Support

Website: [parallelsystems.com.au](https://www.parallelsystems.com.au/)
