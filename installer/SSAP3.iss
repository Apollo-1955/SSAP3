#ifndef SourceRoot
  #error SourceRoot must point to a complete SSAP3 package directory.
#endif

#ifndef OutputRoot
  #error OutputRoot must point to the release output directory.
#endif

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif

[Setup]
AppId={{FE338C63-78A6-4F57-83B5-B1E5A48A9645}
AppName=SSAP3
AppVerName=SSAP3 {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=SSAP3 contributors
AppPublisherURL=https://github.com/Apollo-1955/SSAP3
AppSupportURL=https://github.com/Apollo-1955/SSAP3/issues
AppUpdatesURL=https://github.com/Apollo-1955/SSAP3/releases
DefaultDirName={autopf}\SSAP3
DefaultGroupName=SSAP3
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
SetupIconFile=..\assets\SSAP3.ico
UninstallDisplayIcon={app}\SSAP3.exe
UninstallDisplayName=SSAP3 for Windows
OutputDir={#OutputRoot}
OutputBaseFilename=SSAP3-Setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
VersionInfoVersion={#AppVersion}.0
VersionInfoCompany=SSAP3 contributors
VersionInfoDescription=SSAP3 for Windows Setup
VersionInfoProductName=SSAP3
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 SSAP3 contributors

[InstallDelete]
Type: filesandordirs; Name: "{commonappdata}\SSAP3\package"

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{commonappdata}\SSAP3\package"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\SSAP3.exe"; Description: "Open SSAP3"; Flags: nowait postinstall skipifsilent; Check: IsSsap3Ready

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{commonappdata}\SSAP3\package\Uninstall-SSAP3.ps1"" -NoRelaunch -KeepInstallDirectory"; RunOnceId: "Ssap3SystemCleanup"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\SSAP3\package"
Type: files; Name: "{commonappdata}\SSAP3\restart-required.flag"

[Code]
function IsSsap3Ready(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{app}\SSAP3.exe')) and
    not FileExists(
      ExpandConstant('{commonappdata}\SSAP3\restart-required.flag'));
end;

function NeedRestart(): Boolean;
begin
  Result := FileExists(
    ExpandConstant('{commonappdata}\SSAP3\restart-required.flag'));
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  PowerShellPath: String;
  InstallerScript: String;
  Parameters: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
  begin
    exit;
  end;

  PowerShellPath :=
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
  InstallerScript :=
    ExpandConstant(
      '{commonappdata}\SSAP3\package\Install-SSAP3.ps1');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' +
    InstallerScript + '" -NoRelaunch';

  WizardForm.StatusLabel.Caption :=
    'Installing the SSAP3 app, driver, and spatial audio engine...';
  if not Exec(
      PowerShellPath,
      Parameters,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    RaiseException('SSAP3 system installer could not be started.');
  end;
  if ResultCode <> 0 then
  begin
    RaiseException(
      'SSAP3 system installation failed with exit code ' +
      IntToStr(ResultCode) + '.' + #13#10 +
      'Review C:\ProgramData\SSAP3\install.log.');
  end;

  if NeedRestart() then
  begin
    MsgBox(
      'Windows Test Mode is now prepared.' + #13#10 + #13#10 +
      'Restart Windows, then run this same SSAP3-Setup.exe again ' +
      'to install the driver and finish setup.',
      mbInformation,
      MB_OK);
  end;
end;
