# common/install.ps1 -- build one .reg and import the Fin-Ferry menu (HKCU).
# Dot-source loads functions only; direct run performs the install.
Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:MenuName = "Fin-Ferry"
$script:KeyName = "Fin-Ferry"
$script:Launcher = Join-Path $PSScriptRoot "context-menu.vbs"
$script:FerryScript = Join-Path (Split-Path $PSScriptRoot -Parent) "ferry.ps1"
$script:MenuItems = @(
    [pscustomobject]@{
        Id = "01-optical"
        Label = (-join @([char]0x5149, [char]0x5B66, [char]0x8EE2, [char]0x9001))
        Mode = "optical"
    },
    [pscustomobject]@{
        Id = "02-markdown"
        Label = ("Markdown " + [char]0x5316)
        Mode = "markdown"
    },
    [pscustomobject]@{
        Id = "03-vba"
        Label = ("VBA " + [char]0x62BD + [char]0x51FA)
        Mode = "vba"
    }
)
$script:Contexts = @(
    "HKEY_CURRENT_USER\Software\Classes\*\shell",
    "HKEY_CURRENT_USER\Software\Classes\Directory\shell",
    "HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell"
)

function ConvertTo-RegString {
    param([string]$Value)
    return ($Value -replace "\\", "\\" -replace '"', '\"')
}

function Assert-HkcuPath {
    param([string]$Path)
    if (-not $Path.StartsWith(
            "HKEY_CURRENT_USER\",
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "only HKCU registry paths are allowed"
    }
}

function Invoke-RegExe {
    param([string[]]$CommandArguments)

    $previousErrorAction = $ErrorActionPreference
    $exitCode = 1
    $ErrorActionPreference = "Continue"
    try {
        & reg.exe @CommandArguments 2>&1 | Out-Null
        $exitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorAction
    }
    return $exitCode
}

function Get-MenuCommand {
    param([string]$Mode)
    if ($Mode -eq "optical") {
        $wscript = Join-Path $env:SystemRoot "System32\wscript.exe"
        return ('"{0}" "{1}" "optical" "%V"' -f $wscript, $script:Launcher)
    }

    $powershell = Join-Path $env:SystemRoot (
        "System32\WindowsPowerShell\v1.0\powershell.exe")
    return ('"{0}" -NoProfile -ExecutionPolicy Bypass -File "{1}" --cli --path "%V" --mode "{2}"' -f
        $powershell, $script:FerryScript, $Mode)
}

function Get-AppCommand {
    $wscript = Join-Path $env:SystemRoot "System32\wscript.exe"
    return ('"{0}" "{1}" "app"' -f $wscript, $script:Launcher)
}

function Build-RegistryLines {
    param([bool]$Install)

    $lines = New-Object "System.Collections.Generic.List[string]"
    $lines.Add("Windows Registry Editor Version 5.00")
    $lines.Add("")

    foreach ($context in $script:Contexts) {
        Assert-HkcuPath $context
        $key = $context + "\" + $script:KeyName
        $lines.Add("[-" + $key + "]")
        $lines.Add("")

        if ($Install) {
            $lines.Add("[" + $key + "]")
            $lines.Add('"MUIVerb"="' + (ConvertTo-RegString $script:MenuName) + '"')
            if ($context -eq "HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell") {
                $lines.Add("")
                $lines.Add("[" + $key + "\command]")
                $lines.Add('@="' + (ConvertTo-RegString (Get-AppCommand)) + '"')
                $lines.Add("")
            }
            else {
                $lines.Add('"subcommands"=""')
                $lines.Add("")

                foreach ($item in $script:MenuItems) {
                    $itemKey = $key + "\shell\" + $item.Id
                    $lines.Add("[" + $itemKey + "]")
                    $lines.Add('"MUIVerb"="' + (ConvertTo-RegString $item.Label) + '"')
                    $lines.Add("")
                    $lines.Add("[" + $itemKey + "\command]")
                    $lines.Add('@="' + (ConvertTo-RegString (Get-MenuCommand $item.Mode)) + '"')
                    $lines.Add("")
                }
            }
        }
    }

    return $lines.ToArray()
}

function Import-RegistryLines {
    param([string[]]$Lines, [string]$TemporaryRoot)

    $path = Join-Path $TemporaryRoot "fin-ferry.reg"
    [IO.File]::WriteAllLines($path, $Lines, [Text.Encoding]::Unicode)
    return (Invoke-RegExe -CommandArguments @("import", $path)) -eq 0
}

function Invoke-Install {
    $temporaryRoot = Join-Path $env:TEMP (
        "ferry_install_" + [Guid]::NewGuid().ToString("N"))
    try {
        if (-not (Test-Path -LiteralPath $script:Launcher -PathType Leaf)) {
            throw "context menu launcher not found: $script:Launcher"
        }
        if (-not (Test-Path -LiteralPath $script:FerryScript -PathType Leaf)) {
            throw "Ferry launcher not found: $script:FerryScript"
        }

        New-Item -ItemType Directory -Path $temporaryRoot -Force | Out-Null
        if (-not (Import-RegistryLines (Build-RegistryLines $true) $temporaryRoot)) {
            throw "registry import failed"
        }

        Write-Host ""
        Write-Host "Fin-Ferry was added to the file and folder context menus." -ForegroundColor Green
        return $true
    }
    catch {
        Write-Host ""
        Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
        return $false
    }
    finally {
        $registryFile = Join-Path $temporaryRoot "fin-ferry.reg"
        if (Test-Path -LiteralPath $registryFile -PathType Leaf) {
            Remove-Item -LiteralPath $registryFile -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $temporaryRoot -PathType Container) {
            Remove-Item -LiteralPath $temporaryRoot -Force -ErrorAction SilentlyContinue
        }
    }
}

if ($MyInvocation.InvocationName -ne ".") {
    if (-not (Invoke-Install)) { exit 1 }
    exit 0
}
