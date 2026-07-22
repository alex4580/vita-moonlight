# End-to-end acceptance test

This is a GUI-first release test for Windows 10 or 11 x64 and a physical Vita.
No terminal is required for the normal pass. Record the Windows version, GPU
and driver, Sunshine or Apollo version, Vita model, network type, VPK commit,
and host installer commit.

## 1. Install and configure the host

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator with Sunshine,
   ViGEmBus, and the signed virtual-display driver selected. Setup must not ask
   to install a private root certificate.
2. Reboot only if Windows requests it.
3. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel** and
   choose **Restart as Administrator** if shown.
4. On **Overview**, click **Apply recommended setup**.
5. Click **Run health check**. Sunshine, ViGEmBus, Sunshine gamepad, driver
   bundle, virtual display, recovery task, and **Stream rescue** must report
   ready. **App
   coverage** must say every Sunshine app, **Display lifecycle** must say native
   disconnect recovery enabled, and **Color mode** must say force SDR.

Opening the executable itself must show a persistent control panel. A terminal
window that only flashes and closes indicates an obsolete package.

## 2. Verify the idle and preview states

1. Open **Displays > List displays**. Identify the physical monitor and **VDD
   by MTT**. An initial 800x600 mode on the virtual target is normal.
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

1. During a stream, press **START + L + R**. The overlay must appear over live
   video and host input must be neutral while it is open.
2. Close it with **O**, reopen it with **START + L + R**, and select **Close
   Windows game**. The first **X** must show a confirmation; **O** must cancel.
3. Launch a normal game from Steam Big Picture, reopen the overlay, select
   **Close Windows game**, and press **X** twice. The foreground game must exit
   while Moonlight remains connected and Steam Big Picture becomes visible.
   **Help & recovery > Rescue agent status** must record a successful
   `close-foreground` action. Repeat with a disposable test app that ignores
   its normal close request and verify the agent force-terminates that app only.
4. Select **End Sunshine app** and press **X** twice. Sunshine must end its
   current app session, Moonlight must disconnect, and the physical monitor
   must return.
5. Reconnect, select **Recover display + Sunshine**, and press **X** twice. The
   stream must disconnect. Within roughly ten seconds the physical monitor must
   be active, VDD must be inactive, Sunshine must be running, and rescue status
   must report success. Reconnect successfully.
6. Reopen the overlay and choose **Disconnect stream**. The stream must end
   cleanly and restore Windows.
7. During another stream, double-press **PS**. The Vita must return to LiveArea
   even while PS capture is enabled. Resume Moonlight and verify PS capture is
   restored without a stuck PS/Xbox button.
8. Set 960x544, 60 FPS, and 8 Mbps. Reconnect and confirm the values persist.
9. Run a 20-minute motion-heavy stream. Check fine textures, camera pans,
   frame pacing, audio, reconnect behavior, and the FPS counter. Repeat at 12
   Mbps on a strong network and at 5 Mbps/30 FPS on constrained Wi-Fi.
10. Confirm the 8 Mbps native profile has materially fewer motion artifacts than
   the former 5 Mbps default and does not produce sustained decode errors.

For the reported Doom Eternal case, run one pass in borderless SDR and one in
exclusive fullscreen with the normal game HDR setting. Record whether the
black frame begins during the title-to-menu transition, whether the Vita
overlay remains visible, whether **Close Windows game** returns to Steam, and
the matching Sunshine/rescue logs. Do not label it a display-driver crash based
on a black game frame alone.

## 5. Controller and motion

1. In the overlay choose **Xbox** and reconnect. Steam Big Picture and a
   gamepad tester must see one XInput controller with correct sticks, triggers,
   D-pad, face buttons, START/SELECT, PS, and shoulder-swap behavior. Motion and
   controller-touchpad capabilities must not appear.
2. Choose **PS4 + gyro**, reconnect, and verify one DS4. Test pitch, roll, and
   yaw with no drift at rest and the correct direction on all axes.
3. Disable motion and reconnect. Motion must no longer be advertised.
4. Suspend and resume during motion input. The next stream must start with
   fresh sensor state and no stuck buttons or axes.

## 6. Touch, keyboard, and host management

1. Change each touch mode in the overlay: Off, DS4 Touchpad, Absolute mouse,
   and Tablet. Verify the new mode persists and no confirm/cancel press leaks to
   the host when the overlay closes.
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
3. Verify the release contains the VPK, Windows installer, portable host ZIP,
   source archive, licenses, and `THIRD_PARTY_NOTICES.md`.
4. Repeat sections 1 through 4 on a second clean PC using only release
   artifacts. No SDK, .NET runtime, PowerShell module, or manual driver download
   may be required.

## Optional command-line equivalents

For troubleshooting only, open Windows Terminal as Administrator:

```powershell
Set-Location "C:\Program Files\Vita Moonlight Host"
.\VitaMoonlight.Host.exe doctor
.\VitaMoonlight.Host.exe display list
.\VitaMoonlight.Host.exe display disable-virtual
.\VitaMoonlight.Host.exe session test --width 960 --height 544 --fps 60 --seconds 15
.\VitaMoonlight.Host.exe session status
.\VitaMoonlight.Host.exe session recover
.\VitaMoonlight.Host.exe agent status
.\VitaMoonlight.Host.exe emergency recover-display
```
