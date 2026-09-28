# Production Assembly Duplication Design Specification

## Status and Scope

This specification defines the production multi-assembly layer around the Revit 2025 destination-level Assembly Duplication engine that passed its live regression diagnostic with 93 PASS and 0 FAIL.

The proven single-assembly behavior is locked. This work may mechanically extract it behind an internal engine boundary, but must not redesign its copying, matching, movement, level handling, insulation handling, connector validation, identity marker, production validation, or rollback behavior.

### 2026-09-26 Production Numbering and Preview Addendum

This addendum supersedes the earlier global numeric-occupancy policy and the earlier read-only Assigned Number column. Sources remain ordered by parsed original final numeric value, source name, and ElementId. Availability is now based only on the complete case-insensitive target assembly name, so different complete names may reuse the same numeric value. A user-edited Assigned Number is an immutable-plan input for that source row: it records the requested anchor, resolves forward across complete-name conflicts, and makes following rows continue sequentially until another anchor. Preview rows remain visible on preflight errors, while Confirm stays disabled. Execution consumes only final confirmed names and never renumbers. The Starting Number defaults to `0`; selection resolves direct assemblies and owning assemblies from selected members. These changes do not alter the locked duplication engine or whole-batch rollback boundary.

This production scope includes:

- multi-assembly selection;
- starting-number and destination-level configuration;
- complete batch preview and preflight;
- global occupied-number skipping;
- all-or-nothing batch execution;
- concise production reporting; and
- preservation of the existing diagnostic as a regression adapter over the same engine.

Assembly documentation, sheets, views, schedules, annotations, and cleanup beyond rollback are not part of this production implementation phase. They remain future phases in the larger Assembly Duplication roadmap; this phase boundary does not remove or reject those future capabilities.

## Repository Findings

The repository already provides the following reusable foundation:

- `AssemblyNameParser` finds the final contiguous decimal block and preserves prefix, suffix, and minimum width.
- `AssemblyNamingService` deterministically sorts candidates by final numeric value, source name, and stable element ID.
- `ProposedAssemblyName` carries source and assigned numeric values and the generated name.
- `AssemblyConflictIndex` and `AssemblyPreflightService` provide case-insensitive exact-name conflict checks.
- `AssemblyInstanceSelectionFilter` already restricts Revit picks to `AssemblyInstance`.
- `AssemblyDestinationLevelService.CreatePlan` performs the proven read-only source-level analysis.
- `AssemblyDuplicationDiagnosticService.RunSingleToLevel` currently owns the validated operational path and its `TransactionGroup`.
- `AssemblyIdentityFamilyService.ResolveOrLoad` already loads and validates the marker family within a normal `Transaction`.
- `AppDialog` and existing WPF code-behind establish the required UI conventions.

The current code has four architectural conflicts with the approved production boundary:

1. The proven operational helpers are private members of `AssemblyDuplicationDiagnosticService`, so production cannot reuse them without either extraction or duplication.
2. `AssemblyDestinationLevelPlan` owns a mutable `DestinationValidationCollector`. It cannot be embedded directly in an immutable, reusable confirmed batch plan.
3. `AssemblyIdentityFamilyService.ResolveOrLoad` may mutate the document by loading the family. It cannot be called during read-only preflight.
4. Verification is intentionally production-only: compile the applicable plugin project, require zero errors, run `git diff --check`, and use the specified manual Revit gates. No standalone unit-test project or replacement harness is part of this phase.

The design below resolves these conflicts without changing the proven Revit behavior.

## Architecture

The feature has four layers:

```text
DuplicateAssembliesCommand / DuplicateAssembliesDialog
                    |
                    v
        AssemblyBatchPreflightService
                    |
          immutable AssemblyBatchPlan
                    |
                    v
       AssemblyBatchDuplicationService
                    |
          AssemblyDuplicationEngine
                    |
      locked Revit single-target operations
```

`ParallelSystemsPlugin.Core` owns Revit-independent parsing, ordering, numbering, reservation, and exact-name preflight. The shared Revit project owns element selection, level-plan inspection, immutable Revit batch plans, transactions, engine execution, and user reporting.

## Revit-Independent Numbering

### New types

```csharp
public static class AssemblyBatchNumberingService
{
    public static AssemblyBatchNumberingResult CreatePlan(
        IReadOnlyCollection<AssemblyNamingCandidate> candidates,
        long startingNumber,
        IReadOnlyCollection<long> occupiedNumbers);
}
```

```csharp
public sealed class AssemblyBatchNumberingResult
{
    public bool IsValid { get; }
    public AssemblyBatchNumberingPlan Plan { get; }
    public IReadOnlyList<AssemblyBatchNumberingIssue> Issues { get; }
}
```

```csharp
public sealed class AssemblyBatchNumberingPlan
{
    public long StartingNumber { get; }
    public IReadOnlyList<AssemblyBatchNumberingAssignment> Assignments { get; }
    public IReadOnlyList<long> SkippedOccupiedNumbers { get; }
}
```

```csharp
public sealed class AssemblyBatchNumberingAssignment
{
    public ProposedAssemblyName Proposal { get; }
    public IReadOnlyList<long> SkippedBeforeAssignment { get; }
}
```

```csharp
public sealed class AssemblyBatchNumberingIssue
{
    public long? StableElementId { get; }
    public string SourceName { get; }
    public string Message { get; }
}
```

All properties are constructor-initialized and exposed through read-only collections.

### Algorithm

The service first parses every source with `AssemblyNameParser`. It aggregates every invalid source-name issue and returns no plan if any source is invalid. It then applies the existing deterministic ordering:

1. parsed final numeric value;
2. source name with ordinal-ignore-case comparison; and
3. stable source element ID.

It creates a hash set from all occupied final numeric values in the document and a second set for assignments reserved during this batch. Starting at the requested value, each source advances through occupied or reserved values until it reaches the next available `long`.

The skipped values are retained both per assignment and for the complete batch. Assignment and increment operations use checked `Int64` arithmetic. If the cursor cannot advance or every source cannot be assigned, the result contains issues and no partial plan.

The target name uses the source's parsed prefix and suffix and formats the assigned number to at least the original numeric block width. Width may expand and is never truncated.

After numbering, the existing `AssemblyPreflightService.ValidateNames` performs case-insensitive exact proposed-name and within-batch duplicate checks. Numeric availability and exact-name availability are intentionally separate validations.

## Immutable Revit Batch Models

All confirmed plan types are immutable sealed classes with constructor-assigned values and copied read-only collections.

```csharp
internal sealed class AssemblyBatchPlan
{
    public long StartingNumber { get; }
    public long DestinationLevelId { get; }
    public string DestinationLevelName { get; }
    public double DestinationLevelElevation { get; }
    public IReadOnlyList<AssemblyBatchPlanItem> Items { get; }
    public IReadOnlyList<long> SkippedOccupiedNumbers { get; }
}
```

```csharp
internal sealed class AssemblyBatchPlanItem
{
    public long Order { get; }
    public long SourceAssemblyId { get; }
    public string SourceAssemblyName { get; }
    public AssemblyNameParts SourceNameParts { get; }
    public long AssignedNumber { get; }
    public string TargetAssemblyName { get; }
    public IReadOnlyList<long> SkippedOccupiedNumbers { get; }
    // Canonical ascending, unique ID values; never Revit enumeration order.
    public IReadOnlyList<long> SourceProductionMemberIds { get; }
    public AssemblyDestinationPlanSnapshot Destination { get; }
}
```

```csharp
internal sealed class AssemblyDestinationPlanSnapshot
{
    public long SourceLevelId { get; }
    public string SourceLevelName { get; }
    public double SourceLevelElevation { get; }
    public long DestinationLevelId { get; }
    public string DestinationLevelName { get; }
    public double DestinationLevelElevation { get; }
    public double SourceAssemblyOffset { get; }
    public double DeltaZ { get; }
    public TransformSnapshot TargetTransform { get; }
}
```

`TransformSnapshot` stores scalar origin and basis coordinates rather than a mutable Revit `Transform` reference.

The confirmed plan deliberately does not retain `AssemblyInstance`, `Level`, `ElementId`, `Transform`, or the mutable `AssemblyDestinationLevelPlan`. It stores stable IDs and scalar evidence. Source production member IDs are deduplicated and sorted ascending when captured, producing a deterministic canonical snapshot. This prevents execution-time validation observations from mutating the user's confirmed preview and prevents Revit enumeration order from affecting stale-plan detection.

### Preflight result

```csharp
internal sealed class AssemblyBatchPreflightResult
{
    public bool IsValid { get; }
    public AssemblyBatchPlan Plan { get; }
    public IReadOnlyList<AssemblyBatchPreflightIssue> Issues { get; }
}
```

```csharp
internal sealed class AssemblyBatchPreflightIssue
{
    public AssemblyBatchPreflightIssueKind Kind { get; }
    public long? SourceAssemblyId { get; }
    public string SourceAssemblyName { get; }
    public string Message { get; }
}
```

Issue kinds cover invalid selection, duplicate source, invalid name, number exhaustion, exact target-name conflict, unavailable source, empty production members, invalid destination level, invalid destination plan, missing or invalid identity-family contract, and stale confirmed plan.

## Production Preflight Service

```csharp
internal static class AssemblyBatchPreflightService
{
    public static AssemblyBatchPreflightResult CreatePlan(
        Document document,
        IReadOnlyCollection<ElementId> selectedElementIds,
        ElementId destinationLevelId,
        long startingNumber,
        string assemblyDirectory);

    public static AssemblyBatchRevalidationResult Revalidate(
        Document document,
        AssemblyBatchPlan confirmedPlan,
        string assemblyDirectory);
}
```

### Initial preflight lifecycle

`CreatePlan` performs no transaction and no document mutation:

1. Validate the document and selected IDs.
2. Resolve every selected ID and report every non-assembly selection.
3. Remove duplicate IDs deterministically while reporting unexpected duplicates.
4. Require at least one source.
5. Capture each source name and production member IDs. Canonicalize member IDs as sorted unique numeric values before storing them.
6. Parse all source names and aggregate parsing errors.
7. Enumerate existing assembly names once.
8. Parse every existing name that has a valid final numeric block into the global occupied-number set. Names without a valid block remain in the exact-name conflict index but do not create guessed numeric occupancy.
9. Call `AssemblyBatchNumberingService.CreatePlan`.
10. Call the existing exact-name preflight using the generated proposals.
11. Resolve the selected destination level.
12. For every source, call the read-only `AssemblyDestinationLevelService.CreatePlan` and capture its scalar snapshot. Exceptions become per-source preflight issues rather than stopping the remaining checks.
13. Inspect the identity-family contract without loading it.
14. Check unique source IDs, unique target names, unique assigned numbers, and nonempty member sets.
15. Return an immutable plan only when the issue list is empty.

The service may generate preview diagnostics, but it may not call `LoadFamily`, start a transaction, copy an element, or change a parameter.

### Identity-family inspection

Add a read-only API to the existing family service:

```csharp
internal static AssemblyIdentityFamilyAvailability InspectAvailability(
    Document document,
    string assemblyDirectory);
```

If `PS_AssemblyIdentity` exists, inspection applies the same category, base-symbol, unexpected-symbol, and placement-type checks used by `ResolveOrLoad`. If it does not exist, inspection verifies that the expected RFA file exists and is readable, without loading it.

The shared family-contract validation is extracted into one private helper used by both inspection and `ResolveOrLoad`; the contract is not implemented twice.

### Immutable-plan revalidation

`Revalidate` runs immediately after the user confirms and before a `TransactionGroup` starts. It does not generate a replacement plan.

It verifies:

- every source still exists and is the same `AssemblyInstance`;
- source names still equal the snapshots;
- current production-member IDs, after sorted-unique canonicalization, are set-equal to the confirmed snapshots; enumeration order is ignored, while actual additions or removals remain failures;
- the destination level still exists with the same name and elevation;
- every confirmed target name is still absent;
- every confirmed assigned numeric value is still globally available;
- the identity-family contract or asset remains available; and
- a freshly created destination-level plan matches the confirmed source level, destination level, offset, delta, and target transform within the existing tolerance.

On success it returns ephemeral execution items containing the live source, live destination level, source member IDs, and a fresh `AssemblyDestinationLevelPlan`. The fresh plan may collect validation evidence during execution; the confirmed `AssemblyBatchPlan` remains unchanged.

On any mismatch it returns issues and no executable items. The command tells the user to refresh the preview. It never changes assigned numbers or target names.

## Single-Assembly Engine Extraction

### API

```csharp
internal static class AssemblyDuplicationEngine
{
    public static AssemblyDuplicationEngineResult Duplicate(
        AssemblyDuplicationEngineRequest request);
}
```

```csharp
internal sealed class AssemblyDuplicationEngineRequest
{
    public Document Document { get; }
    public AssemblyInstance Source { get; }
    public IReadOnlyList<ElementId> SourceProductionMemberIds { get; }
    public string TargetName { get; }
    public AssemblyDestinationLevelPlan DestinationPlan { get; }
    public FamilySymbol IdentityMarkerBaseSymbol { get; }
    public string StagePrefix { get; }
}
```

```csharp
internal sealed class AssemblyDuplicationEngineResult
{
    public AssemblyInstance TargetAssembly { get; }
    public IReadOnlyList<ElementId> CopiedProductionMemberIds { get; }
    public AssemblyIdentityMarkerEvidence Marker { get; }
    public AssemblyEvidence SourceBefore { get; }
    public AssemblyEvidence SourceAfter { get; }
    public AssemblyEvidence TargetAfter { get; }
    public AssemblyDuplicationExecutionEvidence Evidence { get; }
}
```

`AssemblyDuplicationExecutionEvidence` contains the transaction stages, copy-matching observations, transform observations, destination-level observations, production-evidence observations, invariants, pass count, and failure list currently written into `AssemblyDuplicationDiagnosticResult`.

### Extraction boundary

The following proven behavior moves mechanically from `AssemblyDuplicationDiagnosticService` into the engine or engine-owned focused helpers:

- target creation;
- `CopyElements` invocation;
- deterministic source-to-copy matching;
- unmatched-copy cleanup;
- destination-level movement and reassociation call;
- marker creation;
- assembly validity checks and `AssemblyInstance.Create`;
- independent-type assertion;
- transform alignment and world-geometry preservation checks;
- visible target naming;
- post-operation regeneration;
- source evidence recapture;
- Pipe Insulation relationship validation;
- source and target production-evidence validation;
- semantic absolute-elevation handling;
- aggregate validation summary and final failure throw; and
- the existing transaction helper and failure preprocessor usage.

The extraction is structural, not behavioral. Constants, tolerances, validation classifications, ordering, messages used by the regression report, and operation order remain unchanged.

### Transaction contract

The engine never creates, assimilates, or rolls back a `TransactionGroup`.

The caller must invoke it while a caller-owned `TransactionGroup` is active and while no `Transaction` is currently open. The engine continues to own the same short phase `Transaction` objects used by the proven implementation, including failure preprocessing and regeneration points.

If any phase or aggregate validation fails, the engine throws `AssemblyDuplicationEngineException` containing:

- source ID and name;
- target name;
- stage name;
- original exception; and
- all evidence captured before failure.

The caller owns group rollback.

## Diagnostic Adapter

`AssemblyDuplicationDiagnosticService.RunSingleToLevel` remains the regression entry point and retains its current signature and report contract.

It will:

1. perform its existing argument checks;
2. create the destination-level plan;
3. start its diagnostic `TransactionGroup`;
4. call `AssemblyIdentityFamilyService.ResolveOrLoad`;
5. construct one engine request;
6. call `AssemblyDuplicationEngine.Duplicate`;
7. map engine evidence into `AssemblyDuplicationDiagnosticResult`;
8. write the existing detailed diagnostic report;
9. assimilate only on complete success; and
10. roll back on any exception.

The active destination-level diagnostic and production batch therefore execute the same engine. The older two-target identity proof may remain as an internal regression utility, but all common target creation, matching, identity, and validation operations must call engine-owned code rather than retain duplicate private implementations.

The required regression acceptance criterion remains 93 PASS and 0 FAIL in the existing representative Revit 2025 model.

## Selection Workflow

`DuplicateAssembliesCommand` performs authorization and active-document checks before any model operation.

If the Revit selection is nonempty, every selected element must be an `AssemblyInstance`; mixed selections are rejected rather than silently filtered. IDs are deduplicated and ordered only by the numbering service, never by UI selection order.

If nothing is preselected, the command calls `PickObjects(ObjectType.Element, new AssemblyInstanceSelectionFilter(), ...)`. Cancellation returns `Result.Cancelled` with no transaction and no dialog error.

At least one unique source is required. The command captures the source IDs, available levels ordered by project elevation and name, and the add-in assembly directory, then opens the production dialog.

## WPF Dialog State Flow

`DuplicateAssembliesDialog` follows existing WPF code-behind and `AppDialog` ownership/icon conventions. It introduces no MVVM or third-party UI framework.

Inputs:

- selected source IDs fixed for the life of the dialog;
- starting-number text box accepting invariant nonnegative `Int64` values;
- destination-level combo box; and
- a read-only explanation of global occupied-number skipping.

Preview columns:

- Order
- Source
- Source Level
- Target
- Assigned Number
- Skipped Occupied Numbers

State transitions:

```text
Loading
  -> Invalid Input       when the starting number or destination is invalid
  -> Preflighting        after a valid input change
  -> Invalid Plan        when preflight returns issues
  -> Valid Plan          when preflight returns an immutable plan
  -> Confirmed           only from Valid Plan
  -> Cancelled           from any non-executing state
```

Every starting-number or destination-level change recomputes the complete preflight plan. A new valid preview replaces the previous preview atomically. The Confirm button is enabled only in `Valid Plan` state. All issues are shown together in a concise issue panel; detailed exception text is not displayed in the main preview.

On confirmation, the dialog returns the exact immutable plan displayed to the user. The command performs revalidation and does not reopen or silently refresh assignments.

## Batch Execution

### API

```csharp
internal static class AssemblyBatchDuplicationService
{
    public static AssemblyBatchResult Execute(
        Document document,
        AssemblyBatchPlan confirmedPlan,
        string assemblyDirectory);
}
```

### Sequence

1. Call `AssemblyBatchPreflightService.Revalidate` before starting a transaction group.
2. Return `StalePlan` with zero changes if revalidation fails.
3. Start one `TransactionGroup` named `Duplicate Assemblies Batch`.
4. Resolve or load `PS_AssemblyIdentity` once inside that group.
5. Iterate ephemeral execution items in confirmed plan order.
6. Call `AssemblyDuplicationEngine.Duplicate` exactly once per item.
7. Record each successful engine result without assimilating the group.
8. After every item succeeds, verify result count, unique target instance IDs, unique target type IDs, exact target names, and source existence.
9. Assimilate the outer group only after every target and batch invariant passes.

If identity-family loading, any engine call, or any final batch invariant fails, processing stops immediately. The service rolls back the outer group. Previously successful targets in that group, the currently failing target, copied members, marker instances, generated marker symbols, and a family loaded only for the batch are all rolled back.

No catch path may assimilate a partial batch.

## Batch Results and Reporting

```csharp
internal enum AssemblyBatchStatus
{
    Succeeded,
    PreflightRejected,
    StalePlan,
    RolledBack
}
```

```csharp
internal sealed class AssemblyBatchResult
{
    public AssemblyBatchStatus Status { get; }
    public AssemblyBatchPlan Plan { get; }
    public IReadOnlyList<AssemblyBatchItemResult> Items { get; }
    public string FailedStage { get; }
    public string FailureMessage { get; }
    public string ReportPath { get; set; }
}
```

```csharp
internal enum AssemblyBatchItemStatus
{
    Planned,
    Succeeded,
    Failed,
    RolledBack,
    NotStarted
}
```

Each `AssemblyBatchItemResult` carries source ID/name, target name, assigned number, target instance/type IDs when created, status, failed stage, and engine evidence.

`AssemblyBatchReport` writes one technical text report under a scoped `%TEMP%\ParallelSystems\AssemblyDuplication` folder. It contains the confirmed assignment table, skipped numbers, revalidation result, family resolution, per-target stages, exact failure, aggregate validation summaries, final group status, and exception details.

The main result dialog remains concise:

- success: duplicated count, destination level, compact assigned-number ranges, skipped occupied numbers, and complete-validation confirmation;
- stale plan: no changes, assumptions changed, refresh/re-preview required;
- execution failure: zero assemblies committed, failing source, failed stage, complete rollback confirmation, and report path.

Technical details are available through the existing expandable `AppDialog.ShowDetailed` pattern.

## Failure Semantics

- Selection cancellation: return cancelled, no preflight transaction, no report required.
- Invalid preview input: keep Confirm disabled, no transaction.
- Preflight issues: show all issues, no transaction.
- Stale confirmed plan: abort before transaction, never renumber.
- Failure to start the outer group: report failure, no mutation.
- Family load/contract failure: roll back the group.
- Single-target engine failure: stop later targets and roll back the group.
- Final batch-invariant failure: roll back the group.
- Report-writing failure: do not change the model outcome; include a concise reporting warning where possible.

No production failure leaves a partially committed batch.

## Verification Policy

Do not add a standalone unit-test project, console test project, or replacement test harness. Verification uses normal compilation of the applicable production plugin project, requires zero compile errors, runs `git diff --check`, and stops at each specified manual Revit gate when runtime validation is required.

Required production behaviors for static review and the applicable Revit preview/runtime gates:

1. Mixed prefixes retain deterministic source ordering and use one global sequence.
2. Existing occupied values `500` and `502`, start `500`, and three sources produce `501`, `503`, and `504`.
3. A value reserved for an earlier assignment cannot be reused by a later assignment.
4. Multiple consecutive occupied values advance to the first available value.
5. Selected sources' existing numeric values are included in occupancy by the Revit preflight collector.
6. `CHW001` assigned `5` becomes `CHW005`.
7. `CHW999` assigned `1000` becomes `CHW1000`.
8. `CHW-L02-007-A` changes only its final numeric block.
9. `CHW-50SM` preserves prefix and suffix.
10. A source without a numeric block produces an issue and no plan.
11. Several invalid source names produce several issues and no partial assignments.
12. Exact target-name conflicts are case-insensitive hard failures.
13. Equal source numeric values use source name and stable ID tie-breakers.
14. Starting at `Int64.MaxValue` succeeds only when one unoccupied assignment is sufficient.
15. Occupied `Int64.MaxValue` or additional required assignments produce exhaustion issues and no plan.
16. The input occupied-number collection is not mutated.
17. Returned plans and collections cannot be changed by callers.

No test-only project is added to the solution by this implementation.

## Revit 2025 Manual Verification

Tests use a disposable representative model and preserve the generated batch and diagnostic reports.

1. **Preview only:** Select multiple assemblies, vary starting number and destination level, verify preview recomputation, then cancel and confirm zero model changes.
2. **Occupied-number skipping:** Seed occupied values `500` and `502`; verify preview and committed targets receive `501`, `503`, and `504`.
3. **Mixed prefixes:** Select mixed CHW/CHR assemblies and verify one deterministic global sequence without prefix restarts.
4. **Multiple source levels:** Select valid assemblies from different source levels, choose one destination, and verify each retains its own source-relative offset using its own delta.
5. **Successful batch:** Verify every target name, level, production count, independent instance/type, identity marker, topology, insulation host, and validation summary.
6. **Stale preview:** Confirm a preview only after creating an intervening target-name/number conflict through a controlled test path; verify execution refuses to renumber and makes no changes.
7. **Forced engine failure:** Cause one later source to fail a strict supported validation; verify earlier targets and family state introduced by the batch are rolled back.
8. **Regression diagnostic:** Run the existing single-assembly destination-level diagnostic and require 93 PASS and 0 FAIL.

Revit 2021–2024 and 2026 builds and live tests occur only after the Revit 2025 production workflow passes. Version-specific code is introduced only for an observed API incompatibility.

## Future Roadmap Boundary

The following capabilities are intentionally deferred from this production implementation phase and remain future Assembly Duplication roadmap work:

- assembly sheets and sheet numbering;
- assembly views and viewport recreation;
- schedules and parts lists;
- annotation and detail-element remapping; and
- production cleanup other than automatic rollback of the current batch.

Nothing in this specification permanently removes or rejects these capabilities. They require separate designs and validation gates after the model-only production batch workflow is proven.

## Files Expected to Be Added

Core:

- `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyBatchNumberingService.cs`
- `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyBatchNumberingPlan.cs`

Shared Revit layer:

- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationEngine.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchModels.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchPreflightService.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchDuplicationService.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyBatchReport.cs`
- `ParallelSystemsPlugin.Shared/UI/Dialogs/DuplicateAssembliesDialog.xaml`
- `ParallelSystemsPlugin.Shared/UI/Dialogs/DuplicateAssembliesDialog.xaml.cs`
- `ParallelSystemsPlugin.Shared/Commands/DuplicateAssembliesCommand.cs`

## Files Expected to Be Modified

- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticService.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticModels.cs`
- `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityFamilyService.cs`
- `ParallelSystemsPlugin.Shared/Commands/RunAssemblyDuplicationDiagnosticCommand.cs` only as required to preserve the diagnostic adapter
- `ParallelSystemsPlugin.Shared/UI/ToolsMenu.cs`
- `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`
- `ParallelSystemsPlugin.sln`

`AssemblyDestinationLevelService.cs` should not require behavioral changes. Any edit to it must be limited to exposing immutable evidence needed by the engine or eliminating a mechanical dependency discovered during extraction; such an edit requires focused regression review.

## Implementation Constraints

- Mark additions with `// Created by Jhay` and relevant modifications with `// Changed by Jhay`.
- Preserve unrelated dirty working-tree files.
- Implement in small slices and validate Revit 2025 first.
- Do not deploy or copy DLLs; the user will build the solution to update the installed add-in.
- Do not commit unless the user explicitly requests it.
- Do not begin implementation until this specification is approved and a separate implementation plan is reviewed.
