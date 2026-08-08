# Vita/host compatibility contract

[`vita-host-contract.json`](vita-host-contract.json) is the versioned build
contract shared by the Vita VPK and Windows host companion.

Moonlight/GameStream remains responsible for launch, video, audio, and input.
The companion adds one narrow boundary around launch: after secure Sunshine
pairing, the Vita makes a mutually authenticated HTTPS `prepare` request before
both GameStream `/launch` and `/resume`. The host enables and switches to its
managed Vita display, then returns an unpredictable generation. After Sunshine
accepts launch/resume, the Vita confirms that exact generation with `started`
before opening media and renews it every 10 seconds with a bounded background
`heartbeat`. The worker is separate from video decode and each heartbeat is
limited to three seconds. When media disconnects, the same paired certificate
must return that exact generation to `stop`; only then can that handoff restore
the physical desktop.

The prepared launch lease lasts 240 seconds, covering the 60-second host
operation plus Sunshine's 120-second launch budget and scheduling margin.
`started` changes it to a 45-second renewable lease. The host checkpoints
heartbeats durably at most every 20 seconds to avoid needless disk traffic;
same-process recovery uses the newer in-memory revision. After an agent restart,
the last checkpoint still provides time for the Vita's next heartbeat.
Sunshine-wide process and client counts can prompt an earlier inspection, but
they can never keep an expired exact Vita lease alive or tear down a live one.

The bridge uses Sunshine's own server certificate/key. The Vita authenticates
the existing Sunshine SPKI pin, and the host accepts only certificates of
enabled entries in Sunshine's `named_devices` state. There is no unauthenticated
status endpoint. The dedicated port is Sunshine HTTP + 23 (48012 for the
default 47989), has an exact managed firewall rule, and accepts only four v1
operations. The rule permits VPN and routed clients because mutual TLS—not a
source address—is the authorization boundary.

Generic Sunshine remains compatible: connection refusal or a timeout before a
TLS session exists falls back to ordinary GameStream. Reaching a bridge but
failing TLS, client authorization, the bounded display operation, HTTP,
generation, or protocol validation is a visible hard failure. This distinction
prevents downgrade after an authenticated bridge answers.

Sunshine's native display-device switching is disabled and its output pin is
cleared because Sunshine probes encoders before its prep commands. The bridge
is authoritative and arms the VDD before Sunshine receives `/launch` or
`/resume`. Obsolete Vita global prep hooks are removed; unrelated user prep
commands are preserved. The per-app `session hook-start` path remains only for
the explicit legacy compatibility mode.

The F11 recovery action remains a parameter-free emergency command carried by
Moonlight's encrypted input channel. F8-F10 remain unacknowledged legacy mode
controls and are not sent by the current Vita client.

Run this before either package is built:

```text
python tools/check-host-client-contract.py
```

When this contract changes, update the JSON, both implementations, the checker,
native protocol vectors, and hardware compatibility notes in the same pull
request. Editing the JSON alone must never make a release pass.
