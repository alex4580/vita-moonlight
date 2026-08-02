# Vita Moonlight Windows host guide

Vita Moonlight Host prepares a Windows PC for streaming to a PlayStation Vita.
It installs or configures Sunshine, Xbox/DS4 controller support, a dedicated
Vita-sized virtual display, SDR capture, and display recovery.

Normal setup, repair, and recovery are graphical. You do not need to use a
terminal.

For the shortest first-time instructions, read the
[main quick-start guide](../README.md).

## Supported PCs

The packaged host supports:

- 64-bit Intel or AMD PCs;
- Windows 10 version 2004 (build 19041) or newer; and
- Windows 11.

Windows Server, ARM64, x86, and older Windows releases are not supported by
this package. Hardware video encoding still depends on the GPU and its current
AMD, Intel, or NVIDIA driver. See [Compatibility and limits](COMPATIBILITY.md)
for details.

## First install

1. Download `Vita-Moonlight-Host-Setup-win-x64.exe` and `moonlight.vpk` from
   the same GitHub release.
2. Save open work and keep a keyboard connected to the PC.
3. Run the Windows installer and accept the recommended components.
4. Restart Windows if asked.
5. Open **Vita Moonlight Host** from the Start menu. Choose
   **Restart as Administrator** if that button appears.
6. On **Get started**, click **Set up or repair this PC**.
7. Click **Check readiness**. Follow the recommendation until the page reports
   that the PC is ready.
8. Install and open the VPK on the Vita, pair the PC, and launch
   **Steam Big Picture**, **Desktop**, or a game.

The display can briefly blink while Windows verifies the virtual display.
When idle, the physical display should be active and the Vita virtual display
should not be the only active screen.

If setup requests a restart, it stops before changing the Sunshine stream
display. Restart Windows, reopen the control panel as Administrator, and click
**Set up or repair this PC** again.

## Upgrade an older version

Do not uninstall the old version first:

1. End the Vita stream.
2. Download the new installer and VPK from the same release.
3. Run the new Windows installer over the existing installation.
4. Restart if asked, then run **Get started > Set up or repair this PC**.
5. Run **Check readiness**.
6. Install the new VPK over the old Vita app.

The upgrade is designed to preserve Sunshine credentials, pairing, unrelated
Sunshine applications, and compatible user configuration. It restores a
physical display before changing installed host components.

If you previously chose **Pause Vita host features**, setup updates the
installed files but preserves that paused state. It does not re-enable the
virtual display or recovery tasks. Shared Sunshine/Apollo is never stopped by
Pause. Safe controller/runtime repairs run immediately; selected streaming-host
and virtual-display work is saved in protected state and completes only when
you later choose **Enable Vita host features**. If completion fails or requires
a restart, Vita host features return to Paused and the saved work can be
retried by choosing Enable again.

## Reinstall or repair the current version

Rerunning the same installer is safe and is the normal repair path:

1. End the stream and run the same setup file again.
2. Open the control panel as Administrator.
3. Click **Set up or repair this PC**.
4. Click **Check readiness**.

This repairs or verifies the Microsoft Visual C++ runtime, Sunshine,
ViGEmBus/controller support, the signed MTT virtual-display driver, Vita display
modes, and both recovery safeguards. It should not create a second Sunshine
installation or duplicate virtual display.

## Control panel

### Get started

Use this page first.

- **Set up or repair this PC** performs the complete guided setup in the
  required order. It is appropriate after a first install, upgrade, or failed
  prerequisite.
- **Check readiness** inspects the PC without changing it. It summarizes the
  result in plain language and recommends the next action.

The recommended Vita starting profile is 960x544, 60 FPS, 8 Mbps, H.264, and
SDR.

### Pause the host for extended idle periods

Use **Pause Vita host features** when this PC will not use Vita-owned display
switching and recovery for a
while. The action:

- restores and verifies a physical-only Windows display layout;
- stops and removes the two Vita Moonlight background safeguards;
- persistently disables the managed VDD device without uninstalling it; and
- leaves shared Sunshine/Apollo installed and reachable. A client pinned to
  the disabled Vita VDD may need Vita host features enabled again or a physical
  output selected in its streaming-host configuration.

The pause survives sign-out, restart, sleep, and an in-place upgrade. It keeps
the app, pairing, settings, drivers, and shared programs installed. Choose
**Enable Vita host features** to restore only the Vita safeguards and exact
managed VDD instances that were active before the pause. Both actions are
safe to run again and require Administrator approval.

Do not use Windows Device Manager or Task Scheduler to reproduce this state by
hand. The control panel journals the exact Vita task and managed-device state.
Sunshine and Apollo remain reachable while paused, so disconnect the Vita first
and do not treat this control as a network-access block.

### Streaming

Most users should keep the defaults:

- **Streaming service:** Sunshine, unless the PC is intentionally using
  Apollo.
- **Preferred virtual display:** blank for automatic selection. Enter a name
  only if the health check finds more than one virtual display.
- **Use the Vita display with every streamed application:** enabled. This
  applies display switching to Steam, Desktop, and custom Sunshine apps.
- **Force SDR for Vita virtual-display sessions:** enabled. The Vita does not
  display HDR correctly.

Use **Save and restart streaming** to apply changes immediately. It ends any
active stream. **Save for next session** waits until the next Sunshine
session. **Restart Sunshine now** restarts only the streaming service.

### Display & recovery

The managed virtual display normally appears as **VDD by MTT** and remains
inactive while idle.

- **Restore physical display now** ends the active stream, restores physical
  displays, reloads VDD, leaves the Vita display inactive, and restarts
  Sunshine.
- **Turn off idle Vita display** disables only an idle managed VDD while
  keeping a physical display active.
- **Test Vita display for 15 seconds** temporarily activates 960x544 and then
  restores the original physical layout automatically.
- **Show detected displays** lists physical and virtual displays.
- **Repair Vita display driver** reinstalls or repairs the packaged signed
  driver and Vita-compatible modes.
- **Restart Vita display driver** restarts the installed VDD device.
- **Show current session state** reports whether a display change is pending.

During a stream or timed display test, a physical monitor may go blank because
Sunshine captures only the Vita virtual display. A normal disconnect restores
the physical layout.

If it does not return, press **Ctrl + Alt + Shift + F11** on the PC keyboard.
While Vita host features are enabled, this shortcut works without opening the
control panel. Allow roughly 15 seconds for the physical displays, driver, and
Sunshine to recover. A complete **Pause Vita host features** removes the rescue
agent and therefore unregisters F11 until **Enable Vita host features** is run.

The rescue agent also prepares a physical-only layout when Windows announces
sleep and performs a bounded topology check after resume. A stream that
crosses system sleep is treated as interrupted: the host prioritizes the
physical desktop and leaves the Vita VDD inactive instead of preserving a
stale Vita-sized layout.

### Diagnostics & support

Technical output and individual repairs are kept away from everyday setup:

- **Run full health check** shows detailed component status.
- **Save support report...** creates a point-in-time JSON report only when
  requested.
- **Copy technical details** copies the latest health/support output.
- **Open diagnostics folder** opens the host's protected sparse rescue records.
  See [Read the sparse Windows rescue records](../docs/LOGGING_AND_SUPPORT.md#read-the-sparse-windows-rescue-records)
  before interpreting or sharing them.
- **Repair sign-in display recovery** repairs the interrupted-session
  safeguard.
- **Repair stream rescue shortcuts** repairs the background Vita and keyboard
  recovery controls.
- **Repair controller support** repairs ViGEmBus.
- **Repair Sunshine** repairs the packaged compatible Sunshine installation.
- **Check rescue shortcuts** reports the rescue agent and hotkey state.

Start with **Set up or repair this PC** instead of repairing individual
components. Use an individual repair only when readiness or support output
names that component.

The host does not continuously write a support log. Review a saved support
report before sharing it because PC, display, network, or recovery details may
be identifying. See [Logging and support](../docs/LOGGING_AND_SUPPORT.md) for
the separate, optional Vita capture.

## Stream controls on the Vita

- Hold **START**, then press **L + R** within one second to open the in-stream
  menu without sending the chord to Windows.
- Use **Open on-screen keyboard** in that menu, or press
  **START + D-pad Left**.
- Use **Disconnect stream** for a normal exit.
- Double-press **PS** for a forced return to LiveArea.

The first-run **Recommended** preset uses the Vita's native 960x544 display at
60 FPS and 8 Mbps. The other display modes are 960x540 for strict 16:9 game
compatibility and 1280x720 for games with a 720p minimum.

Changing resolution, FPS, bitrate, or network mode during a session requires
**Apply resolution + reconnect**. The video connection renegotiates while the
Windows game stays open. Changing Xbox/DS4 controller capabilities requires
**Apply input changes + reconnect**.

See the [Vita settings guide](../docs/VITA_SETTINGS_GUIDE.md) for presets,
controller profiles, gyro, graphical mapping, front-touch zones, PS behavior,
and performance tradeoffs.

## Recover a black game or stuck display

If the Vita menu still draws over a black game:

1. Choose **Close Windows game** and confirm it twice. The host protects
   Sunshine, Steam, Explorer, and critical Windows processes.
2. If Steam does not return, choose **End Sunshine app**.
3. If the captured display is still unusable, choose
   **Recover host display**.

If the Vita menu cannot be used:

1. If Vita host features are enabled, press **Ctrl + Alt + Shift + F11** on the
   PC keyboard. A complete Pause intentionally unregisters this shortcut.
2. If the Windows desktop is visible, open
   **Display & recovery > Restore physical display now**.
3. If recovery cannot run, sign out and back in. The sign-in safeguard checks
   for an interrupted display session.

Do not deliberately end host or display processes on a personal
single-monitor PC. Use the safe community test instructions in
[Minimum public-beta test](BETA_SMOKE_TEST.md) or
[Full end-to-end test](END_TO_END_TEST.md).

## Portable package

The portable ZIP is diagnostics-only. Extract the entire folder and run
`VitaMoonlight.Host.exe` to inspect readiness or create a support report.
Portable mode cannot install components, change displays, or register
high-privilege recovery tasks.

Use the Windows installer for setup, repair, driver changes, and safeguards.

## Uninstall

1. End the stream and confirm a physical display is visible.
2. Open **Windows Settings > Apps > Installed apps** (Windows 11) or
   **Apps & features** (Windows 10).
3. Find **Vita Moonlight Host** and choose **Uninstall**.
4. Leave all shared-component choices cleared for a normal uninstall.

The uninstaller works whether Vita host features are enabled, paused, or
partially recovered. It restores and verifies a physical display before removing the
host or its recovery safeguards. By default it keeps:

- Sunshine;
- ViGEmBus controller emulation; and
- the MTT virtual-display driver.

These components may be used by other software. Select their removal only when
you are certain they are no longer needed. Removing the VDD also removes its
managed Vita display configuration.

When shared components are kept, uninstall leaves Sunshine unchanged and the
VDD device usable but inactive. It
then removes both exact scheduled tasks, the running rescue agent, current
host state, and exact legacy Vita Moonlight state. Unknown files in a legacy
folder are retained rather than deleted recursively.

If Windows requests a restart or a selected dependency cannot yet be removed,
the host and recovery safeguards remain. Restart Windows and run uninstall
again.

If security software removes `VitaMoonlight.Host.exe` during an interrupted
uninstall, restore that file from quarantine or copy the same-version file from
the portable release into the install folder, then rerun uninstall. The
uninstaller deliberately keeps its protected recovery state instead of
guessing that an incomplete transaction is safe.

**Keep the stream-rescue log for troubleshooting** preserves one timestamped
rescue log but still removes settings and recovery records.

## Community testing and development

Testing and diagnostic instructions are kept separate from normal use:

- [Community testing and report template](../docs/COMMUNITY_TESTING.md)
- [Minimum public-beta test](BETA_SMOKE_TEST.md)
- [Full end-to-end test](END_TO_END_TEST.md)
- [Logging and support](../docs/LOGGING_AND_SUPPORT.md)
- [Final release checklist](FINAL_RELEASE_CHECKLIST.md)

To build or fork the project, use
[Building and forking](../docs/BUILDING.md) and
[Releasing a fork](../docs/RELEASING.md).
