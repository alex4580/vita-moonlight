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
- [ ] Record SHA-256 hashes for the VPK, Vita source archive, installer, and
      portable ZIP.

| Field | Value |
|---|---|
| Candidate commit | |
| Vita workflow URL | |
| Windows workflow URL | |
| Tester/date | |

## Required hardware acceptance

- [ ] Complete every step in `END_TO_END_TEST.md` on the primary Windows PC
      and a physical Vita.
- [ ] Aggregate named results for a genuine clean install, in-place upgrade
      from an older release, same-version reinstall/repair, uninstall while
      keeping shared dependencies, and full dependency removal on a disposable
      PC or snapshot. Do not infer one path from another.
- [ ] Install the candidate VPK over itself, remove it from LiveArea, and
      reinstall it. Confirm both preserved external settings and a fresh
      configuration start safely; never delete a parent Vita storage directory
      as cleanup.
- [ ] Repeat install, Steam Big Picture launch, normal disconnect, and
      emergency display recovery on a second clean Windows 10 build 19041+ or
      Windows 11 PC.
- [ ] Test split-token UAC from the intended Administrator streaming account.
      Confirm both tasks use the interactive token, highest run level, and the
      exact installed executable/arguments. F11 must register. On the supported
      authenticated handoff path F8-F10 must remain unregistered and absent
      from readiness requirements.
- [ ] From a standard streaming account, supply a different Administrator at
      setup UAC. Setup must show the unsupported-account explanation and stop
      before display, task, or file mutation. Then verify that an uninstall
      approved by that Administrator still succeeds after exact task-action
      ownership verification. Do not claim alternate-admin setup support.
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
      three times each. Include an immediate reconnect to the same running
      Sunshine application so `/resume`, not only a fresh launch, is covered.
      A delayed stop from an earlier generation must never tear down the newer
      stream.
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
- [ ] Use **Open Windows Task Manager**, explicitly end the game, and confirm
      the Moonlight session can remain connected and Steam Big Picture returns.
      Vita Moonlight must not choose or kill a foreground process itself.
- [ ] Use **End Sunshine app** in a separate run. The stream and Sunshine app
      session must end and the physical display must return.
- [ ] Use **Recover host display** in a separate run. Within roughly ten
      seconds at least one connected physical monitor must be active, VDD
      PnP-disabled, Sunshine running, and the rescue status successful.
- [ ] Attach
      `%ProgramFiles%\Vita Moonlight Host\state\Diagnostics\stream-rescue.log`,
      the optional Vita support log, the Windows host support JSON, and exact
      timestamps/time zones to any remaining black-frame issue. Include
      `%ProgramFiles%\Sunshine\config\sunshine.log` only when Sunshine logging
      was already enabled for that focused reproduction; the supported host
      does not force INFO logging or tail that file.

## Input and usability gates

- [ ] Maximum compatibility reports one Xbox/XInput controller, passes a
      Windows/Steam gamepad tester and at least two games, and restores
      Relative mouse, Local double-tap, gyro/mapping/shoulder-swap/sprint off.
- [ ] Steam / DS4 + gyro reports one DS4 with DS4 Touchpad and Safe PC Guide,
      correct motion axes, sensible units, no drift at rest, and clean state
      after suspend/reconnect. Real-time diagnostics confirms host gyro request
      and increasing sample events. Vita-side horizontal and vertical
      sensitivity each scale the corresponding axis across the documented
      0.1x-5.0x range, while Steam Input remains available for per-game
      refinement.
- [ ] All four touch modes and the floating keyboard pass from both
      **SELECT first + Left** and **Open on-screen keyboard** on the in-stream
      menu. Mapped corner taps fire once; drags, holds, and multitouch pass
      through without a zone action.
- [ ] Default Local double-tap sends no single-PS event, keeps paused PC media
      paused, and double PS always returns to LiveArea. Safe Guide, Immediate
      Guide, and System / LiveArea match the documented behavior; ordinary
      START reaches the PC, while SELECT first + L + R opens the overlay
      without leaking input.
- [ ] Every destructive overlay action requires a second press of the
      configured Confirm button, and the configured Cancel button backs out.
      The on-screen X/O hints must follow **Swap X and O in Moonlight**.
- [ ] The pre-stream Settings screen and the in-stream Stream/Input pages show
      identical values for their shared controls; negotiated changes apply
      only after the documented controlled reconnect.
- [ ] Performance overlay Off, Frame rate, Frame rate + network, and Advanced
      all pass. Enabled modes stay top-right with a 50%-alpha background and
      display the documented metrics; Off leaves no overlay.
- [ ] Real-time diagnostics is a dedicated screen with live stream, network,
      decoder, controller, and gyro state. No inline input-diagnostic line
      remains in the normal session menu.
- [ ] Support log is Not capturing on first run and does not open, create, or
      write a support-log file during normal play.
      **Settings > System and support > Start support log**, the
      in-stream **Start support log**, and Triangle on Real-time diagnostics
      begin the same fresh capture. **Stop and save support log** and Triangle
      close it. A second Start rotates the former `moonlight.log` to the single
      `moonlight.previous.log` and creates a fresh current file.
- [ ] A captured `vita-support-v1` log has ordered session/sequence fields,
      initial system/configuration/connection/stream snapshots, structured
      connection stages, stream actions, decoder state, network state and
      10-second summaries, gyro state changes, rate-limited legacy error
      categories, and a final session summary. It contains no per-touch,
      per-button, typed-character, or per-gyro-sample flood.
- [ ] Run `tools\summarize-vita-log.py --self-test`, normal summary, and
      `--json` summary. The packaged copy under `tools\SupportLog` behaves the
      same, groups capture sessions correctly, and never reproduces raw legacy
      text, unknown values, arbitrary filenames, or local paths.
- [ ] Run `tools\check-windows-upgrade-contract.py`. It must accept every
      recorded legacy host fixture, reject current-only pre-replacement
      commands, and confirm that helper-owned physical recovery precedes the
      protected installer-maintenance snapshot.
- [ ] **Diagnostics & support > Save support report...** creates a readable
      point-in-time JSON report with host version, platform, prerequisite,
      display, lifecycle, recovery, and rescue state. It runs only on request,
      works from installed and diagnostics-only portable packages, and does
      not enable a continuous Windows host logger.
- [ ] A non-technical tester completes install, **Check readiness**, pairing,
      normal play, game close, and recovery using only the GUI documentation.
- [ ] A first-run Vita shows Recommended 960x544/60/8 Mbps, H.264 Rec. 709
      limited-range SDR, 1024-byte packets, packet-loss recovery, immediate
      presentation, fit scaling, vblank off, Maximum compatibility input,
      performance overlay Off, and support log Not capturing.
- [ ] Reliable, Recommended, High quality, Remote / VPN, and Custom preset
      detection pass. Each named preset restores its complete documented
      stream path, and Restore recommended defaults also restores input,
      overlay, and support-log defaults.

## Host safety and packaging gates

- [ ] A clean install includes Sunshine 2026.516.143833 or newer, ViGEmBus,
      Microsoft Visual C++ runtime 14.44.35211.0 or newer, the pinned signed VDD,
      recovery task, and running stream-rescue agent without manual downloads.
- [ ] Readiness proves the authenticated Vita/host boundary is listening before
      the rescue agent reports ready. Startup must first complete a local
      Schannel handshake with the exact Sunshine certificate; a PEM-backed
      ephemeral key or unusable temporary key container must fail readiness
      without opening the public listener. Expired or orphaned display recovery
      must run before TLS credential loading so a credential failure cannot
      strand the VDD. A paired Vita certificate prepares both a fresh launch
      and same-app resume; an unknown, disabled, expired, or
      malformed client identity is rejected without enabling the VDD or writing
      a recovery transaction. The endpoint exposes no unauthenticated status or
      display action.
- [ ] Exercise generation ownership directly: a valid prepare returns one
      generation, its matching stop restores the exact physical baseline and
      PnP-disables the VDD, a duplicate stop is harmless, and an older stop
      cannot tear down a later prepared generation. Timeout and malformed-
      request paths are bounded and fail to a visible physical desktop.
- [ ] Corrupt or stop each prerequisite in a disposable test image. Installer
      and **Get started > Set up or repair this PC** must repair Sunshine,
      ViGEmBus, the Visual
      C++ runtime, and the VDD or stop with an actionable error. Simulate a
      3010/reboot-required result and verify no Sunshine display configuration
      or active-display change runs until after the reboot and the user resumes
      from the GUI.
- [ ] Repeat setup with an already-enabled VDD on Windows 11. PnPUtil error 50
      is accepted only for enable-device, native 960x544 is still verified, and
      any genuine failure is shown with the host error rather than only an exit
      code.
- [ ] With exactly one unjournaled `ROOT\MttVDD` present, run clean install,
      same-version repair, and older-version upgrade. Protected maintenance
      must leave the device unchanged, setup must ask a separate adoption
      question before copying files, **No** must preserve it, and **Yes** must
      record its exact enabled baseline. Silent setup must fail closed unless
      `/ADOPTEXISTINGVDD` is supplied. More than one present candidate must
      always fail without adopting or changing any device.
- [ ] Upgrade over 0.14.2 or 0.14.3 with the existing `ROOT\MttVDD` device and
      an unpinned `C:\VirtualDisplayDriver` containing a marker. Setup renames
      that entry by no-follow handle, never imports its contents, creates a
      protected replacement atomically, persists its Windows file identity,
      and prints the retained quarantine path. On the next repair, modifications
      to the now-pinned configuration are preserved, local/global duplicate
      effective modes are removed, and 960x544/60 is verified using a
      non-persistent live mode change.
- [ ] In a disposable VM, separately precreate the fixed path as a directory,
      file, and directory reparse point. Hold a WRITE_DAC-only handle during
      repair. Each old entry is detached rather than secured in place and no
      reparse target is read. Hold a non-delete-sharing data handle and race a
      replacement into the fixed name; setup must fail closed with restart and
      repair guidance. Driver reload must refuse a directory whose persisted
      volume/file identity no longer matches.
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
      and Windows builds older than 19041. Portable mode is diagnostics-only;
      all elevation, setup, display mutation, and task-install controls remain
      unavailable. `doctor` remains non-mutating.
- [ ] Upgrade install preserves Sunshine credentials and unrelated app
      commands, upgrades an older Sunshine build without installing a
      duplicate, removes obsolete Vita prep hooks, and does not install a new
      global hook as the display-lifecycle authority.
- [ ] Upgrade from a release through 0.14.6 removes only the immutable allowlist
      of obsolete root documentation and retired
      `C:\ProgramData\VitaMoonlight` state. A planted unknown file is retained,
      reparse/busy entries are not followed or broadly deleted, and an
      incomplete cleanup safely retries rather than recording completion.
- [ ] Healthy Enabled + Idle is an exact physical layout, no pending recovery
      transaction, and the exact managed VDD PnP-disabled. Normal disconnect,
      startup reconciliation, emergency recovery, and uninstall all return to
      that state without manual display selection.
- [ ] With no stream active, arrange several windows at recorded sizes and
      positions. Turn the physical monitor off or let it enter power-save
      **before** putting Windows to sleep, then resume Windows three times.
      Within 20 seconds after each wake, the physical desktop must be active at
      its saved mode, every window must retain its size/position, the managed
      VDD must be PnP-disabled, and desktop animation/input must be normally
      responsive without a restart. The bounded resume record may contain only
      a structured non-fatal mode warning when the physical layout is correct.
- [ ] With a disposable stream active, repeat sleep/resume three times after
      allowing the Vita virtual display to become primary. Do not require a
      physical-only topology before initiating sleep. After wake, verify the
      `power-suspend-display-prepare` record and confirm within 20 seconds that
      the physical desktop wins, the VDD is PnP-disabled, and no window remains
      at a 960x544 or 800x600 fallback mode. Reconnect afterward and prove the
      authenticated resume preflight can arm the device again.
- [ ] Start **Test Vita display for 15 seconds** in one process and initiate
      Windows sleep while that process owns the display transaction. Vary the
      timing across three attempts. The durable suspend intent must prevent a
      post-notification VDD commit; after wake the physical saved mode must win,
      VDD must be PnP-disabled, and the intent must clear without permanently
      blocking the next session.
- [ ] Choose **Pause Vita host features** during an enabled installation, restart
      Windows, and run an in-place upgrade/repair. The recovery tasks, rescue
      agent, and managed VDD device must remain paused while pairing, settings,
      ownership state, Sunshine state, and shared installations remain intact.
      The UI must state that F11 is unavailable during a complete Pause and
      that clients pinned to the Vita VDD may require a physical host output.
      Choose **Enable Vita host features** and verify Sunshine remains unchanged and only the previously present Vita
      safeguards and managed VDD are restored. Repeat both actions to prove
      they are idempotent.
- [ ] Source/contract checks prove the host has no foreground-process close or
      kill action and no F12 rescue shortcut. **Open Windows Task Manager** must
      send only Ctrl+Shift+Esc through Moonlight; **End Sunshine app** must use
      the authenticated GameStream quit-app operation.
- [ ] Uninstall removes both scheduled tasks and the background agent and does
      not leave the physical display disabled. It also removes the authenticated
      boundary listener/firewall ownership. It removes an exact app-created VDD
      node, restores an exact adopted node's recorded enabled baseline, and
      leaves an unproven node untouched. Default and silent uninstall keep
      shared Sunshine, ViGEmBus, and the VDD package.

- [ ] With a captured HDMI/DP audio endpoint deliberately unplugged, Pause
      still removes every background task while retaining only inert retry
      state. Uninstall makes a bounded attempt, reports a warning, keeps the
      current Windows default, and completes without guessing another output.
- [ ] Run uninstall once from an enabled backend and once from an intentionally
      paused backend. Cover default shared-dependency retention and explicit
      Sunshine / ViGEmBus removal in disposable snapshots; exact display
      release is mandatory and ownership-scoped in both paths. A forced late finalization failure must
      restore the exact pre-uninstall paused device state and any
      safeguard removed earlier; a retry must complete idempotently. Unknown
      files placed in the state directory must be retained and reported, never
      followed or recursively deleted.
- [ ] Uninstall removes Vita-managed Sunshine integration and all host state.
      Its optional diagnostic choice preserves only the stream-rescue log.
- [ ] Exercise the protected Sunshine ownership journal on an in-place upgrade:
      unchanged managed values return to their recorded originals, newly added
      values are removed, a value edited after setup is preserved, exact
      Vita-owned hooks are removed without touching unrelated hooks, and the
      journal is deleted only after every integration-cleanup location
      succeeds. Record the pre-journal-upgrade baseline limitation.
- [ ] With a disposable standard account, attempt to redirect machine state or
      a recorded Sunshine path through an environment override, junction, or
      other reparse point. Elevated uninstall must ignore the override, refuse
      the reparse path, and retain the host and safeguards without deleting an
      attacker-selected target.
- [ ] A forced nonzero `uninstall prepare --begin` result aborts uninstall
      before the companion, rescue agent, recovery task, or shared dependency
      is removed and deliberately retains the durable guard. Confirm that guard
      blocks new streams/lifecycle changes and that a later uninstall retry
      safely takes it over. Interrupt a later optional-removal step and confirm
      the same retry behavior. A successful retry removes the exact guard only
      after the host process exits.
- [ ] On a disposable VM, interrupt uninstall at both final commit boundaries.
      Before the finalized marker, the primary host and safeguards remain and
      retry repeats finalization. After the exact finalized marker and host
      deletion, retry completes file-only cleanup and removes the guard. A torn
      marker with the host present is repaired and re-finalized; a torn marker
      with the host missing fails closed until the same-version host is restored.
- [ ] Interrupt upgrade/repair after its protected maintenance records are
      published. A repair retry may take over only after the exact recorded
      owner is dead. Repeat and uninstall directly: uninstall must atomically
      bridge the dead maintenance fence into its durable uninstall guard. A
      live or unverifiable owner must never be displaced.
- [ ] Interrupt a clean install after its protected maintenance records are
      published and retry once with host configuration selected and once with
      it cleared. Only the selected recovery tasks may remain, the physical
      desktop must be active, and the exact maintenance records must clear.
- [ ] With a sole pre-existing MTT device on a disposable snapshot, interrupt
      first setup after maintenance begins but before product copy. Setup must
      not adopt, disable, or journal that device before copy. Complete a later
      install with the disclosed Sunshine/display task selected, then prove
      normal uninstall restores the exact pre-install enabled state.
      Repeat once for initially enabled and once for initially disabled.
- [ ] Interrupt upgrade once after each pre-existing recovery task is removed,
      then retry with host configuration deselected. The durable pre-mutation
      snapshot—not current task absence—must restore exactly the safeguards
      that belonged to the enabled installation before the maintenance fence
      can clear. A persisted Paused intent must never recreate them.
- [ ] Upgrade an older host after deleting only the exact managed Vita firewall
      rule. Pre-copy shutdown and cancel rollback must use the current embedded
      helper, not execute the older host; neither path may report `0x80070002`.
      Verify both task principals, the exact Program Files action, the managed
      firewall rule, and the running authenticated agent before handoff.
- [ ] Hold `VitaMoonlight.Host.exe` open without delete sharing during an
      otherwise successful keep-dependencies uninstall. Host deletion failure
      must retain the exact finalized guard. Release the handle, rename the
      host to simulate post-finalize deletion, and retry: exact Finalized plus
      missing host resumes file-only cleanup. Missing host plus torn/InProgress
      must fail closed. Unknown fixture files must be retained.
- [ ] In a disposable VM, explicitly selected Sunshine and ViGEmBus removals
      plus the mandatory exact Vita display release succeed or accurately
      request a reboot. A pending reboot keeps
      the host and safeguards until uninstall is rerun and verifies cleanup.
      Default `/VERYSILENT` uninstall does not remove dependencies; the
      explicit dependency switches do.
- [ ] Cover both exact display ownership kinds. Releasing an app-created node
      removes that device; releasing an adopted node restores its recorded
      enabled baseline. The verified MTT driver package remains staged in both
      cases, and no unrelated display node or package is changed.
- [ ] Installer and portable ZIP contain the same companion build, simple root
      README, `host` and `docs` guide trees (including community testing,
      logging/support, compatibility, Vita tuning, building, and releasing),
      the packaged support-log summarizer, the Windows host notices, the Vita
      `THIRD_PARTY_NOTICES.txt` index, and every exact license under
      `licenses/vita`. Every packaged relative documentation link resolves.
- [ ] Inspect the VPK contents. It contains `licenses/THIRD_PARTY_NOTICES.txt`,
      the project GPL, h264bitstream LGPL, Mononoki OFL, and every exact license
      named in the notice index. No `nerdfont.ttf` or former `mdnsniff`
      submodule payload is present.
- [ ] Run `tools/check-vita-mdns-parser.py`,
      `tools/stage-vita-dependency-sources.py --self-test`,
      `tools/stage-vita-mbedtls.py --self-test`,
      `tools/build-vita-source-bundle.py --self-test`, and
      `tools/check-release-contract.py` from a clean recursive checkout. All
      pass.
- [ ] The Vita workflow stages every locked official dependency source, builds
      the complete project-owned source chain into the pinned SDK, and
      finalizes `vitasdk-dependencies.json`. Its status is `complete`, its
      source list is nonempty, and every required installed output has a valid
      SHA-256 and byte count.
- [ ] The tag workflow produces exactly one `*-Vita-Source.tar.gz` for the
      frozen candidate. Its `SOURCE_BUNDLE_STATUS.txt` and
      `SOURCE_MANIFEST.json` both report complete Corresponding Source, and
      the workflow used `--require-complete`. Never bypass this gate or use
      GitHub's generic source archive as a substitute.
- [ ] Scan release assets with Microsoft Defender and VirusTotal or document
      why an external scan was not used.
- [ ] Configure the mandatory `WINDOWS_CERTIFICATE_BASE64` and
      `WINDOWS_CERTIFICATE_PASSWORD` repository secrets. Tagged builds fail
      closed without them. Verify Authenticode on the companion, installer, and
      generated uninstaller; the VDD's publisher signature does not cover this
      project's executables.
- [ ] Configure and verify the publisher's Git/SSH signing identity, create a
      signed annotated release tag, and verify the published VPK, installer,
      portable ZIP, and checksum-manifest provenance with
      `gh attestation verify`.

## GitHub and release gate

- [ ] Draft PR has no unresolved blocking review comments.
- [ ] Vita and Windows workflows pass on the frozen commit.
- [ ] The Vita source archive, VPK, installer, portable ZIP, dependency
      manifest, signing manifest, and checksum manifest are attached to and
      attested by the same release workflow run.
- [ ] PR is merged into the `vita` release branch with the tested commit
      ancestry intact.
- [ ] Create and push `v0.14.8` only after the hardware and safety gates pass.
      The release workflow builds both platforms and publishes their artifacts.
- [ ] Download the published release, verify hashes/signatures again, and run a
      short install/pair/stream/disconnect smoke test from those public assets.
- [ ] Publish known limitations, tested hardware, signing-certificate
      identity, upgrade instructions, and rollback instructions.

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
