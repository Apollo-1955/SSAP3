#ifndef SourceRoot
  #error SourceRoot must point to a complete SoundStage package directory.
#endif

#ifndef OutputRoot
  #error OutputRoot must point to the release output directory.
#endif

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif

[Setup]
AppId={{FE338C63-78A6-4F57-83B5-B1E5A48A9645}
AppName=SoundStage
AppVerName=SoundStage {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=OpenAI Codex
AppPublisherURL=https://github.com/capriqqw/soundstage-for-airpods
AppSupportURL=https://github.com/capriqqw/soundstage-for-airpods/issues
AppUpdatesURL=https://github.com/capriqqw/soundstage-for-airpods/releases
DefaultDirName={autopf}\SoundStage
DefaultGroupName=SoundStage
DisableProgramGroupPage=yes
DisableWelcomePage=no
LicenseFile=..\LICENSE
SetupIconFile=..\assets\SoundStage.ico
UninstallDisplayIcon={app}\SoundStage.exe
UninstallDisplayName=SoundStage for AirPods
OutputDir={#OutputRoot}
OutputBaseFilename=SoundStage-Setup
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
VersionInfoCompany=OpenAI Codex
VersionInfoDescription=SoundStage for AirPods Setup
VersionInfoProductName=SoundStage
VersionInfoProductVersion={#AppVersion}
VersionInfoCopyright=Copyright (C) 2026 OpenAI Codex

[InstallDelete]
Type: filesandordirs; Name: "{commonappdata}\SoundStage\package"

[Files]
Source: "{#SourceRoot}\*"; DestDir: "{commonappdata}\SoundStage\package"; Flags: ignoreversion recursesubdirs createallsubdirs

[Run]
Filename: "{app}\SoundStage.exe"; Description: "Open SoundStage"; Flags: nowait postinstall skipifsilent; Check: IsSoundStageReady

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{commonappdata}\SoundStage\package\Uninstall-SoundStage.ps1"" -NoRelaunch -KeepInstallDirectory"; RunOnceId: "SoundStageSystemCleanup"; Flags: runhidden waituntilterminated

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\SoundStage\package"
Type: files; Name: "{commonappdata}\SoundStage\restart-required.flag"

[Code]
function IsSoundStageReady(): Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{app}\SoundStage.exe')) and
    not FileExists(
      ExpandConstant('{commonappdata}\SoundStage\restart-required.flag'));
end;

function NeedRestart(): Boolean;
begin
  Result := FileExists(
    ExpandConstant('{commonappdata}\SoundStage\restart-required.flag'));
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
      '{commonappdata}\SoundStage\package\Install-SoundStage.ps1');
  Parameters :=
    '-NoProfile -ExecutionPolicy Bypass -File "' +
    InstallerScript + '" -NoRelaunch';

  WizardForm.StatusLabel.Caption :=
    'Installing the SoundStage app, driver, and spatial audio engine...';
  if not Exec(
      PowerShellPath,
      Parameters,
      '',
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) then
  begin
    RaiseException('SoundStage system installer could not be started.');
  end;
  if ResultCode <> 0 then
  begin
    RaiseException(
      'SoundStage system installation failed with exit code ' +
      IntToStr(ResultCode) + '.' + #13#10 +
      'Review C:\ProgramData\SoundStage\install.log.');
  end;

  if NeedRestart() then
  begin
    MsgBox(
      'Windows Test Mode is now prepared.' + #13#10 + #13#10 +
      'Restart Windows, then run this same SoundStage-Setup.exe again ' +
      'to install the driver and finish setup.',
      mbInformation,
      MB_OK);
  end;
end;
