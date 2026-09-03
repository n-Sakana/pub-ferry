[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$FerryArguments
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

function Get-FerryRuntimeKey {
    if ($PSVersionTable.PSEdition -eq 'Desktop') {
        return 'netfx48-win'
    }

    $platform = if ([System.IO.Path]::DirectorySeparatorChar -eq '\') { 'win' } else { 'unix' }
    return 'net{0}-{1}' -f [Environment]::Version.Major, $platform
}

function Get-FerryCacheBase {
    $cacheBase = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    if (-not [string]::IsNullOrWhiteSpace($cacheBase)) {
        return $cacheBase
    }

    if (-not [string]::IsNullOrWhiteSpace($env:XDG_CACHE_HOME)) {
        return $env:XDG_CACHE_HOME
    }

    $profileFolder = [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)
    if ([string]::IsNullOrWhiteSpace($profileFolder)) {
        throw 'Ferry could not find a per-user cache folder.'
    }

    return Join-Path $profileFolder '.cache'
}

function Get-FerrySourceHash {
    param(
        [System.IO.FileInfo[]]$SourceFiles,
        [string]$RuntimeKey
    )

    $hash = [System.Security.Cryptography.SHA256]::Create()
    try {
        $prefix = "ferry-add-type-cache-v1" + [char]0 + $RuntimeKey + [char]0
        $header = [Text.Encoding]::UTF8.GetBytes($prefix)
        [void]$hash.TransformBlock($header, 0, $header.Length, $header, 0)

        foreach ($file in $SourceFiles) {
            $name = [Text.Encoding]::UTF8.GetBytes($file.Name + [char]0)
            [void]$hash.TransformBlock($name, 0, $name.Length, $name, 0)

            $bytes = [IO.File]::ReadAllBytes($file.FullName)
            if ($bytes.Length -gt 0) {
                [void]$hash.TransformBlock($bytes, 0, $bytes.Length, $bytes, 0)
            }
        }

        $empty = New-Object byte[] 0
        [void]$hash.TransformFinalBlock($empty, 0, 0)
        return ([BitConverter]::ToString($hash.Hash)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Get-FerryCombinedSource {
    param([System.IO.FileInfo[]]$SourceFiles)

    $combined = ($SourceFiles | ForEach-Object {
        [IO.File]::ReadAllText($_.FullName, [Text.Encoding]::UTF8)
    }) -join [Environment]::NewLine

    $usingPattern = '(?m)^\s*using\s+[A-Za-z_][A-Za-z0-9_.]*\s*;\s*$'
    $usings = [regex]::Matches($combined, $usingPattern) |
        ForEach-Object { $_.Value.Trim() } |
        Sort-Object -Unique
    $body = [regex]::Replace($combined, $usingPattern, '')
    return ($usings -join [Environment]::NewLine) +
        [Environment]::NewLine + [Environment]::NewLine + $body
}

$sourceFiles = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.cs' -File | Sort-Object Name)
if ($sourceFiles.Count -eq 0) {
    throw "No C# source files were found beside ferry.ps1."
}

$runtimeKey = Get-FerryRuntimeKey
$sourceHash = Get-FerrySourceHash -SourceFiles $sourceFiles -RuntimeKey $runtimeKey
$cacheRoot = Join-Path (Get-FerryCacheBase) 'Ferry\add-type-cache-v1'
$cacheDirectory = Join-Path (Join-Path $cacheRoot $runtimeKey) $sourceHash
$assemblyPath = Join-Path $cacheDirectory 'Ferry.Host.dll'
$readyPath = Join-Path $cacheDirectory 'complete'

if (-not ((Test-Path -LiteralPath $assemblyPath -PathType Leaf) -and
          (Test-Path -LiteralPath $readyPath -PathType Leaf))) {
    [void](New-Item -ItemType Directory -Path $cacheDirectory -Force)
    $temporaryAssembly = Join-Path $cacheDirectory ("Ferry.Host.{0}.tmp.dll" -f $PID)

    try {
        Write-Host ("Compiling Ferry C# for {0}..." -f $runtimeKey)
        $source = Get-FerryCombinedSource -SourceFiles $sourceFiles
        Add-Type -TypeDefinition $source -OutputAssembly $temporaryAssembly -OutputType Library

        if (Test-Path -LiteralPath $assemblyPath -PathType Leaf) {
            Remove-Item -LiteralPath $assemblyPath -Force
        }
        Move-Item -LiteralPath $temporaryAssembly -Destination $assemblyPath
        [IO.File]::WriteAllText($readyPath, $sourceHash, [Text.Encoding]::ASCII)
    }
    finally {
        if (Test-Path -LiteralPath $temporaryAssembly -PathType Leaf) {
            Remove-Item -LiteralPath $temporaryAssembly -Force
        }
    }
}
else {
    Write-Host ("Using cached Ferry C# for {0}." -f $runtimeKey)
}

[void][Reflection.Assembly]::LoadFrom($assemblyPath)
$exitCode = [Ferry.Program]::Run($PSScriptRoot, [string[]]$FerryArguments)
exit $exitCode
