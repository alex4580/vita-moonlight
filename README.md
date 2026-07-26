# Vita Moonlight

Vita Moonlight is a PlayStation Vita Moonlight client plus a Windows companion
for a console-like Sunshine setup. During a stream, the companion activates an
SDR virtual display at a Vita-compatible resolution; after the stream, it
restores the normal Windows display layout.

This fork is intended to be installed and operated through graphical controls.
The command line remains available for automation and support, but it is not
required for normal setup or troubleshooting.

## What this fork adds

- A single Windows installer containing the host control panel, Sunshine,
  ViGEmBus, and a pinned signed virtual-display driver.
- A native **960x544 at 60 Hz** first-run display and stream configuration.
- Managed **960x544**, **960x540**, and **1280x720** virtual-display modes.
- Global Sunshine display switching for Desktop, Steam Big Picture, and other
  Sunshine applications, with SDR enforcement and automatic recovery.
- Complete Reliable, Recommended, High quality, and Remote / VPN streaming
  presets instead of resolution-and-bitrate shortcuts.
- Matching core stream and controller controls before and during a session.
- In-stream **Apply resolution + reconnect** and **Apply input changes +
  reconnect** actions that renegotiate the session without closing the running
  Windows game.
- Xbox-compatible and Steam / DS4 + gyro controller profiles, DS4 touchpad,
  mouse, and Sunshine tablet touch modes.
- A configurable top-right performance overlay and a separate real-time
  diagnostics screen.
- Optional, append-only file logging that is off by default; extended
  per-frame metric collection is skipped when no diagnostics feature needs it.
- A highest-privilege stream rescue agent that can close a bad foreground
  game, end Sunshine's current app, or recover the display and Sunshine.
- A PS-button policy whose safe default keeps PS local while preserving
  double-PS as a forced LiveArea escape.

## Install

Download both artifacts from the same release:

1. Run `Vita-Moonlight-Host-Setup-win-x64.exe` as Administrator on the Windows
   PC. Keep Sunshine, ViGEmBus, and the signed virtual-display driver selected.
2. Open **Start > Vita Moonlight Host > Vita Moonlight Host Control Panel**.
3. If prompted, choose **Restart as Administrator**, then click **Apply
   recommended setup**. This verifies or repairs Sunshine, ViGEmBus, and the
   signed display driver before applying the streaming configuration.
4. Run **Run health check**. Sunshine, ViGEmBus, virtual display, recovery, and
   **Stream rescue** should report ready.
5. Install the matching `.vpk` on the Vita, pair it with Sunshine, and launch
   Desktop, Steam Big Picture, or another Sunshine application.

The installer keeps a compatible Sunshine installation, upgrades an older one
to the pinned supported build, repairs controller support, and preserves
configuration. Every prerequisite command is checked. If Windows requests a
restart, setup stops before applying Sunshine display configuration or changing
the active display; restart, open the control panel as Administrator, and click
**Apply recommended setup** again.

The virtual display may appear in Windows as an inactive or generic-resolution
display named **VDD by MTT** while idle. Leave it disabled while idle. The host
activates it at the requested mode when a Vita stream starts; the first-run
mode is the Vita panel's native **960x544 at 60 Hz**.

See the [Windows host guide](host/README.md) for installation, configuration,
recovery, and troubleshooting. Release testing is documented in the
[GUI-first acceptance test](host/END_TO_END_TEST.md); use the
[final-release checklist](host/FINAL_RELEASE_CHECKLIST.md) for sign-off. Read
the [host compatibility guide](host/COMPATIBILITY.md) and
[Vita settings guide](docs/VITA_SETTINGS_GUIDE.md) before changing defaults.

## Vita controls while streaming

- Hold **START**, then press **L + R** within one second to open the stream
  menu. Starting with START allows the entire chord to be consumed locally
  instead of sending those buttons to the PC.
- **PS (default)**: a single press stays on the Vita. Double-press PS to return
  to LiveArea; this remains the forced system-level escape from a stream.
- **D-pad Up/Down**: select an item.
- **D-pad Left/Right**: change the selected setting.
- **X**: open or activate the selected item.
- **O**: go back or resume the stream.
- **START + Left**: open the floating keyboard. It is also available as
  **Open on-screen keyboard** on the in-stream menu.

To type, focus a Windows text field first. On the Vita, hold **START** and tap
**D-pad Left** within 300 ms, or select **Open on-screen keyboard** from the
stream menu. Characters are sent to Windows as they are entered; Backspace,
Left/Right, and Enter are forwarded as PC keys. Close or minimize the Vita
keyboard to return to the stream. Choose the matching US, Spanish, or Latin
American keyboard layout under **Settings > Input > Keyboard layout**.

The stream menu is organized around the same choices available before a
session:

- **Stream & virtual display** contains preset, resolution, FPS, bitrate,
  network mode, frame pacing, packet-loss recovery, aspect scaling, vblank,
  and host game optimization.
- **Controller & input** contains controller preset, PS behavior, touchscreen,
  gyro, shoulder swap, and the double-tap sprint helper.
- **Performance overlay** cycles through Off, Frame rate, Frame rate + network,
  and Advanced.
- **Real-time diagnostics** shows live stream, decoder, network, controller,
  gyro, and Circle-button state without adding those details to the normal
  stream interface.

The pre-stream Settings screen and in-stream pages edit the same saved
configuration. Less frequently changed options such as local audio, power
behavior, mapping files, touch zones, and keyboard layout remain in Settings.

Resolution, FPS, bitrate, and network mode are negotiated when a stream
connects. After changing them in-stream, select **Apply resolution +
reconnect**. Moonlight switches the Windows virtual display, ends only the
video connection, and resumes the same Sunshine application with the new
encoder settings. Controller capabilities are also negotiated at connection;
use **Apply input changes + reconnect** for those without sending a display
command. Neither action closes the running Windows game. The picture may
disappear briefly during renegotiation.

Destructive stream actions require pressing **X twice**:

- **Close Windows game** first asks the rescue agent to close the foreground
  game normally, then force-terminates that process tree if necessary. Steam,
  Sunshine, Explorer, and critical Windows processes are protected.
- **End Sunshine app** ends Sunshine's current application session and
  disconnects Moonlight.
- **Recover host display** disconnects, forces a physical display active,
  reloads VDD, restores the physical-only idle topology, and restarts Sunshine.

Choose **Disconnect stream** for a normal exit that restores the physical
display.

## Native display modes

The Vita panel is **960x544**, so that is the default for both the Windows
virtual display and Sunshine encoder. The managed choices are:

| Mode | Use |
|---|---|
| **960x544** | Native Vita geometry and the recommended choice. No unnecessary scaling or 4:3 fallback. |
| **960x540** | Strict 16:9 compatibility for a title that rejects 960x544. Fitting it to the Vita can leave a two-pixel bar at the top and bottom. |
| **1280x720** | Compatibility option for games with tiny UI or a fixed 720p minimum. It is downscaled on the Vita and costs more decoder and network work. |

Changing only the Vita resolution setting during an active stream cannot alter
an encoder session that has already been negotiated. Use **Apply resolution +
reconnect** so the VDD and Sunshine encoder both adopt the selected mode.

## Streaming presets

The first-run preset is **Recommended**. Every preset owns the complete stream
path: 960x544 output, a 1024-byte packet size, H.264, Rec. 709 limited-range
SDR, stereo audio, 60 Hz client timing, host game optimization, packet-loss
recovery, frame pacing, fit-to-screen scaling, local-audio off, Vita vblank
wait off, and power-save suppression. Selecting a preset restores all of those
values, not just resolution and bitrate.

| Preset | FPS / bitrate / network | Tradeoff |
|---|---|---|
| **Reliable** | 30 FPS, 5 Mbps, automatic network detection | Lower packet and decoder load for unstable Wi-Fi; motion is less fluid. |
| **Recommended** | 60 FPS, 8 Mbps, automatic network detection | Native-detail, responsive default for ordinary local play. |
| **High quality** | 60 FPS, 12 Mbps, automatic network detection | Cleaner motion on a strong link; a weak link can stutter or add latency. |
| **Remote / VPN** | 30 FPS, 4 Mbps, remote-network handling | More tolerant of constrained or routed links; least fluid and most compressed. |

Changing an owned value marks the preset **Custom**. Streaming presets do not
silently change controller choices. **Reset all to recommended** additionally
restores the Maximum compatibility controller profile, turns the performance
overlay off, and disables diagnostic file logging.

Higher bitrate improves compression only while the wireless link can sustain
it. Once packets queue or drop, image quality and responsiveness get worse
together. Start with Recommended; use Reliable for freezes or audio breakup,
and High quality only when motion remains smooth but looks blocky.

## Performance overlay and diagnostics

The performance overlay appears in the **top-right corner** over a **50%
alpha** background. It can be changed from Settings or the in-stream menu:

| Mode | Information shown |
|---|---|
| **Off** | No on-screen metrics. Extended per-frame collection is skipped unless the diagnostics screen or file logging needs it. |
| **Frame rate** | Rendered FPS and target FPS. |
| **Frame rate + network** | FPS, network health, estimated round-trip time, and measured encoded-video rate. |
| **Advanced** | The above plus configured stream mode/rate, average and maximum decode time, dropped-frame counts, and recovered/failed/out-of-sequence packet counts. |

For a fuller live view, open the stream menu and choose **Real-time
diagnostics**. This dedicated screen shows connection state, measured and
active video rates, round-trip-time estimate, the active stream and packet
size, any settings selected for the next reconnect, decode timing, drops,
controller type, gyro request/event status, Circle down/up state,
performance-overlay mode, file-logging state, and the actual log path. It does
not write a log unless logging is separately enabled.

### Capture an optional diagnostic log

Diagnostic file logging is **off by default**. When it is off, log calls return
immediately and no log file is opened for normal activity.

1. Before connecting, open **Settings > System > Diagnostic file logging**.
   While streaming, toggle **Diagnostic file logging** directly on the
   in-stream menu, or open **Real-time diagnostics** and press **Triangle**.
2. Reproduce the quality, connection, input, or display problem.
3. Return to Real-time diagnostics and press **Triangle** again, or disable
   logging in Settings. This closes the file cleanly. Exiting Moonlight also
   closes it.
4. Open VitaShell and copy
   `ux0:data/moonlight/moonlight.log` to the PC over USB or FTP.

The log is append-only across captures. Rename or delete an old log before a
fresh reproduction if you want a smaller file. If `ux0:data/moonlight` is not
available, Moonlight can use `ux0:moonlight` or
`uma0:data/moonlight`; Real-time diagnostics displays the exact active path.
Review the file for host names or network addresses before posting it publicly.

## Controller and touch profiles

- **Maximum compatibility** is the first-run controller profile. It presents
  an Xbox/XInput controller, uses relative-mouse touch, keeps single PS presses
  local, disables gyro, mapping files, shoulder swapping, and the sprint
  helper. It has the broadest native Windows game compatibility.
- **Steam / DS4 + gyro** presents a DualShock 4, enables Vita gyro and DS4
  touchpad, and selects **Safe PC Guide**. Configure gyro behavior in Steam
  Input. A single PS press reaches Steam after a short safety delay;
  double-PS remains the local LiveArea escape. Some XInput-only games require
  Steam Input translation.
- **Custom** appears when individual controller or touch choices no longer
  match either complete profile.

After changing controller profiles in-stream, choose **Apply input changes +
reconnect** so Sunshine recreates the virtual controller. This input-only
reconnect does not send a display-mode command or reset the VDD. The Real-time
diagnostics screen distinguishes the active controller from a pending choice
and can confirm whether Sunshine requested gyro samples and the Vita sent them.

The four individual PS modes are:

- **Local double-tap**: recommended default. Single PS stays local; double-PS
  returns to LiveArea. Windows receives no Guide event.
- **Safe PC Guide**: delays a single Guide event by 250 ms so a quick double
  press can remain local.
- **Immediate PC Guide**: sends Guide immediately. It has less delay but may
  trigger Steam, Windows, or media shortcuts before a second press is known.
- **System / LiveArea**: leaves PS to the Vita system; one press exits and no
  Guide event reaches the PC.

Touch choices are **Relative mouse**, **DS4 Touchpad**, **Mouse Absolute**, and
**Tablet (Sunshine)**. They affect pointer/controller behavior, not video
quality or bitrate. See the [Vita settings guide](docs/VITA_SETTINGS_GUIDE.md)
for complete tuning and controller details.

## If something goes wrong

- **The Vita still shows the physical display or a 4:3 image:** open the host
  control panel, enable automatic Vita-display switching and Force SDR, then
  choose **Save and apply**. In-stream, select 960x544 and choose **Apply
  resolution + reconnect**.
- **The physical monitor does not return:** sign out and back in so the
  recovery task restores the saved layout. If the desktop is visible, use
  **Displays > Emergency display reset** in the Administrator control panel.
- **A game turns black while the Vita menu still draws:** try **Close Windows
  game**. If Steam does not return, use **End Sunshine app**. If the captured
  desktop remains black, use **Recover host display**, wait about ten seconds,
  and reconnect.
- **Video capture stops completely:** the Vita menu and diagnostics continue
  redrawing over the last completed frame, so the same close/recovery actions
  remain available.
- **Sunshine reports ViGEmBus missing:** run the control-panel health check. If
  ViGEmBus is running but Sunshine started earlier, click **Restart Sunshine**.
- **Motion looks blocky:** inspect Frame rate + network or Advanced. If the
  network is degraded, lower bitrate or choose Reliable; if delivery is smooth
  at 8 Mbps, try High quality.
- **Gyro or a button behaves incorrectly:** select Steam / DS4 + gyro when
  needed, reconnect, then inspect Real-time diagnostics. Enable file logging
  only while reproducing the issue.

## Build from source

Vita requirements are VitaSDK plus initialized submodules:

```sh
git submodule update --init --recursive
./makepsv
```

Developer deploy/debug helpers read the Vita address from `VITA_IP` or a local
`ip_vita.txt`. The local file is intentionally ignored and must never be
committed with a tester's private-LAN address.

Build the Windows companion on Windows with .NET 8:

```powershell
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release -- self-test
```

The release workflows build the VPK, portable Windows package, and Windows
installer. Pinned third-party components are described in
[host/THIRD_PARTY_NOTICES.md](host/THIRD_PARTY_NOTICES.md).

## Upstream and community

This project builds on the original Vita Moonlight port and the Moonlight
ecosystem. General Moonlight documentation is available from the
[Moonlight documentation wiki](https://github.com/moonlight-stream/moonlight-docs/wiki),
and the original Vita project has additional background in its
[wiki](https://github.com/xyzz/vita-moonlight/wiki).
