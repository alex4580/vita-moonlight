#!/usr/bin/env python3
"""Summarize Vita Moonlight structured support logs without exposing raw data.

The Vita logger writes one key=value record per line using schema
``vita-support-v1``. This tool deliberately retains only fields that the
schema defines as non-identifying diagnostics. Raw legacy messages,
unrecognized fields, and malformed input are counted but never reproduced.
"""

from __future__ import annotations

import argparse
import collections
import json
import re
import shlex
import sys
from dataclasses import dataclass
from typing import (
    FrozenSet,
    Iterable,
    List,
    Mapping,
    MutableMapping,
    Optional,
    Sequence,
    Tuple,
)


LOG_SCHEMA = "vita-support-v1"
SUMMARY_SCHEMA = "vita-support-summary-v1"
MAX_RECORD_LENGTH = 65536

EXIT_OK = 0
EXIT_INPUT_ERROR = 1
EXIT_NO_COMPATIBLE_RECORDS = 3
EXIT_SELF_TEST_FAILED = 4

BASE_FIELDS = frozenset(("ts", "schema", "session", "seq", "level", "event"))
EVENT_NAME_RE = re.compile(r"^[a-z][a-z0-9_.-]{0,95}$")
FIELD_NAME_RE = re.compile(r"^[A-Za-z][A-Za-z0-9_.-]{0,63}$")
SESSION_ID_RE = re.compile(r"^[0-9a-fA-F]{1,32}$")
TIMESTAMP_RE = re.compile(
    r"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,9})?Z$"
)
APP_VERSION_RE = re.compile(r"^\d{1,3}\.\d{1,3}\.\d{1,3}$")
HEX_VALUE_RE = re.compile(r"^0x[0-9A-Fa-f]{1,16}$")
GYRO_SCALAR_RE = re.compile(r"^[0-5](?:\.\d{1,2})?$")
FALLBACK_PAIR_RE = re.compile(
    r"(?:^|\s)([A-Za-z][A-Za-z0-9_.-]{0,63})=([^\s]+)"
)

# Values from any field absent from this table are intentionally discarded.
# This protects summaries when a malformed or future producer unexpectedly
# emits a hostname, path, endpoint, game title, or other identifying value.
EVENT_FIELDS: Mapping[str, Tuple[str, ...]] = {
    "session.start": ("reason", "capture", "privacy"),
    "session.end": (
        "reason",
        "duration_ms",
        "records",
        "warnings",
        "errors",
        "legacy_suppressed",
    ),
    "system.snapshot": (
        "app_version",
        "build",
        "platform",
        "model_id",
        "storage_mount",
        "decoder_backend",
        "identifiers",
    ),
    "config.snapshot": (
        "reason",
        "width",
        "height",
        "fps",
        "bitrate_kbps",
        "packet_size",
        "network_mode",
        "video_formats",
        "audio_config",
        "sops",
        "ref_invalidation",
        "frame_pacer",
        "vblank_wait",
        "scaling",
        "local_audio",
        "controller",
        "motion",
        "touch_mode",
        "gyro_horizontal",
        "gyro_vertical",
        "ps_mode",
        "shoulder_swap",
        "sprint",
        "sprint_window_ms",
        "front_touchzones",
        "mouse_accel",
        "keyboard_layout",
        "mapping_enabled",
        "overlay",
        "power_save_disabled",
    ),
    "connection.snapshot": ("state", "state_id", "stage_id", "connected"),
    "connection.state": ("previous", "state", "reason", "code"),
    "connection.stage": ("stage_id", "state", "code"),
    "stream.snapshot": (
        "state",
        "source",
        "width",
        "height",
        "fps",
        "bitrate_kbps",
        "packet_size",
        "video_formats",
        "audio_config",
        "controller",
        "motion",
        "frame_pacer",
        "vblank_wait",
        "scaling",
    ),
    "stream.action": (
        "action",
        "state",
        "phase",
        "reason",
        "code",
        "stage_id",
        "width",
        "height",
        "fps",
        "bitrate_kbps",
    ),
    "network.state": ("previous", "state"),
    "network.summary": (
        "sample_ms",
        "state",
        "rendered_fps",
        "target_fps",
        "video_kbps",
        "configured_kbps",
        "decoded_frames",
        "dropped_frames",
        "dropped_fps",
        "decode_avg_us",
        "decode_max_us",
        "rtt_ms",
        "rtt_variance_ms",
        "fec_recovered",
        "fec_failed",
        "out_of_sequence",
        "total_frames",
        "total_dropped",
    ),
    "decoder.state": (
        "state",
        "previous_state_id",
        "phase",
        "code",
        "backend",
        "format_mask",
        "width",
        "height",
        "refresh_hz",
        "flags",
        "codec",
        "texture_width",
        "texture_height",
        "ref_frames",
        "frame_pacer",
        "unit_bytes",
        "outputs",
        "repeats_suppressed",
    ),
    "motion.state": (
        "state",
        "reason",
        "sensor_type",
        "report_hz",
    ),
    # The human-readable legacy message is expressly not allowlisted.
    "error.legacy": ("source", "category", "repeats_suppressed"),
}

KNOWN_EVENTS = frozenset(EVENT_FIELDS)
ACTION_EVENTS = frozenset(("stream.action", "motion.state"))
LATEST_EVENT_KEYS: Mapping[str, str] = {
    "system.snapshot": "system",
    "config.snapshot": "configuration",
    "connection.snapshot": "connection_snapshot",
    "decoder.state": "decoder_state",
    "motion.state": "motion_state",
}

INTEGER_FIELDS = frozenset(
    (
        "seq",
        "duration_ms",
        "records",
        "warnings",
        "errors",
        "legacy_suppressed",
        "model_id",
        "width",
        "height",
        "fps",
        "bitrate_kbps",
        "packet_size",
        "sops",
        "ref_invalidation",
        "frame_pacer",
        "vblank_wait",
        "local_audio",
        "motion",
        "shoulder_swap",
        "sprint",
        "sprint_window_ms",
        "front_touchzones",
        "mouse_accel",
        "mapping_enabled",
        "power_save_disabled",
        "state_id",
        "stage_id",
        "connected",
        "sample_ms",
        "rendered_fps",
        "target_fps",
        "video_kbps",
        "configured_kbps",
        "decoded_frames",
        "dropped_frames",
        "dropped_fps",
        "decode_avg_us",
        "decode_max_us",
        "rtt_ms",
        "rtt_variance_ms",
        "fec_recovered",
        "fec_failed",
        "out_of_sequence",
        "total_frames",
        "total_dropped",
        "previous_state_id",
        "refresh_hz",
        "texture_width",
        "texture_height",
        "ref_frames",
        "unit_bytes",
        "outputs",
        "repeats_suppressed",
        "sensor_type",
        "report_hz",
    )
)

ENUM_FIELDS: Mapping[str, FrozenSet[str]] = {
    "reason": frozenset(
        (
            "attempt_begin",
            "capture_start",
            "display_settings",
            "error",
            "graceful",
            "input_settings",
            "invalid_minimize",
            "invalid_paired",
            "invalid_reset",
            "invalid_resume",
            "invalid_stream_started",
            "invalid_terminate_callback",
            "invalid_terminate_request",
            "no_video_frame",
            "no_video_traffic",
            "output_paused",
            "output_resumed",
            "pairing_ready",
            "profile_disabled",
            "protected_content",
            "requested",
            "settings_saved",
            "shutdown",
            "stream_started",
            "unexpected_early_termination",
            "user",
        )
    ),
    "capture": frozenset(("fresh",)),
    "privacy": frozenset(("identifiers_redacted",)),
    "build": frozenset(("VITA",)),
    "platform": frozenset(("vita",)),
    "storage_mount": frozenset(("ux0", "ur0", "uma0", "imc0", "xmc0", "other")),
    "decoder_backend": frozenset(("vita_hw_h264",)),
    "identifiers": frozenset(("redacted",)),
    "network_mode": frozenset(("local", "remote", "auto")),
    "scaling": frozenset(("fit", "crop_fill")),
    "controller": frozenset(("xbox", "dualshock4")),
    "touch_mode": frozenset(
        ("relative_mouse", "ds4_touchpad", "absolute_mouse", "tablet")
    ),
    "ps_mode": frozenset(
        ("local_double_tap", "safe_guide", "immediate_guide", "system_livearea")
    ),
    "keyboard_layout": frozenset(("en_us", "es_es", "es_latam")),
    "overlay": frozenset(("off", "framerate", "framerate_network", "advanced")),
    "state": frozenset(
        (
            "active",
            "cleanup_warning",
            "complete",
            "connected",
            "degraded",
            "disabled",
            "disconnected",
            "enabled",
            "error",
            "failed",
            "good",
            "idle",
            "ignored",
            "initializing",
            "minimized",
            "paired",
            "quarantined",
            "ready",
            "requested",
            "starting",
            "stopped",
            "unknown",
            "waiting",
        )
    ),
    "previous": frozenset(
        (
            "connected",
            "degraded",
            "disconnected",
            "good",
            "minimized",
            "paired",
            "ready",
            "unknown",
            "waiting",
        )
    ),
    "source": frozenset(("active", "compat", "negotiated", "selected")),
    "action": frozenset(
        (
            "close_foreground_game",
            "connect",
            "recover_display",
            "reconnect",
            "stop_stream_app",
        )
    ),
    "phase": frozenset(
        (
            "app_start",
            "decode",
            "host_init",
            "pacer_delete",
            "pacer_stop",
            "preflight",
            "refresh",
            "render_lock",
            "render_mutex_destroy",
            "setup",
            "state_reset",
            "stream_start",
        )
    ),
    "backend": frozenset(("vita_hw_h264",)),
    "codec": frozenset(("h264",)),
    "category": frozenset(
        ("audio", "motion", "decoder", "timeout", "transport", "memory", "internal")
    ),
}

HEX_FIELDS = frozenset(("video_formats", "audio_config", "format_mask", "flags"))
GYRO_SCALAR_FIELDS = frozenset(("gyro_horizontal", "gyro_vertical"))


@dataclass(frozen=True)
class Record:
    timestamp: Optional[str]
    session: str
    sequence: Optional[int]
    level: str
    event: str
    fields: Mapping[str, str]
    unknown_field_count: int
    malformed_token_count: int


@dataclass(frozen=True)
class ParsedLine:
    status: str
    record: Optional[Record] = None


def _safe_value(field: str, value: str) -> Optional[str]:
    """Return a bounded diagnostic scalar or discard it."""

    if not value or len(value) > 128:
        return None
    if field == "ts":
        return value if TIMESTAMP_RE.fullmatch(value) else None
    if field in INTEGER_FIELDS:
        if re.fullmatch(r"-?\d{1,20}", value):
            return value
        return None
    if field == "code":
        if (
            re.fullmatch(r"-?\d{1,20}", value)
            or re.fullmatch(r"0x[0-9A-Fa-f]{1,16}", value)
            or value in ("timeout", "cleanup_blocked")
        ):
            return value
        return None
    if field in HEX_FIELDS:
        return value if HEX_VALUE_RE.fullmatch(value) else None
    if field in GYRO_SCALAR_FIELDS:
        if not GYRO_SCALAR_RE.fullmatch(value):
            return None
        scalar = float(value)
        return value if 0.1 <= scalar <= 5.0 else None
    if field == "app_version":
        return value if APP_VERSION_RE.fullmatch(value) else None
    allowed = ENUM_FIELDS.get(field)
    if allowed is not None:
        return value if value in allowed else None
    return None


def _tokenize(line: str) -> Tuple[Mapping[str, str], int, bool]:
    """Parse shell-style key=value tokens with a conservative fallback."""

    malformed_tokens = 0
    used_fallback = False
    try:
        lexer = shlex.shlex(line, posix=True)
        lexer.whitespace_split = True
        lexer.commenters = ""
        tokens = list(lexer)
        pairs: MutableMapping[str, str] = collections.OrderedDict()
        for token in tokens:
            if "=" not in token:
                malformed_tokens += 1
                continue
            key, value = token.split("=", 1)
            if not FIELD_NAME_RE.fullmatch(key):
                malformed_tokens += 1
                continue
            if key in pairs:
                malformed_tokens += 1
            pairs[key] = value
        return pairs, malformed_tokens, used_fallback
    except ValueError:
        # An unmatched quote must not cause raw text to be printed or retained.
        # Recover only whitespace-delimited scalars so the envelope can still
        # be counted and grouped.
        used_fallback = True
        pairs = collections.OrderedDict(FALLBACK_PAIR_RE.findall(line))
        return pairs, 1, used_fallback


def parse_line(line: str) -> ParsedLine:
    if len(line) > MAX_RECORD_LENGTH:
        return ParsedLine("oversized")
    pairs, malformed_tokens, _ = _tokenize(line.rstrip("\r\n"))
    schema = pairs.get("schema")
    if schema is None:
        return ParsedLine("legacy_or_unstructured")
    if schema != LOG_SCHEMA:
        return ParsedLine("foreign_schema")

    event = pairs.get("event", "")
    session = pairs.get("session", "")
    level = pairs.get("level", "")
    if (
        not EVENT_NAME_RE.fullmatch(event)
        or not SESSION_ID_RE.fullmatch(session)
        or level not in ("info", "warn", "error")
    ):
        return ParsedLine("malformed_structured")

    timestamp = _safe_value("ts", pairs.get("ts", ""))
    sequence_text = _safe_value("seq", pairs.get("seq", ""))
    sequence = int(sequence_text) if sequence_text is not None else None
    if timestamp is None:
        malformed_tokens += 1
    if sequence is None:
        malformed_tokens += 1

    allowed = frozenset(EVENT_FIELDS.get(event, ()))
    fields: MutableMapping[str, str] = collections.OrderedDict()
    unknown_field_count = 0
    for key, value in pairs.items():
        if key in BASE_FIELDS:
            continue
        if key not in allowed:
            unknown_field_count += 1
            continue
        safe = _safe_value(key, value)
        if safe is None:
            malformed_tokens += 1
            continue
        fields[key] = safe

    return ParsedLine(
        "compatible",
        Record(
            timestamp=timestamp,
            session=session,
            sequence=sequence,
            level=level,
            event=event,
            fields=fields,
            unknown_field_count=unknown_field_count,
            malformed_token_count=malformed_tokens,
        ),
    )


def _integer(fields: Mapping[str, str], key: str) -> Optional[int]:
    try:
        return int(fields[key])
    except (KeyError, TypeError, ValueError):
        return None


def _bounded_append(items: List[Mapping[str, object]], value: Mapping[str, object], limit: int = 25) -> None:
    if len(items) < limit:
        items.append(value)


class SessionBuilder:
    def __init__(self, session_id: str) -> None:
        self.session_id = session_id
        self.record_count = 0
        self.first_timestamp: Optional[str] = None
        self.last_timestamp: Optional[str] = None
        self.first_sequence: Optional[int] = None
        self.last_sequence: Optional[int] = None
        self.previous_sequence: Optional[int] = None
        self.missing_sequence_count = 0
        self.nonincreasing_sequence_count = 0
        self.level_counts: collections.Counter[str] = collections.Counter()
        self.event_counts: collections.Counter[str] = collections.Counter()
        self.unknown_events: collections.Counter[str] = collections.Counter()
        self.unknown_field_count = 0
        self.malformed_token_count = 0
        self.latest: MutableMapping[str, Mapping[str, str]] = {}
        self.capture_start: Mapping[str, str] = {}
        self.capture_end: Mapping[str, str] = {}
        self.connection_transitions: List[Mapping[str, object]] = []
        self.connection_stages: List[Mapping[str, object]] = []
        self.actions: List[Mapping[str, object]] = []
        self.network_transitions: List[Mapping[str, object]] = []
        self.network_samples: List[Mapping[str, str]] = []
        self.warning_groups: MutableMapping[str, MutableMapping[str, object]] = collections.OrderedDict()
        self.error_groups: MutableMapping[str, MutableMapping[str, object]] = collections.OrderedDict()

    @staticmethod
    def _event_item(record: Record) -> Mapping[str, object]:
        item: MutableMapping[str, object] = collections.OrderedDict()
        if record.timestamp:
            item["timestamp"] = record.timestamp
        item.update(record.fields)
        return item

    def _record_problem(self, record: Record) -> None:
        if record.level not in ("warn", "error"):
            return
        groups = self.warning_groups if record.level == "warn" else self.error_groups
        group = groups.setdefault(
            record.event,
            collections.OrderedDict((("event", record.event), ("count", 0), ("examples", []))),
        )
        group["count"] = int(group["count"]) + 1
        examples = group["examples"]
        assert isinstance(examples, list)
        if len(examples) < 3:
            examples.append(self._event_item(record))

    def add(self, record: Record) -> None:
        self.record_count += 1
        self.level_counts[record.level] += 1
        self.event_counts[record.event] += 1
        self.unknown_field_count += record.unknown_field_count
        self.malformed_token_count += record.malformed_token_count

        if record.timestamp:
            if self.first_timestamp is None:
                self.first_timestamp = record.timestamp
            self.last_timestamp = record.timestamp
        if record.sequence is not None:
            if self.first_sequence is None:
                self.first_sequence = record.sequence
            self.last_sequence = record.sequence
            if self.previous_sequence is not None:
                if record.sequence > self.previous_sequence + 1:
                    self.missing_sequence_count += (
                        record.sequence - self.previous_sequence - 1
                    )
                elif record.sequence <= self.previous_sequence:
                    self.nonincreasing_sequence_count += 1
            self.previous_sequence = record.sequence

        event_is_known = record.event in KNOWN_EVENTS
        if not event_is_known:
            # Do not reproduce an unexpected event token. A future or malformed
            # producer could otherwise put identifying text in that position.
            self.unknown_events["unrecognized"] += 1
        if record.event == "session.start":
            self.capture_start = dict(record.fields)
        elif record.event == "session.end":
            self.capture_end = dict(record.fields)

        latest_key = LATEST_EVENT_KEYS.get(record.event)
        if latest_key and record.event != "stream.snapshot":
            self.latest[latest_key] = dict(record.fields)
        if record.event == "stream.snapshot":
            stream_key = (
                "stream_negotiated"
                if record.fields.get("source") == "negotiated"
                else "stream_selected"
            )
            self.latest[stream_key] = dict(record.fields)
        if record.event == "decoder.state" and record.fields.get("state") == "ready":
            self.latest["decoder_ready"] = dict(record.fields)

        item = self._event_item(record)
        if record.event == "connection.state":
            _bounded_append(self.connection_transitions, item)
        elif record.event == "connection.stage":
            if record.level == "error" or record.fields.get("state") == "failed":
                stage_item: MutableMapping[str, object] = collections.OrderedDict()
                stage_item["event"] = record.event
                stage_item.update(item)
                _bounded_append(self.connection_stages, stage_item)
        elif record.event in ACTION_EVENTS:
            action_item: MutableMapping[str, object] = collections.OrderedDict()
            action_item["event"] = record.event
            action_item.update(item)
            _bounded_append(self.actions, action_item)
        elif record.event == "network.state":
            _bounded_append(self.network_transitions, item)
        elif record.event == "network.summary":
            self.network_samples.append(dict(record.fields))

        if event_is_known:
            self._record_problem(record)

    @staticmethod
    def _network_summary(samples: Sequence[Mapping[str, str]], transitions: Sequence[Mapping[str, object]]) -> Mapping[str, object]:
        if not samples and not transitions:
            return {}

        result: MutableMapping[str, object] = collections.OrderedDict()
        states = [str(item.get("state")) for item in transitions if item.get("state")]
        states.extend(sample["state"] for sample in samples if sample.get("state"))
        if states:
            result["latest_state"] = states[-1]
            if "degraded" in states:
                result["worst_state"] = "degraded"
            elif "waiting" in states or "unknown" in states:
                result["worst_state"] = (
                    "waiting" if "waiting" in states else "unknown"
                )
            else:
                result["worst_state"] = states[-1]
        result["state_changes"] = len(transitions)
        result["sample_count"] = len(samples)

        def values(key: str) -> List[int]:
            collected: List[int] = []
            for sample in samples:
                value = _integer(sample, key)
                if value is not None:
                    collected.append(value)
            return collected

        sample_durations = values("sample_ms")
        if sample_durations:
            result["sampled_duration_ms"] = sum(sample_durations)

        for key, output_prefix, weight_key in (
            ("rendered_fps", "rendered_fps", "sample_ms"),
            ("dropped_fps", "dropped_fps", "sample_ms"),
            ("video_kbps", "video_kbps", "sample_ms"),
            ("rtt_ms", "rtt_ms", None),
            ("decode_avg_us", "decode_avg_us", "decoded_frames"),
        ):
            observed = values(key)
            if observed:
                weighted_values: List[Tuple[int, int]] = []
                if weight_key:
                    for sample in samples:
                        value = _integer(sample, key)
                        weight = _integer(sample, weight_key)
                        if value is not None and weight is not None and weight > 0:
                            weighted_values.append((value, weight))
                if weighted_values:
                    average = sum(
                        value * weight for value, weight in weighted_values
                    ) / sum(weight for _, weight in weighted_values)
                else:
                    average = sum(observed) / len(observed)
                result[f"{output_prefix}_average"] = round(average, 1)
                result[f"{output_prefix}_minimum"] = min(observed)
                result[f"{output_prefix}_maximum"] = max(observed)

        for key, output_key in (
            ("dropped_frames", "dropped_frames"),
            ("fec_recovered", "fec_recovered"),
            ("fec_failed", "fec_failed"),
            ("out_of_sequence", "out_of_sequence"),
        ):
            observed = values(key)
            if observed:
                result[output_key] = sum(observed)

        maximum_decode = values("decode_max_us")
        if maximum_decode:
            result["decode_max_us"] = max(maximum_decode)

        latest = samples[-1] if samples else {}
        for key in ("target_fps", "configured_kbps", "total_frames", "total_dropped"):
            value = _integer(latest, key)
            if value is not None:
                result[key] = value
        return result

    def finish(self) -> Mapping[str, object]:
        capture: MutableMapping[str, object] = collections.OrderedDict()
        if self.capture_start:
            capture["start"] = self.capture_start
        if self.capture_end:
            capture["end"] = self.capture_end
        capture["complete"] = bool(self.capture_end)

        result: MutableMapping[str, object] = collections.OrderedDict()
        result["session_id"] = self.session_id
        result["record_count"] = self.record_count
        result["first_timestamp"] = self.first_timestamp
        result["last_timestamp"] = self.last_timestamp
        result["first_sequence"] = self.first_sequence
        result["last_sequence"] = self.last_sequence
        result["sequence_anomalies"] = {
            "missing_records": self.missing_sequence_count,
            "nonincreasing_records": self.nonincreasing_sequence_count,
        }
        result["levels"] = dict(sorted(self.level_counts.items()))
        result["capture"] = capture

        for key in (
            "system",
            "configuration",
            "connection_snapshot",
            "stream_selected",
            "stream_negotiated",
            "decoder_ready",
            "decoder_state",
            "motion_state",
        ):
            if key in self.latest:
                result[key] = self.latest[key]

        result["connection_transitions"] = self.connection_transitions
        result["connection_failures"] = self.connection_stages
        result["actions"] = self.actions
        result["network"] = self._network_summary(
            self.network_samples, self.network_transitions
        )
        result["warnings"] = list(self.warning_groups.values())
        result["errors"] = list(self.error_groups.values())
        result["unknown_event_counts"] = dict(sorted(self.unknown_events.items()))
        result["unknown_field_count"] = self.unknown_field_count
        result["malformed_token_count"] = self.malformed_token_count
        return result


def summarize(lines: Iterable[str], source_name: str) -> Mapping[str, object]:
    counters: collections.Counter[str] = collections.Counter()
    sessions: MutableMapping[str, SessionBuilder] = collections.OrderedDict()

    for line in lines:
        counters["total_lines"] += 1
        parsed = parse_line(line)
        counters[parsed.status] += 1
        if parsed.record is None:
            continue
        sessions.setdefault(
            parsed.record.session, SessionBuilder(parsed.record.session)
        ).add(parsed.record)

    report: MutableMapping[str, object] = collections.OrderedDict()
    report["summary_schema"] = SUMMARY_SCHEMA
    # Only the final path component is retained to avoid leaking a username.
    if source_name == "-":
        report["source_name"] = "stdin"
    else:
        basename = re.split(r"[\\/]", source_name)[-1].lower()
        report["source_name"] = (
            basename
            if basename in ("moonlight.log", "moonlight.previous.log")
            else "support-log"
        )
    report["log_schema"] = LOG_SCHEMA
    report["compatible_record_count"] = counters["compatible"]
    report["total_line_count"] = counters["total_lines"]
    report["session_count"] = len(sessions)
    report["ignored_line_counts"] = {
        "legacy_or_unstructured": counters["legacy_or_unstructured"],
        "foreign_schema": counters["foreign_schema"],
        "malformed_structured": counters["malformed_structured"],
        "oversized": counters["oversized"],
    }
    report["sessions"] = [builder.finish() for builder in sessions.values()]
    return report


def _field_text(fields: Mapping[str, object], keys: Sequence[str]) -> str:
    parts = []
    for key in keys:
        value = fields.get(key)
        if value is not None and value != "":
            parts.append(f"{key}={value}")
    return ", ".join(parts)


def _profile_text(fields: Mapping[str, object]) -> str:
    width = fields.get("width")
    height = fields.get("height")
    fps = fields.get("fps")
    bitrate = fields.get("bitrate_kbps")
    parts = []
    if width and height:
        parts.append(f"{width}x{height}")
    if fps:
        parts.append(f"{fps} FPS")
    if bitrate:
        parts.append(f"{bitrate} kbps")
    extras = _field_text(
        fields,
        (
            "network_mode",
            "packet_size",
            "controller",
            "motion",
            "touch_mode",
            "scaling",
        ),
    )
    if extras:
        parts.append(extras)
    return " | ".join(parts) if parts else "not recorded"


def _transition_text(transitions: Sequence[Mapping[str, object]]) -> str:
    if not transitions:
        return "not recorded"
    states: List[str] = []
    first_previous = transitions[0].get("previous")
    if first_previous:
        states.append(str(first_previous))
    for transition in transitions:
        state = transition.get("state")
        if state and (not states or states[-1] != state):
            states.append(str(state))
    text = " -> ".join(states) if states else "state changes recorded"
    reason = transitions[-1].get("reason")
    code = transitions[-1].get("code")
    suffix = []
    if reason:
        suffix.append(f"last reason={reason}")
    if code and str(code) != "0":
        suffix.append(f"code={code}")
    if suffix:
        text += f" ({', '.join(suffix)})"
    return text


def _problem_text(groups: Sequence[Mapping[str, object]]) -> str:
    if not groups:
        return "none"
    return "; ".join(
        f"{group.get('event', 'unknown')} x{group.get('count', 0)}"
        for group in groups
    )


def render_human(report: Mapping[str, object]) -> str:
    lines = [
        f"Vita Moonlight support-log summary ({SUMMARY_SCHEMA})",
        f"Source: {report['source_name']}",
        (
            f"Compatible records: {report['compatible_record_count']} | "
            f"Sessions: {report['session_count']}"
        ),
    ]
    ignored = report["ignored_line_counts"]
    assert isinstance(ignored, Mapping)
    ignored_total = sum(int(value) for value in ignored.values())
    if ignored_total:
        lines.append(
            "Ignored safely: "
            f"{ignored.get('legacy_or_unstructured', 0)} legacy/unstructured, "
            f"{ignored.get('foreign_schema', 0)} other schema, "
            f"{ignored.get('malformed_structured', 0)} malformed structured, "
            f"{ignored.get('oversized', 0)} oversized"
        )
    lines.append(
        "Privacy: raw legacy text and unrecognized field values were not included."
    )

    sessions = report["sessions"]
    assert isinstance(sessions, Sequence)
    for index, session in enumerate(sessions, 1):
        assert isinstance(session, Mapping)
        lines.extend(("", f"Session {index}/{len(sessions)} - {session['session_id']}"))
        capture = session.get("capture", {})
        assert isinstance(capture, Mapping)
        capture_end = capture.get("end", {})
        assert isinstance(capture_end, Mapping)
        capture_text = (
            f"{session.get('first_timestamp') or 'unknown time'} -> "
            f"{session.get('last_timestamp') or 'unknown time'}"
        )
        duration = capture_end.get("duration_ms")
        if duration is not None:
            capture_text += f" | {duration} ms"
        capture_text += " | complete" if capture.get("complete") else " | interrupted/incomplete"
        lines.append(f"  Capture: {capture_text}")

        system = session.get("system", {})
        assert isinstance(system, Mapping)
        if system:
            lines.append(
                "  Vita: "
                + _field_text(
                    system,
                    (
                        "app_version",
                        "build",
                        "platform",
                        "model_id",
                        "storage_mount",
                        "decoder_backend",
                    ),
                )
            )

        configuration = session.get("configuration", {})
        assert isinstance(configuration, Mapping)
        if configuration:
            lines.append(f"  Selected profile: {_profile_text(configuration)}")
            input_text = _field_text(
                configuration,
                (
                    "controller",
                    "motion",
                    "gyro_horizontal",
                    "gyro_vertical",
                    "touch_mode",
                    "front_touchzones",
                    "mapping_enabled",
                    "ps_mode",
                    "shoulder_swap",
                    "sprint",
                    "sprint_window_ms",
                    "mouse_accel",
                    "keyboard_layout",
                ),
            )
            if input_text:
                lines.append(f"  Input: {input_text}")
            overlay_text = _field_text(
                configuration, ("overlay", "power_save_disabled")
            )
            if overlay_text:
                lines.append(f"  Vita options: {overlay_text}")

        transitions = session.get("connection_transitions", [])
        assert isinstance(transitions, Sequence)
        connection_text = _transition_text(transitions)
        if not transitions:
            snapshot = session.get("connection_snapshot", {})
            assert isinstance(snapshot, Mapping)
            if snapshot:
                connection_text = _field_text(
                    snapshot, ("state", "stage_id", "connected")
                )
        lines.append(f"  Connection: {connection_text}")

        stream = session.get("stream_negotiated") or session.get("stream_selected") or {}
        assert isinstance(stream, Mapping)
        if stream:
            lines.append(f"  Active stream: {_profile_text(stream)}")

        decoder = session.get("decoder_ready", {})
        assert isinstance(decoder, Mapping)
        if decoder:
            lines.append(
                "  Decoder: "
                + _field_text(
                    decoder,
                    (
                        "state",
                        "backend",
                        "codec",
                        "width",
                        "height",
                        "texture_width",
                        "texture_height",
                        "refresh_hz",
                        "ref_frames",
                        "frame_pacer",
                    ),
                )
            )
        decoder_state = session.get("decoder_state", {})
        assert isinstance(decoder_state, Mapping)
        if decoder_state and decoder_state != decoder:
            lines.append(
                "  Decoder latest: "
                + _field_text(
                    decoder_state,
                    ("state", "phase", "code", "repeats_suppressed"),
                )
            )

        network = session.get("network", {})
        assert isinstance(network, Mapping)
        if network:
            network_parts = [
                _field_text(
                    network,
                    (
                        "latest_state",
                        "worst_state",
                        "sample_count",
                        "sampled_duration_ms",
                    ),
                )
            ]
            fps_text = _field_text(
                network,
                (
                    "rendered_fps_average",
                    "rendered_fps_minimum",
                    "rendered_fps_maximum",
                    "target_fps",
                ),
            )
            quality_text = _field_text(
                network,
                (
                    "video_kbps_average",
                    "rtt_ms_average",
                    "rtt_ms_maximum",
                    "decode_max_us",
                    "dropped_frames",
                    "dropped_fps_average",
                    "fec_recovered",
                    "fec_failed",
                    "out_of_sequence",
                ),
            )
            network_parts.extend(part for part in (fps_text, quality_text) if part)
            lines.append("  Network: " + " | ".join(part for part in network_parts if part))

        actions = session.get("actions", [])
        assert isinstance(actions, Sequence)
        if actions:
            action_text = []
            for action in actions[:10]:
                assert isinstance(action, Mapping)
                event = action.get("event", "action")
                details = _field_text(
                    action,
                    ("action", "state", "phase", "reason", "stage_id", "code"),
                )
                action_text.append(f"{event}({details})" if details else str(event))
            if len(actions) > 10:
                action_text.append(f"+{len(actions) - 10} more")
            lines.append("  Actions: " + "; ".join(action_text))

        warnings = session.get("warnings", [])
        errors = session.get("errors", [])
        assert isinstance(warnings, Sequence)
        assert isinstance(errors, Sequence)
        lines.append(f"  Warnings: {_problem_text(warnings)}")
        lines.append(f"  Errors: {_problem_text(errors)}")

        failures = session.get("connection_failures", [])
        assert isinstance(failures, Sequence)
        if failures:
            lines.append(
                "  Connection failures: "
                + "; ".join(
                    _field_text(item, ("event", "stage_id", "operation", "state", "code"))
                    for item in failures
                    if isinstance(item, Mapping)
                )
            )

        unknown_events = session.get("unknown_event_counts", {})
        assert isinstance(unknown_events, Mapping)
        unknown_fields = int(session.get("unknown_field_count", 0))
        malformed_tokens = int(session.get("malformed_token_count", 0))
        sequence_anomalies = session.get("sequence_anomalies", {})
        assert isinstance(sequence_anomalies, Mapping)
        notes = []
        if unknown_events:
            notes.append(
                "new events="
                + ",".join(f"{key}:{value}" for key, value in unknown_events.items())
            )
        if unknown_fields:
            notes.append(f"unrecognized fields omitted={unknown_fields}")
        if malformed_tokens:
            notes.append(f"malformed tokens omitted={malformed_tokens}")
        if int(sequence_anomalies.get("missing_records", 0)):
            notes.append(
                "missing sequence records="
                f"{sequence_anomalies.get('missing_records', 0)}"
            )
        if int(sequence_anomalies.get("nonincreasing_records", 0)):
            notes.append(
                "duplicate/out-of-order sequence records="
                f"{sequence_anomalies.get('nonincreasing_records', 0)}"
            )
        if notes:
            lines.append("  Parser notes: " + " | ".join(notes))

        if capture_end:
            lines.append(
                "  End: "
                + _field_text(
                    capture_end,
                    (
                        "reason",
                        "duration_ms",
                        "records",
                        "warnings",
                        "errors",
                        "legacy_suppressed",
                    ),
                )
            )

    return "\n".join(lines) + "\n"


def _representative_log() -> str:
    return "\n".join(
        (
            "old raw failure at 192.0.2.40 on private-hostname",
            (
                "ts=2026-07-26T12:00:00.000001Z schema=vita-support-v1 "
                "session=abc123 seq=1 level=info event=session.start "
                "reason=user capture=fresh privacy=identifiers_redacted "
                "host=private-hostname"
            ),
            (
                "ts=2026-07-26T12:00:00.000002Z schema=vita-support-v1 "
                "session=abc123 seq=2 level=info event=system.snapshot "
                "app_version=0.14.6 build=VITA platform=vita model_id=1000 "
                "storage_mount=ux0 decoder_backend=vita_hw_h264 "
                "identifiers=redacted"
            ),
            (
                "ts=2026-07-26T12:00:00.000003Z schema=vita-support-v1 "
                "session=abc123 seq=3 level=info event=config.snapshot "
                "reason=capture_start width=960 height=544 fps=60 "
                "bitrate_kbps=8000 packet_size=1024 network_mode=local "
                "controller=dualshock4 motion=1 touch_mode=relative_mouse "
                "gyro_horizontal=1.20 gyro_vertical=0.80 "
                "ps_mode=local_double_tap shoulder_swap=0 sprint=0 "
                "sprint_window_ms=200 front_touchzones=0 mouse_accel=100 "
                "keyboard_layout=en_us mapping_enabled=0 overlay=off "
                "scaling=fit"
            ),
            (
                "ts=2026-07-26T12:00:01.000000Z schema=vita-support-v1 "
                "session=abc123 seq=4 level=info event=connection.state "
                "previous=disconnected state=ready reason=attempt_begin code=0"
            ),
            (
                "ts=2026-07-26T12:00:02.000000Z schema=vita-support-v1 "
                "session=abc123 seq=5 level=info event=connection.state "
                "previous=ready state=connected reason=stream_started code=0"
            ),
            (
                "ts=2026-07-26T12:00:02.100000Z schema=vita-support-v1 "
                "session=abc123 seq=6 level=info event=stream.snapshot "
                "state=connected source=negotiated "
                "width=960 height=544 fps=60 bitrate_kbps=8000 "
                "packet_size=1024 controller=dualshock4 motion=1 scaling=fit"
            ),
            (
                "ts=2026-07-26T12:00:02.200000Z schema=vita-support-v1 "
                "session=abc123 seq=7 level=info event=decoder.state "
                "state=ready backend=vita_hw_h264 codec=h264 width=960 "
                "height=544 texture_width=960 texture_height=544 "
                "refresh_hz=60 ref_frames=2 frame_pacer=1"
            ),
            (
                "ts=2026-07-26T12:00:12.000000Z schema=vita-support-v1 "
                "session=abc123 seq=8 level=info event=network.summary "
                "sample_ms=10000 state=good rendered_fps=60 target_fps=60 "
                "video_kbps=7600 configured_kbps=8000 decoded_frames=600 "
                "dropped_frames=0 dropped_fps=0 "
                "decode_avg_us=3200 decode_max_us=5000 "
                "rtt_ms=4 rtt_variance_ms=1 fec_recovered=1 fec_failed=0 "
                "out_of_sequence=0 total_frames=600 total_dropped=0"
            ),
            (
                "ts=2026-07-26T12:00:22.000000Z schema=vita-support-v1 "
                "session=abc123 seq=9 level=warn event=network.summary "
                "sample_ms=10000 state=degraded rendered_fps=55 target_fps=60 "
                "video_kbps=7500 configured_kbps=8000 decoded_frames=550 "
                "dropped_frames=50 dropped_fps=5 "
                "decode_avg_us=3500 decode_max_us=7000 "
                "rtt_ms=12 rtt_variance_ms=3 fec_recovered=4 fec_failed=1 "
                "out_of_sequence=2 total_frames=1150 total_dropped=5"
            ),
            (
                "ts=2026-07-26T12:00:23.000000Z schema=vita-support-v1 "
                "session=abc123 seq=10 level=info event=stream.action "
                "action=reconnect state=complete reason=display_settings "
                "width=960 height=544"
            ),
            (
                "ts=2026-07-26T12:00:24.000000Z schema=vita-support-v1 "
                "session=abc123 seq=11 level=error event=error.legacy "
                'source=compat category=transport message="connection failed '
                'at 192.0.2.40 private-hostname" repeats_suppressed=3'
            ),
            (
                "ts=2026-07-26T12:00:25.000000Z schema=vita-support-v1 "
                "session=abc123 seq=12 level=info event=session.end "
                "reason=user duration_ms=25000 records=12 warnings=1 errors=1 "
                "legacy_suppressed=3"
            ),
            (
                "ts=2026-07-26T13:00:00Z schema=vita-support-v1 "
                "session=def456 seq=1 level=info event=session.start reason=user"
            ),
            (
                "ts=2026-07-26T13:00:01Z schema=vita-support-v1 "
                "session=def456 seq=2 level=info event=session.end "
                "reason=shutdown duration_ms=1000 records=2 warnings=0 errors=0 "
                'broken="unterminated'
            ),
            "ts=2026-07-26T14:00:00Z schema=other-v1 session=x seq=1 "
            "level=info event=session.start",
        )
    )


def run_self_test() -> None:
    schema_fields = frozenset(
        field for fields in EVENT_FIELDS.values() for field in fields
    )
    validated_fields = (
        INTEGER_FIELDS
        | frozenset(ENUM_FIELDS)
        | HEX_FIELDS
        | GYRO_SCALAR_FIELDS
        | frozenset(("app_version", "code"))
    )
    assert schema_fields <= validated_fields
    assert _safe_value("code", "-1") == "-1"
    assert _safe_value("code", "0x80010001") == "0x80010001"
    assert _safe_value("code", "cleanup_blocked") == "cleanup_blocked"
    assert _safe_value("code", "192.0.2.40") is None
    assert _safe_value("gyro_horizontal", "1.20") == "1.20"
    assert _safe_value("gyro_vertical", "0.09") is None
    assert _safe_value("reason", "private-hostname") is None
    assert parse_line("x" * (MAX_RECORD_LENGTH + 1)).status == "oversized"

    report = summarize(_representative_log().splitlines(), "C:/Users/name/moonlight.log")
    assert report["compatible_record_count"] == 14
    assert report["session_count"] == 2
    assert report["source_name"] == "moonlight.log"

    sessions = report["sessions"]
    assert isinstance(sessions, Sequence)
    first = sessions[0]
    assert isinstance(first, Mapping)
    configuration = first["configuration"]
    assert isinstance(configuration, Mapping)
    assert configuration["gyro_horizontal"] == "1.20"
    assert configuration["gyro_vertical"] == "0.80"
    assert configuration["ps_mode"] == "local_double_tap"
    network = first["network"]
    assert isinstance(network, Mapping)
    assert network["sample_count"] == 2
    assert network["rendered_fps_average"] == 57.5
    assert network["dropped_frames"] == 50
    assert network["dropped_fps_average"] == 2.5
    assert network["worst_state"] == "degraded"
    # Both the deliberately injected host field and legacy message are
    # discarded rather than copied into the summary.
    assert first["unknown_field_count"] == 2
    assert first["errors"][0]["event"] == "error.legacy"
    assert "message" not in json.dumps(first["errors"])

    json_output = json.dumps(report, sort_keys=True)
    human_output = render_human(report)
    for secret in ("192.0.2.40", "private-hostname", "C:/Users/name"):
        assert secret not in json_output
        assert secret not in human_output
    assert "reconnect" in human_output
    assert "network.summary x1" in human_output

    second = sessions[1]
    assert isinstance(second, Mapping)
    assert second["capture"]["complete"] is True
    assert second["malformed_token_count"] >= 1

    renamed = summarize(
        _representative_log().splitlines(),
        "C:/Users/name/private-customer-case.log",
    )
    assert renamed["source_name"] == "support-log"
    assert "private-customer-case" not in json.dumps(renamed)

    # Every schema field is treated as hostile input. Even a known event and
    # allowlisted field name must not make an arbitrary hostname, endpoint, or
    # local path appear in either summary format.
    poison_lines: List[str] = []
    sequence = 0
    private_values = (
        "private-hostname",
        "192.0.2.40",
        "C:/Users/name/private-file",
    )
    for event, fields in EVENT_FIELDS.items():
        for field in fields:
            for private_value in private_values:
                sequence += 1
                poison_lines.append(
                    "ts=2026-07-26T15:00:00Z schema=vita-support-v1 "
                    f"session=badc0de seq={sequence} level=info "
                    f"event={event} {field}={private_value}"
                )
    sequence += 1
    poison_lines.append(
        "ts=2026-07-26T15:00:00Z schema=vita-support-v1 "
        f"session=badc0de seq={sequence} level=error "
        "event=private-hostname reason=private-hostname"
    )
    poison_report = summarize(poison_lines, "C:/Users/name/moonlight.log")
    poison_json = json.dumps(poison_report, sort_keys=True)
    poison_human = render_human(poison_report)
    for secret in private_values:
        assert secret not in poison_json
        assert secret not in poison_human


def _argument_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        description=(
            "Create a privacy-conscious summary of a Vita Moonlight "
            f"{LOG_SCHEMA} support log."
        ),
        epilog=(
            "Exit codes: 0 success, 1 input error, 2 command-line error, "
            "3 no compatible records, 4 self-test failure."
        ),
    )
    parser.add_argument(
        "logfile",
        nargs="?",
        help="moonlight.log to summarize, or - to read standard input",
    )
    parser.add_argument(
        "--json",
        action="store_true",
        help="write a machine-readable, sanitized JSON summary",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="run built-in parser, aggregation, and privacy checks",
    )
    return parser


def main(argv: Optional[Sequence[str]] = None) -> int:
    args = _argument_parser().parse_args(argv)
    if args.self_test:
        try:
            run_self_test()
        except Exception as error:  # noqa: BLE001 - CI needs a clear exit code.
            print(f"Support-log summarizer self-test failed: {error}", file=sys.stderr)
            return EXIT_SELF_TEST_FAILED
        print("Support-log summarizer self-test passed.")
        return EXIT_OK

    if not args.logfile:
        print("A log file is required unless --self-test is used.", file=sys.stderr)
        return EXIT_INPUT_ERROR

    try:
        if args.logfile == "-":
            report = summarize(sys.stdin, "-")
        else:
            with open(args.logfile, "r", encoding="utf-8", errors="replace") as stream:
                report = summarize(stream, args.logfile)
    except OSError as error:
        reason = error.strerror or error.__class__.__name__
        print(f"Could not read the support log ({reason}).", file=sys.stderr)
        return EXIT_INPUT_ERROR

    if int(report["compatible_record_count"]) == 0:
        print(
            f"No compatible {LOG_SCHEMA} records were found. "
            "Start a support log in the Vita app and reproduce the issue.",
            file=sys.stderr,
        )
        return EXIT_NO_COMPATIBLE_RECORDS

    if args.json:
        json.dump(report, sys.stdout, indent=2)
        sys.stdout.write("\n")
    else:
        sys.stdout.write(render_human(report))
    return EXIT_OK


if __name__ == "__main__":
    raise SystemExit(main())
