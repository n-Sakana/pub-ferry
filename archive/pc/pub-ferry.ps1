# Pub Ferry — launcher and orchestration.
#
# This script owns everything between "somebody double-clicked something" and
# "the window is up": checking the prerequisites, fetching the WebView2
# assemblies, building the page if it is missing or stale, compiling the C#
# host, and writing down what happened. The BAT file next to it only opens the
# door; the C# only draws and does.
#
# Every prerequisite failure stops with a sentence and the command that fixes
# it. Nothing here degrades quietly — a missing runtime is not a reason to open
# a window that cannot work.

[CmdletBinding()]
param(
    # Opens a DevTools protocol port so the automated screen tests can drive
    # the REAL window. Absent from an ordinary run.
    [int]$DebugPort = 0,
    # A .y4m or .mjpeg file the runtime should present as a camera. This
    # desktop has no camera; this is how the receiving path is exercised on it.
    [string]$FakeVideo = '',
    # Build the page and compile the host, then stop. Used by the tests.
    [switch]$CheckOnly,
    # Rebuild the page even if it looks current.
    [switch]$Rebuild
)

$ErrorActionPreference = 'Stop'

# The console this writes to is code page 932 on a Japanese Windows, and every
# message in this file is Japanese. Without this the one thing somebody reads
# when startup fails is mojibake.
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
    $OutputEncoding = [Console]::OutputEncoding
}
catch {
}

$baseDir = $PSScriptRoot
$repoDir = Split-Path -Parent $baseDir
$libDir = Join-Path $baseDir 'lib'
$srcDir = Join-Path $baseDir 'src'
$appDist = Join-Path $baseDir 'app\dist'

# launch failures use this so a forced shutdown of a running app never looks
# like a startup error.
$startupFailureExitCode = 3

function Write-Line {
    param([string]$Text)
    Write-Host $Text
}

function Write-Problem {
    param([string]$Text, [string]$Fix)
    Write-Host ''
    Write-Host $Text -ForegroundColor Red
    if ($Fix) {
        Write-Host $Fix -ForegroundColor Yellow
    }
    Write-Host ''
}

function Write-LauncherLog {
    param([string]$Level, [string]$Message)
    try {
        $logDir = Join-Path $env:LOCALAPPDATA 'PubFerry\pc\logs'
        if (-not (Test-Path -LiteralPath $logDir -PathType Container)) {
            New-Item -ItemType Directory -Path $logDir -Force | Out-Null
        }
        $logPath = Join-Path $logDir ('pub-ferry_' + (Get-Date -Format 'yyyyMMdd') + '.log')
        $line = '[' + (Get-Date -Format 'HH:mm:ss') + '] [' + $Level + '] ' + $Message
        # HostServices.WriteLog appends UTF-8 with no byte order mark. Match it
        # so the daily log never grows one in the middle.
        [System.IO.File]::AppendAllText(
            $logPath, $line + [Environment]::NewLine,
            (New-Object System.Text.UTF8Encoding($false)))
    }
    catch {
    }
}

function Test-WebView2Runtime {
    # The Evergreen runtime registers its version here. This is the shipped
    # component, which is a different thing from the SDK assemblies in lib\.
    $keys = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKLM:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}',
        'HKCU:\SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}'
    )
    foreach ($key in $keys) {
        try {
            $value = (Get-ItemProperty -Path $key -ErrorAction Stop).pv
            if ($value -and $value -ne '0.0.0.0') { return $value }
        }
        catch {
        }
    }
    return $null
}

function Ensure-WebView2Assemblies {
    # The .NET wrappers. Not the runtime — that is checked separately, and one
    # being present says nothing about the other.
    $needed = @(
        'Microsoft.Web.WebView2.Core.dll',
        'Microsoft.Web.WebView2.Wpf.dll',
        'WebView2Loader.dll'
    )
    $missing = @()
    foreach ($name in $needed) {
        if (-not (Test-Path -LiteralPath (Join-Path $libDir $name) -PathType Leaf)) {
            $missing += $name
        }
    }
    if ($missing.Count -eq 0) { return }

    Write-Line 'WebView2 のアセンブリを取得します…'
    $fetch = Join-Path $repoDir 'tools\fetch-webview2.ps1'
    if (-not (Test-Path -LiteralPath $fetch -PathType Leaf)) {
        throw "取得スクリプトがありません: $fetch"
    }
    & $fetch -Destination $libDir
    foreach ($name in $needed) {
        if (-not (Test-Path -LiteralPath (Join-Path $libDir $name) -PathType Leaf)) {
            throw "$name を用意できませんでした。"
        }
    }
}

function Ensure-Page {
    $indexPath = Join-Path $appDist 'index.html'
    $needsBuild = $Rebuild -or -not (Test-Path -LiteralPath $indexPath -PathType Leaf)

    if (-not $needsBuild) {
        # Rebuild when a source file is newer than what was built. Without this
        # a change to the page silently does not appear and the next hour goes
        # into wondering why.
        $built = (Get-Item -LiteralPath $indexPath).LastWriteTimeUtc
        $watched = @(
            (Join-Path $baseDir 'app'),
            (Join-Path $repoDir 'shared')
        )
        foreach ($dir in $watched) {
            if (-not (Test-Path -LiteralPath $dir)) { continue }
            $newest = Get-ChildItem -LiteralPath $dir -Recurse -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -notlike '*\dist\*' } |
                Sort-Object LastWriteTimeUtc -Descending |
                Select-Object -First 1
            if ($newest -and $newest.LastWriteTimeUtc -gt $built) {
                $needsBuild = $true
                break
            }
        }
    }
    if (-not $needsBuild) { return }

    $npm = Get-Command npm -ErrorAction SilentlyContinue
    if (-not $npm) {
        throw @"
画面を組み立てるには Node.js が必要です。
  https://nodejs.org/ から入れて、もう一度起動してください。
  すでに組み立て済みの $appDist があれば Node.js は要りません。
"@
    }
    if (-not (Test-Path -LiteralPath (Join-Path $repoDir 'node_modules') -PathType Container)) {
        Write-Line '依存関係を取得します（初回だけ数分かかります）…'
        Push-Location $repoDir
        try { & npm.cmd install | Out-Host } finally { Pop-Location }
        if ($LASTEXITCODE -ne 0) { throw 'npm install に失敗しました。' }
    }
    Write-Line '画面を組み立てます…'
    Push-Location $repoDir
    try { & npm.cmd run build:pc | Out-Host } finally { Pop-Location }
    if ($LASTEXITCODE -ne 0) { throw '画面の組み立てに失敗しました。' }
    if (-not (Test-Path -LiteralPath $indexPath -PathType Leaf)) {
        throw "組み立ては終わりましたが $indexPath がありません。"
    }
}

try {
    $runtime = Test-WebView2Runtime
    if (-not $runtime) {
        Write-Problem `
            'Microsoft Edge WebView2 ランタイムが見つかりません。' `
            @"
このアプリの画面は WebView2 で描かれます。入れてから、もう一度起動してください。
  https://developer.microsoft.com/microsoft-edge/webview2/
"@
        Write-LauncherLog 'ERROR' 'WebView2 runtime missing'
        exit $startupFailureExitCode
    }

    Ensure-WebView2Assemblies
    Ensure-Page

    Add-Type -AssemblyName PresentationFramework
    Add-Type -AssemblyName PresentationCore
    Add-Type -AssemblyName WindowsBase
    Add-Type -AssemblyName System.Xaml
    Add-Type -AssemblyName System.Drawing
    Add-Type -AssemblyName System.Web.Extensions

    $env:Path = $libDir + [System.IO.Path]::PathSeparator + $env:Path
    foreach ($assembly in @('Microsoft.Web.WebView2.Core.dll', 'Microsoft.Web.WebView2.Wpf.dll')) {
        $assemblyPath = Join-Path $libDir $assembly
        # A copy that arrived as a zip download carries a Mark of the Web, and
        # Assembly::LoadFrom refuses such a file with HRESULT 0x80131515.
        # Handing over the bytes loads the same assembly without touching it.
        [System.Reflection.Assembly]::Load([System.IO.File]::ReadAllBytes($assemblyPath)) | Out-Null
    }

    $csFiles = Get-ChildItem -LiteralPath $srcDir -Filter '*.cs' | Sort-Object -Property Name
    if (@($csFiles).Count -eq 0) {
        throw "C# のソースが見つかりません: $srcDir"
    }
    $combined = ($csFiles | ForEach-Object {
        [System.IO.File]::ReadAllText($_.FullName, [System.Text.Encoding]::UTF8)
    }) -join "`n"
    # Add-Type wants one compilation unit, and using-directives have to lead it.
    $usingPattern = '(?m)^\s*using\s+[\w][\w.]*\s*;'
    $usings = [regex]::Matches($combined, $usingPattern) |
        ForEach-Object { $_.Value.Trim() } | Sort-Object -Unique
    $source = ($usings -join "`n") + "`n`n" + ($combined -replace $usingPattern, '')

    $references = @(
        [System.Windows.Window].Assembly.Location
        [System.Windows.UIElement].Assembly.Location
        [System.Windows.DependencyObject].Assembly.Location
        [System.Xaml.XamlReader].Assembly.Location
        'System'
        'System.Core'
        'Microsoft.CSharp'
        'System.Drawing'
        'System.Web.Extensions'
        (Join-Path $libDir 'Microsoft.Web.WebView2.Core.dll')
        (Join-Path $libDir 'Microsoft.Web.WebView2.Wpf.dll')
    )
    Add-Type -TypeDefinition $source -ReferencedAssemblies $references -Language CSharp

    if ($CheckOnly) {
        Write-Line '準備できています（画面の組み立てとホストのコンパイルは成功）。'
        exit 0
    }

    if ($DebugPort -gt 0) {
        $env:PUB_FERRY_DEBUG_PORT = [string]$DebugPort
        Write-Line "試験用のデバッグポートを開きます: $DebugPort"
    }
    if ($FakeVideo) {
        if (-not (Test-Path -LiteralPath $FakeVideo -PathType Leaf)) {
            throw "指定された映像ファイルがありません: $FakeVideo"
        }
        $env:PUB_FERRY_FAKE_VIDEO = (Resolve-Path -LiteralPath $FakeVideo).Path
        Write-Line "カメラの代わりに映像ファイルを使います: $env:PUB_FERRY_FAKE_VIDEO"
    }

    Write-LauncherLog 'INFO' ('start (webview2 ' + $runtime + ')')
    [PubFerry.App]::Run($baseDir)
    exit 0
}
catch {
    $location = ''
    if ($null -ne $_.InvocationInfo) {
        $location = ' (' + $_.InvocationInfo.ScriptName + ':' + $_.InvocationInfo.ScriptLineNumber + ')'
    }
    $detail = 'launcher failed' + $location + ' ' + $_.Exception.ToString()
    Write-LauncherLog 'ERROR' $detail
    Write-Problem '起動できませんでした。' $_.Exception.Message
    exit $startupFailureExitCode
}
