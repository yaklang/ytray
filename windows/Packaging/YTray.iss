#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif
#ifndef Architecture
  #define Architecture "amd64"
#endif
#ifndef SourceExe
  #error SourceExe is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{A5E27D87-89F3-4B49-9B11-EE34C9DB4DB7}
AppName=YTray
AppVersion={#AppVersion}
AppPublisher=Yaklang
AppPublisherURL=https://yaklang.io/ytray/
AppSupportURL=https://github.com/yaklang/ytray/issues
DefaultDirName={autopf}\YTray
DefaultGroupName=YTray
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=YTray-{#AppVersion}-windows-{#Architecture}-setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
SetupIconFile=..\src\Assets\Icons\ytray-app.ico
UninstallDisplayIcon={app}\YTray.exe
CloseApplications=yes
CloseApplicationsFilter=YTray.exe
AlwaysRestart=no
RestartApplications=no
#if Architecture == "amd64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=x86compatible
#endif

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "..\.dependencies\WinSparkle-0.9.4\COPYING.expat"; DestDir: "{app}\Licenses"; DestName: "Expat.txt"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}\Licenses"; Flags: ignoreversion
Source: "..\.dependencies\WinSparkle-0.9.4\COPYING"; DestDir: "{app}\Licenses"; DestName: "WinSparkle.txt"; Flags: ignoreversion
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "YTray.exe"; Flags: ignoreversion

[Icons]
Name: "{group}\YTray"; Filename: "{app}\YTray.exe"
Name: "{autodesktop}\YTray"; Filename: "{app}\YTray.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Run]
Filename: "{app}\YTray.exe"; Description: "Launch YTray"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\YTray.exe"; Flags: nowait skipifdoesntexist runasoriginaluser; Check: IsAutoUpdate

[Code]
function IsAutoUpdate(): Boolean;
begin
  Result := CompareText(ExpandConstant('{param:YTRAYAUTOUPDATE|0}'), '1') = 0;
end;
