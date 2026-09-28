# Production Assembly Duplication Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver a production multi-assembly command that previews deterministic conflict-free numbering, duplicates every source through the validated single-target engine, and commits only when the entire batch passes strict validation.

**Architecture:** Mechanically extract the proven single-target path into `AssemblyDuplicationEngine`, with callers owning the outer `TransactionGroup`. A pure numbering planner and read-only Revit preflight create one immutable confirmed plan; execution revalidates without renumbering and rolls the complete batch back on any failure.

**Tech Stack:** C#, .NET Standard 2.0 core, Revit 2021–2024 .NET Framework 4.8 adapters, Revit 2025–2026 .NET 8 Windows adapters, Autodesk Revit API, WPF code-behind, existing `AppDialog`.

**Spec:** `docs/superpowers/specs/2026-09-25-production-assembly-duplication-design.md`

**2026-09-26 policy override:** Complete target names, not numeric values, define occupancy. The preview defaults to starting number `0`, accepts per-row manual anchors, preserves requested and resolved numbers plus skipped full-name conflicts in the immutable plan, retains invalid source rows, and never renumbers during execution. This override supersedes older global-number and read-only-preview steps below without changing the locked engine or atomic batch execution.

## Global Constraints

- The Revit 2025 single-assembly result of 93 PASS / 0 FAIL is locked.
- Do not redesign or duplicate copying, matching, reassociation, translation, fitting/insulation handling, connector validation, offset/elevation validation, marker creation, transform alignment, aggregate validation, or rollback.
- The engine owns short phase `Transaction` objects but never a `TransactionGroup`.
- Production owns one outer group and commits no partial batch.
- The confirmed plan is immutable; revalidation may pass or fail but never renumber.
- Occupancy is global across prefixes. Skip numbers, never selected sources.
- Store production member IDs as sorted unique numeric values and compare by set equality.
- Family inspection is read-only; family loading occurs inside the outer group.
- Use existing WPF/AppDialog conventions and no new UI framework.
- Verification builds use `-p:DeployToRevitOnBuild=false`. Do not deploy or copy DLLs; the user performs builds that update the installed add-in.
- Do not commit unless explicitly requested. Preserve unrelated dirty files.
- Mark additions `// Created by Jhay` and relevant modifications `// Changed by Jhay`.
- Documentation, sheets, views, schedules, annotations, and cleanup beyond rollback remain future phases.

## Dependency Order

```text
1 Production-only verification baseline
 -> 2 Numbering and canonical IDs
 -> 3 Immutable Revit models
 -> 4 Read-only family inspection
 -> 5 Engine extraction and 93/0 gate
 -> 6 Preflight and immutable revalidation
 -> 7 Batch execution and report
 -> 8 WPF command
 -> 9 Production acceptance and final regression
```

Task 5 is a hard gate. Do not start Task 6 until the user confirms 93 PASS / 0 FAIL after extraction.

## Review Focus

1. Reordered Revit member enumeration must not make a plan stale; Tasks 2 and 6 pin canonical set comparison.
2. `Int64.MaxValue` exhaustion must return no partial assignment; Task 2 implements and statically reviews it.
3. Existing non-numeric names must remain in exact-name conflicts without creating numeric occupancy; Task 6 reviews the split indexes.
4. A reserved identity family with the wrong contract must fail read-only preflight; Task 4 centralizes the contract.
5. A late target failure must roll back earlier targets and batch-loaded family state; Tasks 7 and 9 prove it.

---

### Task 1: Establish Production-Only Verification

**Dependencies:** None.

**Files:**
- Modify: `ParallelSystemsPlugin.sln`

**Interfaces:**
- Keeps verification within the existing production plugin projects.
- Produces no test-only project or replacement test harness.

- [ ] **Step 1: Keep the solution production-only**

Confirm the solution contains only the existing production/shared projects relevant to this feature. Do not create or register a standalone unit-test project.

- [ ] **Step 2: Use the production verification gate**

For every applicable slice: build the applicable production project, require 0 compile errors, run `git diff --check`, and stop at the specified Revit manual gate when runtime validation is required.

---

### Task 2: Add Occupied-Number Planning and Canonical Member IDs

**Dependencies:** Task 1.

**Files:**
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyBatchNumberingPlan.cs`
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyBatchNumberingService.cs`
- Modify: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyMemberSetComparer.cs`

**Interfaces:**
- Produces: `AssemblyBatchNumberingService.CreatePlan(IReadOnlyCollection<AssemblyNamingCandidate>, long, IReadOnlyCollection<long>)` returning `AssemblyBatchNumberingResult`.
- Produces: `AssemblyMemberSetComparer.Canonicalize(IEnumerable<long>)` returning sorted unique IDs.

- [ ] **Step 1: Specify occupied-number behavior**

Implement existing values `500, 502`, start `500`, and three sources as assignments `501, 503, 504`, with skipped values `500, 502`. Cover consecutive occupancy, mixed prefixes, batch reservations, `CHW001 -> CHW005`, `CHW999 -> CHW1000`, and final-block-only replacement in production behavior and static review.

- [ ] **Step 2: Specify error behavior**

Require multiple invalid source names to produce multiple issues and `Plan == null`. Require one free assignment at `long.MaxValue` to succeed; occupied `long.MaxValue` or two required assignments starting there must fail with no partial plan.

- [ ] **Step 3: Specify canonicalization behavior**

Require `[9,3,7,3] -> [3,7,9]`, `[9,3,7]` to match `[7,9,3]`, and additions/removals to fail equality.

- [ ] **Step 4: Implement immutable numbering models**

Create constructor-only `AssemblyBatchNumberingResult`, `AssemblyBatchNumberingPlan`, `AssemblyBatchNumberingAssignment`, and `AssemblyBatchNumberingIssue`. Copy incoming collections before exposing them.

- [ ] **Step 5: Implement Policy 1 planning**

Parse every candidate and aggregate parse errors before assigning. Sort by final numeric value, ordinal-ignore-case source name, then stable ID. Use separate document-occupied and batch-reserved hash sets. Advance with checked arithmetic and format with the source minimum width. Never mutate inputs or return a partial plan.

- [ ] **Step 6: Implement canonical member IDs**

Add `Canonicalize` using `Distinct().OrderBy(...).ToArray()`. Make `Matches` compare canonical sequences so enumeration order is irrelevant.

- [ ] **Step 7: Verify production compilation**

Build the applicable production project, require 0 compile errors, and run `git diff --check`.

---

### Task 3: Add Immutable Revit Batch Models

**Dependencies:** Task 2.

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchModels.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces the spec-defined `AssemblyBatchPlan`, `AssemblyBatchPlanItem`, `AssemblyDestinationPlanSnapshot`, `TransformSnapshot`, preflight/revalidation types, engine contracts, and batch result types.

- [ ] **Step 1: Implement confirmed-plan models**

Use constructor-only properties. Copy all collections. `AssemblyBatchPlanItem` must canonicalize `SourceProductionMemberIds` through `AssemblyMemberSetComparer.Canonicalize`.

- [ ] **Step 2: Implement transform snapshots**

Add `TransformSnapshot.Capture(Transform)` and `Matches(Transform, double tolerance)`, storing and comparing origin plus all basis-vector scalars.

- [ ] **Step 3: Implement preflight/revalidation contracts**

Add the issue enum/model, `AssemblyBatchPreflightResult`, `AssemblyBatchRevalidationResult`, and `AssemblyBatchExecutionItem`. A failed result exposes no alternative plan and no execution items.

- [ ] **Step 4: Implement engine/batch contracts**

Add `AssemblyDuplicationEngineRequest`, `AssemblyDuplicationEngineResult`, `AssemblyDuplicationExecutionEvidence`, `AssemblyDuplicationEngineException`, batch/item status enums, item results, and `AssemblyBatchResult` exactly as specified.

- [ ] **Step 5: Compile without deployment**

Run `dotnet build .\ParallelSystemsPlugin.2025\ParallelSystemsPlugin.2025.csproj --no-restore -p:DeployToRevitOnBuild=false`, require 0 compile errors, and run `git diff --check`. Expected: 0 errors and no deployment messages.

---

### Task 4: Add Read-Only Identity-Family Inspection

**Dependencies:** Task 3.

**Files:**
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityFamilyService.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityMarkerEvidence.cs`

**Interfaces:**
- Produces: `AssemblyIdentityFamilyService.InspectAvailability(Document, string)` returning `AssemblyIdentityFamilyAvailability` without mutation.

- [ ] **Step 1: Add the availability model**

Store asset path, existing-family/base-symbol IDs, category, placement type, availability, and exact issue message as immutable values.

- [ ] **Step 2: Extract one contract validator**

Move current reserved-name, in-place, Generic Model, base-symbol, unexpected-symbol, and `OneLevelBased` checks into one private helper used by both inspection and `ResolveOrLoad`. Preserve existing exception text and load transaction boundaries.

- [ ] **Step 3: Implement inspection**

Existing family: validate and return IDs. Missing family: verify the RFA exists and is readable without loading it. Invalid reserved family or missing asset: return unavailable. Never start a transaction, call `LoadFamily`, activate a symbol, or regenerate.

- [ ] **Step 4: Verify**

Run the no-deploy Revit 2025 production build, require 0 compile errors, and run `git diff --check`. Inspect the diff to confirm `ResolveOrLoad` changed only by routing contract checks through the shared helper.

---

### Task 5: Extract the Locked Single-Assembly Engine

**Dependencies:** Tasks 3–4.

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationEngine.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticService.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticModels.cs`
- Modify: `ParallelSystemsPlugin.Shared/Commands/RunAssemblyDuplicationDiagnosticCommand.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces: `AssemblyDuplicationEngine.Duplicate(AssemblyDuplicationEngineRequest)`.
- Preserves: `AssemblyDuplicationDiagnosticService.RunSingleToLevel(...)` and its detailed report.

- [ ] **Step 1: Inventory the extraction**

List every private method/nested type reachable from `RunSingleToLevel`: target creation, matcher, cleanup, alignment, evidence capture, source/target validation, meaningful-parameter classification, and transaction helper. Use the list as a move checklist.

- [ ] **Step 2: Add engine preconditions**

Reject null/invalid document, source, destination plan, marker symbol, empty source members, blank target name, a currently modifiable document, or unavailable source members. The engine assumes a caller-owned active group and opens only proven short transactions.

- [ ] **Step 3: Move copy/create operations mechanically**

Move `CopyElements`, deterministic matching, unmatched cleanup, destination movement, marker creation, assembly validity/Create, independent-type assertion, naming, and helpers without altering algorithms, tolerances, ordering, or messages.

- [ ] **Step 4: Move alignment and validation mechanically**

Move transform alignment, geometry evidence, Pipe Insulation validation, evidence recapture, meaningful-parameter checks, semantic elevation validation, aggregate summary, and final failure throw. Preserve every regeneration and validation position.

- [ ] **Step 5: Return evidence and stage-aware errors**

Map current observations/invariants to `AssemblyDuplicationExecutionEvidence`. Wrap failures with source, target, current stage, inner exception, and captured evidence while preserving the original message and stack.

- [ ] **Step 6: Make the diagnostic an adapter**

Keep diagnostic plan creation, its outer group, family resolution, report writing, and assimilate/rollback. Replace the target operation with one engine request. Adapt the older two-target proof to engine-owned common operations so no duplicate target-creation implementation remains.

- [ ] **Step 7: Static regression review**

Diff moved methods against the original and confirm no change to constants, tolerance, built-in classifications, CopyElements arguments, matching, operation order, marker count, validations, or failure conditions.

- [ ] **Step 8: Compile without deployment**

Run the no-deploy Revit 2025 production build, require 0 compile errors, and run `git diff --check`.

- [ ] **Step 9: Hard manual regression gate**

STOP. The user builds the solution and runs the existing destination-level diagnostic. Required result: **93 PASS / 0 FAIL**, source unchanged, target committed, and detailed report sections retained. Fix only extraction regressions and repeat. Do not start Task 6 until this passes.

---

### Task 6: Implement Batch Preflight and Immutable Revalidation

**Dependencies:** Task 5 manual gate passed.

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchPreflightService.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchModels.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces: `CreatePlan(Document, IReadOnlyCollection<ElementId>, ElementId, long, string)`.
- Produces: `Revalidate(Document, AssemblyBatchPlan, string)`.

- [ ] **Step 1: Implement and review occupancy indexes**

For `CHW500`, `CHR502`, and `NO-NUMBER`, ensure numeric occupancy is `500,502` while all three names remain in the exact-name list. Preserve case-insensitive exact-name conflicts and verify through production code review and the preview manual gate.

- [ ] **Step 2: Implement read-only plan creation**

Resolve all selected IDs, aggregate non-assembly issues, deduplicate, require at least one source, and canonicalize each member set. Enumerate existing names once; create separate numeric and exact-name indexes. Call the core planner and existing exact-name preflight. For each source call `AssemblyDestinationLevelService.CreatePlan`, converting exceptions to per-source issues. Call `InspectAvailability`. Return no plan if any issue exists.

- [ ] **Step 3: Build immutable items**

Store numbering-plan order, canonical member IDs, skipped values, parsed parts, and scalar destination snapshots. Do not retain the mutable destination plan in the confirmed plan.

- [ ] **Step 4: Implement revalidation without renumbering**

Verify source existence/name, canonical member set equality, destination identity/elevation, target-name absence, assigned-number availability, family availability, and a fresh destination plan matching offset/delta/transform. Never invoke the numbering planner. Return live execution items with fresh mutable destination plans only when every check passes.

- [ ] **Step 5: Verify read-only behavior**

Run the no-deploy Revit 2025 production build, require 0 compile errors, and run `git diff --check`. Search the service and confirm it contains no `Transaction`, `LoadFamily`, `CopyElements`, or parameter writes.

---

### Task 7: Implement All-or-Nothing Batch Execution and Reporting

**Dependencies:** Task 6.

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchDuplicationService.cs`
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchReport.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchModels.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces: `AssemblyBatchDuplicationService.Execute(Document, AssemblyBatchPlan, string)`.
- Produces: `AssemblyBatchReport.Write(Document, AssemblyBatchResult, Exception)`.

- [ ] **Step 1: Revalidate before transactions**

Call `Revalidate` before constructing/starting the group. Return `StalePlan` with confirmed assignments and no target IDs when it fails.

- [ ] **Step 2: Execute one outer group**

Start `TransactionGroup(document, "Duplicate Assemblies Batch")`; inside it call `ResolveOrLoad` once, then call the engine exactly once per item in confirmed order. After all succeed, validate result count, unique instance/type IDs, exact names, and source existence. Assimilate only then.

- [ ] **Step 3: Map failures and roll back**

On any exception stop iteration, roll back the group, mark earlier successes `RolledBack`, the current item `Failed`, and later items `NotStarted`. Never assimilate in catch/finally.

- [ ] **Step 4: Write the batch report**

Write UTF-8 under `%TEMP%\ParallelSystems\AssemblyDuplication`, including assignments, skipped values, revalidation, family resolution, per-target stages/evidence, aggregate summaries, final group state, and exception. Reporting failure must not change model outcome.

- [ ] **Step 5: Verify transaction ownership**

Run the no-deploy Revit 2025 production build, require 0 compile errors, and run `git diff --check`. Use `rg` to confirm `new TransactionGroup` exists in batch/diagnostic callers and not in the `AssemblyDuplicationEngine` class.

---

### Task 8: Add the Production WPF Command

**Dependencies:** Task 7.

**Files:**
- Create: `ParallelSystemsPlugin.Shared/UI/Dialogs/DuplicateAssembliesDialog.xaml`
- Create: `ParallelSystemsPlugin.Shared/UI/Dialogs/DuplicateAssembliesDialog.xaml.cs`
- Create: `ParallelSystemsPlugin.Shared/Commands/DuplicateAssembliesCommand.cs`
- Modify: `ParallelSystemsPlugin.Shared/UI/ToolsMenu.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Consumes: selection filter, preflight, immutable plan, batch execution, and `AppDialog`.
- Produces: `DuplicateAssembliesCommand : IExternalCommand` and modal production preview.

- [ ] **Step 1: Create the dialog layout**

Add starting-number input, destination combo, fixed policy explanation, aggregate issue panel, preview grid, Cancel, and Confirm. Columns: Order, Source, Source Level, Target, Assigned Number, Skipped Occupied Numbers. Use existing owner/icon/centering conventions.

- [ ] **Step 2: Implement dialog state**

Parse a nonnegative invariant `long`; require a destination. Every input change calls the preflight callback and atomically replaces rows. Invalid input/plan clears the confirmed plan and disables Confirm. Confirm returns only the exact displayed immutable plan.

- [ ] **Step 3: Implement selection**

Preserve authorization/project guards. Reject mixed preselection. With no preselection use `PickObjects` and `AssemblyInstanceSelectionFilter`. Deduplicate IDs. Treat Revit cancellation as `Result.Cancelled` with zero mutation.

- [ ] **Step 4: Execute and report concisely**

After confirmation call batch execution. Success shows count, destination, compact assigned ranges, skipped numbers, and complete validation. Failure shows zero committed, failing source/stage, rollback/stale status, and report path. Put technical evidence in expandable details.

- [ ] **Step 5: Route the ribbon**

Point `PS_DuplicateAssemblies` to `DuplicateAssembliesCommand` and update only its tooltip. Keep the diagnostic command compiled for regression.

- [ ] **Step 6: Verify without deployment**

Run the no-deploy Revit 2025 production build, require 0 compile errors, and run `git diff --check`.

- [ ] **Step 7: Preview-only manual gate**

STOP. The user builds the solution, varies starting number/destination for multiple assemblies, verifies deterministic rows/skips, then cancels and confirms zero model changes. Fix only preview/preflight issues before Task 9.

---

### Task 9: Revit 2025 Production Acceptance and Final Regression

**Dependencies:** Task 8 manual gate passed.

**Files:**
- Add: `docs/testing/production-assembly-duplication-revit-2025.md`
- Modify only production files implicated by a specific failed acceptance case.

**Interfaces:**
- Produces: accepted production workflow plus captured manual evidence.

- [ ] **Step 1: Write the manual checklist**

Record sources, occupied numbers, destination, expected assignments/count, source evidence, rollback checks, report paths, and the 93/0 requirement. State the future documentation phase boundary explicitly.

- [ ] **Step 2: Verify occupied numbers and mixed prefixes**

The user builds and confirms occupied `500,502` yields `501,503,504`, mixed prefixes share one sequence, and suffix/padding are preserved.

- [ ] **Step 3: Verify multiple source levels**

Select valid assemblies from different source levels and one destination. Verify each target uses its source-specific delta and preserves offsets, topology, insulation hosts, independent type, and one marker.

- [ ] **Step 4: Verify stale-plan refusal**

Introduce a controlled post-preview conflict. Confirm no renumbering, no mutation, and an instruction to refresh/re-preview.

- [ ] **Step 5: Verify forced late failure rollback**

Cause a later target to fail strict validation. Confirm earlier/failing targets, copied members, marker types, and batch-loaded family state are rolled back; later items are not attempted; report names source and stage.

- [ ] **Step 6: Verify a successful batch**

For every source verify source unchanged, target name/number/level, exact world delta, category/type, topology, offset, insulation host, production count, independent instance/type, one marker, and complete aggregate validation.

- [ ] **Step 7: Run the locked diagnostic**

Required final result: **93 PASS / 0 FAIL** with existing detailed sections. Any failure is a shared-engine regression and must be fixed narrowly.

- [ ] **Step 8: Run final production compile verification without deployment**

Build the Revit 2025 production project with `-p:DeployToRevitOnBuild=false`, require 0 compile errors, and run `git diff --check`. Runtime acceptance remains the user's manual Revit verification from the preceding steps.

## Final Production Acceptance Criteria

- Multi-selection works through preselection or filtered picking.
- Preview resolves the complete deterministic plan before mutation.
- Global occupied/reserved numbers are skipped without skipping sources.
- Prefix, suffix, final-block replacement, padding, and overflow are correct.
- Confirmed assignments never change; stale state requires re-preview.
- Member revalidation is order-independent and detects real set changes.
- Preflight is read-only and aggregates issues.
- Family loading occurs once inside the production group.
- Every target uses the shared locked engine.
- The engine owns no transaction group and preserves short transactions.
- Every target passes strict level, geometry, topology, insulation, evidence, marker, and independence checks.
- Any failure rolls back the complete batch and batch-introduced family state.
- Production reporting is concise with a detailed batch report.
- The Tools button uses the production command; diagnostic remains internal.
- Revit 2025 production cases pass and the diagnostic remains 93 PASS / 0 FAIL.
- No documentation, sheet, view, schedule, annotation, or later cleanup capability is implemented in this phase.
