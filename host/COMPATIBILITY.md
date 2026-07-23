# Host compatibility and portability

## Supported release target

The all-in-one package targets **Windows 10 version 2004 (build 19041) or
newer, and Windows 11, on x64 Intel or AMD CPUs**. It uses the official AMD64
Sunshine installer and the pinned x64 signed Virtual Display Driver. Build
19041 is the minimum because the safe driver reload uses PnPUtil device
enable/restart commands introduced in that Windows release. The installer and
portable companion refuse older, non-x64, or non-client Windows before running
setup actions. Diagnostics remain available through `doctor`.

Sunshine supports hardware encoding on AMD, Intel, and NVIDIA GPUs. Actual
codec/encoder availability still depends on the installed GPU and vendor
driver. The Vita profile requests H.264, which is the broadest hardware encoder
path and the Vita's native hardware-decoder format.

Not currently packaged:

- Windows ARM64. Sunshine publishes ARM64 builds, but this release would also
  need an ARM64 companion and a release-qualified ARM64 VDD package.
- Windows Server. The bundled ViGEmBus project explicitly supports Windows
  10/11 rather than Server editions.
- Windows 7/8 and Windows 10 builds older than 19041. The current Sunshine,
  ViGEmBus, VDD, and safe device-reload path require a newer release.
- PCs with no writable `C:` volume. The pinned signed VDD binary reads
  `C:\VirtualDisplayDriver\vdd_settings.xml`; this path is imposed by that
  upstream driver even when Windows itself is installed elsewhere. Use Apollo
  on an unusual machine without a usable `C:` volume.

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
Use the same Windows account for setup and streaming. Supplying credentials for
a different Administrator account from a standard-user session is a required
release test and must not be advertised as supported until both scheduled
tasks are observed in the intended interactive session.

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
