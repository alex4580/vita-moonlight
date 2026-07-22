# Vita streaming settings guide

The first-run profile is **Balanced**: 960x544, 60 FPS, 8 Mbps, H.264,
packet-loss recovery and frame pacing enabled, vblank wait disabled, and an
Xbox-compatible controller. This is the best general starting point for the
Vita's 960x544 panel and 2.4 GHz Wi-Fi hardware. It is not the maximum value of
every slider; it balances image quality, latency, decoder load, and tolerance
of ordinary home Wi-Fi.

The Vita settings screen includes two help entries and three presets:

| Preset | Video | Use it when |
|---|---|---|
| Reliable | 960x544, 30 FPS, 5 Mbps | Wi-Fi is congested, distant, or dropping frames. Motion is less fluid, but the lower packet rate is easier to sustain. |
| Balanced | 960x544, 60 FPS, 8 Mbps | Default for normal play. It preserves the Vita panel's native detail and provides the most responsive 60 FPS experience without an excessive bitrate. |
| High quality | 960x544, 60 FPS, 12 Mbps | The access point is nearby and motion still shows blockiness. It reduces compression, but packet loss or stutter may become worse on a weak link. |

Changing an individual value makes the preset show **Custom**. It does not
erase the custom values.

## Video and network settings

| Setting | Increasing/enabling it does | Cost or risk |
|---|---|---|
| Resolution | Gives the encoder more source detail. | Anything above 960x544 is downscaled to the Vita panel, increases decode/bandwidth cost, and usually produces little visible benefit. Use higher modes mainly to test game/UI scaling. |
| FPS | Improves motion fluidity and reduces the time between input-visible frames. | 60 FPS roughly doubles the frame rate and network work of 30 FPS. Choose 30 when Wi-Fi cannot hold 60 consistently. |
| Bitrate | Reduces compression blocks, color smearing, and loss of detail in motion. | A bitrate above what Wi-Fi can sustain causes packet loss, latency spikes, freezes, and worse apparent quality. Lower it before lowering resolution. Allowed range is 1-30 Mbps. |
| Recover after packet loss | Lets Sunshine replace damaged H.264 reference frames sooner. | Small protocol/encoder overhead. Leave it enabled for normal Wi-Fi use. |
| Frame pacer | Drops late/excess frames instead of presenting an uneven queue. | Usually looks smoother. Disabling it can feel slightly more immediate in some games but may cause judder. |
| Wait for display vblank | Synchronizes buffer swaps to the Vita display. | Can reduce tearing, but adds synchronization latency. It is off by default for responsiveness. |
| Remote-network optimization | Tells the Moonlight transport to expect a remote/WAN-style connection. | May be less responsive on a clean LAN. Leave it off at home; try it for VPN or internet streaming. |
| Let host optimize game settings | Allows the host protocol to request game-oriented performance settings. | A game may change its own graphics options. Disable it if a title keeps rewriting settings. |

The client deliberately requests **H.264 only**. The Vita has a hardware H.264
decoder; HEVC, AV1, 10-bit video, and HDR are not useful compatibility choices
for this device. The Windows companion also asks Sunshine for SDR during Vita
sessions.

## Controller and touch settings

| Mode | Compatibility and behavior |
|---|---|
| Xbox / local PS | First-run default. Exposes one conventional XInput controller, keeps single PS presses off the PC, and works with the broadest range of Windows games and launchers. It does not expose gyro or a DS4 touchpad. |
| Steam / DS4 + gyro | Applies DS4 emulation, Vita gyro, DS4 touchpad, and Safe PC Guide as one preset. Reconnect after selecting it. A single PS press reaches Steam after a 250 ms safety window; double-PS remains the local LiveArea escape. Some XInput-only games need Steam Input translation. |
| Relative mouse | Default touchscreen behavior. One finger moves the pointer, taps click, and two fingers scroll. It works independently of controller type. |
| DS4 Touchpad | Sends the front panel as a DS4 touchpad. Pair it with DS4 controller mode. |
| Mouse Absolute | Maps Vita screen coordinates directly to the Windows pointer. It is convenient for desktop/UI control but depends on the host display matching the streamed geometry. |
| Tablet (Sunshine) | Sends native pen/touch-style coordinates through Sunshine. Use it for multitouch-aware Windows applications; game support varies. |

### PS button behavior

| Mode | Behavior | Tradeoff |
|---|---|---|
| Local double-tap | Recommended default. A single PS press is discarded locally; a quick double-press releases capture and returns to LiveArea. | Windows receives no Guide button. This prevents Steam Input, browser media keys, or another host mapper from turning PS into play/pause. |
| Safe PC Guide | Waits 250 ms before sending a single PS press as the controller Guide button. A quick double-press is consumed locally and returns to LiveArea. | Preserves Guide with a small delay. Choose this only when a game or Steam layout needs Guide. |
| Immediate PC Guide | Sends Guide as soon as PS is pressed; double-PS still releases to LiveArea. | Lowest Guide latency, but the first press reaches Windows before Moonlight can know whether a second press is coming. It can trigger host shortcuts or resume paused media. |
| System / LiveArea | Leaves the PS button under Vita system control. One press returns to LiveArea and nothing is sent to Windows. | No captured double-tap or PC Guide behavior. |

Changing this option alters input behavior only; it has no effect on video
quality, stream latency, or bandwidth. If PS unexpectedly controls PC media,
select **Local double-tap**.

The in-stream overlay shows live input diagnostics in blue. **Gyro: live**
confirms that Sunshine requested motion and the Vita is sending sensor packets.
**Host has not requested it** means the current virtual controller is not using
the DS4 motion path. The Circle down/up counters should remain equal after the
button is released. Moonlight sends a neutral state and removes the controller
on pause or disconnect to prevent stale held buttons.

Gyro reporting remains enabled so switching from Xbox to DS4 immediately makes
motion available. Motion packets are not advertised as a controller capability
while Xbox mode is selected.

## Practical tuning order

1. Start with **Balanced** and keep the PC wired to the router when possible.
2. If the image blocks during motion but remains smooth, try **High quality**.
3. If frames freeze, audio breaks up, or input latency jumps, return to
   **Balanced**, then try **Reliable**.
4. Keep 960x544 unless a game has a UI-scaling problem.
5. Change controller/touch modes for a game's feature requirements, not for
   video quality; they do not improve the encoder.
