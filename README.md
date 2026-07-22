# Vita Moonlight

Vita Moonlight is a PlayStation Vita Moonlight client plus a Windows companion
for a console-like Sunshine setup. The host switches Windows to a Vita-native
virtual display before capture, forces SDR for the session, restores the normal
desktop afterward, and configures Xbox or DS4 controller support.

This fork is designed to be installed and operated through graphical controls.
The command line remains available for diagnostics and automation, but it is
not required for normal setup.

## What this fork adds

- A single Windows installer containing the host control panel, Sunshine,
  ViGEmBus, and a pinned signed virtual-display driver.
- Automatic 960x544 at 60 Hz virtual-display switching for **every Sunshine
  application**, including Desktop and Steam Big Picture.
- SDR enforcement on the virtual display so an HDR desktop is not captured as
  a washed-out image on the Vita.
- Transactional display recovery after normal exit, network interruption, or
  the next Windows sign-in after an interrupted session.
- A highest-privilege stream rescue agent: the Vita overlay can close or
  force-close the foreground Windows game, end Sunshine's current app, or
  restore the physical display, reload VDD, and restart Sunshine.
- Xbox-compatible and PS4 + gyro controller profiles, plus DS4 touchpad,
  absolute mouse, and Sunshine tablet touch modes.
- A real in-stream Vita overlay for resume, disconnect, resolution, bitrate,
  frame rate, controller mode, touch mode, and the FPS counter.
- A native default profile of 960x544, 60 FPS, H.264, and 8 Mbps. Existing
  native-resolution installations using the old 5 Mbps default are migrated
  automatically to reduce motion artifacts.

## Install

Download both artifacts from the same release:

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator on the Windows
   PC. Keep Sunshine, ViGEmBus, and the signed virtual-display driver selected.
2. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel**.
3. If prompted, choose **Restart as Administrator**, then click **Apply
   recommended setup**. The control panel enables Sunshine's native global
   display lifecycle and restarts Sunshine.
4. Run **Run health check**. Sunshine, ViGEmBus, virtual display, recovery, and
   **Stream rescue** should report ready.
5. Install the matching `.vpk` on the Vita, pair it with Sunshine, and launch
   Desktop, Steam Big Picture, or any other Sunshine application.

The virtual display may initially appear in Windows as an inactive or 800x600
display named **VDD by MTT**. That is expected. Leave it disabled while idle;
the host activates it at the Vita's requested mode only for a stream.

See the [Windows host guide](host/README.md) for installation, configuration,
recovery, and troubleshooting. Release testing is documented in the
[GUI-first acceptance test](host/END_TO_END_TEST.md); use the
[final-release checklist](host/FINAL_RELEASE_CHECKLIST.md) for sign-off.

## Vita controls while streaming

- **START + L + R**: open the stream overlay.
- **Double-press PS**: temporarily release PS capture and return to the Vita
  LiveArea. This remains the forced system-level escape from a stream.
- **D-pad Up/Down**: select an overlay item.
- **D-pad Left/Right**: change a setting.
- **X**: activate the selected item.
- **O**: close the overlay and resume.
- **START + Left**: open the floating keyboard.

Destructive overlay actions require pressing **X twice**:

- **Close Windows game** first asks the rescue agent to close the foreground
  game normally, then force-terminates that process tree if it does not exit.
  Steam, Sunshine, Explorer, and critical Windows processes are protected.
- **End Sunshine app** ends Sunshine's current application session and
  disconnects Moonlight. This is useful when the foreground window cannot be
  identified safely.
- **Recover display + Sunshine** disconnects, forces a physical display active,
  reloads VDD, enforces the physical-only idle topology, and restarts Sunshine.

Resolution, bitrate, frame-rate, and controller changes take effect on the next
connection. Touch mode and the FPS counter update immediately. Choose
**Disconnect stream** in the overlay for a normal exit that also restores the
physical display.

## Recommended streaming settings

Start with **960x544, 60 FPS, 8 Mbps**. It matches the Vita panel, avoids
wasting bandwidth on pixels the device cannot display, and gives the H.264
encoder enough headroom for motion. If Wi-Fi is unstable, try 5 Mbps or 30 FPS.
On a strong local network, 12 Mbps can reduce artifacts further.

The Windows companion forces the Vita virtual display to SDR by default. This
does not permanently disable HDR on the physical monitor; the saved physical
layout and color behavior return when the stream ends.

## Controller and touch profiles

- **Xbox** exposes a conventional XInput controller for broad Windows and
  Steam Big Picture compatibility.
- **PS4 + gyro** exposes DS4 motion and touchpad capabilities. Gyroscope values
  are sent in degrees per second and acceleration in metres per second squared,
  at no more than the host-requested rate.
- **DS4 Touchpad**, **Absolute mouse**, and **Tablet** provide different ways to
  map the Vita touchscreen. These can be changed from the in-stream overlay or
  the normal settings screen.

## If something goes wrong

- Washed-out video or Sunshine capturing the physical monitor: open the host
  control panel, keep **Automatically switch to the Vita display for every
  Sunshine application** and **Force SDR** enabled, then choose **Save and
  apply**.
- The physical monitor does not return: sign out and back in. The installed
  recovery task restores the saved layout. If the desktop is visible, use
  **Displays > Emergency display reset** in the Administrator control panel.
- A game turns black while the local Vita overlay still draws: try **Close
  Windows game** first. If Steam does not return, use **End Sunshine app**. If
  the entire captured desktop remains black, use **Recover display +
  Sunshine**, wait about ten seconds, then reconnect.
- Sunshine reports ViGEmBus missing: run the control panel health check. If
  ViGEmBus is running but Sunshine started earlier, click **Restart Sunshine**.
- Motion artifacts: verify the overlay shows at least 8 Mbps at 960x544/60 and
  that the PC is using wired Ethernet or strong 5 GHz/6 GHz Wi-Fi.

## Build from source

Vita requirements are VitaSDK plus initialized submodules:

```sh
git submodule update --init --recursive
./makepsv
```

Build the Windows companion on Windows with .NET 8:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

The release workflows build the VPK, portable Windows package, and Windows
installer. The host packaging and pinned third-party components are described
in [host/THIRD_PARTY_NOTICES.md](host/THIRD_PARTY_NOTICES.md).

## Upstream and community

This project builds on the original Vita Moonlight port and the Moonlight
ecosystem. General Moonlight documentation is available from the
[Moonlight documentation wiki](https://github.com/moonlight-stream/moonlight-docs/wiki)
and the original Vita project has additional background in its
[wiki](https://github.com/xyzz/vita-moonlight/wiki).
