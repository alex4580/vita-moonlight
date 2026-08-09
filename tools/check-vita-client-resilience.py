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
    ime = read("src/gui/ime.c")
    ime_header = read("src/gui/ime.h")
    ime_text = read("src/gui/ime_text.h")
    check_dir = read("src/check_dir.c")
    check_dir_header = read("src/check_dir.h")
    main_source = read("src/main.c")
    ui = read("src/gui/ui.c")
    ui_connect = read("src/gui/ui_connect.c")
    wake_on_lan = read("src/wake_on_lan.c")
    settings = read("src/gui/ui_settings.c")
    overlay = read("src/gui/ui_stream_overlay.c")
    diagnostics = read("src/gui/ui_diagnostics.c")
    keyboard_system = read("src/keyboardsystem.c")
    keyboard_ime = read("src/input/keyboard_ime.h")
    shortcuts = read("src/input/shortcuts.c")
    vita_input = read("src/input/vita.c")
    touch_zone_gesture = read("src/input/touch_zone_gesture.c")
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
        "inventory_storage_root(" in check_dir
        and "choose_existing_root(" in check_dir
        and check_dir.find("inventory_storage_root(&inventories")
        < check_dir.find("ensure_directory(directory)")
        and 'STORAGE_ROOT_MARKER ".vita-moonlight-root"' in check_dir
        and '(.path = "ux0:data")' not in check_dir
        and '{.path = "ux0:data"}' in check_dir
        and "Pairing identities were not merged or overwritten" in check_dir,
        "src/check_dir.c: upgrades must inventory all modern and legacy roots before creation and fail closed on conflicting pairing stores",
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
        "size_t text_size" in ime_header
        and "ime_utf16_to_utf8(" in ime
        and "text, text_size" in ime
        and "strcpy(text" not in ime
        and "strcpy(userText" not in ime
        and "destination_bytes" in ime_text
        and "destination_units" in ime_text,
        "src/gui/ime.c: Vita keyboard conversion and every caller buffer must be size-bounded",
    )

    require(
        'const char *broadcast = "255.255.255.255";' in ui
        and "strcpy(last_dot+1" not in ui
        and "void ui_connect_address(char *addr, size_t addr_size)" in ui_connect
        and "snprintf(addr, addr_size" in ui_connect
        and "parse_mac_address(" in wake_on_lan
        and "strlen(text) != 17" in wake_on_lan
        and "inet_pton(AF_INET" in wake_on_lan
        and "sent == (int)sizeof(packet)" in wake_on_lan,
        "Vita UI: Wake-on-LAN and displayed connection addresses must not copy untrusted host text out of bounds",
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
        and "sceKernelWaitEventFlag(" in vita_input
        and "input_worker_event" in vita_input
        and "2 ms only while streaming" in vita_input
        and "wake_input_worker();" in function_body(
            vita_input, "void vitainput_start(void)", "void vitainput_stop(void)"
        )
        and "wake_input_worker();" in function_body(
            vita_input, "bool vitainput_shutdown(void)", "void vitainput_config("
        )
        and vita_input.find("unlock_psbutton();", vita_input.find(
            "bool vitainput_shutdown(void)"))
        < vita_input.find("sceKernelWaitThreadEnd", vita_input.find(
            "bool vitainput_shutdown(void)")),
        "src/input/vita.c: the input worker must block while idle, wake for a stream/shutdown, and retain an owned joinable lifecycle",
    )
    require(
        "bool vitapower_shutdown(void)" in power
        and "sceKernelWaitEventFlag" in power
        and "power_worker_running" in power
        and "static bool start_power_worker_locked(void)" in power
        and "static bool stop_power_worker_locked(void)" in power
        and "sceKernelCreateThread(" not in function_body(
            power, "bool vitapower_init()", "bool vitapower_shutdown(void)"
        )
        and "scePowerIsLowBattery" not in power
        and "POWER_WORKER_STACK_SIZE 0x10000U" in power
        and "config->disable_powersave = true;" in config
        and "config.disable_powersave = true;" not in function_body(
            config, "void config_apply_stream_preset(",
            "const char *config_stream_preset_name("
        ),
        "src/power/vita.c: keep-awake must be independently configurable, lazy, stream-scoped, wakeable, and joinable",
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
        "#define LOCAL_SHORTCUT_LEADER SCE_CTRL_SELECT" in shortcuts
        and "#define OVERLAY_CHORD_WINDOW_US 1000000" in shortcuts
        and "#define KEYBOARD_CHORD_WINDOW_US 1000000" in shortcuts
        and "SCE_CTRL_START | SCE_CTRL_L1" not in shortcuts
        and "SCE_CTRL_START | SCE_CTRL_LEFT" not in shortcuts
        and "keyboard_shortcut_consumed = false;" in shortcuts
        and "pad->buttons &= ~KEYBOARD_CHORD_MASK" in shortcuts
        and "reset_overlay_chord();\n                keyboardsystem_open_keyboard();"
        in shortcuts,
        "src/input/shortcuts.c: SELECT-led local shortcuts must preserve START and keep a release barrier",
    )
    require(
        "vita_touch_zone_gesture_step(" in vita_input
        and "process_touchzones();" in vita_input
        and "VITA_TOUCH_ZONE_TAP_PENDING" in touch_zone_gesture
        and "VITA_TOUCH_ZONE_PASSTHROUGH" in touch_zone_gesture
        and "SETTINGS_ENABLE_SPECIAL_KEYS" not in settings
        and "Front-touch tap-zone mapper" in settings,
        "Vita front-touch actions must use tap arbitration and one graphical configuration authority",
    )
    require(
        "e->param.text.caretIndex" in keyboard_system
        and "e->param.text.editLengthChange" in keyboard_system
        and "vita_keyboard_ime_interpret(" in keyboard_system
        and "output_text[IME_MAX_TEXT_UNITS + 1]" in keyboard_system
        and "param.maxTextLength     = IME_MAX_TEXT_UNITS" in keyboard_system
        and "initial_text_update" in keyboard_ime
        and "VITA_KEYBOARD_IME_ACTION_BACKSPACE" in keyboard_ime,
        "src/keyboardsystem.c: UPDATE_TEXT must use its text-union payload and the native-tested event interpreter",
    )
    require(
        "if (selected_item == MAIN_KEYBOARD)" in overlay
        and "if (stream_overlay_close()) {\n      keyboardsystem_open_keyboard();"
        in overlay,
        "src/gui/ui_stream_overlay.c: opening the IME must dismiss the stream menu first",
    )
    require(
        "suppress_remote_input_until_release = true;" in vita_input
        and "LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT)"
        in vita_input
        and "mapped_actions" in vita_input,
        "src/input/vita.c: reconnect/teardown must release and suppress stale input",
    )
    require(
        "pad_snapshot" not in vita_input
        and "curr_snapshot" not in vita_input
        and "Never snapshot and replay controller state here" in vita_input,
        "src/input/vita.c: the synchronous IME must never replay a stale controller snapshot",
    )
    require(
        "termination_in_progress" in connection
        and "lifecycle_mutex" in connection
        and "__atomic_exchange_n" in connection
        and connection.find("pthread_mutex_unlock(&lifecycle_mutex);")
        < connection.find("LiStopConnection();"),
        "src/connection.c: stream start/stop must be serialized without reentrant LiStop deadlock",
    )
    terminate_internal = connection.split(
        "static void connection_connection_terminated_internal", 1
    )[1].split("static void connection_connection_terminated", 1)[0]
    ordered_release = ui_connect.split(
        "static bool release_host_client_state_with_options", 1
    )[1].split("static bool release_host_client_state(void)", 1)[0]
    disconnect_path = ui_connect.split("\ndisconnect:", 1)[1].split(
        "\nint ui_connect(", 1
    )[0]
    require(
        terminate_internal.find("LiStopConnection();")
        < terminate_internal.find("set_connection_state(LI_DISCONNECTED")
        < terminate_internal.rfind("end_termination();")
        and "connection_wait_for_termination()" in connection
        and "CONNECTION_TERMINATION_WAIT_US" in connection
        and "connection_is_terminating()" in connection,
        "src/connection.c: disconnected must be published only after bounded media teardown completes",
    )
    require(
        ordered_release.find("connection_wait_for_termination();")
        < ordered_release.find("release_stream_boundary(show_restore_error)")
        < ordered_release.find("gs_cleanup(&server);")
        and "status == LI_DISCONNECTED || connection_is_terminating()" in ui_connect,
        "src/gui/ui_connect.c: host Quit/network termination must join the async owner before heartbeat/CURL cleanup",
    )
    require(
        disconnect_path.find("release_host_client_state_with_options(true)")
        < disconnect_path.find('flash_message("Disconnected")')
        and main_source.find("ui_connect_shutdown();")
        < main_source.find("gui_shutdown();")
        and "void gui_shutdown()" in ui
        and ui.find("void gui_loop()") < ui.find("void gui_shutdown()")
        and "vita2d_fini();" not in ui.split(
            "void gui_loop()", 1
        )[1].split("void gui_shutdown()", 1)[0],
        "Vita2D must not draw or finalize until asynchronous decoder teardown has joined",
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
