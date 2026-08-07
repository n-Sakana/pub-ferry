# Runs the C# path validator over a list of paths and prints its verdicts.
#
# Exists so tests/path-parity.test.ts can hold the two implementations —
# shared/relative-path.ts and pc/src/03_SafePath.cs — to the same answers on the
# same inputs. Two receivers disagreeing about the same bundle is the failure
# this pair of validators exists to prevent, and only running both catches it.
#
#   tools\check-safepath.ps1 -InputFile paths.json -OutputFile verdicts.json
#
# The input is a JSON array of UTF-16 CODE UNIT arrays, not of strings: a
# lone surrogate does not survive a JSON string round trip (the deserializer
# quietly repairs it), and lone surrogates are exactly one of the cases the two
# validators have to agree on. Code units transport verbatim.
#
# The output is a JSON array of { ok: bool, reason: string|null }, same order.

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$InputFile,
    [Parameter(Mandatory = $true)][string]$OutputFile
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Web.Extensions
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

$repoDir = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repoDir 'pc\src\03_SafePath.cs'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
    throw "Not found: $source"
}

if ($null -eq ('PubTransfer.SafePath' -as [type])) {
    Add-Type -TypeDefinition ([System.IO.File]::ReadAllText($source, [System.Text.Encoding]::UTF8)) -Language CSharp
}

# Read as UTF-8 and parse by hand rather than with ConvertFrom-Json: the point
# of this harness is to hand the C# side the EXACT code units the TypeScript
# side saw, including lone surrogates, and a round trip through a JSON cmdlet
# is not something to take on trust here.
$json = [System.IO.File]::ReadAllText($InputFile, (New-Object System.Text.UTF8Encoding($false)))
$reader = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$unitLists = $reader.Deserialize($json, [int[][]])
$paths = @()
foreach ($units in $unitLists) {
    $builder = New-Object System.Text.StringBuilder
    foreach ($unit in $units) { [void]$builder.Append([char]$unit) }
    $paths += $builder.ToString()
}

$serializer = New-Object System.Web.Script.Serialization.JavaScriptSerializer
$parts = New-Object System.Collections.ArrayList
$count = 0
foreach ($path in $paths) {
    $reason = $null
    $ok = $false
    try {
        $ok = [PubTransfer.SafePath]::Check($path, [ref]$reason)
    }
    catch {
        # A validator that throws has already failed: the caller cannot tell
        # "unsafe" from "crashed". Recorded as its own verdict so the parity
        # test reports it rather than dying.
        $ok = $false
        $reason = 'threw: ' + $_.Exception.GetType().Name
    }
    # Built by hand. Handing a PSCustomObject to the serializer walks its
    # PSMethod members and dies on a circular reference; only the two values
    # matter here, and escaping one string is cheaper than the alternative.
    $reasonJson = if ($null -eq $reason) { 'null' } else { $serializer.Serialize([string]$reason) }
    [void]$parts.Add(('{"ok":' + $(if ($ok) { 'true' } else { 'false' }) + ',"reason":' + $reasonJson + '}'))
    $count++
}

$out = '[' + ($parts -join ',') + ']'
[System.IO.File]::WriteAllText($OutputFile, $out, (New-Object System.Text.UTF8Encoding($false)))
Write-Output ("{0} 件を判定しました。" -f $count)
