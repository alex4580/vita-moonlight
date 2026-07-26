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
refuse Windows Server, ARM64, x86, and older Windows. See
[COMPATIBILITY.md](COMPATIBILITY.md). The installed control panel is safe to
rerun after an update.

The portable ZIP is deliberately **diagnostics-only**. Extract the whole folder
and double-click `VitaMoonlight.Host.exe` to run the health check or use
`self-test`; administrator setup and display/session mutation controls remain
disabled. This prevents a user-writable extracted executable from ever being
registered as a highest-privilege recovery task. Use the signed installer for
setup, repair, driver changes, and recovery safeguards.

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

Setup does not assume a clean machine, but it never trusts or repairs an
unidentified `C:\VirtualDisplayDriver` directory in place. On the first upgrade
to this security model, the existing fixed-path entry is renamed without
following it or reading its contents. Setup then atomically installs a new
directory born with an Administrator/SYSTEM-only write policy and records its
Windows volume/file identity in protected machine state. An empty detached
entry is removed through the handle already held for it; a non-empty entry is
left at the randomized quarantine path printed by setup for manual review.
Later driver configuration and reload actions refuse to run if the fixed path
no longer has the recorded identity. Run **Install/update display driver** as
Administrator to recreate it; if Windows reports a sharing violation, restart
Windows and repeat the repair.

After that one-time identity migration, an existing trusted VDD is repaired
without discarding its configuration. Setup stages the pinned package first,
then reapplies the Vita modes to the live configuration so a driver upgrade
cannot replace them with its stock XML. Existing resolutions and driver
options in the trusted configuration are preserved. Duplicate effective
refresh modes are removed when the same rate is both local and global because
the upstream driver already replicates global rates onto every resolution.
After an in-place device restart, Windows may temporarily omit VDD from its
display inventory even though the repair succeeded. Setup waits up to 30
seconds for that existing target to re-enumerate before it changes any display
topology; physical monitors remain active during the wait.
The temporary verification mode is not written to the Windows display
profile; Sunshine applies the live mode when a stream starts.

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

The UI-independent recovery shortcut is **Ctrl + Alt + Shift + F11**. The
background rescue agent stops Sunshine, restores and verifies a physical-only
topology, reloads the MTT virtual display driver, reapplies the physical-only
idle topology, and restarts Sunshine. The equivalent elevated terminal command
is `VitaMoonlight.Host.exe emergency reset-display-driver`.

The recovery file is stored beneath
`%ProgramFiles%\Vita Moonlight Host\state` before any display mutation. Do not
deliberately terminate the host during a display test unless the PC has an
independent remote-control path.

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
live stream, decoder, network, controller, and gyro state; those details are
not placed in the normal menu.

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
response, current mode, and advertised modes together with **Displays > List
displays**. A target that appears in **List displays** immediately after an
older installer reported “No virtual display was found” indicates the
re-enumeration race fixed in 0.14.4; upgrade the host package in place.

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
- **START + Left** opens the floating keyboard. It is also available as
  **Open on-screen keyboard** on the in-stream menu.

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
.\VitaMoonlight.Host.exe emergency reset-display-driver
```

Advanced installed setups may use `--config-dir PATH` (only a system-wide
Program Files directory) and `--display-match TEXT`. Driver and
prerequisite operations always use the protected, pinned copies installed
under `tools`. Path environment variables are test-only and are cleared for
every operational command.

Configuration creates `apps.json.vita-moonlight.backup` once, removes only
exact legacy hooks generated by this companion, and preserves unrelated
applications and commands. Before changing Sunshine, it records every original
and applied value plus exact hook/application fingerprints in the
administrator-only `HKLM\SOFTWARE\VitaMoonlight\Host` ownership journal. The
global Sunshine display settings are health-checked by the control panel.
Runtime mode control accepts only 960x540, 960x544, and
1280x720 at 60 Hz. The installed stream agent exposes those modes to the Vita's
existing `Ctrl+Alt+Shift` control channel as F8, F9, and F10 respectively.
F11 display recovery and F12 close-game remain available even if another
program owns one optional mode chord; the health check reports incomplete mode
hotkey readiness.

## Uninstall and upgrades

Install a newer package over the existing version; the fixed application ID
performs an in-place upgrade. Setup first restores a physical-only layout and
discards any legacy recovery blob without applying it, repairs the selected
components, preserves Sunshine credentials and unrelated application commands,
and reinstalls both recovery tasks against the new companion path.

The uninstaller is deliberately conservative:

1. it stops Sunshine only when necessary, activates a physical-only topology,
   verifies that a physical display is active and VDD is inactive, and only
   then clears any untrusted stale recovery record without applying it;
2. from the protected ownership journal, it removes exact Vita-owned hooks and
   restores each original Sunshine value only when the current value still
   equals the value Vita Moonlight applied; later user edits are preserved;
3. it optionally removes shared dependencies only when you explicitly select
   **MTT virtual display driver**, **Sunshine**, or **ViGEmBus**;
4. it removes the stream-rescue agent and logon-recovery task only after all
   requested dependency operations succeed; and
5. after successful removal, it deletes host settings, recovery records, and
   transient diagnostics from the protected installed `state` directory.

Sunshine, ViGEmBus, and VDD are kept by default because other software may use
them. If VDD is selected, its device, driver-store package, native-mode
verification record, and managed `C:\VirtualDisplayDriver\vdd_settings.xml`
are removed. The fixed directory itself is deleted only when its recorded
Windows file identity still matches and it is empty; an unknown replacement is
never traversed or deleted. Windows may request a restart. If physical-display
verification or a requested dependency removal fails, uninstall stops while
the companion and both recovery safeguards are still available. If several
explicitly selected shared dependencies are requested, an earlier one may
already have been removed before a later one reports an error. A
restart-required removal also stops uninstall: restart Windows and run
uninstall again so the pending device/product removal can be verified before
the safeguards disappear.

The optional **Keep the stream-rescue log** checkbox preserves only a
timestamped `%ProgramData%\VitaMoonlight-stream-rescue-*.log`; it does not
retain settings. Silent automation is noninteractive and keeps every shared
dependency unless its explicit switch is supplied:

```powershell
& "$env:ProgramFiles\Vita Moonlight Host\unins000.exe" /VERYSILENT /NORESTART

# Destructive dependency removal must be requested component by component:
& "$env:ProgramFiles\Vita Moonlight Host\unins000.exe" /VERYSILENT `
  /REMOVEVDD /REMOVESUNSHINE /REMOVEVIGEMBUS /KEEPDIAGNOSTICS
```

Setup upgrades the old broadly writable state directory to an
administrator-owned machine-state and diagnostics tree that standard users can
read but not replace. Installed operational commands ignore test-only path environment
overrides (`VITA_MOONLIGHT_STATE_DIR`, Sunshine/Apollo paths and config
directories, and `DISPLAYWIZARD_PATH`) and refuse redirected machine-state,
driver, or host-config paths.
On the first upgrade from a release that predates the ownership journal, the
then-current Sunshine values become the safe baseline. The uninstaller cannot
reconstruct values overwritten by an older release, so it preserves that
baseline rather than guessing and deleting potentially user-owned settings.

## Developer build

From the repository root:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

Use [END_TO_END_TEST.md](END_TO_END_TEST.md) for the functional acceptance pass
and [FINAL_RELEASE_CHECKLIST.md](FINAL_RELEASE_CHECKLIST.md) for final sign-off.
Host/platform coverage is in [COMPATIBILITY.md](COMPATIBILITY.md), and Vita-side
tuning is in `VITA_SETTINGS_GUIDE.md` in the installed package. The smallest
public-beta hardware gate is [BETA_SMOKE_TEST.md](BETA_SMOKE_TEST.md).
`BUILDING.md` explains how to build and fork both products, and `RELEASING.md`
documents the signing and publication pipeline in the installed package.
