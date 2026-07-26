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
AppVersion=0.14.6
VersionInfoVersion=0.14.6.0
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
Name: "host\sunshine"; Description: "Sunshine - recommended for most users"; Flags: exclusive
Name: "host\sunshine\virtualdriver"; Description: "Vita-sized virtual display (recommended with Sunshine)"
Name: "host\apollo"; Description: "Apollo - select only if this PC already uses Apollo"; Flags: exclusive unchecked

[Dirs]
Name: "{app}\state"
Name: "{app}\state\Diagnostics"

[Registry]
Root: HKLM64; Subkey: "SOFTWARE\VitaMoonlight\Host"; Flags: uninsdeletekey

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DisplayWizardDir}\*"; DestDir: "{app}\tools\DisplayWizard"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ViGEmBusDir}\*"; DestDir: "{app}\tools\ViGEmBus"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SunshineDir}\*"; DestDir: "{app}\tools\Sunshine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\tools\summarize-vita-log.py"; DestDir: "{app}\tools\SupportLog"; Flags: ignoreversion
Source: "..\..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion
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
Source: "..\..\docs\VITA_SETTINGS_GUIDE.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE-VitaMoonlight.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Vita Moonlight Host Control Panel"; Filename: "{app}\VitaMoonlight.Host.exe"; WorkingDir: "{app}"
Name: "{group}\Quick start and help"; Filename: "{sys}\notepad.exe"; Parameters: """{app}\README.md"""
Name: "{group}\Uninstall Vita Moonlight Host"; Filename: "{uninstallexe}"

[Run]
Filename: "{app}\VitaMoonlight.Host.exe"; Description: "Open the Vita Moonlight Host Control Panel"; Check: CanLaunchControlPanel; Flags: postinstall skipifsilent nowait

[Code]
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
  ExistingInstallDetected: Boolean;
  PreviousInstalledVersion: String;

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
    Parameters,
    ExpandConstant('{app}'),
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode) then
  begin
    RaiseException(Description + ' could not be started.');
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
      RaiseException(
        Description + ' failed with exit code ' + IntToStr(ResultCode) + '.' + #13#10 + #13#10 +
        ErrorText + #13#10 + #13#10 +
        'No later host-configuration steps were run.');
    end
    else
    begin
      RaiseException(
        Description + ' failed with exit code ' + IntToStr(ResultCode) + '.' + #13#10 +
        'No later host-configuration steps were run.');
    end;
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
        'driver status',
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
  Result := not ConfigurationDeferredForRestart;
end;

function NeedRestart: Boolean;
begin
  Result := RestartRequiredByPrerequisite;
end;

procedure CurStepChanged(CurStep: TSetupStep);
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

  if WizardIsTaskSelected('host\sunshine\virtualdriver') then
  begin
    if not RunRequiredHostCommand(
      'Checking and repairing the virtual display runtime',
      'runtime ensure-compatible --installer "' +
      ExpandConstant('{app}\tools\DisplayWizard\VC_redist.x64.exe') +
      '"') then
      exit;

    if not RunRequiredHostCommand(
      'Installing and verifying the virtual display driver',
      'driver install') then
      exit;
    DriverReadinessChecked := False;
  end;

  if WizardIsTaskSelected('host\sunshine') then
  begin
    if not DriverReadyForConfiguration then
    begin
      DriverNeedsAttention := True;
      RaiseException(
        'The virtual display did not pass its native 960x544 readiness check. ' +
        'No Sunshine display configuration was written.');
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

  if WizardIsTaskSelected('host\apollo') then
  begin
    if not RunRequiredHostCommand(
      'Configuring Apollo',
      'configure --host apollo') then
      exit;
  end;

  if not RunRequiredHostCommand(
    'Installing the automatic display-recovery safeguard',
    'recovery install') then
    exit;
  if not RunRequiredHostCommand(
    'Installing the in-stream rescue agent',
    'agent install') then
    exit;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpWelcome then
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
        'Accept the recommended choices unless this PC already uses Apollo. Save open ' +
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
begin
  Log('ERROR: ' + MessageText);
  if not IsSilentUninstall then
  begin
    SuppressibleMsgBox(
      MessageText,
      mbError,
      MB_OK,
      IDOK);
  end;
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
      'Sunshine, ViGEmBus, and the virtual display driver can be shared with ' +
      'other streaming or controller software. They are kept by default. ' +
      'Select a dependency only when you want it removed from this PC.';

    RemoveVirtualDisplayCheck := TNewCheckBox.Create(OptionsForm);
    RemoveVirtualDisplayCheck.Parent := OptionsForm;
    RemoveVirtualDisplayCheck.Left := ScaleX(36);
    RemoveVirtualDisplayCheck.Top := ScaleY(123);
    RemoveVirtualDisplayCheck.Width := ScaleX(520);
    RemoveVirtualDisplayCheck.Height := ScaleY(28);
    RemoveVirtualDisplayCheck.Caption :=
      'Remove the MTT virtual display driver and its managed display configuration';
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

procedure PreserveRescueLog;
var
  SourcePath: String;
begin
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
      'Windows could not preserve the requested stream-rescue log at:' +
      '' + #13#10 + PreservedRescueLogPath);
    PreservedRescueLogPath := '';
  end;
end;

procedure RemoveHostState;
var
  StateDirectory: String;
begin
  StateDirectory := ExpandConstant('{app}\state');
  if not DelTree(StateDirectory, True, True, True) then
  begin
    ReportUninstallError(
      'Vita Moonlight Host was removed, but Windows could not delete all files under:' + #13#10 +
      StateDirectory + #13#10 + #13#10 +
      'No recovery task or background agent remains. You may delete that folder after restarting Windows.');
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    { No product or safeguard is removed until Windows confirms that at least
      one physical monitor is active and the managed VDD is inactive. }
    if not RunCheckedUninstallHostCommand(
      'Restoring and verifying the physical display',
      'uninstall prepare',
      False) then
      Abort;

    if not RunCheckedUninstallHostCommand(
      'Restoring Vita-owned Sunshine configuration',
      'uninstall cleanup-integration',
      False) then
      Abort;

    if RemoveVirtualDisplayOnUninstall and
       not RunCheckedUninstallHostCommand(
         'Removing the shared virtual display driver',
         'driver uninstall',
         True) then
      Abort;

    if RemoveSunshineOnUninstall and
       not RunCheckedUninstallHostCommand(
         'Removing shared Sunshine installation',
         'dependency uninstall sunshine',
         True) then
      Abort;

    if RemoveViGEmBusOnUninstall and
       not RunCheckedUninstallHostCommand(
         'Removing shared ViGEmBus installation',
         'dependency uninstall vigembus',
         True) then
      Abort;

    if not RunCheckedUninstallHostCommand(
      'Removing the in-stream rescue agent',
      'agent uninstall',
      False) then
      Abort;
    if not RunCheckedUninstallHostCommand(
      'Removing the automatic display-recovery safeguard',
      'recovery uninstall',
      False) then
      Abort;

    PreserveRescueLog;
  end
  else if CurUninstallStep = usPostUninstall then
  begin
    RemoveHostState;
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
