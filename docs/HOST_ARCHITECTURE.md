# Host architecture and implementation phases

The streaming protocol remains standard Moonlight/GameStream. Controller
emulation and display topology belong to the Windows host, not the Vita client.

## Current phase

`VitaMoonlight.Host` is a read-only .NET 8 command-line companion. It detects:

- official Sunshine or Apollo;
- ViGEmBus, required for Windows virtual gamepads;
- optional DisplayWizard installations;
- whether the process is elevated for a future driver setup operation.

It emits the recommended 960x544, 60 FPS, 5000 Kbps profile with Sunshine's
`gamepad = auto` and `motion_as_ds4 = enabled`. Because the Vita client no
longer advertises DS4-only capabilities in Xbox mode, Sunshine can reliably
choose XInput for the compatibility profile and DS4 for the motion profile.

## Display transaction phase

Monitor mutation must be recoverable. The future `display apply` operation
will:

1. acquire a single-instance lock;
2. capture the active Windows display topology and primary monitor;
3. write an atomic recovery record;
4. ensure a 960x544 virtual mode exists;
5. activate and select the virtual display for capture;
6. arm a watchdog before disabling any physical display.

`display restore` and the watchdog will restore the captured topology. A failed
apply must roll back immediately. Driver installation will be a separate,
explicitly elevated installer action and will never occur on stream start.

## Packaging phase

The unified release will contain two independently built artifacts:

- the Vita VPK;
- a signed Windows installer containing the companion, profiles, and optional
  integration adapters.

Third-party virtual display drivers will only be bundled after license,
redistribution, certificate, and unattended recovery behavior are verified.
Until then the companion will integrate with separately installed
DisplayWizard or recommend Apollo.
