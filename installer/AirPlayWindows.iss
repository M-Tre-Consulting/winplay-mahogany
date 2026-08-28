; Inno Setup script for "WinPlay Mahogany".
;
; Wraps the already-working *unpackaged* `dotnet publish` output into a
; single Setup.exe with a Start Menu entry and a proper uninstaller — on
; purpose NOT an MSIX package. MSIX would mean flipping
; AirPlaySender.App.csproj to WindowsPackageType=MSIX, which reopens the
; XamlCompiler.exe / AppxMSBuildToolsPath build-tooling problems documented
; in the main README (a real multi-hour debugging session). This script
; changes nothing about how the app is built or debugged day-to-day — it
; only wraps the publish output that already works.
;
; How to (re)build the installer, end to end:
;   powershell -File installer\build-installer.ps1
; That does the `dotnet publish` + runs this script through ISCC.exe and
; leaves AirPlayWindows-Setup-<version>.exe in installer\output\.
;
; To edit: bump MyAppVersion below when you want a new version number on
; the installer/uninstaller entry; everything else rarely needs to change.

#define MyAppName "WinPlay Mahogany"
#define MyAppVersion "0.1.0"
#define MyAppPublisher "M-Tre Consulting"
#define MyAppExeName "AirPlaySender.App.exe"
#define MyPublishDir "..\src\AirPlaySender.App\bin\Release\net9.0-windows10.0.19041.0\win-x64\publish"

[Setup]
AppId={{9D6E9C7B-6E3B-4C2C-8C0B-2E7C6C6E9A11}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
DefaultDirName={localappdata}\Programs\AirPlayWindows
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Per-user install, no admin/UAC prompt — friendlier for an unsigned app
; that a friend is installing on their own PC.
PrivilegesRequired=lowest
OutputDir=output
OutputBaseFilename=AirPlayWindows-Setup-{#MyAppVersion}
SetupIconFile=..\src\AirPlaySender.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible

[Languages]
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
