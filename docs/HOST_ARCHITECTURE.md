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

1. acquires a single-instance session lock;
2. captures the active Windows paths and modes;
3. atomically writes a recovery record beneath the protected
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
after 1.5 seconds. The display-recovery action stops Sunshine, restores any
saved manual transaction, enables every connected physical display when none
is active, reloads the signed VDD, reapplies the physical-only topology after driver enumeration, and
starts Sunshine. Results are written beneath the protected installed `state`
directory for the control panel and diagnostics.

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
