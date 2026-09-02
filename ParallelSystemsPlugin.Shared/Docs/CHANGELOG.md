# Changelog

## 1.17.8 - Controlled Development Mode (Internal / Unreleased)

- Added password-protected Development Mode gated by the per-user `PARALLEL_SYSTEMS_DEVELOPMENT_MODE=1` environment variable.
- Development Mode bypasses server authorization and protected-command checks, deliberately leaves timesheet tracking disabled, and resets when Revit closes.
- Added a topmost password prompt with retry handling, an explicit `Do not enable development mode` action, Parallel Systems branding, and a persistent `DEVELOPMENT MODE ACTIVE / NO TRACKING` ribbon indicator.
- Added enable and disable command files plus an internal Word guide under `Development Tools`.
- Successful normal-mode Reconnect authorization now starts the same authorized services as startup, including timesheet tracking and Outbox flushing.
- Updated Procurement configuration to auto-detect the Revit Project Number and Project Name when both saved fields are empty, use a larger resizable layout, and reveal report-specific settings when a report is clicked.
- Added the Fitting Report `Include Weld` option. Fitting Report PDF and Excel output now group by resolved package instead of material grade, place `NO PACKAGE ASSIGNED` last, and use `BOM DESCRIPTION NAME` for equal-size tees when the parameter is populated.
- Updated Loading Report rows to sort by length from smallest to largest within each package.
- Logo selection now opens `%ProgramData%\Parallel Systems\Images` by default when the directory exists. The installers deploy the bundled Parallel Systems, Climatech, and Brown Moodie images to that shared location.
- Iterated product, assembly, file, informational, splash, tracker fallback, User Manual, developer notes, and changelog versions to 1.17.8.

## 1.17.7 - Procurement Reporting and Fabrication Diagnostics (Internal / Unreleased)

- Added `Filter Items` beside Export BOM so users can review active-view BOM components by family/type and temporarily hide unchecked groups before validating or exporting reports.
- Reworked Procurement configuration into a larger resizable layout, added an explicit Excel/PDF output choice, made Excel and exclusion of site-measured work the new defaults, and added a configurable reusable-offcut threshold.
- Updated Cut List output ordering, package grouping, assembly-mark resolution, total-waste calculation, site-measure matching, and Excel layout.
- Updated Pipe Report stock quantities to separate reusable partial lengths into an `OFFCUT` column and label the configured stock length in the quantity heading.
- Updated Fitting Report Excel output to group by resolved package and fitting category, apply category-specific formatting, and show unassigned packages explicitly.
- Improved package-name resolution across assemblies, types, members, and numeric `Vic_Package` references. Field Material Report now uses the same assembly-name package rule as Assembly Register; a loose field-material fitting follows either connector through bounded chains of Victaulic rigid/flex couplings to a connected pipe and uses that pipe's owning assembly.
- Standardized procurement Excel headers, group bands, project-phase display, red exclusion notes, widths, and report-specific PDF/Excel generation.
- Added continuous-top capability diagnostics and expanded fabrication JSON/STEP diagnostics for complex shaped-branch topology, surface continuity, validation, and fallback analysis.
- Expanded Fabrication STEP branch geometry and topology handling, connection resolution, selection support, preflight behavior, and generation diagnostics for current family cases.
- Iterated product, assembly, file, informational, splash, tracker fallback, User Manual, developer notes, and changelog versions to 1.17.7.

## 1.17.6 - Fabrication STEP Large-Package Performance Optimisation (Internal / Unreleased)

- Added a developer-only `Export Diagnostics` command under the Fabrication split button. It exports one compact JSON file containing selected components, bounded connector context, all instance/type parameters, connectors, worksharing state, transforms, physical geometry, edge loops, meshes, and cylindrical-face candidates.
- Based the default diagnostics filename on the opened sheet number/name, with active-view and document fallbacks.
- Kept the diagnostics command read-only and available across Revit 2021-2026.
- Removed the separate validation CSV from Fabrication STEP output; successful generation now saves only the verified STEP file while validation remains in the custom detailed dialog.
- Added a command-scoped cache for repeated fabrication classification, family/type lookup, pipe dimensions, a bounded extracted-solid cache, element bounds, centres, extents, display names, and transparent-connection traversal.
- Reused source geometry only within the current generation command and cleared it before returning, preventing stale Revit data from leaking into later exports.
- Filtered the bounded nearby-pipe fallback by the nominal connector sizes actually required by the selected assembly before performing full ID/OD resolution.
- Cached straight-pipe axes and precomputed shaped-branch connector axes so packages with many selected pipes do not repeatedly read and normalize the same Revit locations for every branch candidate.
- Avoided the first validation-report write on successful runs; successful exports now write the final CSV once.
- Preserved generated STEP geometry, selected scope, worksharing preflight, custom dialogs, retained inspection view, and verified save behaviour.
- Kept all Revit API and geometry work single-threaded because Revit document and geometry APIs are not thread-safe.
- Corrected the release-source mismatch where documentation identified 1.17.5 while assembly, splash, and tracker fallback values still reported 1.17.4.
- Iterated product, assembly, file, informational, splash, tracker fallback, User Manual, developer notes, changelog, and fabrication documentation to 1.17.6.

## 1.17.5 - SET-ON Shaped-Branch Bore Reliability and Validation Cleanup (Internal / Unreleased)

- Fixed SET-ON shaped branches whose header connector is unconnected or omitted by resolving the selected header from the physical branch-axis intersection.
- Added controlled outlet-dimension resolution for explicitly classified `STD WT-CS` adjustable branches when no branch pipe is present in the selected assembly.
- Allowed connected carbon flanges to inherit verified shaped-branch outlet dimensions instead of failing with unresolved inside-diameter messages.
- Restricted the header opening to the near-side wall while preserving the full circular or elliptical opening required by the branch connection.
- Trimmed shaped-branch material flush to the header outside surface so no branch sleeve protrudes into the main pipe bore.
- Added a post-trim coaxial bore cleanup pass to remove saddle lips or thin membranes exposed by adjustable multi-solid branch families.
- Kept the main header bore smooth end to end and kept the shaped-branch outlet bore circular when viewed from the branch side.
- Changed the validated `STD WT-CS` table fallback from Warning to Information because it is deterministic and does not require corrective action; blocking behaviour remains unchanged for unresolved or unsafe dimensions.
- Preserved worksharing freshness checks: elements newer or deleted in Central still require Reload Latest or Synchronize with Central before export.
- Iterated product, assembly, splash, tracker fallback, User Manual, developer documentation, and fabrication documentation to version 1.17.5.

## 1.17.4 - Fabrication STEP Geometry, Worksharing Safety, Architecture, and Performance (Internal / Unreleased)

- Split Fabrication STEP into focused partial files for selection, preflight, status, generation, dimensions, connections, pipe geometry, fitting geometry, reducers, chamfers, branches, classification, and shared geometry utilities.
- Added worksharing preflight checks for stale central-model elements, deleted-in-central elements, read-only documents, source ownership, and active-view ownership before temporary isolation.
- Routed Fabrication STEP, Show Ready, preflight, warning, confirmation, success, and failure messages through the custom `AppDialog`; native Revit `TaskDialog` and standard message boxes are not used by the fabrication workflow.
- Restricted generated STEP geometry to the selected assembly members or explicitly selected supported pipes, fittings, and accessories.
- Replaced the project-wide pipe-dimension fallback scan with a bounded search around the selected fabrication scope while retaining direct connected-network resolution.
- Built inspection-view section boxes from the generated DirectShapes instead of source-family reference geometry, then automatically framed and zoomed to the exported fabrication elements.
- Limited Generic Model filtering to the new inspection view and its active section box instead of scanning all Generic Models across a large project.
- Retained and opened the dedicated Revit inspection view only after a verified successful STEP export; failed exports roll back their temporary geometry and view.
- Added verified geometry rules for weld skipping, flange-to-flange through bores, SET-ON shaped branches, tap-half side couplings, concentric butt-weld reducers, and plain-end copper capillary reducers.
- Added complete bore continuity through shaped-branch and side-coupling saddles so no thin internal diaphragm remains.
- Preserved delayed saving: the destination Save dialog is shown only after generation succeeds without blocking issues and the temporary STEP file is verified as non-empty.
- Iterated the Revit plugin, About, splash screen, tracker fallback, User Manual, developer notes, and documentation version to 1.17.4.

## 1.17.3 - Stability, Compatibility, Offline Reliability, Fabrication Documentation, and Versioning (Internal / Unreleased)

- Set the Revit plugin product version, assembly version, file version, and informational version to 1.17.3.
- Prevented the SDK-generated Git source-revision suffix from appearing in the user-facing About version.
- Added a defensive About version formatter that removes a +revision suffix and a trailing .0 assembly field.
- Added Internal / Unreleased status to the About screen.
- Added server-side authorization checks before protected commands and tracking services start.
- Moved authorization HTTP work off the Revit UI thread and added retry behaviour for temporary service, timeout, and network failures.
- Added authorization diagnostics under the installed add-in Logs folder.
- Added tracker schema version 3 while preserving backend compatibility with schema version 2.
- Fixed rejected compatible Revit checkpoints not reaching PostgreSQL.
- Preserved schema-version deployment-order failures in Outbox instead of treating them as permanently corrupt.
- Added recovery tooling for previously rejected Outbox messages.
- Documented Outbox, Failed, Overflow, tracker.log, retry order, duplicate acknowledgements, and reliability limitations.
- Added and documented the current Fabrication ribbon panel from the authoritative source.
- Added a worksharing preflight that blocks stale central-model elements and uses the custom application dialog to confirm read-only export of elements owned by another user.
- Split the Fabrication STEP subsystem into selection, status, generation, dimensions, connections, pipe, fitting, reducer, chamfer, branch, classification, and geometry partial files.
- Changed STEP generation to use a temporary transaction-group model that is rolled back after verified export, preventing mass view checkout and persistent DirectShape/view pollution.
- Added local processed-source status storage for Show Ready while retaining compatibility with legacy DirectShape metadata.
- Documented Fabrication STEP selection, hollow-geometry generation, 30-degree bevel with 1 mm root face, plain flange joints, validation CSV, Revit 2025+ STEP export, and Show Ready temporary isolation.
- Rebased About, User Manual, What's New, Developer Notes, architecture, test, and package documentation onto the latest source without replacing newer authorization or fabrication code.
- Aligned the manual test-checkpoint plugin version with 1.17.3.

## 1.17.2 - Task Mapping and Client Billing Corrections

- Added Task Mapping based on automatically detected Revit evidence.
- Added evidence for project, document, view, view template, discipline, sub-discipline, Area, Level, Zone, System, Scope, Status, modified categories, worksets, and transaction names.
- Added reusable mapping rules that assign Task Category only.
- Removed invalid automatic Task Category-to-client mapping.
- Kept client assignment as a separate manual administrator action.
- Added manual bulk client assignment for categorized sessions.
- Added project and date-range selection.
- Added client billable-duration summaries by Task Category.
- Kept Time by Person as an internal monitoring view instead of the primary client view.
- Removed hardcoded sample clients and billing categories such as B&M and CLIMATECH from automatic seeding.
- Compacted Task Mapping filters into a responsive layout.

## 1.17.1 - Cloud Deployment and Web Application Fixes

- Fixed backend and frontend Docker and Render deployment issues.
- Added the missing DateRangePicker implementation and corrected frontend build imports.
- Fixed TimesheetsController query binding to use the complete project/date-range query.
- Registered BillingRepository correctly.
- Fixed the billing catalog HTTP 500 error.
- Added or corrected PostgreSQL schema initialization.
- Added project/date-range filtering and compact filter layouts.
- Added Render-compatible Docker deployment, health checks, environment-based secrets, and React hosting through the API service.

## 1.17.0 - Timesheet Monitoring and Solution Restructure

- Added silent Revit timesheet tracking.
- Restructured the solution into RevitPlugin, Backend, Frontend, Shared contracts, scripts, and deployment configuration.
- Connected the tracker to an ASP.NET Core API.
- Moved centralized storage to PostgreSQL.
- Added Render deployment support.
- Removed MSMQ and the old Activity Queue Service from the active architecture.
- Replaced workstation SQLite queuing with a lightweight local JSON Outbox.
- Added Measured Active Time, Engaged Project Time, Foreground Time, Inactive Time, and grace-period logic.
- Added automatic Revit evidence collection without drafter input.
- Added compact cumulative work sessions instead of permanent element-level activity rows.
- Added idempotent checkpoint processing so normal retries do not duplicate duration.
- Added data minimization: no screenshots, keystroke contents, or permanent individual-element activity list.

## 1.16.0 - Export BOM Excel Support

- Added support for exporting BOM data to Excel.
- Fixed BOM export data inconsistencies.

## 1.15.0 - .NET 8 Support

- Added .NET 8 support for current Revit releases.

## 1.14.0 - Tools - Sheet Number Check and BOM Check

- Added Sheet Number Check.
- Added BOM Check for duplicate or invalid BOM entries.

## 1.13.0 - Tools - End Prep Check

- Added active-view end-prep colour filters controlled by Configurations > Tools.

## 1.12.0 - Tools - Pipe Length Check

- Added active-view pipe-length filters controlled by Configurations > Tools.

## 1.11.0 - Tools

- Added Assembly Type renaming through CSV export/import.
- Added Elevation Check.
- Added tool configuration through Configurations > Tools.

## 1.10.0 - Procurement - Export BOM

- Added configurable PDF generation for BOM export.

## 1.9.0 - Pipe Weight

- Added pipe-weight configuration.
- Added dry, wet, cladding, insulation, fluid, total, and computed overall-size mappings.

## 1.8.0 - Settings

- Added the Configurations window.
- Added dynamic configuration for pipe mapping, fitting mapping, Pipe Weight, Procurement, and Tools.

## 1.7.0 - About and Manual

- Added the About and Manual dialog with User Manual, What's New, and About tabs.
- Added in-app version and build date.
- Added installed documentation links and the company website.

## 1.6.0 - Custom Dialogs

- Added styled WPF information, warning, error, confirmation, and progress dialogs.
- Added Revit owner-window handling.

## 1.5.0 - Header ND Tools

- Added Header ND mapping and clearing command classes.
- The Header ND ribbon split button is not active in the current authoritative source.

## 1.4.0 - Fittings End Prep

- Added Map Fittings End Prep.
- Added Clear Fittings End Prep.

## 1.3.0 - Pipe End Prep

- Added pipe End 1, End 2, and Pipe End Prep mapping.
- Added Clear Pipe End Prep.

## 1.2.0 - About Us Link

- Added the Parallel Systems website command.

## 1.1.0 - Initial Plugin

- First functional build of the Parallel Systems Revit add-in.
