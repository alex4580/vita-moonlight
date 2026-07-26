# Minimum public-beta test

This is the shortest community test that covers the release-critical paths.
It is written for someone who has never used Vita Moonlight. No terminal,
developer tools, or existing installation is assumed.

Use the [full end-to-end test](END_TO_END_TEST.md) for deeper feature testing.
Read [Community testing](../docs/COMMUNITY_TESTING.md) before posting a result.

## What to download

Download these two files from the **same GitHub release**:

- `Vita-Moonlight-Host-Setup-win-x64.exe`
- `moonlight.vpk`

Do not combine a VPK from one release with a Windows installer from another.
Record the release name and, if shown, its commit.

You need a homebrew-enabled Vita with VitaShell and a 64-bit Intel or AMD PC
running Windows 10 build 19041 or newer, or Windows 11. Save open work before
testing display changes.

## Choose and record the starting state

Do not assume the PC already has Vita Moonlight Host. Record exactly one:

- **Clean install:** Vita Moonlight Host has never been installed on this
  Windows installation.
- **Upgrade:** an older Vita Moonlight Host release is installed and working.
  Record its version before running the candidate.
- **Same-version reinstall/repair:** this exact candidate is already installed.

The community beta needs results from every starting state. One tester does
not need to erase a personal PC to manufacture a clean result; use another PC
or a reversible test-machine snapshot.

## A. Install, upgrade, or repair

1. Run the downloaded Windows setup file. Accept the recommended components.
   An upgrade must be run directly over the older version; do not uninstall it
   first. For a repair, rerun the exact same installer.
2. If setup requests a restart, restart Windows. Open **Vita Moonlight Host**
   from Start, choose **Restart as Administrator** if offered, and click
   **Set up or repair this PC**.
3. Click **Check readiness**. Sunshine, controller support, virtual display,
   recovery, and Stream rescue must report ready. The physical display must be
   visible while idle.
4. For an upgrade, confirm existing Sunshine pairing credentials and unrelated
   Sunshine applications still exist. For a repair, confirm the second setup
   completes without adding a duplicate virtual display or Sunshine install.
5. Press **Ctrl + Alt + Shift + F11** on the PC keyboard. A short display blink
   is acceptable. The physical display must remain or return, and Sunshine
   must be available again.

Result: **Pass / Fail / Not tested**, plus the clean, upgrade, or repair
starting state.

## B. Install and stream from the Vita

1. Copy `moonlight.vpk` to the Vita using VitaShell USB or FTP. Select the VPK
   in VitaShell and install it. Installing it over an older Vita Moonlight app
   is the upgrade path.
2. Start Vita Moonlight and leave the **Recommended** preset selected.
3. Select the PC. If it is not paired, enter the PIN shown on the Vita in
   Sunshine's web page.
4. Launch **Steam Big Picture** or **Desktop**.
5. Confirm the picture fills the Vita screen, is not 4:3, and is not washed
   out. The expected first-run mode is 960x544, 60 FPS, H.264 SDR, and 8 Mbps.
6. Hold **START**, then press **L + R**. The in-stream menu must open without
   those buttons reaching Windows.
7. From the menu, briefly enable **Frame rate + network**. Move through a game
   or Steam interface and confirm the menu remains responsive.
8. Open the on-screen keyboard from the menu and type into a non-secret field.
9. Press a face button once and confirm it is not stuck or repeated after
   release.
10. Choose **Disconnect stream**. The physical PC display must return without
    a restart or sign-out.

Result: **Pass / Fail**, including any incorrect resolution, color, input, or
display-recovery behavior.

## C. Interrupted-stream recovery

1. Start another stream.
2. Interrupt the connection once by suspending the Vita or temporarily
   disconnecting its network. Do not terminate Windows processes on a PC where
   the stream has removed your only visible control surface.
3. Confirm the physical PC display returns. If it does not, press
   **Ctrl + Alt + Shift + F11** on the PC keyboard and allow about 15 seconds.
4. Start one more stream and disconnect normally.

Long-pressing PS can expose a Vita system Wi-Fi toggle. Some firmware/plugin
combinations do not restore the radio to the running app after it is disabled;
close Vita Moonlight or restart the Vita if that occurs. Report it, but judge
PC display recovery separately.

Result: **Automatic recovery / Hotkey recovery / Failed recovery**.

## D. Laptop and multi-monitor coverage

These checks can be contributed by different testers.

### Laptop

1. Repeat sections A-C on a laptop while it is on battery for at least one
   stream and one interrupted recovery.
2. Confirm the internal panel returns after normal and interrupted disconnects.
3. If an external display or dock is available, repeat once while connected.

### Multiple physical monitors

1. Before streaming, record which physical displays are enabled and how they
   are arranged.
2. Complete one stream, one normal disconnect, and one
   **Ctrl + Alt + Shift + F11** recovery.
3. Confirm every connected physical display is available again and the
   Vita virtual display is not the only active display. Record any changed
   arrangement.

Result: **Pass / Fail / Not available** for each layout.

## E. Reinstall and remove the Vita app

Complete this while the Windows host is still available:

1. In VitaShell, install the exact same `moonlight.vpk` over the current app.
2. Start Vita Moonlight. Saved settings and the paired host should remain.
   Complete one short reconnect.
3. After copying any support log you need, highlight Vita Moonlight in
   LiveArea, press **Triangle**, and choose **Delete**. Confirm the app icon is
   gone.
4. External Moonlight settings and support-log files may remain for a future
   reinstall. This is expected. Review them with VitaShell, but do not delete
   a parent storage directory merely to remove a log.
5. Reinstall the candidate VPK and confirm it starts safely with either the
   preserved settings or a first-run configuration.

Result: **Pass / Fail**.

## F. Uninstall the Windows host while keeping shared components

Sunshine, ViGEmBus, and the MTT virtual-display driver can be used by other
software. This is the normal uninstall path.

1. End the stream and confirm a physical display is visible.
2. Open **Windows Settings > Apps > Installed apps** (Windows 11) or
   **Apps & features** (Windows 10), find **Vita Moonlight Host**, and choose
   **Uninstall**.
3. Leave all shared-dependency removal choices cleared.
4. If Windows requests a restart, restart and run uninstall again.
5. Confirm Vita Moonlight Host and its Start-menu entry are gone, the physical
   display still works, and Sunshine, ViGEmBus, and the virtual-display driver
   remain installed.

Result: **Pass / Fail**.

## G. Uninstall the Windows host while removing shared components

Run this only on a PC or reversible test snapshot where Sunshine, ViGEmBus, and
the virtual-display driver are not needed by anything else.

1. Reinstall the candidate, click **Set up or repair this PC**, and run
   **Check readiness**.
2. Start the uninstaller and explicitly select removal of the MTT
   virtual-display driver, Sunshine, and ViGEmBus.
3. The physical display must be restored before removal begins.
4. If Windows requests a restart or says removal is pending, the host and its
   recovery safeguards must remain. Restart, run uninstall again, and finish.
5. Confirm the host and all three selected shared components are gone and the
   physical display remains usable.

Result: **Pass / Fail / Not tested - shared PC**.

## Report the result

Copy this into a GitHub issue or community report:

```text
Release:
VPK and installer from the same release: Yes / No
Starting state: Clean / Older-version upgrade / Same-version repair
Older version, if upgraded:

PC or laptop:
Windows edition and build:
CPU:
GPU and driver:
Network adapter and wired/Wi-Fi layout:
Physical monitors and HDR state:
Vita model, firmware, and relevant plugins:

Install/upgrade/repair: Pass / Fail
960x544 SDR stream: Pass / Fail
Menu, keyboard, and basic input: Pass / Fail
Vita same-version reinstall and app removal: Pass / Fail
Normal disconnect recovery: Pass / Fail
Interrupted recovery: Automatic / F11 hotkey / Fail
Laptop on battery: Pass / Fail / Not tested
Multiple monitors: Pass / Fail / Not tested
Uninstall, keep shared components: Pass / Fail
Uninstall, remove shared components: Pass / Fail / Not tested

Shortest steps that reproduce any failure:
Expected result:
Actual result:
Could you recover the physical display:
Exact local time and time zone of the problem:
Optional redacted log attached: Yes / No
Screenshots or video:
```

Support logging is optional and off by default. If a failure is reproducible,
choose **Start support log**, capture only that attempt, then choose
**Stop and save support log** using the
[logging and support guide](../docs/LOGGING_AND_SUPPORT.md).

A failure to restore a physical display, a stuck controller input, an
installer that cannot repair or uninstall safely, or mismatched VPK and host
versions is a beta blocker.
