# End-to-end acceptance test

This is a GUI-first release test for client Windows 10 version 2004 (build
19041) or newer, or Windows 11, on x64 and a physical Vita. No terminal is
required for the normal pass. Record the exact Windows build, GPU and driver, Sunshine or
Apollo version, Vita model, network type, VPK commit, and host installer
commit.

## 1. Install and configure the host

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator with Sunshine,
   ViGEmBus, and the signed virtual-display driver selected. Setup must not ask
   to install a private root certificate or expose transient command windows.
   Every failed prerequisite must stop later configuration with a visible
   error. If Setup requests a reboot, it must defer Sunshine display
   configuration and active-display changes; reboot and finish with **Apply
   recommended setup**.
2. Reboot only if Windows requests it.
3. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel** and
   choose **Restart as Administrator** if shown.
4. On **Overview**, click **Apply recommended setup**.
   On a repair pass, this single action must restore a missing/stopped
   ViGEmBus service, update an unsupported Sunshine build, reinstall and
   verify the VDD at native mode, and reinstall both safeguards. A requested
   reboot must stop the workflow and explain how to resume it. During native
   verification, every active physical monitor must remain part of the
   topology. A brief display-mode flicker is acceptable; a sustained black
   physical screen or VDD-only topology is a failure.
5. Run **Apply recommended setup** again while the VDD is already installed,
   started, and enabled. A PnPUtil code 50 from the enable-device step must be
   treated as a verified no-op; setup must continue through restart and
   960x544 verification instead of showing a generic exit-code failure. If
   Windows rejects the mode, setup must restore the original topology, show
   the final Windows response as an error, and must not request another reboot
   merely because the mode check failed.
6. Treat the same machine as an upgrade, not a clean install. Add an unrelated
   resolution and retain non-default driver options, then rerun the installer.
   The pinned package must be staged before Vita modes are normalized, the
   unrelated settings must survive, and the effective mode list must contain
   only one 960x544/60 entry even when 60 Hz is global. Verification must use a
   non-persistent live mode change; it must not require
   `CDS_UPDATEREGISTRY` for the temporary extended topology.
   The existing target may disappear briefly while its device stack restarts;
   setup must wait up to 30 seconds for it to re-enumerate, without changing
   the active physical topology, then continue automatically.
7. Click **Run health check**. Sunshine, its supported version, ViGEmBus,
   Sunshine gamepad, Microsoft Visual C++ runtime, driver bundle, virtual
   display, recovery task, and
   **Stream rescue** must report ready. **Vita display modes** must say the
   modes are provisioned and native 960x544 was verified; **Display mode
   controls** must list 960x544, 960x540, and 1280x720 as ready. **Platform**
   must report an x64 OS/process. **App
   coverage** must say every Sunshine app, **Display lifecycle** must say native
   disconnect recovery enabled, and **Color mode** must say force SDR.

Opening the executable itself must show a persistent control panel. A terminal
window that only flashes and closes indicates an obsolete package.

## 2. Verify the idle and preview states

1. Open **Displays > List displays**. Identify the physical monitor and **VDD
   by MTT**. The managed target must be inactive while idle. Its first
   activation in this release must use 960x544/60; 800x600 indicates that the
   driver mode provisioning or reload did not complete.
2. If the desktop is extended onto VDD by MTT, click **Disable idle virtual
   display**. List displays again and verify the physical monitor is active and
   the managed virtual target is inactive.
3. Click **Preview 960x544 for 15 seconds** and approve the warning. The
   physical monitor may be blank during the countdown.
4. Wait without terminating the host. The original physical layout must return
   automatically. Click **Session status** and verify no transaction is pending.

Do not perform a forced-process-termination test on a normal single-monitor
desktop. It intentionally removes the only locally visible control surface. On
a dedicated lab PC with independent remote administration, an optional crash
test may terminate the host while switched, then sign out and in to verify the
recovery safeguard.

## 3. Install, pair, and launch

1. Install the VPK from the same build on the Vita.
2. Pair the Vita with Sunshine and confirm the PIN in Sunshine's web UI.
3. Launch Sunshine's built-in **Steam Big Picture** entry, not only the
   generated Vita Moonlight entry.
4. Verify Windows activates **VDD by MTT** at 960x544/60 before capture and the
   Vita shows Steam without the physical monitor's HDR washout.
5. End the stream from the Vita overlay. Verify the physical display returns
   and **Session status** is clear.
6. Repeat with **Desktop** and one custom Sunshine game. This is the regression
   test for global native display-lifecycle coverage.
7. Repeat one connection with Wi-Fi loss and one with Vita suspend. In both
   cases the physical display must recover or the logon safeguard must recover
   it at the next sign-in.

## 4. Overlay and video quality

1. During a stream, hold **START**, then press **L + R** within one second. The
   session menu must appear over live video with separate **Stream & virtual
   display**, **Controller & input**, performance-overlay, and **Real-time
   diagnostics** entries. Use a Windows controller-input viewer to confirm
   START, L, and R are never forwarded. Close it and repeat by pressing all
   three together, then by pressing L + R before START; both fallback orders
   must still open the menu and release any host input.
2. Before connecting, select **Recommended** in Settings and record its stream
   and controller values. During the stream, verify the matching core values
   appear on the two in-stream pages. Change bitrate, network mode, PS behavior,
   and touch mode in-stream, disconnect normally, and verify those saved values
   appear in Settings.
3. Cycle **Performance overlay** through **Off**, **Frame rate**, **Frame rate +
   network**, and **Advanced**. Each enabled mode must stay in the top-right
   corner over a translucent 50%-alpha background. Off must draw nothing;
   Frame rate must show rendered/target FPS; Frame rate + network must add
   connection health, RTT, and measured encoded-video rate; Advanced must add
   stream, decode-time, dropped-frame, and recovered/failed/out-of-sequence
   packet data.
4. Open **Real-time diagnostics**. It must replace the session menu with a
   dedicated live view of session, FPS, network, video rate, stream/packet,
   decode, drops, controller, gyro, Circle, overlay, logging, and log-path
   state. No inline gyro/Circle diagnostic line may remain in the normal menu.
   **O** and **START** must return to the menu.
5. Disconnect, open pre-stream Settings, use **Reset all to recommended**, and
   reconnect. Verify diagnostic file logging is Off. With performance overlay
   Off and the diagnostics screen closed, stream for one minute and confirm no
   new diagnostic lines are appended. Open Real-time diagnostics, press
   **Triangle**, reproduce a short stream/input issue, and verify logging shows
   Enabled. The first completed one-second sample must start from fresh
   counters, with no cumulative bitrate, FEC, or out-of-sequence spike. Press
   Triangle again, then use VitaShell to copy the append-only
   `ux0:data/moonlight/moonlight.log`. The capture must include the reproduction
   interval. If fallback storage is in use, copy the exact path displayed by
   the diagnostics screen instead.
6. Close the menu with **O**, reopen it with the same chord, and select **Close
   Windows game**. The first **X** must show a confirmation; **O** must cancel.
7. Launch a normal game from Steam Big Picture, reopen the menu, select
   **Close Windows game**, and press **X** twice. The foreground game must exit
   while Moonlight remains connected and Steam Big Picture becomes visible.
   **Help & recovery > Rescue agent status** must record a successful
   `close-foreground` action. Repeat with a disposable test app that ignores
   its normal close request and verify the agent force-terminates that app only.
8. Select **End Sunshine app** and press **X** twice. Sunshine must end its
   current app session, Moonlight must disconnect, and the physical monitor
   must return.
9. Reconnect, select **Recover host display**, and press **X** twice. The
   stream must disconnect. Within roughly ten seconds the physical monitor must
   be active, VDD must be inactive, Sunshine must be running, and rescue status
   must report success. Reconnect successfully.
10. Reopen the menu and choose **Disconnect stream**. The stream must end
   cleanly and restore Windows.
11. In Vita settings leave **PS button behavior** on its default **Local
   double-tap**. During a stream, pause host media and press PS once. Nothing
   may reach the Windows gamepad viewer and the media must stay paused. Quickly
   press PS twice: the Vita must return to LiveArea. Resume Moonlight and verify
   capture is restored without a stuck PS/Xbox button.
12. Select **Safe PC Guide**, reconnect, and press PS once. Windows must receive
   one Guide press after about 250 ms. Reconnect and double-press PS; the Vita
   must return to LiveArea without any Guide event reaching Windows. Verify
   **Immediate PC Guide** sends Guide without the delay and **System / LiveArea**
   returns home on one press without sending Guide. Restore **Local
   double-tap** after this test.
13. Select **Reliable**, **Recommended**, **High quality**, and **Remote /
    VPN** in turn. Verify their differing values are respectively 30 FPS /
    5 Mbps / Auto, 60 FPS / 8 Mbps / Auto, 60 FPS / 12 Mbps / Auto, and
    30 FPS / 4 Mbps / Remote. Each selection must also restore 960x544,
    1024-byte packets, H.264 Rec. 709 limited-range SDR, stereo with local audio
    off, 60 Hz client timing, host optimization, packet-loss recovery, frame
    pacing, fit scaling, vblank off, and Vita power-save suppression.
14. Change bitrate manually and verify the preset reads **Custom** without
    discarding the value. Choose **Reset all to recommended** and verify it
    additionally selects **Maximum compatibility**, performance overlay Off,
    and diagnostic file logging Off.
15. Select **Recommended**, reconnect, and confirm all values persist. Compare
    Settings with the in-stream pages again; controls shared by both surfaces
    must read identically.
16. Launch a Windows game, then choose 960x540, 960x544, and 1280x720 from
    **Stream & virtual display**. For each choice, select **Apply resolution +
    reconnect** and verify the host agent reports a successful
    `display-mode-WxH` action, the Vita performs a controlled video reconnect,
    Sunshine reports matching desktop and encoder dimensions, and the same
    Windows game remains running. The physical topology must not flash on.
    End the stream after each mode and verify normal physical-display recovery.
    An unsupported value such as 800x600 must be rejected by the host command.
17. Run a 20-minute motion-heavy stream with Advanced enabled. Check fine
    textures, camera pans, frame pacing, audio, reconnect behavior, measured
    video rate, decode time, and drops. Repeat at 12 Mbps on a strong network
    and at 5 Mbps/30 FPS on constrained Wi-Fi.
18. Confirm the 8 Mbps native Recommended profile has materially fewer motion
    artifacts than the former 5 Mbps default and does not produce sustained
    decode errors.
19. Exercise the Vita render-watchdog path on hardware. While streaming,
    temporarily stop host capture without closing the Moonlight session; the
    last frame must remain visible and the menu, performance overlay, and
    diagnostics screen must remain responsive. Rapidly open and close the menu,
    toggle vblank on and off several times, disconnect while the watchdog is
    drawing, and complete five reconnect cycles. There must be no crash,
    deadlock, torn UI, use-after-free symptom, or stuck input. If the decoder
    itself becomes unresponsive and UI cannot be drawn, double PS must still
    return to LiveArea; restart Moonlight before the next stream.

For the reported Doom Eternal case, run one pass in borderless SDR and one in
exclusive fullscreen with the normal game HDR setting. Record whether the
black frame begins during the title-to-menu transition, whether the Vita
overlay remains visible, whether **Close Windows game** returns to Steam, and
the matching Sunshine, rescue, and optional Vita diagnostic logs. Do not label
it a display-driver crash based on a black game frame alone.

## 5. Controller and motion

1. Choose **Maximum compatibility** and reconnect. Steam Big Picture and a
   gamepad tester must see one XInput controller with correct sticks, triggers,
   D-pad, face buttons, START/SELECT, and shoulder behavior. The profile must
   also restore Relative mouse, Local double-tap, gyro off, mapping off,
   shoulder swap off, and sprint helper off. Motion and controller-touchpad
   capabilities must not appear.
2. Choose **Steam / DS4 + gyro**, select **Apply input changes + reconnect**,
   and verify one DS4 with DS4 Touchpad and Safe PC Guide. The VDD must remain
   active at the same mode and rescue status must not gain a display-mode
   action. Test pitch, roll, and yaw with no drift at rest and the correct
   direction on all axes.
   Real-time diagnostics must progress from awaiting a host gyro request to a
   report rate and increasing events while Steam Input polls motion.
3. Disable motion, select **Apply input changes + reconnect**, and verify
   motion is no longer advertised.
4. Suspend and resume during motion input. The next stream must start with
   fresh sensor state and no stuck buttons or axes.

## 6. Touch, keyboard, and host management

1. Change each touch mode in the overlay: Relative mouse, DS4 Touchpad,
   Absolute mouse, and Tablet. Verify the new mode persists and no
   confirm/cancel press leaks to the host when the overlay closes.
2. In DS4 Touchpad mode test click, drag, and two-finger interaction.
3. In Absolute mouse mode touch all four corners and verify pixel alignment.
4. In Tablet mode test contact and pressure in a compatible application.
5. Press **START + Left** and type into a normal and an Administrator app.
6. Test discovery, manual host entry, pairing, Wake-on-LAN, reconnect, and
   deleting a saved host.

## 7. Recovery, uninstall, and artifacts

1. End the stream and verify no display transaction is pending.
2. Uninstall Vita Moonlight Host. The recovery and stream-rescue tasks must be
   removed, their background process must stop, and the physical display layout
   must remain intact.
3. In a disposable VM, make `session recover` return a nonzero result before
   uninstalling. Uninstall must stop before deleting the companion, rescue
   agent, or recovery task. Restore the test condition, recover the display,
   and confirm uninstall then completes normally.
4. Verify the release contains the VPK, Windows installer, portable host ZIP,
   source archive, licenses, `COMPATIBILITY.md`, `VITA_SETTINGS_GUIDE.md`, and
   `THIRD_PARTY_NOTICES.md`.
5. Repeat sections 1 through 4 on a second clean PC using the portable ZIP and
   only release artifacts. **Apply recommended setup** must install or verify
   the packaged Microsoft Visual C++ runtime before the VDD. No SDK, .NET
   runtime, PowerShell module, or manual driver download may be required.
6. On a laptop, repeat task installation and sign-in recovery while running on
   battery. Both recovery tasks must run rather than waiting for AC power.
7. On a PC with two physical displays, disable both through the controlled test
   path and run emergency recovery. Every connected physical display must be
   available again and VDD must be inactive.
7. If available, test an existing Sunshine installation outside the default
   Program Files directory. The companion must discover the registered service
   path and its adjacent `config` directory. A compatible newer build remains
   in place; an older build upgrades to the pinned minimum without installing
   a second copy or losing credentials.

## Optional command-line equivalents

For troubleshooting only, open Windows Terminal as Administrator:

```powershell
Set-Location "C:\Program Files\Vita Moonlight Host"
.\VitaMoonlight.Host.exe doctor
.\VitaMoonlight.Host.exe display list
.\VitaMoonlight.Host.exe display disable-virtual
.\VitaMoonlight.Host.exe session test --width 960 --height 544 --fps 60 --seconds 15
.\VitaMoonlight.Host.exe session mode --width 960 --height 544 --fps 60
.\VitaMoonlight.Host.exe session status
.\VitaMoonlight.Host.exe session recover
.\VitaMoonlight.Host.exe agent status
.\VitaMoonlight.Host.exe emergency recover-display
```
