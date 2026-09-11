Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
function Assert-True([bool]$Value, [string]$Message) { if (-not $Value) { throw $Message } }
. (Join-Path $root 'common\install.ps1')
$lines = @(Build-RegistryLines $true)
$text = $lines -join "`n"
Assert-True ($script:Contexts.Count -eq 4) 'Ferry must register four contexts.'
foreach ($context in @('HKEY_CURRENT_USER\Software\Classes\*\shell', 'HKEY_CURRENT_USER\Software\Classes\Directory\shell', 'HKEY_CURRENT_USER\Software\Classes\Directory\Background\shell', 'HKEY_CURRENT_USER\Software\Classes\DesktopBackground\shell')) {
    $key = $context + '\Fin-Ferry'
    Assert-True ($lines -contains ('[' + $key + ']')) ('Missing Ferry context: ' + $context)
    if ($context -match 'Background\\shell$') {
        Assert-True ($lines -contains ('[' + $key + '\command]')) 'A background must launch the app directly.'
        Assert-True (-not $text.Contains('[' + $key + '\shell\')) 'A background unexpectedly has selected-item actions.'
    }
    else {
        foreach ($id in @('01-optical', '02-markdown', '03-vba')) {
            Assert-True ($lines -contains ('[' + $key + '\shell\' + $id + '\command]')) 'A selected-item action is missing.'
        }
    }
}
foreach ($mode in @('optical', 'markdown', 'vba')) {
    $command = Get-MenuCommand $mode
    Assert-True ($command.Contains('"%1"')) 'Selected items must use %1.'
    Assert-True (-not $command.Contains('%V')) 'A selected-item action incorrectly uses %V.'
}
Assert-True ((Get-AppCommand) -notmatch '%[1Vv]') 'Background launch must not pass a target.'
$removeLines = @(Build-RegistryLines $false)
Assert-True (@($removeLines | Where-Object { $_.StartsWith('[-HKEY_CURRENT_USER\') }).Count -eq 4) 'Removal plan misses a context.'
$uninstall = [IO.File]::ReadAllText((Join-Path $root 'common\uninstall.ps1'))
Assert-True ($uninstall.Contains('RelativePath = "Software\Classes\DesktopBackground\shell"')) 'Interactive uninstaller misses the desktop context.'
foreach ($path in @((Join-Path $root 'common\install.ps1'), (Join-Path $root 'common\uninstall.ps1'))) {
    $tokens = $null; $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-True (@($errors).Count -eq 0) ('PowerShell parse failed: ' + $path)
}
Write-Output 'FERRY_DESKTOP_OK'
