; SSAP3 Setup (Inno Setup 6).
;
; Production policy: no Test Mode, no BCD/Secure Boot/BitLocker changes, no
; certificates. The elevated payload lives in {app} (Program Files, admin-only)
; and never in a user-writable folder (SECURITY_AUDIT.md S-01/S-02/S-03).

#ifndef SourceRoot
  #error SourceRoot must point to a complete SSAP3 package directory.
#endif
#ifndef OutputRoot
  #error OutputRoot must point to the release output directory.
#endif
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

[Setup]
AppId={{6B2E0B8F-4C1D-4E0A-9C3B-5A1F0D2E7C41}
AppName=SSAP3
AppVerName=SSAP3 {#AppVersion}
AppVersion={#AppVersion}
AppPublisher=SSAP3 contributors
AppPublisherURL=https://github.com/Apollo-1955/SSAP3
AppSupportURL=https://github.com/Apollo-1955/SSAP3/issues
DefaultDirName={autopf}\SSAP3
DisableDirPage=yes
DefaultGroupName=SSAP3
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
SetupIconFile=..\assets\SSAP3.ico
UninstallDisplayIcon={app}\SSAP3.exe
UninstallDisplayName=SSAP3 for Windows
OutputDir={#OutputRoot}
OutputBaseFilename=SSAP3-Setup-{#AppVersion}
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

[Tasks]
Name: "autostart"; Description: "Start SSAP3 when I sign in"; Flags: checkedonce
Name: "spatialengine"; Description: "Install the system-wide 3D soundstage engine (advanced; read the note on the next page)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceRoot}\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceRoot}\setup\*"; DestDir: "{app}\setup"; Flags: ignoreversion
Source: "{#SourceRoot}\driver\*"; DestDir: "{app}\driver"; Flags: ignoreversion skipifsourcedoesntexist
Source: "{#SourceRoot}\spatial\*"; DestDir: "{app}\spatial"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist
Source: "{#SourceRoot}\docs\*"; DestDir: "{app}\docs"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\SSAP3"; Filename: "{app}\SSAP3.exe"; Comment: "SSAP3 Control Center"
Name: "{autodesktop}\SSAP3"; Filename: "{app}\SSAP3.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\SSAP3.exe"; Description: "Open SSAP3"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\setup\Uninstall-SSAP3.ps1"" {code:UninstallArguments}"; RunOnceId: "SSAP3SystemCleanup"; Flags: runhidden waituntilterminated

[Code]
var
  SpatialNotePage: TOutputMsgMemoWizardPage;
  RemoveUserData: Boolean;

procedure InitializeWizard();
begin
  SpatialNotePage := CreateOutputMsgMemoPage(wpSelectTasks,
    'System-wide 3D soundstage engine',
    'Only relevant if you selected the advanced engine task.',
    'What this changes on Windows:',
    'The engine runs inside the Windows audio service through the Equalizer APO host.' + #13#10 +
    'Equalizer APO requires the registry value DisableProtectedAudioDG=1, which turns off' + #13#10 +
    'Windows'' signature check for audio processing components for all audio devices.' + #13#10 + #13#10 +
    'SSAP3 records the previous value and every audio setting it changes, and restores them' + #13#10 +
    'exactly when you uninstall. Secure Boot, Memory Integrity, Defender and driver signing are' + #13#10 +
    'never changed. If you do not want this trade-off, go back and leave the task unchecked;' + #13#10 +
    'the rest of SSAP3 still works.');
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SpatialNotePage.ID) and not WizardIsTaskSelected('spatialengine');
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Parameters: String;
  ResultCode: Integer;
begin
  if CurStep <> ssPostInstall then
    exit;

  Parameters := '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\setup\Install-SSAP3.ps1') + '" -Version "{#AppVersion}"';
  if WizardIsTaskSelected('spatialengine') then
    Parameters := Parameters + ' -EnableSpatialEngine';
  if WizardIsTaskSelected('autostart') then
    Parameters := Parameters + ' -Autostart';

  WizardForm.StatusLabel.Caption := 'Configuring SSAP3...';
  if not Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Parameters, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    RaiseException('SSAP3 system configuration could not be started.');
  if ResultCode <> 0 then
    RaiseException('SSAP3 system configuration failed and was rolled back.' + #13#10 +
      'Details: ' + ExpandConstant('{app}\setup\install.log'));
end;

function InitializeUninstall(): Boolean;
begin
  RemoveUserData := MsgBox('Also delete your SSAP3 settings, logs and reports?' + #13#10 + #13#10 +
    'Choose No for a standard uninstall that keeps them.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES;
  Result := True;
end;

function UninstallArguments(Param: String): String;
begin
  if RemoveUserData then
    Result := '-RemoveUserData'
  else
    Result := '';
end;
