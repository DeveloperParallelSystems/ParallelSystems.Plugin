# Duplicate Assemblies Phases 1–2 Design

## Goal

Establish the testable naming and preflight foundation for Duplicate Assemblies, then add an internal one-assembly Revit diagnostic that proves a copied assembly is independent from its source. Phases 3–6 remain out of scope until the user completes the Phase 2 manual verification.

## Repository Constraints

- Reuse the existing shared-project and six-version adapter structure.
- Target Revit 2021–2024 on .NET Framework 4.8 and Revit 2025–2026 on .NET 8 Windows.
- Reuse WPF and `AppDialog`; do not introduce an MVVM framework.
- Keep version-specific behavior behind the existing compatibility boundary or a focused adjacent adapter.
- Mark relevant additions with `// Created by Jhay` and modifications with `// Changed by Jhay`.
- Do not modify unrelated code or any source assembly, source type, source view, or source sheet.

## Phase 1: Pure Naming and Preflight Logic

Create Revit-independent types in `ParallelSystemsPlugin.Core` so the logic can be compiled and tested without loading Revit assemblies.

The parser finds the final contiguous decimal digit block. It returns the exact prefix, numeric text, numeric value, suffix, and original width. Names without a numeric block, values outside the supported integer range, and blank names are invalid rather than guessed.

Ordering uses the parsed final numeric value as the primary key, source name with ordinal-ignore-case natural comparison as the deterministic secondary key, and the stable source element identifier as the final key. Prefixes never affect primary ordering.

Sequence generation replaces only the parsed final numeric block. It pads to at least the source block width and never truncates overflow. Each selected source retains its own prefix and suffix.

Conflict validation receives pre-indexed existing assembly names, sheet numbers, and view names. It reports every conflict before model mutation. Safe documentation-name substitution replaces an exact occurrence of the source assembly token only; it does not perform arbitrary numeric replacement. If the source token is absent, the original documentation name remains unchanged and any uniqueness conflict is reported separately.

Tests cover the examples in the approved brief plus blank names, multiple numeric blocks, equal numeric values, padding overflow, case-insensitive conflicts, duplicate proposed names, and documentation names that do not contain the assembly token.

## Phase 2: Internal Model-Only Diagnostic

Add an explicitly internal/development diagnostic command, not the production Tools command. It accepts exactly one preselected or interactively selected `AssemblyInstance` and a destination test name supplied by the diagnostic UI or a conservative default.

Within a `TransactionGroup`, the diagnostic records immutable source evidence: instance ID, type ID, type name, member IDs, and member assembly ownership. It copies only the source member elements with `ElementTransformUtils.CopyElements`, using an identity transform in Phase 2. It validates that the returned elements are valid assembly members, selects the source naming category, and creates a fresh assembly with `AssemblyInstance.Create`.

The creation transaction is committed before renaming, following Autodesk's documented assembly lifecycle. A later transaction renames only the new assembly through `AssemblyTypeName`, regenerates, and verifies:

- the source instance still exists;
- source type ID and name are unchanged;
- source member IDs and ownership are unchanged;
- the target instance and type IDs differ from the source;
- every copied member belongs to the target assembly;
- the target name equals the diagnostic name; and
- a second regeneration does not change those results.

Any failed assertion rolls back the entire transaction group, leaving no copied members or temporary assembly. Success may be assimilated only when the diagnostic clearly reports the created IDs and reminds the tester that this is test-model-only behavior. Technical results are presented through the existing detailed branded dialog and written to a scoped diagnostic report suitable for comparing results after save/reload.

The implementation must not disassemble, rename, move, delete, or otherwise edit the source. It creates no views or sheets and performs no level reassociation.

## Failure Handling

- Selection cancellation returns `Cancelled` without a model transaction.
- Invalid selection, copy failure, invalid assembly membership, naming conflict, shared target type, or any source mutation fails the diagnostic and rolls back all created artifacts.
- Normal users receive a concise explanation; the diagnostic report includes IDs, API stage, and exception details.
- No Phase 2 result is treated as proof for all six versions until it has been manually exercised in each supported Revit host.

## Verification Gate

Phase 1 must pass its focused tests and the complete repository test suite. All six plugin projects must compile against their installed Revit APIs.

For Phase 2, the user must run the diagnostic on a disposable model in Revit 2021–2026, confirm source/target IDs and names, rename the target again, save, close, reopen, and confirm independence persists. Phase 3 may begin only after those results are reported as successful.

## Out of Scope

Destination-level reassociation, offset preservation, documentation recreation, annotation remapping, the production ribbon command, multi-assembly workflow, preview UI, and installer changes are deliberately deferred to Phases 3–6.
