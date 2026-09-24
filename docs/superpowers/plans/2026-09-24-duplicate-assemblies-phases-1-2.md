# Duplicate Assemblies Phases 1–2 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver tested, Revit-independent assembly naming/preflight logic and an internal one-assembly Revit diagnostic that proves source/target independence before any production workflow is built.

**Architecture:** Put parsing, ordering, numbering, substitution, and conflict checks in `ParallelSystemsPlugin.Core`, behind immutable models that do not reference Revit. Put the Phase 2 diagnostic in the shared Revit project as a command plus focused service; it copies member elements, creates a new `AssemblyInstance`, renames only the target, verifies source invariants, and rolls the entire operation back on any failed assertion.

**Tech Stack:** C#, .NET Standard 2.0 core library, .NET 8 console test harness, WPF/AppDialog, Autodesk Revit API 2021–2026, MSBuild shared projects.

**Spec:** `docs/superpowers/specs/2026-09-24-duplicate-assemblies-phases-1-2-design.md`

## Global Constraints

- Phase 1 and Phase 2 only; do not implement level reassociation, documentation, annotations, production ribbon UI, or multi-assembly processing.
- Never disassemble, rename, move, delete, or otherwise modify the source assembly, its type, members, views, or sheet.
- Revit 2021–2024 target .NET Framework 4.8; Revit 2025–2026 target .NET 8 Windows.
- Keep Revit-specific code in the shared plugin and pure logic in `ParallelSystemsPlugin.Core`.
- Keep version-specific behavior centralized; do not scatter conditional compilation through business logic.
- Reuse `AppDialog` and existing command/selection conventions.
- Add `// Created by Jhay` or `// Changed by Jhay` to relevant additions and modifications.
- Preserve unrelated working-tree changes, especially edits already present in `ParallelSystemsPlugin.LayoutTests/Program.cs` and `FabricationStepService.Selection.cs`.

## Review Focus

- Multiple numeric blocks: replace and sort by only the final block; `CHW-L02-007-A` becomes `CHW-L02-100-A`.
- Equal final numbers: order deterministically by source name and then stable element ID.
- Padding overflow: never truncate; `CHW999` at 1000 becomes `CHW1000`.
- Missing documentation token: leave the name unchanged and perform normal conflict validation.
- Failed Phase 2 invariants: roll back every target artifact if the target shares the source type, membership is incomplete, or source evidence changes.

---

### Task 1: Pure-Logic Test Harness and Final Numeric Parser

**Files:**
- Create: `ParallelSystemsPlugin.CoreTests/ParallelSystemsPlugin.CoreTests.csproj`
- Create: `ParallelSystemsPlugin.CoreTests/Program.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyNameParts.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyNameParser.cs`
- Modify: `ParallelSystemsPlugin.sln`

**Interfaces:**
- Produces: `AssemblyNameParser.TryParse(string sourceName, out AssemblyNameParts parts, out string error)`
- Produces: immutable `AssemblyNameParts` properties `SourceName`, `Prefix`, `NumericText`, `NumericValue`, `Suffix`, and `MinimumWidth`

- [ ] **Step 1: Write failing parser tests**

Create a `net8.0-windows` executable project referencing only `ParallelSystemsPlugin.Core`. Add a `Main` runner that exits nonzero on failure. Test `CHW001`, `CHW-50SM`, and `CHW-L02-007-A`, including every parsed field. Test blank, `CHW-MAIN`, and a numeric block larger than `Int64.MaxValue` as invalid.

- [ ] **Step 2: Run the test harness and verify RED**

Run `dotnet run --project ParallelSystemsPlugin.CoreTests/ParallelSystemsPlugin.CoreTests.csproj -c Debug`. Expected: compilation fails because `AssemblyNameParser` and `AssemblyNameParts` do not exist.

- [ ] **Step 3: Implement the minimal parser**

Scan backward for the last digit, expand backward to the beginning of that contiguous digit block, and parse with invariant culture into `long`. Return a clear error for blank, missing, or overflowing numeric blocks. Include `// Created by Jhay` on the new production types.

- [ ] **Step 4: Run the test harness and verify GREEN**

Expected: all parser tests print PASS and the process exits 0.

- [ ] **Step 5: Add the project to the solution and build it**

Run `dotnet sln ParallelSystemsPlugin.sln add ParallelSystemsPlugin.CoreTests/ParallelSystemsPlugin.CoreTests.csproj`, then build the test project. Expected: no warnings or errors.

- [ ] **Step 6: Commit the parser slice**

Stage only Task 1 files and commit with `test: add assembly name parser coverage`.

---

### Task 2: Numeric Ordering and Sequence Generation

**Files:**
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyNamingCandidate.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/ProposedAssemblyName.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyNamingService.cs`
- Modify: `ParallelSystemsPlugin.CoreTests/Program.cs`

**Interfaces:**
- Consumes: `AssemblyNameParser.TryParse(...)`
- Produces: `AssemblyNamingCandidate(string sourceName, long stableElementId)`
- Produces: `AssemblyNamingService.Generate(IReadOnlyCollection<AssemblyNamingCandidate> candidates, long startingNumber)` returning an ordered read-only list of `ProposedAssemblyName`
- Produces: result properties for source name/ID, parsed source number, assigned number, and proposed name

- [ ] **Step 1: Add failing ordering and generation tests**

Pin these results: `CHW001, CHW002, CHR111` at 500 becomes `CHW500, CHW501, CHR502`; `CHW110, CHR111, CHR112` at 500 retains numeric order; `CHW-50SM` at 60 becomes `CHW-60SM`; `CHW-L02-007-A` at 100 becomes `CHW-L02-100-A`; `CHW001` at 5 becomes `CHW005`; and `CHW999` at 1000 becomes `CHW1000`. Add equal-number candidates in reverse input order and assert name then stable-ID tie breaks. Assert one invalid source rejects the full request, and sequence overflow reports an error.

- [ ] **Step 2: Run the harness and verify RED**

Expected: compilation fails because the naming candidate, service, and result types do not exist.

- [ ] **Step 3: Implement minimal ordering and generation**

Parse every candidate before returning output. Sort by numeric value, source name with `StringComparer.OrdinalIgnoreCase`, then stable element ID. Format using the source minimum width and invariant culture. Use checked arithmetic.

- [ ] **Step 4: Run the harness and verify GREEN**

Expected: all parser, ordering, padding, and generation tests pass.

- [ ] **Step 5: Commit the naming slice**

Commit only Task 2 production and test changes with `feat: add deterministic assembly sequence generation`.

---

### Task 3: Safe Documentation Substitution and Conflict Preflight

**Files:**
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyNameSubstitution.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyConflictIndex.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyPreflightIssue.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyPreflightService.cs`
- Modify: `ParallelSystemsPlugin.CoreTests/Program.cs`

**Interfaces:**
- Produces: `AssemblyNameSubstitution.ReplaceExactToken(string documentName, string sourceAssemblyName, string proposedAssemblyName)`
- Produces: `AssemblyConflictIndex` initialized from existing assembly names, sheet numbers, and view names using ordinal-ignore-case sets
- Produces: `AssemblyPreflightService.ValidateNames(...)` returning every issue with `Kind`, `SourceName`, `ProposedName`, and `Message`

- [ ] **Step 1: Add failing substitution tests**

Assert `FAB-CHW001` becomes `FAB-CHW500`, `CHW001-SHEET` becomes `CHW500-SHEET`, and `PRD-CHW-50SM` becomes `PRD-CHW-60SM`. Assert `DETAIL-001` remains unchanged when the source token is `CHW001`. Require an ambiguity issue when the exact token occurs more than once rather than silently replacing every occurrence.

- [ ] **Step 2: Add failing conflict tests**

Cover existing assembly-name/type collisions, sheet-number collisions, view-name collisions, duplicate proposals within a batch, case-only collisions, and a clean request. Assert all issues are returned in one call.

- [ ] **Step 3: Run the harness and verify RED**

Expected: compilation fails for missing substitution and preflight types.

- [ ] **Step 4: Implement exact-token substitution and indexed validation**

Locate tokens with ordinal-ignore-case comparison. Replace exactly one occurrence, return unchanged when absent, and report ambiguity when repeated. Build conflict sets once and validate without repeated full-model scans.

- [ ] **Step 5: Run focused tests and verify GREEN**

Expected: every Phase 1 test passes.

- [ ] **Step 6: Run the existing layout harness**

Run `dotnet run --project ParallelSystemsPlugin.LayoutTests/ParallelSystemsPlugin.LayoutTests.csproj -c Debug -p:DeployToRevitOnBuild=false`. Expected: existing tests pass. Do not overwrite the user's current edits in that harness.

- [ ] **Step 7: Commit Phase 1 preflight**

Commit only Task 3 files with `feat: add assembly duplication name preflight`.

---

### Task 4: Phase 2 Evidence and Result Models

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticModels.cs`
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticReport.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces: `AssemblyEvidence.Capture(Document, AssemblyInstance)` containing instance ID, type ID, type name, sorted member IDs, and member-owner assembly IDs
- Produces: `AssemblyDuplicationDiagnosticResult` with success, summary, details, source-before/source-after/target evidence, and report path
- Produces: `AssemblyDuplicationDiagnosticReport.Write(...)`

- [ ] **Step 1: Add absent model references and verify RED**

Add the planned shared compile entries and a service contract that references the missing models. Build Revit 2021 with deployment disabled. Expected: missing-type compilation errors.

- [ ] **Step 2: Implement evidence capture and report serialization**

Use `RevitApiCompatibility.GetElementIdValue` for all IDs and sort IDs before comparisons. The report records source-before, source-after, target-after, every invariant result, Revit version, document title, UTC timestamp, and exception details. Default reports go under `%TEMP%\ParallelSystems\AssemblyDuplicationDiagnostic`.

- [ ] **Step 3: Build Revit 2021 and verify GREEN**

Expected: the project compiles without deployment.

- [ ] **Step 4: Commit diagnostic models**

Commit only Task 4 files with `feat: add assembly duplication diagnostic evidence`.

---

### Task 5: Transactional One-Assembly Duplication Diagnostic

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticService.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Consumes: evidence/result/report models and `RevitApiCompatibility`
- Produces: `AssemblyDuplicationDiagnosticService.Run(Document document, AssemblyInstance source, string targetName)`

- [ ] **Step 1: Write invariant validation before the copy operation**

Add validation that rejects a missing source, changed source type ID/name, changed source member IDs/ownership, identical source/target instance IDs, identical source/target type IDs, incorrect target name, target-member count mismatch, or copied members not owned by the target. Each error names the violated invariant.

- [ ] **Step 2: Build and verify RED**

Expected: compilation fails because the service calls a not-yet-implemented copy/create operation.

- [ ] **Step 3: Implement the transactional copy/create/rename sequence**

Capture source evidence; start a `TransactionGroup`; copy source member IDs in the same document with `ElementTransformUtils.CopyElements`, `Transform.Identity`, and `CopyPasteOptions`; commit and regenerate. Reject empty, mismatched, invalid, or already-assembled returned elements. Validate the copied IDs for assembly creation with the source `NamingCategoryId`. In a new transaction call `AssemblyInstance.Create`; commit and regenerate. In a third transaction set only `target.AssemblyTypeName`; commit and regenerate. Capture evidence, verify all invariants twice with regeneration between checks, and assimilate only on complete success. Any exception or failed invariant must roll back the group.

- [ ] **Step 4: Build all six adapters**

Run `.\Build\Build-All.ps1 -Configuration Debug -RevitVersion All -Deploy:$false`. Expected: Revit 2021–2026 compile and no Autodesk host DLL is copied. If an API signature differs, isolate only that call in one compatibility method.

- [ ] **Step 5: Inspect every failure path**

Confirm no catch commits partial work, group disposal rolls back before assimilation, and report-writing failure cannot turn a model failure into success.

- [ ] **Step 6: Commit the service**

Commit Task 5 files with `feat: add isolated assembly duplication diagnostic`.

---

### Task 6: Internal Diagnostic Command Without a Production Ribbon Button

**Files:**
- Create: `ParallelSystemsPlugin.Shared/Commands/RunAssemblyDuplicationDiagnosticCommand.cs`
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyInstanceSelectionFilter.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Consumes: `AssemblyDuplicationDiagnosticService.Run(...)`
- Produces: `ParallelSystemsPlugin.Commands.RunAssemblyDuplicationDiagnosticCommand`

- [ ] **Step 1: Add shared-project entries and verify RED**

Reference the two missing files in `.projitems` and build Revit 2021. Expected: missing-file/type errors.

- [ ] **Step 2: Implement exact-one assembly selection**

Use preselection only when it contains exactly one `AssemblyInstance`; otherwise use `PickObject` with a dedicated selection filter. Reject mixed/multiple preselection and return `Cancelled` on Revit selection cancellation.

- [ ] **Step 3: Implement command guards and reporting**

Require authorization, an active non-family document, and a unique diagnostic target name based on `CHW_TEST_100`. Show a branded warning that this writes a disposable test copy. Invoke the service and use `AppDialog.ShowDetailed` to report source/target IDs, type IDs, names, invariant results, and report path. Do not register the command in `ToolsMenu` or any production ribbon panel.

- [ ] **Step 4: Build all six versions**

Run the non-deploying all-version build. Expected: all adapters compile.

- [ ] **Step 5: Commit the command**

Commit Task 6 files with `feat: expose internal assembly independence diagnostic`.

---

### Task 7: Final Verification and Manual-Test Handoff

**Files:**
- Create: `docs/testing/duplicate-assemblies-phase-2-manual-test.md`
- Modify only if verification exposes a Phase 1/2 defect: files owned by Tasks 1–6, with a failing regression test first where possible

**Interfaces:**
- Produces: disposable-model procedure and evidence table for Revit 2021–2026

- [ ] **Step 1: Run the isolated Phase 1 harness**

Expected: exit 0 with named PASS output for parsing, ordering, padding, substitution, and conflicts.

- [ ] **Step 2: Run the existing layout harness**

Expected: exit 0. Report any pre-existing failure by exact test name without changing unrelated code.

- [ ] **Step 3: Build every Revit adapter without deployment**

Expected: six successful builds, no warnings introduced by this feature, and no Revit DLLs in output.

- [ ] **Step 4: Write exact manual Revit instructions**

For each version, use a disposable model copy, select one assembly, invoke the internal command through Revit's external-command/debug mechanism, record IDs and names, rename the target, confirm the source is unchanged, save, close, reopen, and repeat the identity checks. Include a six-version result table and an explicit stop condition for shared types or any source mutation.

- [ ] **Step 5: Confirm scope and working-tree isolation**

Run `git status --short` and `git diff --check`. Confirm no Phase 3–6 code or production ribbon registration was added and all pre-existing user changes remain intact.

- [ ] **Step 6: Stop and report**

Report files created/modified, tests and results, exact Revit APIs, independence guarantees, API deviations, and manual instructions. Do not begin Phase 3 until the user reports successful manual verification.
