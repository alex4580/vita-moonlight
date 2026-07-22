# Vita Moonlight Windows host

This Windows companion prepares Sunshine or Apollo for the Vita's native
960x544 display, controller, touch, and motion features. The installer bundles
the host companion, the signed virtual-display driver, ViGEmBus, and Sunshine.

## Open the control panel

You do not need a terminal for normal use.

- Installed package: open **Start > Vita Moonlight Host > Vita Moonlight Host
  Control Panel**.
- Installed folder: double-click
  `C:\Program Files\Vita Moonlight Host\VitaMoonlight.Host.exe`.
- Portable ZIP: extract the entire ZIP, open the extracted folder, and
  double-click `VitaMoonlight.Host.exe`.

Double-clicking the executable opens a persistent control panel. It should not
flash and disappear. The panel automatically runs diagnostics and provides
buttons for setup, display checks, a 960x544 test, and recovery. Click **Restart
as Administrator** before using any setup, test, or recovery button.

## First-time Sunshine setup

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator.
2. Keep **Sunshine**, **Install the pinned, officially signed virtual display
   driver**, and **ViGEmBus** selected. Choose **Apollo** instead only if you
   already use Apollo.
3. Allow setup to finish. The control panel opens automatically.
4. In the control panel, click **Restart as Administrator** if that button is
   visible, then click **Run diagnostics**. Sunshine, ViGEmBus, the driver
   bundle, and the virtual display should report `OK`.
5. Click **Configure Sunshine**, then restart Sunshine. For Apollo, click
   **Configure Apollo** and restart Apollo.
6. Install the VPK from the same release on the Vita, pair with the PC, and
   launch **Vita Moonlight**.

The installer normally performs the driver and host-configuration steps for
the selected host. The control-panel buttons make them easy to repeat after an
upgrade or repair.

## About the extra 800x600 display

The 800x600 screen named **VDD by MTT** is the signed virtual display used for
streaming. Seeing it immediately after installation means the driver loaded,
but it does not need to remain active while no stream is running.

Open the control panel as Administrator and click **Disable idle virtual
display**. This keeps the physical monitor active and disables only the Vita
virtual monitor; it does not uninstall the driver. The streaming preparation
hook activates the virtual monitor at 960x544 when a Vita session starts and
restores the prior physical-display layout when it ends.

If you are using an older package without that button, use **Settings > System
> Display**, select the 800x600 display, choose **Disconnect this display**
under **Multiple displays**, and apply. Do not disconnect your physical
2560x1440 monitor.

## Guided 960x544 display test

Use the control panel as Administrator:

1. Click **List displays** and identify the physical display and **VDD by MTT**.
2. Click **Run 15-second 960x544 test** and approve the confirmation. Windows
   temporarily activates only the Vita virtual display at 960x544 and 60 Hz.
   Your physical screen may go blank during the countdown.
3. After 15 seconds, your original physical layout should return automatically.
4. Click **Run diagnostics** and confirm **Recovery task** says `OK installed`.
   If it does not, click **Install recovery safeguard**.

The recovery record is written to `%ProgramData%\VitaMoonlight` before a
display change. Setup also registers a highest-privilege logon recovery task so
an interrupted session can be restored after signing out and back in.

Do not deliberately terminate the host from Task Manager while the physical
display is disabled. That test requires a second, independent way to control
the PC and is not part of the normal acceptance run.

## Controller, touch, keyboard, and gyro

- **Xbox controller mode** advertises an XInput-compatible controller. Use it
  for Steam Big Picture and games that do not need gyro.
- **PS4/automatic mode** advertises a DS4 with touchpad and motion capabilities
  when the corresponding Vita settings are enabled.
- Vita touch can operate as **DS4 Touchpad**, **Mouse Absolute**, or **Tablet
  (Sunshine)**.
- Press **START + LEFT** on the Vita to open the elevated floating keyboard.

The Vita client converts acceleration to metres per second squared and angular
velocity to degrees per second, matching the Moonlight protocol. Sensor state
is reset between streams, and reports are limited to the rate requested by the
host.

The default streaming profile is 960x544 at 60 FPS and 5000 Kbps. The signed
driver package is hash-checked at runtime, and setup does not add a private
certificate to the Windows trust store.

## Optional command line

The same actions remain available for scripting. Here is the exact installed
path and PowerShell syntax:

1. Open **Start**, search for **Windows Terminal**, and choose **Run as
   administrator**.
2. Enter:

```powershell
Set-Location "C:\Program Files\Vita Moonlight Host"
.\VitaMoonlight.Host.exe doctor
.\VitaMoonlight.Host.exe display list
.\VitaMoonlight.Host.exe session status
```

Other commands are:

```powershell
.\VitaMoonlight.Host.exe profile
.\VitaMoonlight.Host.exe configure --host sunshine
.\VitaMoonlight.Host.exe configure --host apollo
.\VitaMoonlight.Host.exe driver install
.\VitaMoonlight.Host.exe driver reload
.\VitaMoonlight.Host.exe display disable-virtual
.\VitaMoonlight.Host.exe session test --width 960 --height 544 --fps 60 --seconds 15
.\VitaMoonlight.Host.exe session start --width 960 --height 544 --fps 60
.\VitaMoonlight.Host.exe session stop
.\VitaMoonlight.Host.exe session recover
.\VitaMoonlight.Host.exe recovery install
.\VitaMoonlight.Host.exe recovery status
```

For the portable package, open Terminal as Administrator, use `Set-Location`
with the extracted folder instead, and then use the same `.\VitaMoonlight.Host.exe`
commands.

Useful overrides are `--config-dir PATH`, `--driver-bundle PATH`, and
`--display-match TEXT`. `--display-match` selects a particular virtual monitor
when a PC has more than one. `SUNSHINE_PATH`, `APOLLO_PATH`,
`DISPLAYWIZARD_PATH`, and `VITA_MOONLIGHT_STATE_DIR` are also supported.

## Configuration rollback

`configure` creates `apps.json.vita-moonlight.backup` before editing the host
application list. It replaces only the prep command carrying the
`VitaMoonlight.Host` marker and preserves unrelated applications.

## Developer build

From the repository root:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

`installer/prepare-displaywizard.ps1` downloads pinned DisplayWizard source and
the signed Virtual Display Driver release, then verifies both SHA-256 values.
The package retains the reviewed helper source and signed device-node helper;
it deliberately omits the legacy DisplayWizard GUI that offers an obsolete
driver download. See `THIRD_PARTY_NOTICES.md` for source and licenses.

CI publishes the installer and portable ZIP. Optional repository secrets
`WINDOWS_CERTIFICATE_BASE64` and `WINDOWS_CERTIFICATE_PASSWORD` Authenticode-sign
and timestamp the companion and installer; builds without those secrets can
trigger Windows SmartScreen.

Use `END_TO_END_TEST.md` for the GUI-first clean-machine and physical-Vita
release test.
