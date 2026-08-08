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
or older build before enabling the automatic handoff. Upgrade removes exact
Vita preparation hooks owned by older releases while preserving unrelated
global and per-application commands.

Sunshine's native display manager resolves an output and probes encoders before
running application preparation commands, so neither that manager nor a global
preparation hook can safely arm a PnP-disabled idle VDD for every launch and
resume path. The companion therefore keeps `output_name` on Sunshine's active-
display default and disables Sunshine's native `dd_*` topology transaction.

The supported client and host add a small authenticated preflight beside the
standard GameStream protocol. The endpoint presents the same host identity
that the Vita pinned during pairing and accepts only a client certificate still
enabled in Sunshine's paired-client state. Before sending either Sunshine's
launch or resume request, the Vita asks the companion to prepare one supported
mode. The companion creates a protected recovery record, enables the exact
managed VDD, activates 960x544, 960x540, or 1280x720 at a driver-safe 60 Hz,
applies SDR, and returns a generation token. A matching authenticated stop
restores the exact physical baseline and PnP-disables the VDD; a delayed stop
from an older generation cannot tear down a newer stream. No manual Windows
display selection is part of the supported path.

The physical baseline also records the exact Windows default render endpoint
for Console, Multimedia, and Communications roles. Restoration never selects
an arbitrary replacement: after the physical display and its DisplayPort/HDMI
audio endpoint re-enumerate, the companion reapplies only the captured device
ID. Suspend performs no Core Audio call: it durably moves the captured IDs into
a protected, transaction-bound audio retry record for resume; an unavailable
or stalled endpoint can never consume the suspend deadline, delay display
safety, or keep the VDD active.

After Sunshine accepts launch or resume, the Vita marks that exact generation
started and renews a short authenticated lease while the stream is alive. The
renewal is protocol state, not support logging: successful heartbeats are not
written to the optional user log. The companion keeps ordinary renewals in
memory and checkpoints the small protected lease at a sparse interval so an
agent restart can reconnect without granting indefinite display ownership.

## Display lifecycle and transaction

Feature preference and runtime display state are separate. Healthy Enabled +
Idle means at least one physical display is active, no recovery transaction is
pending, and the exact managed VDD device node is PnP-disabled. An
authenticated preflight before either launch or resume transitions through
Preparing to Streaming under the same machine-wide lease. Its generation-
matched stop is the normal restoration boundary. An expired client-bound lease
is the authoritative abrupt-loss signal; an exact Sunshine process exit or a
matching authenticated stop may accelerate that recovery. The experimental
legacy hook mode can additionally use its historical global zero-session log
signal, but that signal is not part of the supported all-app path. Another
Moonlight client's active Sunshine session can never keep a stale Vita display
alive. Sleep, startup, emergency recovery, and uninstall also restore the exact
saved physical layout and PnP-disable the VDD. There is no idle display polling,
continuous Sunshine-log tail, forced Sunshine INFO level, or continuous support
log.

Pending audio state uses checksummed, revisioned primary and backup records.
Each successfully restored role is removed immediately so a still-unplugged
communications device cannot later override a legitimate Console/Multimedia
choice. Every Core Audio attempt runs in a short-lived helper process; a hung
Windows audio RPC is terminated at the process boundary rather than hanging a
Vita stop request, suspend handler, recovery agent, Pause, or uninstall. The
background agent retries only while such a record exists and stops at a circuit
breaker. A later real device/display arrival may open one fresh bounded window,
while the worker's own file writes cannot. Pause quiesces the agent even if a device remains absent;
uninstall makes one final bounded attempt, warns, and then leaves Windows'
current default untouched rather than guessing or becoming uninstallable.

The same `session start` transaction remains available for the timed control-
panel preview and an explicitly selected experimental Apollo CLI path. Apollo
is not exposed by the installer or GUI, is not public-beta qualified, and
requires an explicit display match. A transaction:

1. authenticates and validates the requested mode for a production Vita
   boundary, then acquires the machine-wide display transaction lock;
2. verifies Enabled + Idle and captures the exact active physical Windows
   paths and modes while the VDD is PnP-disabled;
3. writes and flushes a recovery record beneath the protected
   `%ProgramFiles%\Vita Moonlight Host\state` directory;
4. enables the one exact managed VDD device and waits for its target;
5. validates and applies a topology containing only that target;
6. changes it to the Vita-requested resolution and refresh rate;
7. disables advanced color on the target when Force SDR is enabled; and
8. returns the generation only after the display is ready for Sunshine.

If activation fails, the saved physical topology is restored immediately and
the VDD is disabled. A scheduled highest-privilege logon task invokes recovery
after an interrupted transaction. The saved record is cleared only after the
exact physical baseline has been reapplied following device shutdown.
Recovery and rescue tasks allow battery operation and delayed starts, so a
laptop does not postpone recovery until it is connected to AC power.

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
that marker until the saved managed-device inventory and previously present
safeguards are restored and verified. Enable then reconciles the exact VDD back
to PnP-disabled idle; it does not leave a spare display active. A partial or
corrupt transition fails closed and does not authorize a stream. Upgrades read
the existing intent and preserve an intentional Pause.

## Stream rescue agent

Setup installs a highest-privilege per-user logon task that runs a hidden,
single-instance WinForms message loop. The physical-display recovery hotkey is
mandatory. The supported automatic Sunshine path neither registers nor
requires the legacy F8-F10 display-mode hotkeys: the current Vita client uses
the authenticated preflight before both GameStream launch and resume. A named
ready event is signaled only after mandatory recovery registration and the
authenticated boundary listener are ready. The listener uses the existing
Sunshine pairing identities, accepts only the narrow prepare/stop protocol, and
does not expose an unauthenticated status or control endpoint. The Vita overlay
sends the F11 recovery chord through the normal encrypted Moonlight input
channel only for emergency recovery.

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
The display-recovery action first restores any saved display transaction and a
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
After a successful installed-payload transition, versioned cleanup removes only
the immutable exact names shipped at obsolete root/ProgramData locations by
older releases. Unknown, busy, or reparse entries are retained and prevent that
cleanup generation from being marked complete until a safe retry succeeds.

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
Sunshine and ViGEmBus are kept unless the user explicitly selects their
removal. The MTT package is always retained unless a future package-consumer
proof can show that deletion is safe. By default the exact managed device is
left PnP-disabled; selecting display release removes an app-created node or
restores an adopted node to its recorded baseline. Uninstall also removes the
exact authenticated-boundary firewall rule
and stops its listener with the rest of the Vita-owned safeguards.

## Vita client

The client advertises a conventional controller in Xbox mode and DS4 motion
and touchpad capabilities only in the PS4 profile. Sensor samples are converted
to Moonlight protocol units and rate-limited independently to the host request.
Motion initialization is nonfatal and lazy: the single event-driven worker and
Vita sampler exist only during a compatible PS/DS4 stream, and sleep without
polling until Sunshine requests a sensor.

GameStream HTTPS and the narrow display-boundary request use the same client
certificate plus a persisted SPKI SHA-256 pin for Sunshine's self-signed server
certificate. The PIN pairing proof binds the pin before the first privileged
HTTPS request. Older installs retain valid client IDs and keys but require one
visible secure re-pair because no trusted server pin can be reconstructed after
the fact. Reachability probes use a disposable client state and every exit
releases certificate, HTTP, app-list, audio, motion, and stream resources.

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
terminates the video session, sends the generation-matched stop, refreshes
GameStream state, prepares a new display generation, and resumes the same
application without a legacy hotkey or extra pre-disconnect delay. Opening
Task Manager is ordinary encrypted keyboard input and does not add a host-side
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
