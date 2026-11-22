; Inno Setup script for the Windows installer. Built by
; scripts/package-installer.ps1, which passes /DAppVersion and /DPublishDir.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
; Never change AppId: Windows uses it to find the existing install on upgrade/uninstall.
AppId={{861CB116-EDB2-45F0-9A06-6C00DAB0A1E0}
AppName=freedisk.space
AppVersion={#AppVersion}
AppPublisher=freedisk.space
AppPublisherURL=https://freedisk.space
DefaultDirName={autopf}\freedisk.space
DefaultGroupName=freedisk.space
DisableProgramGroupPage=yes
; The app itself is requireAdministrator (WIN-072), so a machine-wide install costs nothing extra.
PrivilegesRequired=admin
; [UninstallDelete] names {localappdata} on purpose: the elevated app keeps its scan cache under
; the elevated account's profile, which is the one this elevated uninstaller sees.
UsedUserAreasWarning=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#PublishDir}\..
OutputBaseFilename=freedisk.space-setup-{#AppVersion}-x64
SetupIconFile=..\app\DiskMap.App\app.ico
UninstallDisplayIcon={app}\Freedisk.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\freedisk.space"; Filename: "{app}\Freedisk.exe"
Name: "{autodesktop}\freedisk.space"; Filename: "{app}\Freedisk.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Freedisk.exe"; Description: "{cm:LaunchProgram,freedisk.space}"; Flags: nowait postinstall skipifsilent shellexec

[UninstallDelete]
; The scan cache indexes every file on each scanned drive (ScanCache.DirectoryPath) — don't
; leave it behind. Settings, history and snapshots beside it in DiskMap\ are the user's and stay.
Type: filesandordirs; Name: "{localappdata}\DiskMap\ScanCache"
