## 0.14.8

* Replaced front-corner hit-test buttons with deterministic tap gestures. A
  mapped action now requires one short stationary touch; swipes, drags, holds,
  and multitouch pass through to relative mouse, absolute mouse, DS4 touchpad,
  or Sunshine tablet mode until all fingers lift. The graphical tap-zone
  mapper is now the single enable/configuration authority.
* Moved local stream-menu and keyboard chords to SELECT-led sequences. START
  is no longer buffered by local shortcut detection and reaches the streamed
  controller immediately; holding SELECT first keeps every member of a
  completed SELECT+L+R or SELECT+Left chord local.
* Ordered stream teardown behind Moonlight's media-worker completion barrier,
  fixing the crash that could occur when Sunshine ended an application while
  the Vita UI was returning to its menus.
* The host now records the exact pre-stream Windows default render endpoints.
  After restoring the physical display it waits boundedly for those same
  endpoints, reapplies only them, and keeps a protected retry record across
  monitor sleep when DisplayPort/HDMI audio is not available yet.
* Fixed the idle virtual display remaining PnP-available after a stream. When a
  physical monitor entered standby before Windows sleep, Windows could promote
  the 960x544 target, resize or relocate open windows, and leave DWM/display
  recovery sluggish after wake. Enabled + Idle now means the exact physical
  layout is active and the managed display device is stopped; startup, normal
  disconnect, abrupt loss, suspend, upgrade, Pause, and uninstall all reconcile
  that invariant with bounded recovery.
* Added a mutually authenticated Vita-to-companion stream boundary. The paired
  Vita arms the exact display before both Sunshine launch and resume, receives
  an unguessable generation token, starts a short certificate-bound heartbeat
  lease after Sunshine accepts the app, and uses that token to restore the
  saved layout. Other Moonlight clients cannot arm or retain the Vita display
  by merely using the same resolution or sharing Sunshine. Exact lease expiry
  is the abrupt-loss authority; Sunshine exit and a proven zero-session event
  can only accelerate recovery.
* Made repair normalize existing VDD installations instead of assuming a clean
  PC: one virtual monitor is retained and driver logging/debug logging are off.
  Upgrade and uninstall also remove only exact obsolete Vita-owned payload and
  legacy state names while preserving unknown user files and shared software.
* Kept a saved computer visible when Sunshine revokes or forgets the Vita.
  The pinned HTTPS probe now treats Sunshine's exact unauthorised response as
  **Pairing required**, falls back only to public discovery metadata, and
  preserves every other certificate or transport failure as a hard error.
* Fixed the Vita pairing retry path that could authorize the client in
  Sunshine but leave the real saved-computer entry unpaired or hidden until a
  restart. Failed and cancelled attempts now remain visible as **Pairing
  required**, successful pairing is committed before the app continues, and
  journaled device storage survives an interrupted write.
* Fixed upgrades that could create a new empty data folder before checking the
  Vita's historical storage locations, making existing computers appear to
  disappear. Startup now inventories every supported legacy/current root
  first, reuses one authoritative writable store, and refuses to merge
  ambiguous pairing identities.
* Fixed the in-stream Vita keyboard to read `UPDATE_TEXT` from the correct IME
  union member, reserve its terminator, and return directly to live video.
  Native event vectors now cover characters, Backspace, arrows, Enter, close,
  and the initial sentinel update without logging typed text.
* Corrected native-aspect rendering so 960x544 fills the Vita panel, 960x540
  produces only the expected two-pixel top and bottom bars, and crop/fill never
  samples outside the hardware decoder texture.
* Isolated every PIN attempt with a fresh Sunshine pairing session identity and
  kept the final certificate-pinned pairing challenge authoritative. Aborted
  attempts can be retried without inheriting stale Sunshine session state.
* Reworked discovery and saved-host health checks around one owned worker,
  bounded probes, offline backoff, and stable saved identities. Idle or
  unpaired entries no longer create a permanent polling load, and worker
  shutdown is joined before pairing, suspension, or exit.
* Hardened Vita configuration, connection, input, motion, and power lifecycles
  so partial startup and reconnect failures clean up in reverse order,
  shortcuts remain local, released inputs cannot stick, and optional support
  logging remains off until the user starts a capture.
* Removed the legacy one-second "frame pacer" that converted harmless timer
  jitter into drops of future decoded frames. Completed frames now present
  immediately for lower latency and smoother motion; optional Vita vblank
  synchronization remains available for tear control.
* Restored the Vita network pool to 4 MiB so Moonlight's 2.13 MiB video receive
  buffer fits without silently stepping down, while retaining nearly 2 MiB for
  audio, control, discovery, and allocator overhead. No bitrate, resolution,
  packet-size, FEC, or image-quality reduction was made.
* Made the authenticated Vita preflight authoritative for managed Sunshine
  sessions and removed the misleading client switch that could leave the
  physical desktop selected after choosing a Vita resolution.
* Made in-stream display changes reconnect through the ordinary GameStream
  launch-mode contract without sending F8-F10 or adding a pre-disconnect delay.
  The host now registers and requires those legacy mode keys only for an
  explicitly selected single-application fallback; default installs reserve
  only the F11 physical-display recovery chord.
* Fixed discovery-menu refresh bookkeeping for an empty result list and when
  the newest discovered computer is already paired.
* Made Vita packages optimized Release builds by default while preserving the
  Vita platform definition across every bundled dependency.
* Tightened Windows virtual-display selection and recovery. Automatic setup
  selects exactly one managed MTT display, rejects legacy or duplicate device
  ambiguity, preserves the exact physical baseline, and returns the device to
  PnP-disabled idle after any verification or recovery attempt.
* Added a fail-closed installer and emergency bootstrap for a broken older
  installation that exposes only one active managed MTT display. It rescans
  first, may restart only that exact enabled device while backend features are
  enabled, and refuses to replace files or clear safeguards until Windows
  proves a physical-only layout.
* Made the public Windows path unambiguous: Sunshine always installs with the
  required managed Vita display. Apollo is preserved but removed from the
  installer and GUI until separately qualified; its retained experimental CLI
  path requires an explicit display match.
* Removed the host-side F12 foreground-process termination shortcut. A Vita can
  now open Windows Task Manager through ordinary Moonlight input, while ending
  a Sunshine application continues to use GameStream's authenticated quit-app
  request.
* Bound recovery tasks to the actual interactive streaming account. Setup,
  repair, and Enable reject different-account UAC before mutation, task
  definitions are verified as interactive/highest after creation, and
  uninstall remains available to another Administrator after exact action
  ownership checks.
* Made Sunshine application/configuration updates rollback together on normal
  failure and restart safely after interruption. Backup ownership is published
  before file creation, and legacy Apollo hooks are removed only when unchanged
  and after Apollo has exited.
* Expanded release-contract checks and first-time community instructions for
  clean install, in-place upgrade, same-version repair, Vita reinstall,
  interrupted pairing retry, Pause/Enable, and safe shared-component retention
  or exact-device release during uninstall.
* Replaced the unlicensed mDNS submodule and opaque Vita dependency packages
  with project-owned, hash-locked clean-room/source recipes and corresponding-
  source packaging. Vita discovery, TLS, media, font, compression, and audio
  dependencies are now reproducible from reviewed upstream source inputs.
* Moved the deliberately unsigned Windows preview exception to the exact
  `v0.14.8-beta.1` tag. The workflow verifies its Unknown-publisher status,
  paired artifact identity, SHA-256 manifest, and GitHub provenance; every
  other public tag remains signing-required.

## 0.14.7

* Fixed Vita connections to current Sunshine builds whose bounded `appversion`
  contains the legitimate signed build sentinel used by `7.1.431.-1`. Invalid
  server numeric data now identifies the exact field instead of reporting a
  generic numeric-field error.
* Restored Sunshine PIN pairing on Vita by replacing the embedded-runtime-
  dependent `%hhx` certificate conversion with a strict ASCII hex decoder.
  The decoder accepts only exact upper- or lowercase hex and permanently tests
  Sunshine's PEM certificate envelope while rejecting odd or decorated input.
* Added a machine-checked Vita/host compatibility contract. Vita requests are
  limited to 960x544, 960x540, or 1280x720 at 24/30/40/50/60 FPS while the
  Windows virtual desktop remains at a driver-safe 60 Hz. Native Sunshine
  display management covers all apps; the tolerant hook remains only for the
  legacy single-application fallback.
* Secured Vita-to-Sunshine HTTPS with a certificate pin established by the PIN
  exchange. Upgrades preserve valid unique client identities; the historical
  shared identity is replaced only during the required one-time migration when
  no authenticated host pin exists. Fresh installs generate a unique identity,
  all cryptographic randomness is checked, and optional HTTP diagnostics no
  longer print pairing secrets or response bodies.
* Removed blocking direct-submit audio/video callbacks from Moonlight's network
  receive path, bounded SPS rewriting, disabled incompatible H.264 reference-
  frame invalidation, and pinned the Vita-tested Moonlight common transport
  fixes at `07c32c80f98bb0d7214c577bd080eea3ce64a856`.
* Bounded untrusted Sunshine XML, App lists, mode lists, and UI strings; fixed
  empty-list menu indexing and optional legacy-host fields; and made failed
  decoder, audio, motion, pairing, and reconnect lifecycles safe to retry.
* Made gyro sampling lazy and host-driven. No motion worker runs outside a
  compatible PS/DS4 stream, one 64 KiB event-driven worker replaces two
  permanent polling workers, and sensor setup failures no longer trap startup.
* Added a durable, reversible **Pause Vita host features** lifecycle that restores
  the physical desktop, disables Vita background tasks and the managed VDD,
  while leaving shared Sunshine/Apollo installed and reachable. Clients pinned
  to the paused Vita VDD may need a physical host output. Enable restores only
  the previously present Vita safeguards, and upgrades preserve pause.
* Added pre-sleep physical-display preparation and bounded post-resume
  topology/mode recovery so interrupted streams cannot strand Windows on a
  Vita-only, physical-plus-VDD, 800x600, or Vita-sized layout after wake.
* Made clean installs, upgrades, and repairs exclusive and crash-resumable.
  Setup preserves the original paused/enabled intent and exact recovery-task
  obligations across a killed installer, then restores them before clearing
  its protected maintenance fence.
* Fixed in-place upgrades from older beta hosts that predate the
  `uninstall prepare` command. The installer now relies on its embedded current
  maintenance helper's already-completed physical-display safety check instead
  of asking the legacy installed executable to run a command it does not have.
* Hardened uninstall with exact Task Scheduler COM verification, a second
  physical-display safety gate, rollback of recovery safeguards on failure,
  and allowlisted cleanup of current and legacy Vita-owned state.
* Expanded privacy-safe support reports with backend issue categories, exact
  task states, managed-VDD state, and active display resolution/refresh data.

## 0.14.6

* Removed obsolete Circle/O transition counters from real-time diagnostics.
  Normal Circle input and disconnect release safety are unchanged.
* Fixed clipped and overlapping Vita menu text, made long help dialogs
  scrollable, and hardened host-discovery labels and touch-zone previews.
* Bounded every stored front-touch contact, sanitized legacy touch geometry,
  and released mouse, DS4-touchpad, and tablet contacts when changing modes,
  entering a mapped corner, or ending a stream.
* Corrected the VPK build identity to 0.14.6 and added tag/version validation,
  a one-time unsigned `v0.14.6-beta.1` bootstrap gate, signing enforcement for
  every later tag, exact release-asset checks, SHA-256 manifests, and GitHub
  provenance attestations to the publishing workflow.
* Rebuilt Windows uninstall around verified physical-display recovery. Shared
  Sunshine, ViGEmBus, and VDD installations are retained by default, optional
  removal is explicit, Vita-managed host state is cleaned, and silent
  automation remains noninteractive.
* Added an on-device graphical controller mapper for every remote gamepad
  button and a live front-touch-zone editor with gamepad, mouse, keyboard, and
  local actions. Mapping changes apply immediately and use Vita-safe
  journaled saves with interrupted-write recovery.
* Hardened the virtual-display driver's fixed configuration path. Install or
  repair now replaces legacy entries without following them, creates the
  protected directory atomically, pins its Windows file identity, and rejects
  substitutions during reload, verification, or uninstall.
* Added the `emergency reset-display-driver` recovery alias and packaged the
  beta smoke-test, build/fork, and signed-release guides with both Windows
  distributions.
* Replaced input-by-input diagnostics with an optional, privacy-safe support
  log. Each capture starts a fresh structured session, keeps one previous
  capture, records concise system/connection/decoder/network summaries, and
  can be converted into a shareable report with the bundled summarizer.
* Added structured public issue and pull-request templates plus signed GitHub
  provenance attestations for the VPK, Windows packages, and checksum
  manifest.
* Reorganized the Vita settings and Windows control panel around ordinary
  setup, streaming, controller, touch, recovery, and support tasks instead of
  developer diagnostics.
* Rewrote the quick start, minimum beta test, full end-to-end test, logging
  guide, and community report template for clean installs, in-place upgrades,
  same-version repairs, and both shared-component uninstall choices.
* Made the Windows installer lifecycle-aware, added a reviewable one-click host
  support report, and packaged the matching user, testing, logging, build, and
  release documentation with both Windows distributions.
* **Code signing policy:** see the
  [public policy](docs/CODE_SIGNING_POLICY.md)
  and [privacy disclosure](PRIVACY.md).

## 0.14.5

* Added explicit **Diagnostic file logging** and **Open on-screen keyboard**
  rows to the in-stream menu. Logging remains off by default and the existing
  Triangle and START + Left shortcuts remain available.

## 0.14.4

* Fixed repair installs racing Windows display-target re-enumeration after the
  existing VDD device stack restarts. Native verification now waits up to 30
  seconds for the already-installed target to return before changing any
  display topology.
* Kept every physical display active during the new wait and added a specific
  timeout that distinguishes a delayed/absent target from a real display-name
  mismatch.

## 0.14.3

* Fixed **Apply recommended setup** on an existing VDD installation. Repair
  now stages the driver package first, then reapplies and normalizes the
  existing configuration so a real upgrade cannot overwrite Vita modes with
  the stock XML.
* Removed duplicate effective modes created when a resolution contained an
  explicit 60 Hz entry and the upstream driver also replicated global 60 Hz
  onto it. Other user resolutions, refresh rates, and driver options remain
  intact.
* Fixed native-mode verification using `CDS_UPDATEREGISTRY` on its temporary
  extended-display topology. Verification now uses the same non-persistent
  live change as streaming, which was validated against an upgraded
  `ROOT\MttVDD` device at 960x544/60.
* Mode failures now distinguish an absent mode from a Windows rejection and
  report the current mode plus the closest modes actually advertised by the
  active display source.

## 0.14.2

* Fixed driver setup and repair temporarily making the Vita virtual display
  the only active screen while checking 960x544. Verification now preserves
  every active physical monitor, adds VDD as an extended display, retries the
  native mode in place with a fresh driver mode enumeration, and restores the
  exact original topology once.
* Replaced the generic ten-second enumeration failure and repeated restart
  advice with the final Windows mode error. A failed native-mode check now
  stops setup as a real error after restoring the physical desktop.

## 0.14.1

* Added a dedicated real-time diagnostics screen and optional append-only Vita
  log. Logging is off by default, opens no file until enabled, and can be
  toggled before or during a stream.
* Added top-right performance overlays for frame rate, frame rate plus network,
  and advanced stream/decode statistics. The overlays use a 50%-alpha
  background and avoid extended metric collection when it is not needed.
* Replaced partial bitrate-only choices with complete Reliable, Recommended,
  High quality, and Remote / VPN streaming presets. Added matching preset and
  input controls before and during a stream, clearer tradeoff explanations, and
  a full recommended-settings reset.
* Made native 960x544/60 the first-run client and virtual-display mode. Added
  managed 960x540 and 1280x720 compatibility modes and a controlled in-stream
  display change that updates the active VDD, reconnects Moonlight, and leaves
  the Windows game running.
* Hardened virtual-display provisioning, verification, rescue hotkeys, and
  physical-display recovery across clean installs and repair installs.
* Made the Windows installer and **Apply recommended setup** verify or repair
  Sunshine, ViGEmBus, and native VDD readiness with checked failures and an
  explicit stop-and-resume path when Windows requires a reboot.
* Fixed repair installs failing on Windows builds where PnPUtil reports error
  50 for an already-enabled VDD. That result is accepted only for the
  enable-device step and is followed by device restart and native-mode
  verification. Installer failures now include the host's concrete error
  breadcrumb instead of only a numeric exit code.

## 0.14.0

* Added a Steam / DS4 controller preset that enables Vita gyro, DS4 touchpad,
  and delayed Steam Guide behavior together while retaining double-PS as the
  forced LiveArea escape. The in-stream overlay can also change PS behavior
  independently.
* Fixed unsynchronized host motion requests and concurrent Vita sensor reads
  that could leave Steam seeing a gyro-capable DS4 without receiving motion.
  The stream overlay now reports requested/live/error gyro state.
* Hardened controller release handling: button flags use the protocol's full
  width, stream start clears stale state, and pause/disconnect explicitly send
  a neutral state and controller removal. Circle down/up counters in the
  overlay make held-input faults observable during testing.
* Fixed START + L + R failing when a shoulder was sampled before START or the
  original 300 ms window expired. The overlay now accepts any button order and
  gives the non-leaking START-led sequence a one-second window.
* Changed the default PS-button policy so single presses remain local and can
  no longer trigger Windows/Steam Guide shortcuts such as media play/pause.
  Double-PS still provides the forced LiveArea escape. Added delayed Safe PC
  Guide, legacy Immediate PC Guide, and direct System / LiveArea modes.
* Fixed Sunshine falling back to the physical monitor when the VDD lacked a
  Vita-selectable mode such as 960x540/60. Existing and new installations now
  provision the safe 960x540, 960x544, and 1280x720 desktop modes at 60 Hz;
  client FPS remains independently configurable. Doctor verifies the runtime
  mode set and rejects an incomplete Sunshine display mapping.
* The START, L, and R overlay chord is now captured locally. Holding START and
  pressing L + R within one second opens the overlay without sending any of
  those buttons to the Windows game; other button orders also open it.
* Added Reliable, Balanced, and High quality Vita streaming presets plus
  on-device explanations for resolution, FPS, bitrate, frame pacing, vblank,
  controller, and touch tradeoffs.
* New installations default to the compatibility-first Xbox/XInput controller,
  H.264 at native 960x544/60 and 8 Mbps, frame pacing, and H.264 packet-loss
  recovery. Existing explicit controller and video selections are preserved.
* Hardened legacy configuration parsing and validation so corrupt or obsolete
  resolution, bitrate, packet-size, controller, touch, and motion values fall
  back safely instead of destabilizing the Vita client.
* Improved Windows portability across custom Sunshine locations, reordered
  display logs, laptops on battery, multiple physical monitors, and stale
  rescue-agent processes. Doctor now rejects unsupported OS/architecture
  combinations explicitly.
* Added Windows 2022/2025 build/self-test coverage and packaged compatibility
  and Vita tuning guides.
* Added an in-stream Vita overlay opened with START + L + R. It provides
  resume, disconnect, resolution, bitrate, frame-rate, controller, touch-mode,
  and FPS-counter controls. Double-press PS remains the forced LiveArea escape.
* Added double-confirmed overlay actions to close or force-close the foreground
  Windows game, end Sunshine's app session, or recover a failed display stack.
  A protected highest-privilege agent handles the host actions without exposing
  a network control service.
* Sunshine's native display lifecycle now covers every application by default,
  including Desktop and Steam Big Picture, and restores the physical layout
  when all clients disconnect even if the application stays open.
* Vita virtual-display sessions force SDR by default to prevent an HDR physical
  desktop from appearing washed out on the Vita.
* Raised the 960x544/60 recommended bitrate from 5 Mbps to 8 Mbps and migrate
  older native-resolution configurations to reduce motion artifacts.
* Reworked the Windows host control panel into task-oriented Overview,
  Streaming, Displays, and Help & recovery pages.
* Added GUI settings for all-application integration, SDR enforcement, display
  matching, safe display previews, and recovery.
* Rewrote the installation and end-to-end test documentation around the GUI
  workflow and current release behavior.

## 0.13.2

* added PS button capture
  - when enabled, double-pressing the PS button will open the menu, single press is passed as PS/Xbox button to Sunshine
  - This required disabling the safe (-s) flag in `vita-make-fself` and adding the `SceShell` permission (0x2800000000000001)
* allow enabling/disabling touch zones independent of touch mode (separate setting)
  - allows using front screen touch zones regardless of touch input mode. when a touch zone is pressed, the touch input is ignored. unused touch zones are ignored and function as normal touch area.

Bug fixes:
* fixed front touch zone L2/R2 special keys not working

Feature:
* feat: Implement MAC address handling for devices and remove deprecated MAC retrieval functions

Refactor:
* fix: Update subproject commits for enet, inih, and moonlight-common-c
* split `vitainput_process` into multiple smaller functions for better code readability

## 0.13.1

* fix: Split shortcuts into individual files for better code organization and maintainability.
* fix: Resolved ghost/phantom button presses when handling overlays and shortcuts. Button state is now correctly reset and restored.

## 0.13.0

* You can now choose between three touchscreen modes: DS4 Touchpad, Absolute Mouse, and Tablet (Sunshine) from the settings menu.
* "DS4 Touchpad" mode lets you use the Vita screen as a DualShock 4-style multitouch touchpad, compatible with gestures and advanced controls in games and Steam Input.
* DS4 Touchpad mode is now more sensitive and precise for a smoother experience.
* Wake-on-LAN (WOL) fully integrated into the host management menu: allows you to power on the remote PC from the Vita using the saved MAC and automatically calculated broadcast address.
* The host MAC address is now saved and loaded correctly from the device.ini file, with all ARP/legacy logic removed.
* WOL packet sending is robust and compatible with local networks, with cross-platform MAC parsing and detailed debug logs.
* Added Python script (`tools/wol_sniffer.py`) to receive and verify Wake-on-LAN packets on the network, useful for testing without powering off the PC.
* Minor UI fixes and improved visual feedback in the host management menu.
* Fixed: Combos like L1+L2 and R1+R2 now work correctly even when used together with either touchscreen or backtouch. Pressing both at the same time no longer causes one to be lifted; both can be held simultaneously as expected.
* Fixed: In absolute touch mode, special button overlays no longer interfere with touch input. If a special button is detected, the corresponding touchscreen input is ignored, allowing for true absolute touch and making all screen corners usable.

## 0.12.3

* Moonlight now automatically selects and creates the best available folder for config and cache (supports ux0:/data/moonlight, ux0:/moonlight, uma0:/data/moonlight, and fallback to ux0:data).
* The chosen folder location is logged at startup for easier debugging.
* The config file (moonlight.conf) is always created in the selected folder, improving compatibility for users with missing folders.
* Improved error handling and debug logging for folder creation and config file setup.
* All folder and config path logic is now centralized and robust, reducing crashes and edge cases.
* Bugfix: Prevented crashes on startup due to buffer or path issues in folder selection logic.
* New Host Management menu: manage paired hosts with improved status display, unified status text, and direct actions (connect, force connect, change IP/name, delete).
  - Now allows:
    - Changing the host name
    - Changing the host IP
    - Forcing connection even if the host is not shown as online
    - Deleting hosts
* The connection and host management menus now show the port next to the IP (uses httpPort if available, otherwise httpsPort).
* Host Management menu now shows host status (paired/unpaired, online/offline, IP changed) in a unified way, matching the main menu logic.
* Visual feedback and menu options for host management are now clearer and more robust.
* Internal refactor: main.c and check_dir.c now use a shared macro for path buffer size and a helper for folder selection.

## 0.12.2

* Added option to swap L1/R1 and L2/R2 button functions from the settings menu.
* Added option to select gamepad type: Xbox or PlayStation layout, configurable from the settings menu.
* Improved host scan logic: scan is always restarted cleanly when returning to the main menu or after leaving any submenu.
* UI and config logic for swap and mapping exclusivity is now robust and bidirectional.
* Controller type selection in UI now saves/loads correctly and updates immediately.
* Touch Mouse and Touchscreen toggles now update their subname in the menu immediately after toggling.
* Main menu always restarts host scan and waits for thread readiness before allowing host selection.
* Cleaned up and stabilized code, especially around host scan thread handling and function declarations.
* Many bugfixes and code cleanups.

## 0.12.1

* Add support for multi-touch touchscreen mode (Sunshine compatible) and absolute mouse mode.
* New settings: choose between "Touch Mouse Absolute" (gestures, mouse) and "Touchscreen (Sunshine multitouch)" (tablet mode).
* In touchscreen mode: true multitouch, native gestures (e.g. zoom), no mouse cursor/ghost, no mouse clicks.
* In absolute mouse mode: improved gestures (right click with two-finger tap, scroll with two fingers), cursor always follows main finger.
* Physical controls (gamepad) work in both modes.
* Improved logic for right click and scroll gestures.
* Many bugfixes and refinements to touch and gesture experience.

## 0.11.4

* Host status indicator dots: green (online), yellow (IP change pending), red (offline/disconnected).
* Robust host status checking with background scan thread and mDNS sniffer.
* Manual refresh of host status with TRIANGLE (△) in main menu and device search.
* IP change confirmation dialog now always shows old and new IP, and always asks for confirmation.
* Host status and menu only refresh when status actually changes or user requests it.
* Improved debug logging for all host and menu transitions.
* UI and logic are now robust even if host fields are empty or uninitialized.
* All UI messages for search device and refresh are now in English.
* Added screenshots to documentation (see docs/):
  * keyboard.png: Floating keyboard in a Steam app using Moonlight.
  * ip1.png: Host waiting for IP update (yellow).
  * ip2.png: Host online (green).
  * ip3.png: Host offline/disconnected (red).
  * ip4.png: IP change confirmation dialog (shows old/new IP).
  * ip5.png: Search device function showing a found local device.

## 0.11.2

* More reliable and user-friendly automatic pairing.
* Experimental absolute touch controls (beta).
* Floating virtual keyboard that no longer covers the main screen.
* Updated libraries and dependencies for improved compatibility and stability.
* New modular UDP mDNS sniffer for device discovery (replaces legacy mdns code).
* Improved debug logging, especially for device search and reconnect flows.
* Cleanup of legacy code and dependencies.

## 0.11.0

* The hold L to activate gyro feature has been removed and replaced with gyro reporting.
This means that the client will try to get Sunshine to emulate a DS5 controller and gyro will
be reported. This opens up full configurability with steam input. Note this only works on Windows
or on Linux with Pre-release versions of Sunshine as of 1/10/25. This could be different in the future.
* Video slicing added back
* Logging added to pairing for debug
* An icon to indicate when the double tap sprinting has been enabled has been added

## 0.10.1
* Fix pairing issue (Same one from v0.9.3)

## 0.10.0
* Add double tap to sprint option
* Add motion controls
* Fork moonlight-common-c and enet

## 0.9.3
* Fix pairing issue (#231, 5494d93)
* Update latest moonlight-common-c & enet (902cbed)
* Update latest libgamestream (#232)

## 0.9.2
* Fixed disconnects after stream is started (#222)

## 0.9.1
* Support GFE 3.22 (2452e98)

## 0.9.0
* Expose local audio setting to the end user (#173)
* Remember the currently connected address (7998108)
* Add a 21:9 resolution (1280x540) to Settings (f1eb931)
* Properly display stream in the correct display ratio, and place in the middle of the screen (14e3a2c)
* Since GFE will add blackbars to the stream even when a non-16:9 monitor is using 16:9 resolution,
    there is another option to use along the 21:9 resolution to only display the center 16:9 region (5b7a2cc)
* Detect supported resolutions (#193)
* Add new option to enable/disable of the vita vblank waiting (#197)

## 0.8.0
* Add new option for swapping O/X buttons (#168)
* Add new option for drawing FPS value (#167)
* Fix `RTSP message too long` (#164, fbe3d06)

## 0.7.0
* Support 960x540 resolution (#162, fc7e19c)
* Support GFE 3.20 (97a6a0b)

## 0.6.1
* Fix the stream delay issue (f791b3d); thanks @AlC4pwn to confirm fixing the bug

## 0.6.0
* Update latest moonlight-common-c (cfadd84, ffe8c15)
* Update latest inih (a2e38bb)
* Fix invalid variable initializations
* Apply SPS changes of moonlight-embedded
* Use nerdfont instead VITA system font (7e2b682)
* Use vita2d instead direct framebuffer handling (70330e2)
* Implement poor network indicator (749aaaf)

## 0.5.0
* Support to discover stream server via mDNS (#143)
* Implement frame pacer (#147)
* Update latest enet & moonlight-common-c (904a5d1, 7f63f0d)

## 0.4.1
* Update latest moonlight-common-c (2c9d61c)

## 0.4.0
* Sort app list alphabetically (#127)
* Improve special button settings (#130)
* Implement video cleanup for fixing connection issue (#131)

## 0.3.3
* Update latest moonlight-common-c (8e77710)
* Cherry pick upstream change about gamepad masking (933d700)
* Reduce mismatch between upstream (e2d7910, e3cad393)

## 0.3.2
* New configure option about streaming optimization (d2c974a)
* Fix cannot use 960x544 resolution (653afa6)

## 0.3.1
* Fix cannot connect with new devices (b838278)

## 0.3.0
* Support GFE 3.11
* New configure option about reference frame invalidation (#89, #91)
* Fix unset resolution configure (#90)

## 0.2.0
* Support newer vitasdk (94988ab)
* Support forward error correction (f8631b5, 184bdbe)
* No more build uncompress binary (6fd22e7)

## 0.1.2
* Compress binary (#75)
* Support GFE 3.2 (#76)

## 0.1.1
* Fix connection problem on the GFE 2.2.3 (#66)
* Improve logging datas (#67, #68)
* Support newer vitasdk (#70)

## 0.1.0
* Add new option for store debugging log (#53)
* Fix crash at quit application (#54)
* Add livearea (#62)
* Add new option for mouse acceleration (#63)
* Cleanup codes. (#60)
* Now remove alpha mark. but still lower version :) (#64)

## alpha6
* Reimplement input & config process for the improve stability (#47)
* Fix little memory leaks (#47)
* Fix crash if have too many moonlight supporting games (#47)
* Fix crash at press circle button on the connect menu (#47)
* Fix bug about not detect device model (#50)
* Fix cannot use analog sticks on the VITA 2000 (#50)
* Change priority of input thread for the high bitrate connection (#50)
* Update mapping values & add new mapping file for the PSTV (#51)

## alpha5
* Fix crash at first time (dcd1dc8)
* Improve input packet handle (#24)
* Change connection option (#26)
* Code cleanup for input handling (#27, #30, #31)
* Support L2/R2/L3/R3 Buttons on PSTV (#36)
* Update build toolchains & Fix connection problems under GFE 3.0 (#39)

## alpha4
* Support GUI (#13)
* Support editing config file (#4)
* Support input mapping (#5)
* Add new config options for power saving (#6)
* Turn safe application (#7)

## alpha3
* Support virtual button for L2/R2/L3/R3 using touchscreen (#1, #2)
* Support mouse move and click using touchscreen (#3)

## alpha2
* Support custom settings; screen resolution, framerate and bitrate (d145403)

## alpha1
* Initial release (04a7c1d)
