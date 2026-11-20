<#
.SYNOPSIS
Creates a Manual-mode Home.dyn test copy; Preview selects the script's rollback path.
.DESCRIPTION
The original file is never overwritten. This changes the Boolean wired to IN[2]
in the supplied Home graph. Preview relies on the graph's Python implementation;
it is not a general sandbox or guarantee against external side effects.
#>
[CmdletBinding()]
param(
    [string] $Path = (Join-Path $PSScriptRoot '../Home.dyn'),
    [Parameter(Mandatory)] [string] $OutputPath,
    [ValidateSet('Preview', 'Commit')] [string] $Mode = 'Preview'
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Path).Path
$destination = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
if ([string]::Equals($source, $destination, [StringComparison]::OrdinalIgnoreCase)) { throw 'Choose a different output file; Home.dyn must remain unchanged.' }
if ([IO.Path]::GetExtension($destination) -ne '.dyn') { throw 'OutputPath must end with .dyn.' }
if (Test-Path -LiteralPath $destination) { throw 'OutputPath already exists. Choose a new filename for this test.' }
$graph = Get-Content -LiteralPath $source -Raw | ConvertFrom-Json
$python = @($graph.Nodes | Where-Object Id -eq 'a4f1b169b2b64e08b48d06cdac097ce9')
$boolean = @($graph.Nodes | Where-Object Id -eq 'a2b63a356a574f72955bc7571c25e2f6')
if ($python.Count -ne 1 -or $boolean.Count -ne 1 -or $python[0].Engine -ne 'CPython3' -or
    $boolean[0].ConcreteType -ne 'CoreNodeModels.Input.BoolSelector, CoreNodeModels' -or
    $python[0].Code -notmatch 'dry_run\s*=\s*\(len\(IN\)<3 or IN\[2\] is None or bool\(IN\[2\]\)\)') {
    throw 'Home graph structure or preview expression changed. Review it before preparing a test copy.'
}
$connector = @($graph.Connectors | Where-Object { $_.End -eq $python[0].Inputs[2].Id })
if ($connector.Count -ne 1 -or $connector[0].Start -ne $boolean[0].Outputs[0].Id) {
    throw 'Expected exactly one Boolean connection to Python IN[2].'
}
$boolean[0].InputValue = ($Mode -eq 'Preview')
$graph.View.Dynamo.RunType = 'Manual'
$graph.Uuid = [guid]::NewGuid().ToString()
$graph.Name = [IO.Path]::GetFileNameWithoutExtension($destination)
$json = $graph | ConvertTo-Json -Depth 100
$bytes = [Text.UTF8Encoding]::new($false).GetBytes($json)
$outputStream = [IO.File]::Open($destination, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $outputStream.Write($bytes, 0, $bytes.Length) } finally { $outputStream.Dispose() }
[pscustomobject]@{ path = $destination; mode = $Mode; pythonInput2 = $boolean[0].InputValue;
    sha256 = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() }
