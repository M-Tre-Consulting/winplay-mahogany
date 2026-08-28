<#
.SYNOPSIS
    Builds a self-contained release of AirPlay per Windows and packages it
    into a single Setup.exe with Inno Setup.

.DESCRIPTION
    Two steps, both scripted so this is a one-command rebuild:
      1. `dotnet publish` the WinUI 3 app, self-contained win-x64 (no .NET
         runtime needed on the target machine).
      2. Run that publish output through installer\AirPlayWindows.iss via
         Inno Setup's command-line compiler (ISCC.exe).

    Output: installer\output\AirPlayWindows-Setup-<version>.exe

.PARAMETER InnoCompiler
    Path to ISCC.exe. Defaults to the standard per-user winget install
    location; override if Inno Setup is installed elsewhere.
#>
param(
    [string]$InnoCompiler = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $root "src\AirPlaySender.App\AirPlaySender.App.csproj"

Write-Host "==> dotnet publish (Release, win-x64, self-contained)" -ForegroundColor Cyan
dotnet publish $appProject -c Release -r win-x64 --self-contained true
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

# `dotnet publish` for an unpackaged WinUI 3 app drops the app's own
# compiled-XAML output (AirPlaySender.App.pri + every *.xbf) even though
# `dotnet build` produces it correctly right next door - a known gap, not
# a mistake in this script. Without these the app launches and crashes
# instantly (Microsoft.UI.Xaml.dll, STATUS_STOWED_EXCEPTION) because the
# XAML runtime can't find the compiled window markup. Copy them over from
# the sibling (non-publish) build output, which `dotnet publish` always
# produces as a side effect of building before it publishes.
$buildOutDir = Join-Path $root "src\AirPlaySender.App\bin\Release\net9.0-windows10.0.19041.0\win-x64"
$publishDir = Join-Path $buildOutDir "publish"
Write-Host "==> Copying app .pri/.xbf (dotnet publish drops these for unpackaged WinUI 3 apps)" -ForegroundColor Cyan
$xamlOutputFiles = Get-ChildItem $buildOutDir -File | Where-Object { $_.Name -eq "AirPlaySender.App.pri" -or $_.Extension -eq ".xbf" }
if ($xamlOutputFiles.Count -eq 0) { throw "No .pri/.xbf files found in $buildOutDir - did the build actually run?" }
foreach ($f in $xamlOutputFiles) {
    Copy-Item $f.FullName -Destination $publishDir -Force
    Write-Host "   copied $($f.Name)"
}

if (-not (Test-Path $InnoCompiler)) {
    throw "Inno Setup compiler not found at '$InnoCompiler'. Install it (winget install JRSoftware.InnoSetup) or pass -InnoCompiler <path>."
}

Write-Host "==> ISCC.exe (Inno Setup)" -ForegroundColor Cyan
& $InnoCompiler (Join-Path $PSScriptRoot "AirPlayWindows.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

Write-Host "==> Done. Installer in installer\output\" -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot "output") -Filter "*.exe" | Select-Object Name, Length, LastWriteTime
