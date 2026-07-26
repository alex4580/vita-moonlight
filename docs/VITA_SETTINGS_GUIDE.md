# Vita streaming settings guide

The first-run configuration is designed to work without tuning:

- **Recommended** stream preset: 960x544, 60 FPS, 8 Mbps, H.264 SDR.
- **Maximum compatibility** controller: Xbox/XInput, relative-mouse touch,
  local PS button, gyro off.
- Performance overlay and diagnostic file logging off.

The Vita's panel is 960x544. Matching the Windows virtual display, Sunshine
encoder, decoder, and panel avoids the 4:3 fallback and unnecessary scaling
that prompted this fork's display work.

## Complete streaming presets

Presets are full configurations, not bitrate shortcuts. All four set:

- 960x544 stream and virtual-display geometry;
- 1024-byte network packets;
- H.264 video;
- Rec. 709 limited-range SDR color;
- stereo audio with PC-local audio disabled;
- 60 Hz Vita client timing;
- host game optimization enabled;
- H.264 reference-frame invalidation enabled for packet-loss recovery;
- Vita frame pacing enabled;
- Vita vblank wait disabled;
- fit-entire-frame scaling instead of crop/fill;
- Vita power-save suppression enabled.

They differ only where the use case calls for it:

| Preset | FPS | Bitrate | Network handling | Best use and tradeoff |
|---|---:|---:|---|---|
| **Reliable** | 30 | 5 Mbps | Auto detect | Congested or distant Wi-Fi. Lower delivery and decoder load, but visibly less fluid motion and a longer interval between input-visible frames. |
| **Recommended** | 60 | 8 Mbps | Auto detect | First-run local-play default. Native panel detail, responsive motion, and enough compression headroom for an ordinary home link. |
| **High quality** | 60 | 12 Mbps | Auto detect | Strong nearby access point when motion is smooth but looks blocky. It preserves more detail, but a link that cannot sustain it will queue, stutter, and feel slower. |
| **Remote / VPN** | 30 | 4 Mbps | Remote / VPN | Routed, VPN, or bandwidth-constrained connections. Most conservative data rate, with more compression and less-fluid motion. |

Changing any owned stream value changes the displayed preset to **Custom**;
your custom values remain saved. Selecting a named preset restores every owned
value listed above. It does not change controller/touch settings.

**Reset all to recommended** is broader than selecting the Recommended stream
preset. It also selects Maximum compatibility input, turns the performance
overlay off, and turns diagnostic file logging off.

## Resolution and the Windows virtual display

The client and Windows companion manage exactly three end-to-end modes:

| Mode | Behavior |
|---|---|
| **960x544** | Native Vita resolution and default. Use this unless a particular game rejects it. |
| **960x540** | Strict 16:9 compatibility. Fit-to-screen can show a two-pixel bar at both the top and bottom because the Vita panel is four pixels taller. |
| **1280x720** | Compatibility mode for a fixed 720p minimum or very small game UI. The Vita downscales it, so it adds encoder, network, and decoder work without adding panel pixels. |

Resolution is negotiated when the video session starts. Saving another
resolution during an existing connection cannot reshape the encoder that is
already running.

To change it remotely:

1. Hold **START**, then press **L + R** to open the stream menu.
2. Open **Stream & virtual display**.
3. Select **Stream + virtual display** and choose 960x544, 960x540, or
   1280x720.
4. Select **Apply resolution + reconnect**.

Moonlight tells the host agent to change the active VDD, disconnects only the
video session, and resumes the same Sunshine application. The Windows game
continues running. The reconnect renegotiates resolution, FPS, bitrate,
network mode, and other stream-start settings with Sunshine's encoder. Expect
a brief black screen while this happens.

If the Vita still receives the physical monitor, an 800x600 desktop, or a 4:3
picture after reconnecting, the host display lifecycle is not active. Run the
Windows control-panel health check, verify automatic Vita-display switching is
enabled, and use **Save and apply** before retrying 960x544.

## Settings before and during a stream

The pre-stream Settings screen and in-stream menu use the same saved
configuration.

| Area | Available in Settings | Available in-stream |
|---|---|---|
| Stream | Preset, managed resolution, FPS, bitrate, host optimization, packet recovery, network mode, vblank, frame pacing, aspect scaling, local audio | Preset, managed resolution, FPS, bitrate, host optimization, packet recovery, network mode, vblank, frame pacing, aspect scaling, plus Apply resolution + reconnect |
| Input | Controller preset, gyro, sprint helper/timing, shoulder swap, PS behavior, touch mode, mappings, touch zones, mouse acceleration | Controller preset, gyro, sprint helper, shoulder swap, PS behavior, touch mode, plus Apply input changes + reconnect |
| Diagnostics | Performance-overlay mode and diagnostic file-logging toggle | Performance-overlay mode and dedicated Real-time diagnostics screen with logging toggle |
| System | Power behavior, X/O layout, keyboard layout | Rescue and disconnect actions |

Immediate settings such as performance-overlay mode update while streaming.
Stream-format settings and controller type require a reconnect. Each in-stream
page has an explicit apply-and-reconnect action; both preserve the running
Windows game.

## Video and network controls

| Setting | What it changes | Cost or risk |
|---|---|---|
| **Frame rate** | 60 FPS produces smoother movement and shorter input-to-visible-frame intervals. | 30 FPS is easier for Wi-Fi and decoder load, but less fluid. Intermediate 24/40/50 values are available for special cases and make the preset Custom. |
| **Video bitrate** | More bits reduce blocks, smearing, and lost detail during motion. | A rate above the sustainable link capacity causes queues, loss, freezes, and added input latency. The accepted range is 1-30 Mbps. |
| **Network mode** | Auto detect chooses local/remote handling; Local only and Remote / VPN override it. | Forcing the wrong path can reduce responsiveness or reliability. Leave Auto detect selected unless the host path is known. |
| **Packet-loss recovery** | Requests a clean H.264 reference frame when damage is detected. | Small protocol/encoder overhead; leave it on for Wi-Fi. |
| **Frame pacing** | Drops late or excess frames instead of displaying an uneven queue. | Usually smoother. Turning it off can feel slightly more immediate in a special case but can introduce judder. |
| **Wait for Vita vblank** | Synchronizes drawing to the Vita display. | May reduce tearing, but can add synchronization latency; presets leave it off. |
| **Aspect scaling** | Fit shows the complete encoded frame; Crop / fill removes borders by trimming edges. | Crop can hide desktop UI and game HUD elements. Fit is the safe default. |
| **Host game optimization** | Allows Sunshine's protocol to request game-oriented settings. | A game may rewrite its own graphics choices. Disable only for titles that repeatedly change them. |
| **Local audio** | Keeps audio playing on the PC as well as the Vita. | Can cause echo in the room; presets leave it off. |

The client requests H.264 only because the Vita has a hardware H.264 decoder.
The host virtual display is SDR; HEVC, AV1, 10-bit video, and HDR are not
compatibility targets for this device.

## Performance overlay

Choose **Performance overlay** in Settings or on the stream menu's main page.
It is drawn in the top-right corner over a 50%-alpha background.

| Mode | Shows | Collection behavior |
|---|---|---|
| **Off** | Nothing | Extended per-frame diagnostics are skipped unless the diagnostics screen or file logging is active. |
| **Frame rate** | Rendered FPS / target FPS | Smallest on-screen view. |
| **Frame rate + network** | FPS, Moonlight connection health, estimated round-trip time, and measured encoded-video Kbps | Useful for separating encoder artifacts from an unstable link. |
| **Advanced** | The above, stream resolution/configured bitrate, average and maximum decode time, dropped frames, and recovered/failed/out-of-sequence packet counts | Best for short tuning sessions; more screen area and metric collection. |

These are local Vita measurements. “Network degraded” is Moonlight's
connection-health signal, measured video Kbps is the received encoded-video
rate, and RTT is Moonlight's current transport estimate; none is a router speed
test.

## Real-time diagnostics and optional logging

Live diagnostic values no longer clutter the normal stream menu. Open the
in-stream menu and select the dedicated **Real-time diagnostics** item to see:

- connection state and rendered/target FPS;
- network health, RTT/variance, and measured/active video rates;
- active resolution and packet size, plus settings queued for the next
  reconnect when they differ;
- average/maximum decode time and dropped frames;
- virtual-controller type;
- gyro enabled/requested state, report rate, event count, and sensor errors;
- performance-overlay and file-logging state;
- the actual diagnostic-log path.

Opening this screen collects the metrics required to update it, but does not
write a file. Toggle **Diagnostic file logging** directly on the in-stream
menu, or press **Triangle** on this screen. Press **O** or **START** to return.

File logging is off by default. In the disabled state, log calls return after a
single check and the file remains closed. When enabled, logs append to:

`ux0:data/moonlight/moonlight.log`

If that directory is unavailable, Moonlight may select
`ux0:moonlight/moonlight.log` or
`uma0:data/moonlight/moonlight.log`. Trust the path displayed on the
Real-time diagnostics screen.

For a useful reproduction:

1. Leave the performance overlay on the mode that best demonstrates the
   problem, if needed.
2. Enable **Settings > System > Diagnostic file logging**, toggle it on the
   in-stream menu, or press Triangle on Real-time diagnostics immediately
   before the test.
3. Reproduce one problem once and note the approximate time and game.
4. Disable logging again to close the file, or exit Moonlight.
5. Use VitaShell USB or FTP to copy `moonlight.log` to the PC.
6. Include the selected preset, controller profile, host connection type, and
   what was visible when the issue occurred.

The file is append-only, so an older capture remains above the new one. Rename
or delete it before testing if you need an isolated log. Inspect it for host
names or network addresses before sharing it publicly. While logging is
enabled, its once-per-second performance records include FPS, received video
rate, decode time, drops, network state, RTT/variance, FEC recovery/failure,
and out-of-sequence packets.

## On-screen keyboard

Focus a text field in the streamed Windows application first. Then either hold
**START** and tap **D-pad Left** within 300 ms, or choose **Open on-screen
keyboard** from the in-stream menu. Typed characters are forwarded
immediately; Backspace, Left/Right, and Enter are sent as PC keys. Close or
minimize the Vita keyboard to return to the stream.

Select the matching US, Spanish, or Latin American layout under **Settings >
Input > Keyboard layout**. The keyboard translates supported characters to PC
virtual keys; it is not a Unicode paste or clipboard feature.

## Controller profiles

### Maximum compatibility

This first-run profile applies the complete general-purpose input path:

- Xbox/XInput virtual controller;
- relative-mouse touchscreen;
- Local double-tap PS behavior;
- gyro disabled;
- mapping file disabled;
- normal shoulder mapping;
- double-tap sprint helper disabled.

Use it for games with native XInput prompts or when Steam Input is not
required. It prevents a single PS press from reaching Windows, avoiding
unexpected Guide or media actions.

### Steam / DS4 + gyro

This profile applies:

- DualShock 4 virtual controller;
- Vita gyroscope enabled;
- front touchscreen as DS4 Touchpad;
- Safe PC Guide;
- mapping file, shoulder swap, and sprint helper reset off.

Choose **Apply input changes + reconnect** after selecting it so Sunshine
recreates the virtual controller. This is an input-only reconnect: it does not
change or reset the active virtual display.
Steam should then see a DS4-class controller and can map its gyro through Steam
Input. Moonlight sends raw motion; configure camera/action behavior and
sensitivity in the game's Steam Input layout.

If Steam does not expose gyro, inspect **Real-time diagnostics**:

- **Awaiting host request** means the DS4 motion path is enabled locally but
  Sunshine has not requested gyro samples.
- A report rate and increasing event count confirms the Vita is sending
  samples.
- A sensor error identifies a Vita-side motion failure.

### Custom input and touch

Changing a profile-owned option makes the controller preset **Custom** without
discarding the selection. Available touch modes are:

| Touch mode | Use |
|---|---|
| **Relative mouse** | Broad desktop compatibility; finger movement moves the pointer. |
| **DS4 Touchpad** | Sends DS4 touch coordinates; pair with the DS4 controller path. |
| **Mouse Absolute** | Maps Vita coordinates directly to Windows; works best when display geometry matches the stream. |
| **Tablet (Sunshine)** | Sends Sunshine's tablet/multitouch-style coordinates; application support varies. |

The mapping-file, shoulder-swap, sprint-helper, touch-zone, and mouse
acceleration controls are advanced customizations. Change them only for a
specific game's need; they do not improve video performance.

### PS button behavior

| Mode | Behavior | Tradeoff |
|---|---|---|
| **Local double-tap** | Single PS stays local; quick double-PS returns to LiveArea. | Safest default. Windows receives no Guide event. |
| **Safe PC Guide** | Waits 250 ms, then sends a single PS as Guide. Quick double-PS remains local. | Steam Guide works with a small delay. |
| **Immediate PC Guide** | Sends Guide immediately; double-PS still releases to LiveArea. | Lowest Guide delay, but the first event can trigger Steam, Windows, or paused media before the second press is known. |
| **System / LiveArea** | Vita retains PS; one press leaves Moonlight. | No captured double-tap and no PC Guide event. |

## Practical tuning order

1. Start with **Recommended**, 960x544, and Maximum compatibility input.
2. If motion is smooth but compressed, try **High quality**.
3. If frames freeze, audio breaks up, or latency jumps, return to Recommended
   and then try **Reliable**.
4. Use Frame rate + network to distinguish a low rendered FPS from degraded
   transport. Use Advanced briefly when decode time or drops matter.
5. Keep 960x544 unless a title needs strict 16:9 or 720p. Apply the new mode
   with the controlled reconnect.
6. Use Steam / DS4 + gyro only when the game or Steam layout benefits from
   those capabilities, then reconnect.
7. Enable file logging only for a reproducible problem, copy the log, and turn
   it off again.
