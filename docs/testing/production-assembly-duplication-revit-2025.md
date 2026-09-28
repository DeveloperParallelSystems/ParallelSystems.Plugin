# Production Assembly Duplication — Revit 2025 Manual Acceptance

Use a disposable representative project and retain the generated batch and diagnostic reports. This checklist verifies runtime behavior that cannot be proven by compilation alone.

## Preview and selection

- Window-select members from multiple assemblies plus unrelated model elements. Verify one preview row per owning `AssemblyInstance`, no component rows, and no duplicate assemblies.
- Verify the initial Starting Number is `0` and preview calculates when the dialog opens.
- Clear the Starting Number and enter invalid, negative, and overflow values. Verify existing source rows remain visible with `Invalid` status and Confirm remains disabled.
- Verify rows are ordered by the source's original final numeric value, then source name, then ElementId—not selection order.
- Verify the dialog fills its client area on first open, the grid grows and shrinks with the window, the status area scrolls, and the action buttons stay visible.
- Cancel and verify zero model changes.

## Complete-name numbering and anchors

- Seed an occupied complete target name and verify only that complete name is skipped. Verify the same numeric value remains available to a different complete target name.
- Select sources with different prefixes/suffixes and verify original padding and final-numeric-block replacement are preserved.
- Edit an Assigned Number in a middle row. Verify rows above remain unchanged, that row records a manual anchor, and lower rows continue sequentially.
- Add a second lower anchor and verify it starts a new sequence without changing earlier rows.
- Make an anchor's complete target name conflict. Verify the row retains the requested anchor, displays the first available resolved number, lists every skipped complete-name conflict, and lower rows continue from the resolved number.
- Confirm that the displayed immutable plan is the plan execution uses; an intervening target-name conflict must reject the stale plan without renumbering or mutation.

## Successful batch

- Repeat the validated 18-assembly batch to Level 3 and verify the whole `TransactionGroup` commits.
- For every source verify: source unchanged; exact confirmed target name; independent AssemblyInstance and AssemblyType; correct destination level and world delta; preserved source-relative offsets; category/type; MEP topology; Pipe Insulation host relationships; validated host-dependent dependencies; one internal identity marker; and complete aggregate validation.
- Run a 100+ assembly batch and verify preview responsiveness remains acceptable and the batch report contains bounded per-row results.

## Rollback and exception safety

- Force a strict validation failure on a later row. Verify processing stops at that row, later rows are not attempted, and the entire batch—including earlier targets, copied/dependent members, markers, marker types, names, level changes, and a family loaded only by the batch—is rolled back.
- Force or reproduce an unexpected exception. Verify no transaction/group remains open, zero batch changes commit, and the result/report identify the source, stage, exception, and rollback status.

## Locked regression

- Run the existing Revit 2025 single-assembly destination-level diagnostic and require `93 PASS / 0 FAIL`.
- Confirm the production batch report has no unclassified target members and no weakened geometry, topology, level, offset, insulation, production-evidence, or identity validation.

## Phase boundary

Assembly sheets, views, schedules, annotations, documentation, and cleanup beyond rollback remain future Assembly Duplication roadmap work and are not part of this acceptance run.
