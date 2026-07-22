# Vita Moonlight Windows host

The Windows companion makes Sunshine behave like a dedicated Vita streaming
host. Normal operation is GUI-driven; opening `VitaMoonlight.Host.exe` without
arguments launches the control panel and keeps it open.

## First-time setup

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator.
2. Keep **Sunshine**, **ViGEmBus**, and **Install the pinned, officially signed
   virtual display driver** selected. Select Apollo only if you already use it.
3. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel**.
4. Choose **Restart as Administrator** if shown.
5. On **Overview**, click **Apply recommended setup**. This configures the
   controller path, enables Sunshine's native disconnect-aware display
   lifecycle for every application, and restarts Sunshine.
6. Click **Run health check**. Resolve any item that is not ready before
   pairing the Vita.

The installer and control panel are safe to rerun after an update. A portable
ZIP is also available: extract the whole folder and double-click
`VitaMoonlight.Host.exe`.

## Control panel

### Overview

**Apply recommended setup** is the normal setup and repair action. The default
profile is 960x544, 60 FPS, 8 Mbps, H.264, and SDR. **Run health check** shows
the installed host, ViGEmBus state, virtual display, app coverage, SDR policy,
and recovery state.

### Streaming

- **Streaming host** selects Sunshine or Apollo.
- **Virtual display match** is normally blank. Enter part of a device name only
  when the PC has multiple virtual-display drivers.
- **Automatically switch to the Vita display for every Sunshine application**
  must remain enabled if you launch Desktop, Steam Big Picture, or individual
  games. It selects Sunshine's global display manager, including automatic
  client resolution/refresh-rate selection and restoration when all clients
  disconnect. Disabling it uses the legacy generated-app prep hook only.
- **Force SDR for Vita virtual-display sessions** prevents HDR capture from
  looking washed out on the Vita.

Choose **Save and apply** to refresh Sunshine's display inventory, write the
settings, and restart Sunshine. Existing application commands are preserved.

### Displays

The signed driver normally appears as **VDD by MTT**. It may be 800x600 after
installation and should be inactive while idle.

- **Preview 960x544 for 15 seconds** captures the current display layout,
  switches to the virtual display, then restores the original layout.
- **Disable idle virtual display** disconnects only the managed virtual screen.
- **Restore physical display** restores the most recently saved layout.
- **List displays** and **Session status** show diagnostic details in the
  activity panel.

The physical monitor can go blank during a preview or stream because Sunshine
must capture only the Vita display. You are not expected to control the PC
locally during that interval. The timed preview restores itself; a normal
stream exit restores the layout; and the logon safeguard handles an interrupted
session.

### Help & recovery

If a stream is interrupted and the physical monitor does not return, sign out
and back in. The highest-privilege logon task restores the saved layout. If the
desktop is already visible, open the panel as Administrator and choose
**Displays > Restore physical display**.

The recovery file is stored under `%ProgramData%\VitaMoonlight` before any
display mutation. Do not deliberately terminate the host during a display test
unless the PC has an independent remote-control path.

## Streaming from the Vita

After pairing, you may launch any Sunshine application, including its built-in
**Steam Big Picture** entry. Sunshine's native display lifecycle:

1. selects the stable VDD device ID discovered in Sunshine's display inventory;
2. activates only that display at the resolution and refresh rate requested by
   the Vita;
3. applies SDR because the Vita does not request HDR;
4. lets Sunshine capture the virtual target; and
5. restores the physical layout 500 ms after all clients disconnect, even if
   Steam remains open for a later session.

Open the Vita overlay with **START + L + R**. The overlay can resume or
disconnect and change resolution, video quality, frame rate, controller
profile, touch mode, and the FPS counter. Stream-negotiation changes are saved
for the next connection. **Double-press PS** remains the forced system escape:
it temporarily releases PS capture and returns to the Vita LiveArea.

For motion-heavy games, begin at 960x544/60 and 8 Mbps. Try 12 Mbps on a strong
network or 5 Mbps/30 FPS when Wi-Fi is constrained. The Vita client migrates
old native-resolution 5 Mbps configurations to the new 8 Mbps default once.

## Controller, gyro, touch, and keyboard

- **Xbox** mode provides the conventional XInput-compatible path used by most
  games and Steam Big Picture.
- **PS4 + gyro** provides DS4 motion and touchpad capabilities. Sunshine must
  see ViGEmBus running before it starts.
- Touch modes are **Off**, **DS4 Touchpad**, **Absolute mouse**, and **Tablet**.
- **START + Left** opens the floating keyboard.

If Sunshine's web UI reports that ViGEmBus is missing, run **Run health check**.
If ViGEmBus reports running but Sunshine reports restart required, click
**Restart Sunshine** and refresh the web UI. Reboot or repair ViGEmBus only if
the service itself is not running.

## Optional command line

These commands are for scripting and troubleshooting only. Open Windows
Terminal as Administrator and run:

```powershell
Set-Location "C:\Program Files\Vita Moonlight Host"
.\VitaMoonlight.Host.exe doctor
.\VitaMoonlight.Host.exe display list
.\VitaMoonlight.Host.exe session status
.\VitaMoonlight.Host.exe session recover
```

Other supported commands include:

```powershell
.\VitaMoonlight.Host.exe profile
.\VitaMoonlight.Host.exe configure --host sunshine --all-apps true --force-sdr true
.\VitaMoonlight.Host.exe host restart --host sunshine
.\VitaMoonlight.Host.exe display disable-virtual
.\VitaMoonlight.Host.exe driver install
.\VitaMoonlight.Host.exe driver reload
.\VitaMoonlight.Host.exe session test --width 960 --height 544 --fps 60 --seconds 15
.\VitaMoonlight.Host.exe recovery install
```

Useful overrides are `--config-dir PATH`, `--driver-bundle PATH`, and
`--display-match TEXT`. Environment overrides include `SUNSHINE_PATH`,
`APOLLO_PATH`, `DISPLAYWIZARD_PATH`, and `VITA_MOONLIGHT_STATE_DIR`.

Configuration creates `apps.json.vita-moonlight.backup` once, removes obsolete
prep commands marked `VitaMoonlight.Host`, and preserves unrelated applications
and commands. The global Sunshine display settings are health-checked by the
control panel.

## Developer build

From the repository root:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

Use [END_TO_END_TEST.md](END_TO_END_TEST.md) for the release acceptance pass.
