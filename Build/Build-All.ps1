[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [ValidateSet('All', '2021', '2022', '2023', '2024', '2025', '2026')]
    [string]$RevitVersion = 'All',

    [switch]$Deploy,

    [switch]$FailOnMissingRevit
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$deployValue = if ($Deploy) { 'true' } else { 'false' }
$years = if ($RevitVersion -eq 'All') { 2021..2026 } else { @([int]$RevitVersion) }

# Build Core once so adapter failures are never hidden behind a missing metadata message.
$coreProject = Join-Path $root 'ParallelSystemsPlugin.Core\ParallelSystemsPlugin.Core.csproj'
& dotnet build $coreProject --configuration $Configuration --property:Platform=x64
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$built = 0
foreach ($year in $years) {
    $revitDir = Join-Path $env:ProgramW6432 "Autodesk\Revit $year"
    $api = Join-Path $revitDir 'RevitAPI.dll'
    if (-not (Test-Path -LiteralPath $api)) {
        $message = "Revit $year is not installed at '$revitDir'; skipping its adapter."
        if ($FailOnMissingRevit) { throw $message }
        Write-Warning $message
        continue
    }

    $project = Join-Path $root "ParallelSystemsPlugin.$year\ParallelSystemsPlugin.$year.csproj"
    Write-Host "Building Revit $year adapter: $project"
    & dotnet build $project `
        --configuration $Configuration `
        --property:Platform=x64 `
        --property:DeployToRevitOnBuild=$deployValue

    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $built++
}

if ($built -eq 0) {
    throw 'No Revit adapter was built. Install at least one supported Revit version or specify the correct RevitInstallDir when building a project directly.'
}
