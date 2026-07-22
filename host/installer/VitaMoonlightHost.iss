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
AppVersion=0.14.0
AppPublisher=Vita Moonlight contributors
AppPublisherURL=https://github.com/xyzz/vita-moonlight
DefaultDirName={autopf}\Vita Moonlight Host
DefaultGroupName=Vita Moonlight Host
DisableProgramGroupPage=yes
LicenseFile=..\..\LICENSE
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
PrivilegesRequired=admin
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=Vita-Moonlight-Host-Setup-win-x64
UninstallDisplayIcon={app}\VitaMoonlight.Host.exe

[Tasks]
Name: "gamepaddriver"; Description: "Install ViGEmBus for Xbox/DS4 controller emulation"; GroupDescription: "Host setup:"; Flags: checkedonce
Name: "host"; Description: "Configure a streaming host now"; GroupDescription: "Host setup:"; Flags: checkedonce
Name: "host\sunshine"; Description: "Sunshine (signed virtual display)"; Flags: exclusive
Name: "host\sunshine\virtualdriver"; Description: "Install the pinned, officially signed virtual display driver"; Flags: checkedonce
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
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "session recover"; StatusMsg: "Checking for an interrupted display session..."; Flags: runhidden waituntilterminated
Filename: "{app}\tools\ViGEmBus\ViGEmBus_1.22.0_x64_x86_arm64.exe"; Parameters: "/qn /norestart"; StatusMsg: "Installing the virtual gamepad driver..."; Tasks: gamepaddriver; Check: ViGEmBusMissing; Flags: runhidden waituntilterminated
Filename: "{sys}\msiexec.exe"; Parameters: "/i ""{app}\tools\Sunshine\Sunshine-Windows-AMD64-installer.msi"" /qn /norestart"; StatusMsg: "Installing Sunshine..."; Tasks: host\sunshine; Check: SunshineMissing; Flags: runhidden waituntilterminated
Filename: "{app}\tools\DisplayWizard\VC_redist.x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the virtual display runtime..."; Tasks: host\sunshine\virtualdriver; Flags: runhidden waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "driver install"; StatusMsg: "Installing the virtual display driver..."; Tasks: host\sunshine\virtualdriver; Flags: waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "host restart --host sunshine"; StatusMsg: "Refreshing Sunshine display detection..."; Tasks: host\sunshine; Flags: runhidden waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "configure --host sunshine"; StatusMsg: "Configuring Sunshine..."; Tasks: host\sunshine; Flags: waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "host restart --host sunshine"; StatusMsg: "Restarting Sunshine with gamepad support..."; Tasks: host\sunshine; Flags: runhidden waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "configure --host apollo"; StatusMsg: "Configuring Apollo..."; Tasks: host\apollo; Flags: waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "recovery install"; StatusMsg: "Installing the automatic display-recovery safeguard..."; Flags: runhidden waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "agent install"; StatusMsg: "Installing the in-stream rescue agent..."; Flags: runhidden waituntilterminated
Filename: "{app}\VitaMoonlight.Host.exe"; Description: "Open the Vita Moonlight Host Control Panel"; Flags: postinstall skipifsilent nowait

[UninstallRun]
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "agent uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveStreamRescueAgent"
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "session recover"; Flags: runhidden waituntilterminated; RunOnceId: "RecoverDisplays"
Filename: "{app}\VitaMoonlight.Host.exe"; Parameters: "recovery uninstall"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveRecoveryTask"

[Code]
function ViGEmBusMissing: Boolean;
begin
  Result := not RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\ViGEmBus');
end;

function SunshineMissing: Boolean;
var
  ServiceNames: TArrayOfString;
  ServiceImagePath: String;
  SunshinePath: String;
  I: Integer;
begin
  SunshinePath := ExpandConstant('{%SUNSHINE_PATH}');
  if (SunshinePath <> '') and
     (FileExists(SunshinePath) or FileExists(AddBackslash(SunshinePath) + 'sunshine.exe')) then
  begin
    Result := False;
    Exit;
  end;

  Result := not FileExists(ExpandConstant('{autopf}\Sunshine\sunshine.exe')) and
            not FileExists(ExpandConstant('{pf32}\Sunshine\sunshine.exe')) and
            not FileExists(ExpandConstant('{localappdata}\Programs\Sunshine\sunshine.exe'));
  if not Result then
    Exit;

  if RegGetSubkeyNames(HKLM64, 'SYSTEM\CurrentControlSet\Services', ServiceNames) then
  begin
    for I := 0 to GetArrayLength(ServiceNames) - 1 do
    begin
      if RegQueryStringValue(HKLM64,
           'SYSTEM\CurrentControlSet\Services\' + ServiceNames[I],
           'ImagePath', ServiceImagePath) and
         (Pos('sunshine', Lowercase(ServiceImagePath)) > 0) then
      begin
        Result := False;
        Exit;
      end;
    end;
  end;
end;
