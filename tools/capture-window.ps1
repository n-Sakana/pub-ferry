# Moves, resizes and photographs a real window.
#
# Used by the screen tests. A page screenshot taken through the DevTools
# protocol shows the page; this shows the WINDOW — its chrome, its title bar,
# its real size on this display — which is what somebody sitting in front of it
# sees and what the evaluation is about.
#
# The DPI dance is the whole difficulty. Windows PowerShell starts out
# DPI-unaware, so GetWindowRect hands back VIRTUALISED coordinates while
# Graphics.CopyFromScreen reads REAL pixels. On this 150% display that put the
# crop 355 pixels to the left of the window and produced photographs of the
# desktop behind it. SetProcessDPIAware() has to be called before anything
# else touches a window, and -Width/-Height are then given in the same
# device-independent pixels WPF uses, converted here.
#
#   tools\capture-window.ps1 -Title "Pub Transfer" -Out shot.png
#   tools\capture-window.ps1 -Title "Pub Transfer" -Width 1180 -Height 800 -Out shot.png

[CmdletBinding()]
param(
    [string]$Title = 'Pub Transfer',
    [string]$Out = '',
    # Device-independent pixels, the same units the window declares.
    [int]$Width = 0,
    [int]$Height = 0,
    [int]$Left = -1,
    [int]$Top = -1,
    [switch]$Activate,
    [int]$SettleMs = 450
)

$ErrorActionPreference = 'Stop'
try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { }

if (-not ('PubTransfer.Win32' -as [type])) {
    Add-Type -Namespace PubTransfer -Name Win32 -MemberDefinition @'
[StructLayout(LayoutKind.Sequential)]
public struct RECT { public int Left, Top, Right, Bottom; }

[DllImport("user32.dll", SetLastError = true)]
public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

[DllImport("user32.dll", SetLastError = true)]
public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool repaint);

[DllImport("user32.dll")]
public static extern bool SetForegroundWindow(IntPtr hWnd);

[DllImport("user32.dll")]
public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

[DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();

[DllImport("user32.dll")]
public static extern IntPtr GetDC(IntPtr hWnd);

[DllImport("user32.dll")]
public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

[DllImport("gdi32.dll")]
public static extern int GetDeviceCaps(IntPtr hdc, int nIndex);
'@
}

# Before System.Drawing or System.Windows.Forms are loaded, and before any
# window is measured.
[void][PubTransfer.Win32]::SetProcessDPIAware()

Add-Type -AssemblyName System.Drawing

$dc = [PubTransfer.Win32]::GetDC([IntPtr]::Zero)
$dpi = [PubTransfer.Win32]::GetDeviceCaps($dc, 88)  # LOGPIXELSX
[void][PubTransfer.Win32]::ReleaseDC([IntPtr]::Zero, $dc)
if ($dpi -le 0) { $dpi = 96 }
$scale = $dpi / 96.0

function Get-TargetWindow {
    param([string]$WindowTitle)
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        $process = Get-Process |
            Where-Object { $_.MainWindowHandle -ne 0 -and $_.MainWindowTitle -like "*$WindowTitle*" } |
            Select-Object -First 1
        if ($process) { return $process.MainWindowHandle }
        Start-Sleep -Milliseconds 500
    }
    return [IntPtr]::Zero
}

$handle = Get-TargetWindow -WindowTitle $Title
if ($handle -eq [IntPtr]::Zero) {
    Write-Error "ウィンドウが見つかりません: $Title"
    exit 2
}

function Get-Rect {
    $rect = New-Object PubTransfer.Win32+RECT
    if (-not [PubTransfer.Win32]::GetWindowRect($handle, [ref]$rect)) {
        Write-Error 'ウィンドウの位置を取得できませんでした。'
        exit 3
    }
    return $rect
}

if ($Width -gt 0 -and $Height -gt 0) {
    $rect = Get-Rect
    $x = if ($Left -ge 0) { [int]($Left * $scale) } else { $rect.Left }
    $y = if ($Top -ge 0) { [int]($Top * $scale) } else { $rect.Top }
    [void][PubTransfer.Win32]::MoveWindow(
        $handle, $x, $y, [int]($Width * $scale), [int]($Height * $scale), $true)
    Start-Sleep -Milliseconds $SettleMs
}

if ($Activate) {
    [void][PubTransfer.Win32]::ShowWindow($handle, 9)  # SW_RESTORE
    [void][PubTransfer.Win32]::SetForegroundWindow($handle)
    Start-Sleep -Milliseconds 250
}

$rect = Get-Rect
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

if (-not $Out) {
    Write-Output ("{0}x{1} 実ピクセル / {2}x{3} DIP / 表示倍率 {4}% / 位置 {5},{6}" -f
        $w, $h, [int]($w / $scale), [int]($h / $scale), [int]($scale * 100), $rect.Left, $rect.Top)
    exit 0
}

if ($w -le 0 -or $h -le 0) {
    Write-Error 'ウィンドウの大きさが取得できませんでした。'
    exit 3
}

$directory = Split-Path -Parent $Out
if ($directory -and -not (Test-Path -LiteralPath $directory)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

Start-Sleep -Milliseconds $SettleMs
$bitmap = New-Object System.Drawing.Bitmap($w, $h)
try {
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        # From the screen rather than PrintWindow: WebView2 draws through a
        # child surface that PrintWindow returns blank, so a "screenshot" of
        # the window would be an empty frame with a title bar.
        $graphics.CopyFromScreen($rect.Left, $rect.Top, 0, 0, (New-Object System.Drawing.Size($w, $h)))
    }
    finally {
        $graphics.Dispose()
    }
    $bitmap.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $bitmap.Dispose()
}
Write-Output ("{0} ({1}x{2} 実ピクセル / {3}x{4} DIP / 表示倍率 {5}%)" -f
    $Out, $w, $h, [int]($w / $scale), [int]($h / $scale), [int]($scale * 100))
