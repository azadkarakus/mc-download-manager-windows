; Inno Setup script. Build: iscc /DAppVersion=1.0.0 /DSourceDir=..\publish installer\setup.iss
; Per-user install (no admin / UAC prompt), like the app's own data and autostart (HKCU).

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

[Setup]
AppId={{6F0B8C3E-2A4D-4E7B-9C61-3D5A8F2E1B47}
AppName=MC Download Manager
AppVersion={#AppVersion}
AppVerName=MC Download Manager {#AppVersion}
AppPublisher=azadkarakus
AppPublisherURL=https://github.com/azadkarakus/mc-download-manager-windows
AppSupportURL=https://github.com/azadkarakus/mc-download-manager-windows/issues
AppUpdatesURL=https://github.com/azadkarakus/mc-download-manager-windows/releases
DefaultDirName={autopf}\MC Download Manager
DefaultGroupName=MC Download Manager
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=MCDownloadManager-{#AppVersion}-setup
SetupIconFile=..\MCDownloadManager\app.ico
UninstallDisplayIcon={app}\MCDownloadManager.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Close the running tray app before replacing its files.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\MC Download Manager"; Filename: "{app}\MCDownloadManager.exe"
Name: "{autodesktop}\MC Download Manager"; Filename: "{app}\MCDownloadManager.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\MCDownloadManager.exe"; Description: "{cm:LaunchProgram,MC Download Manager}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Stop the tray app so its files can be removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM MCDownloadManager.exe /F"; Flags: runhidden; RunOnceId: "StopApp"

[Registry]
; Remove "start with Windows" on uninstall (the app writes this value itself when the user opts in).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "MC Download Manager"; Flags: uninsdeletevalue dontcreatekey

; User data (%LOCALAPPDATA%\MC Download Manager: download list, settings, video tools) and downloaded files are kept on uninstall.
