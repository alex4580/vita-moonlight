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

Sunshine preparation commands are installed per application because Sunshine
does not apply an application-specific hook globally. By default the
configurator removes stale commands carrying the `VitaMoonlight.Host` marker
and appends a current start/stop pair to every application, including the
generated Vita Moonlight app. Unrelated commands and applications are retained,
and the original `apps.json` is backed up once.

## Display transaction

Before Sunshine begins capture, `session start`:

1. acquires a single-instance session lock;
2. captures the active Windows paths and modes;
3. atomically writes a recovery record under `%ProgramData%\VitaMoonlight`;
4. finds the configured or known managed virtual target;
5. validates and applies a topology containing only that target;
6. changes it to the Vita-requested resolution and refresh rate; and
7. disables advanced color on the target when Force SDR is enabled.

If any activation step fails, the saved physical topology is restored
immediately. `session stop` restores it after a normal disconnect. A scheduled
highest-privilege logon task invokes recovery after a host or power interruption
left a transaction pending. The saved record is cleared only after a successful
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
