# Final release evaluation checklist

Use this after `END_TO_END_TEST.md` passes. Do not create the final version tag
until every required gate below is checked or an exception is documented in the
release notes.

## Candidate identity

- [ ] Freeze one commit for the VPK, installer, portable ZIP, source, and test
      results. Record its full SHA below.
- [ ] Confirm `CHANGELOG.md`, package version, installer `AppVersion`, and the
      intended Git tag agree.
- [ ] Download both CI artifacts from that commit; do not combine files from
      different workflow runs.
- [ ] Record SHA-256 hashes for the VPK, installer, and portable ZIP.

| Field | Value |
|---|---|
| Candidate commit | |
| Vita workflow URL | |
| Windows workflow URL | |
| Tester/date | |

## Required hardware acceptance

- [ ] Complete every step in `END_TO_END_TEST.md` on the primary Windows PC
      and a physical Vita.
- [ ] Repeat install, Steam Big Picture launch, normal disconnect, and
      emergency display recovery on a second clean Windows 10 build 19041+ or
      Windows 11 PC.
- [ ] Test both split-token UAC and a standard streaming account that supplies
      a different Administrator credential. After sign-out/sign-in, confirm
      the recovery and rescue tasks run in the intended interactive streaming
      session and that F8-F12 hotkeys are registered there. If alternate-admin
      setup cannot meet this gate, document same-account setup as a release
      requirement rather than claiming multi-user support.
- [ ] Cover both Windows 10 and Windows 11 if testers are available; include a
      laptop/on-battery recovery pass and a multi-monitor recovery pass.
- [ ] Cover at least two GPU/encoder families when testers are available (AMD,
      NVIDIA, or Intel); record GPU and driver versions.
- [ ] Test at least one Vita 1000 and one Vita 2000 when available. If only one
      model is available, state that limitation in the release notes.
- [ ] Run a continuous 60-minute 960x544/60 stream at 8 Mbps with no sustained
      decoder errors, audio drift, stuck input, or unrecovered display state.
- [ ] From an active stream, switch through 960x540, 960x544, and 1280x720.
      Each selection must change the VDD safely, reconnect with matching
      Sunshine encoder dimensions, keep the same Windows game running, and
      retain disconnect/crash recovery.
- [ ] Repeat normal disconnect, Vita suspend, Wi-Fi loss, and reconnect at least
      three times each.
- [ ] On physical Vita hardware, pause host capture without ending the session
      and verify the last-frame watchdog keeps the menu, overlay, and
      diagnostics responsive. Stress rapid menu navigation, repeated vblank
      changes, disconnect during watchdog redraw, and five reconnect cycles
      with no crash, deadlock, torn UI, or stuck input. Record Vita model and
      firmware because Vita2D thread behavior cannot be certified by desktop
      CI.

## Doom Eternal / black-frame triage

- [ ] Reproduce or clear the title-to-menu black-frame report with the exact
      game version, GPU driver, renderer, fullscreen mode, HDR state, and Steam
      launch options recorded.
- [ ] Test borderless SDR first, then exclusive fullscreen and the game's HDR
      option separately. Do not change multiple variables in one run.
- [ ] When the frame turns black, confirm whether the Vita-rendered overlay and
      audio remain active. An intact overlay means the client renderer is alive
      and does not by itself prove a VDD crash.
- [ ] Use **Close Windows game**. The game must exit (forced if necessary), the
      Moonlight session must stay connected, and Steam Big Picture must return.
- [ ] Use **End Sunshine app** in a separate run. The stream and Sunshine app
      session must end and the physical display must return.
- [ ] Use **Recover host display** in a separate run. Within roughly ten
      seconds LG/another physical monitor must be active, VDD inactive,
      Sunshine running, and the rescue status successful.
- [ ] Attach `%ProgramFiles%\Sunshine\config\sunshine.log`,
      `%ProgramData%\VitaMoonlight\stream-rescue.log`, the optional Vita
      diagnostic log, and exact timestamps to any remaining black-frame issue.

## Input and usability gates

- [ ] Maximum compatibility reports one Xbox/XInput controller, passes a
      Windows/Steam gamepad tester and at least two games, and restores
      Relative mouse, Local double-tap, gyro/mapping/shoulder-swap/sprint off.
- [ ] Steam / DS4 + gyro reports one DS4 with DS4 Touchpad and Safe PC Guide,
      correct motion axes, sensible units, no drift at rest, and clean state
      after suspend/reconnect. Real-time diagnostics confirms host gyro request
      and increasing sample events.
- [ ] All four touch modes and the floating keyboard pass from both
      **START + Left** and **Open on-screen keyboard** on the in-stream menu.
- [ ] Default Local double-tap sends no single-PS event, keeps paused PC media
      paused, and double PS always returns to LiveArea. Safe Guide, Immediate
      Guide, and System / LiveArea match the documented behavior; START + L +
      R opens the overlay without leaking input.
- [ ] Every destructive overlay action requires the second **X** confirmation,
      and **O** cancels it.
- [ ] The pre-stream Settings screen and the in-stream Stream/Input pages show
      identical values for their shared controls; negotiated changes apply
      only after the documented controlled reconnect.
- [ ] Performance overlay Off, Frame rate, Frame rate + network, and Advanced
      all pass. Enabled modes stay top-right with a 50%-alpha background and
      display the documented metrics; Off leaves no overlay.
- [ ] Real-time diagnostics is a dedicated screen with live stream, network,
      decoder, controller, and gyro state. No inline input-diagnostic line
      remains in the normal session menu.
- [ ] Diagnostic file logging is Off on first run and does not append during a
      normal Off-mode stream. Triangle and the Settings toggle both enable and
      disable it; the in-stream menu shows and changes the same state; a
      reproduction appends to the path shown on-screen and the closed file
      copies successfully with VitaShell.
- [ ] A non-technical tester completes install, health check, pairing, normal
      play, game close, and recovery using only the GUI documentation.
- [ ] A first-run Vita shows Recommended 960x544/60/8 Mbps, H.264 Rec. 709
      limited-range SDR, 1024-byte packets, packet-loss recovery, frame pacing,
      fit scaling, vblank off, Maximum compatibility input, performance overlay
      Off, and diagnostic logging Off.
- [ ] Reliable, Recommended, High quality, Remote / VPN, and Custom preset
      detection pass. Each named preset restores its complete documented
      stream path, and Reset all to recommended also restores input, overlay,
      and logging defaults.

## Host safety and packaging gates

- [ ] A clean install includes Sunshine 2026.516.143833 or newer, ViGEmBus,
      Microsoft Visual C++ runtime 14.44.35211.0 or newer, the pinned signed VDD,
      recovery task, and running stream-rescue agent without manual downloads.
- [ ] Corrupt or stop each prerequisite in a disposable test image. Installer
      and **Apply recommended setup** must repair Sunshine, ViGEmBus, the Visual
      C++ runtime, and the VDD or stop with an actionable error. Simulate a
      3010/reboot-required result and verify no Sunshine display configuration
      or active-display change runs until after the reboot and the user resumes
      from the GUI.
- [ ] Repeat setup with an already-enabled VDD on Windows 11. PnPUtil error 50
      is accepted only for enable-device, native 960x544 is still verified, and
      any genuine failure is shown with the host error rather than only an exit
      code.
- [ ] Upgrade over 0.14.2 or 0.14.3 with the existing `ROOT\MttVDD` device and
      modified `C:\VirtualDisplayDriver\vdd_settings.xml`. Package staging
      happens before managed-mode normalization; unrelated resolutions/options
      remain, local/global duplicate effective modes are removed, and
      960x544/60 is verified using a non-persistent live mode change.
- [ ] During an in-place VDD repair, delay display-target re-enumeration.
      Verification must wait up to 30 seconds for the existing target before
      changing topology, keep every physical display active while waiting, and
      continue automatically when the target appears.
- [ ] During install and repair verification, every active physical display
      remains in the applied topology while VDD is added as an extended
      display and its GDI mode list is re-enumerated. Success and failure both
      restore the exact original topology once; a rejected native mode reports
      its final Windows error and does not enter a restart loop.
- [ ] The installer and portable companion reject Windows Server, ARM64, x86,
      and Windows builds older than 19041 before setup actions. `doctor` must
      report the unsupported platform and remain non-mutating.
- [ ] Upgrade install preserves Sunshine credentials and unrelated app
      commands, upgrades an older Sunshine build without installing a
      duplicate, and removes obsolete Vita prep hooks.
- [ ] Idle state is physical display active / VDD inactive. Normal disconnect
      and emergency recovery both return to that state.
- [ ] The close-game agent refuses Steam, Sunshine, Explorer, the companion,
      and critical Windows processes; a disposable uncooperative app is
      force-terminated successfully.
- [ ] Uninstall removes both scheduled tasks and the background agent and does
      not leave the physical display disabled.
- [ ] A forced nonzero `session recover` result aborts uninstall before the
      companion, rescue agent, or recovery task is removed; uninstall succeeds
      after recovery is restored.
- [ ] Installer and portable ZIP contain the same companion build, current
      guides (including compatibility and Vita tuning), licenses, and
      `THIRD_PARTY_NOTICES.md`.
- [ ] Scan release assets with Microsoft Defender and VirusTotal or document
      why an external scan was not used.
- [ ] Decide the Authenticode policy. For a polished public release, configure
      `WINDOWS_CERTIFICATE_BASE64` and `WINDOWS_CERTIFICATE_PASSWORD` repository
      secrets and verify signatures on the companion and installer. The VDD's
      existing publisher signature is not a signature for this project's EXEs.

## GitHub and release gate

- [ ] Draft PR has no unresolved blocking review comments.
- [ ] Vita and Windows workflows pass on the frozen commit.
- [ ] PR is merged into the `vita` release branch with the tested commit
      ancestry intact.
- [ ] Create and push `v0.14.6` only after the hardware and safety gates pass.
      The release workflow builds both platforms and publishes their artifacts.
- [ ] Download the published release, verify hashes/signatures again, and run a
      short install/pair/stream/disconnect smoke test from those public assets.
- [ ] Publish known limitations, tested hardware, unsigned-binary status if
      applicable, upgrade instructions, and rollback instructions.

## Sign-off

| Area | Tester | Result | Evidence / issue |
|---|---|---|---|
| Clean install and GUI | | | |
| Steam / Doom Eternal | | | |
| Display lifecycle | | | |
| Game force-close | | | |
| Emergency VDD recovery | | | |
| Maximum compatibility input | | | |
| Steam / DS4 + gyro | | | |
| Overlay / diagnostics / logging | | | |
| Touch / keyboard | | | |
| Long stream / quality | | | |
| Upgrade / uninstall | | | |
| Packaging / signatures | | | |

Final decision: **GO / NO-GO**

Approved by:

Date:
