# Host compatibility and portability

## Supported release target

The all-in-one package targets **Windows 10/11 x64 on Intel or AMD CPUs**. It
uses the official AMD64 Sunshine installer and the pinned x64 signed Virtual
Display Driver. The installer now refuses non-x64 Windows rather than allowing
an architecture-mismatched display driver to fail halfway through setup.

Sunshine supports hardware encoding on AMD, Intel, and NVIDIA GPUs. Actual
codec/encoder availability still depends on the installed GPU and vendor
driver. The Vita profile requests H.264, which is the broadest hardware encoder
path and the Vita's native hardware-decoder format.

Not currently packaged:

- Windows ARM64. Sunshine publishes ARM64 builds, but this release would also
  need an ARM64 companion and a release-qualified ARM64 VDD package.
- Windows Server. The bundled ViGEmBus project explicitly supports Windows
  10/11 rather than Server editions.
- Windows 7/8. The current Sunshine, ViGEmBus, and VDD stack targets Windows
  10/11.

## Machine layouts covered by the companion

- One or multiple connected physical monitors.
- Laptop internal panels and external/docked monitors.
- Systems that are on battery when recovery is needed; both scheduled tasks
  are configured to run on battery and after a missed trigger.
- Existing Sunshine installations in Program Files, Program Files (x86), a
  per-user Programs folder, or the directory registered by Sunshine's Windows
  service. `SUNSHINE_PATH` and `SUNSHINE_CONFIG_DIR` remain explicit overrides.
- Sunshine logs that emit display JSON properties in different orders.
- A disabled physical topology: emergency recovery enables every available
  physical display rather than assuming one particular monitor name.

The rescue hotkey and sign-in recovery triggers belong to the Windows account
used for streaming. This release supports one configured interactive streaming
account per PC; applying setup from another account reassigns the tasks to that
account. Simultaneous fast-user-switching sessions are not a release target.
Recovery state itself is machine-wide under `%ProgramData%\VitaMoonlight`.

The companion never selects a display by a developer's monitor model or device
path. It identifies only the managed VDD by its published driver identities and
uses Windows display APIs for the remaining physical displays.

## Items that still require real hardware testing

GitHub builds and self-tests run on both Windows Server 2022 and 2025 runner
images to catch framework/API and path-handling differences. Hosted runners do
not provide real consumer GPUs, monitors, HDR, Wi-Fi, suspend, ViGEm input, or
a physical Vita. The final checklist therefore still requires:

- Windows 10 and Windows 11 consumer machines when testers are available.
- At least two of AMD, Intel, and NVIDIA encoder families.
- A laptop/on-battery pass and a multi-monitor pass.
- Clean install, upgrade, uninstall, suspend, Wi-Fi-loss, game-close, and
  emergency-recovery tests.

Do not describe the package as universal until those hardware gates pass.
