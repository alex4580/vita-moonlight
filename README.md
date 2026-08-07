# Vita Moonlight

Stream games from a Windows PC to a PlayStation Vita. This release includes:

- `moonlight.vpk` for the Vita;
- an all-in-one Windows installer for the host control panel, Sunshine,
  controller support, display recovery, and a Vita-sized virtual display; and
- sensible first-run settings: 960x544, 60 FPS, H.264 SDR, and 8 Mbps.

## Before you install

You need:

- a homebrew-enabled PlayStation Vita or PlayStation TV with VitaShell;
- a 64-bit Intel or AMD PC running Windows 10 build 19041 or newer, or
  Windows 11; and
- the Windows installer and VPK from the **same** entry on the
  [Releases page](../../releases).

Save your work before setup. The display may briefly blink while Windows
checks the virtual display.

Publisher and data-handling details are in the
[Code signing policy](docs/CODE_SIGNING_POLICY.md)
and [Privacy policy](PRIVACY.md).
For Windows releases whose Authenticode signatures identify SignPath
Foundation: **Free code signing provided by SignPath.io, certificate by
SignPath Foundation.** Unsigned previews, if any, are labeled explicitly and
are not covered by that statement.

The exact `v0.14.8-beta.1` preview is built and published by GitHub Actions
without Authenticode. Windows will show **Unknown publisher** and may display a
Microsoft Defender SmartScreen warning. Check the warning on the release page,
verify `SHA256SUMS` and the GitHub provenance attestations, and confirm
`unsigned-beta-preview` in `windows-signing-status.json` before running it.
Every other beta, release-candidate, and stable tag remains signing-required.

## Install for the first time

1. On the PC, run `Vita-Moonlight-Host-Setup-win-x64.exe` and accept the
   recommended components. For the explicitly unsigned `v0.14.8-beta.1`
   preview, verify its checksum and provenance first; if SmartScreen appears, choose
   **More info > Run anyway** only for the verified GitHub download.
2. Restart Windows if setup asks you to. Open **Vita Moonlight Host** from the
   Start menu, choose **Restart as Administrator** if offered, and click
   **Set up or repair this PC**.
3. Click **Check readiness**. Follow any action shown until the host reports
   that it is ready.
4. Copy `moonlight.vpk` to the Vita with VitaShell USB or FTP, select the file
   in VitaShell, and install it.
5. Start Vita Moonlight. Select the PC, enter the displayed PIN in Sunshine's
   web page when asked, and launch **Steam Big Picture** or **Desktop**. Pairing
   also pins that PC's Sunshine identity so later HTTPS control requests fail
   safely if the host certificate changes unexpectedly.

On Sunshine's first launch, its web page may ask you to create a username and
password. Those are local Sunshine administration credentials; the short PIN
shown later on the Vita is a separate one-time pairing code.

The virtual display is normally inactive when you are not streaming. During a
stream, a physical PC monitor may go blank while Sunshine captures the
Vita-sized display. It should return automatically after disconnecting.

## Use it

- Open the in-stream menu: hold **START**, then press **L + R** within one
  second. The shortcut stays on the Vita and is not sent to the PC.
- Open the Vita keyboard: hold **START**, then press **D-pad Left** within one
  second, or choose
  **Open on-screen keyboard** from the in-stream menu.
- End normally: choose **Disconnect stream** from the menu.
- Return to LiveArea if the stream is stuck: double-press **PS**.
- Recover the PC display without the control panel: press
  **Ctrl + Alt + Shift + F11** on a keyboard connected to the PC.
- When you will not use Vita streaming for a while, open the Windows control
  panel as Administrator and choose **Pause Vita host features**. Disconnect
  the Vita first. This restores
  the physical desktop and turns off Vita Moonlight's background safeguards
  and managed virtual-display device. Sunshine and any pre-existing Apollo
  installation remain installed and reachable; this beta configures Sunshine
  only. A client pinned to the disabled Vita virtual display may
  need Vita host features enabled again or its host output changed to a
  physical display. Pairing, settings, and installed shared components are
  kept. Choose
  **Enable Vita host features** before the next Vita session. Streaming servers
  remain reachable while paused, so Pause is not a network-access block.
  A complete Pause removes the rescue agent, so **Ctrl + Alt + Shift + F11** is
  not registered again until Vita host features are enabled.

Start with the **Recommended** preset. See the
[Vita settings guide](docs/VITA_SETTINGS_GUIDE.md) before changing bitrate,
resolution, controller, gyro, touch, or PS-button behavior.

## Update or repair

Download a matching installer and VPK from one release. End any stream, run
the installer over the existing Windows installation, then open the control
panel as Administrator and click **Set up or repair this PC**. Install the new
VPK over the old Vita app.

Running the same installer again is also the normal repair procedure. Your
Sunshine credentials and unrelated Sunshine applications should be preserved.
If Windows requests a restart, restart and run **Set up or repair this PC**
again.

An upgrade or same-version reinstall preserves an intentional paused state;
it does not silently turn Vita host features back on. Open the updated control
panel and choose **Enable Vita host features** when you are ready to use them.
Setup applies controller/runtime repairs which are safe while paused and saves
any selected host or virtual-display repair as a protected plan. That plan runs
automatically during Enable; if it needs a restart or fails, host features
return to Paused and the plan remains available for a safe retry.

Installing the same VPK over the current Vita app is also safe. Saved hosts and
settings should remain. The first VPK in this security-hardened line asks an
existing paired PC to **Pair securely (one-time upgrade)** because older Vita
builds did not save a verifiable Sunshine certificate pin. Enter the new PIN in
Sunshine once; this is expected and does not assume a clean install.

## Uninstall

1. End the stream and confirm that the physical PC display is visible.
2. Open **Windows Settings > Apps > Installed apps** (Windows 11) or
   **Apps & features** (Windows 10), find **Vita Moonlight Host**, and choose
   **Uninstall**.
3. Keep Sunshine, ViGEmBus, and the virtual-display driver unless you are sure
   no other streaming or controller software uses them. They are shared
   components and are kept by default.
4. To remove the Vita app, highlight it in LiveArea, press **Triangle**, and
   choose **Delete**.

The Windows uninstaller works from either the enabled or paused state. It
restores and verifies a physical display before removing recovery safeguards,
removes Vita-owned settings and legacy state, and leaves shared components
that you keep in place. If it asks for a restart,
restart Windows and run the uninstaller again.

Deleting the Vita app may leave its external settings and support-log files so
they can survive a reinstall. Use VitaShell to review or back up those files;
do not delete a parent storage directory merely to remove a log.

## Get help

| Problem | What to do |
|---|---|
| A PC disappeared after pairing failed or was cancelled | Return to the main screen and look under **Saved computers**. Select the entry marked **Pairing required** and retry. A successful pairing must appear there immediately; if an older build is installed, fully close and reopen Vita Moonlight once, then install the current VPK. |
| Physical display did not return | While Vita host features are enabled, press **Ctrl + Alt + Shift + F11** on the PC keyboard. During a complete Pause the shortcut is intentionally unavailable; if Windows is visible, reopen the control panel as Administrator and use **Display & recovery > Restore physical display now**. |
| Vita shows the physical monitor, 800x600, or a washed-out image | Open the control panel as Administrator, run **Set up or repair this PC**, then **Check readiness**. Confirm every-application switching and Force SDR are enabled. |
| Sunshine says ViGEmBus is missing | Run **Diagnostics & support > Run full health check**, use **Repair controller support**, and restart Sunshine. |
| A Windows game is black but the Vita menu still opens | Use **Open Windows Task Manager** from the in-stream menu, select the bad process, and end it deliberately. **End Sunshine app** stops Sunshine's active app; use **Recover host display** only for a display problem. |
| Motion is blocky or the stream stalls | Try the **Reliable** preset. Start a support log only for one short reproduction. |

**Forget on this Vita** removes the local saved computer and its pinned
credentials. It does not delete Sunshine's authorized-client entry; revoke
that separately in Sunshine only when you intend to remove the Vita there too.

For detailed Windows recovery and advanced setup, see the
[Windows host guide](host/README.md).

## Help test the beta

Community testing is welcome, including first installs, upgrades, repairs,
laptops, and multi-monitor PCs:

- [How to test and report results](docs/COMMUNITY_TESTING.md)
- [Minimum beta test](host/BETA_SMOKE_TEST.md)
- [Full end-to-end test](host/END_TO_END_TEST.md)
- [Logging and support guide](docs/LOGGING_AND_SUPPORT.md)

Support logging is **off by default**. During normal play, Vita Moonlight does
not open, create, or write a support-log file.

## Build or fork the project

Developers can use [Building and forking](docs/BUILDING.md) and
[Releasing a fork](docs/RELEASING.md). Supported Windows versions and known
hardware limits are listed in the [compatibility guide](host/COMPATIBILITY.md).
Third-party licenses and versions are listed in
[third-party notices](host/THIRD_PARTY_NOTICES.md).
