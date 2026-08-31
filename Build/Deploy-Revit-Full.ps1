[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('2021', '2022', '2023', '2024', '2025', '2026')]
    [string]$RevitVersion,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$RevitInstallDir,

    [switch]$SkipBuild,

    [switch]$KeepNuGetCache
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "ParallelSystemsPlugin.$RevitVersion\ParallelSystemsPlugin.$RevitVersion.csproj"
$source = Join-Path $root "ParallelSystemsPlugin.$RevitVersion\bin\x64\$Configuration"
$obj = Join-Path $root "ParallelSystemsPlugin.$RevitVersion\obj"
$addinRoot = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitVersion"
$target = Join-Path $addinRoot 'ParallelSystemPlugin'
$manifestSource = Join-Path $root "ParallelSystemsPlugin.$RevitVersion\Addin\ParallelSystemPlugin.addin"
$manifestTarget = Join-Path $addinRoot 'ParallelSystemPlugin.addin'

function Invoke-CheckedCommand {
    param(
        [Parameter(Mandatory = $true)][string]$FilePath,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')" -ForegroundColor Cyan
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Command failed with exit code $LASTEXITCODE: $FilePath $($Arguments -join ' ')"
    }
}

function Get-NuGetGlobalPackagesFolder {
    $output = & dotnet nuget locals global-packages --list 2>$null
    if ($LASTEXITCODE -ne 0) {
        return $null
    }

    foreach ($line in $output) {
        if ($line -match '^global-packages:\s*(.+)$') {
            return $Matches[1].Trim()
        }
    }

    return $null
}

function Clear-PdfSharpNuGetCache {
    if ($KeepNuGetCache) {
        Write-Host 'Keeping NuGet global cache because -KeepNuGetCache was supplied.' -ForegroundColor Yellow
        return
    }

    $globalPackages = Get-NuGetGlobalPackagesFolder
    if ([string]::IsNullOrWhiteSpace($globalPackages) -or -not (Test-Path $globalPackages)) {
        Write-Host 'NuGet global packages folder was not found; skipping PDFsharp cache cleanup.' -ForegroundColor Yellow
        return
    }

    foreach ($pattern in @('pdfsharp*', 'migradoc*')) {
        Get-ChildItem -Path $globalPackages -Directory -Filter $pattern -ErrorAction SilentlyContinue |
            ForEach-Object {
                Write-Host "Removing cached package: $($_.FullName)" -ForegroundColor Yellow
                Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
            }
    }
}

function Remove-StalePdfFiles {
    param([Parameter(Mandatory = $true)][string]$Folder)

    if (-not (Test-Path $Folder)) {
        return
    }

    $patterns = @(
        'PdfSharp*.dll',
        'MigraDoc*.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Bcl.AsyncInterfaces.dll',
        'System.Security.Cryptography.Pkcs.dll',
        'System.Memory.dll',
        'System.Buffers.dll',
        'System.Numerics.Vectors.dll',
        'System.Runtime.CompilerServices.Unsafe.dll',
        'System.Threading.Tasks.Extensions.dll'
    )

    foreach ($pattern in $patterns) {
        Remove-Item (Join-Path $Folder $pattern) -Force -ErrorAction SilentlyContinue
    }
}

function Assert-PdfFilesPresent {
    param([Parameter(Mandatory = $true)][string]$Folder)

    $required = @(
        'PdfSharp.System.dll',
        'PdfSharp-gdi.dll',
        'MigraDoc.DocumentObjectModel.dll',
        'MigraDoc.Rendering-gdi.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll'
    )

    foreach ($file in $required) {
        $path = Join-Path $Folder $file
        if (-not (Test-Path $path)) {
            throw "Required PDF export dependency is missing: $path"
        }
    }
}

if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
    throw 'Close all Revit sessions before deploying. Revit can lock old PDFsharp/MigraDoc DLLs.'
}

if (-not (Test-Path $project)) {
    throw "Project file was not found: $project"
}

if (-not $SkipBuild) {
    Write-Host "Cleaning stale Revit $RevitVersion output..." -ForegroundColor Yellow
    Remove-StalePdfFiles -Folder $source

    if (Test-Path $obj) {
        Remove-Item $obj -Recurse -Force -ErrorAction SilentlyContinue
    }

    Clear-PdfSharpNuGetCache

    $restoreArgs = @(
        'restore',
        $project,
        '--force',
        '--no-cache',
        '--source',
        'https://api.nuget.org/v3/index.json'
    )
    if (-not [string]::IsNullOrWhiteSpace($RevitInstallDir)) {
        $restoreArgs += "-p:RevitInstallDir=$RevitInstallDir"
    }
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments $restoreArgs

    $buildArgs = @(
        'build',
        $project,
        '-c',
        $Configuration,
        '--no-restore',
        '-p:Platform=x64',
        '-p:DeployToRevitOnBuild=false'
    )
    if (-not [string]::IsNullOrWhiteSpace($RevitInstallDir)) {
        $buildArgs += "-p:RevitInstallDir=$RevitInstallDir"
    }
    Invoke-CheckedCommand -FilePath 'dotnet' -Arguments $buildArgs
}

if (-not (Test-Path $source)) {
    throw "Build output folder was not found: $source"
}

Assert-PdfFilesPresent -Folder $source

if (-not (Test-Path $manifestSource)) {
    throw "Manifest file was not found: $manifestSource"
}

New-Item -ItemType Directory -Force -Path $addinRoot | Out-Null

if (Test-Path $target) {
    Write-Host "Removing old deployed add-in folder: $target" -ForegroundColor Yellow
    Remove-Item $target -Recurse -Force
}

New-Item -ItemType Directory -Force -Path $target | Out-Null
Copy-Item -Path (Join-Path $source '*') -Destination $target -Recurse -Force
Copy-Item -Path $manifestSource -Destination $manifestTarget -Force

Assert-PdfFilesPresent -Folder $target

Write-Host "Deployed Revit $RevitVersion add-in:" -ForegroundColor Green
Write-Host "  $target"
Write-Host 'Manifest:'
Write-Host "  $manifestTarget"
Write-Host "Restart Revit $RevitVersion, then run Export BOM again."
