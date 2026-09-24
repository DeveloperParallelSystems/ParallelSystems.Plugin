# Assembly Identity Marker POC Design

## Status and scope

This design replaces the failed `AssemblyType.Duplicate()` experiment with a controlled identity-marker proof of concept. It applies only to duplicating one selected source assembly twice in the active Revit project. It does not implement destination-level production behavior, the final multi-assembly UI, sheets, assembly views, viewport recreation, schedules, or annotation copying.

The POC succeeds only when Revit creates three mutually independent assembly types: the existing source type, target 500, and target 501. Any required check failure rolls back the complete operation.

## Confirmed constraints

- Revit intentionally assigns matching assemblies to the same `AssemblyType`.
- The tested construction `AssemblyType` rejects `ElementType.Duplicate()` at runtime.
- Production members must not be changed merely to force assembly uniqueness.
- The only intentional target-member difference is one internal Generic Model family instance whose `FamilySymbol` is unique to that target.
- The implementation remains shared across Revit 2021–2026 unless testing proves a version-specific difference.

## Family asset

Create the binary asset at:

`ParallelSystemsPlugin.Shared/Families/PS_AssemblyIdentity.rfa`

Author it in Revit 2021 from the installed standalone template:

`C:\ProgramData\Autodesk\RVT 2021\Family Templates\English\Metric Generic Model.rft`

The family name is `PS_AssemblyIdentity`, its category is Generic Models, and its one base symbol is named `PS_ASSEMBLY_IDENTITY_BASE`.

The family contains no model, symbolic, or detail geometry; connectors; nested families; voids; hosts; engineering parameters; or shared parameters. It is not face-, wall-, floor-, ceiling-, or roof-hosted and is not Work Plane-Based. Only the template's required reference origin and internal family structure remain.

Family creation is an authoring-time operation only. The production add-in never depends on installed templates and never generates the RFA at runtime.

## Build and deployment

Add `Families/**/*` to `ParallelSystemsPlugin.Shared.projitems` as `Content`, link it beneath `Families`, and use `CopyToOutputDirectory=PreserveNewest`. This matches the existing Icons and Docs mechanism.

Every adapter output will therefore contain:

`Families/PS_AssemblyIdentity.rfa`

The existing MSBuild deployment target and `Deploy-Revit-Full.ps1` already copy the complete adapter output recursively, so no second deployment system or developer-machine path is introduced.

## On-demand family loading

The Duplicate Assemblies command resolves the RFA relative to `typeof(App).Assembly.Location`. It first searches the active document for a `Family` whose name is exactly `PS_AssemblyIdentity`.

- If found, reuse that family and its base symbol.
- If absent, verify the deployed RFA exists, then load it inside the POC `TransactionGroup` using `Document.LoadFamily`.
- Re-query the family and symbol after loading; do not rely on stale wrappers.
- Do not load the family during Revit startup.
- If the POC later rolls back, the load transaction is part of the same group so a newly loaded family and its generated symbols are removed with all other POC artifacts.

If an existing family has the reserved name but does not contain the expected base symbol or has an incompatible category/placement type, fail and roll back rather than overwrite user content.

## Placement type and level selection

After resolving the family, record and report `Family.FamilyPlacementType`. The expected value from the selected standalone Generic Model template is `OneLevelBased`, but the implementation does not assume that value before inspecting it.

For `OneLevelBased`, choose a level deterministically:

1. Use the source assembly's `LevelId` if it resolves to a valid `Level`.
2. Otherwise inspect valid levels referenced by source production members. Prefer the most frequently referenced level; break ties by distance from the assembly-origin Z, then by stable element ID.
3. If no source member provides a valid level, use the document level whose elevation is closest to the assembly-origin Z, breaking ties by stable element ID.
4. Never derive the level from the active view.

Place the marker using the level-aware `NewFamilyInstance` overload with `StructuralType.NonStructural`. Use `sourceAssembly.GetTransform().Origin` as the required world reference point. Set the built-in instance offset, when writable, so the final `LocationPoint.Point` equals that origin. Regenerate and verify the final point within Revit's geometric tolerance. Report the selected level ID, level name, level elevation, requested origin, resulting point, and offset.

If the actual placement type is not safely supported by this explicit branch, stop and roll back with the observed placement type. Do not invent a work plane, host, or active-view dependency.

## Unique marker symbols

Reuse only the base symbol as the source for duplication. Create one new `FamilySymbol` for each target.

The internal name contains a sanitized human-readable target fragment plus a collision-resistant identifier derived from a new GUID, for example:

`PS_ASM_ID_CHW500_7F3C18A2`

Sanitization replaces characters unsafe for Revit type names, collapses repeated separators, and bounds the readable fragment length. The GUID suffix—not the visible assembly name—guarantees uniqueness. Before accepting a name, verify no element type in the document already uses it. The target assembly's visible `AssemblyTypeName` remains independent from this internal symbol name.

Activate each symbol if required before placement. Never reuse a generated symbol between targets.

## POC transaction flow

Run the entire operation in one `TransactionGroup`:

1. Capture source instance ID, type ID, type name, naming category, transform origin, and production-member evidence.
2. Resolve or load the identity family and inspect its category and placement type.
3. Generate the two visible target names using the existing naming/parser rules with sequence values 500 and 501. Reject name conflicts before copying anything.
4. For target 500:
   - Copy the source production members exactly with identity transform.
   - Duplicate the base marker symbol with a unique internal name.
   - Place one marker at the source assembly transform origin using the selected source level strategy.
   - Validate that every copied production element is loose and that exactly one marker exists.
   - Combine copied production IDs plus the marker ID.
   - Validate the combined collection with `AssemblyInstance.AreElementsValidForAssembly` using the source naming category.
   - Create the assembly and commit that creation transaction before inspecting its assigned type.
   - Require its type ID to differ from the source type ID, then set its visible assembly type name.
5. Repeat the same process independently for target 501 with a different marker symbol.
6. Require source type A, target-500 type B, and target-501 type C to be pairwise different.
7. Temporarily rename target 500 to a unique probe name, regenerate, and prove that the source and target 501 names do not change. Restore target 500's requested name before final validation.
8. Capture all evidence again after regeneration. Assimilate the group only if every invariant passes.

If any transaction does not return `TransactionStatus.Committed`, treat it as failure and include the actual status in the diagnostic report. Any exception or failed invariant rolls back the transaction group.

## Production-member validation

Classify a member as an internal identity marker only when it is a `FamilyInstance` belonging to the exact `PS_AssemblyIdentity` family. Do not classify arbitrary Generic Models as markers.

For each target require:

- production-member count equals the source count;
- internal identity-marker count equals one;
- target production member IDs equal the IDs returned by copying the source members;
- each target production member and marker reports the target assembly as its owner;
- source member IDs and ownership remain unchanged;
- production member category, element type, relevant geometry-affecting values, relative position, and intended model/engineering data match the source evidence.

The POC report distinguishes production members and internal members instead of comparing raw total counts.

## Schedule, view, selection, and browser observations

The POC records what can be established through the API and provides explicit manual checks for UI-only behavior. It does not add filters or cleanup behavior.

Record and report:

- whether the marker has any model geometry in a normal project view and in an assembly view;
- whether the marker is returned by collectors for normal, assembly, part-list, multi-category, and available BOM-related schedules;
- whether the marker can be selected or highlighted despite having no geometry;
- how `PS_AssemblyIdentity` and generated symbol names appear under Families/Types in Project Browser;
- the owner assembly ID and visibility/category state used by relevant views.

Where an assembly view, part list, or relevant schedule does not exist, report `not available in test model` rather than creating production documentation during this POC.

## Diagnostic output

Extend the existing text report to include:

- exact RFA template and deployed/load paths;
- family name, category, and `FamilyPlacementType`;
- whether the family was pre-existing or loaded by the command;
- marker symbol IDs/names, placement coordinates, selected level evidence, and offset;
- source, target-500, and target-501 assembly instance/type IDs and names;
- pairwise type-independence results;
- temporary rename/restoration results;
- separated production/internal member evidence;
- schedule/view/selection/browser observations and unavailable checks;
- every transaction commit status and any Revit exception.

## Manual persistence test

After the in-memory POC succeeds:

1. Save the RVT under a safe test name.
2. Close Revit completely.
3. Reopen the saved RVT.
4. Verify the source, target 500, and target 501 names.
5. Record all three assembly type IDs and confirm they remain pairwise different.
6. Rename target 500 temporarily.
7. Confirm the source and target 501 names remain unchanged.
8. Restore target 500's requested name.
9. Inspect marker visibility, schedule/part-list inclusion, selection behavior, and Project Browser type pollution.
10. Return the resulting report and observations before any later phase begins.

## Compatibility and verification order

First prove the mechanism in the currently used Revit development version. After that succeeds, test runtime behavior at the compatibility boundaries: Revit 2021, 2024, 2025, and 2026. Compile all adapters for Revit 2021–2026 after implementation.

Use shared code unless a real runtime or API difference is observed. Do not add speculative version branches.

## Stop condition

Stop after the identity-marker POC and its save/reload/manual contamination checks. Do not implement destination-level production logic, final batch UI, sheets, views, schedules, viewports, or annotations until the source and both targets retain independent types and names across save/reload and the marker's model/documentation impact is understood.
