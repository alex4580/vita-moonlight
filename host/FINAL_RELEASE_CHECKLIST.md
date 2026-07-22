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
      emergency display recovery on a second clean Windows 10/11 PC.
- [ ] Cover at least two GPU/encoder families when testers are available (AMD,
      NVIDIA, or Intel); record GPU and driver versions.
- [ ] Test at least one Vita 1000 and one Vita 2000 when available. If only one
      model is available, state that limitation in the release notes.
- [ ] Run a continuous 60-minute 960x544/60 stream at 8 Mbps with no sustained
      decoder errors, audio drift, stuck input, or unrecovered display state.
- [ ] Repeat normal disconnect, Vita suspend, Wi-Fi loss, and reconnect at least
      three times each.

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
- [ ] Use **Recover display + Sunshine** in a separate run. Within roughly ten
      seconds LG/another physical monitor must be active, VDD inactive,
      Sunshine running, and the rescue status successful.
- [ ] Attach `%ProgramFiles%\Sunshine\config\sunshine.log`,
      `%ProgramData%\VitaMoonlight\stream-rescue.log`, the Vita debug log, and
      exact timestamps to any remaining black-frame issue.

## Input and usability gates

- [ ] Xbox mode passes a Windows/Steam gamepad tester and at least two games.
- [ ] PS4 + gyro reports one DS4 with correct axes, sensible units, no drift at
      rest, and clean state after suspend/reconnect.
- [ ] All four touch modes and the floating keyboard pass.
- [ ] Single PS reaches the host; double PS always returns to LiveArea; START +
      L + R always opens the overlay without leaking input.
- [ ] Every destructive overlay action requires the second **X** confirmation,
      and **O** cancels it.
- [ ] A non-technical tester completes install, health check, pairing, normal
      play, game close, and recovery using only the GUI documentation.

## Host safety and packaging gates

- [ ] A clean install includes Sunshine, ViGEmBus, the pinned signed VDD,
      recovery task, and running stream-rescue agent without manual downloads.
- [ ] Upgrade install preserves Sunshine credentials and unrelated app commands
      while removing obsolete Vita prep hooks.
- [ ] Idle state is physical display active / VDD inactive. Normal disconnect
      and emergency recovery both return to that state.
- [ ] The close-game agent refuses Steam, Sunshine, Explorer, the companion,
      and critical Windows processes; a disposable uncooperative app is
      force-terminated successfully.
- [ ] Uninstall removes both scheduled tasks and the background agent and does
      not leave the physical display disabled.
- [ ] Installer and portable ZIP contain the same companion build, current
      guides, licenses, and `THIRD_PARTY_NOTICES.md`.
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
- [ ] Create and push `v0.14.0` only after the hardware and safety gates pass.
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
| Xbox input | | | |
| PS4 + gyro | | | |
| Touch / keyboard | | | |
| Long stream / quality | | | |
| Upgrade / uninstall | | | |
| Packaging / signatures | | | |

Final decision: **GO / NO-GO**

Approved by:

Date:
