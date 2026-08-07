#!/usr/bin/env python3
"""Guard Vita runtime, persistence, input-release, and latency invariants.

The Vita cross-build proves symbols and ABI compatibility. These source-level
checks protect lifecycle behavior that is otherwise easy to regress while
refactoring callbacks and menus.
"""

from __future__ import annotations

import re
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
errors: list[str] = []


def read(relative_path: str) -> str:
    try:
        return (ROOT / relative_path).read_text(encoding="utf-8-sig")
    except OSError as error:
        errors.append(f"{relative_path}: could not read file: {error}")
        return ""


def require(condition: bool, message: str) -> None:
    if not condition:
        errors.append(message)


def function_body(source: str, signature: str, next_signature: str) -> str:
    if signature not in source:
        return ""
    body = source.split(signature, 1)[1]
    if next_signature in body:
        body = body.split(next_signature, 1)[0]
    return body


def main() -> int:
    config = read("src/config.c")
    config_header = read("src/config.h")
    check_dir = read("src/check_dir.c")
    check_dir_header = read("src/check_dir.h")
    main_source = read("src/main.c")
    ui = read("src/gui/ui.c")
    ui_connect = read("src/gui/ui_connect.c")
    settings = read("src/gui/ui_settings.c")
    overlay = read("src/gui/ui_stream_overlay.c")
    diagnostics = read("src/gui/ui_diagnostics.c")
    shortcuts = read("src/input/shortcuts.c")
    vita_input = read("src/input/vita.c")
    power = read("src/power/vita.c")
    motion = read("src/input/motion.c")
    connection = read("src/connection.c")
    video = read("src/video/vita.c")

    save_body = function_body(
        config, "bool config_save(", "void update_layout()"
    )
    require(
        "bool config_save(" in config_header
        and 'config_sibling_name(filename, ".tmp")' in save_body
        and 'config_sibling_name(filename, ".bak")' in save_body
        and "CONFIG_COMPLETION_MARKER" in save_body
        and "config_rename_file(temporary_name, filename)" in save_body
        and "exit(" not in save_body,
        "src/config.c: settings must use a non-fatal recoverable journal",
    )
    require(
        'write_config_string(fd, "key_dir"' not in save_body
        and "runtime-selected storage state" in save_body,
        "src/config.c: runtime storage roots must not be persisted",
    )
    require(
        "config->keyboard_layout >= KB_LAYOUT_COUNT" in config,
        "src/config.c: loaded keyboard layouts must be range checked",
    )
    require(
        "inih handlers return non-zero" in config
        and re.search(r"static int ini_handle\(.*?return 1;\n}", config, re.DOTALL)
        is not None,
        "src/config.c: the inih callback must accept valid lines",
    )
    require(
        "CONFIGURATION parsed;" in config
        and "config_parse_candidate(path, config, &parsed)" in config
        and "const char* candidates[3] = {filename, backup_name, temporary_name}"
        in config
        and "config_promote_recovery(selected, filename)" in config,
        "src/config.c: invalid live settings must fall back without partial-value contamination",
    )
    require(
        "candidate->config_version > 0" in config
        and "candidate->config_version < CURRENT_CONFIG_VERSION" in config
        and "config_temporary_file_complete(path)" in config,
        "src/config.c: empty or truncated current-format settings must not beat a complete recovery file",
    )
    require(
        "bool config_parse(" in config_header
        and "Saved configuration is invalid and has no valid recovery file"
        in config
        and "exit(-1)" not in config,
        "src/config.c: invalid settings must fail safely instead of overwriting recovery state",
    )
    require(
        "bool check_and_create_moonlight_dir" in check_dir_header
        and ".vita-moonlight-write-test" in check_dir
        and "verify_writable_directory" in check_dir
        and '"ux0:data/"' not in check_dir
        and '"key_dir = "' not in check_dir
        and "if (!check_and_create_moonlight_dir" in main_source,
        "src/check_dir.c: startup storage must be app-owned, writable, and fail-fast",
    )
    require(
        "display_error(" in function_body(
            settings, "void ui_settings_save_config()", "\n}"
        )
        and "!config_save(config_path, &config)" in settings,
        "src/gui/ui_settings.c: settings-save failure must be user-visible",
    )
    require(
        "static bool flush_settings(void)" in overlay
        and "bool stream_overlay_close(void)" in overlay
        and "settings_save_failed" in overlay
        and "if (stream_overlay_close()) disconnect_requested = true;"
        in overlay
        and "Do not write flash storage on every D-pad edge" in overlay,
        "src/gui/ui_stream_overlay.c: in-stream edits must be batched and report failures",
    )
    require(
        "MAIN_TASK_MANAGER" in overlay
        and "MAIN_CLOSE_GAME" not in overlay
        and "stream_overlay_take_task_manager_request" in ui_connect
        and "send_windows_task_manager_shortcut" in ui_connect
        and "SEND_TASK_MANAGER_KEY(0x11, KEY_ACTION_DOWN)" in ui_connect
        and "SEND_TASK_MANAGER_KEY(0x10, KEY_ACTION_DOWN)" in ui_connect
        and "SEND_TASK_MANAGER_KEY(0x1B, KEY_ACTION_DOWN)" in ui_connect
        and "send_host_rescue_hotkey(0x7B)" not in ui_connect,
        "in-stream recovery must open Task Manager through Moonlight, never kill an arbitrary foreground app",
    )
    require(
        "config_save(config_path, &config)" not in diagnostics,
        "src/gui/ui_diagnostics.c: per-run support logging must not write preferences",
    )

    require(
        "loop_forever" not in main_source
        and "static bool vita_init()" in main_source
        and "goto fail;" in main_source
        and "vita_runtime_shutdown();" in main_source
        and "if (!config_parse(argc, argv, &config))" in main_source
        and "ret != CURLE_OK" in main_source,
        "src/main.c: failed subsystem initialization must unwind and exit",
    )
    require(
        "exit(0)" not in ui and "exit_menu = 1;" in ui,
        "src/gui/ui.c: Quit must return through the application shutdown path",
    )
    main_menu_body = function_body(ui, "int ui_main_menu()", "int global_loop")
    menu_return = main_menu_body.find("int result = display_menu(")
    unconditional_scan_stop = main_menu_body.find(
        "stop_host_scan();", menu_return
    )
    require(
        menu_return >= 0
        and menu_return < unconditional_scan_stop
        < main_menu_body.find("free(menu);", menu_return)
        and "Back/O exits display_menu" in main_menu_body,
        "src/gui/ui.c: every main-menu exit, including Back/O, must join the host scanner before network shutdown",
    )
    require(
        "bool vitainput_shutdown(void)" in vita_input
        and "sceKernelWaitThreadEnd" in vita_input
        and "input_worker_is_running()" in vita_input
        and vita_input.find("unlock_psbutton();", vita_input.find(
            "bool vitainput_shutdown(void)"))
        < vita_input.find("sceKernelWaitThreadEnd", vita_input.find(
            "bool vitainput_shutdown(void)")),
        "src/input/vita.c: the input worker must have an owned joinable lifecycle",
    )
    require(
        "bool vitapower_shutdown(void)" in power
        and "sceKernelWaitEventFlag" in power
        and "power_worker_running" in power,
        "src/power/vita.c: the power worker must be wakeable and joinable",
    )
    require(
        "bool vita_motion_shutdown(void)" in motion
        and "stream_motion_scalar_x" in motion
        and "stream_motion_scalar_y" in motion
        and "config.motion_controls_scalar" not in function_body(
            motion, "static void motion_process_sample(",
            "int vitainput_motion_thread("
        ),
        "src/input/motion.c: gyro worker settings must be snapshotted and resources released",
    )

    require(
        "#define OVERLAY_CHORD_WINDOW_US 1000000" in shortcuts
        and "#define KEYBOARD_CHORD_WINDOW_US 1000000" in shortcuts
        and "keyboard_shortcut_consumed = false;" in shortcuts
        and "pad->buttons &= ~KEYBOARD_CHORD_MASK" in shortcuts
        and "reset_overlay_chord();\n                keyboardsystem_open_keyboard();"
        in shortcuts,
        "src/input/shortcuts.c: START-led shortcuts need a humane window and release barrier",
    )
    require(
        "suppress_remote_input_until_release = true;" in vita_input
        and "LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT)"
        in vita_input
        and "mapped_actions" in vita_input,
        "src/input/vita.c: reconnect/teardown must release and suppress stale input",
    )
    require(
        "termination_in_progress" in connection
        and "lifecycle_mutex" in connection
        and "__atomic_exchange_n" in connection
        and connection.find("pthread_mutex_unlock(&lifecycle_mutex);")
        < connection.find("LiStopConnection();"),
        "src/connection.c: stream start/stop must be serialized without reentrant LiStop deadlock",
    )
    require(
        "need_drop" not in video
        and "atomic_sub_u32" not in video
        and "Present each completed hardware-decoded frame immediately" in video
        and "curr_frame_count * PACER_SAMPLE_INTERVAL_US" in video
        and "elapsed_us / 2) / elapsed_us" in video
        and "config.enable_frame_pacer = false;" in config
        and "config->enable_frame_pacer = false;" in config
        and "config->sops = true;" in config
        and "STREAM_FRAME_PACER" not in overlay
        and "SETTINGS_ENABLE_FRAME_PACER" not in settings
        and "STREAM_HOST_OPTIMIZE" not in overlay
        and "SETTINGS_SOPS" not in settings,
        "Vita video must present completed frames immediately, avoid coarse frame dropping, and keep managed-host optimization enabled",
    )
    if errors:
        print("Vita client resilience contract FAILED:", file=sys.stderr)
        for error in errors:
            print(f"- {error}", file=sys.stderr)
        return 1

    print("Vita client resilience contract passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
