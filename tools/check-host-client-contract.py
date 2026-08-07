#!/usr/bin/env python3
"""Fail the build when the Vita and Windows host stream contracts drift."""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path
from typing import Any, Iterable


SCHEMA = "vita-moonlight/host-client-contract/v2"


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
    _require(contract["contract_version"] == 2, "contract_version must be 2 for schema v2")

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
            "native_sunshine_display_management",
            "legacy_single_application_hook",
            "legacy_unacknowledged_hotkeys",
        },
        "mode_control",
    )
    _require(
        mode_control["authoritative_transport"] == "gamestream-launch-mode",
        "GameStream launch mode must remain authoritative",
    )
    _require(
        mode_control["native_sunshine_display_management"] == {
            "scope": "all-applications",
            "configuration_option": "ensure_only_display",
            "resolution_option": "auto",
            "refresh_rate_option": "manual",
            "desktop_refresh_hz": 60,
            "exact_contract_resolution_remapping": True,
            "force_sdr_for_vita": True,
            "revert_delay_ms": 500,
            "revert_on_disconnect": True,
        },
        "native Sunshine display management must select only the Vita output, "
        "honor exact client modes at 60 Hz SDR, and restore on disconnect",
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
        "native_display": mode_control["native_sunshine_display_management"],
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
    _require(
        reconnect.find("connection_terminate();") < reconnect.find("gs_refresh(&server);") <
        reconnect.find("ui_connect_stream(reconnect_app);") and
        reconnect.find("connection_terminate();") >= 0,
        "the Vita controlled reconnect must terminate, refresh, and resume the same app in order",
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

    remap_match = re.search(
        r"private\s+const\s+string\s+VitaDisplayModeRemapping\s*=\s*(?P<value>.*?);",
        sunshine,
        re.DOTALL,
    )
    _require(remap_match is not None, "cannot find Sunshine Vita display-mode remapping")
    remap_text = _decode_csharp_string_expression(
        remap_match.group("value"),
        "Sunshine VitaDisplayModeRemapping",
    )
    try:
        remap = json.loads(remap_text)
    except json.JSONDecodeError as error:
        raise ContractFailure(f"Sunshine VitaDisplayModeRemapping is not valid JSON: {error}") from error
    expected_remaps = [
        {
            "requested_resolution": f"{width}x{height}",
            "final_resolution": f"{width}x{height}",
        }
        for width, height in values["resolutions"]
    ]
    _require(remap.get("mixed") == [] and remap.get("refresh_rate_only") == [],
             "Sunshine remapping must not add mixed or refresh-only transforms")
    actual_remaps = remap.get("resolution_only")
    _require(isinstance(actual_remaps, list) and len(actual_remaps) == len(expected_remaps),
             "Sunshine must have exactly one explicit remap per contracted resolution")
    _require(
        {
            (entry.get("requested_resolution"), entry.get("final_resolution"))
            for entry in actual_remaps
            if isinstance(entry, dict)
        } == {
            (entry["requested_resolution"], entry["final_resolution"])
            for entry in expected_remaps
        },
        f"Sunshine resolution remapping drifted: {actual_remaps} != {expected_remaps}",
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
        "useNativeDisplayManagement ? Array.Empty<JsonObject>()" in compact_configure and
        "RemoveOwnedHooks(app, ownership.Hooks);" in configure and
        "RemoveLegacyGeneratedHooks(app);" in configure and
        "ConfigureNativeDisplayManagement(" in configure,
        "the default all-application path must remove legacy hooks and use native Sunshine display management",
    )
    native_configuration = _extract_braced_block(
        sunshine,
        r"private\s+static\s+void\s+ConfigureNativeDisplayManagement\s*\(",
        "SunshineConfigurator.ConfigureNativeDisplayManagement",
    )
    native_contract = values["native_display"]
    required_native_fragments = [
        '"output_name", displayDeviceId',
        '"dd_configuration_option", "ensure_only_display"',
        '"dd_resolution_option", "auto"',
        '"dd_refresh_rate_option", "manual"',
        f'"dd_manual_refresh_rate", "{native_contract["desktop_refresh_hz"]}"',
        '"dd_mode_remapping", VitaDisplayModeRemapping',
        '"dd_hdr_option", forceSdr ? "auto" : "disabled"',
        f'"dd_config_revert_delay", "{native_contract["revert_delay_ms"]}"',
        '"dd_config_revert_on_disconnect", "enabled"',
    ]
    for fragment in required_native_fragments:
        _require(
            fragment in native_configuration,
            f"native Sunshine display management is missing contracted fragment: {fragment}",
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
        "fresh and upgraded host settings must default to native Sunshine all-app SDR sessions",
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
        "if (HostRecoveryAgentManager.LegacyModeHotkeysRequired(HostSettings.Load()))"
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
