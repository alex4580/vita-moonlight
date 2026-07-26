# Full community end-to-end test

This is the complete public-beta acceptance test for Vita Moonlight. It is
written for first-time users and uses graphical controls wherever possible.
No existing Vita Moonlight installation, terminal, SDK, or source checkout is
assumed.

Individual testers may submit a partial result. The release as a whole needs
coverage of every installation state and hardware layout below. For a short
pass, use the [minimum public-beta test](BETA_SMOKE_TEST.md).

## Safety and requirements

Use:

- a homebrew-enabled PlayStation Vita or PlayStation TV with VitaShell;
- a 64-bit Intel or AMD PC running Windows 10 build 19041 or newer, or
  Windows 11;
- a PC keyboard for the emergency display shortcut; and
- `moonlight.vpk` and `Vita-Moonlight-Host-Setup-win-x64.exe` downloaded from
  the **same GitHub release**.

Save open work before testing. The physical monitor can go blank while
Sunshine captures the Vita virtual display. The normal disconnect path,
recovery agent, and sign-in safeguard should restore it.

Do not deliberately terminate Windows display or host processes on a personal
single-monitor PC. Crash testing belongs on a disposable machine with an
independent way to recover it. Press **Ctrl + Alt + Shift + F11** on the PC
keyboard if the physical display does not return.

## Test matrix

Record every configuration you test. A complete community release evaluation
needs all of these paths; they may be split among testers:

| Path | Required starting state |
|---|---|
| Clean install | Vita Moonlight Host has never been installed on this Windows installation. |
| Older-version upgrade | A named older Vita Moonlight Host release is installed and paired before the candidate is run over it. |
| Same-version reinstall/repair | The exact candidate is installed, then the same installer and recommended setup are run again. |
| Vita package lifecycle | Candidate VPK is installed over itself, removed from LiveArea, and installed again. |
| Keep-dependencies uninstall | Host is uninstalled while Sunshine, ViGEmBus, and VDD are retained. |
| Remove-dependencies uninstall | Disposable PC/snapshot where all three shared components can safely be removed. |
| Laptop | Internal panel, including a stream and recovery while on battery. |
| Multiple monitors | Two or more connected physical displays. |

For a genuine upgrade result, do not uninstall the older release first. If you
do not already have an older build, install a prior published release on a
test PC, pair it, complete one stream, and then install the candidate over it.

## 1. Record the starting environment

Before installing, record:

- release name and commit, if shown;
- clean install, older-version upgrade, or same-version repair;
- older installed version, if applicable;
- Windows edition, version, and OS build;
- desktop or laptop, CPU, GPU, and GPU driver version;
- Sunshine version if it was installed independently;
- Ethernet/Wi-Fi arrangement and Wi-Fi band;
- physical monitor count, arrangement, resolutions, and HDR state; and
- Vita/PSTV model, firmware, relevant plugins, and storage location.

Take a screenshot of **Windows Settings > System > Display**. This makes an
unexpected layout change much easier to diagnose.

## 2. Exercise every installation state

### Clean install

1. Confirm Vita Moonlight Host is absent from **Windows Settings > Apps >
   Installed apps** (Windows 11) or **Apps & features** (Windows 10).
2. Run the candidate setup file as Administrator and accept the recommended
   components.
3. Setup must not ask you to install an unknown root certificate. Any failed
   prerequisite must produce a visible explanation and stop later
   configuration.
4. If Windows requests a restart, restart. Open **Vita Moonlight Host** from
   Start, choose **Restart as Administrator** if offered, and click
   **Set up or repair this PC**.
5. Click **Check readiness**. Sunshine, Microsoft Visual C++ runtime,
   ViGEmBus/controller support, VDD/virtual display, display modes, recovery,
   and Stream rescue must be ready.

No separate .NET runtime, SDK, PowerShell module, driver download, or command
line should be required.

### Upgrade from an older release

1. Before updating, verify that the older installation can still see the
   paired Vita and any unrelated Sunshine applications.
2. End the stream and run the candidate installer directly over the older
   installation.
3. Restart Windows if requested, then open the Administrator control panel and
   click **Set up or repair this PC**.
4. Run **Check readiness**. Confirm there is one Vita Moonlight Host entry, one
   managed virtual display, and no second Sunshine installation.
5. Confirm Sunshine credentials, Vita pairing, unrelated applications, and
   user-created Sunshine settings were not discarded.
6. Complete a stream before judging the upgrade successful.

### Same-version reinstall and repair

1. With the candidate already working, run the exact same installer again.
2. Open the Administrator control panel and click **Set up or repair this PC**
   twice: once as repair and once when everything is already ready.
3. Neither pass may leave a sustained black physical screen, create duplicate
   devices, duplicate Sunshine installs, or lose pairing/settings.
4. Run **Check readiness** and complete another stream.

If setup requests a restart at any point, it must stop safely before applying
stream-display configuration. Restart once and resume with
**Set up or repair this PC**; it must not enter an unexplained restart loop.

## 3. Check the Windows control panel and idle display

1. Launching **Vita Moonlight Host** must open a persistent graphical control
   panel. A command window that flashes and disappears indicates the wrong or
   obsolete file.
2. On **Get started**, read the ready state and run **Check readiness**.
   Instructions should describe a user action rather than expose raw developer
   commands.
3. On **Streaming**, confirm:
   - automatic Vita-display switching is enabled;
   - Force SDR is enabled; and
   - the virtual-display match is blank unless the PC has multiple virtual
     display drivers.
4. On **Display & recovery**, click **Show detected displays**. Record the physical display names
   and **VDD by MTT**. VDD should be inactive while idle and must not be the
   only active display.
5. If VDD is active while idle, click **Turn off idle Vita display**, list
   displays again, and verify the physical display remains active.
6. Choose **Test Vita display for 15 seconds**. The physical screen may go blank
   during the preview. Do not end the host process.
7. Wait for automatic restoration. Click **Show current session state** and verify no
   display transaction remains pending.
8. Run **Ctrl + Alt + Shift + F11** while idle. A short blink is acceptable;
   physical displays must remain usable and Sunshine must restart.
9. Open **Diagnostics & support**, run **Run full health check**, and choose
   **Save support report...**. Confirm the saved JSON identifies the host
   version and current component/display/recovery state. Review it in a text
   editor. Creating it must be an explicit one-time action; the control panel
   must not continuously write a host log.
10. Test **Copy technical details** and **Open diagnostics folder**. Neither
    action may change the display or streaming configuration.

Record every unclear, overlapping, clipped, or incorrectly rendered control
panel label.

## 4. Install, pair, and launch on the Vita

1. Transfer `moonlight.vpk` with VitaShell USB or FTP, select it in VitaShell,
   and install it. For a Vita-side upgrade, install the new VPK over the older
   app instead of deleting the old app first.
2. Start Vita Moonlight. On a clean first run, confirm **Recommended** is
   selected: 960x544, 60 FPS, 8 Mbps, H.264 SDR.
3. Discover the PC. If discovery fails, test manual host entry and record the
   address type used.
4. Select the PC and enter the displayed PIN in Sunshine's web page.
5. Launch **Steam Big Picture**. Confirm Windows activates the dedicated VDD
   at 960x544 and the Vita shows that display rather than merely mirroring a
   physical monitor.
6. Confirm the image fills the Vita panel, is not 4:3, and is not washed out
   by HDR.
7. Disconnect normally from the Vita menu. The physical display must return.
8. Repeat with **Desktop** and one custom Sunshine game. The virtual-display
   lifecycle must cover every Sunshine application, not only Steam.
9. Test deleting a saved host, rediscovery, manual entry, pairing again,
   Wake-on-LAN if the PC supports it, and reconnect.

## 5. Check the Vita interface and stream controls

1. During a stream, hold **START**, then press **L + R** within one second.
   Confirm the in-stream menu opens and START/L/R do not reach Windows.
2. Open and close every menu and settings page. Record text that is clipped,
   poorly aligned, rendered incorrectly, or covered by another element.
3. Compare pre-stream Settings with **Stream & virtual display** and
   **Controller & input** in the stream menu. Shared choices must show the same
   saved values.
4. Cycle **Performance overlay** through **Off**, **Frame rate**,
   **Frame rate + network**, and **Advanced**. Enabled modes must appear at the
   top right on a translucent background without blocking menus. Off must draw
   nothing.
5. Open **Real-time diagnostics**. It should show understandable session,
   decoder, network, controller, gyro, overlay, and support-log state. It must
   not start a support log by itself. Press **O** or **START** to return.
6. Test **Reliable**, **Recommended**, **High quality**, and **Remote / VPN**.
   Confirm their visible values are respectively:
   - 960x544, 30 FPS, 5 Mbps, Auto;
   - 960x544, 60 FPS, 8 Mbps, Auto;
   - 960x544, 60 FPS, 12 Mbps, Auto; and
   - 960x544, 30 FPS, 4 Mbps, Remote.
7. Change bitrate manually and verify the preset becomes **Custom**. Choose
   **Restore recommended defaults** and confirm Recommended, Maximum
   compatibility, performance overlay Off, and support log Not capturing are
   restored.
8. Leave Settings, disconnect, restart Vita Moonlight, and confirm settings
   persist.

## 6. Check video, reconnection, and game recovery

1. Run a motion-heavy game for at least 20 minutes on Recommended. Record
   frame rate, RTT, measured video rate, decode time, frame drops, audio
   breakup, visible compression, and reconnect behavior.
2. Repeat a shorter pass on High quality with strong Wi-Fi and Reliable on a
   constrained link. Higher bitrate should improve motion only when the link
   can sustain it.
3. While a game is running, select 960x540, 960x544, and 1280x720 one at a
   time. After each selection choose **Apply resolution + reconnect**.
   Sunshine must reconnect at the selected desktop/encoder dimensions without
   closing the Windows game.
4. Change the controller profile in-stream and choose
   **Apply input changes + reconnect**. This must recreate the controller
   without resetting the display.
5. If a game goes black but the Vita-rendered menu still opens, select
   **Close Windows game**. The first confirmation must be cancellable; after
   confirming twice, only the foreground game should close and Steam should
   remain.
6. Test **End Sunshine app**. The session must end and the physical display
   must return.
7. Test **Recover host display**. The stream should disconnect; within roughly
   15 seconds the physical display should be active, VDD inactive, and
   Sunshine available for a new stream.

For a Doom Eternal result, state whether the game used borderless or exclusive
fullscreen and whether in-game HDR was enabled. Record whether the black frame
began during a menu transition and whether the Vita menu remained visible. A
black game frame alone does not prove a display-driver crash.

## 7. Check controller, gyro, touch, and keyboard

1. Choose **Maximum compatibility**, reconnect, and confirm Windows sees one
   XInput controller. Test sticks, D-pad, face buttons, L/R, triggers,
   START/SELECT, and stick clicks. Press and release each input; no button or
   axis may stay active.
2. Press **O** repeatedly in a game that uses it. Each press must produce one
   action rather than a continuous held input.
3. With the default **Local double-tap** PS behavior, pause Windows media and
   press PS once. It must not resume media or send Guide. Double-press PS and
   confirm the Vita can always return to LiveArea.
4. Select **Safe PC Guide**, reconnect, and confirm a single PS press reaches
   Steam after its short safety delay while double PS remains a local escape.
   Also test Immediate PC Guide and System/LiveArea, then restore Local
   double-tap.
5. Choose **Steam / DS4 + gyro**, select **Apply input changes + reconnect**,
   and confirm Steam sees a DS4 motion source. Test pitch, roll, and yaw, no
   obvious drift at rest, suspend/resume, and gyro disable. Real-time
   diagnostics should show whether the host requested motion and whether
   reports are being sent. Under **Settings > Controller**, change **Gyro
   horizontal sensitivity** and **Gyro vertical sensitivity** independently.
   Confirm the corresponding axis becomes less responsive near 0.1x and more
   responsive near 5.0x, then restore the starting values. Use Steam Input
   afterward for optional per-game response curves, dead zones, and activation
   behavior.
6. Open **Settings > Controller > Graphical button mapper**. Remap at least one face
   button, one shoulder/trigger, Guide, and a rear-touch zone. Enable the
   custom map, verify each assignment, then disable it and confirm hardware
   defaults return.
7. Open **Settings > Touch and keyboard > Front-touch zone mapper**. Change
   zone size/inset and assign one local action plus one gamepad, mouse, or
   keyboard action. Confirm the preview is readable, touches correspond to the
   drawn zones, and settings survive an app restart.
8. Test Relative mouse, DS4 Touchpad, Absolute mouse, and Tablet touch modes.
   Record app compatibility and corner alignment.
9. Focus a non-secret Windows text field. Open the keyboard with
   **START + D-pad Left**, type, use Backspace/Enter, and close it. Repeat from
   **Open on-screen keyboard** in the stream menu. No menu-confirm/cancel input
   should leak to the game.

## 8. Capture one readable support log

Support logging is optional and **off by default**. Follow
[Logging and support](../docs/LOGGING_AND_SUPPORT.md).

1. Start with the support log Not capturing and performance overlay Off.
   Stream for one minute. Vita Moonlight must not open, create, or write a
   support-log file.
2. Choose **Start support log** immediately before one short reproduction,
   note the local clock time and time zone, reproduce the issue once, then
   choose **Stop and save support log**.
3. Copy the path displayed by Real-time diagnostics using VitaShell. The
   normal path is `ux0:data/moonlight/moonlight.log`.
4. Confirm the file begins with `schema=vita-support-v1` records for
   `session.start`, `system.snapshot`, `config.snapshot`,
   `connection.snapshot`, and `stream.snapshot`, and ends with a
   `session.end` summary. Starting another capture must rotate this one to
   `moonlight.previous.log` and create a fresh `moonlight.log`.
5. Inspect it before sharing. A useful stream capture should use
   `connection.state`, `connection.stage`, `stream.action`, `decoder.state`,
   `network.state`, 10-second `network.summary`, `motion.state`, and
   rate-limited `error.legacy` records as applicable. Ordinary touch movement,
   every button sample, typed characters, and every gyro sample must not flood
   the file.
6. Redact host names, usernames, IP/MAC addresses, pairing information, and
   other personal data. Attach the log with the exact problem timestamp and
   report metadata.
7. If Python 3 is available, run the packaged
   `tools\SupportLog\summarize-vita-log.py` once in normal and `--json` modes.
   Both summaries must omit raw legacy text, unknown fields, arbitrary
   filenames, and paths. Python is optional for community reporters; a
   reviewed raw log is acceptable.

## 9. Recovery and interrupted sessions

Complete all safe tests available on the machine:

1. **Normal disconnect:** choose Disconnect stream. The original physical
   layout returns without sign-out or reboot.
2. **Vita suspend:** suspend during a stream. The PC returns to a physical
   display automatically or at next sign-in; resume and reconnect.
3. **Network loss:** temporarily disconnect the Vita network during a stream.
   Confirm PC recovery independently of whether Vita firmware restores Wi-Fi
   to the running app.
4. **Emergency hotkey:** during a stream, press
   **Ctrl + Alt + Shift + F11** on the PC keyboard. The stream ends, physical
   displays return, the VDD reloads, Sunshine restarts, and another stream can
   begin.
5. **Sign-in safeguard:** if an interruption leaves a pending display state,
   sign out and back in. Confirm the physical layout is restored, open the
   control panel, choose **Show current session state**, and verify that no
   display transaction remains pending.

Optional crash injection is for a disposable lab PC with independent remote
administration only. End the host/recovery process while the stream display is
active, then sign out and in to verify recovery. Do not perform this on a
normal single-monitor personal PC.

## 10. Laptop and multi-monitor passes

### Laptop

1. Repeat clean install or upgrade, one normal stream, network/suspend
   interruption, F11 recovery, same-version repair, and uninstall on a laptop.
2. Perform at least one stream and recovery while on battery. Recovery must
   not wait for AC power.
3. Repeat once docked or with an external display if available.

### Multiple physical monitors

1. Record the original enabled displays, primary display, arrangement,
   resolutions, scaling, and HDR state.
2. Complete one normal stream/disconnect, one interrupted stream, and one F11
   recovery.
3. Verify every connected physical display is available again, VDD is
   inactive while idle, and the original arrangement is preserved.

Report layout changes even when every screen becomes visible again.

## 11. Reinstall and remove the Vita app

Complete this while the Windows host is still available:

1. In VitaShell, install the exact candidate `moonlight.vpk` over the current
   app.
2. Start Vita Moonlight. Confirm settings, graphical mappings, touch zones,
   and the paired host remain, then complete one short stream.
3. Stop and copy any support capture you need.
4. Highlight Vita Moonlight in LiveArea, press **Triangle**, and choose
   **Delete**. Confirm the app icon is gone.
5. External Moonlight settings and support-log files may remain so they can
   survive a reinstall. Record what remains, but do not delete a parent storage
   directory merely to remove a log.
6. Reinstall the candidate VPK. Confirm the app starts safely whether external
   settings were preserved or the test Vita presents a fresh configuration.

## 12. Uninstall the Windows host while keeping shared components

This is the recommended path on a normal PC.

1. End all streams and confirm a physical display is visible.
2. Open **Windows Settings > Apps > Installed apps** (Windows 11) or
   **Apps & features** (Windows 10), find **Vita Moonlight Host**, and choose
   **Uninstall**.
3. Leave removal of MTT VDD, Sunshine, and ViGEmBus **unselected**. Optionally
   keep the one stream-rescue log when diagnosing a recovery failure.
4. If Windows requests a restart, restart and run uninstall again.
5. Confirm:
   - Vita Moonlight Host and its Start-menu entry are gone;
   - the physical display is usable and VDD is inactive;
   - Sunshine, ViGEmBus, and VDD remain installed;
   - Vita-owned recovery tasks and Sunshine integration are removed; and
   - unrelated Sunshine applications and later user changes remain.

Reinstall once after this test and repeat the install, health-check, and stream
steps. This checks installation over retained shared dependencies.

## 13. Uninstall the Windows host while removing shared components

Use a disposable PC or reversible snapshot. Removing these components can
break other streaming/controller applications.

1. Start with a healthy candidate installation and a visible physical display.
2. Run uninstall and explicitly select MTT VDD, Sunshine, and ViGEmBus.
3. The uninstaller must restore and verify a physical display before removing
   the driver or recovery safeguards.
4. If a dependency removal fails or requests a restart, the host and recovery
   safeguards must remain. Restart Windows and run uninstall again.
5. Confirm the host, VDD, Sunshine, and ViGEmBus are absent and the physical
   display remains usable.
6. Reinstall from the release files once more. The all-in-one setup must
   restore every selected prerequisite without a manual download.

## 14. Submit the result

Use the copyable template in
[Community testing](../docs/COMMUNITY_TESTING.md). For every failure include:

- the shortest exact reproduction;
- expected and actual results;
- whether the display recovered automatically, with F11, at sign-in, or not
  at all;
- the exact local time and time zone of the failure;
- a screenshot/video when it clarifies UI or display behavior; and
- the shortest optional, redacted log covering that time.

Never post credentials, pairing data, public/private tokens, usernames, or
unredacted network identifiers.
