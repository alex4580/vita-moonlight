#!/usr/bin/env python3
"""Fail the build when the Vita and Windows host stream contracts drift."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable


SCHEMA = "vita-moonlight/host-client-contract/v4"


class ContractFailure(Exception):
    pass


def _object_without_duplicate_keys(pairs: Iterable[tuple[str, Any]]) -> dict[str, Any]:
    result: dict[str, Any] = {}
    for key, value in pairs:
        if key in result:
            raise ContractFailure(f"duplicate JSON key: {key}")
        result[key] = value
    return result


def _load_contract(path: Path) -> dict[str, Any]:
    try:
        return json.loads(
            path.read_text(encoding="utf-8"),
            object_pairs_hook=_object_without_duplicate_keys,
        )
    except (OSError, json.JSONDecodeError, ContractFailure) as error:
        raise ContractFailure(f"cannot load {path}: {error}") from error


def _read(root: Path, relative_path: str) -> str:
    path = root / relative_path
    try:
        return path.read_text(encoding="utf-8")
    except OSError as error:
        raise ContractFailure(f"cannot read {relative_path}: {error}") from error


def _require(condition: bool, message: str) -> None:
    if not condition:
        raise ContractFailure(message)


def _require_keys(value: dict[str, Any], expected: set[str], location: str) -> None:
    actual = set(value)
    _require(
        actual == expected,
        f"{location} keys differ: expected {sorted(expected)}, found {sorted(actual)}",
    )


def _require_int(value: Any, location: str, minimum: int = 1) -> int:
    _require(
        isinstance(value, int) and not isinstance(value, bool) and value >= minimum,
        f"{location} must be an integer >= {minimum}",
    )
    return value


def _validate_contract(contract: dict[str, Any]) -> dict[str, Any]:
    _require_keys(
        contract,
        {"schema", "contract_version", "stream", "mode_control", "companion_control"},
        "contract",
    )
    _require(contract["schema"] == SCHEMA, f"schema must be {SCHEMA}")
    _require(contract["contract_version"] == 4, "contract_version must be 4 for schema v4")

    stream = contract["stream"]
    _require(isinstance(stream, dict), "stream must be an object")
    _require_keys(
        stream,
        {"resolutions", "allowed_frame_rates_fps", "desktop_refresh_hz", "capabilities"},
        "stream",
    )

    resolutions = stream["resolutions"]
    _require(isinstance(resolutions, list) and len(resolutions) == 3,
             "stream.resolutions must contain exactly three entries")
    parsed_resolutions: list[tuple[int, int]] = []
    roles: list[str] = []
    for index, resolution in enumerate(resolutions):
        location = f"stream.resolutions[{index}]"
        _require(isinstance(resolution, dict), f"{location} must be an object")
        _require_keys(resolution, {"width", "height", "role"}, location)
        width = _require_int(resolution["width"], f"{location}.width", 64)
        height = _require_int(resolution["height"], f"{location}.height", 64)
        role = resolution["role"]
        _require(isinstance(role, str) and role, f"{location}.role must be a string")
        parsed_resolutions.append((width, height))
        roles.append(role)
    _require(len(set(parsed_resolutions)) == 3, "stream resolutions must be unique")
    _require(len(set(roles)) == 3 and roles[0] == "vita-native",
             "resolution roles must be unique and the first mode must be vita-native")

    frame_rates = stream["allowed_frame_rates_fps"]
    _require(
        frame_rates == [24, 30, 40, 50, 60],
        "schema v2 stream frame rates must be [24, 30, 40, 50, 60]",
    )
    desktop_refresh = _require_int(stream["desktop_refresh_hz"], "desktop_refresh_hz")
    _require(desktop_refresh == 60, "schema v2 desktop_refresh_hz must be 60")

    capabilities = stream["capabilities"]
    _require(isinstance(capabilities, dict), "stream.capabilities must be an object")
    _require_keys(
        capabilities,
        {"video_codecs", "audio_configurations", "color_space", "color_range", "hdr"},
        "stream.capabilities",
    )
    _require(capabilities == {
        "video_codecs": ["H264"],
        "audio_configurations": ["STEREO"],
        "color_space": "REC_709",
        "color_range": "LIMITED",
        "hdr": False,
    }, "schema v2 capability profile must remain H.264, stereo, Rec.709 limited SDR")

    mode_control = contract["mode_control"]
    _require(isinstance(mode_control, dict), "mode_control must be an object")
    _require_keys(
        mode_control,
        {
            "authoritative_transport",
            "authenticated_stream_boundary",
            "sunshine_display_safety",
            "legacy_single_application_hook",
            "legacy_unacknowledged_hotkeys",
        },
        "mode_control",
    )
    _require(
        mode_control["authoritative_transport"] == "mutual-tls-stream-boundary-v1",
        "the mutually authenticated stream boundary must remain authoritative",
    )
    boundary = mode_control["authenticated_stream_boundary"]
    _require(isinstance(boundary, dict),
             "authenticated_stream_boundary must be an object")
    expected_boundary = {
        "protocol": "vita-moonlight-stream-boundary/1",
        "transport": "https-mutual-tls",
        "port_offset_from_sunshine_http": 23,
        "default_port": 48012,
        "connect_timeout_ms": 1000,
        "preauthentication_timeout_ms": 2000,
        "reached_operation_timeout_ms": 65000,
        "heartbeat_request_timeout_ms": 3000,
        "heartbeat_interval_ms": 10000,
        "prepared_lease_lifetime_ms": 240000,
        "started_lease_lifetime_ms": 45000,
        "heartbeat_checkpoint_interval_ms": 20000,
        "maximum_response_bytes": 256,
        "state_file_configuration_key": "file_state",
        "server_identity": "paired-sunshine-spki-sha256",
        "client_identity": "enabled-sunshine-named-device-certificate",
        "firewall_remote_addresses": "*",
        "prepare": {
            "method": "GET",
            "path": "/v1/prepare",
            "parameters": ["width", "height", "fps"],
            "success_body": (
                "vita-moonlight-stream-boundary/1 prepared "
                "generation=<32-lowercase-hex>"
            ),
        },
        "started": {
            "method": "GET",
            "path": "/v1/started",
            "parameters": ["generation"],
            "success_body": "vita-moonlight-stream-boundary/1 started",
        },
        "heartbeat": {
            "method": "GET",
            "path": "/v1/heartbeat",
            "parameters": ["generation"],
            "success_body": "vita-moonlight-stream-boundary/1 heartbeat",
        },
        "stop": {
            "method": "GET",
            "path": "/v1/stop",
            "parameters": ["generation"],
            "success_body": "vita-moonlight-stream-boundary/1 stopped",
        },
        "generation": "128-bit-random-lowercase-hex",
        "exact_generation_and_client_owner_required": True,
        "generic_sunshine_fallback": "connection-refused-or-pre-tls-timeout-only",
        "unauthenticated_endpoints": False,
    }
    _require(
        boundary == expected_boundary,
        "authenticated stream-boundary transport or downgrade policy drifted",
    )
    sunshine_safety = mode_control["sunshine_display_safety"]
    _require(
        sunshine_safety == {
            "scope": "all-applications",
            "native_display_management": "disabled",
            "pinned_output": "empty-default-active-output",
            "vita_global_prep_hook": "absent",
        },
        "Sunshine must leave display switching to the authenticated boundary",
    )
    _require(
        mode_control["legacy_single_application_hook"] == {
            "command": "session hook-start",
            "recognized_contract_mode": "normalize-to-fixed-desktop-refresh",
            "unrecognized_mode": "pass-through-no-op",
        },
        "the legacy single-application hook must normalize Vita modes and leave unrelated "
        "Moonlight client modes untouched",
    )
    legacy_control = mode_control["legacy_unacknowledged_hotkeys"]
    _require(isinstance(legacy_control, dict),
             "legacy_unacknowledged_hotkeys must be an object")
    _require_keys(
        legacy_control,
        {"availability", "sent_by_current_client", "modes"},
        "legacy_unacknowledged_hotkeys",
    )
    _require(
        legacy_control["availability"] ==
        "explicit-legacy-single-application-fallback-only",
        "legacy mode hotkeys must be restricted to the explicit single-application fallback",
    )
    _require(
        legacy_control["sent_by_current_client"] is False,
        "the current Vita client must not send legacy display-mode hotkeys",
    )
    legacy = legacy_control["modes"]
    expected_legacy = [
        (960, 540, 60, "F8", 0x77),
        (960, 544, 60, "F9", 0x78),
        (1280, 720, 60, "F10", 0x79),
    ]
    _require(isinstance(legacy, list), "legacy_unacknowledged_hotkeys.modes must be an array")
    actual_legacy: list[tuple[int, int, int, str, int]] = []
    for index, hotkey in enumerate(legacy):
        location = f"legacy_unacknowledged_hotkeys.modes[{index}]"
        _require(isinstance(hotkey, dict), f"{location} must be an object")
        _require_keys(
            hotkey,
            {"width", "height", "desktop_refresh_hz", "key", "windows_virtual_key"},
            location,
        )
        actual_legacy.append((
            _require_int(hotkey["width"], f"{location}.width"),
            _require_int(hotkey["height"], f"{location}.height"),
            _require_int(hotkey["desktop_refresh_hz"], f"{location}.desktop_refresh_hz"),
            hotkey["key"],
            _require_int(hotkey["windows_virtual_key"], f"{location}.windows_virtual_key"),
        ))
    _require(actual_legacy == expected_legacy,
             "schema v2 legacy mode hotkeys must remain F8/F9/F10 for the three display modes")
    _require(
        {(width, height) for width, height, _, _, _ in actual_legacy} == set(parsed_resolutions),
        "legacy mode hotkeys must cover exactly the contracted resolutions",
    )

    control = contract["companion_control"]
    _require(isinstance(control, dict), "companion_control must be an object")
    _require_keys(
        control,
        {"transport", "acknowledgement", "modifiers", "actions"},
        "companion_control",
    )
    _require(control["transport"] == "moonlight-encrypted-keyboard-input",
             "schema v2 companion control must use the Moonlight input channel")
    _require(control["acknowledgement"] == "none",
             "schema v2 hotkey actions are unacknowledged")

    expected_modifiers = [
        {"key": "CONTROL", "windows_virtual_key": 0x11},
        {"key": "ALT", "windows_virtual_key": 0x12},
        {"key": "SHIFT", "windows_virtual_key": 0x10},
    ]
    _require(control["modifiers"] == expected_modifiers,
             "companion modifiers must remain Control+Alt+Shift")

    expected_actions = [
        {
            "id": "recover-physical-display",
            "key": "F11",
            "windows_virtual_key": 0x7A,
            "host_action": "display-recovery",
            "disconnect_after_send": True,
        }
    ]
    _require(control["actions"] == expected_actions,
             "companion actions must be exactly F11 display recovery")

    return {
        "resolutions": parsed_resolutions,
        "frame_rates": frame_rates,
        "desktop_refresh": desktop_refresh,
        "capabilities": capabilities,
        "legacy": actual_legacy,
        "boundary": boundary,
        "sunshine_safety": sunshine_safety,
        "legacy_hook": mode_control["legacy_single_application_hook"],
        "modifiers": expected_modifiers,
        "actions": expected_actions,
    }


def _array_body(text: str, declaration_pattern: str, location: str) -> str:
    match = re.search(declaration_pattern + r"\s*=\s*\{(?P<body>.*?)\};", text, re.DOTALL)
    _require(match is not None, f"cannot find {location}")
    return match.group("body")


def _parse_c_resolution_array(text: str, declaration_pattern: str, location: str) -> list[tuple[int, int]]:
    body = _array_body(text, declaration_pattern, location)
    return [(int(width), int(height)) for width, height in re.findall(r"\{\s*(\d+)\s*,\s*(\d+)\s*\}", body)]


def _parse_c_int_array(text: str, declaration_pattern: str, location: str) -> list[int]:
    body = _array_body(text, declaration_pattern, location)
    return [int(value) for value in re.findall(r"\b\d+\b", body)]


def _parse_define(text: str, name: str, location: str) -> int:
    match = re.search(rf"^\s*#define\s+{re.escape(name)}\s+(\d+)\s*$", text, re.MULTILINE)
    _require(match is not None, f"cannot find {name} in {location}")
    return int(match.group(1))


def _extract_braced_block(text: str, start_pattern: str, location: str) -> str:
    start = re.search(start_pattern, text, re.DOTALL)
    _require(start is not None, f"cannot find {location}")
    brace = text.find("{", start.start())
    _require(brace >= 0, f"cannot find opening brace for {location}")
    depth = 0
    for index in range(brace, len(text)):
        if text[index] == "{":
            depth += 1
        elif text[index] == "}":
            depth -= 1
            if depth == 0:
                return text[brace:index + 1]
    raise ContractFailure(f"cannot find closing brace for {location}")


def _check_vita(root: Path, values: dict[str, Any]) -> None:
    config = _read(root, "src/config.c")
    settings = _read(root, "src/gui/ui_settings.c")
    overlay = _read(root, "src/gui/ui_stream_overlay.c")
    overlay_header = _read(root, "src/gui/ui_stream_overlay.h")
    connect = _read(root, "src/gui/ui_connect.c")
    gamestream = _read(root, "libgamestream/client.c")
    bridge_protocol = _read(root, "libgamestream/bridge_protocol.h")
    bridge_parser = _read(root, "libgamestream/bridge_protocol.c")
    http = _read(root, "libgamestream/http.c")

    resolutions = values["resolutions"]
    frame_rates = values["frame_rates"]
    native_width, native_height = resolutions[0]
    refresh = values["desktop_refresh"]

    settings_resolutions = _parse_c_resolution_array(
        settings,
        r"static\s+int\s+RESOLUTIONS\s*\[[^;=]*\]",
        "Vita settings resolution array",
    )
    overlay_resolutions = _parse_c_resolution_array(
        overlay,
        r"static\s+const\s+int\s+resolutions\s*\[[^;=]*\]",
        "in-stream resolution array",
    )
    _require(settings_resolutions == resolutions,
             f"Vita settings resolutions drifted: {settings_resolutions} != {resolutions}")
    _require(overlay_resolutions == resolutions,
             f"in-stream resolutions drifted: {overlay_resolutions} != {resolutions}")

    overlay_frame_rates = _parse_c_int_array(
        overlay,
        r"static\s+const\s+int\s+frame_rates\s*\[[^;=]*\]",
        "in-stream frame-rate array",
    )
    settings_fps_match = re.search(
        r"char\s*\*settings\s*\[\]\s*=\s*\{(?P<body>[^}]*)\}",
        settings,
    )
    _require(settings_fps_match is not None, "cannot find Vita settings frame-rate choices")
    settings_frame_rates = [
        int(value) for value in re.findall(r'"(\d+)"', settings_fps_match.group("body"))
    ]
    _require(overlay_frame_rates == frame_rates,
             f"in-stream frame rates drifted: {overlay_frame_rates} != {frame_rates}")
    _require(settings_frame_rates == frame_rates,
             f"Vita settings frame rates drifted: {settings_frame_rates} != {frame_rates}")

    config_resolutions = _parse_c_resolution_array(
        config,
        r"static\s+const\s+int\s+VITA_STREAM_CONTRACT_RESOLUTIONS\s*\[[^;=]*\]",
        "Vita loaded-config resolution contract",
    )
    config_frame_rates = _parse_c_int_array(
        config,
        r"static\s+const\s+int\s+VITA_STREAM_CONTRACT_FRAME_RATES\s*\[[^;=]*\]",
        "Vita loaded-config frame-rate contract",
    )
    _require(config_resolutions == resolutions,
             f"Vita loaded-config resolutions drifted: {config_resolutions} != {resolutions}")
    _require(config_frame_rates == frame_rates,
             f"Vita loaded-config frame rates drifted: {config_frame_rates} != {frame_rates}")
    _require(
        "contract_supports_resolution(" in config and
        "contract_supports_frame_rate(config->stream.fps)" in config,
        "config_sanitize must reject modes outside the shared host/client contract",
    )

    _require(_parse_define(config, "DEFAULT_STREAM_WIDTH", "src/config.c") == native_width,
             "Vita default width differs from the native contract mode")
    _require(_parse_define(config, "DEFAULT_STREAM_HEIGHT", "src/config.c") == native_height,
             "Vita default height differs from the native contract mode")
    _require(_parse_define(config, "DEFAULT_STREAM_FPS", "src/config.c") == refresh,
             "Vita default FPS differs from the contract default")

    required_profile_lines = [
        f"config.stream.width = {native_width};",
        f"config.stream.height = {native_height};",
        "config.stream.audioConfiguration = AUDIO_CONFIGURATION_STEREO;",
        "config.stream.supportedVideoFormats = VIDEO_FORMAT_H264;",
        f"config.stream.clientRefreshRateX100 = {refresh * 100};",
        "config.stream.colorSpace = COLORSPACE_REC_709;",
        "config.stream.colorRange = COLOR_RANGE_LIMITED;",
        "config->stream.audioConfiguration = AUDIO_CONFIGURATION_STEREO;",
        "config->stream.supportedVideoFormats = VIDEO_FORMAT_H264;",
        f"config->stream.clientRefreshRateX100 = {refresh * 100};",
        "config->stream.colorSpace = COLORSPACE_REC_709;",
        "config->stream.colorRange = COLOR_RANGE_LIMITED;",
    ]
    for line in required_profile_lines:
        _require(line in config, f"Vita config is missing contracted profile assignment: {line}")

    _require(
        "gs_start_app(&server, &config.stream" in connect,
        "Vita launch must pass the contracted stream configuration to gs_start_app",
    )
    _require(
        "config->sops = true;" in config and
        "SETTINGS_SOPS" not in settings and
        "STREAM_HOST_OPTIMIZE" not in overlay,
        "the managed-host launch-mode contract must be mandatory, not a client toggle",
    )
    _require(
        "LiStartConnection(&server.serverInfo, &config.stream" in connect,
        "Vita connection must pass the same contracted stream configuration to Moonlight",
    )
    boundary = values["boundary"]
    expected_defines = {
        "VITA_STREAM_BOUNDARY_PROTOCOL": f'"{boundary["protocol"]}"',
        "VITA_STREAM_BOUNDARY_PREPARE_PATH": f'"{boundary["prepare"]["path"]}"',
        "VITA_STREAM_BOUNDARY_STARTED_PATH": f'"{boundary["started"]["path"]}"',
        "VITA_STREAM_BOUNDARY_HEARTBEAT_PATH": f'"{boundary["heartbeat"]["path"]}"',
        "VITA_STREAM_BOUNDARY_STOP_PATH": f'"{boundary["stop"]["path"]}"',
        "VITA_STREAM_BOUNDARY_PORT_OFFSET": (
            f'{boundary["port_offset_from_sunshine_http"]}u'
        ),
        "VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS": "32u",
        "VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES": (
            f'{boundary["maximum_response_bytes"]}u'
        ),
        "VITA_STREAM_BOUNDARY_HEARTBEAT_INTERVAL_MS": (
            f'{boundary["heartbeat_interval_ms"]}u'
        ),
        "VITA_STREAM_BOUNDARY_HEARTBEAT_TIMEOUT_MS": (
            f'{boundary["heartbeat_request_timeout_ms"]}L'
        ),
    }
    for name, expected in expected_defines.items():
        _require(
            re.search(
                rf"^#define\s+{re.escape(name)}\s+{re.escape(expected)}$",
                bridge_protocol,
                re.MULTILINE,
            ) is not None,
            f"Vita stream-boundary constant {name} drifted",
        )
    _require(
        "stream_boundary_parse_prepared" in bridge_parser and
        "is_lower_hex_generation" in bridge_parser and
        "stream_boundary_parse_started" in bridge_parser and
        "stream_boundary_parse_heartbeat" in bridge_parser and
        "stream_boundary_parse_stopped" in bridge_parser and
        "!tls_established" in bridge_parser and
        "STREAM_BOUNDARY_TRANSPORT_TIMEOUT" in bridge_parser,
        "Vita must parse exact versioned lease responses",
    )
    prepare_call = connect.find("gs_prepare_stream_boundary(")
    launch_call = connect.find("gs_start_app(&server, &config.stream")
    started_call = connect.find("gs_started_stream_boundary(")
    heartbeat_start = connect.find("start_stream_boundary_heartbeat()")
    media_call = connect.find("LiStartConnection(&server.serverInfo, &config.stream")
    _require(
        0 <= prepare_call < launch_call < started_call < heartbeat_start < media_call,
        "Vita must prepare, launch/resume, confirm started, and arm heartbeat before media",
    )
    release_boundary = _extract_braced_block(
        connect,
        r"static\s+bool\s+release_stream_boundary\s*\(",
        "Vita stream-boundary release",
    )
    _require(
        release_boundary.find("stop_stream_boundary_heartbeat()") <
        release_boundary.find("active_stream_boundary_generation[0] = '\\0';") <
        release_boundary.find("gs_stop_stream_boundary(") and
        "The Windows rescue agent will keep trying automatically." in
        release_boundary,
        "Vita must stop heartbeat, consume a generation once, and delegate failed restore to the host observer",
    )
    main_source = _read(root, "src/main.c")
    release_host_state = _extract_braced_block(
        connect,
        r"static\s+bool\s+release_host_client_state_with_options\s*\(",
        "Vita ordered host-client release",
    )
    _require(
        "ui_connect_shutdown();" in main_source and
        "ui_connect_stream_boundary_local_cleanup_ready();" in main_source and
        "connection_wait_for_termination();" in release_host_state and
        release_host_state.find("connection_wait_for_termination();") <
        release_host_state.find("release_stream_boundary(show_restore_error)") <
        release_host_state.find("gs_cleanup(&server);") and
        "if (!ui_connect_stream_boundary_local_cleanup_ready())" in connect and
        "VITA_STREAM_BOUNDARY_HEARTBEAT_INTERVAL_MS" in connect and
        "gs_heartbeat_stream_boundary(" in connect and
        "sceKernelWaitEventFlag(" in connect,
        "Vita shutdown must join media, stop the event-driven heartbeat, and only then release host/CURL state",
    )
    _require(
        "release_host_client_state_with_options(true)" in connect and
        "sceKernelDelayThread(1000 * 1000)" not in connect and
        "action=stop state=observer_pending local_cleanup=complete" in connect,
        "ordinary disconnect must use one ordered cleanup owner and treat remote restore separately from local safety",
    )
    bridge_http = _extract_braced_block(
        http,
        r"HTTP_BRIDGE_RESULT\s+http_bridge_request_with_timeout_ms\s*\(",
        "bounded bridge HTTP request",
    )
    _require(
        "CURLOPT_CONNECTTIMEOUT_MS, 1000L" in bridge_http and
        "CURLOPT_TIMEOUT_MS, timeoutMs" in bridge_http and
        "CURLINFO_APPCONNECT_TIME_T" in bridge_http and
        "STREAM_BOUNDARY_TRANSPORT_REFUSED" in bridge_http and
        "STREAM_BOUNDARY_TRANSPORT_TIMEOUT" in bridge_http and
        "HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE" in bridge_http and
        "CURLOPT_FORBID_REUSE, 0L" in bridge_http and
        "CURLE_SSL_PINNEDPUBKEYNOTMATCH" in bridge_http,
        "generic Sunshine fallback must be bounded to refusal/timeout and restore HTTP state",
    )
    optional_return = bridge_http.find(
        "bridgeResult = HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE;")
    refused_check = bridge_http.find("result == CURLE_COULDNT_CONNECT")
    timeout_check = bridge_http.find("result == CURLE_OPERATION_TIMEDOUT")
    tls_check = bridge_http.find("failure, appConnectTime > 0")
    _require(
        0 <= refused_check < optional_return and
        0 <= timeout_check < optional_return and
        0 <= tls_check < optional_return and
        "http_bridge_request_with_timeout_ms(\n      url, data, responseCode, 65000L)" in http and
        "VITA_STREAM_BOUNDARY_HEARTBEAT_TIMEOUT_MS" in gamestream and
        bridge_http.count("HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE") == 1,
        "no reached TLS/auth/protocol failure may downgrade to generic Sunshine",
    )
    compact_gamestream = re.sub(r"\s+", " ", gamestream)
    _require(
        "mode=%dx%dx%d" in gamestream and
        "config->width, config->height, fps, sops" in compact_gamestream,
        "GameStream launch URL must carry the selected width, height, and FPS",
    )
    launch_contract = _extract_braced_block(
        gamestream,
        r"static\s+bool\s+is_vita_contract_mode\s*\(",
        "libgamestream Vita launch contract",
    )
    launch_resolutions = _parse_c_resolution_array(
        launch_contract,
        r"static\s+const\s+unsigned\s+short\s+resolutions\s*\[[^;=]*\]",
        "libgamestream launch resolutions",
    )
    launch_frame_rates = _parse_c_int_array(
        launch_contract,
        r"static\s+const\s+unsigned\s+char\s+frameRates\s*\[[^;=]*\]",
        "libgamestream launch frame rates",
    )
    _require(launch_resolutions == resolutions,
             f"libgamestream launch resolutions drifted: {launch_resolutions} != {resolutions}")
    _require(launch_frame_rates == frame_rates,
             f"libgamestream launch frame rates drifted: {launch_frame_rates} != {frame_rates}")
    _require(
        "!correct_mode && !(sops && is_vita_contract_mode(config))" in compact_gamestream,
        "unadvertised GameStream modes may bypass the host list only for the bounded Vita SOPS contract",
    )

    helper = _extract_braced_block(
        connect,
        r"static\s+void\s+send_host_rescue_hotkey\s*\(",
        "send_host_rescue_hotkey",
    )
    keyboard_events = re.findall(
        r"LiSendKeyboardEvent\((0x[0-9A-Fa-f]+|virtual_key)\s*,\s*"
        r"(KEY_ACTION_DOWN|KEY_ACTION_UP)",
        helper,
    )
    expected_down = [
        (f"0x{modifier['windows_virtual_key']:02X}".lower(), "KEY_ACTION_DOWN")
        for modifier in values["modifiers"]
    ]
    expected_up = [
        (f"0x{modifier['windows_virtual_key']:02X}".lower(), "KEY_ACTION_UP")
        for modifier in reversed(values["modifiers"])
    ]
    normalized_events = [(key.lower(), action) for key, action in keyboard_events]
    _require(
        normalized_events ==
        expected_down +
        [("virtual_key", "KEY_ACTION_DOWN"), ("virtual_key", "KEY_ACTION_UP")] +
        expected_up,
        "Vita companion hotkey must press Control+Alt+Shift, tap the action key, "
        "and release every modifier in reverse order",
    )
    literal_action_keys = [
        int(value, 16)
        for value in re.findall(r"send_host_rescue_hotkey\(0x([0-9A-Fa-f]+)\)", connect)
    ]
    _require(
        sorted(literal_action_keys) == sorted(action["windows_virtual_key"] for action in values["actions"]),
        "Vita companion action calls must be exactly the contracted F11 recovery action",
    )

    _require(
        "resolution_virtual_key" not in overlay and
        "apply_display_virtual_key" not in overlay,
        "the current Vita client must not map stream resolutions to legacy hotkeys",
    )
    _require(
        re.findall(
            r"bool\s+stream_overlay_take_apply_display_request\s*\(([^)]*)\)\s*;",
            overlay_header,
        ) == ["void"],
        "the Vita display-apply request must not carry a legacy virtual key",
    )
    _require(
        "stream_overlay_take_apply_display_request()" in connect and
        "send_host_rescue_hotkey(display_virtual_key)" not in connect and
        "sceKernelDelayThread(750 * 1000)" not in connect,
        "Apply resolution must use an immediate ordinary GameStream reconnect without a legacy hotkey delay",
    )
    reconnect = _extract_braced_block(
        connect,
        r"if\s*\(apply_display\s*\|\|\s*apply_input\)\s*",
        "Vita controlled reconnect",
    )
    reconnect_terminate = reconnect.find("connection_terminate()")
    _require(
        reconnect_terminate < reconnect.find("gs_refresh(&server);") <
        reconnect.find("ui_connect_stream(reconnect_app);") and
        reconnect_terminate <
        reconnect.find("release_stream_boundary(true)") <
        reconnect.find("gs_refresh(&server);") and
        reconnect_terminate >= 0,
        "the Vita controlled reconnect must terminate, restore, refresh, and resume in order",
    )


def _decode_csharp_string_expression(expression: str, location: str) -> str:
    fragments = re.findall(r'"((?:\\.|[^"\\])*)"', expression)
    _require(fragments, f"cannot find string fragments in {location}")
    try:
        return "".join(json.loads(f'"{fragment}"') for fragment in fragments)
    except json.JSONDecodeError as error:
        raise ContractFailure(f"cannot decode {location}: {error}") from error


def _parse_csharp_uint_constants(text: str) -> dict[str, int]:
    values: dict[str, int] = {}
    for name, raw_value in re.findall(
        r"private\s+const\s+uint\s+(\w+)\s*=\s*(0x[0-9A-Fa-f]+|\d+)\s*;",
        text,
    ):
        values[name] = int(raw_value, 0)
    return values


def _check_host(root: Path, values: dict[str, Any]) -> None:
    modes = _read(root, "host/VitaMoonlight.Host/VitaDisplayModes.cs")
    session = _read(root, "host/VitaMoonlight.Host/SessionManager.cs")
    sunshine = _read(root, "host/VitaMoonlight.Host/SunshineConfigurator.cs")
    hotkeys = _read(root, "host/VitaMoonlight.Host/HostRecoveryAgent.cs")
    bridge = _read(root, "host/VitaMoonlight.Host/SunshineStreamBoundaryBridge.cs")
    lease_journal = _read(root, "host/VitaMoonlight.Host/StreamBoundaryLeaseJournal.cs")
    firewall = _read(root, "host/VitaMoonlight.Host/ManagedStreamBridgeFirewall.cs")
    program = _read(root, "host/VitaMoonlight.Host/Program.cs")
    host_settings = _read(root, "host/VitaMoonlight.Host/HostSettings.cs")

    refresh_match = re.search(r"\bDesktopRefreshRate\s*=\s*(\d+)\s*;", modes)
    _require(refresh_match is not None, "host must define VitaDisplayModes.DesktopRefreshRate")
    _require(int(refresh_match.group(1)) == values["desktop_refresh"],
             "host desktop refresh differs from the contract")

    fps_match = re.search(
        r"SupportedStreamFrameRates[^=]*=\s*\[(?P<body>.*?)\]\s*;",
        modes,
        re.DOTALL,
    )
    _require(fps_match is not None, "host must define SupportedStreamFrameRates")
    host_frame_rates = [int(value) for value in re.findall(r"\b\d+\b", fps_match.group("body"))]
    _require(host_frame_rates == values["frame_rates"],
             f"host stream frame rates drifted: {host_frame_rates} != {values['frame_rates']}")

    native_match = re.search(
        r"\bNative\b[^=]*=\s*new\(\s*(\d+)\s*,\s*(\d+)\s*,\s*"
        r"(?:DesktopRefreshRate|\d+)\s*\)",
        modes,
    )
    _require(native_match is not None, "cannot find host native Vita display mode")
    host_resolutions = [(int(native_match.group(1)), int(native_match.group(2)))]
    supported_match = re.search(
        r"\bSupported\b[^=]*=\s*\[(?P<body>.*?)\]\s*;",
        modes,
        re.DOTALL,
    )
    _require(supported_match is not None, "cannot find host supported display modes")
    for width, height in re.findall(
        r"new\s+VitaDisplayMode\(\s*(\d+)\s*,\s*(\d+)\s*,\s*"
        r"(?:DesktopRefreshRate|\d+)\s*\)",
        supported_match.group("body"),
    ):
        host_resolutions.append((int(width), int(height)))
    _require(host_resolutions == values["resolutions"],
             f"host display resolutions drifted: {host_resolutions} != {values['resolutions']}")
    stream_mode_validator = _extract_braced_block(
        modes,
        r"internal\s+static\s+VitaStreamMode\s+RequireSupportedStreamMode\s*\(",
        "VitaDisplayModes.RequireSupportedStreamMode",
    )
    _require(
        "TryGetSupportedStreamMode" in stream_mode_validator and
        "return streamMode;" in stream_mode_validator,
        "strict host stream-mode validation must delegate to the shared recognizer",
    )
    stream_mode_recognizer = _extract_braced_block(
        modes,
        r"internal\s+static\s+bool\s+TryGetSupportedStreamMode\s*\(",
        "VitaDisplayModes.TryGetSupportedStreamMode",
    )
    _require(
        "SupportedStreamFrameRates.Contains(streamFps)" in stream_mode_recognizer and
        "new VitaStreamMode(desktopMode, streamFps)" in stream_mode_recognizer and
        "return false;" in stream_mode_recognizer and
        "return true;" in stream_mode_recognizer,
        "host stream-mode recognition must preserve allowed encoder FPS, normalize "
        "the desktop mode, and reject modes outside the Vita contract without mutation",
    )
    start_method = _extract_braced_block(
        session,
        r"internal\s+SessionStartResult\s+Start\s*\(",
        "SessionManager.Start",
    )
    _require("VitaDisplayModes.RequireSupportedStreamMode" in start_method,
             "SessionManager.Start must use the contracted stream-mode normalizer")
    start_core = _extract_braced_block(
        session,
        r"private\s+SessionStartResult\s+StartCoreLocked\s*\(",
        "SessionManager.StartCoreLocked",
    )
    _require(
        "var desktopMode = streamMode.DesktopMode;" in start_core and
        "streamMode.StreamFps" in start_core and
        start_core.count("desktopMode.Fps") >= 3,
        "SessionManager must apply the fixed desktop Hz separately from encoder FPS",
    )

    session_command = _extract_braced_block(
        program,
        r"private\s+static\s+int\s+SessionCommand\s*\(",
        "Program.SessionCommand",
    )
    recognize_index = session_command.find("if (!VitaDisplayModes.TryGetSupportedStreamMode")
    pass_through_message_index = session_command.find("is not a Vita mode")
    no_op_return_index = session_command.find(
        "return ExitSuccess;",
        pass_through_message_index,
    )
    administrator_index = session_command.find("EnsureAdministrator")
    _require(
        0 <= recognize_index < pass_through_message_index < no_op_return_index < administrator_index,
        "the legacy Sunshine hook must return success without privilege or display "
        "mutation when a client mode is outside the Vita contract",
    )
    _require(
        'case "hook-start":' in session_command and
        "hookStreamMode!.Value.DesktopMode" in session_command and
        "manager.Start(" in session_command,
        "recognized Vita hook modes must enter the strict normalized SessionManager path",
    )

    configure = _extract_braced_block(
        sunshine,
        r"internal\s+static\s+SunshineConfigurationResult\s+Configure\s*\(",
        "SunshineConfigurator.Configure",
    )
    compact_configure = re.sub(r"\s+", " ", configure)
    _require(
        'settings.HostMode == "sunshine" && settings.IntegrateAllSunshineApps' in
        compact_configure and
        "useAuthenticatedStreamBoundary ? Array.Empty<JsonObject>()" in compact_configure and
        "RemoveOwnedHooks(app, ownership.Hooks);" in configure and
        "RemoveLegacyGeneratedHooks(app);" in configure and
        "RemoveOwnedGlobalHooks(" in configure and
        "RemoveLegacyGeneratedGlobalHooks(configurationLines);" in configure and
        '"output_name", string.Empty' in compact_configure and
        '"dd_configuration_option", "disabled"' in compact_configure and
        '"dd_config_revert_on_disconnect", "disabled"' in compact_configure and
        "RetireOwnedLifecycleConfigurationValue(" in configure and
        '"min_log_level", "info"' in compact_configure and
        "if (!useAuthenticatedStreamBoundary)" in configure,
        "the default path must retire unsafe hooks and leave switching to the authenticated boundary",
    )
    readiness = _extract_braced_block(
        sunshine,
        r"internal\s+static\s+bool\s+IsAuthenticatedStreamBoundaryConfigurationReady\s*\(",
        "Sunshine authenticated-boundary readiness",
    )
    _require(
        "ownership.GlobalHooks.Count != 0" in readiness and
        "configuredOutput.Length != 0" in readiness and
        "HasGeneratedGlobalLifecycleHook(lines)" in readiness and
        'HasConfigurationValue(lines, "dd_configuration_option", "disabled")' in readiness and
        "min_log_level" not in readiness,
        "Sunshine readiness must reject a stale hook, pinned output, or native display switching",
    )

    boundary_contract = values["boundary"]
    required_bridge_fragments = [
        f'ProtocolIdentifier =\n        "{boundary_contract["protocol"]}"',
        f'PreparePath = "{boundary_contract["prepare"]["path"]}"',
        f'StartedPath = "{boundary_contract["started"]["path"]}"',
        f'HeartbeatPath = "{boundary_contract["heartbeat"]["path"]}"',
        f'StopPath = "{boundary_contract["stop"]["path"]}"',
        "BridgePortOffset = 23",
        "GenerationHexCharacters = 32",
        'ClientCertificateRequired = true',
        'EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13',
        'CertificateRevocationCheckMode = X509RevocationMode.NoCheck',
        'TryGetProperty("named_devices"',
        'values.GetValueOrDefault("file_state")',
        "settings.IntegrateAllSunshineApps",
        "SunshinePairedClientCertificates.IsEnabledClient(",
        "CryptographicOperations.FixedTimeEquals(",
        "RandomNumberGenerator.GetBytes(16)",
        "new SessionManager().Start(",
        "StreamBoundaryLeaseJournal.PublishPrepared(",
        "StreamBoundaryLeaseJournal.MarkStarted(",
        "StreamBoundaryLeaseJournal.RenewHeartbeat(",
        "new SessionManager().RestoreIfPending(",
        "StreamBoundaryLeaseJournal.Assess(",
        "new SessionManager().RecoverToIdle()",
        "listenerFaulted();",
    ]
    for fragment in required_bridge_fragments:
        _require(fragment in bridge,
                 f"host authenticated stream boundary is missing: {fragment}")
    _require(
        "TcpListener(IPAddress.IPv6Any, port)" in bridge and
        "candidate.Server.DualMode = true" in bridge and
        "PreAuthenticationTimeout =\n        TimeSpan.FromSeconds(2)" in bridge and
        "OperationTimeout =\n        TimeSpan.FromSeconds(60)" in bridge and
        "MaximumRequestHeaderBytes = 4096" in bridge and
        "unknown endpoint" in bridge and
        "durableCapturedAt is null" in bridge and
        "assessment.RequiresRecovery" in bridge and
        "assessment.AuthorizesActiveHandoff" in bridge and
        "status" not in re.findall(
            r'Path\s*=\s*"([^\"]+)"', bridge),
        "host bridge must be bounded, dual-stack, and expose no status endpoint",
    )
    inspect_recovery = _extract_braced_block(
        hotkeys,
        r"private\s+void\s+InspectSunshineRecoveryMarker\s*\(",
        "Sunshine exact-lease recovery observer",
    )
    _require(
        "TimeSpan.FromSeconds(240)" in lease_journal and
        "TimeSpan.FromSeconds(45)" in lease_journal and
        "TimeSpan.FromSeconds(20)" in lease_journal and
        "GetStreamBoundaryRecoveryDelay(" in hotkeys and
        "lease.LeaseExpiresAtUtc - now" in hotkeys and
        "DiscardStaleStreamBoundaryLease();" in hotkeys and
        "!settings.IntegrateAllSunshineApps" in hotkeys and
        "if (!legacySunshineLogObserverEnabled)" in inspect_recovery and
        inspect_recovery.find("if (!legacySunshineLogObserverEnabled)") <
        inspect_recovery.find("ArmSunshineLogWatcher()") and
        "CancelScheduledSunshineRecovery();" not in
        _extract_braced_block(
            hotkeys,
            r"private\s+void\s+ApplySunshineSessionLine\s*\(",
            "Sunshine global session observer",
        ),
        "observer must honor exact lease expiry and never let unrelated Sunshine sessions suppress it",
    )
    _require(
        'RuleName =\n        "Vita Moonlight authenticated stream boundary (managed)"' in firewall and
        'rule.RemoteAddresses = "*";' in firewall and
        "rule.EdgeTraversal = false;" in firewall and
        "rule.ApplicationName = normalizedExecutable;" in firewall and
        "RequireOwned(existing, normalizedExecutable);" in firewall and
        "rule.Profiles != AllProfiles" in firewall and
        'rule.InterfaceTypes,\n                    "All"' in firewall,
        "the mTLS bridge firewall rule must be exact, all-profile/interface, and ownership-checked",
    )
    _require(
        "catch (Exception error) when (IsMissingRuleError(error))" in firewall and
        "current.HResult == ErrorFileNotFound" in firewall and
        "VerifyMissingRuleInteropForSelfTest();" in program,
        "a missing first-install firewall rule must accept both COMException and FileNotFoundException HRESULT mappings and exercise the real Windows COM lookup in self-test",
    )
    manager_install = _extract_braced_block(
        hotkeys,
        r"internal\s+static\s+void\s+Install\s*\(",
        "HostRecoveryAgentManager.Install",
    )
    _require(
        manager_install.find("ManagedStreamBridgeFirewall.InstallOrRepair(") <
        manager_install.find('RunTask("/Run", "/TN", TaskName)') and
        "ManagedStreamBridgeFirewall.RemoveOwned(executablePath);" in manager_install,
        "agent install must repair the firewall before readiness and clean it outside Sunshine mode",
    )
    manager_uninstall = _extract_braced_block(
        hotkeys,
        r"internal\s+static\s+void\s+Uninstall\s*\(",
        "HostRecoveryAgentManager.Uninstall",
    )
    _require(
        "ManagedStreamBridgeFirewall.RemoveOwned(" in manager_uninstall,
        "agent uninstall must remove the exact owned firewall rule",
    )
    agent_run = _extract_braced_block(
        hotkeys,
        r"internal\s+static\s+int\s+Run\s*\(",
        "HostRecoveryAgentManager.Run",
    )
    _require(
        agent_run.find("new HostRecoveryAgentContext()") <
        agent_run.find("ready.Set();") and
        "context.StreamBoundaryListenerFaulted ? 1 : 0" in agent_run,
        "agent readiness must wait for TLS/state/certificate setup and listener bind",
    )
    default_settings_match = re.search(
        r"HostSettings\s+Default[^=]*=\s*new\((?P<body>.*?)\)\s*;",
        host_settings,
        re.DOTALL,
    )
    _require(default_settings_match is not None, "cannot find default host settings")
    compact_default_settings = re.sub(
        r"\s+",
        "",
        default_settings_match.group("body"),
    )
    _require(
        compact_default_settings.startswith('"sunshine",') and
        compact_default_settings.endswith("CurrentFormatVersion,true,true") and
        "ForceSdr = true" in host_settings,
        "fresh and upgraded host settings must default to authenticated Sunshine all-app SDR sessions",
    )
    build_start = _extract_braced_block(
        sunshine,
        r"internal\s+static\s+string\s+BuildStartCommand\s*\(",
        "SunshineConfigurator.BuildStartCommand",
    )
    _require(
        values["legacy_hook"]["command"] in build_start and
        "session start --width" not in build_start,
        "legacy generated Sunshine hooks must use the tolerant hook-start boundary",
    )

    uint_constants = _parse_csharp_uint_constants(hotkeys)
    for action in values["actions"]:
        constant_name = "Vk" + action["key"].title()
        _require(uint_constants.get(constant_name) == action["windows_virtual_key"],
                 f"host {constant_name} differs from the companion contract")
    for _, _, _, key, virtual_key in values["legacy"]:
        constant_name = "Vk" + key.title()
        _require(uint_constants.get(constant_name) == virtual_key,
                 f"host legacy {constant_name} differs from the contract")

    required_registrations = re.findall(
        r"RegisterRequiredHotkey\(\s*\w+\s*,\s*modifiers\s*,\s*(VkF\d+)\s*,\s*"
        r'"([^"]+)"\s*\)',
        hotkeys,
    )
    expected_registrations = [
        ("Vk" + action["key"].title(), action["host_action"])
        for action in reversed(values["actions"])
    ]
    _require(sorted(required_registrations) == sorted(expected_registrations),
             f"host required hotkeys must be only F11 recovery: {required_registrations}")

    optional_keys = re.findall(
        r"RegisterOptionalModeHotkey\(\s*\w+\s*,\s*modifiers\s*,\s*(VkF\d+)",
        hotkeys,
    )
    _require(optional_keys == ["VkF8", "VkF9", "VkF10"],
              f"legacy host mode hotkeys drifted: {optional_keys}")
    compact_hotkeys = re.sub(r"\s+", " ", hotkeys)
    _require(
        "internal static bool LegacyModeHotkeysRequired(HostSettings settings)" in
        compact_hotkeys and
        "!string.Equals(settings.HostMode, \"sunshine\", StringComparison.OrdinalIgnoreCase) || "
        "!settings.IntegrateAllSunshineApps;" in compact_hotkeys,
        "host legacy-mode hotkey policy must be the inverse of native Sunshine all-app integration",
    )
    readiness = _extract_braced_block(
        hotkeys,
        r"internal\s+static\s+IReadOnlyList<HostModeHotkeyStatus>\s+"
        r"GetModeHotkeyReadiness\s*\(",
        "HostRecoveryAgentManager.GetModeHotkeyReadiness",
    )
    _require(
        "!LegacyModeHotkeysRequired(HostSettings.Load())" in readiness and
        "return Array.Empty<HostModeHotkeyStatus>();" in readiness,
        "native/default host readiness must not require legacy F8-F10 shortcuts",
    )
    constructor = _extract_braced_block(
        hotkeys,
        r"internal\s+HostRecoveryHotkeyWindow\s*\(",
        "HostRecoveryHotkeyWindow constructor",
    )
    registration_gate = (
        "if (HostRecoveryAgentManager.LegacyModeHotkeysRequired(settings))"
    )
    _require(
        registration_gate in constructor and
        constructor.find(registration_gate) < constructor.find("RegisterOptionalModeHotkey("),
        "the host must register F8-F10 only inside the explicit legacy fallback gate",
    )
    _require(
        "public bool ModeHotkeysReady =>" in program and
        "HostRecoveryAgentManager.LegacyModeHotkeysRequired(" in program and
        ": ModeHotkeys.Count == 0;" in program,
        "host diagnostics must accept an empty legacy-hotkey list on the native/default path",
    )
    _require(
        "var modifiers = ModAlt | ModControl | ModShift | ModNoRepeat;" in hotkeys,
        "host companion chord must remain Control+Alt+Shift with no-repeat",
    )


def check(root: Path) -> dict[str, Any]:
    contract_path = root / "protocol/vita-host-contract.json"
    contract = _load_contract(contract_path)
    values = _validate_contract(contract)
    _check_vita(root, values)
    _check_host(root, values)
    return values


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--root",
        type=Path,
        default=Path(__file__).resolve().parents[1],
        help="repository root (defaults to the script's parent repository)",
    )
    args = parser.parse_args()
    root = args.root.resolve()
    try:
        values = check(root)
    except ContractFailure as error:
        print(f"host/client contract check failed: {error}", file=sys.stderr)
        return 1

    print(
        "host/client contract check passed: "
        f"{len(values['resolutions'])} resolutions, "
        f"{len(values['frame_rates'])} stream frame rates, "
        f"{values['desktop_refresh']} Hz desktop, "
        f"{len(values['actions'])} companion actions"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
