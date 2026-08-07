# Community beta testing

Thank you for testing Vita Moonlight. Reports from different Windows versions,
GPUs, Wi-Fi adapters, display layouts, and Vita models are how this beta
becomes dependable on computers beyond the developer's own machine.

You do not need to be a developer. A clear partial test is useful.

## Pick a test

- Use the [minimum public-beta test](../host/BETA_SMOKE_TEST.md) for the
  shortest release-critical pass. One volunteer can complete its core path
  and leave the clearly marked lifecycle/hardware variants to other testers;
  list every skipped variant in the report.
- Use the [full end-to-end test](../host/END_TO_END_TEST.md) to exercise the
  complete UI, display lifecycle, controllers, gyro, touch, recovery, upgrade,
  repair, and uninstall paths.
- Use [Logging and support](LOGGING_AND_SUPPORT.md) only when a problem is
  reproducible or a maintainer asks for a capture.

The project especially needs results for:

- a genuinely clean first install;
- an in-place upgrade from a named older release;
- reinstalling and repairing the same release;
- installing the same VPK over itself and removing/reinstalling the Vita app;
- Windows 10 and Windows 11;
- AMD, Intel, and NVIDIA GPUs;
- laptop internal displays and recovery while on battery;
- sleep/resume recovery from both idle and interrupted-stream states;
- sleep initiated while the 15-second display test is actively switching;
- the Pause, restart, Enable host-feature lifecycle;
- two or more physical monitors;
- Sunshine installations that existed before Vita Moonlight Host; and
- uninstalling while keeping, and on disposable PCs removing, the shared
  Sunshine, ViGEmBus, and MTT VDD components.

## Before testing

1. Download `moonlight.vpk` and
   `Vita-Moonlight-Host-Setup-win-x64.exe` from the same GitHub release.
2. Save open Windows work and keep a keyboard connected to the PC.
3. Record whether this is a clean install, older-version upgrade, or
   same-version repair. Do not call an upgrade "clean" merely because setup
   completed successfully.
4. Take a screenshot of the current Windows display arrangement.
5. Know the emergency shortcut:
   **Ctrl + Alt + Shift + F11** restores physical displays from the PC
   keyboard without opening the control panel while Vita host features are
   enabled. A complete **Pause Vita host features** removes the rescue agent,
   so this shortcut is intentionally unavailable until the features are
   enabled again.

Do not deliberately crash display processes on a personal single-monitor PC.
Specialized crash-injection tests require a disposable test machine with
independent remote administration.

## How to write a useful report

A good report answers four questions:

1. Which exact build and hardware were used?
2. What were the shortest actions that caused the result?
3. What was expected, and what happened instead?
4. Could the user recover without rebooting or losing work?

Open a [GitHub issue](../../../issues/new/choose)
for a reproducible failure. Search existing issues first. Submit successful
test passes in the release's testing discussion if one is provided, or use a
GitHub issue when no collection thread exists.

Copy this template:

```text
Title: [Area] Short description

Release:
Commit, if shown:
VPK and installer came from the same release: Yes / No

Test type:
[ ] Clean first install
[ ] Upgrade from version:
[ ] Same-version reinstall/repair
[ ] Reinstall/repair while deliberately paused
[ ] Interrupted setup retry or direct-uninstall takeover (disposable VM)
[ ] Vita VPK reinstall/removal
[ ] Normal stream
[ ] Interrupted recovery
[ ] Sleep/resume recovery
[ ] Pause/restart/enable lifecycle
[ ] Laptop/on battery
[ ] Multiple monitors
[ ] Uninstall, shared components kept
[ ] Uninstall from deliberately paused state
[ ] Uninstall, shared components removed

Windows edition, version, and OS build:
Desktop or laptop:
CPU:
GPU and driver:
Sunshine version:
Network adapter and connection:
Physical monitors, scaling, and HDR:
Vita/PSTV model:
Vita firmware:
Relevant Vita plugins:
Streaming preset and controller profile:

Shortest reproduction:
1.
2.
3.

Expected:
Actual:
How often it happens:
Exact local time and time zone of the last reproduction:

Did the physical display return automatically:
Did Ctrl+Alt+Shift+F11 recover it:
Was a restart required:
Did an intentional Vita host-feature pause survive restart:
Did Enable leave Sunshine unchanged and restore the saved VDD/recovery state:

Host support report attached: Yes / No
Short redacted Vita log attached: Yes / No
Screenshots or video:
Additional notes:
```

Use one report per distinct problem. For example, a display-restoration
failure and a graphical-mapper layout defect need separate issues even if they
occurred during the same session.

## Host support report

The Windows control panel does not continuously log activity. To create a
point-in-time machine report:

1. Open **Vita Moonlight Host**.
2. Open **Diagnostics & support**.
3. Click **Save support report...**.
4. Open the saved JSON file in a text editor and review it before sharing.

The report captures the host version, Windows/platform state, component
readiness, managed display inventory, recovery state, and recommendation at
the moment it is created. It does not include a continuous history of your
session. Create it soon after the problem, before changing the setup. For a
Pause/Enable or sleep/resume report, use the exact comparison fields and sparse
rescue-record instructions in
[Logging and support](LOGGING_AND_SUPPORT.md#create-a-windows-host-support-report).

## Optional Vita support log

Support logging is off by default and is not needed for a successful report.
When a failure repeats, choose **Start support log** immediately before one
attempt, reproduce the problem once, then choose
**Stop and save support log**. Follow the
[logging guide](LOGGING_AND_SUPPORT.md).

Always include the exact local time and time zone of the reproduction. This is
what lets a maintainer align your written steps, UTC Vita records, Sunshine
events, and the host support report.

## Privacy checklist

Before uploading anything:

- remove usernames and personal folder names;
- remove host names and PC names;
- remove IP and MAC addresses unless a maintainer specifically needs a
  redacted comparison;
- remove pairing PINs, credentials, cookies, tokens, and certificates;
- do not show text typed through the on-screen keyboard; and
- crop unrelated applications, notifications, and browser tabs from images.

Logs and support reports are plain text/JSON. Review them with a text editor.
If safe redaction would destroy information needed to reproduce the issue,
describe the missing field instead of publishing the original value.

## How results are evaluated

Use these priorities in a report:

- **Release blocker:** physical display cannot be recovered, installer or
  uninstaller leaves Windows unsafe, stuck controller input, data/credential
  loss, or matching release artifacts cannot connect.
- **High:** repeatable stream crash, unusable video/controller path, or
  recovery needs a restart.
- **Normal:** quality, performance, compatibility, text/layout, or confusing
  workflow defect with a usable workaround.
- **Suggestion:** polish or a new option that does not block current use.

Do not guess at the root cause. "The Vita menu still drew over a black game
frame" is more useful than labeling it a driver crash without evidence.
