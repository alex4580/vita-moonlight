# Host architecture

The streaming protocol remains standard Moonlight/GameStream. The versioned,
machine-checked boundary is
[`protocol/vita-host-contract.json`](../protocol/vita-host-contract.json).
The Vita client
negotiates video and sends controller, touch, and motion data; the Windows
companion owns Sunshine integration, virtual-display topology, SDR state, and
crash recovery.

## Windows companion

`VitaMoonlight.Host` is a self-contained .NET 8 Windows application with a
WinForms control panel and an optional CLI. The release path detects and
configures Sunshine, ViGEmBus, the packaged signed virtual-display driver, and
elevation state. Its
recommended profile is 960x544, 60 FPS, 8000 Kbps, H.264, and SDR.
The Vita's first-run controller is Xbox/XInput. H.264 recovery uses automatic
IDR requests; reference-frame invalidation is deliberately not advertised
while the Vita SPS compatibility rewrite constrains the decoder to one
reference frame.

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

The companion's `session start` transaction remains available for the legacy
single-app path, the timed manual preview, and an explicitly selected
experimental Apollo CLI path. Apollo is not exposed by the installer or GUI,
is not public-beta qualified, and requires an explicit display match. It:

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
stream start never installs a driver. The supported Sunshine release path
requires the managed MTT display. The retained experimental Apollo CLI path
delegates display creation to Apollo, refuses automatic display selection, and
must not be described as release-ready without a separate hardware matrix.

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
Vita scheduled tasks. Sunshine and any pre-existing Apollo installation remain
shared programs and are never started, stopped, disabled, or executed by this
lifecycle. A client
pinned to the disabled Vita VDD may therefore need a different host output.

The saved state uses redundant checksummed JSON records plus a small Disabled
intent marker. Disable publishes intent before changing Windows; Enable keeps
that marker until the saved VDD instances and previously present safeguards
are restored and verified. A partial or corrupt transition fails closed and
does not authorize a stream. Upgrades read the existing intent and preserve an
intentional Pause.

## Stream rescue agent

Setup installs a highest-privilege per-user logon task that runs a hidden,
single-instance WinForms message loop. The physical-display recovery hotkey is
mandatory. The supported native Sunshine path neither registers nor requires
the legacy F8-F10 display-mode hotkeys: the current Vita client changes mode by
reconnecting with an ordinary GameStream launch request. F8-F10 register
independently only when the user explicitly selects the legacy
single-application fallback, preserving older clients without reserving those
keys on a default installation. A named ready event is signaled only after
mandatory recovery registration succeeds. No TCP listener, credentials, or
remotely callable HTTP endpoint is added. The Vita overlay sends only the F11
recovery chord through the normal encrypted Moonlight input channel.

Both scheduled tasks are created for the current interactive token at highest
available privilege and their executable, arguments, logon type, and run level
are verified through Task Scheduler after creation. Before setup or repair can
recover displays or replace files, the maintenance helper compares the elevated
Windows identity with the owner of the current interactive session. An
over-the-shoulder UAC elevation with a different Administrator account is
rejected before mutation because its task would run in the wrong desktop.
Uninstall does not create a task and may run under another Administrator; it
removes a same-name task only after verifying its exact Vita executable and
arguments.

The companion never closes or kills an inferred foreground process. The Vita
can open Windows Task Manager through the ordinary Moonlight keyboard channel;
ending a Sunshine application uses GameStream's authenticated quit-app request.
The display-recovery action first restores any saved manual transaction and a
physical-only topology, then stops Sunshine, reloads the
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
to Moonlight protocol units and rate-limited independently to the host request.
Motion initialization is nonfatal and lazy: the single event-driven worker and
Vita sampler exist only during a compatible PS/DS4 stream, and sleep without
polling until Sunshine requests a sensor.

GameStream HTTPS uses the client certificate plus a persisted SPKI SHA-256 pin
for Sunshine's self-signed server certificate. The PIN pairing proof binds the
pin before the first privileged HTTPS request. Older installs retain valid
client IDs and keys but require one visible secure re-pair because no trusted
server pin can be reconstructed after the fact. Reachability probes use a
disposable client state and every exit releases certificate, HTTP, app-list,
audio, motion, and stream resources.

Video and audio callbacks use moonlight-common's queues because Vita decoding,
display synchronization, and audio output can block. They are never declared
as direct-submit callbacks on network receive threads. SPS rewriting has a
fixed checked output bound and submits the actual rewritten access-unit size.

New installs use the Recommended 960x544/60/8 Mbps preset. Reliable, High
quality, and Remote / VPN presets set the whole streaming path, including
resolution, frame rate, bitrate, route detection, packet size, codec, color
range, recovery, immediate presentation, and scaling. Configuration values are range-checked
before decoder/input initialization so an old or damaged INI file cannot select
unsafe packet, video, motion, controller, or touch values.

The in-stream overlay is rendered by vita2d over decoded video. While open, it
sends a neutral controller state and consumes Vita input locally. Settings are
saved immediately; negotiation settings apply through the controlled reconnect,
while input policies and performance-overlay modes can update during the
current session. The dedicated diagnostics page is read-only except for its
explicit **Start support log** / **Stop and save support log** action.
Destructive rescue items require a second confirmation.
Disconnect, Sunshine-app termination, display-setting reconnects, and
host-recovery requests are consumed by the connection UI loop so network
teardown remains ordered. A display-setting reconnect saves the new mode,
terminates the video session, refreshes GameStream state, and resumes the same
application without a legacy hotkey or extra pre-disconnect delay. Opening Task
Manager is ordinary encrypted keyboard input and does not add a host-side
process-control endpoint.

## Packaging

Tagged CI builds three user-facing artifacts from one source revision:

- the Vita VPK;
- a self-contained portable Windows ZIP; and
- the Windows installer with the host, pinned Sunshine package, ViGEmBus, and
  pinned signed virtual-display components.

Third-party hashes and notices live with the host packaging. Optional CI
secrets Authenticode-sign the companion and installer; the virtual-display
driver itself is distributed with its existing publisher signature.
