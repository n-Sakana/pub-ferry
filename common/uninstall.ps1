# common/uninstall.ps1 -- interactively remove HKCU context-menu registrations.
[CmdletBinding()]
param(
    [switch]$ListOnly
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = "Stop"

$script:FerryRoot = Split-Path $PSScriptRoot -Parent
$script:Contexts = @(
    [pscustomobject]@{
        Id = "file"
        Label = "File"
        RelativePath = "Software\Classes\*\shell"
        RegistryPath = "HKEY_CURRENT_USER\Software\Classes\*\shell"
    },
    [pscustomobject]@{
        Id = "folder"
        Label = "Folder"
        RelativePath = "Software\Classes\Directory\shell"
        RegistryPath = "HKEY_CURRENT_USER\Software\Classes\Directory\shell"
    },
    [pscustomobject]@{
        Id = "background"
        Label = "Folder background"
        RelativePath = "Software\Classes\Directory\Background\shell"
        RegistryPath = "HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell"
    }
)

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

function Add-UniqueValue {
    param([System.Collections.ArrayList]$List, [string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }
    if (@($List | Where-Object { $_ -eq $Value }).Count -eq 0) {
        [void]$List.Add($Value)
    }
}

function Read-RegistryString {
    param($Key, [string]$Name)
    $value = $Key.GetValue(
        $Name,
        $null,
        [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
    if ($null -eq $value) {
        return $null
    }
    return [string]$value
}

function Add-CommandValues {
    param($Key, [System.Collections.ArrayList]$Values)

    foreach ($subKeyName in $Key.GetSubKeyNames()) {
        $subKey = $Key.OpenSubKey($subKeyName)
        if ($null -eq $subKey) {
            continue
        }
        try {
            if ($subKeyName -eq "command") {
                Add-UniqueValue $Values (Read-RegistryString $subKey "")
            }
            Add-CommandValues $subKey $Values
        }
        finally {
            $subKey.Dispose()
        }
    }
}

function Add-CommandPaths {
    param([string]$Command, [System.Collections.ArrayList]$Paths)

    $first = [Text.RegularExpressions.Regex]::Match(
        $Command,
        '^\s*(?:"(?<path>[^"]+)"|(?<path>\S+))')
    if ($first.Success) {
        $executable = $first.Groups["path"].Value
        if ([IO.Path]::IsPathRooted($executable)) {
            Add-UniqueValue $Paths ([Environment]::ExpandEnvironmentVariables($executable))
        }
        else {
            $resolved = Get-Command $executable -CommandType Application -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if ($null -ne $resolved) {
                Add-UniqueValue $Paths $resolved.Source
            }
        }
    }

    $quoted = [Text.RegularExpressions.Regex]::Matches(
        $Command,
        '"(?<path>[A-Za-z]:\\[^"]+)"')
    foreach ($match in $quoted) {
        $path = [Environment]::ExpandEnvironmentVariables(
            $match.Groups["path"].Value)
        if ($path.IndexOf("%V", [StringComparison]::OrdinalIgnoreCase) -lt 0) {
            Add-UniqueValue $Paths $path
        }
    }
}

function Get-Registrations {
    $groups = @{}
    $currentUser = [Microsoft.Win32.Registry]::CurrentUser

    foreach ($context in $script:Contexts) {
        $contextRoot = $currentUser.OpenSubKey($context.RelativePath)
        if ($null -eq $contextRoot) {
            continue
        }
        try {
            foreach ($keyName in $contextRoot.GetSubKeyNames()) {
                $registration = $contextRoot.OpenSubKey($keyName)
                if ($null -eq $registration) {
                    continue
                }
                try {
                    if (-not $groups.ContainsKey($keyName)) {
                        $groups[$keyName] = [pscustomobject]@{
                            KeyName = $keyName
                            DisplayName = $keyName
                            Contexts = New-Object System.Collections.ArrayList
                            ContextDetails = New-Object System.Collections.ArrayList
                            ContextIds = New-Object System.Collections.ArrayList
                            RegistryPaths = New-Object System.Collections.ArrayList
                            ChildNames = New-Object System.Collections.ArrayList
                            Commands = New-Object System.Collections.ArrayList
                            CommandPaths = New-Object System.Collections.ArrayList
                        }
                    }
                    $group = $groups[$keyName]
                    $displayName = Read-RegistryString $registration "MUIVerb"
                    if (-not [string]::IsNullOrWhiteSpace($displayName)) {
                        $group.DisplayName = $displayName
                    }
                    Add-UniqueValue $group.Contexts $context.Label
                    Add-UniqueValue $group.ContextIds $context.Id
                    Add-UniqueValue $group.RegistryPaths (
                        $context.RegistryPath + "\" + $keyName)

                    $childCount = 0
                    $children = $registration.OpenSubKey("shell")
                    if ($null -ne $children) {
                        try {
                            $childNames = @($children.GetSubKeyNames())
                            $childCount = $childNames.Count
                            foreach ($childName in $childNames) {
                                Add-UniqueValue $group.ChildNames $childName
                            }
                        }
                        finally {
                            $children.Dispose()
                        }
                    }
                    Add-UniqueValue $group.ContextDetails (
                        "{0} ({1} children)" -f
                        $context.Label,
                        $childCount)
                    Add-CommandValues $registration $group.Commands
                }
                finally {
                    $registration.Dispose()
                }
            }
        }
        finally {
            $contextRoot.Dispose()
        }
    }

    $result = @()
    foreach ($group in $groups.Values) {
        foreach ($command in $group.Commands) {
            Add-CommandPaths $command $group.CommandPaths
        }
        $result += $group
    }
    return @($result | Sort-Object DisplayName, KeyName)
}

function Show-RegistrationMenu {
    param(
        [object[]]$Registrations,
        [int]$Cursor
    )

    if (-not [Console]::IsOutputRedirected) {
        try { [Console]::Clear() } catch {}
    }
    Write-Host "Context menu uninstaller (current user only)" -ForegroundColor Cyan
    Write-Host "Use Up/Down to move, Enter to remove. Esc cancels."
    Write-Host ""

    if ($Registrations.Count -eq 0) {
        Write-Host "No HKCU context-menu registrations were found."
        return
    }

    for ($index = 0; $index -lt $Registrations.Count; $index++) {
        $registration = $Registrations[$index]
        $cursorMark = if ($index -eq $Cursor) { ">" } else { " " }
        $color = if ($index -eq $Cursor) { "Yellow" } else { "Gray" }
        Write-Host (
            "{0} {1}" -f $cursorMark, $registration.DisplayName) `
            -ForegroundColor $color
    }

    $current = $Registrations[$Cursor]
    Write-Host ""
    Write-Host "Current item:" -ForegroundColor Cyan
    Write-Host ("  Key: {0}" -f $current.KeyName)
    Write-Host ("  Contexts: {0}" -f ($current.ContextDetails -join ", "))
    if ($current.CommandPaths.Count -eq 0) {
        Write-Host "  Command paths: (none found)"
    }
    else {
        Write-Host "  Command paths:"
        foreach ($path in $current.CommandPaths) {
            Write-Host ("    {0}" -f $path)
        }
    }
}

function Select-Registrations {
    param([object[]]$Registrations)

    if ($Registrations.Count -eq 0) {
        Write-Host "No HKCU context-menu registrations were found."
        return @()
    }

    $cursor = 0
    while ($true) {
        Show-RegistrationMenu $Registrations $cursor
        $key = [Console]::ReadKey($true)
        if ($key.Key -eq [ConsoleKey]::UpArrow) {
            $cursor = ($cursor + $Registrations.Count - 1) % $Registrations.Count
        }
        elseif ($key.Key -eq [ConsoleKey]::DownArrow) {
            $cursor = ($cursor + 1) % $Registrations.Count
        }
        elseif ($key.Key -eq [ConsoleKey]::Escape) {
            return @()
        }
        elseif ($key.Key -eq [ConsoleKey]::Enter) {
            return @($Registrations[$cursor])
        }
    }
}

function Assert-RegistrationPath {
    param([string]$Path)

    foreach ($context in $script:Contexts) {
        $prefix = $context.RegistryPath + "\"
        if ($Path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $remainder = $Path.Substring($prefix.Length)
            if (-not [string]::IsNullOrWhiteSpace($remainder) -and
                $remainder.IndexOf("\") -lt 0) {
                return
            }
        }
    }
    throw "Refusing registry path outside the three HKCU contexts: $Path"
}

function Get-SafeFileName {
    param([string]$Value)
    $safe = $Value
    foreach ($character in [IO.Path]::GetInvalidFileNameChars()) {
        $safe = $safe.Replace([string]$character, "_")
    }
    return $safe
}

function Backup-Registrations {
    param([object[]]$Registrations)

    $backupRoot = Join-Path $script:FerryRoot "output\context-menu-backups"
    $backupDirectory = Join-Path $backupRoot (Get-Date -Format "yyyyMMdd-HHmmssfff")
    [void](New-Item -ItemType Directory -Path $backupDirectory -Force)
    $manifest = New-Object "System.Collections.Generic.List[string]"
    $manifest.Add("Context menu uninstall backup (current user)")
    $manifest.Add("Created: " + (Get-Date -Format "yyyy-MM-dd HH:mm:ss zzz"))
    $manifest.Add("")

    foreach ($registration in $Registrations) {
        $manifest.Add("Display: " + $registration.DisplayName)
        $manifest.Add("Key: " + $registration.KeyName)
        foreach ($registryPath in $registration.RegistryPaths) {
            Assert-RegistrationPath $registryPath
            $context = $script:Contexts |
                Where-Object {
                    $registryPath.StartsWith(
                        $_.RegistryPath + "\",
                        [StringComparison]::OrdinalIgnoreCase)
                } |
                Select-Object -First 1
            $fileName = "{0}_{1}.reg" -f (
                Get-SafeFileName $registration.KeyName), $context.Id
            $backupPath = Join-Path $backupDirectory $fileName
            $exitCode = Invoke-RegExe -CommandArguments @(
                "export", $registryPath, $backupPath, "/y")
            if ($exitCode -ne 0 -or
                -not (Test-Path -LiteralPath $backupPath -PathType Leaf)) {
                throw "Registry backup failed: $registryPath"
            }
            $manifest.Add("  " + $registryPath)
            $manifest.Add("  Backup: " + $backupPath)
        }
        $manifest.Add("")
    }

    [IO.File]::WriteAllLines(
        (Join-Path $backupDirectory "manifest.txt"),
        $manifest.ToArray(),
        (New-Object Text.UTF8Encoding($true)))
    return $backupDirectory
}

function Remove-Registrations {
    param([object[]]$Registrations)

    $lines = New-Object "System.Collections.Generic.List[string]"
    $lines.Add("Windows Registry Editor Version 5.00")
    $lines.Add("")
    foreach ($registration in $Registrations) {
        foreach ($registryPath in $registration.RegistryPaths) {
            Assert-RegistrationPath $registryPath
            $lines.Add("[-" + $registryPath + "]")
            $lines.Add("")
        }
    }

    $temporaryDirectory = Join-Path $env:TEMP (
        "ferry_uninstall_" + [Guid]::NewGuid().ToString("N"))
    $registryFile = Join-Path $temporaryDirectory "remove.reg"
    try {
        [void](New-Item -ItemType Directory -Path $temporaryDirectory)
        [IO.File]::WriteAllLines(
            $registryFile,
            $lines.ToArray(),
            [Text.Encoding]::Unicode)
        $exitCode = Invoke-RegExe -CommandArguments @("import", $registryFile)
        if ($exitCode -ne 0) {
            throw "Registry removal import failed."
        }
    }
    finally {
        if (Test-Path -LiteralPath $registryFile -PathType Leaf) {
            Remove-Item -LiteralPath $registryFile -Force
        }
        if (Test-Path -LiteralPath $temporaryDirectory -PathType Container) {
            Remove-Item -LiteralPath $temporaryDirectory -Force
        }
    }
}

function Test-RegistrationRemoved {
    param([object[]]$Registrations)

    $currentUser = [Microsoft.Win32.Registry]::CurrentUser
    foreach ($registration in $Registrations) {
        foreach ($registryPath in $registration.RegistryPaths) {
            Assert-RegistrationPath $registryPath
            $relativePath = $registryPath.Substring("HKEY_CURRENT_USER\".Length)
            $key = $currentUser.OpenSubKey($relativePath)
            if ($null -ne $key) {
                $key.Dispose()
                throw "Registry key remained after removal: $registryPath"
            }
        }
    }
}

try {
    $registrations = @(Get-Registrations)
    if ($ListOnly) {
        Show-RegistrationMenu $registrations 0
        exit 0
    }
    $selected = @(Select-Registrations $registrations)
    if ($selected.Count -eq 0) {
        Write-Host ""
        Write-Host "Nothing selected. No registry keys were removed." -ForegroundColor Green
        exit 0
    }

    Write-Host ""
    Write-Host "Backing up every selected key before removal..."
    $backupDirectory = Backup-Registrations $selected
    Remove-Registrations $selected
    Test-RegistrationRemoved $selected

    Write-Host ""
    Write-Host "Removed:" -ForegroundColor Green
    foreach ($registration in $selected) {
        Write-Host ("  {0} ({1})" -f
            $registration.DisplayName, $registration.KeyName)
    }
    Write-Host "Backup directory:"
    Write-Host $backupDirectory
    exit 0
}
catch {
    Write-Host ""
    Write-Host ("ERROR: " + $_.Exception.Message) -ForegroundColor Red
    exit 1
}
