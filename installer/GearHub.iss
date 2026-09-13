; GearHub installer script for Inno Setup 6.
; Build (from the repo root):
;   1) dotnet publish src\GearHub.App\GearHub.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish
;   2) iscc /DAppVersion=0.1.0 installer\GearHub.iss
; The release workflow (.github/workflows/release.yml) does both automatically.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

#define AppName "GearHub"
#define AppPublisher "jenesei software"
#define AppExeName "GearHub.exe"

[Setup]
AppId={{E5C3F6B2-5B4A-4F7E-9C21-8A3D6E4B7F10}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={autopf}\GearHub
DefaultGroupName=GearHub
DisableProgramGroupPage=yes
; Per-user installation: no administrator rights required (Add/Remove Programs still works).
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir=..\Output
OutputBaseFilename=GearHub-Setup-{#AppVersion}
SetupIconFile=..\src\GearHub.App\Assets\GearHub.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName} {#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Make sure the widget (tray process) is not running while uninstalling.
Filename: "{cmd}"; Parameters: "/C taskkill /IM {#AppExeName} /F >nul 2>&1"; Flags: runhidden; RunOnceId: "StopGearHub"
