# Vita/host compatibility contract

[`vita-host-contract.json`](vita-host-contract.json) is the small, versioned
contract shared by the Vita VPK and the Windows host companion. It is a build
invariant, not another network service.

Moonlight/GameStream remains authoritative for stream launch, video, audio,
and controller input. In particular, the resolution and stream frame rate are
sent in the ordinary GameStream launch request. Sunshine keeps the Windows
virtual desktop at the driver-safe refresh rate in the contract while the
encoder may deliver any listed stream frame rate.

The host's shared Sunshine prep hook applies that normalization only when the
request matches the Vita contract. Requests from other Moonlight clients are
a successful no-op in the companion and remain under Sunshine's control; the
companion must not reject, coerce, or break those sessions.

The companion-specific F11 and F12 actions are fixed, parameter-free commands
carried inside the already paired and encrypted Moonlight input session. They
do not acknowledge success. F8-F10 are retained only as legacy,
unacknowledged mode controls for the current fallback path; they are not a
second authoritative display protocol.

Run this before either package is built:

```text
python tools/check-host-client-contract.py
```

When the contract changes, update the JSON, both implementations, the checker
when a source representation changes, and the hardware compatibility matrix
in the same pull request. Changing the JSON alone must never make a release
pass.
