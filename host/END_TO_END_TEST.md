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

For the deliberately unsigned beta preview, also download `SHA256SUMS` and
`windows-signing-status.json`. The release warning must name the exact tag,
the signing manifest must say `unsigned-beta-preview`, and Windows will show
**Unknown publisher**. Do not continue if the release page, tag, checksum, and
signing status disagree.

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
| Paused reinstall/repair | The exact candidate is deliberately paused before the same installer is run over it; Enable later completes deferred work. |
| Vita package lifecycle | Candidate VPK is installed over itself, removed from LiveArea, and installed again. |
| Keep-dependencies uninstall | Host is uninstalled while Sunshine, ViGEmBus, and the shared VDD package are retained; exact Vita display authority is still released. |
| Optional-removal uninstall | Disposable PC/snapshot where Sunshine and ViGEmBus can safely be removed. |
| Different-account UAC | Disposable standard-user session where setup is approved with a different Administrator account; setup must fail before mutation, while later uninstall remains possible. |
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
2. Sign in to the Administrator account that will be used for streaming. Run
   the candidate setup file and accept the recommended components.
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

### Different-account UAC safety and uninstall access

Run this negative case on a disposable PC or snapshot; it is not a supported
setup path.

1. Sign in with a standard Windows account and start setup. At UAC, supply a
   different Administrator account.
2. Setup must stop with a message naming the mismatch and explaining that the
   recovery tasks would target the wrong interactive account. It must do so
   before display recovery, task removal/creation, or product-file replacement.
3. Confirm the display topology, existing Vita tasks, installed version, and
   existing pairing are unchanged.
4. Sign in to the intended Administrator streaming account and rerun setup. It
   must proceed normally and both Task Scheduler entries must use **Run only
   when user is logged on**, **Run with highest privileges**, and the exact
   installed `VitaMoonlight.Host.exe` action.
5. From a standard-user session, approve **uninstall** with the different
   Administrator account. Uninstall must still restore a physical display and
   remove only the two exact Vita-owned task definitions; it must not reject
   this cleanup merely because the interactive account differs.

### Upgrade from an older release

1. Before updating, verify that the older installation can still see the
   paired Vita and any unrelated Sunshine applications.
2. End the stream and run the candidate installer directly over the older
   installation.
3. Repeat once after deleting only the exact managed Vita firewall rule while
   leaving both Vita scheduled tasks installed. The candidate must use its
   embedded current helper for pre-copy shutdown; it must not fail inside the
   older host with `0x80070002`, and cancel rollback must recreate a ready agent.
4. Restart Windows if requested, then open the Administrator control panel and
   click **Set up or repair this PC**.
5. Run **Check readiness**. Confirm there is one Vita Moonlight Host entry, one
   managed virtual display, and no second Sunshine installation.
6. Confirm Sunshine credentials, Vita pairing, unrelated applications, and
   user-created Sunshine settings were not discarded.
7. Before upgrading, use Notepad to place a harmless
   `community-test-retain.txt` beneath `C:\ProgramData\VitaMoonlight` if that
   old directory exists. After upgrade, confirm the exact retired state files
   from the older release and obsolete duplicate guide files beside the host
   executable are gone, while that unrelated test file remains. Cleanup must
   not follow reparse points or broadly delete an old directory. Remove the
   test file yourself after recording the result.
8. Complete a stream before judging the upgrade successful.

### Same-version reinstall and repair

1. With the candidate already working, run the exact same installer again.
2. Open the Administrator control panel and click **Set up or repair this PC**
   twice: once as repair and once when everything is already ready.
3. Neither pass may leave a sustained black physical screen, create duplicate
   devices, duplicate Sunshine installs, or lose pairing/settings.
4. Run **Check readiness** and complete another stream.

### Reinstall or repair while deliberately paused

1. With the candidate healthy and no stream active, choose **Pause Vita host
   features**, restart Windows, and verify the control panel still says Paused.
2. Run the exact same candidate installer with the recommended components.
   The physical desktop must remain visible throughout. Setup must preserve the
   Paused preference, leave both Vita recovery tasks absent, and leave the
   managed VDD disabled; Sunshine and any pre-existing Apollo installation
   must remain unchanged. The public beta must not offer Apollo as a setup
   choice.
3. Restart if requested. Reopen the Administrator control panel and confirm it
   still says Paused rather than silently enabling host features.
4. Choose **Enable Vita host features**. Setup work saved while paused must run
   exactly once, **Check readiness** must pass, the VDD must be PnP-disabled
   while idle, and existing pairing/settings must remain intact.

### Interrupted setup takeover (disposable VM or snapshot only)

This deliberately terminates setup. Do not run it on a PC you cannot restore.

1. Start once from a clean disposable snapshot. End setup after the protected
   maintenance records appear but before host configuration completes. Run the
   installer again with host configuration selected, then repeat with it
   cleared. The retry must take over the dead owner, create only the selected
   safeguards, clear both maintenance records, and leave a physical display
   active.
2. Start from a healthy candidate install with a visible physical display.
   Run the same installer again. Once installation progress has begun and
   `%ProgramFiles%\Vita Moonlight Host\state\installer-maintenance.json`
   exists, end the **Vita Moonlight Host Setup** process from Task Manager.
   Do not delete or edit either maintenance record.
3. Run the same installer again. It must recognize that the recorded owner is
   no longer running, safely take over, finish repair, clear the maintenance
   records, and pass **Check readiness**.
4. Repeat step 2 from the repaired snapshot. This time choose **Uninstall** in
   Windows Settings without first repair-installing. The uninstaller must prove
   the old setup owner is gone, hand off to its durable uninstall transaction,
   restore a physical-only display, and complete the keep-dependencies path.
5. While a setup process that owns a live maintenance record is still running,
   a second setup/uninstall or control-panel change must refuse to compete. A
   read-only health/support view may remain available.
6. From a healthy enabled snapshot, repeat the interruption once immediately
   after **Stopping the stream-rescue agent** and once after **Suspending
   automatic display recovery** appears in setup. On each retry, clear
   **Configure this PC for Vita streaming now**. Setup must still restore every
   safeguard recorded before the interruption before it clears the maintenance
   fence. It must not infer that a now-missing task was absent originally.
7. On a separate disposable snapshot, stage exactly one MTT display device
   that was not created by Vita Moonlight and record its instance ID and
   enabled state. Start setup with the Sunshine/display task selected, then
   terminate setup after protected maintenance begins but before product files
   are copied. The device state must be unchanged and no Vita ownership journal
   may remain. Rerun setup to completion: setup must show a separate adoption
   question before copying files. Choose **No** once and verify the device is
   unchanged, then retry and choose **Yes**. Idle must leave that exact device
   disabled, and normal uninstall must restore the enabled state recorded
   before installation. Repeat from a snapshot where that device was
   initially disabled. Repeat silent setup without `/ADOPTEXISTINGVDD`; it must
   stop before copying files. With that switch, it may adopt only one
   unambiguous device. No second MTT or legacy IDD target may be changed.

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
   - the virtual-display match is blank; the supported path identifies the
     exact managed device without a user-selected output.
4. On **Display & recovery**, click **Show detected displays**. Record the
   physical display names. The VDD target may be absent from this active-target
   list because the exact managed device must be PnP-disabled while Enabled +
   Idle; it must never be the only active display.
5. If VDD is active while idle, click **Reconcile idle display now**, list
   displays again, and verify the physical display remains active. Do not
   enable, disable, or select the VDD manually in Windows.
6. Choose **Test Vita display for 15 seconds**. The physical screen may go blank
   during the preview. Do not end the host process.
7. Wait for automatic restoration. Click **Show current session state** and verify no
   display transaction remains pending.
8. Run **Ctrl + Alt + Shift + F11** while idle. A short blink is acceptable;
   physical displays must remain usable and Sunshine must restart.
9. Open **Diagnostics & support**, run **Run full health check**, and choose
   **Save support report...**. Save it as `enabled-before-pause.json` and
   confirm the JSON identifies the host
   version and current component/display/recovery state. Review it in a text
   editor. Creating it must be an explicit one-time action; the control panel
   must not continuously write a host log. Record `backendStatus`,
   `backendDesiredState`, `recoveryTaskStatus`, `rescueAgentTaskStatus`,
   `rescueAgentRunning`, `backendActivePhysicalDisplayCount`,
   `backendManagedVddDeviceCount`, `backendManagedVddEnabledCount`,
   `backendManagedVddActive`, `hostMode`, `sunshineInstalled`, and
   `sunshineVersion`.
10. Test **Copy technical details** and **Open diagnostics folder**. Neither
    action may change the display or streaming configuration.
11. Disconnect the Vita, note whether Sunshine's web page is reachable, choose
    **Pause Vita host features**, and restart Windows. Confirm the physical
    desktop remains usable. Reopen **Vita Moonlight Host** as Administrator;
    the persistent text must report Paused and say the rescue agent and F11
    shortcut are unavailable until Enable. Save `paused-after-restart.json`
    and confirm `backendStatus` and
    `backendDesiredState` are `Disabled`, `backendManagedVddEnabledCount` is
    `0`, `backendManagedVddActive` is `false`, `recoveryTaskStatus` and
    `rescueAgentTaskStatus` are `Missing`, and `rescueAgentRunning` is `false`.
    `hostMode`, `sunshineInstalled`, and `sunshineVersion` must match the
    baseline; a Sunshine web page that was reachable before Pause must remain
    reachable. Pause is not a network-access control.
12. Choose **Enable Vita host features**, run **Check readiness**, and save
    `enabled-again.json`. Confirm the lifecycle, task, rescue-agent, and
    managed-VDD fields match `enabled-before-pause.json`; the VDD must remain
    PnP-disabled while idle. Sunshine, pairing, and settings must remain
    unchanged. Use the
    [support-report comparison table](../docs/LOGGING_AND_SUPPORT.md#create-a-windows-host-support-report)
    when a field is unclear. Repeat Pause and Enable once to confirm both are
    idempotent.

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
4. First exercise an interrupted pairing: enter an incorrect PIN in Sunshine
   or cancel the attempt. Return to the Vita main screen and confirm the PC is
   still listed under **Saved computers** as **Pairing required**.
5. Select that same entry, enter the correct displayed PIN in Sunshine, and
   confirm it appears paired under **Saved computers** immediately without an
   app restart. Fully close and reopen Vita Moonlight and confirm the paired
   entry remains.
6. Launch **Steam Big Picture**. Without selecting a Windows output or touching
   Device Manager, confirm the authenticated Vita preflight activates the
   dedicated VDD at 960x544 before Sunshine capture and the Vita shows that
   display rather than merely mirroring a physical monitor.
7. Confirm the image fills the Vita panel, is not 4:3, and is not washed out
   by HDR.
8. Disconnect normally from the Vita menu. The exact physical layout must
   return and the managed device must be PnP-disabled.
9. Repeat with **Desktop** and one custom Sunshine game. The virtual-display
   lifecycle must cover every Sunshine application, not only Steam.
10. Choose **Forget on this Vita**, restart the Vita app, and confirm the local
    entry and credentials stay removed. This action must not claim to revoke
    Sunshine's authorized-client entry. Then test rediscovery, manual entry,
    pairing again, Wake-on-LAN if the PC supports it, and reconnect.

## 5. Check the Vita interface and stream controls

1. During a stream, press **START** once and confirm Windows receives one
   ordinary controller Start press. Then hold **SELECT first** and press
   **L + R** within one second. Confirm the in-stream menu opens and
   SELECT/L/R do not reach Windows.
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
   not start a support log by itself. Press the configured **Cancel** button
   (O by default) or **START** to return.
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
   closing the Windows game. The supported default configuration must use an
   authenticated host preflight before Sunshine resume, without sending a
   display-mode shortcut or adding the former 750 ms pre-disconnect delay.
   **Check automatic handoff** must report automatic authenticated handoff and
   that legacy F8-F10 shortcuts are not registered.
4. Disconnect while the game remains Sunshine's current application, then
   immediately select the same application again. Confirm the resume path arms
   the VDD and reconnects at the selected mode without restarting the game.
   Disconnect again; a delayed stop from the first generation must not restore
   the physical display over the newer stream.
5. Change the controller profile in-stream and choose
   **Apply input changes + reconnect**. This must recreate the controller
   without resetting the display.
6. If a game goes black but the Vita-rendered menu still opens, select
   **Open Windows Task Manager**. Verify Ctrl+Shift+Esc reaches Windows without
   leaking a stuck controller state, then explicitly end the test game. Steam
   should remain; Vita Moonlight must never infer or terminate a foreground
   process itself.
7. Test **End Sunshine app**. The session must end and the physical display
   must return.
8. Test **Recover host display**. The stream should disconnect; within roughly
   15 seconds the physical display should be active, the VDD PnP-disabled, and
   Sunshine available for a new stream.
9. With no stream running and the Vita disconnected or off, arrange at least
   two ordinary windows at distinct sizes and positions and take a reference
   screenshot. Turn the physical monitor off or let it enter power-save before
   putting Windows to sleep with a chassis button or known keyboard shortcut.
   Wake the PC and turn the monitor back on. The rescue agent
   should record a successful `power-suspend-display-prepare` action and then
   either a successful `resume-display-check:` decision or a successful
   `recover-display-host` action triggered by resume. Within about 20 seconds,
   at least one physical display must be active, the managed VDD PnP-disabled,
   every test window must retain its size and position, and Windows must be
   normally responsive at the physical monitor's saved resolution and refresh
   rate without a restart. Record any structured mode-repair warning; it is
   non-fatal only when the physical topology remains usable at the intended
   mode.
10. Repeat sleep while a disposable/test stream is active. Treat the stream as
   interrupted. Do not require the physical display to become visible before
   initiating sleep; instead, verify the suspend-preparation diagnostic after
   wake. The physical desktop must then win over a stale VDD-only or
   physical-plus-VDD topology; reconnect normally afterward.
11. Exercise the cross-process switch/suspend race on a disposable PC: choose
    **Test Vita display for 15 seconds** and immediately put Windows to sleep
    while the display is switching. Repeat three times with slightly different
    timing. After every wake, the physical desktop must be active at its saved
    mode, the VDD PnP-disabled, and no later test process may recommit a Vita-only
    topology. A failed/aborted test command is acceptable; a stale Vita display
    is not.

For all sleep tests, open **Diagnostics & support > Open diagnostics folder**
after wake and inspect `stream-rescue.log` at the test timestamp. A line is
successful when its third tab-separated field is `True`. A
`resume-display-check:` message reports `restored physical modes` and
`mode-repair warnings`; record any nonzero warning count and the bracketed
warning code. A `recover-display-host` message instead lists the recovery
steps it performed. The separate
`stream-rescue-status.json` contains only the latest result. See
[Read the sparse Windows rescue records](../docs/LOGGING_AND_SUPPORT.md#read-the-sparse-windows-rescue-records)
before sharing either file.

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
7. Open **Settings > Touch and keyboard > Front-touch tap-zone mapper**. Change
   zone size/inset and assign one local action plus one gamepad, mouse, or
   keyboard action. Confirm a short stationary tap runs the action once, while
   a swipe, drag, hold, and two-finger touch beginning in that same zone remain
   normal touch input. Confirm settings survive an app restart.
8. Test Relative mouse, DS4 Touchpad, Absolute mouse, and Tablet touch modes.
   Record app compatibility and corner alignment.
9. Focus a non-secret Windows text field. Open the keyboard with
   **SELECT first + D-pad Left**, type, use Backspace/Enter, and close it. Repeat from
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
   to the running app. Within about 60 seconds (the authenticated lease plus
   bounded recovery), the fallback observer must restore the exact physical
   layout and PnP-disable the managed VDD without a normal stop request.
4. **Sunshine crash:** only on a disposable PC or a machine with independent
   physical/remote control, start a stream and end the exact Sunshine process.
   The fallback observer must restore the exact physical layout and PnP-disable
   the VDD. Restart Sunshine, then prove a new stream and normal disconnect
   work. Do not end the Vita Moonlight rescue agent in this case.
5. **Emergency hotkey:** during a stream, press
   **Ctrl + Alt + Shift + F11** on the PC keyboard. The stream ends, physical
   displays return, the VDD reloads, Sunshine restarts, and another stream can
   begin.
6. **Sign-in safeguard:** if an interruption leaves a pending display state,
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
3. Verify every connected physical display is available again, the managed VDD
   is PnP-disabled while idle, and the original arrangement is preserved.

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
3. Leave removal of Sunshine and ViGEmBus **unselected**. Optionally keep the
   one stream-rescue log when diagnosing a recovery failure. The shared MTT
   package is always retained; exact Vita display authority is always released.
4. If Windows requests a restart, restart and run uninstall again.
5. Confirm:
   - Vita Moonlight Host and its Start-menu entry are gone;
   - the physical display is usable;
   - Sunshine, ViGEmBus, and VDD remain installed;
   - an exact Vita-created VDD node is absent, an exact adopted node matches
     its recorded pre-install enabled state, and an unproven node is unchanged;
   - Vita-owned recovery tasks and Sunshine integration are removed; and
   - unrelated Sunshine applications and later user changes remain.
6. Reinstall the candidate, choose **Pause Vita host features**, restart, and
   repeat this keep-dependencies uninstall without enabling again. The
   uninstaller must restore a physical-only topology, remove the saved paused
   lifecycle/tasks, preserve Sunshine/ViGEmBus and the shared VDD package, and
   release the exact display under the same ownership rules. No Vita-owned
   listener or background process may remain.

### Interrupted-finalization retry (disposable VM/snapshot)

Exercise each boundary with the exact release candidate and restore the
snapshot between cases:

1. Interrupt uninstall before the finalized marker is committed. The primary
   `VitaMoonlight.Host.exe`, durable guard, and required safeguards must remain;
   rerunning uninstall must safely repeat finalization.
2. Interrupt after the finalized marker is committed and the primary host is
   deleted, but before the guard is deleted. Rerunning uninstall must recognize
   the exact finalized marker, finish file-only cleanup, and remove the guard.
3. With the primary host still present, truncate or replace the marker with an
   invalid value. Rerunning uninstall must repair the marker to an in-progress
   transaction and repeat safe finalization. With the marker invalid and the
   host missing, uninstall must fail closed without removing recovery state;
   restore the same-version host from quarantine or the portable release and
   retry.

Reinstall once after these tests and repeat the install, health-check, and stream
steps. This checks installation over retained shared dependencies.

### Advanced finalized-uninstall retry (disposable VM only)

This is a maintainer/community-specialist crash fixture. Take a snapshot first.

1. Open an Administrator PowerShell window and hold the installed host file
   open without delete sharing:

   ```powershell
   $hostPath = "$env:ProgramFiles\Vita Moonlight Host\VitaMoonlight.Host.exe"
   $heldHost = [IO.File]::Open($hostPath, 'Open', 'Read', 'Read')
   ```

2. From a second Administrator PowerShell window, run
   `& "$env:ProgramFiles\Vita Moonlight Host\unins000.exe" /NOCLOSEAPPLICATIONS`
   and keep shared dependencies. Uninstall must safely finalize, fail visibly
   when it cannot delete the held host, and retain the exact finalized guard.
3. Run `$heldHost.Dispose()` in the first window. Rename the host to
   `VitaMoonlight.Host.exe.retry-test` to model a crash after host deletion but
   before guard deletion, then run `unins000.exe` again. It must accept only the
   exact finalized guard, resume file-only cleanup, and remove that guard.
   The deliberately unknown `.retry-test` file must be retained rather than
   recursively deleted; reset the VM snapshot after recording the result.
4. Repeat from the snapshot with a missing host and a deliberately torn or
   in-progress guard. The uninstaller must fail closed and must not claim that
   finalization committed. Do not perform this mutation on a real installation.

## 13. Uninstall the Windows host while removing optional shared components

Use a disposable PC or reversible snapshot. Removing Sunshine or ViGEmBus can
affect other streaming/controller applications. The shared MTT driver package
is deliberately retained; exact display release follows the same mandatory,
ownership-scoped behavior as a normal uninstall.

1. Start with a healthy candidate installation and a visible physical display.
2. Run uninstall and explicitly select Sunshine and ViGEmBus removal.
3. The uninstaller must restore and verify a physical display before releasing
   the exact device or removing recovery safeguards.
4. If a dependency removal fails or requests a restart, the host and recovery
   safeguards must remain. Restart Windows and run uninstall again.
5. Confirm the host, Sunshine, and ViGEmBus are absent and the physical display
   remains usable. An app-created exact MTT device must be absent; an adopted
   device must match its recorded pre-install enabled state. The shared MTT
   driver package must remain staged.
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
