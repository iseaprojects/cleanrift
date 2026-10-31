; CleanRift 1.0 Beta 3.1 installer
; Build with Inno Setup 6: ISCC.exe installer\CleanRift.iss

#define MyAppName "CleanRift"
#define MyAppVersion "1.0.0-beta.3.1"
#define MyAppDisplayVersion "1.0 Beta 3.1"
#define MyAppPublisher "CleanRift Project"
#define MyAppExeName "CleanRift.exe"
#define MyAppId "{{5D6ED849-41B8-4CAC-8716-F8B294CB29A2}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppDisplayVersion}
AppPublisher={#MyAppPublisher}
AppCopyright=Copyright (c) 2026 CleanRift contributors
AppComments=Beta 3.1. Removed files and registry entries are quarantined by default and can be restored from Tools.
DefaultDirName={autopf}\CleanRift
DefaultGroupName=CleanRift
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\artifacts\release
OutputBaseFilename=CleanRift-1.0-beta3.1-setup
SetupIconFile=..\src\CleanRift\Assets\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
LicenseFile=..\src\CleanRift\LICENSE.txt
InfoBeforeFile=..\src\CleanRift\README.txt
AppMutex=Local\CleanRift
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
VersionInfoVersion=1.0.0.0
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion=1.0.0.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription={#MyAppName} {#MyAppDisplayVersion} Setup

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "french"; MessagesFile: "compiler:Languages\French.isl"
Name: "german"; MessagesFile: "compiler:Languages\German.isl"
Name: "italian"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "brazilianportuguese"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\artifacts\release\CleanRift-1.0-beta3.1-portable\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Quick start"; Filename: "{app}\README.txt"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Best-effort removal of the optional SkipUAC task. If the uninstaller runs
; without administrator rights, delete it from Preferences before uninstalling.
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN ""CleanRift_SkipUAC_{%COMPUTERNAME}"" /F"; Flags: runhidden; RunOnceId: "DeleteSkipUacTask"

[UninstallDelete]
; CleanRift's local data is intentionally preserved. Delete it manually from
; %LOCALAPPDATA%\CleanRift if you also want to remove traces and settings.
Type: filesandordirs; Name: "{app}\*.log"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { Remove the optional Explorer command if the user enabled it. }
    RegDeleteKeyIncludingSubkeys(HKCU,
      'Software\Classes\exefile\shell\CleanRift.InstallTrace');
    RegDeleteKeyIncludingSubkeys(HKCU,
      'Software\Classes\Msi.Package\shell\CleanRift.InstallTrace');
  end;
end;
