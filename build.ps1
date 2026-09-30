# ============================================================================
#  PowerFeeTray - build script
#
#  Compiles a single self-contained native .exe using the C# compiler that
#  ships with Windows (.NET Framework 4.x). No SDK, no runtime install,
#  no third-party packages required.
#
#  NOTE: this file is deliberately ASCII-only. Windows PowerShell 5.1
#  decodes BOM-less .ps1 files using the ANSI code page, which corrupts
#  non-ASCII text and breaks parsing.
#
#  Usage:
#     powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1
#     powershell -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Clean -Run
# ============================================================================
[CmdletBinding()]
param(
    [switch]$Clean,
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Src  = Join-Path $Root 'src\PowerFeeTray.cs'
$Ico  = Join-Path $Root 'src\app.ico'
$Dist = Join-Path $Root 'dist'
$Out  = Join-Path $Dist 'PowerFeeTray.exe'

Write-Host "=== PowerFeeTray build ===" -ForegroundColor Cyan

# A running exe holds a lock on itself, so a rebuild would fail with
# "Access to the path is denied". Stop any live instance and bring it back
# after the build so the tray app is never left down.
$wasRunning = $false
$running = Get-Process -Name 'PowerFeeTray' -ErrorAction SilentlyContinue
if ($running) {
    $wasRunning = $true
    Write-Host "stopping running instance (pid $($running.Id -join ', '))..."
    $running | Stop-Process -Force
    Start-Sleep -Milliseconds 800
}

if ($Clean -and (Test-Path $Dist)) {
    try {
        Remove-Item $Dist -Recurse -Force -ErrorAction Stop
        Write-Host "cleaned dist/"
    } catch {
        Write-Host "WARNING: could not clean dist\ - $($_.Exception.Message)" -ForegroundColor Yellow
    }
}
if (-not (Test-Path $Dist)) { New-Item -ItemType Directory -Path $Dist | Out-Null }

if (-not (Test-Path $Src)) { throw "source not found: $Src" }

# --- 1. locate csc.exe ------------------------------------------------------
$csc = @(
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'),
    (Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe')
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $csc) {
    throw "csc.exe not found. .NET Framework 4.x is required (bundled with Windows 10/11)."
}
$fwDir = Split-Path -Parent $csc
Write-Host "compiler: $csc  ($((Get-Item $csc).VersionInfo.FileVersion))"

# --- 2. verify reference assemblies ----------------------------------------
$refs = @('System.Windows.Forms.dll', 'System.Drawing.dll', 'System.Web.Extensions.dll')
foreach ($dll in $refs) {
    if (-not (Test-Path (Join-Path $fwDir $dll))) { throw "missing reference assembly: $dll" }
}

# --- 3. source encoding -----------------------------------------------------
# csc.exe is told the source is UTF-8 via /codepage:65001 further down, so the
# file does not need a BOM. The build never rewrites the source: doing so would
# churn timestamps and surprise editors. We only report what we found.
$bytes = [System.IO.File]::ReadAllBytes($Src)
$hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
if ($hasBom) {
    Write-Host "source encoding: UTF-8 with BOM"
} else {
    Write-Host "source encoding: UTF-8 without BOM (ok, /codepage:65001 set)"
}

# --- 4. generate the exe icon ----------------------------------------------
try {
    $null = Add-Type -AssemblyName System.Drawing -ErrorAction Stop
} catch {
    $null = Add-Type -AssemblyName System.Drawing.Common -ErrorAction Stop
}

$bmp = New-Object System.Drawing.Bitmap -ArgumentList 32, 32
$g   = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAlias
$g.Clear([System.Drawing.Color]::Transparent)

$brushCircle = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::FromArgb(62, 142, 64))
$g.FillEllipse($brushCircle, 1, 1, 30, 30)

$font   = New-Object System.Drawing.Font -ArgumentList 'Microsoft YaHei', 15, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
$brushT = New-Object System.Drawing.SolidBrush -ArgumentList ([System.Drawing.Color]::White)
$glyph  = [char]0x7535          # U+7535 CJK "electricity"
$sz     = $g.MeasureString($glyph, $font)
$g.DrawString($glyph, $font, $brushT, (32 - $sz.Width) / 2, (32 - $sz.Height) / 2)
$g.Dispose()

$hicon = $bmp.GetHicon()
$icon  = [System.Drawing.Icon]::FromHandle($hicon)
$fs    = [System.IO.File]::Create($Ico)
$icon.Save($fs)
$fs.Close()
$icon.Dispose()
$bmp.Dispose()
Write-Host "icon written: src\app.ico ($((Get-Item $Ico).Length) bytes)"

# --- 5. compile -------------------------------------------------------------
$cscArgs = @(
    '/nologo'
    '/target:winexe'
    '/platform:anycpu'
    '/optimize+'
    '/codepage:65001'
    '/warn:4'
    "/out:$Out"
    "/r:$(Join-Path $fwDir 'System.Windows.Forms.dll')"
    "/r:$(Join-Path $fwDir 'System.Drawing.dll')"
    "/r:$(Join-Path $fwDir 'System.Web.Extensions.dll')"
    "/win32icon:$Ico"
    $Src
)

Write-Host ""
Write-Host "compiling..." -ForegroundColor Cyan
$output = & $csc @cscArgs 2>&1
$output | ForEach-Object { Write-Host $_ }

if ($LASTEXITCODE -ne 0 -or -not (Test-Path $Out)) {
    throw "compile failed (exit=$LASTEXITCODE)"
}

$sizeKB = [math]::Round((Get-Item $Out).Length / 1KB, 1)
Write-Host ""
Write-Host "BUILD OK -> dist\PowerFeeTray.exe ($sizeKB KB)" -ForegroundColor Green

# --- 6. optional self-check -------------------------------------------------
if ($Run) {
    Write-Host ""
    Write-Host "running API self-check..." -ForegroundColor Cyan
    & $Out --check
    Start-Sleep -Seconds 1
    $checkFile = Join-Path $env:APPDATA 'PowerFeeTray\check.txt'
    if (Test-Path $checkFile) {
        Write-Host "--- check.txt ---"
        Get-Content $checkFile -Encoding UTF8 | ForEach-Object { Write-Host $_ }
    } else {
        Write-Host "(check.txt not produced)" -ForegroundColor Yellow
    }
}

# --- 7. bring the tray app back up if we took it down -----------------------
if ($wasRunning) {
    Start-Process -FilePath $Out
    Write-Host "restarted tray instance" -ForegroundColor Green
}
