# Logging and support

Vita Moonlight has three different diagnostic tools. None is required for
normal play:

| Tool | Purpose | Writes a file? |
|---|---|---|
| Performance overlay | A small top-right FPS/network view during play. | No |
| Real-time diagnostics | A full Vita screen showing current session, decoder, network, controller, gyro, and support-log state. | No |
| Support log | A short, structured history for reproducing a problem. | Yes, only between Start and Stop |

Support logging is **off by default**. When it is off, Vita Moonlight does not
open, create, or write a support-log file during normal activity. Leave it off
unless you are reproducing a problem. During a capture, entries are buffered
and written in batches rather than forcing a storage write for every event. A
capture never remains enabled across an app restart.

With the performance overlay off, the diagnostics screen closed, and support
capture stopped, the Vita also disables optional FPS aggregation, extended
decode/transport counters, and diagnostic snapshots. It does not count gyro
or controller reports. Gyro sampling itself still runs when the user selects a
gyro-capable controller profile and the host requests motion reports, because
those reports are game input rather than diagnostics.

The supported Windows all-app handoff also does not force Sunshine to INFO,
tail `sunshine.log`, or parse global client events. Its authenticated stream
generation, sparse heartbeat lease, matching stop, and exact Sunshine-process
exit are the recovery signals. Setup preserves the user's Sunshine log level;
an upgrade from a release that previously owned `min_log_level = info` restores
the exact recorded prior value. Sunshine logging can still be enabled manually
for a specific Sunshine investigation, but it is not required for ordinary
streaming or display recovery.

## Create a clean Vita log

1. First write down:
   - the Vita Moonlight release;
   - the Windows host release;
   - the local clock time and time zone;
   - the selected streaming preset and controller profile; and
   - the shortest steps that reproduce the problem.
2. Start Vita Moonlight but do not choose **Start support log** until you are
   ready to reproduce the issue.
3. Start a capture in one of these ways:
   - before connecting: choose
     **Settings > System and support > Start support log**;
   - during a stream: choose **Start support log** on the main stream menu; or
   - on **Real-time diagnostics**, press **Triangle** while the Support log row
     says **Not capturing**.
4. Note the exact local time and time zone, reproduce the issue once, and
   avoid unrelated browsing or typing.
5. Choose **Stop and save support log**, or press Triangle again on Real-time
   diagnostics. This closes and flushes the capture. Exiting Vita Moonlight
   also closes it, but using Stop gives the clearest session boundary.
6. Open Real-time diagnostics and note the displayed log path.
7. Use VitaShell USB or FTP to copy the file to the PC.

The normal path is:

```text
ux0:data/moonlight/moonlight.log
```

Depending on storage and upgrade history, Vita Moonlight can use another path,
including `ux0:moonlight/moonlight.log`,
`uma0:data/moonlight/moonlight.log`, or `ux0:data/moonlight.log`. Do not guess:
use the exact **Support log file** path displayed by Real-time diagnostics.

The first **Start support log** creates a fresh `moonlight.log`. On later
captures, the current file becomes `moonlight.previous.log`, replacing the
one previous backup, and a fresh `moonlight.log` is created. Copy a capture
before starting another if you need to preserve it.

## What a useful log should contain

Every record uses one line of space-separated `key=value` fields. The common
prefix is:

```text
ts=... schema=vita-support-v1 session=... seq=... level=... event=...
```

`session` groups one intentional capture, `seq` gives the record order, and
`event` identifies its meaning. This makes the file readable in a text editor
and straightforward to filter or parse without relying on translated prose.

A complete capture begins with `session.start`, then records
`system.snapshot`, `config.snapshot`, and `connection.snapshot`. It ends with
`session.end`, including capture duration, record/warning/error counts, and
the number of noisy legacy messages that were suppressed.

The configuration snapshot records the selected stream profile plus the
controller type, touch mode, gyro on/off state, horizontal yaw and vertical
pitch sensitivity, PS-button mode, mapper/shoulder/sprint choices, keyboard
layout, overlay mode, and related non-identifying toggles. It records whether
a custom mapping is enabled, but not its filename or contents.

Within those boundaries, a readable capture is organized around state, not
raw input traffic. Current structured events include:

- `connection.state` and `connection.stage` for connection progress, failures,
  and termination reasons;
- `stream.snapshot` for selected or active stream properties;
- `stream.action` for connection/launch phases, controlled reconnect, game
  close, Sunshine-app termination, and display recovery;
- `decoder.state` for initialization, negotiated decoder properties,
  rate-limited decode failures, cleanup warnings, and shutdown;
- `motion.state` only when gyro negotiation changes;
- `network.state` when health changes; and
- `network.summary` about every 10 seconds while streaming, containing FPS,
  encoded rate, configured rate, RTT, decode time, frame drops, packet
  recovery, and cumulative frame totals.

Normal touch movement, precise touch coordinates, every controller sample,
typed characters, and every gyro sample should not generate a line. Relevant
input state changes may be summarized when diagnosing input negotiation, but a
held or moving control must not flood the file.

Older library messages are kept only when they look like errors, are converted
to `error.legacy`, and are repeat-limited. The `session.end` field
`legacy_suppressed` shows how many non-actionable or repeated legacy messages
were omitted.

## Read a capture quickly

Timestamps ending in `Z` are UTC. Start at `session.start`, confirm the
configuration and negotiated stream, then search for:

- `level=error` for a failed stage, decoder failure, or invalid transition;
- `level=warn` for degraded network summaries or recoverable cleanup;
- `event=connection.state` for connection and termination reasons;
- `event=network.state` for the moment delivery became degraded; and
- `event=network.summary` to compare the 10 seconds before and after the
  visible problem.

All records in one capture share the same `session` value. Increasing `seq`
values reveal a missing or out-of-order record without depending on wall-clock
format. Keep the entire short capture when reporting a problem rather than
copying only one error line.

If a short reproduction produces thousands of repetitive input lines, attach
only a small redacted sample and state that logging itself flooded. That is a
logging defect worth reporting.

## Optional automatic summary

You may simply review and attach the raw `moonlight.log`; Python is not
required for reporting a problem.

If Python 3 is already installed, the release also includes a privacy-conscious
summarizer. From PowerShell, use:

```powershell
python "$env:ProgramFiles\Vita Moonlight Host\tools\SupportLog\summarize-vita-log.py" "C:\path\to\moonlight.log"
```

From a source checkout, use:

```powershell
python tools\summarize-vita-log.py "C:\path\to\moonlight.log"
```

Add `--json` for machine-readable `vita-support-summary-v1` output. The tool
groups multiple capture sessions and summarizes only allowlisted schema
fields. It reports system/settings snapshots, connection stages, stream and
decoder state, network aggregates, actions, warnings, errors, session
boundaries, and malformed or missing records. It does not reproduce raw legacy
messages, unknown values, arbitrary filenames, or local paths.

Logs created by older beta builds did not contain `vita-support-v1` records.
The same command also recognizes their exact `[PERF]` lines and known
Moonlight network, audio, video, and recovery messages. Legacy results are a
whole-file aggregate because those logs have no reliable session boundaries.
They include FPS, bitrate, decode time, RTT, frame/packet totals, network-state
counts, and fixed recovery-event counters. The parser accepts only complete,
known line formats and fixed numeric fields. It discards the original text,
suppresses known touch/input lines, and ignores every other legacy message;
it never copies IP addresses, host names, paths, pairing material, typed text,
or arbitrary event content into either summary format.

`recognized_line_count` in the optional `legacy_summary` section is the number
of old lines that contributed to safe aggregates. `suppressed_input_line_count`
shows known noisy input lines that were deliberately not parsed, while
`unrecognized_line_count` shows all other old free-form lines that were
discarded. These counts do not mean the raw legacy file is safe to publish.

Review either output before sharing. The summarizer reduces accidental
disclosure; it cannot prove that every future value is harmless.

## Create a Windows host support report

The Windows control panel does not run a continuous host logger. It creates a
point-in-time support report only when requested:

1. Open **Vita Moonlight Host** after the problem.
2. Open **Diagnostics & support**.
3. Click **Save support report...**.
4. Save the JSON file somewhere easy to find.
5. Review it in a text editor before sharing.

The report records host/platform version, prerequisite readiness, Sunshine and
controller state, display inventory, SDR/display-lifecycle configuration,
recovery status, rescue shortcuts, and the current recommendation. It is
designed to be machine-readable across reports from many testers.

For a Pause/Enable comparison, save one report before Pause, one after the
paused PC restarts, and one after Enable. Compare these exact schema-version 3
fields:

| What to compare | JSON fields | Expected result |
|---|---|---|
| Requested lifecycle | `backendStatus`, `backendDesiredState`, `backendPreferencePersisted` | `Enabled` before, `Disabled` while paused, then `Enabled`; the preference is persisted after the first lifecycle choice. |
| Recovery safeguards | `recoveryTaskStatus`, `rescueAgentTaskStatus`, `rescueAgentRunning` | Healthy Enabled reports use `Present`, `Present`, `true`; a complete Pause uses `Missing`, `Missing`, `false`; Enable restores the baseline. `Unknown` is a failed inspection, not the same as missing. |
| Interactive task account | `scheduledTaskAccountReady` | `true` means the elevated process belongs to the interactive streaming account. `false` explains a setup/repair block caused by SYSTEM, a disconnected session, or different-account UAC without exposing either account name. |
| Managed Vita display | `backendManagedVddDeviceCount`, `backendManagedVddEnabledCount`, `backendManagedVddActive` | Healthy Enabled + Idle and Paused both keep the installed device count, report enabled count `0`, and keep the exact device PnP-disabled. An authenticated Vita preflight enables it for launch or resume; the matching stop returns it to `0`. |
| Physical safety | `backendActivePhysicalDisplayCount`, `recoveryPending` | At least one active physical display and no pending transaction are expected while idle or paused. |
| Shared host identity | `hostMode`, `sunshineInstalled`, `sunshineVersion` | These values must not change across Pause/Enable. |

The support report deliberately does not claim whether a shared Sunshine or
Apollo service is currently running or how it starts with Windows. For a
community Pause test, also note whether the already-open Sunshine web page was
reachable before Pause and remains reachable afterward. Pause controls only
Vita-owned host features; it is not a network-access control.

When the control panel is still open, **Copy technical details** copies the
most recent health or support output. Use that for a short issue description;
prefer the saved JSON report when comparing multiple PCs.

## Read the sparse Windows rescue records

The Windows companion keeps a small action record for display rescue,
emergency hotkeys, authenticated stream boundaries, and sleep/resume
decisions. This is separate from the optional Vita support log. It does not
sample input, continuously record a stream, or continuously tail Sunshine
while Enabled + Idle. Sunshine lifecycle observation is a fallback only while
a display transaction may need recovery after an abrupt disconnect or crash.

Open it without a terminal:

1. Open **Vita Moonlight Host**.
2. Choose **Diagnostics & support > Open diagnostics folder**.
3. Open `stream-rescue.log` in Notepad. Use the timestamp of the test to find
   the relevant lines.

The installed folder is normally:

```text
C:\Program Files\Vita Moonlight Host\state\Diagnostics
```

Each `stream-rescue.log` line has four tab-separated fields:

```text
UTC timestamp    action    True/False    summary
```

For a sleep/resume test, first confirm the physical monitor has its normal mode
and window placement and the managed device is PnP-disabled. Expect a successful
`power-suspend-display-prepare` line followed after wake by either a successful
`resume-display-check:` decision or a successful `recover-display-host` action
whose summary says it was triggered by resume. A `resume-display-check:`
summary includes:

- `physical=`: active physical display count;
- `managed Vita VDD=`: active managed virtual-display count;
- `restored physical modes=`: how many persisted physical modes were applied;
- `mode-repair warnings=`: warning count and, when nonzero, bracketed warning
  codes; and
- `session pending=`: whether a display transaction still exists.

A `recover-display-host` summary instead lists the recovery steps that ran,
including the resume trigger, physical-display activation, VDD reload when
needed, and shared-host restart when it was already running.

`stream-rescue-status.json` is an overwrite-in-place snapshot of only the
latest rescue result. Use `stream-rescue.log` when event order matters. The
sparse log is capped at roughly 512 KiB and resets with a rotation record when
that cap is reached.

After wake, the physical layout and window sizes/positions should match the
pre-sleep baseline, Windows should be normally responsive, no recovery
transaction should remain, and the managed device should again be PnP-disabled.

These files can contain display names and Windows error details. Review and
redact them before sharing, just as you would a support report. Do not confuse
their existence with Vita support logging: the Vita `moonlight.log` remains
off unless the user explicitly starts a capture.

## Capture only what the issue needs

| Problem | Evidence to include |
|---|---|
| Install, upgrade, or repair failure | Screenshot of the complete error, starting install state, Windows build, and host support report if available. |
| Wrong/black/washed-out display | Host support report, original display-layout screenshot, whether the Vita menu remained visible, and exact recovery result. |
| Compression, latency, or disconnect | Short Vita log covering still-to-motion behavior, preset, Wi-Fi layout, and exact time. |
| Controller or gyro negotiation | Controller profile, whether input reconnect was used, Steam Input result, Real-time diagnostics screenshot, and short Vita log. |
| Stuck/repeated input | Exact control, press/release sequence, mapper settings, video if possible, and short log. |
| UI overlap or bad text | Screenshot, Vita/PSTV model, page name, selected item, and language/layout settings. |
| Uninstall or recovery failure | Shared components selected for removal, exact message, whether F11 worked, and host support report created before retrying. |

Do not leave a support capture running for hours "just in case." A short
capture with an exact timestamp and written reproduction is faster to inspect
and places less load on Vita storage.

## Review and redact

Open both `.log` and `.json` files in a text editor. Remove or replace:

- Windows/Vita usernames and personal paths;
- host and device names;
- IP addresses, MAC addresses, and network names;
- pairing PINs, credentials, tokens, cookies, and certificates; and
- unrelated application or game information.

The support format redacts common IP addresses, MAC addresses, endpoints, and
Vita storage paths from converted legacy errors. The logger is not intended to
record typed characters or precise touch coordinates. Automatic redaction is
not a substitute for review: do not type passwords or private messages during
a capture, and inspect the file before posting it publicly.

Keep timestamps and numeric performance values intact. They are needed to
align the report with the reproduction.

## Attach the context

A log without context is rarely actionable. Include:

```text
Release and commit:
Clean install / older-version upgrade / same-version repair:
Windows build, GPU, and driver:
Sunshine version:
Vita model, firmware, and relevant plugins:
Network layout:
Physical monitors and HDR:
Preset and controller profile:
Exact local reproduction time and time zone:
Shortest reproduction:
Expected:
Actual:
Normal disconnect recovery:
Ctrl+Alt+Shift+F11 recovery:
```

Use the complete report template in
[Community testing](COMMUNITY_TESTING.md) and attach the shortest redacted
capture that includes the failure.
