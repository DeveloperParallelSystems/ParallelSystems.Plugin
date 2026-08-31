[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',
    [string]$RevitInstallDir,
    [switch]$SkipBuild,
    [switch]$KeepNuGetCache
)

$script = Join-Path $PSScriptRoot 'Deploy-Revit-Full.ps1'
$argsList = @('-RevitVersion', '2025', '-Configuration', $Configuration)
if (-not [string]::IsNullOrWhiteSpace($RevitInstallDir)) { $argsList += @('-RevitInstallDir', $RevitInstallDir) }
if ($SkipBuild) { $argsList += '-SkipBuild' }
if ($KeepNuGetCache) { $argsList += '-KeepNuGetCache' }
& $script @argsList
exit $LASTEXITCODE
