# Host architecture

The streaming protocol remains standard Moonlight/GameStream. The Vita client
negotiates video and sends controller, touch, and motion data; the Windows
companion owns Sunshine integration, virtual-display topology, SDR state, and
crash recovery.

## Windows companion

`VitaMoonlight.Host` is a self-contained .NET 8 Windows application with a
WinForms control panel and an optional CLI. It detects Sunshine or Apollo,
ViGEmBus, the packaged signed virtual-display driver, and elevation state. Its
recommended profile is 960x544, 60 FPS, 8000 Kbps, H.264, and SDR.
The Vita's first-run controller is Xbox/XInput; H.264 reference-frame
invalidation and client frame pacing are enabled for Wi-Fi resilience.

Host discovery does not require the default installation directory. It checks
explicit environment overrides, Sunshine's registered Windows service image,
Program Files variants, and common per-user installation directories. The
configuration directory follows the discovered executable unless explicitly
overridden.

The installer upgrades Sunshine builds older than `2026.516.143833`, while
leaving compatible newer builds in place. The health check rejects an unknown
or older build before the companion writes native display-manager keys. After a
restart, configuration polls Sunshine's display inventory for up to 30 seconds
instead of assuming service-running means enumeration is complete.

For a compatible Sunshine version, the companion selects the virtual display
by Sunshine's stable `device_id` and enables its native Windows display manager:
`ensure_only_display`, automatic client resolution, a driver-safe 60 Hz
Windows desktop refresh, automatic HDR state, and
`dd_config_revert_on_disconnect`. Moonlight negotiates the stream frame rate
independently. This policy is global, so it
covers Desktop, Steam Big Picture, and custom games and restores the physical
layout after the final client disconnects even when the application remains
open. Obsolete `VitaMoonlight.Host` prep hooks are removed while unrelated
commands are retained, and the original `apps.json` is backed up once.

## Display lifecycle and manual transaction

Sunshine owns the default streaming transaction. It snapshots display state,
activates only the configured virtual target, applies the mode requested by the
Vita, and reverts after a 500 ms disconnect grace period. This avoids tying
display restoration to application shutdown; Sunshine deliberately keeps
detached applications such as Steam Big Picture alive for resume.

The companion's `session start` transaction remains available for Apollo,
legacy single-app mode, and the timed manual preview. It:

1. acquires the machine-wide display transaction lock;
2. captures the active Windows paths and modes;
3. writes and flushes a recovery record beneath the protected
   `%ProgramFiles%\Vita Moonlight Host\state` directory;
4. finds the configured or known managed virtual target;
5. validates and applies a topology containing only that target;
6. changes it to the Vita-requested resolution and refresh rate; and
7. disables advanced color on the target when Force SDR is enabled.

If a manual activation step fails, the saved physical topology is restored
immediately. `session stop` restores it after the legacy application ends. A
scheduled highest-privilege logon task invokes recovery after an interrupted
manual transaction. The saved record is cleared only after a successful
restore. Recovery and rescue tasks allow battery operation and delayed starts,
so a laptop does not postpone recovery until it is connected to AC power.

The driver is installed or updated explicitly by the installer/control panel;
stream start never installs a driver. Apollo mode delegates virtual-display
creation to Apollo but retains the same client and controller profile.

Every companion path that can mutate topology, a managed display device, or
the pending recovery record uses that same typed transaction lease: session
start/stop/mode, timed tests, explicit driver install/reload/removal,
Pause/Enable, suspend/resume recovery, emergency recovery, and uninstall. A
session rechecks the fully enabled backend state only after acquiring the
lease. Wake recovery likewise rechecks the recovery marker while holding it,
so a session which legitimately starts after resume cannot be mistaken for a
stale pre-sleep transaction.

## Host-feature lifecycle

**Pause Vita host features** is a durable lifecycle for Vita-owned background
work, not a network or Sunshine power switch. Before pausing, the companion
restores and verifies a physical-only topology. It then disables only the
exact managed MTT VDD instance IDs captured on entry and removes the exact two
Vita scheduled tasks. Sunshine and Apollo remain shared dependencies and are
never started, stopped, disabled, or executed by this lifecycle. A client
pinned to the disabled Vita VDD may therefore need a different host output.

The saved state uses redundant checksummed JSON records plus a small Disabled
intent marker. Disable publishes intent before changing Windows; Enable keeps
that marker until the saved VDD instances and previously present safeguards
are restored and verified. A partial or corrupt transition fails closed and
does not authorize a stream. Upgrades read the existing intent and preserve an
intentional Pause.

## Stream rescue agent

Setup installs a highest-privilege per-user logon task that runs a hidden,
single-instance WinForms message loop. Close-game and emergency-recovery
hotkeys are mandatory. The three managed display-mode hotkeys register
independently, so an unrelated shortcut collision cannot disable the two
recovery actions; readiness is exposed to the health check. A named ready event
is signaled only after mandatory registration succeeds. No TCP listener,
credentials, or remotely callable HTTP endpoint is added. The Vita overlay
emits the matching keyboard chords through the normal encrypted Moonlight input
channel.

The close-game action captures the foreground window, refuses Windows shell,
Steam, Sunshine, companion, and critical-system process names, requests a
normal window close, then terminates only that process tree if it remains alive
after 1.5 seconds. The display-recovery action first restores any saved manual
transaction and a physical-only topology, then stops Sunshine, reloads the
signed VDD, reapplies the physical-only topology after driver enumeration, and
starts Sunshine. If a suspend notification arrives during that action,
physical recovery is kept and the slower service/driver phase is skipped.
Results are written beneath the protected installed `state` directory for the
control panel and diagnostics.

The agent also handles Windows suspend/resume broadcasts. Suspend preparation
publishes a protected cross-process intent before acknowledging sleep, then
serializes a physical-only recovery. Every session/driver path that can commit
a managed-VDD topology checks that intent before and after activation; an
in-flight process must abort to physical-only rather than commit after the
suspend notification. A stale intent at agent startup is recovered and cleared
only after physical safety is re-established. Resume uses a bounded,
generation-scoped observation window whose routine samples are read-only; any
repair runs only after reacquiring the display transaction. It repairs only
unmistakable 800x600,
Vita-sized, or 30 Hz physical fallback drift when Windows advertises the
persisted user mode. Per-monitor mode-repair failures are sparse structured
warnings and never invalidate an already visible physical topology.

## Installer maintenance transaction

An in-place upgrade or repair first uses the installer-embedded current host to
recover and verify a physical-only topology. It then publishes redundant,
protected maintenance records containing the exact live setup PID/start time,
the saved backend intent, and pre-mutation recovery-task obligations. A command
gate serializes all later installed-host children; only the matching live owner
may mutate state, while exact physical recovery remains available without an
owner. Dead-owner takeover carries the original obligations forward, and a
live or unverifiable owner is never displaced. Uninstall bridges a dead setup
record into its own durable guard before removing either maintenance copy.

## Uninstall transaction

The uninstaller creates a durable InProgress transaction guard before optional shared
dependency removals. While it exists, new sessions and user lifecycle changes
are rejected and a surviving rescue agent is limited to physical-display
safety. Finalization holds the display lease through physical recovery,
paused-backend handoff, exact task removal, and the last topology proof.
Failures restore any removed safeguards and the prior paused state before the
guard is cleared. After irreversible owned cleanup succeeds, the guard advances
to Finalized. Inno deliberately retains the primary host until that stage,
verifies its deletion, and removes the exact guard last. A retry with an exact
Finalized stage but a missing host may finish file-only cleanup; a missing host
with any earlier, torn, or absent stage fails closed. Successful uninstall
removes only allowlisted Vita-owned state; unknown entries are retained.
Sunshine, ViGEmBus, and the MTT driver are kept unless the user explicitly
selects their removal.

## Vita client

The client advertises a conventional controller in Xbox mode and DS4 motion
and touchpad capabilities only in the PS4 profile. Sensor samples are converted
to Moonlight protocol units and rate-limited to the host request.

New installs use the Recommended 960x544/60/8 Mbps preset. Reliable, High
quality, and Remote / VPN presets set the whole streaming path, including
resolution, frame rate, bitrate, route detection, packet size, codec, color
range, recovery, pacing, and scaling. Configuration values are range-checked
before decoder/input initialization so an old or damaged INI file cannot select
unsafe packet, video, motion, controller, or touch values.

The in-stream overlay is rendered by vita2d over decoded video. While open, it
sends a neutral controller state and consumes Vita input locally. Settings are
saved immediately; negotiation settings apply through the controlled reconnect,
while input policies and performance-overlay modes can update during the
current session. The dedicated diagnostics page is read-only except for its
explicit **Start support log** / **Stop and save support log** action.
Destructive rescue items require a second confirmation.
Disconnect, Sunshine-app termination, display-mode changes, and host-recovery
requests are consumed by the connection UI loop so network teardown remains
ordered; close-game recovery keeps the current stream alive.

## Packaging

Tagged CI builds three user-facing artifacts from one source revision:

- the Vita VPK;
- a self-contained portable Windows ZIP; and
- the Windows installer with the host, pinned Sunshine package, ViGEmBus, and
  pinned signed virtual-display components.

Third-party hashes and notices live with the host packaging. Optional CI
secrets Authenticode-sign the companion and installer; the virtual-display
driver itself is distributed with its existing publisher signature.
