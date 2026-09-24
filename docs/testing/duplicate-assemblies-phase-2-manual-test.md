# Duplicate Assemblies Phase 2 Manual Test

Run this proof only on a disposable copy of a representative Revit model. Do not use a production model.

## Before Testing

1. Close every Revit session.
2. Run `Development Tools\Enable-DevelopmentMode.cmd`.
3. Build and deploy the adapter for the Revit version being tested with `Build\Deploy-Revit-Full.ps1 -RevitVersion YEAR -Configuration Debug`.
4. Start that Revit version. When prompted, enable development mode with the established development password.
5. Open a disposable model copy containing a normal production assembly such as `CHW01` or `CHW001`.
6. Confirm the `ParallelSystems` ribbon contains the `DEVELOPMENT MODE` panel and its `Assembly Independence Diagnostic` button.

## Test Procedure

Repeat all steps separately in Revit 2021, 2022, 2023, 2024, 2025, and 2026.

1. Save the disposable model before starting.
2. Select exactly one `AssemblyInstance`. Do not select its members, views, sheet, or any additional element.
3. Record the source assembly name, instance ID, type ID, and member count using RevitLookup or the information available in the diagnostic result.
4. Click `ParallelSystems → DEVELOPMENT MODE → Assembly Independence Diagnostic`.
5. Read the confirmation carefully and click Yes.
6. If the diagnostic reports failure, copy the report path from the result, preserve that report, stop testing that Revit version, and do not begin Phase 3.
7. If it reports success, confirm every listed invariant is `PASS` and record the target instance ID, type ID, name, and member count.
8. Confirm the target instance ID differs from the source instance ID.
9. Confirm the target type ID differs from the source type ID.
10. Confirm the source name and source type ID still match the values recorded before the command.
11. Confirm the source member count and ownership are unchanged.
12. Confirm every target member belongs to the target assembly.
13. Rename the target assembly to another unique value such as `CHW_TEST_101`.
14. Confirm the source assembly name does not change.
15. Save the disposable model, close Revit completely, and reopen the model.
16. Confirm both assemblies still exist with distinct instance IDs and type IDs.
17. Confirm the source retains its original name and the target retains `CHW_TEST_101`.
18. Change the target name once more and confirm the source remains unchanged.
19. Attach the generated diagnostic report and complete the results row below.

## Stop Conditions

Stop immediately and report the diagnostic output if any of these occurs:

- The source assembly name, type ID, member list, or member ownership changes.
- The target shares the source type ID.
- Renaming the target renames the source.
- Copied members remain assigned to the source assembly or are not assigned to the target.
- Revit creates extra copied dependency elements, and the diagnostic rolls back rather than guessing membership.
- The command leaves copied members or a partial target after reporting failure.
- Save/reload changes either assembly's identity relationship.

## Results

| Revit | Diagnostic | Source unchanged | Separate type | Rename independent | Save/reload independent | Report path / notes |
|---|---|---|---|---|---|---|
| 2021 | Not run | — | — | — | — | |
| 2022 | Not run | — | — | — | — | |
| 2023 | Not run | — | — | — | — | |
| 2024 | Not run | — | — | — | — | |
| 2025 | Not run | — | — | — | — | |
| 2026 | Not run | — | — | — | — | |

Phase 3 is authorized only after all required versions have passed or an explicitly approved version-specific alternative has been designed and verified.

After testing, close Revit and run `Development Tools\Disable-DevelopmentMode.cmd` unless development mode is still required for other work.
