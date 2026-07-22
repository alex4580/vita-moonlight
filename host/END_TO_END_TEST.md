# End-to-end acceptance test

This is a GUI-first test for a clean Windows 10 or 11 x64 PC and a physical
Vita. No terminal is required for the normal test. Before publishing a release,
record the Windows version, GPU and driver, Sunshine or Apollo version, Vita
model, network type, and VPK commit.

## Before you begin

Keep a physical monitor connected. A screenshot of **Settings > System >
Display** before installation is helpful but optional. If you forgot it, record
which monitor is physical and its normal resolution. For example, a 2560x1440
LG display is the physical baseline; the later 800x600 **VDD by MTT** display is
virtual.

## 1. Install and open the control panel

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator with the
   default Sunshine options. Setup must not ask you to install a private root
   certificate.
2. Reboot if Windows requests it. Setup now installs ViGEmBus before Sunshine
   and restarts Sunshine after configuration.
3. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel**.
   The installer also offers to open it on the final page.
4. Click **Restart as Administrator** if shown.
5. Click **Run diagnostics**. Sunshine, ViGEmBus, the driver bundle, and the
   virtual display must report `OK`, and no display transaction should be
   pending.

If double-clicking `VitaMoonlight.Host.exe` opens and immediately closes a
terminal, that is an old package. Install the package containing this guide;
the executable must open a persistent control-panel window.

## 2. Check and clean up the idle virtual display

1. Click **List displays**. Both the physical display and **VDD by MTT** should
   appear. It is normal for the virtual display to first appear as 800x600.
2. If Windows currently extends the desktop onto that 800x600 screen, click
   **Disable idle virtual display** and confirm. Click **List displays** again;
   the physical display must remain active and the virtual display must be
   inactive or unavailable.
3. Do not disable or disconnect the physical display in Windows Settings.

The button disables the idle device only. It does not uninstall the driver,
and Vita Moonlight will reactivate it when streaming starts.

## 3. Test automatic display switching and recovery

1. In the Administrator control panel, click **Run 15-second 960x544 test**.
2. Approve the warning. The physical monitor may go blank while Windows uses
   only the virtual display at 960x544 and 60 Hz.
3. Wait. The physical display layout must return automatically after 15
   seconds. Click **Session status**; it must say no display transaction is
   pending.
4. Click **Run diagnostics**. Confirm **Recovery task** reports `OK installed`.
   If it does not, click **Install recovery safeguard**, then run diagnostics
   again. This verifies that the independent logon recovery path is registered
   without deliberately stranding the local desktop.
5. Do not terminate the host from Task Manager while the physical display is
   disabled. A forced-interruption test is optional and intended only for a
   dedicated lab PC with an independent control path, such as another computer
   connected through remote administration. It is not part of the normal
   acceptance run.

If a real stream is ever interrupted and the physical layout does not return,
sign out and back in to trigger the recovery task. If the desktop is already
visible, you can instead open the Administrator control panel and click
**Recover previous display layout**.

For Apollo, install or select Apollo, click **Configure Apollo**, restart
Apollo, and repeat the connection test below. Apollo supplies its own virtual
display, so the separate signed Sunshine display driver is not required.

## 4. Pair and stream

1. Click **Configure Sunshine**, then **Restart Sunshine**. Run diagnostics
   again; **Sunshine gamepad** must report `OK ready`.
2. Install the release VPK, pair the Vita with the PC, and launch **Vita
   Moonlight**.
3. Confirm the host switches to 960x544 at 60 Hz and restores the physical
   desktop after each of these cases: normal stream exit, Wi-Fi loss, and Vita
   suspend.
4. Verify a stable 20-minute stream at the default 5000 Kbps. Check video frame
   pacing, audio, reconnect behavior, and the on-screen latency statistics.
5. Repeat at 30 FPS and with a deliberately constrained access point. The
   stream must reduce bandwidth without corrupt frames or leaving the virtual
   display active after disconnect.

## 5. Controller and motion

1. Select **Xbox controller** on the Vita. Steam Big Picture and a gamepad
   tester must see one XInput controller with correct sticks, triggers, D-pad,
   face buttons, START/SELECT, PS button, and shoulder-swap setting. Motion and
   controller-touchpad capabilities must not appear.
2. Select **PS4/automatic** and enable motion controls. Sunshine must expose one
   DS4. Verify pitch, roll, and yaw in a DS4 motion tester, with no movement at
   rest and correct direction on all three axes.
3. Disable motion controls and reconnect. DS4 motion must no longer be
   advertised.
4. Suspend and resume the Vita during motion input. The next stream must start
   with fresh sensor state and no stuck motion.

## 6. Touch, keyboard, and device control

1. In **DS4 Touchpad** mode, test click, drag, and two-finger interaction in a
   DS4-aware title.
2. In **Mouse Absolute** mode, touch all four display corners. The pointer must
   reach the corresponding pixels without offset or acceleration.
3. In **Tablet (Sunshine)** mode, test contact and pressure behavior in a
   compatible Windows application.
4. Press **START + LEFT**. The floating keyboard must type into both a normal
   application and an Administrator application.
5. Test host discovery, manual host entry, pairing, Wake-on-LAN, reconnect, and
   removing a saved host.

## 7. Uninstall and release artifacts

1. End any stream and confirm **Session status** reports no pending display
   transaction.
2. Uninstall Vita Moonlight Host. The recovery task must be removed and the
   physical display layout must remain intact.
3. Verify the release contains the VPK, Windows installer, portable host ZIP,
   source archive, licenses, and `THIRD_PARTY_NOTICES.md`.
4. On a second clean PC, repeat sections 1 through 4 using only the release
   artifacts. No SDK, .NET runtime, PowerShell module, or manual driver download
   may be required.

## Optional command-line equivalents

Only use these for scripting or troubleshooting. Open Windows Terminal as
Administrator and enter:

```powershell
Set-Location "C:\Program Files\Vita Moonlight Host"
.\VitaMoonlight.Host.exe doctor
.\VitaMoonlight.Host.exe display list
.\VitaMoonlight.Host.exe display disable-virtual
.\VitaMoonlight.Host.exe session test --width 960 --height 544 --fps 60 --seconds 15
.\VitaMoonlight.Host.exe session status
.\VitaMoonlight.Host.exe session recover
```
