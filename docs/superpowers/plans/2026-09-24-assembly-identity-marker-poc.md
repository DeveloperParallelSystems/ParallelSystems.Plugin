# Assembly Identity Marker POC Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prove in live Revit that two exact production-member copies receive independent assembly types when each includes one no-geometry Generic Model marker with a unique `FamilySymbol` type.

**Architecture:** A Revit-2021-authored `PS_AssemblyIdentity.rfa` ships as shared content and loads only when the command runs. Focused services resolve/load the family, generate unique symbol names, select a source-derived level, place one marker per target, and capture evidence. The existing diagnostic becomes a two-target transaction-group POC and removes the unsupported `AssemblyType.Duplicate()` path.

**Tech Stack:** C#/.NET Standard 2.0 core logic, C# shared Revit API code targeting Revit 2021–2026, Revit 2021 family authoring, MSBuild shared content, existing console-style LayoutTests, PowerShell build scripts.

**Spec:** `docs/superpowers/specs/2026-09-24-assembly-identity-marker-poc-design.md`

## Global Constraints

- Do not add another permanent project or restore `ParallelSystemsPlugin.CoreTests`.
- Preserve unrelated working-tree changes in fabrication configuration, fabrication selection, and existing LayoutTests.
- Author `ParallelSystemsPlugin.Shared/Families/PS_AssemblyIdentity.rfa` from `C:\ProgramData\Autodesk\RVT 2021\Family Templates\English\Metric Generic Model.rft` using Revit 2021.
- The RFA has one type named `PS_ASSEMBLY_IDENTITY_BASE` and contains no model/symbolic/detail geometry, connectors, nested families, voids, hosts, engineering parameters, or shared parameters.
- Runtime family loading uses only `Families/PS_AssemblyIdentity.rfa` relative to the deployed add-in assembly.
- Do not load the family at Revit startup.
- Each target receives exactly one marker and a different generated `FamilySymbol`.
- Do not modify source production members or source assembly/type/documentation.
- The complete POC is inside one `TransactionGroup`; any failed check rolls back copied members, markers, symbols, assemblies, renames, and a family loaded by that group.
- Stop after the identity-marker POC and manual persistence/contamination checks. Do not implement destination-level production logic, final batch UI, sheets, views, schedules, viewports, or annotations.

## Review Focus

- A project already contains a different family named `PS_AssemblyIdentity`: reject it without overwriting or deleting user content.
- Source assembly/member `LevelId` values are invalid or mixed: choose a deterministic real document level without using the active view.
- Target names contain punctuation or exceed practical symbol-name length: sanitize only the internal symbol name and retain GUID-based uniqueness.
- Family load, symbol creation, marker placement, assembly creation, rename, or validation returns a non-committed status: report the exact stage/status and roll back the whole group.
- The second target matches the first despite different marker symbols: fail the pairwise type-ID invariant and roll back both targets.

---

### Task 1: Author and deploy the Revit 2021 family asset

**Files:**
- Create: `ParallelSystemsPlugin.Shared/Families/PS_AssemblyIdentity.rfa`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`
- Temporary only: `.codex-rvt-family-authoring/*` (delete after verified RFA creation)

**Interfaces:**
- Produces: deployed relative asset `Families/PS_AssemblyIdentity.rfa`
- Produces: family `PS_AssemblyIdentity` with base symbol `PS_ASSEMBLY_IDENTITY_BASE`

- [ ] **Step 1: Add a pre-authoring check that fails while the asset is absent**

Run:

```powershell
if (-not (Test-Path -LiteralPath 'ParallelSystemsPlugin.Shared\Families\PS_AssemblyIdentity.rfa')) { throw 'Expected failure: identity RFA is absent.' }
```

Expected: FAIL with `identity RFA is absent`.

- [ ] **Step 2: Create a temporary Revit 2021 authoring harness**

Create a git-ignored temporary add-in beneath `.codex-rvt-family-authoring` that runs once in Revit 2021. Its Revit API handler must:

```csharp
const string templatePath =
    @"C:\ProgramData\Autodesk\RVT 2021\Family Templates\English\Metric Generic Model.rft";
const string outputPath =
    @"C:\Users\Edgar\Desktop\Projects\ParallelSystems\ParallelSystems.Plugin\ParallelSystemsPlugin.Shared\Families\PS_AssemblyIdentity.rfa";

using (Document familyDocument = application.NewFamilyDocument(templatePath))
{
    using (var transaction = new Transaction(familyDocument, "Name identity base type"))
    {
        transaction.Start();
        familyDocument.FamilyManager.RenameCurrentType("PS_ASSEMBLY_IDENTITY_BASE");
        if (transaction.Commit() != TransactionStatus.Committed)
            throw new InvalidOperationException("Family type rename did not commit.");
    }

    familyDocument.SaveAs(outputPath, new SaveAsOptions { OverwriteExistingFile = true });
    familyDocument.Close(false);
}
```

The harness must not add geometry, parameters, hosts, connectors, nested content, or work-plane settings. Build it against Revit 2021 API assemblies, install only its temporary `.addin`, launch Revit 2021, wait for a success evidence file plus the RFA, and remove the temporary manifest/harness after verification.

- [ ] **Step 3: Verify the authored binary exists and is non-empty**

Run:

```powershell
$rfa = Get-Item -LiteralPath 'ParallelSystemsPlugin.Shared\Families\PS_AssemblyIdentity.rfa'
if ($rfa.Length -lt 1024) { throw "Identity RFA is unexpectedly small: $($rfa.Length)" }
```

Expected: PASS.

- [ ] **Step 4: Add the family directory to shared content**

Add to the existing content item group in `ParallelSystemsPlugin.Shared.projitems`:

```xml
<Content Include="$(MSBuildThisFileDirectory)Families\**\*">
  <Link>Families\%(RecursiveDir)%(Filename)%(Extension)</Link>
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

- [ ] **Step 5: Build Revit 2021 without deployment and verify copied content**

Run:

```powershell
dotnet build 'ParallelSystemsPlugin.2021\ParallelSystemsPlugin.2021.csproj' -c Debug -p:Platform=x64 -p:DeployToRevitOnBuild=false
Test-Path -LiteralPath 'ParallelSystemsPlugin.2021\bin\x64\Debug\Families\PS_AssemblyIdentity.rfa'
```

Expected: build succeeds and `Test-Path` returns `True`.

- [ ] **Step 6: Commit the family asset and content rule**

```powershell
git add -- 'ParallelSystemsPlugin.Shared/Families/PS_AssemblyIdentity.rfa' 'ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems'
git commit -m 'feat: add assembly identity family asset'
```

---

### Task 2: Add collision-resistant marker-symbol naming

**Files:**
- Create: `ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyIdentityMarkerName.cs`
- Modify: `ParallelSystemsPlugin.LayoutTests/Program.cs`

**Interfaces:**
- Produces: `AssemblyIdentityMarkerName.Create(string visibleAssemblyName, Guid uniqueId): string`
- Consumes later: generated names beginning with `PS_ASM_ID_`, bounded to 120 characters, safe for `ElementType.Duplicate`

- [ ] **Step 1: Add failing tests to the existing LayoutTests program**

Add calls from `Main()` and test methods preserving all current unrelated tests:

```csharp
private static void AssemblyIdentityMarkerNamesAreSafeAndUnique()
{
    Guid first = Guid.Parse("7f3c18a2-0000-0000-0000-000000000000");
    Guid second = Guid.Parse("9a5d2b11-0000-0000-0000-000000000000");

    string a = AssemblyIdentityMarkerName.Create("CHW500", first);
    string b = AssemblyIdentityMarkerName.Create("CHW500", second);
    string unusual = AssemblyIdentityMarkerName.Create("CHW/500:{bad}|name", first);

    AssertEqual("PS_ASM_ID_CHW500_7F3C18A2", a, "first identity name");
    AssertEqual(false, string.Equals(a, b, StringComparison.Ordinal), "unique identity names");
    AssertEqual(false, unusual.IndexOfAny(new[] { '/', ':', '{', '}', '|' }) >= 0,
        "safe identity characters");
    AssertEqual(true, unusual.Length <= 120, "bounded identity name length");
}
```

- [ ] **Step 2: Run LayoutTests and observe the missing-type failure**

Run:

```powershell
dotnet run --project 'ParallelSystemsPlugin.LayoutTests\ParallelSystemsPlugin.LayoutTests.csproj' -c Debug -p:DeployToRevitOnBuild=false
```

Expected: compile failure because `AssemblyIdentityMarkerName` does not exist.

- [ ] **Step 3: Implement the pure naming helper**

Implement a static class that:

```csharp
public static string Create(string visibleAssemblyName, Guid uniqueId)
```

Rules:

- reject null/whitespace visible names;
- uppercase and retain letters, digits, `_`, and `-`;
- replace other runs with one `_`;
- trim separators;
- use `ASSEMBLY` if sanitization becomes empty;
- truncate the readable fragment so the final name is at most 120 characters;
- append the first eight uppercase GUID hex characters without braces/hyphens.

- [ ] **Step 4: Run LayoutTests and confirm all tests pass**

Expected: all existing layout/fabrication tests plus the marker-name test pass.

- [ ] **Step 5: Commit only the naming helper and the added test hunks**

```powershell
git add -- 'ParallelSystemsPlugin.Core/AssemblyDuplication/AssemblyIdentityMarkerName.cs' 'ParallelSystemsPlugin.LayoutTests/Program.cs'
git commit -m 'feat: add assembly identity marker naming'
```

---

### Task 3: Implement on-demand family resolution and deterministic marker placement

**Files:**
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityFamilyService.cs`
- Create: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityMarkerEvidence.cs`
- Modify: `ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems`

**Interfaces:**
- Produces: `AssemblyIdentityFamilyResolution ResolveOrLoad(Document document, string assemblyDirectory)`
- Produces: `AssemblyIdentityMarkerPlacement CreateMarker(Document document, FamilySymbol baseSymbol, string targetName, AssemblyInstance source, IReadOnlyCollection<ElementId> sourceMemberIds)`
- Produces evidence fields: family loaded flag/path/category/placement type, symbol ID/name, marker ID, origin, level ID/name/elevation, offset, final point

- [ ] **Step 1: Add a failing source-contract check**

Run before implementation:

```powershell
$required = @(
  'AssemblyIdentityFamilyService',
  'ResolveOrLoad',
  'CreateMarker',
  'FamilyPlacementType',
  'PS_ASSEMBLY_IDENTITY_BASE'
)
$text = Get-Content -Raw 'ParallelSystemsPlugin.Shared\AssemblyDuplication\AssemblyIdentityFamilyService.cs' -ErrorAction SilentlyContinue
foreach ($token in $required) { if ($text -notmatch [regex]::Escape($token)) { throw "Expected failure: missing $token" } }
```

Expected: FAIL because the service file is absent.

- [ ] **Step 2: Implement family lookup/load**

Use constants:

```csharp
internal const string FamilyName = "PS_AssemblyIdentity";
internal const string BaseSymbolName = "PS_ASSEMBLY_IDENTITY_BASE";
internal const string RelativeAssetPath = @"Families\PS_AssemblyIdentity.rfa";
```

Search `FilteredElementCollector(document).OfClass(typeof(Family))` by exact family name. If absent, load from `Path.Combine(assemblyDirectory, RelativeAssetPath)` in a named transaction and require `TransactionStatus.Committed`. Re-query after load. Reject a family whose category is not `OST_GenericModel`, whose base symbol is absent, or whose placement type is not `FamilyPlacementType.OneLevelBased`.

- [ ] **Step 3: Implement deterministic source level selection**

Create a private selector returning `Level`:

1. valid source `LevelId`;
2. most frequent valid source-member level, tie by `Math.Abs(level.Elevation - origin.Z)`, then stable ID;
3. closest document level, tie by stable ID;
4. fail if the document has no levels.

Do not inspect `ActiveView`.

- [ ] **Step 4: Implement unique symbol creation and marker placement**

Within the caller's open transaction:

```csharp
string symbolName = AssemblyIdentityMarkerName.Create(targetName, Guid.NewGuid());
FamilySymbol symbol = baseSymbol.Duplicate(symbolName) as FamilySymbol;
if (symbol == null) throw new InvalidOperationException("Identity symbol duplication failed.");
if (!symbol.IsActive) symbol.Activate();
document.Regenerate();

XYZ origin = source.GetTransform().Origin;
Level level = SelectLevel(document, source, sourceMemberIds, origin);
XYZ levelPoint = new XYZ(origin.X, origin.Y, level.Elevation);
FamilyInstance marker = document.Create.NewFamilyInstance(
    levelPoint, symbol, level, StructuralType.NonStructural);
Parameter offset = marker.get_Parameter(BuiltInParameter.INSTANCE_FREE_HOST_OFFSET_PARAM);
if (offset != null && !offset.IsReadOnly)
    offset.Set(origin.Z - level.Elevation);
document.Regenerate();
```

Require the resulting `LocationPoint.Point` to match the requested origin within `1e-6` feet in X/Y/Z. Capture all placement evidence.

- [ ] **Step 5: Run the source-contract check and compile Revit 2021 and 2026**

Expected: contract check passes; both boundary adapters compile with deployment disabled.

- [ ] **Step 6: Commit the focused family service**

```powershell
git add -- 'ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityFamilyService.cs' 'ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyIdentityMarkerEvidence.cs' 'ParallelSystemsPlugin.Shared/ParallelSystemsPlugin.Shared.projitems'
git commit -m 'feat: load and place assembly identity markers'
```

---

### Task 4: Expand evidence and diagnostic reporting for two targets

**Files:**
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticModels.cs`
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticReport.cs`

**Interfaces:**
- Produces result properties: `Target500After`, `Target501After`, `Target500Marker`, `Target501Marker`, `FamilyResolution`, `TransactionStages`, `ContaminationObservations`
- Produces member evidence that records category ID/name, type ID, marker classification, location/transform evidence, and owner assembly ID

- [ ] **Step 1: Add a failing report-contract check**

Run:

```powershell
$report = Get-Content -Raw 'ParallelSystemsPlugin.Shared\AssemblyDuplication\AssemblyDuplicationDiagnosticReport.cs'
foreach ($token in @('TARGET 500 AFTER','TARGET 501 AFTER','FAMILY RESOLUTION','MARKER 500','MARKER 501','TRANSACTION STAGES')) {
  if ($report -notmatch [regex]::Escape($token)) { throw "Expected failure: report missing $token" }
}
```

Expected: FAIL on the first missing section.

- [ ] **Step 2: Extend immutable evidence types**

Add explicit types instead of string bags:

```csharp
internal sealed class AssemblyIdentityFamilyResolution { /* path, loaded, category, placement */ }
internal sealed class AssemblyTransactionStage { public string Name; public TransactionStatus Status; }
internal sealed class AssemblyContaminationObservation { public string Surface; public string Result; }
```

Extend `AssemblyMemberEvidence` with `CategoryId`, `CategoryName`, `TypeId`, `IsIdentityMarker`, and point/transform fields needed for source-target comparison. Determine `IsIdentityMarker` only from a `FamilyInstance` whose `Symbol.Family.Name` exactly equals `PS_AssemblyIdentity`.

- [ ] **Step 3: Replace the single target result with two explicit targets**

Retain `SourceBefore` and `SourceAfter`; replace `TargetAfter` with `Target500After` and `Target501After`. Add family, marker, transaction-stage, and contamination evidence collections.

- [ ] **Step 4: Extend the text report with every mandated section**

Write both targets, pairwise type IDs, marker details, production/internal counts, exact paths, placement type/level/origin/offset/final point, rename probe, transaction statuses, and contamination observations. Print `not available in test model` explicitly when a relevant schedule/view is absent.

- [ ] **Step 5: Run the report-contract check and compile Revit 2025**

Expected: contract passes and Revit 2025 compiles.

- [ ] **Step 6: Commit evidence/report changes**

```powershell
git add -- 'ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticModels.cs' 'ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticReport.cs'
git commit -m 'feat: report two-target identity marker evidence'
```

---

### Task 5: Replace unsupported type duplication with the two-target marker POC

**Files:**
- Modify: `ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticService.cs`

**Interfaces:**
- Changes: `Run(Document document, AssemblyInstance source, string target500Name, string target501Name, string assemblyDirectory)`
- Consumes: `AssemblyIdentityFamilyService.ResolveOrLoad` and `.CreateMarker`
- Produces: pairwise-independent source/target evidence or a completely rolled-back result

- [ ] **Step 1: Add a failing structural regression check**

Run:

```powershell
$service = Get-Content -Raw 'ParallelSystemsPlugin.Shared\AssemblyDuplication\AssemblyDuplicationDiagnosticService.cs'
if ($service -match 'sourceType\.Duplicate') { throw 'Expected failure: unsupported AssemblyType duplication remains.' }
if ($service -notmatch 'Target500After' -or $service -notmatch 'Target501After') { throw 'Expected failure: two targets are absent.' }
if ($service -notmatch 'CreateMarker') { throw 'Expected failure: marker creation is absent.' }
```

Expected: FAIL because the unsupported call remains and the two-target flow is absent.

- [ ] **Step 2: Extract one-target creation into a private method**

Implement:

```csharp
private static CreatedAssemblyTarget CreateTarget(
    Document document,
    AssemblyInstance source,
    IReadOnlyList<ElementId> sourceProductionMemberIds,
    FamilySymbol baseMarkerSymbol,
    string targetName,
    string stagePrefix)
```

The method copies only source production IDs, requires exact copied count/loose state, creates one unique marker, validates the combined IDs, creates/commits the assembly, re-queries it, requires its type ID to differ from the source, names it, and returns copied IDs, marker evidence, assembly evidence, and transaction statuses.

- [ ] **Step 3: Run two target creations inside one transaction group**

Resolve/load the family after the group starts. Create target 500 and target 501 from the same unchanged source. Require:

```csharp
sourceTypeId != target500TypeId &&
sourceTypeId != target501TypeId &&
target500TypeId != target501TypeId
```

Fail and roll back both targets if any equality remains.

- [ ] **Step 4: Implement the rename independence probe**

In a transaction, rename target 500 to a GUID-suffixed probe name, regenerate, and require source and target 501 names remain unchanged. Rename target 500 back in a second committed transaction and verify all names again.

- [ ] **Step 5: Implement separated member validation**

For each target require exactly one `PS_AssemblyIdentity` member. Compare only non-marker target members against the copied production IDs. Validate category/type and relative-position evidence against the source; require every target member owner ID to equal its target assembly instance ID. Run validation twice with an intervening regeneration transaction.

- [ ] **Step 6: Add contamination observations without creating documentation**

Inspect existing project/assembly views and schedules with filtered collectors. Record whether the marker is returned by each available view/schedule collector and whether its geometry is empty under appropriate `Options`. Record absent surfaces as unavailable. Do not create or filter views/schedules.

- [ ] **Step 7: Require committed statuses and remove `AssemblyType.Duplicate` completely**

Wrap transaction start/commit through a focused helper that records each stage and throws unless commit returns `Committed`. Retain the outer catch/group rollback and report writing.

- [ ] **Step 8: Run structural regression check and compile Revit 2021, 2025, and 2026**

Expected: no unsupported call, both targets and marker creation present, all three adapters compile.

- [ ] **Step 9: Commit the two-target service**

```powershell
git add -- 'ParallelSystemsPlugin.Shared/AssemblyDuplication/AssemblyDuplicationDiagnosticService.cs'
git commit -m 'feat: create independent assemblies with identity markers'
```

---

### Task 6: Update the live command and preserve the original solution/ribbon shape

**Files:**
- Modify: `ParallelSystemsPlugin.Shared/Commands/RunAssemblyDuplicationDiagnosticCommand.cs`
- Modify: `ParallelSystemsPlugin.Shared/UI/ToolsMenu.cs`
- Modify: `ParallelSystemsPlugin.Shared/App.cs`
- Modify: `ParallelSystemsPlugin.sln`
- Delete: `ParallelSystemsPlugin.CoreTests/ParallelSystemsPlugin.CoreTests.csproj`
- Delete: `ParallelSystemsPlugin.CoreTests/Program.cs`

**Interfaces:**
- Consumes new two-target `Run` signature
- Keeps normal ribbon label `Duplicate Assemblies` in the existing Tools panel
- Removes only the added assembly diagnostic item from the pre-existing development-mode panel

- [ ] **Step 1: Update command target naming**

Use existing parser/naming logic to propose sequence 500 and 501 from the selected source. Pass both names plus `Path.GetDirectoryName(typeof(App).Assembly.Location)` to the service. Confirmation text must state that two assemblies and two internal identity marker types will be created and retained only if all checks pass.

- [ ] **Step 2: Update result details**

Show source, target 500, target 501, marker names, placement type, selected levels, pairwise type IDs, rename result, production/internal counts, contamination observations, and report path.

- [ ] **Step 3: Preserve approved ribbon/solution cleanup**

Keep the normal Tools button, remove the assembly button from development mode while preserving the original development indicator, and remove CoreTests from the solution/files. Do not modify any other solution project/configuration.

- [ ] **Step 4: Run static shape checks**

```powershell
if (Select-String -Quiet -Path 'ParallelSystemsPlugin.sln' -Pattern 'ParallelSystemsPlugin.CoreTests') { throw 'CoreTests remains.' }
if (Select-String -Quiet -Path 'ParallelSystemsPlugin.Shared\App.cs' -Pattern 'PS_AssemblyIndependenceDiagnostic') { throw 'Diagnostic remains in development mode.' }
if (-not (Select-String -Quiet -Path 'ParallelSystemsPlugin.Shared\UI\ToolsMenu.cs' -Pattern 'PS_DuplicateAssemblies')) { throw 'Tools button missing.' }
```

Expected: PASS.

- [ ] **Step 5: Commit command and repository-shape changes**

Stage only the listed files and their deletions. Do not stage unrelated fabrication changes.

```powershell
git commit -m 'feat: expose identity marker assembly POC'
```

---

### Task 7: Verify builds and hand off the live/save-reload POC

**Files:**
- Modify: `docs/testing/duplicate-assemblies-phase-2-manual-test.md`

**Interfaces:**
- Produces: exact Revit live-test and persistence-test procedure
- Produces: compile evidence for all six adapters

- [ ] **Step 1: Run existing LayoutTests with deployment disabled**

```powershell
dotnet run --project 'ParallelSystemsPlugin.LayoutTests\ParallelSystemsPlugin.LayoutTests.csproj' -c Debug -p:DeployToRevitOnBuild=false --no-restore
```

Expected: all existing tests and identity-name tests pass.

- [ ] **Step 2: Build all Revit versions through the original build script**

```powershell
& '.\Build\Build-All.ps1' -Configuration Debug -RevitVersion All -Deploy:$false
```

Expected: Revit 2021–2026 builds succeed; only known pre-existing warnings remain; no Autodesk host DLL is copied.

- [ ] **Step 3: Update the manual guide**

Document target-version deployment, selecting one disposable source assembly, required two-target success evidence, marker placement/family/type inspection, normal/assembly view checks, available schedule/part-list/BOM checks, Project Browser type checks, save/close/reopen, type-ID verification, isolated rename, restoration, and report retrieval.

- [ ] **Step 4: Deploy only the current development version after Revit is closed**

For the current Revit 2025 test:

```powershell
& '.\Build\Deploy-Revit-Full.ps1' -RevitVersion 2025 -Configuration Debug -KeepNuGetCache
```

Expected: deployed add-in contains the 2025 DLL and `Families/PS_AssemblyIdentity.rfa`.

- [ ] **Step 5: Run the live Revit 2025 POC**

Require a generated report showing source A, target 500 B, target 501 C, pairwise inequality, isolated rename, equal production evidence, one marker per target, and committed stages. If it fails, stop and diagnose the report; do not proceed to version boundaries.

- [ ] **Step 6: Run the manual save/reload and contamination checks**

Save under a test name, close/reopen, record all type IDs/names, rerun isolated rename, inspect marker visibility/schedules/part lists/BOM/selection/Project Browser, restore the name, and return results.

- [ ] **Step 7: After Revit 2025 succeeds, test runtime boundaries**

Repeat the POC in Revit 2021, 2024, and 2026. Add version-specific code only for an observed incompatibility.

- [ ] **Step 8: Commit the manual-test guide**

```powershell
git add -- 'docs/testing/duplicate-assemblies-phase-2-manual-test.md'
git commit -m 'docs: add identity marker POC verification'
```

- [ ] **Step 9: Final review**

Request a fresh review focused on rollback completeness, source immutability, family-name collision handling, deterministic placement, pairwise type independence, and absence of out-of-scope documentation creation. Fix Critical/Important findings with failing tests or reproducible live-host evidence, rerun the complete build, and report deferred Minor findings.
