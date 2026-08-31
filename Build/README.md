# Build Folder README

This folder contains the shared build and deployment infrastructure for the multi-version Revit add-in solution.

The goal of the `Build` folder is to avoid duplicating the same MSBuild and deployment logic inside every Revit adapter project.

The solution supports:

- Revit 2021 — .NET Framework 4.8
- Revit 2022 — .NET Framework 4.8
- Revit 2023 — .NET Framework 4.8
- Revit 2024 — .NET Framework 4.8
- Revit 2025 — .NET 8 Windows
- Revit 2026 — .NET 8 Windows

The actual plugin code is maintained in:

```text
ParallelSystemsPlugin.Core
ParallelSystemsPlugin.Shared
```

The Revit year projects are thin adapter projects:

```text
ParallelSystemsPlugin.2021
ParallelSystemsPlugin.2022
ParallelSystemsPlugin.2023
ParallelSystemsPlugin.2024
ParallelSystemsPlugin.2025
ParallelSystemsPlugin.2026
```

The `Build` folder keeps the shared build behavior centralized.

---

## Folder Contents

```text
Build
├── Build-All.ps1
├── Build-All.cmd
├── RevitAdapter.Common.props
└── RevitAdapter.Common.targets
```

---

## Build-All.ps1

`Build-All.ps1` is the main helper script for building one or more Revit adapter projects.

It performs these steps:

1. Builds `ParallelSystemsPlugin.Core` first.
2. Checks which Revit versions are installed.
3. Builds only the requested and installed Revit adapters.
4. Optionally enables post-build deployment.
5. Fails clearly if nothing was built.

### Basic usage

Build all installed Revit versions in Debug mode:

```powershell
.\Build\Build-All.ps1
```

Build all installed Revit versions and deploy them:

```powershell
.\Build\Build-All.ps1 -Deploy
```

Build only Revit 2024:

```powershell
.\Build\Build-All.ps1 -RevitVersion 2024
```

Build and deploy only Revit 2025:

```powershell
.\Build\Build-All.ps1 -RevitVersion 2025 -Deploy
```

Build Release output for Revit 2026:

```powershell
.\Build\Build-All.ps1 -Configuration Release -RevitVersion 2026
```

Build all versions and fail when any requested Revit version is not installed:

```powershell
.\Build\Build-All.ps1 -RevitVersion All -FailOnMissingRevit
```

---

## Build-All.ps1 Parameters

### `-Configuration`

Controls the build configuration.

Allowed values:

```text
Debug
Release
```

Default:

```text
Debug
```

Example:

```powershell
.\Build\Build-All.ps1 -Configuration Release
```

---

### `-RevitVersion`

Controls which Revit adapter project to build.

Allowed values:

```text
All
2021
2022
2023
2024
2025
2026
```

Default:

```text
All
```

Example:

```powershell
.\Build\Build-All.ps1 -RevitVersion 2024
```

When `All` is used, the script checks Revit 2021 through Revit 2026 and builds only the versions installed on the machine.

---

### `-Deploy`

Enables post-build deployment.

Without `-Deploy`, the script only builds the adapter projects.

With `-Deploy`, the script passes this MSBuild property:

```text
DeployToRevitOnBuild=true
```

Example:

```powershell
.\Build\Build-All.ps1 -RevitVersion 2025 -Deploy
```

The adapter project then deploys the output to:

```text
%APPDATA%\Autodesk\Revit\Addins\<year>\ParallelSystemPlugin
```

and installs the `.addin` manifest to:

```text
%APPDATA%\Autodesk\Revit\Addins\<year>\ParallelSystemPlugin.addin
```

---

### `-FailOnMissingRevit`

Controls what happens when a requested Revit version is not installed.

Without this flag, missing Revit versions are skipped with a warning.

With this flag, the script fails immediately.

Example:

```powershell
.\Build\Build-All.ps1 -RevitVersion All -FailOnMissingRevit
```

Use this for CI/build servers where every requested Revit version is expected to be installed.

---

## Build-All.cmd

`Build-All.cmd` is a Windows command-line wrapper around `Build-All.ps1`.

It exists for convenience if you want to run the build from Command Prompt instead of PowerShell.

PowerShell is still the preferred way to run the build because it supports named parameters clearly.

---

## RevitAdapter.Common.props

`RevitAdapter.Common.props` contains shared project configuration used by all Revit adapter projects.

This file exists so that the same project settings do not have to be repeated in all six `.csproj` files.

Typical responsibilities include:

- Common build settings
- x64 platform settings
- WPF support
- Windows Forms support if needed
- Shared root namespace
- Shared output behavior
- Revit installation path logic
- Revit API references
- Shared package references
- Core project reference
- Shared source inclusion from `ParallelSystemsPlugin.Shared`

The Revit adapter projects import this file.

Example:

```xml
<Import Project="..\Build\RevitAdapter.Common.props" />
```

### Why this matters

If a Revit API reference, package, output rule, or shared source rule needs to change, it should be changed once here instead of six times.

---

## RevitAdapter.Common.targets

`RevitAdapter.Common.targets` contains shared build targets used by all Revit adapter projects.

Typical responsibilities include:

- Validating that the expected Revit API files exist
- Preventing Autodesk host DLLs from being copied into the plugin output
- Running post-build deployment when enabled
- Copying built plugin files into the Revit Addins folder
- Copying the `.addin` manifest
- Printing clear build/deployment messages

The Revit adapter projects import this file.

Example:

```xml
<Import Project="..\Build\RevitAdapter.Common.targets" />
```

---

## Post-Build Deployment

Post-build deployment is controlled by the MSBuild property:

```text
DeployToRevitOnBuild
```

When it is `true`, the adapter project deploys after building.

When it is `false`, the project only builds.

### Enable deployment from the build script

```powershell
.\Build\Build-All.ps1 -RevitVersion 2024 -Deploy
```

### Disable deployment explicitly

```powershell
dotnet build .\ParallelSystemsPlugin.2024\ParallelSystemsPlugin.2024.csproj `
  -c Debug `
  -p:Platform=x64 `
  -p:DeployToRevitOnBuild=false
```

---

## Deployment Output

For Revit 2024, deployment goes to:

```text
%APPDATA%\Autodesk\Revit\Addins\2024\ParallelSystemPlugin
%APPDATA%\Autodesk\Revit\Addins\2024\ParallelSystemPlugin.addin
```

For Revit 2025, deployment goes to:

```text
%APPDATA%\Autodesk\Revit\Addins\2025\ParallelSystemPlugin
%APPDATA%\Autodesk\Revit\Addins\2025\ParallelSystemPlugin.addin
```

The same pattern is used for 2021, 2022, 2023, and 2026.

---

## Files That Must Not Be Deployed

The build/deployment logic must never copy Autodesk host assemblies into the plugin output or Revit Addins folder.

These files belong to Revit itself:

```text
RevitAPI.dll
RevitAPIUI.dll
AdWindows.dll
```

Copying them beside the plugin can cause:

- Add-in load failures
- Assembly version conflicts
- Revit startup crashes
- Different behavior between Revit versions

The shared MSBuild targets should reject or exclude those files.

---

## Typical Developer Workflow

### Build and test one Revit version

```powershell
.\Build\Build-All.ps1 -Configuration Debug -RevitVersion 2024 -Deploy
```

Then start Revit 2024 and test the plugin.

---

### Build all installed versions

```powershell
.\Build\Build-All.ps1 -Configuration Debug
```

---

### Build all installed versions and deploy

```powershell
.\Build\Build-All.ps1 -Configuration Debug -Deploy
```

---

### Release build for installer packaging

```powershell
.\Build\Build-All.ps1 -Configuration Release
```

For installer builds, deployment should normally stay disabled.

The installer should package the Release build outputs directly.

---

## Important Notes

### Close Revit before deploying

Before using `-Deploy`, close every running Revit instance.

Revit can lock plugin DLLs while they are loaded. Deploying over locked DLLs can leave the add-in folder in a mixed or broken state.

---

### Build Core first

`Build-All.ps1` builds `ParallelSystemsPlugin.Core` first on purpose.

If Core fails, the adapter projects may show a misleading error like:

```text
Metadata file 'ParallelSystemsPlugin.Core.dll' could not be found
```

That error is usually not the root cause. The real error is normally in the Core build output above it.

---

### Missing Revit versions are skipped by default

When building `All`, missing Revit versions are skipped unless `-FailOnMissingRevit` is used.

This lets a developer with only Revit 2024 and 2025 installed still build those two adapters without installing every supported Revit version.

---

## When to edit files in this folder

Edit the `Build` folder when changing:

- Build behavior
- Deployment behavior
- Shared Revit API reference logic
- Shared package references
- Output folder rules
- Add-in copy rules
- Validation rules

Do not edit the `Build` folder when changing plugin behavior.

Plugin behavior should be changed in:

```text
ParallelSystemsPlugin.Core
ParallelSystemsPlugin.Shared
```

---

## Maintenance Rule

Use this rule to avoid creating technical debt:

```text
Plugin feature change       → edit Core or Shared
Revit API compatibility     → edit Shared\Compatibility
Project identity/version    → edit the specific ParallelSystemsPlugin.<year> project
Build/deployment behavior   → edit the Build folder
```

The `Build` folder exists to keep the six Revit adapter projects consistent and prevent duplicated build logic from drifting over time.
