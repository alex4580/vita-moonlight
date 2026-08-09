# Minimum public-beta test

This is the shortest community test that covers the release-critical paths.
It is written for someone who has never used Vita Moonlight. No terminal,
developer tools, or existing installation is assumed.

Use the [full end-to-end test](END_TO_END_TEST.md) for deeper feature testing.
Read [Community testing](../docs/COMMUNITY_TESTING.md) before posting a result.

## How much one tester needs to do

A useful minimum result is:

1. choose one real starting state;
2. complete section A through the first support report;
3. complete sections B and C; and
4. report the result, including every item you did not test.

That core pass is enough for one volunteer. The beta as a whole also needs the
Pause/Enable, laptop, multi-monitor, Vita reinstall, and Windows uninstall
variants below, but those can be divided among other testers. Only perform a
shared-component removal or interruption test on a disposable PC or reversible
snapshot.

## What to download

Download these two files from the **same GitHub release**:

- `Vita-Moonlight-Host-Setup-win-x64.exe`
- `moonlight.vpk`

Also download `SHA256SUMS` and read the warning at the top of the release
notes. If the release is labeled unsigned, Windows will show **Unknown
publisher** and may show a Microsoft Defender SmartScreen warning. That is
expected for the exact `v0.14.8-beta.1` release only when
`windows-signing-status.json` says `unsigned-beta-preview`. Do not run a download
whose checksum or stated signing status differs from the release.
After those checks, choose **More info > Run anyway** if SmartScreen blocks
that exact beta-preview installer.

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
- **Paused repair or uninstall variant:** this exact candidate is installed and
  **Pause Vita host features** was chosen before rerunning setup or uninstall.

The community beta needs results from every starting state. One tester does
not need to erase a personal PC to manufacture a clean result; use another PC
or a reversible test-machine snapshot.

## A. Install, upgrade, or repair

1. Sign in to the Administrator Windows account you will use for streaming,
   then run the downloaded setup file and accept the recommended components.
   Do not supply a different Administrator account from a standard-user UAC
   prompt; this beta stops that unsupported setup before changing the PC.
   An upgrade must be run directly over the older version; do not uninstall it
   first. For a repair, rerun the exact same installer.
2. If setup requests a restart, restart Windows. Open **Vita Moonlight Host**
   from Start, choose **Restart as Administrator** if offered, and click
   **Set up or repair this PC**.
3. Click **Check readiness**. Sunshine, controller support, virtual display,
   recovery, and Stream rescue must report ready. The physical display must be
   visible while idle, no display transaction may be pending, and the exact
   managed Vita display must be PnP-disabled rather than merely absent from the
   Windows desktop.
4. For an upgrade, confirm existing Sunshine pairing credentials and unrelated
   Sunshine applications still exist. For a repair, confirm the second setup
   completes without adding a duplicate virtual display or Sunshine install.
   For an older-version upgrade, also confirm obsolete duplicate guide files
   are no longer left beside the installed application and known retired state
   is no longer left under `C:\ProgramData\VitaMoonlight`. An unrelated file
   placed there by the tester must be retained; never delete that folder by
   hand to make this test pass.
5. Press **Ctrl + Alt + Shift + F11** on the PC keyboard. A short display blink
   is acceptable. The physical display must remain or return, and Sunshine
   must be available again.
6. Disconnect the Vita. Open **Diagnostics & support > Save support
   report...** and save `before-pause.json`. In a text editor, confirm
   `backendStatus` is `Enabled`, `recoveryTaskStatus` and
   `rescueAgentTaskStatus` are `Present`, `rescueAgentRunning` is `true`,
   `backendManagedVddEnabledCount` is `0`, and `backendManagedVddActive` is
   `false`. `scheduledTaskAccountReady` must be `true`.
   Also note `hostMode`, `sunshineInstalled`, `sunshineVersion`, and whether
   Sunshine's web page is reachable.

### Assigned Pause/Enable variant

Continue here only if you are covering the Pause/Enable lifecycle for the
community matrix.

7. Choose **Pause Vita host features**. Confirm the physical monitor remains
   visible and the page reports that Vita host features are paused. Restart
   Windows, reopen **Vita Moonlight Host** as Administrator, and confirm the
   paused state remains. The persistent control-panel text must say that the
   rescue agent and **Ctrl + Alt + Shift + F11** are unavailable until Enable;
   do not count that intentional state as a shortcut failure.
8. Save `paused-after-restart.json`. Confirm `backendStatus` and
   `backendDesiredState` are `Disabled`, `backendManagedVddEnabledCount` is
   `0`, `backendManagedVddActive` is `false`, `recoveryTaskStatus` and
   `rescueAgentTaskStatus` are `Missing`, and `rescueAgentRunning` is `false`.
   `hostMode`, `sunshineInstalled`, and `sunshineVersion` must match the
   baseline, and a Sunshine web page that was reachable before Pause must
   remain reachable. Pause is not a network-access control.
9. Choose **Enable Vita host features**, then **Check readiness**, and save
   `enabled-again.json`. The lifecycle, task, rescue-agent, and managed-VDD
   fields must match `before-pause.json`; the VDD must remain PnP-disabled
   while idle. Sunshine, pairing, and settings must remain unchanged. The
   [support-report comparison table](../docs/LOGGING_AND_SUPPORT.md#create-a-windows-host-support-report)
    explains every field and the meaning of `Unknown`.

For the assigned **paused repair** variant, pause and restart before step 1,
then run the same installer over the paused candidate. Setup must leave the
physical desktop visible and the saved state Paused; it must not recreate the
two tasks or enable the VDD. Open the control panel and choose **Enable Vita
host features**. Any setup work deferred while paused must complete once,
readiness must pass, and Sunshine pairing/settings must remain unchanged.

Result: **Pass / Fail / Not tested**, plus the clean, upgrade, or repair
starting state.

## B. Install and stream from the Vita

1. Copy `moonlight.vpk` to the Vita using VitaShell USB or FTP. Select the VPK
   in VitaShell and install it. Installing it over an older Vita Moonlight app
   is the upgrade path.
2. Start Vita Moonlight and leave the **Recommended** preset selected.
3. On a newly added or deliberately forgotten test PC, select the PC and enter
   one incorrect PIN in Sunshine, or cancel the first pairing attempt. After
   the failure, return to the Vita main screen. The PC must remain under
   **Saved computers** and say **Pairing required**; it must not disappear.
4. Select that same saved entry, retry, and enter the correct PIN shown on the
   Vita in Sunshine's web page. The PC must appear as paired under **Saved
   computers** immediately, without closing Vita Moonlight.
5. Fully close and reopen Vita Moonlight. The paired PC must still be saved.
   Select it and launch **Steam Big Picture** or **Desktop**.
6. Confirm the picture fills the Vita screen, is not 4:3, and is not washed
   out. The expected first-run mode is 960x544, 60 FPS, H.264 SDR, and 8 Mbps.
7. Press **START** once and confirm Steam/the game receives one ordinary Start
   press. Then hold **SELECT first** and press **L + R** within one second. The
   in-stream menu must open without SELECT/L/R reaching Windows.
8. From the menu, briefly enable **Frame rate + network**. Move through a game
   or Steam interface and confirm the menu remains responsive.
9. Open the on-screen keyboard from the menu and type into a non-secret field.
10. Press a face button once and confirm it is not stuck or repeated after
   release.
11. Choose **Disconnect stream**. The Vita app must return to its menus without
    crashing. The exact physical PC layout and its pre-stream default audio
    output must return without a restart or sign-out, and the managed VDD must
    be PnP-disabled.
12. Immediately select the same Sunshine application again. This exercises
    Sunshine's resume path rather than a fresh app launch. The dedicated Vita
    display must arm again at 960x544, the same Windows application must remain
    running, and the stream must reconnect normally. Disconnect once more and
    confirm the physical layout returns; a delayed stop from the first stream
    must not tear down the second one.

Result: **Pass / Fail**, including any incorrect resolution, color, input, or
display-recovery behavior.

## C. Interrupted-stream recovery

1. Start another stream.
2. Interrupt the connection once by suspending the Vita or temporarily
   disconnecting its Wi-Fi. Do not terminate Windows processes on a PC where
   the stream has removed your only visible control surface.
3. Confirm the physical PC display returns automatically and the managed VDD
   is PnP-disabled. If it does not, press
   **Ctrl + Alt + Shift + F11** on the PC keyboard and allow about 15 seconds.
4. Start one more stream and disconnect normally.
5. With no stream running and the Vita disconnected or off, place two ordinary
   Windows windows at recognizable sizes and positions and take a reference
   screenshot. Confirm the desktop is responsive. Turn the physical monitor
   off or let it enter power-save **before** putting the PC to sleep, then put
   Windows to sleep with the chassis power button or a known keyboard shortcut.
   Wake the PC and turn the monitor back on. Within about 20 seconds the
   physical display must be active at its normal saved resolution and refresh
   rate, every test window must retain its original size and position, the
   managed VDD must be PnP-disabled, and desktop animation/input must be smooth
   without a restart. Open
   **Diagnostics & support > Open diagnostics folder**, then open
   `stream-rescue.log`. After the timestamp of this test, find a successful
   `power-suspend-display-prepare` line and then either a successful
   `resume-display-check:` decision or a successful `recover-display-host`
   action whose message says it was triggered by resume. A resume-decision
   message reports `restored physical modes` and `mode-repair warnings`;
   record any nonzero warning count/code, moved/resized window, or sustained
   post-wake slowdown.
   `stream-rescue-status.json` contains only the latest rescue result. See the
   [host rescue-record guide](../docs/LOGGING_AND_SUPPORT.md#read-the-sparse-windows-rescue-records)
   for the fields and privacy warning.

Long-pressing PS can expose a Vita system Wi-Fi toggle. Some firmware/plugin
combinations do not restore the radio to the running app after it is disabled;
close Vita Moonlight or restart the Vita if that occurs. Report it, but judge
PC display recovery separately.

For an assigned crash-recovery result, use only a disposable PC or a machine
with an independent physical/remote control path. Start a stream, end the exact
Sunshine process, and confirm the fallback observer restores the physical
layout and PnP-disables the VDD without waiting for a normal Vita stop. Restart
Sunshine, reconnect, and disconnect normally. Do not end the Vita Moonlight
rescue agent for this minimum test.

For the ordinary network-loss check, allow about 60 seconds for the exact
authenticated client lease to expire and bounded recovery to finish. Another
Moonlight client connected to the same Sunshine instance must not extend that
Vita lease or delay restoration.

Result: **Automatic recovery / Hotkey recovery / Failed recovery**.

## D. Optional laptop and multi-monitor coverage

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
3. Confirm every connected physical display has its original arrangement and
   the managed Vita device is PnP-disabled while idle. Record any changed
   arrangement, scaling, resolution, or primary display.

Result: **Pass / Fail / Not available** for each layout.

## E. Assigned Vita reinstall and removal variant

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

## F. Assigned Windows uninstall, keeping shared components

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
   remain installed. The retained managed device must be PnP-disabled, and no
   Vita-owned recovery task, listener, or background process may remain.
6. The public beta needs one enabled-host result and one deliberately paused-host
   result; different testers may contribute them. For the paused case, choose
   **Pause Vita host features**, restart, and then uninstall without enabling
   again. Kept Sunshine must remain unchanged, and the kept VDD must remain
   installed but PnP-disabled.

Result: **Pass / Fail**.

## G. Lab-only uninstall, releasing the display and removing optional shared components

Run this only on a PC or reversible test snapshot where Sunshine and ViGEmBus
are not needed by anything else. Vita Moonlight does not delete the shared MTT
driver package; its option releases only the exact device it manages.

1. Reinstall the candidate, click **Set up or repair this PC**, and run
   **Check readiness**.
2. Start the uninstaller and explicitly select release of the Vita-managed
   display device plus removal of Sunshine and ViGEmBus.
3. The physical display must be restored before removal begins.
4. If Windows requests a restart or says removal is pending, the host and its
   recovery safeguards must remain. Restart, run uninstall again, and finish.
5. Confirm the host, Sunshine, and ViGEmBus are gone and the physical display
   remains usable. If Vita Moonlight created the exact MTT device, that device
   is gone; if it adopted the device, its original enabled state is restored.
   The shared MTT driver package remains staged.

Result: **Pass / Fail / Not tested - shared PC**.

## Report the result

Copy this into a GitHub issue or community report:

```text
Release:
VPK and installer from the same release: Yes / No
Starting state: Clean / Older-version upgrade / Same-version repair / Paused repair / Paused uninstall
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
Immediate same-app resume/reconnect: Pass / Fail
Interrupted recovery: Automatic / F11 hotkey / Fail
Wi-Fi-loss recovery: Automatic / F11 hotkey / Fail
Sunshine-process crash recovery: Pass / Fail / Not tested
Monitor-off-before-sleep window/performance recovery: Pass / Fail
Pause/restart/enable lifecycle: Pass / Fail
Paused repair or paused uninstall: Pass / Fail / Not tested
Laptop on battery: Pass / Fail / Not tested
Multiple monitors: Pass / Fail / Not tested
Uninstall, keep shared components: Pass / Fail
Uninstall, release display and remove optional shared components: Pass / Fail / Not tested

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
