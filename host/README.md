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
   lifecycle for every application, installs the logon safeguard and stream
   rescue agent, and restarts Sunshine.
6. Click **Run health check**. Resolve any item that is not ready before
   pairing the Vita.

Setup keeps Sunshine `2026.516.143833` or newer, upgrades older installations
with the pinned MSI, repairs ViGEmBus when its service is missing or unhealthy,
and preserves Sunshine configuration. Every prerequisite and configuration
command is checked. If Windows requests a restart, setup stops before applying
Sunshine display configuration or changing the active display and prompts for
the restart; afterward, open the control panel as Administrator and click
**Apply recommended setup**.

The x64 installer supports client Windows 10 version 2004 (build 19041) or
newer and Windows 11 on Intel/AMD CPUs. The installer and portable control panel
refuse Windows Server, ARM64, x86, and older Windows before setup actions. See
[COMPATIBILITY.md](COMPATIBILITY.md). The installer and control panel are safe to rerun after an update. A portable
ZIP is also available: extract the whole folder and double-click
`VitaMoonlight.Host.exe`.

## Control panel

### Overview

**Apply recommended setup** is the normal setup and repair action. It verifies
or repairs packaged Sunshine, ViGEmBus, the Microsoft Visual C++ runtime, and
the signed display driver before
writing configuration, installing recovery safeguards, and restarting
Sunshine. If Windows requests a reboot, it stops safely and tells you to repeat
the action after sign-in. The native-mode readiness check keeps all active
physical monitors in the topology, temporarily adds VDD as an extended
display, requests a fresh driver mode enumeration, and restores the exact
original layout after one verification pass. It must not leave the VDD as the
only visible screen. The default Vita client profile is **Recommended**:
960x544, 60 FPS, 8 Mbps, H.264, and SDR. **Run health check** shows the
installed host, ViGEmBus state, virtual display, app coverage, SDR policy,
recovery state, and stream rescue agent.

### Streaming

- **Streaming host** selects Sunshine or Apollo.
- **Virtual display match** is normally blank. Enter part of a device name only
  when the PC has multiple virtual-display drivers.
- **Automatically switch to the Vita display for every Sunshine application**
  must remain enabled if you launch Desktop, Steam Big Picture, or individual
  games. It selects Sunshine's global display manager, including automatic
  client resolution, a driver-safe 60 Hz Windows desktop refresh, and
  restoration when all clients disconnect. Moonlight's stream frame rate is
  independent. Disabling it uses the legacy generated-app prep hook only.
- **Force SDR for Vita virtual-display sessions** prevents HDR capture from
  looking washed out on the Vita.

Choose **Save and apply** to refresh Sunshine's display inventory, write the
settings, and restart Sunshine. Existing application commands are preserved.

### Displays

The signed driver normally appears as **VDD by MTT** and should be inactive
while idle. The upstream driver template begins at 800x600; host setup
provisions the three Vita-safe modes and places native 960x544/60 first before
the driver is activated. If a repaired or upgraded installation still starts
VDD at 800x600, click **Install/update display driver**, then **Apply
recommended setup**.

- **Preview 960x544 for 15 seconds** captures the current display layout,
  switches to the virtual display, then restores the original layout.
- **Disable idle virtual display** disconnects only the managed virtual screen.
- **Emergency display reset** disconnects active streams, restores a physical
  topology, reloads VDD, reapplies the physical-only idle state, and restarts
  Sunshine.
- **List displays** and **Session status** show diagnostic details in the
  activity panel.

The physical monitor can go blank during a preview or stream because Sunshine
must capture only the Vita display. You are not expected to control the PC
locally during that interval. The timed preview restores itself; a normal
stream exit restores the layout; and the logon safeguard handles an interrupted
session.

### Help & recovery

**Install stream rescue agent** creates or repairs the background agent used by
the Vita overlay. **Rescue agent status** shows whether it is installed and
running plus the last recovery result.

If a stream is interrupted and the physical monitor does not return, use
**Recover host display** from the Vita overlay while input is still
connected. If the desktop is visible, open the panel as Administrator and
choose **Displays > Emergency display reset**. Sign out and back in only when
the agent cannot run; the highest-privilege logon task restores saved manual
transactions.

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

Open the Vita overlay by holding **START**, then pressing **L + R** within one
second. Pressing all three together or in another order also works; START first
allows the client to consume the complete chord before it reaches Windows. The
overlay has matching **Stream & virtual display** and **Controller & input**
pages for the core choices in the normal Vita Settings screen. Stream controls
include complete presets, managed resolution, FPS, bitrate, network mode,
frame pacing, packet-loss recovery, aspect scaling, vblank, and host game
optimization. Input controls include controller preset, PS behavior,
touchscreen, gyro, shoulder swap, and the sprint helper.

Resolution and other stream-format changes are saved until the user selects
**Apply resolution + reconnect**. That action switches the VDD, reconnects the
video session, and renegotiates Sunshine's encoder while leaving the current
Windows game and Sunshine app running. The default **Local double-tap** PS
policy never sends a single PS press to Windows, while double-PS remains the
forced LiveArea escape. **Safe PC Guide**, **Immediate PC Guide**, and direct
**System / LiveArea** behavior are available before and during a stream.

The main overlay also selects a separate top-right performance display:
**Off**, **Frame rate**, **Frame rate + network**, or **Advanced**. It uses a
50%-alpha background. **Real-time diagnostics** is a dedicated full screen for
live stream, decoder, network, controller, gyro, and Circle state; those
details are not placed in the normal menu.

Optional Vita file logging is off by default. Enable it from the Vita Settings
screen, or press **Triangle** on Real-time diagnostics only while reproducing
an issue. Disable it afterward to close the append-only file, then use VitaShell
to copy `ux0:data/moonlight/moonlight.log`. The diagnostics screen shows the
actual path if the Vita selected fallback storage.

Three double-confirmed recovery actions are available:

- **Close Windows game** closes the foreground game normally, then force-kills
  only that process tree after 1.5 seconds if necessary. The stream stays open
  so Steam Big Picture can reappear. Windows, Steam, Sunshine, Explorer, and
  the host companion are protected from termination.
- **End Sunshine app** asks Sunshine to close its current app session and then
  disconnects. Use this when no safe game window is in the foreground.
- **Recover host display** disconnects, activates the physical monitor,
  reloads VDD, and restarts Sunshine. Reconnect after roughly ten seconds.

If video goes black but this Vita-rendered overlay remains visible, begin with
**Close Windows game**: the decoder and overlay are still alive, so a game,
exclusive-fullscreen, HDR, Vulkan, or capture transition is more likely than a
dead Vita client. Escalate to **End Sunshine app**, then **Recover host
display** only if the earlier action does not restore video.

For motion-heavy games, begin with the Vita's **Recommended** preset at
960x544/60 and 8 Mbps. Try **High quality** (12 Mbps) on a strong network,
**Reliable** (5 Mbps/30 FPS) when local Wi-Fi is constrained, or **Remote /
VPN** (4 Mbps/30 FPS with remote-network handling) for routed links. Every
preset also restores the complete H.264/SDR, packet, audio, host-optimization,
loss-recovery, pacing, scaling, vblank, and power behavior described in
`VITA_SETTINGS_GUIDE.md`.

The host provisions safe 960x540, 960x544, and 1280x720 desktop modes at 60 Hz
in the virtual-display driver, with native 960x544 as the preferred and
catch-all mode. Vita stream FPS remains independently configurable, which
avoids overloading drivers that cap the total advertised mode count.
In-stream selection asks the background host agent to change only the active
virtual display; it never activates a physical display, reloads VDD, or clears
a recovery record. The Vita then reconnects with the selected dimensions so
Sunshine renegotiates the encoder as well as the Windows desktop. A desktop
mode change without that reconnect would only scale into the already
negotiated video stream.
If Sunshine logs `Failed to set display mode` and continues capturing the
physical monitor, click **Install/update display driver**, then **Apply
recommended setup**. Health check must report **Vita display modes:
compatibility modes provisioned**.
If setup instead reports that safe native-mode verification failed, the
physical layout has already been restored. Do not repeat a restart loop: keep
the complete error shown by the control panel and report its final Windows
response together with **Displays > List displays**.

## Controller, gyro, touch, and keyboard

- **Maximum compatibility** is the first-run profile. It provides the
  conventional Xbox/XInput path, relative-mouse touch, local PS behavior, and
  disables gyro and custom mapping helpers.
- **Steam / DS4 + gyro** provides DS4 motion and touchpad capabilities plus
  Safe PC Guide. Choose **Apply input changes + reconnect** after selecting it
  so Sunshine recreates the controller. Sunshine must see ViGEmBus running
  before it starts.
- Touch modes are **Relative mouse**, **DS4 Touchpad**, **Absolute mouse**, and
  **Tablet**.
- **START + Left** opens the floating keyboard.

If Sunshine's web UI reports that ViGEmBus is missing, run **Run health check**.
If ViGEmBus reports running but Sunshine reports restart required, click
**Restart Sunshine** and refresh the web UI. Reboot or repair ViGEmBus only if
the service itself is not running. **Help & recovery > Repair controller
support** reruns the pinned repair path and verifies the service;
**Update/repair Sunshine** does the equivalent for Sunshine.

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
.\VitaMoonlight.Host.exe session mode --width 960 --height 544 --fps 60
.\VitaMoonlight.Host.exe recovery install
.\VitaMoonlight.Host.exe agent install
.\VitaMoonlight.Host.exe agent status
.\VitaMoonlight.Host.exe emergency recover-display
```

Useful overrides are `--config-dir PATH`, `--driver-bundle PATH`, and
`--display-match TEXT`. Environment overrides include `SUNSHINE_PATH`,
`SUNSHINE_CONFIG_DIR`, `APOLLO_PATH`, `APOLLO_CONFIG_DIR`,
`DISPLAYWIZARD_PATH`, and `VITA_MOONLIGHT_STATE_DIR`.

Configuration creates `apps.json.vita-moonlight.backup` once, removes obsolete
prep commands marked `VitaMoonlight.Host`, and preserves unrelated applications
and commands. The global Sunshine display settings are health-checked by the
control panel. Runtime mode control accepts only 960x540, 960x544, and
1280x720 at 60 Hz. The installed stream agent exposes those modes to the Vita's
existing `Ctrl+Alt+Shift` control channel as F8, F9, and F10 respectively.
F11 display recovery and F12 close-game remain available even if another
program owns one optional mode chord; the health check reports incomplete mode
hotkey readiness.

## Developer build

From the repository root:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

Use [END_TO_END_TEST.md](END_TO_END_TEST.md) for the functional acceptance pass
and [FINAL_RELEASE_CHECKLIST.md](FINAL_RELEASE_CHECKLIST.md) for final sign-off.
Host/platform coverage is in [COMPATIBILITY.md](COMPATIBILITY.md), and Vita-side
tuning is in `VITA_SETTINGS_GUIDE.md` in the installed package.
