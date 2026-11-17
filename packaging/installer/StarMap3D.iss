; Inno Setup script for the Star Map 3D installer (StarMap3D-Setup.exe).
; Built by .github/workflows/release.yml:  iscc /DAppVersion=1.2.3 /DPublishDir=<publish folder> /DOutputDir=<out> StarMap3D.iss
; Installs to C:\Program Files\Qapow\StarMap3D by default (every QAPOW program goes under C:\Program Files\Qapow),
; and the user can pick another folder. The app keeps its settings and data in the user's profile, not here.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\..\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\out"
#endif

#define AppName "Star Map 3D"
#define AppFolder "StarMap3D"
#define AppExe "StarMap3D.exe"

[Setup]
; AppId identifies this program to Windows across versions. Never change it.
AppId={{5C5A595F-092F-5E8C-82DA-5BEC38199432}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=John Apolloni
AppPublisherURL=https://qapow.com/p/star-map-3d/
AppSupportURL=https://github.com/johnovanni/StarMap3D
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\Qapow\{#AppFolder}
DisableDirPage=no
DefaultGroupName=Qapow
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
SetupIconFile=..\..\Assets\StarMap3D.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
CloseApplications=yes
OutputDir={#OutputDir}
OutputBaseFilename=StarMap3D-Setup

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\Qapow\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent
