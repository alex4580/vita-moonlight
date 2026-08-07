# Vita/host compatibility contract

[`vita-host-contract.json`](vita-host-contract.json) is the small, versioned
contract shared by the Vita VPK and the Windows host companion. It is a build
invariant, not another network service.

Moonlight/GameStream remains authoritative for stream launch, video, audio,
and controller input. In particular, the resolution and stream frame rate are
sent in the ordinary GameStream launch request. Sunshine keeps the Windows
virtual desktop at the driver-safe refresh rate in the contract while the
encoder may deliver any listed stream frame rate.

The supported host path uses Sunshine's native all-application display
lifecycle: the managed virtual display is selected by stable device ID,
Sunshine applies the requested Vita resolution at a driver-safe 60 Hz, forces
an SDR session, and restores the prior layout after disconnect. No prep hook
or extra network service sits in the streaming path.

`session hook-start` remains only for the explicit legacy single-application
fallback. There it normalizes recognized Vita modes and returns a successful
no-op for unrelated Moonlight requests; it must never coerce or reject those
sessions.

The companion-specific F11 recovery action is a fixed, parameter-free command
carried inside the already paired and encrypted Moonlight input session. It
does not acknowledge success. Ending an application uses GameStream's
authenticated quit-app request and is not a foreground-window hotkey. F8-F10 are retained only as legacy,
unacknowledged mode controls for older clients on the explicit fallback path;
the current Vita client never sends them and the host registers them only when
that fallback is selected. They are not a second authoritative display
protocol.

Run this before either package is built:

```text
python tools/check-host-client-contract.py
```

When the contract changes, update the JSON, both implementations, the checker
when a source representation changes, and the hardware compatibility matrix
in the same pull request. Changing the JSON alone must never make a release
pass.
