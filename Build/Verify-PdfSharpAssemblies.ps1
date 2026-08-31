[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Folder,

    [string]$ExpectedToken = 'f94615aa0424f9eb',

    [string]$ExpectedVersion = '6.2.4.0'
)

$ErrorActionPreference = 'Stop'

function Get-AssemblyPublicKeyTokenText {
    param([Parameter(Mandatory = $true)][string]$Path)

    $name = [System.Reflection.AssemblyName]::GetAssemblyName($Path)
    $tokenBytes = $name.GetPublicKeyToken()

    if ($null -eq $tokenBytes -or $tokenBytes.Length -eq 0) {
        return ''
    }

    return ([System.BitConverter]::ToString($tokenBytes)).Replace('-', '').ToLowerInvariant()
}

if (-not (Test-Path $Folder)) {
    throw "Output folder was not found: $Folder"
}

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

$assembliesToCheck = Get-ChildItem -Path $Folder -File -Filter '*.dll' |
    Where-Object { $_.Name -like 'PdfSharp*.dll' -or $_.Name -like 'MigraDoc*.dll' }

if (-not $assembliesToCheck -or $assembliesToCheck.Count -eq 0) {
    throw "No PDFsharp/MigraDoc assemblies were found in: $Folder"
}

$bad = @()
foreach ($dll in $assembliesToCheck) {
    $assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($dll.FullName)
    $token = Get-AssemblyPublicKeyTokenText -Path $dll.FullName
    $version = $assemblyName.Version.ToString()

    if ([string]::IsNullOrWhiteSpace($token)) {
        $bad += "$($dll.Name): UNSIGNED / no public key token"
        continue
    }

    if ($token -ne $ExpectedToken) {
        $bad += "$($dll.Name): token=$token expected=$ExpectedToken"
        continue
    }

    if ($version -ne $ExpectedVersion) {
        $bad += "$($dll.Name): version=$version expected=$ExpectedVersion"
        continue
    }
}

if ($bad.Count -gt 0) {
    $details = $bad -join [Environment]::NewLine
    throw @"
Invalid PDFsharp/MigraDoc DLL set detected in:
$Folder

$details

Fix:
1. Close Revit.
2. Delete bin/obj for the affected project.
3. Restore PDFsharp-MigraDoc-GDI 6.2.4 from official NuGet.
4. Rebuild and deploy the full output folder.

Do not copy only ParallelSystemsPlugin.2025.dll.
"@
}

Write-Host "PDFsharp/MigraDoc strong-name validation passed: $Folder" -ForegroundColor Green
