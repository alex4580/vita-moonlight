#ifndef PublishDir
  #define PublishDir "..\..\artifacts\host"
#endif
#ifndef DisplayWizardDir
  #define DisplayWizardDir "..\..\artifacts\displaywizard"
#endif
#ifndef OutputDir
  #define OutputDir "..\..\artifacts\installer"
#endif
#ifndef ViGEmBusDir
  #define ViGEmBusDir "..\..\artifacts\vigembus"
#endif
#ifndef SunshineDir
  #define SunshineDir "..\..\artifacts\sunshine"
#endif

[Setup]
AppId={{D88FE6B4-D767-4A27-B192-E1DB4F6E835C}
AppName=Vita Moonlight Host
AppVersion=0.14.8
VersionInfoVersion=0.14.8.0
AppPublisher=Vita Moonlight contributors
AppPublisherURL=https://github.com/alex4580/vita-moonlight
DefaultDirName={autopf}\Vita Moonlight Host
DisableDirPage=yes
UsePreviousAppDir=no
DefaultGroupName=Vita Moonlight Host
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
PrivilegesRequired=admin
SetupMutex=VitaMoonlightHostSetup,Global\VitaMoonlightHostSetup
RedirectionGuard=yes
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=Vita-Moonlight-Host-Setup-win-x64
UninstallDisplayIcon={app}\VitaMoonlight.Host.exe
#ifdef VitaMoonlightSignedBuild
SignTool=VitaMoonlightReleaseAuthenticode
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Tasks]
Name: "gamepaddriver"; Description: "Controller support (recommended for Xbox, DS4, and Steam Input)"; GroupDescription: "Choose what setup should prepare:"
Name: "host"; Description: "Configure this PC for Vita streaming now (recommended)"; GroupDescription: "Choose what setup should prepare:"
Name: "host\sunshine"; Description: "Sunshine and the required Vita-sized virtual display (if one existing MTT display is found, setup adopts only that exact device and restores its original enabled state on uninstall)"

[Dirs]
Name: "{app}\state"
Name: "{app}\state\Diagnostics"

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\VitaMoonlight\Host"; Flags: uninsdeletekey

[Files]
Source: "{#PublishDir}\*"; Excludes: "VitaMoonlight.Host.exe"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Keep the primary host past ordinary payload deletion. The uninstaller
; verifies an exact finalized transaction, deletes this file explicitly after
; the child exits, and only then removes the durable finalized guard.
Source: "{#PublishDir}\VitaMoonlight.Host.exe"; DestDir: "{app}"; Flags: ignoreversion uninsneveruninstall
; The current host is also embedded as an installer-owned maintenance helper.
; It establishes the protected upgrade fence before the installed payload is
; replaced, including when repairing an older host which predates the fence.
Source: "{#PublishDir}\VitaMoonlight.Host.exe"; DestDir: "{tmp}"; DestName: "VitaMoonlight.Host.Maintenance.exe"; Flags: dontcopy
Source: "{#PublishDir}\D3DCompiler_47_cor3.dll"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PublishDir}\PenImc_cor3.dll"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PublishDir}\PresentationNative_cor3.dll"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PublishDir}\vcruntime140_cor3.dll"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#PublishDir}\wpfgfx_cor3.dll"; DestDir: "{tmp}"; Flags: dontcopy
Source: "{#DisplayWizardDir}\*"; DestDir: "{app}\tools\DisplayWizard"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ViGEmBusDir}\*"; DestDir: "{app}\tools\ViGEmBus"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SunshineDir}\*"; DestDir: "{app}\tools\Sunshine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\tools\summarize-vita-log.py"; DestDir: "{app}\tools\SupportLog"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion
Source: "..\..\PRIVACY.md"; DestDir: "{app}"; DestName: "PRIVACY.md"; Flags: ignoreversion
Source: "..\..\THIRD_PARTY_NOTICES.txt"; DestDir: "{app}"; DestName: "THIRD_PARTY_NOTICES.txt"; Flags: ignoreversion
Source: "..\..\licenses\vita\*"; DestDir: "{app}\licenses\vita"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\third_party\h264bitstream\LICENSE"; DestDir: "{app}\licenses\vita"; DestName: "LGPL-2.1-h264bitstream.txt"; Flags: ignoreversion
Source: "..\..\assets\LICENSE-Mononoki.txt"; DestDir: "{app}\licenses\vita"; DestName: "OFL-Mononoki.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\BETA_SMOKE_TEST.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\END_TO_END_TEST.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\FINAL_RELEASE_CHECKLIST.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\COMPATIBILITY.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}\host"; Flags: ignoreversion
Source: "..\..\docs\BUILDING.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\docs\COMMUNITY_TESTING.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\docs\LOGGING_AND_SUPPORT.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\docs\RELEASING.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\docs\CODE_SIGNING_POLICY.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\docs\VITA_SETTINGS_GUIDE.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE-VitaMoonlight.txt"; Flags: ignoreversion

[UninstallDelete]
; The host finalizer performs a no-follow, best-effort cleanup while it is
; still running. Repeat only this exact allowlist after every host child and
; lock handle has exited. Unknown files are deliberately never traversed or
; deleted. Keep the transaction fence as the final exact file deletion.
Type: files; Name: "{app}\state\Diagnostics\stream-rescue-status.json"
Type: files; Name: "{app}\state\Diagnostics\stream-rescue.log"
Type: dirifempty; Name: "{app}\state\Diagnostics"
Type: files; Name: "{app}\state\display-recovery.json"
Type: files; Name: "{app}\state\host-settings.json"
Type: files; Name: "{app}\state\session.lock"
Type: files; Name: "{app}\state\last-command-error.txt"
Type: files; Name: "{app}\state\display-driver-verification.json"
Type: files; Name: "{app}\state\display-driver-directory-identity.json"
; Keep exact VDD authority through the host finalizer's durable commit. This
; post-child pass removes its released tombstone only after rollback is no
; longer possible.
Type: files; Name: "{app}\state\managed-vdd-ownership.json"
Type: files; Name: "{app}\state\stream-boundary-lease.json"
Type: files; Name: "{app}\state\stream-boundary-lease.backup.json"
Type: files; Name: "{app}\state\stream-boundary-lease.lock"
Type: files; Name: "{app}\state\backend-lifecycle.json"
Type: files; Name: "{app}\state\backend-lifecycle.backup.json"
Type: files; Name: "{app}\state\backend-lifecycle.lock"
Type: files; Name: "{app}\state\backend-disabled.intent"
Type: files; Name: "{app}\state\deferred-host-setup.json"
Type: files; Name: "{app}\state\display-suspend.intent"
Type: files; Name: "{app}\state\display-suspend.lock"
Type: files; Name: "{app}\state\installer-maintenance.json"
Type: files; Name: "{app}\state\installer-maintenance.backup.json"
Type: files; Name: "{app}\state\installer-maintenance.lock"
; Exact documentation names shipped at the root by releases through 0.14.6.
; The host removes these under a pinned directory identity; this final Inno
; pass handles an ordinary transient file lock after every child has exited.
Type: files; Name: "{app}\COMPATIBILITY.md"
Type: files; Name: "{app}\END_TO_END_TEST.md"
Type: files; Name: "{app}\FINAL_RELEASE_CHECKLIST.md"
Type: files; Name: "{app}\THIRD_PARTY_NOTICES.md"
Type: files; Name: "{app}\VITA_SETTINGS_GUIDE.md"
Type: files; Name: "{app}\VitaMoonlight.Host.exe"
Type: files; Name: "{app}\state\uninstall-in-progress.intent"
Type: dirifempty; Name: "{app}\state"

[Icons]
Name: "{group}\Vita Moonlight Host Control Panel"; Filename: "{app}\VitaMoonlight.Host.exe"; WorkingDir: "{app}"
Name: "{group}\Quick start and help"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\README.md"""
Name: "{group}\Uninstall Vita Moonlight Host"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VitaMoonlight.Host.exe"; Description: "Open the Vita Moonlight Host Control Panel"; Check: CanLaunchControlPanel; Flags: postinstall skipifsilent nowait

[Code]
const
  { Inno reserves 0-8 for its own documented setup results. A post-copy host
    configuration failure is an intentional, incomplete setup result rather
    than a Pascal-script exception, so use a stable product-specific code. }
  SetupHostConfigurationFailedExitCode = 10;

var
  DriverReadinessChecked: Boolean;
  DriverReady: Boolean;
  DriverNeedsAttention: Boolean;
  RestartRequiredByPrerequisite: Boolean;
  ConfigurationDeferredForRestart: Boolean;
  RemoveVirtualDisplayOnUninstall: Boolean;
  RemoveSunshineOnUninstall: Boolean;
  RemoveViGEmBusOnUninstall: Boolean;
  PreserveDiagnosticsOnUninstall: Boolean;
  RestartRequiredByUninstall: Boolean;
  PreservedRescueLogPath: String;
  CompletedExplicitRemovalActions: String;
  ExistingInstallDetected: Boolean;
  PreviousInstalledVersion: String;
  BackendRemainsPausedAfterSetup: Boolean;
  PreflightHostPath: String;
  UpgradeBackendWasEnabled: Boolean;
  UpgradeBackendWasDisabled: Boolean;
  UpgradeAgentWasStopped: Boolean;
  UpgradeRecoveryTaskWasRemoved: Boolean;
  UpgradeSafeguardsRestored: Boolean;
  MaintenanceFenceActive: Boolean;
  MaintenanceHelperExtracted: Boolean;
  MaintenanceOwnerPid: Integer;
  SetupFailureRecorded: Boolean;
  SetupFailureText: String;
  SetupFailurePage: TOutputMsgMemoWizardPage;

function GetCurrentProcessId: Integer;
  external 'GetCurrentProcessId@kernel32.dll stdcall';

function WithMaintenanceBypass(const Parameters: String): String;
begin
  Result := Parameters;
  if MaintenanceFenceActive then
  begin
    Result := Result + ' --maintenance-owner-pid ' +
      IntToStr(MaintenanceOwnerPid);
  end;
end;

procedure RecordSetupFailure(const ErrorText: String); forward;

procedure InitializeWizard;
begin
  { Host configuration runs after Inno has copied and finalized the new
    payload. A conditional page is therefore the supported way to explain an
    incomplete post-copy setup; GetCustomSetupExitCode supplies the nonzero
    process result without exposing an internal "Runtime error" dialog. }
  SetupFailurePage := CreateOutputMsgMemoPage(
    wpInstalling,
    'Setup could not complete',
    'Vita Moonlight Host stopped safely.',
    'Review the details below. Your physical display remains the priority.',
    '');
end;

function InitializeSetup: Boolean;
var
  InstallationType: String;
begin
  Result :=
    RegQueryStringValue(
      HKLM64,
      'SOFTWARE\Microsoft\Windows NT\CurrentVersion',
      'InstallationType',
      InstallationType) and
    (CompareText(Trim(InstallationType), 'Client') = 0);
  if not Result then
  begin
    SuppressibleMsgBox(
      'Vita Moonlight Host requires client Windows 10 version 2004 or newer, ' +
      'or Windows 11, on an x64 Intel or AMD PC. Windows Server, ARM64, and x86 ' +
      'are not supported by this package.',
      mbError,
      MB_OK,
      IDOK);
  end;
  if Result then
  begin
    PreviousInstalledVersion := '';
    ExistingInstallDetected :=
      RegQueryStringValue(
        HKLM64,
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' +
        '{D88FE6B4-D767-4A27-B192-E1DB4F6E835C}_is1',
        'DisplayVersion',
        PreviousInstalledVersion);
  end;
end;

function RunHostCommand(
  const Description: String;
  const Parameters: String;
  var ResultCode: Integer): Boolean;
var
  ErrorDetails: AnsiString;
  ErrorPath: String;
  ErrorText: String;
begin
  WizardForm.StatusLabel.Caption := Description;
  WizardForm.StatusLabel.Update;
  if not Exec(
    ExpandConstant('{app}\VitaMoonlight.Host.exe'),
    WithMaintenanceBypass(Parameters),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    RecordSetupFailure(Description + ' could not be started.');
    Result := False;
    exit;
  end;

  if ResultCode = 4 then
  begin
    RestartRequiredByPrerequisite := True;
    ConfigurationDeferredForRestart := True;
    SuppressibleMsgBox(
      Description + ' requires a Windows restart.' + #13#10 + #13#10 +
      'Setup has stopped before applying Sunshine display configuration or ' +
      'changing the active display. ' +
      'Restart Windows, then open Vita Moonlight Host as Administrator and ' +
      'click "Set up or repair this PC" to finish.',
      mbInformation,
      MB_OK,
      IDOK);
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorPath := ExpandConstant(
      '{app}\state\last-command-error.txt');
    ErrorText := '';
    if LoadStringFromFile(ErrorPath, ErrorDetails) then
      ErrorText := Trim(ErrorDetails);
    if ErrorText <> '' then
    begin
      RecordSetupFailure(
        Description + ' failed with exit code ' + IntToStr(ResultCode) + '.' + #13#10 + #13#10 +
        ErrorText);
    end
    else
    begin
      RecordSetupFailure(
        Description + ' failed with exit code ' + IntToStr(ResultCode) + '.');
    end;
    Result := False;
    exit;
  end;
  Result := True;
end;

function RunRequiredHostCommand(
  const Description: String;
  const Parameters: String): Boolean;
var
  ResultCode: Integer;
begin
  Result := RunHostCommand(Description, Parameters, ResultCode);
end;

function ReadSetupHostCommandError: String;
var
  ErrorDetails: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(
    ExpandConstant('{app}\state\last-command-error.txt'),
    ErrorDetails) then
  begin
    Result := Trim(ErrorDetails);
  end;
end;

function ReadMaintenanceHelperError: String;
var
  ErrorDetails: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(
    ExpandConstant('{tmp}\VitaMoonlight.Host.Maintenance.error.txt'),
    ErrorDetails) then
  begin
    Result := Trim(ErrorDetails);
  end;
end;

function ExtractMaintenanceHelper(var ErrorText: String): Boolean;
begin
  Result := False;
  ErrorText := '';
  if MaintenanceHelperExtracted then
  begin
    Result := True;
    exit;
  end;
  try
    ExtractTemporaryFile('VitaMoonlight.Host.Maintenance.exe');
    ExtractTemporaryFile('D3DCompiler_47_cor3.dll');
    ExtractTemporaryFile('PenImc_cor3.dll');
    ExtractTemporaryFile('PresentationNative_cor3.dll');
    ExtractTemporaryFile('vcruntime140_cor3.dll');
    ExtractTemporaryFile('wpfgfx_cor3.dll');
    MaintenanceHelperExtracted := True;
    Result := True;
  except
    ErrorText :=
      'Setup could not extract its protected maintenance helper. No installed ' +
      'host process or recovery safeguard was changed.';
  end;
end;

function BeginUpgradeMaintenance(var ErrorText: String): Boolean;
var
  HostError: String;
  Parameters: String;
  ResultCode: Integer;
begin
  Result := False;
  ErrorText := '';
  if MaintenanceFenceActive then
  begin
    Result := True;
    exit;
  end;
  if not ExtractMaintenanceHelper(ErrorText) then
    exit;

  WizardForm.StatusLabel.Caption :=
    'Restoring and verifying the physical display before installation';
  WizardForm.StatusLabel.Update;
  MaintenanceOwnerPid := GetCurrentProcessId;
  ResultCode := -1;
  DeleteFile(ExpandConstant(
    '{tmp}\VitaMoonlight.Host.Maintenance.error.txt'));
  Parameters :=
    'maintenance begin --owner-pid ' + IntToStr(MaintenanceOwnerPid);
  if not Exec(
    ExpandConstant('{tmp}\VitaMoonlight.Host.Maintenance.exe'),
    Parameters,
    ExpandConstant('{tmp}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    ErrorText :=
      'Setup could not start its protected maintenance helper. No installed ' +
      'host process or recovery safeguard was changed.';
    exit;
  end;
  if ResultCode = 4 then
  begin
    RestartRequiredByPrerequisite := True;
    HostError := ReadMaintenanceHelperError;
    if HostError <> '' then
      HostError := #13#10 + #13#10 + HostError;
    ErrorText :=
      'Windows requires a restart before Vita Moonlight can safely recover ' +
      'the physical display for setup or repair.' + HostError + #13#10 + #13#10 +
      'No application files or recovery safeguards were replaced. Restart ' +
      'Windows, confirm the physical monitor is visible, then run this installer again.';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    HostError := ReadMaintenanceHelperError;
    if HostError <> '' then
      HostError := #13#10 + #13#10 + HostError;
    ErrorText :=
      'Setup could not begin exclusive Vita Moonlight maintenance (exit code ' +
      IntToStr(ResultCode) + ').' + HostError + #13#10 + #13#10 +
      'Finish any other setup or host change, then run this installer again.';
    exit;
  end;
  DeleteFile(ExpandConstant(
    '{tmp}\VitaMoonlight.Host.Maintenance.error.txt'));

  MaintenanceFenceActive := True;
  Log(
    'Protected installer maintenance began under owner process ' +
    IntToStr(MaintenanceOwnerPid) + '.');
  Result := True;
end;

function EndUpgradeMaintenance: Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  if not MaintenanceFenceActive then
    exit;
  ResultCode := -1;
  if not Exec(
    ExpandConstant('{tmp}\VitaMoonlight.Host.Maintenance.exe'),
    'maintenance end --owner-pid ' + IntToStr(MaintenanceOwnerPid),
    ExpandConstant('{tmp}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log(
      'ERROR: Setup could not start the helper which ends protected installer maintenance. ' +
      'The fence was kept for a safe installer retry.');
    Result := False;
    exit;
  end;
  if ResultCode <> 0 then
  begin
    Log(
      'ERROR: Setup could not end protected installer maintenance. Exit code: ' +
      IntToStr(ResultCode) +
      '. The fence was kept for a safe installer retry.');
    Result := False;
    exit;
  end;
  MaintenanceFenceActive := False;
  Log('Protected installer maintenance ended.');
end;

function QueryMaintenanceSnapshotState(
  const Description: String;
  const Action: String;
  var WasPresent: Boolean;
  var ErrorText: String): Boolean;
var
  ResultCode: Integer;
begin
  WasPresent := False;
  ErrorText := '';
  WizardForm.StatusLabel.Caption := Description;
  WizardForm.StatusLabel.Update;
  ResultCode := -1;
  if not Exec(
    ExpandConstant('{tmp}\VitaMoonlight.Host.Maintenance.exe'),
    'maintenance ' + Action + ' --owner-pid ' +
      IntToStr(MaintenanceOwnerPid),
    ExpandConstant('{tmp}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    ErrorText := Description + ' could not be started.';
    Result := False;
    exit;
  end;
  if ResultCode = 0 then
  begin
    WasPresent := True;
    Result := True;
    exit;
  end;
  if ResultCode = 3 then
  begin
    WasPresent := False;
    Result := True;
    exit;
  end;
  ErrorText := Description + ' failed with exit code ' +
    IntToStr(ResultCode) + '. The protected maintenance snapshot was kept.';
  Result := False;
end;

function RunPreflightHostCommand(
  const Description: String;
  const Parameters: String;
  var ResultCode: Integer;
  var ErrorText: String): Boolean;
begin
  WizardForm.StatusLabel.Caption := Description;
  WizardForm.StatusLabel.Update;
  ErrorText := '';
  ResultCode := -1;
  Result := Exec(
    PreflightHostPath,
    WithMaintenanceBypass(Parameters),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode);
  if not Result then
  begin
    ErrorText := Description + ' could not be started.';
    exit;
  end;
  if ResultCode <> 0 then
  begin
    ErrorText := ReadSetupHostCommandError;
    if ErrorText <> '' then
      ErrorText := #13#10 + ErrorText;
    ErrorText := Description + ' failed with exit code ' +
      IntToStr(ResultCode) + '.' + ErrorText;
    Result := False;
  end;
end;

function BackendLifecycleStateExists: Boolean;
begin
  Result :=
    FileExists(ExpandConstant('{app}\state\backend-lifecycle.json')) or
    FileExists(ExpandConstant('{app}\state\backend-lifecycle.backup.json')) or
    FileExists(ExpandConstant('{app}\state\backend-disabled.intent'));
end;

procedure TryRestoreUpgradeSafeguards;
var
  InstalledHostPath: String;
  ResultCode: Integer;
begin
  if not UpgradeBackendWasEnabled then
    exit;
  if not (UpgradeAgentWasStopped or UpgradeRecoveryTaskWasRemoved) then
    exit;

  InstalledHostPath := ExpandConstant('{app}\VitaMoonlight.Host.exe');
  if not FileExists(InstalledHostPath) then
  begin
    Log(
      'ERROR: Setup could not roll back the recovery safeguards because ' +
      'the installed host executable is unavailable: ' + InstalledHostPath);
    exit;
  end;

  { A legacy host with no lifecycle record has no Pause feature to re-read and
    may not expose the current `backend` command. Keep its enabled snapshot
    authoritative without sending it a command it cannot understand. A
    private pre-fence build which did publish lifecycle state may still have
    accepted Pause after setup's initial snapshot, so re-read that intent
    immediately before rollback and never recreate enabled-state tasks over a
    newer Paused preference. }
  ResultCode := 0;
  if not BackendLifecycleStateExists then
  begin
    Log(
      'The installed host has no backend lifecycle record; using the protected ' +
      'enabled-state maintenance snapshot for legacy safeguard rollback.');
  end
  else if not Exec(
    InstalledHostPath,
    WithMaintenanceBypass('backend status --intent-exit-code'),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    Log(
      'ERROR: Setup could not re-read the current Vita host preference before ' +
      'safeguard rollback. The maintenance fence will be kept for retry.');
    exit;
  end;
  if ResultCode = 5 then
  begin
    UpgradeBackendWasEnabled := False;
    UpgradeBackendWasDisabled := True;
    UpgradeAgentWasStopped := False;
    UpgradeRecoveryTaskWasRemoved := False;
    Log(
      'The current Vita host preference is Paused; setup did not recreate ' +
      'the previously enabled recovery tasks during rollback.');
    exit;
  end;
  if ResultCode <> 0 then
  begin
    Log(
      'ERROR: Setup could not safely classify the current Vita host preference ' +
      'before safeguard rollback. Exit code: ' + IntToStr(ResultCode) +
      '. The maintenance fence will be kept for retry.');
    exit;
  end;

  if UpgradeRecoveryTaskWasRemoved then
  begin
    ResultCode := -1;
    if Exec(
      InstalledHostPath,
      WithMaintenanceBypass('recovery install'),
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0) then
    begin
      UpgradeRecoveryTaskWasRemoved := False;
      Log('Rolled back the automatic display-recovery task.');
    end
    else
      Log(
        'ERROR: Setup could not roll back the automatic display-recovery task. ' +
        'Exit code: ' + IntToStr(ResultCode));
  end;

  if UpgradeAgentWasStopped then
  begin
    ResultCode := -1;
    if Exec(
      InstalledHostPath,
      WithMaintenanceBypass('agent install'),
      ExpandConstant('{app}'),
      SW_HIDE,
      ewWaitUntilTerminated,
      ResultCode) and (ResultCode = 0) then
    begin
      UpgradeAgentWasStopped := False;
      Log('Rolled back the stream-rescue agent.');
    end
    else
      Log(
        'ERROR: Setup could not roll back the stream-rescue agent. ' +
        'Exit code: ' + IntToStr(ResultCode));
  end;
end;

procedure RecordSetupFailure(const ErrorText: String);
begin
  if SetupFailureRecorded then
    exit;

  SetupFailureRecorded := True;
  SetupFailureText :=
    'Setup installed or updated the application files, but could not finish ' +
    'configuring this PC.' + #13#10 + #13#10 +
    ErrorText + #13#10 + #13#10 +
    'No later host-configuration steps were run. Close Setup, correct the ' +
    'reported problem, and run this installer again. Setup will return a ' +
    'nonzero result so deployment tools cannot mistake this for success.';
  ConfigurationDeferredForRestart := True;
  Log('ERROR: ' + SetupFailureText);

  { Restore safeguards removed during upgrade preflight immediately. The
    maintenance fence remains active until DeinitializeSetup verifies this
    rollback and closes the exact transaction. }
  TryRestoreUpgradeSafeguards;

  if SetupFailurePage <> nil then
    SetupFailurePage.RichEditViewer.Lines.Text := SetupFailureText;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  ErrorText: String;
  AgentTaskWasInstalled: Boolean;
  RecoveryTaskWasInstalled: Boolean;
begin
  Result := '';
  NeedsRestart := False;
  if not BeginUpgradeMaintenance(ErrorText) then
  begin
    Result := ErrorText;
    exit;
  end;

  { Read the original, durable pre-mutation snapshot from the maintenance
    helper. A setup process killed after deleting either exact task can then be
    retried without mistaking the current Missing state for the old baseline. }
  if not QueryMaintenanceSnapshotState(
    'Reading the saved Vita host-feature preference for upgrade or repair',
    'backend-was-enabled',
    UpgradeBackendWasEnabled,
    ErrorText) then
  begin
    Result := ErrorText;
    exit;
  end;
  UpgradeBackendWasDisabled := not UpgradeBackendWasEnabled;
  if not QueryMaintenanceSnapshotState(
    'Reading the saved stream-rescue rollback obligation',
    'rescue-task-was-present',
    AgentTaskWasInstalled,
    ErrorText) then
  begin
    Result := ErrorText;
    exit;
  end;
  if not QueryMaintenanceSnapshotState(
    'Reading the saved display-recovery rollback obligation',
    'recovery-task-was-present',
    RecoveryTaskWasInstalled,
    ErrorText) then
  begin
    Result := ErrorText;
    exit;
  end;
  if UpgradeBackendWasEnabled then
  begin
    { Establish both rollback flags before any installed child can mutate a
      task. These flags are reconstructed from the durable snapshot on every
      dead-owner retry. }
    UpgradeAgentWasStopped := AgentTaskWasInstalled;
    UpgradeRecoveryTaskWasRemoved := RecoveryTaskWasInstalled;
  end;

  { After the embedded helper's narrowly gated physical-recovery bootstrap,
    later task mutations deliberately trust only the protected, currently
    installed executable. The temporary helper receives no general installed-
    host authority. }
  PreflightHostPath := ExpandConstant('{app}\VitaMoonlight.Host.exe');
  if not FileExists(PreflightHostPath) then
  begin
    if ExistingInstallDetected then
      Log(
        'The registered installation has no host executable. Setup will repair ' +
        'the payload after the embedded helper has already restored and verified ' +
        'the physical display. No existing background process was stopped.')
    else
      Log(
        'Protected maintenance now covers this clean installation. The physical ' +
        'desktop is safe and no installed-host preflight is required before files are copied.');
    exit;
  end;

  { The embedded current maintenance helper already restored and verified a
    physical-only topology before it published the maintenance snapshot in
    BeginUpgradeMaintenance. Do not repeat that step through the installed
    host: older supported builds do not expose `uninstall prepare` and return
    exit code 2 for that current-only command. }
  Log(
    'Protected installer maintenance already restored and verified the ' +
    'physical display before upgrade or repair.');

  if UpgradeBackendWasDisabled then
  begin
    if not RunPreflightHostCommand(
      'Stopping any remaining paused stream-rescue agent',
      'agent uninstall',
      ResultCode,
      ErrorText) then
    begin
      Result := ErrorText + #13#10 + #13#10 +
        'Setup stopped before replacing files. The saved paused preference was kept.';
      exit;
    end;
    if not RunPreflightHostCommand(
      'Removing any remaining paused display-recovery task',
      'recovery uninstall',
      ResultCode,
      ErrorText) then
    begin
      Result := ErrorText + #13#10 + #13#10 +
        'Setup stopped before replacing files. The saved paused preference was kept.';
      exit;
    end;
    Log(
      'Existing Vita host features are intentionally paused; setup will ' +
      'update product files without re-enabling shared components or safeguards.');
    exit;
  end;

  if not RunPreflightHostCommand(
    'Stopping the stream-rescue agent for upgrade or repair',
    'agent uninstall',
    ResultCode,
    ErrorText) then
  begin
    Result := ErrorText + #13#10 + #13#10 +
      'The physical display is safe, but setup stopped before replacing files.';
    exit;
  end;
  if not RunPreflightHostCommand(
    'Suspending automatic display recovery for upgrade or repair',
    'recovery uninstall',
    ResultCode,
    ErrorText) then
  begin
    Result := ErrorText + #13#10 + #13#10 +
      'Setup is restoring any safeguard it already stopped before returning.';
    TryRestoreUpgradeSafeguards;
    exit;
  end;
end;

procedure DeinitializeSetup;
begin
  if not UpgradeSafeguardsRestored then
    TryRestoreUpgradeSafeguards;
  if MaintenanceFenceActive then
  begin
    if UpgradeAgentWasStopped or UpgradeRecoveryTaskWasRemoved then
    begin
      Log(
        'ERROR: Setup kept the installer-maintenance fence because one or more ' +
        'pre-existing recovery safeguards could not be restored. Run setup again.');
    end
    else if not EndUpgradeMaintenance then
    begin
      Log(
        'ERROR: Setup finished without clearing its exact maintenance fence. ' +
        'Run this installer again before using Vita host features.');
    end;
  end;
end;

function DriverReadyForConfiguration: Boolean;
var
  ResultCode: Integer;
begin
  if not DriverReadinessChecked then
  begin
    DriverReadinessChecked := True;
    DriverReady :=
      Exec(
        ExpandConstant('{app}\VitaMoonlight.Host.exe'),
        WithMaintenanceBypass('driver status'),
        ExpandConstant('{app}'),
        SW_HIDE,
        ewWaitUntilTerminated,
        ResultCode) and
      (ResultCode = 0);
    DriverNeedsAttention := not DriverReady;
  end;
  Result := DriverReady;
end;

function CanLaunchControlPanel: Boolean;
begin
  Result := not ConfigurationDeferredForRestart and not SetupFailureRecorded;
end;

function NeedRestart: Boolean;
begin
  Result := RestartRequiredByPrerequisite;
end;

function TryGetBackendSetupIntent(var BackendIntent: Integer): Boolean;
var
  ErrorText: String;
begin
  Result := False;
  BackendIntent := -1;
  if not Exec(
    ExpandConstant('{app}\VitaMoonlight.Host.exe'),
    WithMaintenanceBypass('backend status --intent-exit-code'),
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    BackendIntent) then
  begin
    RecordSetupFailure(
      'Setup could not read the saved Vita host-feature preference. ' +
      'No Vita host configuration was changed.');
    exit;
  end;

  if (BackendIntent <> 0) and (BackendIntent <> 5) and
    (BackendIntent <> 6) then
  begin
    ErrorText := ReadSetupHostCommandError;
    if ErrorText <> '' then
      ErrorText := #13#10 + #13#10 + ErrorText;
    RecordSetupFailure(
      'Setup could not safely classify the saved Vita host-feature preference ' +
      '(exit code ' + IntToStr(BackendIntent) + '). No Vita host configuration was changed.' +
      ErrorText);
    exit;
  end;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  BackendIntent: Integer;
  DeferredSetupParameters: String;
begin
  if CurStep <> ssPostInstall then
    exit;

  if not RunRequiredHostCommand(
    'Safely recovering legacy display state',
    'session recover-upgrade') then
    exit;

  if not RunRequiredHostCommand(
    'Securing machine recovery state',
    'state secure') then
    exit;

  { A deliberate pause is a durable user preference, not a failed setup.
    Update product files and safe shared prerequisites, reassert the
    physical-safe paused state with the new executable, and defer work which
    could enable VDD or recreate background tasks until explicit Enable. }
  if not TryGetBackendSetupIntent(BackendIntent) then
    exit;
  if BackendIntent = 6 then
  begin
    RecordSetupFailure(
      'The protected Vita host-feature record is unreadable. Setup kept ' +
      'the physical desktop and ran no Vita host-configuration steps. Open the ' +
      'Vita Moonlight Host control panel as Administrator for recovery details.');
    exit;
  end;
  if BackendIntent = 5 then
  begin
    { Repairs which cannot activate a Vita-owned task or display are safe to
      apply immediately. Host/VDD work is stored as a protected plan and is
      completed transactionally only after the user later chooses Enable. }
    if WizardIsTaskSelected('gamepaddriver') then
    begin
      if not RunRequiredHostCommand(
        'Installing or repairing ViGEmBus while Vita host features remain paused',
        'gamepad ensure-compatible --installer "' +
        ExpandConstant('{app}\tools\ViGEmBus\ViGEmBus_1.22.0_x64_x86_arm64.exe') +
        '"') then
        exit;
    end;

    if WizardIsTaskSelected('host\sunshine') then
    begin
      if not RunRequiredHostCommand(
        'Checking the virtual display runtime while Vita host features remain paused',
        'runtime ensure-compatible --installer "' +
        ExpandConstant('{app}\tools\DisplayWizard\VC_redist.x64.exe') +
        '"') then
        exit;
    end;

    if WizardIsTaskSelected('host\sunshine') then
    begin
      DeferredSetupParameters :=
        'deferred-setup save --host sunshine --virtual-driver true';
      if not RunRequiredHostCommand(
        'Saving Sunshine setup until Vita host features are enabled',
        DeferredSetupParameters) then
        exit;
    end
    else
    begin
      if not RunRequiredHostCommand(
        'Clearing deferred host setup because no streaming host was selected',
        'deferred-setup clear') then
        exit;
    end;

    if not RunRequiredHostCommand(
      'Preserving the intentionally paused Vita host features',
      'backend disable') then
      exit;
    BackendRemainsPausedAfterSetup := True;
    exit;
  end;

  if WizardIsTaskSelected('gamepaddriver') then
  begin
    if not RunRequiredHostCommand(
      'Installing or repairing ViGEmBus',
      'gamepad ensure-compatible --installer "' +
      ExpandConstant('{app}\tools\ViGEmBus\ViGEmBus_1.22.0_x64_x86_arm64.exe') +
      '"') then
      exit;
  end;

  if WizardIsTaskSelected('host\sunshine') then
  begin
    if not RunRequiredHostCommand(
      'Checking and updating Sunshine',
      'host ensure-compatible --installer "' +
      ExpandConstant('{app}\tools\Sunshine\Sunshine-Windows-AMD64-installer.msi') +
      '"') then
      exit;
  end;

  if WizardIsTaskSelected('host\sunshine') then
  begin
    if not RunRequiredHostCommand(
      'Checking and repairing the virtual display runtime',
      'runtime ensure-compatible --installer "' +
      ExpandConstant('{app}\tools\DisplayWizard\VC_redist.x64.exe') +
      '"') then
      exit;

    if not RunRequiredHostCommand(
      'Installing and verifying the virtual display driver',
      'driver install --adopt-existing-vdd') then
      exit;
    DriverReadinessChecked := False;
  end;

  if WizardIsTaskSelected('host\sunshine') then
  begin
    if not DriverReadyForConfiguration then
    begin
      DriverNeedsAttention := True;
      RecordSetupFailure(
        'The virtual display did not pass its native 960x544 readiness check. ' +
        'No Sunshine display configuration was written.');
      exit;
    end;
    if not RunRequiredHostCommand(
      'Refreshing Sunshine display detection',
      'host restart --host sunshine') then
      exit;
    if not RunRequiredHostCommand(
      'Configuring Sunshine',
      'configure --host sunshine') then
      exit;
    if not RunRequiredHostCommand(
      'Restarting Sunshine with the Vita configuration',
      'host restart --host sunshine') then
      exit;
  end;

  { The selected non-paused setup path has now superseded any plan left by an
    earlier interrupted paused upgrade. }
  if not RunRequiredHostCommand(
    'Clearing completed deferred host setup',
    'deferred-setup clear') then
    exit;

  if WizardIsTaskSelected('host') then
  begin
    if not RunRequiredHostCommand(
      'Installing the automatic display-recovery safeguard',
      'recovery install') then
      exit;
    if not RunRequiredHostCommand(
      'Installing the in-stream rescue agent',
      'agent install') then
      exit;
  end
  else
  begin
    { A controller-only clean install must not create Vita background tasks.
      During an in-place repair, however, deselecting "configure now" must not
      silently remove safeguards which belonged to the enabled installation. }
    TryRestoreUpgradeSafeguards;
    if UpgradeAgentWasStopped or UpgradeRecoveryTaskWasRemoved then
    begin
      RecordSetupFailure(
        'Setup updated the selected components, but could not restore the ' +
        'pre-existing Vita display safeguards. Run setup again before streaming.');
      exit;
    end;
  end;
  UpgradeSafeguardsRestored := True;
  UpgradeAgentWasStopped := False;
  UpgradeRecoveryTaskWasRemoved := False;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result :=
    ((SetupFailurePage <> nil) and
      (PageID = SetupFailurePage.ID) and not SetupFailureRecorded) or
    ((PageID = wpFinished) and SetupFailureRecorded);
end;

function GetCustomSetupExitCode: Integer;
begin
  if SetupFailureRecorded then
    Result := SetupHostConfigurationFailedExitCode
  else
    Result := 0;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if (SetupFailurePage <> nil) and (CurPageID = SetupFailurePage.ID) then
  begin
    SetupFailurePage.RichEditViewer.Lines.Text := SetupFailureText;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonFinish);
  end
  else if CurPageID = wpWelcome then
  begin
    if ExistingInstallDetected then
    begin
      WizardForm.WelcomeLabel2.Caption :=
        'Setup found Vita Moonlight Host ' + PreviousInstalledVersion + '.' + #13#10 + #13#10 +
        'Continue to update or repair it in place. You do not need to uninstall first. ' +
        'Vita Moonlight restores a safe physical display before changing the host, ' +
        'and preserves Sunshine pairing, unrelated applications, and shared components.';
    end
    else
    begin
      WizardForm.WelcomeLabel2.Caption :=
        'This all-in-one setup prepares Sunshine, controller support, a Vita-sized ' +
        'virtual display, and automatic display recovery.' + #13#10 + #13#10 +
        'Accept the recommended choices. Save open ' +
        'work first because connected displays may briefly blink during verification.';
    end;
  end
  else if (CurPageID = wpFinished) and DriverNeedsAttention then
  begin
    SuppressibleMsgBox(
      'Windows has not finished enumerating the Vita virtual display. ' +
      'Setup preserved your physical display and skipped Sunshine display configuration.' + #13#10 + #13#10 +
      'Restart Windows. Then open Vita Moonlight Host as Administrator, click ' +
      '"Repair Vita display driver" under Display & recovery, and then click ' +
      '"Set up or repair this PC" under Get started.',
      mbInformation,
      MB_OK,
      IDOK);
  end;
  if (CurPageID = wpFinished) and BackendRemainsPausedAfterSetup then
  begin
    SuppressibleMsgBox(
      'The update or repair is installed, and Vita host features remain paused ' +
      'as requested.' + #13#10 + #13#10 +
      'Open Vita Moonlight Host as Administrator and choose "Enable Vita ' +
      'host features" when you want to use it again. Any selected host/display ' +
      'repair will finish during that Enable action; a failed or restart-gated ' +
      'repair safely returns the features to Paused. Pairing and settings were kept.',
      mbInformation,
      MB_OK,
      IDOK);
  end;
end;

function HasUninstallSwitch(const Name: String): Boolean;
var
  I: Integer;
  Value: String;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    Value := ParamStr(I);
    if (CompareText(Value, '/' + Name) = 0) or
       (CompareText(Value, '-' + Name) = 0) then
    begin
      Result := True;
      exit;
    end;
  end;
end;

function IsSilentUninstall: Boolean;
begin
  Result :=
    HasUninstallSwitch('SILENT') or
    HasUninstallSwitch('VERYSILENT');
end;

procedure ReportUninstallError(const MessageText: String);
var
  FullMessage: String;
begin
  FullMessage := MessageText;
  if CompletedExplicitRemovalActions <> '' then
  begin
    FullMessage := FullMessage + #13#10 + #13#10 +
      'Completed explicit shared-component requests before this failure: ' +
      CompletedExplicitRemovalActions + '.' + #13#10 +
      'Those selected component changes are not rolled back; Vita Moonlight ' +
      'kept its own host and recovery safeguards for a safe retry.';
  end;
  Log('ERROR: ' + FullMessage);
  if not IsSilentUninstall then
  begin
    SuppressibleMsgBox(
      FullMessage,
      mbError,
      MB_OK,
      IDOK);
  end;
end;

procedure RecordCompletedExplicitRemoval(const DisplayName: String);
begin
  if CompletedExplicitRemovalActions = '' then
    CompletedExplicitRemovalActions := DisplayName
  else
    CompletedExplicitRemovalActions :=
      CompletedExplicitRemovalActions + ', ' + DisplayName;
  Log(
    'Completed explicit shared-component removal request: ' +
    DisplayName);
end;

function InitializeUninstall: Boolean;
var
  OptionsForm: TSetupForm;
  HeadingLabel: TNewStaticText;
  ExplanationLabel: TNewStaticText;
  SafetyLabel: TNewStaticText;
  RemoveVirtualDisplayCheck: TNewCheckBox;
  RemoveSunshineCheck: TNewCheckBox;
  RemoveViGEmBusCheck: TNewCheckBox;
  PreserveDiagnosticsCheck: TNewCheckBox;
  ContinueButton: TNewButton;
  CancelButton: TNewButton;
begin
  RemoveVirtualDisplayOnUninstall := HasUninstallSwitch('REMOVEVDD');
  RemoveSunshineOnUninstall := HasUninstallSwitch('REMOVESUNSHINE');
  RemoveViGEmBusOnUninstall := HasUninstallSwitch('REMOVEVIGEMBUS');
  PreserveDiagnosticsOnUninstall := HasUninstallSwitch('KEEPDIAGNOSTICS');
  CompletedExplicitRemovalActions := '';
  Result := True;

  { Silent automation removes only this product unless a dependency switch was
    explicitly supplied. Shared software is never removed merely because the
    uninstaller is noninteractive. }
  if IsSilentUninstall then
    exit;

  OptionsForm := CreateCustomForm(ScaleX(590), ScaleY(390), False, True);
  try
    OptionsForm.Caption := 'Uninstall Vita Moonlight Host';

    HeadingLabel := TNewStaticText.Create(OptionsForm);
    HeadingLabel.Parent := OptionsForm;
    HeadingLabel.Left := ScaleX(24);
    HeadingLabel.Top := ScaleY(20);
    HeadingLabel.Width := ScaleX(542);
    HeadingLabel.Height := ScaleY(28);
    HeadingLabel.AutoSize := False;
    HeadingLabel.Font.Style := [fsBold];
    HeadingLabel.Font.Size := 12;
    HeadingLabel.Caption := 'Choose what Vita Moonlight Host should remove';

    ExplanationLabel := TNewStaticText.Create(OptionsForm);
    ExplanationLabel.Parent := OptionsForm;
    ExplanationLabel.Left := ScaleX(24);
    ExplanationLabel.Top := ScaleY(55);
    ExplanationLabel.Width := ScaleX(542);
    ExplanationLabel.Height := ScaleY(55);
    ExplanationLabel.AutoSize := False;
    ExplanationLabel.WordWrap := True;
    ExplanationLabel.Caption :=
      'Sunshine and ViGEmBus can be shared with other streaming or controller ' +
      'software and are kept by default. The MTT driver package may also be ' +
      'shared, so Vita Moonlight never deletes that package without proof it ' +
      'owns every consumer. The option below releases only the exact display ' +
      'device managed by this installation.';

    RemoveVirtualDisplayCheck := TNewCheckBox.Create(OptionsForm);
    RemoveVirtualDisplayCheck.Parent := OptionsForm;
    RemoveVirtualDisplayCheck.Left := ScaleX(36);
    RemoveVirtualDisplayCheck.Top := ScaleY(123);
    RemoveVirtualDisplayCheck.Width := ScaleX(520);
    RemoveVirtualDisplayCheck.Height := ScaleY(28);
    RemoveVirtualDisplayCheck.Caption :=
      'Release the Vita-managed display device (remove it only if Vita created it)';
    RemoveVirtualDisplayCheck.Checked := RemoveVirtualDisplayOnUninstall;

    RemoveSunshineCheck := TNewCheckBox.Create(OptionsForm);
    RemoveSunshineCheck.Parent := OptionsForm;
    RemoveSunshineCheck.Left := ScaleX(36);
    RemoveSunshineCheck.Top := ScaleY(166);
    RemoveSunshineCheck.Width := ScaleX(520);
    RemoveSunshineCheck.Height := ScaleY(28);
    RemoveSunshineCheck.Caption := 'Remove Sunshine';
    RemoveSunshineCheck.Checked := RemoveSunshineOnUninstall;

    RemoveViGEmBusCheck := TNewCheckBox.Create(OptionsForm);
    RemoveViGEmBusCheck.Parent := OptionsForm;
    RemoveViGEmBusCheck.Left := ScaleX(36);
    RemoveViGEmBusCheck.Top := ScaleY(201);
    RemoveViGEmBusCheck.Width := ScaleX(520);
    RemoveViGEmBusCheck.Height := ScaleY(28);
    RemoveViGEmBusCheck.Caption := 'Remove ViGEmBus controller emulation';
    RemoveViGEmBusCheck.Checked := RemoveViGEmBusOnUninstall;

    PreserveDiagnosticsCheck := TNewCheckBox.Create(OptionsForm);
    PreserveDiagnosticsCheck.Parent := OptionsForm;
    PreserveDiagnosticsCheck.Left := ScaleX(36);
    PreserveDiagnosticsCheck.Top := ScaleY(246);
    PreserveDiagnosticsCheck.Width := ScaleX(520);
    PreserveDiagnosticsCheck.Height := ScaleY(28);
    PreserveDiagnosticsCheck.Caption :=
      'Keep the stream-rescue log for troubleshooting (all settings are still removed)';
    PreserveDiagnosticsCheck.Checked := PreserveDiagnosticsOnUninstall;

    SafetyLabel := TNewStaticText.Create(OptionsForm);
    SafetyLabel.Parent := OptionsForm;
    SafetyLabel.Left := ScaleX(24);
    SafetyLabel.Top := ScaleY(292);
    SafetyLabel.Width := ScaleX(542);
    SafetyLabel.Height := ScaleY(43);
    SafetyLabel.AutoSize := False;
    SafetyLabel.WordWrap := True;
    SafetyLabel.Font.Color := clGray;
    SafetyLabel.Caption :=
      'Before anything is deleted, uninstall restores and verifies a physical-only ' +
      'display layout. If that check fails, the host and both recovery safeguards remain installed.';

    ContinueButton := TNewButton.Create(OptionsForm);
    ContinueButton.Parent := OptionsForm;
    ContinueButton.Left := ScaleX(370);
    ContinueButton.Top := ScaleY(348);
    ContinueButton.Width := ScaleX(95);
    ContinueButton.Height := ScaleY(28);
    ContinueButton.Caption := 'Uninstall';
    ContinueButton.Default := True;
    ContinueButton.ModalResult := mrOk;

    CancelButton := TNewButton.Create(OptionsForm);
    CancelButton.Parent := OptionsForm;
    CancelButton.Left := ScaleX(471);
    CancelButton.Top := ScaleY(348);
    CancelButton.Width := ScaleX(95);
    CancelButton.Height := ScaleY(28);
    CancelButton.Caption := 'Cancel';
    CancelButton.Cancel := True;
    CancelButton.ModalResult := mrCancel;

    Result := OptionsForm.ShowModal = mrOk;
    if Result then
    begin
      RemoveVirtualDisplayOnUninstall := RemoveVirtualDisplayCheck.Checked;
      RemoveSunshineOnUninstall := RemoveSunshineCheck.Checked;
      RemoveViGEmBusOnUninstall := RemoveViGEmBusCheck.Checked;
      PreserveDiagnosticsOnUninstall := PreserveDiagnosticsCheck.Checked;
    end;
  finally
    OptionsForm.Free;
  end;
end;

function ReadLastHostCommandError: String;
var
  ErrorDetails: AnsiString;
begin
  Result := '';
  if LoadStringFromFile(
    ExpandConstant('{app}\state\last-command-error.txt'),
    ErrorDetails) then
  begin
    Result := Trim(ErrorDetails);
  end;
end;

function RunCheckedUninstallHostCommand(
  const Description: String;
  const Parameters: String;
  const AllowRestartRequired: Boolean): Boolean;
var
  ErrorText: String;
  ResultCode: Integer;
begin
  UninstallProgressForm.StatusLabel.Caption := Description;
  UninstallProgressForm.StatusLabel.Update;
  if not Exec(
    ExpandConstant('{app}\VitaMoonlight.Host.exe'),
    Parameters,
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    ReportUninstallError(
      Description + ' could not be started.' + #13#10 + #13#10 +
      'Uninstall stopped before deleting the host or its recovery safeguards.');
    Result := False;
    exit;
  end;

  if AllowRestartRequired and (ResultCode = 4) then
  begin
    RestartRequiredByUninstall := True;
    ReportUninstallError(
      Description + ' requires a Windows restart.' + #13#10 + #13#10 +
      'Restart Windows, then run uninstall again. Vita Moonlight Host and its ' +
      'recovery safeguards were kept so post-restart cleanup can be verified.');
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorText := ReadLastHostCommandError;
    if ErrorText <> '' then
      ErrorText := #13#10 + #13#10 + ErrorText;
    ReportUninstallError(
      Description + ' failed with exit code ' + IntToStr(ResultCode) + '.' +
      ErrorText + #13#10 + #13#10 +
      'Uninstall stopped before deleting the host or its remaining recovery safeguards. ' +
      'Open Vita Moonlight Host as Administrator, recover the physical display, and try again.');
    Result := False;
    exit;
  end;
  Result := True;
end;

function PreserveRescueLog: Boolean;
var
  SourcePath: String;
begin
  Result := True;
  PreservedRescueLogPath := '';
  if not PreserveDiagnosticsOnUninstall then
    exit;
  SourcePath := ExpandConstant(
    '{app}\state\Diagnostics\stream-rescue.log');
  if not FileExists(SourcePath) then
    exit;
  PreservedRescueLogPath := ExpandConstant(
    '{commonappdata}\VitaMoonlight-stream-rescue-' +
    GetDateTimeString('yyyymmdd-hhnnss', '-', ':') + '.log');
  { The source tree is administrator-owned beneath Program Files. Rename the
    entry directly to ProgramData without opening or copying its contents. }
  if not RenameFile(SourcePath, PreservedRescueLogPath) then
  begin
    ReportUninstallError(
      'Windows could not preserve the requested stream-rescue log at:' + #13#10 +
      PreservedRescueLogPath + #13#10 + #13#10 +
      'Uninstall stopped without deleting the original log or the recovery safeguards.');
    PreservedRescueLogPath := '';
    Result := False;
  end;
end;

function UninstallFinalizationCommitted: Boolean;
var
  MarkerText: AnsiString;
begin
  Result :=
    LoadStringFromFile(
      ExpandConstant('{app}\state\uninstall-in-progress.intent'),
      MarkerText) and
    (CompareStr(
      String(MarkerText),
      'vita-moonlight-uninstall-finalized-v1') = 0);
end;

function DeleteFinalizedHostExecutable: Boolean;
var
  HostPath: String;
begin
  HostPath := ExpandConstant('{app}\VitaMoonlight.Host.exe');
  Result := True;
  if not FileExists(HostPath) then
    exit;
  if not DeleteFile(HostPath) then
  begin
    ReportUninstallError(
      'Vita Moonlight finalization committed safely, but Windows could not ' +
      'delete the stopped host executable:' + #13#10 + HostPath + #13#10 + #13#10 +
      'The finalized transaction guard was kept. Close any process using the ' +
      'file, then run uninstall again.');
    Result := False;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  FinalizeParameters: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { A crash after host finalization may leave only the uninstaller and the
      finalized guard. If the host still exists, reverify the physical desktop
      with it. If it was already deleted, the exact finalized stage is the
      durable authority to resume file-only cleanup. Any missing/torn stage
      fails closed. }
    if UninstallFinalizationCommitted then
    begin
      if FileExists(ExpandConstant('{app}\VitaMoonlight.Host.exe')) then
      begin
        if not RunCheckedUninstallHostCommand(
          'Reverifying the finalized physical display cleanup',
          'uninstall prepare --begin',
          False) then
          Abort;
      end;
      if not DeleteFinalizedHostExecutable then
        Abort;
      Log(
        'Resuming exact file-only cleanup from the durable finalized uninstall stage.');
      exit;
    end;
    if not FileExists(ExpandConstant('{app}\VitaMoonlight.Host.exe')) then
    begin
      ReportUninstallError(
        'The Vita Moonlight host executable is missing, but Windows does not ' +
        'contain a valid finalized uninstall transaction.' + #13#10 + #13#10 +
        'Restore VitaMoonlight.Host.exe from your security-software quarantine, ' +
        'or copy the same-version file from the portable release into the Vita ' +
        'Moonlight Host install folder, then retry uninstall. Do not start a ' +
        'new install while this protected uninstall transaction remains. ' +
        'The transaction guard and recovery state were kept.');
      Abort;
    end;

    { No product or safeguard is removed until Windows confirms that at least
      one physical monitor is active and the managed VDD is inactive. }
    if not RunCheckedUninstallHostCommand(
      'Restoring and verifying the physical display',
      'uninstall prepare --begin',
      False) then
      Abort;

    if RemoveVirtualDisplayOnUninstall then
    begin
      if not RunCheckedUninstallHostCommand(
        'Releasing the exact Vita-managed virtual display device',
        'driver uninstall',
        True) then
        Abort;
      RecordCompletedExplicitRemoval('Vita-managed virtual display device');
    end;

    if RemoveSunshineOnUninstall then
    begin
      if not RunCheckedUninstallHostCommand(
        'Removing shared Sunshine installation',
        'dependency uninstall sunshine',
        True) then
        Abort;
      RecordCompletedExplicitRemoval('Sunshine');
    end;

    if RemoveViGEmBusOnUninstall then
    begin
      if not RunCheckedUninstallHostCommand(
        'Removing shared ViGEmBus installation',
        'dependency uninstall vigembus',
        True) then
        Abort;
      RecordCompletedExplicitRemoval('ViGEmBus');
    end;

    if not PreserveRescueLog then
      Abort;

    { Finalization performs another physical-only recovery after any lengthy
      optional dependency removals. It removes and verifies both exact tasks,
      stops the agent, restores Vita-owned Sunshine configuration only after
      all earlier fallible work passes, and cleans exact state. Safeguards are
      rolled back if a pre-commit operation fails. }
    FinalizeParameters := 'uninstall finalize-owned';
    if RemoveSunshineOnUninstall then
      FinalizeParameters := FinalizeParameters + ' --sunshine-removed';
    if RemoveVirtualDisplayOnUninstall then
      FinalizeParameters := FinalizeParameters + ' --vdd-removed';
    if not RunCheckedUninstallHostCommand(
      'Removing Vita Moonlight background functions and owned state',
      FinalizeParameters,
      False) then
      Abort;
    if not UninstallFinalizationCommitted then
    begin
      ReportUninstallError(
        'Vita Moonlight Host returned success without publishing its exact ' +
        'finalized uninstall transaction. The host and guard were kept for retry.');
      Abort;
    end;
    if not DeleteFinalizedHostExecutable then
      Abort;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    if (PreservedRescueLogPath <> '') and not IsSilentUninstall then
    begin
      SuppressibleMsgBox(
        'Uninstall is complete. The requested stream-rescue log was kept at:' +
        '' + #13#10 + #13#10 + PreservedRescueLogPath,
        mbInformation,
        MB_OK,
        IDOK);
    end;
  end;
end;

function UninstallNeedRestart: Boolean;
begin
  Result := RestartRequiredByUninstall;
end;
