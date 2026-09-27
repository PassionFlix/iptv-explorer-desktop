#define MyAppName "IPTV Explorer"
#define MyAppPublisher "PassionFlix"
#define MyAppExeName "IPTVExplorer.Desktop.exe"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\dist\IPTV-Explorer-1.0.0-win-x64"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

[Setup]
AppId={{187D94BB-57C0-4F81-A0BE-769A539D3BC7}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL=https://github.com/PassionFlix/iptv-explorer-desktop
AppSupportURL=https://github.com/PassionFlix/iptv-explorer-desktop/issues
AppUpdatesURL=https://github.com/PassionFlix/iptv-explorer-desktop/releases/latest
DefaultDirName={localappdata}\Programs\IPTV Explorer
DefaultGroupName=IPTV Explorer
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=IPTV-Explorer-Setup-{#AppVersion}-x64
SetupIconFile={#OutputDir}\IPTVExplorer.Setup.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=IPTV Explorer Desktop Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#AppVersion}
LicenseFile=..\LICENSE

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Créer un raccourci sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\IPTV Explorer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\IPTV Explorer"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Lancer IPTV Explorer"; Flags: nowait postinstall skipifsilent
