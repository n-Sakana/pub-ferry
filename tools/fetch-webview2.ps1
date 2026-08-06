# Puts the WebView2 .NET assemblies where the desktop app expects them.
#
# They are Microsoft's redistributable wrappers, shipped in the
# Microsoft.Web.WebView2 NuGet package under the terms recorded in
# THIRD-PARTY-NOTICES.md. They are not committed to this repository — a binary
# in a source tree is a binary nobody re-checks — so this fetches them, and
# prefers a copy that is already on the machine before reaching for the network.
#
#   tools\fetch-webview2.ps1                    fetch into pc\lib
#   tools\fetch-webview2.ps1 -From <folder>     copy from a folder you already have

[CmdletBinding()]
param(
    # Resolved in the body, not here: a default that reads $PSScriptRoot is
    # bound in the CALLER's scope, where that variable is empty.
    [string]$Destination = '',
    [string]$From = '',
    [string]$Version = '1.0.2903.40'
)

$ErrorActionPreference = 'Stop'

if (-not $Destination) {
    $Destination = Join-Path (Split-Path -Parent $PSScriptRoot) 'pc\lib'
}

$needed = @(
    @{ Name = 'Microsoft.Web.WebView2.Core.dll'; Package = 'lib\net462\Microsoft.Web.WebView2.Core.dll' },
    @{ Name = 'Microsoft.Web.WebView2.Wpf.dll';  Package = 'lib\net462\Microsoft.Web.WebView2.Wpf.dll' },
    @{ Name = 'Microsoft.Web.WebView2.WinForms.dll'; Package = 'lib\net462\Microsoft.Web.WebView2.WinForms.dll' },
    @{ Name = 'WebView2Loader.dll'; Package = 'runtimes\win-x64\native\WebView2Loader.dll' }
)

if (-not (Test-Path -LiteralPath $Destination -PathType Container)) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
}

function Copy-From {
    param([string]$Folder)
    $copied = 0
    foreach ($item in $needed) {
        $source = Join-Path $Folder $item.Name
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $item.Name) -Force
            $copied++
        }
    }
    return $copied
}

if ($From) {
    $copied = Copy-From -Folder $From
    if ($copied -eq 0) { throw "指定されたフォルダーに WebView2 のアセンブリがありません: $From" }
    Write-Host "$copied 個のファイルを $From からコピーしました。"
    exit 0
}

# Somewhere on this machine already? A developer usually has the package in the
# NuGet cache, and copying beats downloading.
$cacheRoots = @(
    (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.web.webview2'),
    (Join-Path $env:NUGET_PACKAGES 'microsoft.web.webview2')
) | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Container) }

foreach ($root in $cacheRoots) {
    $newest = Get-ChildItem -LiteralPath $root -Directory | Sort-Object Name -Descending | Select-Object -First 1
    if (-not $newest) { continue }
    $found = 0
    foreach ($item in $needed) {
        $source = Join-Path $newest.FullName $item.Package
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $item.Name) -Force
            $found++
        }
    }
    if ($found -ge 3) {
        Write-Host "NuGet キャッシュから取り出しました: $($newest.FullName)"
        exit 0
    }
}

# Otherwise fetch the package. A .nupkg is a zip.
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('webview2-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $url = "https://www.nuget.org/api/v2/package/Microsoft.Web.WebView2/$Version"
    $package = Join-Path $temp 'webview2.zip'
    Write-Host "NuGet から取得します: Microsoft.Web.WebView2 $Version"
    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $url -OutFile $package -UseBasicParsing
    $extracted = Join-Path $temp 'extracted'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [System.IO.Compression.ZipFile]::ExtractToDirectory($package, $extracted)
    $found = 0
    foreach ($item in $needed) {
        $source = Join-Path $extracted $item.Package
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination (Join-Path $Destination $item.Name) -Force
            $found++
        }
    }
    if ($found -lt 3) { throw 'パッケージに必要なファイルが入っていませんでした。' }
    Write-Host "$found 個のファイルを $Destination に置きました。"
}
catch {
    Write-Host ''
    Write-Host 'WebView2 のアセンブリを取得できませんでした。' -ForegroundColor Red
    Write-Host @'
ネットワークが使えない場合は、すでに持っているフォルダーから渡せます:
  tools\fetch-webview2.ps1 -From <Microsoft.Web.WebView2.*.dll のあるフォルダー>
'@ -ForegroundColor Yellow
    throw
}
finally {
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
