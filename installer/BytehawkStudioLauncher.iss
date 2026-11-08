; Inno Setup script for Bytehawk Studio Launcher 1.0.0
; Build: run ..\build.ps1, or compile this file with Inno Setup 6 (ISCC.exe) after publishing.

#define MyAppName "Bytehawk Studio Launcher"
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#define MyAppPublisher "Bytehawk Studio"
#define MyAppExeName "BytehawkStudioLauncher.exe"
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
AppId={{5E7B3C1D-9A42-4F6B-8D2E-B1C7A9F04E35}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=© Bytehawk Studio
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoCopyright=© Bytehawk Studio
VersionInfoDescription={#MyAppName} Setup
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
DefaultDirName={autopf}\Bytehawk Studio\Launcher
DefaultGroupName=Bytehawk Studio
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=BytehawkStudioLauncher-{#MyAppVersion}-Setup
SetupIconFile=..\assets\bytehawk.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
WizardStyle=modern
WizardImageFile=wizard-large-1.bmp,wizard-large-2.bmp
WizardSmallImageFile=wizard-small-55.bmp,wizard-small-110.bmp
Compression=lzma2/max
SolidCompression=yes
; Ask the user to close the launcher if it is running
AppMutex=BytehawkStudioLauncher.SingleInstance
CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
; Self-update: the launcher runs Setup with /SILENT /RELAUNCH=1, so reopen it when done
Filename: "{app}\{#MyAppExeName}"; Flags: nowait runasoriginaluser; Check: IsRelaunch

[Code]
function IsRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

; NOTE: There is deliberately no [UninstallDelete] section.
; Uninstalling removes the program and its shortcuts only. The user's data in
; %APPDATA%\BytehawkStudio\Launcher\ (settings, catalog, library, web apps, downloads) is left alone.
