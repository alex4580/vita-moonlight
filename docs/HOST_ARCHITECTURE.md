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

For the bundled Sunshine version, the companion selects the virtual display by
Sunshine's stable `device_id` and enables its native Windows display manager:
`ensure_only_display`, automatic client resolution and refresh rate, automatic
HDR state, and `dd_config_revert_on_disconnect`. This policy is global, so it
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
3. atomically writes a recovery record under `%ProgramData%\VitaMoonlight`;
4. finds the configured or known managed virtual target;
5. validates and applies a topology containing only that target;
6. changes it to the Vita-requested resolution and refresh rate; and
7. disables advanced color on the target when Force SDR is enabled.

If a manual activation step fails, the saved physical topology is restored
immediately. `session stop` restores it after the legacy application ends. A
scheduled highest-privilege logon task invokes recovery after an interrupted
manual transaction. The saved record is cleared only after a successful
restore.

The driver is installed or updated explicitly by the installer/control panel;
stream start never installs a driver. Apollo mode delegates virtual-display
creation to Apollo but retains the same client and controller profile.

## Vita client

The client advertises a conventional controller in Xbox mode and DS4 motion
and touchpad capabilities only in the PS4 profile. Sensor samples are converted
to Moonlight protocol units and rate-limited to the host request.

The in-stream overlay is rendered by vita2d over decoded video. While open, it
sends a neutral controller state and consumes Vita input locally. Settings are
saved immediately; negotiation settings apply on reconnect, while touch mode
and the FPS counter can update during the current session. Disconnect requests
are consumed by the connection UI loop so normal Moonlight teardown and host
display restoration still run.

## Packaging

Tagged CI builds three user-facing artifacts from one source revision:

- the Vita VPK;
- a self-contained portable Windows ZIP; and
- the Windows installer with the host, pinned Sunshine package, ViGEmBus, and
  pinned signed virtual-display components.

Third-party hashes and notices live with the host packaging. Optional CI
secrets Authenticode-sign the companion and installer; the virtual-display
driver itself is distributed with its existing publisher signature.
