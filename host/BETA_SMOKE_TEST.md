# Minimum beta smoke test

This is the smallest hardware pass that can justify a public **beta**. It does
not replace `END_TO_END_TEST.md` or `FINAL_RELEASE_CHECKLIST.md` for a final
release.

Test the exact VPK and Windows package produced from one frozen commit. Run the
first pass as an in-place upgrade over the previously installed build; do not
assume a clean PC. Repeat the abbreviated install/stream/recovery/uninstall
pass on the laptop before publishing.

## Automated gate

From the repository root:

```powershell
python tools\check-version-consistency.py
dotnet format host\VitaMoonlight.Host\VitaMoonlight.Host.csproj `
  --verify-no-changes
dotnet build host\VitaMoonlight.Host\VitaMoonlight.Host.csproj -c Release
dotnet run --project host\VitaMoonlight.Host\VitaMoonlight.Host.csproj `
  -c Release --no-build -- self-test
```

Both GitHub workflows must pass on the same commit. Download, but do not mix,
their Windows and VPK artifacts.

## Primary PC and Vita

1. Run the new setup EXE over the current installation. Keep Sunshine,
   ViGEmBus, and the VDD selected. A reboot request must stop setup safely;
   reboot once, reopen the Administrator control panel, and continue.
2. Click **Apply recommended setup**, then **Run health check**. All required
   rows, including Stream rescue, must be ready. When idle, the physical
   monitor must be active and VDD must not be the only active display.
3. On the physical PC keyboard, press **Ctrl + Alt + Shift + F11**. The rescue
   agent must restore the physical topology, reload the virtual-display
   driver, and restart Sunshine without opening the control panel. A brief
   display blink is expected.
4. Install the matching VPK. Leave **Recommended** selected:
   960x544, 60 FPS, 8 Mbps, H.264 SDR, 1024-byte packets, frame pacing on,
   vblank wait off, performance overlay off, and file logging off.
5. Pair and start Steam Big Picture or Desktop. Confirm the stream uses the
   dedicated 960x544 display, fills the Vita panel correctly, and is not
   washed out. Open **Frame rate + network** briefly; normal motion should
   approach 60 FPS and the menu must remain responsive.
6. Verify the safety controls:
   - hold START, then press L + R; the overlay opens and none of those buttons
     reaches the PC;
   - press O once in a game; it produces one release/action, not a held input;
   - open the on-screen keyboard from the overlay and type in a non-secret test
     field;
   - single PS follows the selected local policy and double PS still provides
     the forced LiveArea escape.
7. If using **Steam / DS4 + gyro**, reconnect once and confirm Steam sees a DS4
   motion source. Gyro events may run at the requested 100 Hz, but diagnostic
   file logging must remain off unless reproducing a problem.
8. While streaming, press **Ctrl + Alt + Shift + F11** on the physical PC
   keyboard. The stream should end, the physical display should return within
   roughly 15 seconds, Sunshine should restart, and a new stream should work.
9. Reconnect, then disconnect normally from the Vita. The physical display
   must return without sign-out, reboot, or manual display settings.
10. Run the uninstaller. It must recover the physical display first, remove
    both scheduled safeguards and the host files, and follow the selected VDD
    retention/removal choice. If Windows reports that a removal is pending,
    the host and safeguards must remain; reboot and run uninstall again to
    verify cleanup.

## Laptop repeat

On the second PC, repeat steps 1-5 and 8-10. Include at least one
disconnect/reconnect while the laptop is on battery. Record Windows build,
GPU, driver version, network adapter, and whether this was an upgrade or clean
install.

## Known Vita Wi-Fi behavior

Long-pressing PS opens Vita system controls that can disable Wi-Fi while a
stream is active. The PC-side recovery should still restore the physical
display. Some Vita firmware/plugin combinations do not bring the radio back
for the running application; close Moonlight or restart the Vita if the system
Wi-Fi control remains wedged. Do not have the app silently override a Wi-Fi
choice the user made in the system UI.

## Result record

| Field | Result |
|---|---|
| Candidate commit | |
| Vita workflow URL | |
| Windows workflow URL | |
| Primary PC / Windows / GPU | |
| Laptop / Windows / GPU | |
| Vita model / firmware | |
| Upgrade install | Pass / Fail |
| 960x544 SDR stream | Pass / Fail |
| Input / overlay / keyboard | Pass / Fail |
| Ctrl+Alt+Shift+F11 recovery | Pass / Fail |
| Normal disconnect recovery | Pass / Fail |
| Uninstall | Pass / Fail |
| Known limitations | |

A failure to restore a physical display, a stuck input, an unsigned Windows
release binary, or a mismatch between VPK and host commits is a beta blocker.
