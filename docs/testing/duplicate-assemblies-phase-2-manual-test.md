# Duplicate Assemblies Identity-Marker POC

Run this proof only on a disposable copy of a representative Revit model. Do not use a production model.

## Before testing

1. Close every Revit session.
2. Build and deploy the adapter for the Revit version being tested with `Build\Deploy-Revit-Full.ps1 -RevitVersion YEAR -Configuration Debug -KeepNuGetCache`.
3. Confirm the deployed `ParallelSystemPlugin\Families` directory contains `PS_AssemblyIdentity.rfa`.
4. Start Revit and open a disposable model containing a production assembly whose name ends with a number, such as `CHW01` or `CHW001`.
5. Confirm `ParallelSystems → Tools → Duplicate Assemblies` is visible. No development-mode ribbon is required.

## Live POC

1. Save the disposable model before starting.
2. Select exactly one source `AssemblyInstance`.
3. Record the source instance ID, assembly type ID/name, member IDs, and member count.
4. Click `ParallelSystems → Tools → Duplicate Assemblies` and approve the confirmation.
5. The command must propose sequence 500 and 501 using the source naming pattern and must report success only after every transaction and invariant passes.
6. Preserve the generated report from `%TEMP%\ParallelSystems\AssemblyDuplicationDiagnostic`.
7. Confirm the report shows source type A, target-500 type B, and target-501 type C with `A != B`, `A != C`, and `B != C`.
8. Confirm the source member IDs, ownership, type ID, and name are unchanged.
9. Confirm each target has the same production-member evidence as the source plus exactly one internal `PS_AssemblyIdentity` marker.
10. Confirm each marker uses a different generated type beginning `PS_ASM_ID_`, is located at the reported source assembly origin, and uses the reported deterministic level and offset.
11. Confirm the built-in rename probe passed: target 500 temporarily changed while the source and target 501 names remained unchanged, then target 500 was restored.
12. If the command reports failure, verify no target, marker, generated marker type, or newly loaded family remains, attach the report, and stop testing that version.

## Contamination checks

Record each result without creating production documentation solely for the test. Write `not available in test model` when a surface does not exist.

- Normal project view: marker geometry/visibility and whether it can be selected or highlighted.
- Existing target assembly view: marker geometry/visibility.
- Existing part list, multi-category schedule, and BOM-related schedules: whether the marker appears.
- Project Browser: `PS_AssemblyIdentity`, `PS_ASSEMBLY_IDENTITY_BASE`, and the two generated `PS_ASM_ID_*` types.
- Assembly member editing/selection: marker ownership and whether it interferes with normal assembly operations.

## Save/reload persistence

1. Save the disposable model under a new test filename.
2. Close Revit completely and reopen that file.
3. Record the source, target-500, and target-501 instance IDs, type IDs, and names again.
4. Confirm the three type IDs remain pairwise different and all names remain correct.
5. Rename target 500 temporarily; confirm the source and target 501 do not change; restore target 500.
6. Repeat the contamination checks and return the report plus observations before later production phases begin.

## Stop conditions

Stop and report the output if the source changes, any two assemblies share a type, either target has other than one identity marker, production evidence differs, a rename propagates, a failed command leaves artifacts, or save/reload changes the independence relationship.

## Verification order

Prove the mechanism first in Revit 2025. Only after it succeeds, repeat the live and persistence checks in Revit 2021, 2024, and 2026. Add version-specific code only for an observed incompatibility.

| Revit | Live POC | Pairwise types | Production + one marker | Rename isolated | Save/reload | Contamination/report notes |
|---|---|---|---|---|---|---|
| 2021 | Not run | — | — | — | — | |
| 2024 | Not run | — | — | — | — | |
| 2025 | Not run | — | — | — | — | |
| 2026 | Not run | — | — | — | — | |
