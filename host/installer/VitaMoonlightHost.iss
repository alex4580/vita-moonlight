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
DefaultGroupName=Vita Moonlight Host
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
PrivilegesRequired=admin
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
Name: "gamepaddriver"; Description: "Install or repair ViGEmBus for Xbox/DS4 controller emulation"; GroupDescription: "Host setup:"
Name: "host"; Description: "Configure a streaming host now"; GroupDescription: "Host setup:"
Name: "host\sunshine"; Description: "Sunshine (signed virtual display)"; Flags: exclusive
Name: "host\sunshine\virtualdriver"; Description: "Install or repair the pinned, officially signed virtual display driver"
Name: "host\apollo"; Description: "Apollo (built-in virtual display)"; Flags: exclusive unchecked

[Dirs]
Name: "{commonappdata}\VitaMoonlight"; Permissions: users-modify

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#DisplayWizardDir}\*"; DestDir: "{app}\tools\DisplayWizard"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#ViGEmBusDir}\*"; DestDir: "{app}\tools\ViGEmBus"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SunshineDir}\*"; DestDir: "{app}\tools\Sunshine"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; DestName: "README.md"; Flags: ignoreversion
Source: "..\END_TO_END_TEST.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\FINAL_RELEASE_CHECKLIST.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\COMPATIBILITY.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\docs\VITA_SETTINGS_GUIDE.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD_PARTY_NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE-VitaMoonlight.txt"; Flags: ignoreversion

[Icons]
Name: "{group}\Vita Moonlight Host Control Panel"; Filename: "{app}\VitaMoonlight.Host.exe"; WorkingDir: "{app}"
Name: "{group}\Documentation"; Filename: "{app}\README.md"
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
    MsgBox(
      'Vita Moonlight Host requires client Windows 10 version 2004 or newer, ' +
      'or Windows 11, on an x64 Intel or AMD PC. Windows Server, ARM64, and x86 ' +
      'are not supported by this package.',
      mbError,
      MB_OK);
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
    MsgBox(
      Description + ' requires a Windows restart.' + #13#10 + #13#10 +
      'Setup has stopped before applying Sunshine display configuration or ' +
      'changing the active display. ' +
      'Restart Windows, then open Vita Moonlight Host as Administrator and ' +
      'click "Apply recommended setup" to finish.',
      mbInformation,
      MB_OK);
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    ErrorPath := ExpandConstant(
      '{commonappdata}\VitaMoonlight\last-command-error.txt');
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
    'Checking for an interrupted display session',
    'session recover') then
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
  if (CurPageID = wpFinished) and DriverNeedsAttention then
  begin
    MsgBox(
      'Windows has not finished enumerating the Vita virtual display. ' +
      'Setup preserved your physical display and skipped Sunshine display configuration.' + #13#10 + #13#10 +
      'Restart Windows. Then open Vita Moonlight Host as Administrator, click ' +
      '"Install/update display driver", and click "Apply recommended setup".',
      mbInformation,
    MB_OK);
  end;
end;

function RunCheckedUninstallHostCommand(
  const Description: String;
  const Parameters: String): Boolean;
var
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
    SuppressibleMsgBox(
      Description + ' could not be started.' + #13#10 + #13#10 +
      'Uninstall stopped before deleting the host or its recovery safeguards.',
      mbError,
      MB_OK,
      IDOK);
    Result := False;
    exit;
  end;

  if ResultCode <> 0 then
  begin
    SuppressibleMsgBox(
      Description + ' failed with exit code ' + IntToStr(ResultCode) + '.' + #13#10 + #13#10 +
      'Uninstall stopped before deleting the host or its remaining recovery safeguards. ' +
      'Open Vita Moonlight Host as Administrator, recover the physical display, and try again.',
      mbError,
      MB_OK,
      IDOK);
    Result := False;
    exit;
  end;
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep <> usUninstall then
    exit;

  { Recovery must succeed while both the host executable and rescue tasks are
    still present. Never remove the last recovery path from a dark desktop. }
  if not RunCheckedUninstallHostCommand(
    'Recovering the physical display before uninstall',
    'session recover') then
    Abort;
  if not RunCheckedUninstallHostCommand(
    'Removing the in-stream rescue agent',
    'agent uninstall') then
    Abort;
  if not RunCheckedUninstallHostCommand(
    'Removing the automatic display-recovery safeguard',
    'recovery uninstall') then
    Abort;
end;
