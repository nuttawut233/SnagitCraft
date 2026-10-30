; Inno Setup script for SnagitCraft (formerly "Snagit Anything").
; Build with installer\build-installer.ps1 (publishes the app first, then compiles this script).

#define AppName "SnagitCraft"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#define AppExe "SnagitCraft.exe"

[Setup]
AppId={{6C1D5E7A-3F2B-4E8C-9A41-2B7D0F5C8E13}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#AppVersion}
; Per-user install by default (no admin prompt); the user can choose "all users" in the wizard.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=SnagitCraft-Setup-{#AppVersion}
SetupIconFile=..\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close a running copy before files are replaced (same mutex name as App.xaml.cs).
AppMutex=Local\SnagitAnything.SingleInstance
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"
Name: "startup"; Description: "Start {#AppName} when Windows starts (in the tray, ready for Print Screen)"; GroupDescription: "Startup:"

[Files]
; Self-contained publish output: the target PC does not need .NET installed.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueName: "Snagit Anything"; Flags: deletevalue

; Upgrading from "Snagit Anything" 1.0 (same AppId): remove the old exe and its old-name shortcuts / startup entry.
[InstallDelete]
Type: files; Name: "{app}\SnagitAnything.exe"
Type: files; Name: "{app}\SnagitAnything.dll"
Type: files; Name: "{app}\SnagitAnything.deps.json"
Type: files; Name: "{app}\SnagitAnything.runtimeconfig.json"
Type: files; Name: "{autoprograms}\Snagit Anything.lnk"
Type: files; Name: "{autodesktop}\Snagit Anything.lnk"

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

; Settings (%APPDATA%\SnagitAnything) and saved captures are left in place on uninstall.
