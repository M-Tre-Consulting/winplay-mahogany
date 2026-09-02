<#
.SYNOPSIS
    Builds a self-contained release of WinPlay Mahogany and packages it
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

# Self-contained publish for this TFM (net9.0-windows + UseWinUI) drags in
# the ENTIRE Windows Desktop shared runtime - WPF and WinForms included -
# even though the app is WinUI 3 only and never references either one.
# That's ~64 MB of dead weight (measured), dwarfing the actual per-language
# resource files (~3.6 MB total for every culture combined - languages were
# never the problem). Safe to delete post-publish: nothing in this app's
# call graph touches WPF/WinForms types, so the CLR never tries to load
# these DLLs. Left alone on purpose: UIAutomationClient/Provider/Types
# (WinUI's own accessibility/Narrator support genuinely uses these) and
# DirectWriteForwarder.dll (text rendering, shared with WinUI's own path).
$deadWeightDlls = @(
    "PresentationCore.dll", "PresentationFramework.dll", "PresentationFramework.Aero.dll",
    "PresentationFramework.Aero2.dll", "PresentationFramework.AeroLite.dll", "PresentationFramework.Classic.dll",
    "PresentationFramework.Fluent.dll", "PresentationFramework.Luna.dll", "PresentationFramework.Royale.dll",
    "PresentationFramework-SystemCore.dll", "PresentationFramework-SystemData.dll",
    "PresentationFramework-SystemDrawing.dll", "PresentationFramework-SystemXml.dll",
    "PresentationFramework-SystemXmlLinq.dll", "PresentationNative_cor3.dll", "PresentationUI.dll",
    "ReachFramework.dll", "System.Printing.dll", "System.Windows.Controls.Ribbon.dll",
    "System.Windows.Extensions.dll", "System.Windows.Forms.dll", "System.Windows.Forms.Design.dll",
    "System.Windows.Forms.Design.Editors.dll", "System.Windows.Forms.Primitives.dll",
    "System.Windows.Input.Manipulations.dll", "System.Windows.Presentation.dll", "System.Windows.dll",
    "System.Xaml.dll", "WindowsFormsIntegration.dll", "Microsoft.VisualBasic.dll",
    "Microsoft.VisualBasic.Core.dll", "Microsoft.VisualBasic.Forms.dll", "PenImc_cor3.dll",
    "wpfgfx_cor3.dll", "System.Design.dll", "System.Drawing.Design.dll"
)
Write-Host "==> Stripping unused WPF/WinForms assemblies from the self-contained publish" -ForegroundColor Cyan
$removedBytes = 0
foreach ($name in $deadWeightDlls) {
    $path = Join-Path $publishDir $name
    if (Test-Path $path) {
        $removedBytes += (Get-Item $path).Length
        Remove-Item $path -Force
    }
}
Write-Host ("   removed {0:N1} MB" -f ($removedBytes / 1MB))

# The fdk-aac NuGet ships both the decoder (libAACdec.dll, which AacEldDecoder.cs
# P/Invokes) and the encoder (libAACenc.dll, which nothing here touches). Drop the
# encoder. Belt-and-braces: also drop any ICU payload that slipped through despite
# InvariantGlobalization (there normally is none).
Write-Host "==> Stripping the unused fdk-aac encoder + stray ICU" -ForegroundColor Cyan
$strippedBytes = 0
$strayFiles = @(Join-Path $publishDir "libAACenc.dll")
$strayFiles += Get-ChildItem $publishDir -Filter "icu*.dll" -ErrorAction SilentlyContinue | ForEach-Object { $_.FullName }
foreach ($path in $strayFiles) {
    if (Test-Path $path) {
        $strippedBytes += (Get-Item $path).Length
        Remove-Item $path -Force
        Write-Host "   removed $(Split-Path $path -Leaf)"
    }
}
Write-Host ("   removed {0:N1} MB" -f ($strippedBytes / 1MB))

if (-not (Test-Path $InnoCompiler)) {
    throw "Inno Setup compiler not found at '$InnoCompiler'. Install it (winget install JRSoftware.InnoSetup) or pass -InnoCompiler <path>."
}

Write-Host "==> ISCC.exe (Inno Setup)" -ForegroundColor Cyan
& $InnoCompiler (Join-Path $PSScriptRoot "AirPlayWindows.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

Write-Host "==> Done. Installer in installer\output\" -ForegroundColor Green
Get-ChildItem (Join-Path $PSScriptRoot "output") -Filter "*.exe" | Select-Object Name, Length, LastWriteTime
