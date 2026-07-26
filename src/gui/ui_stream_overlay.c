#include "ui_stream_overlay.h"

#include "guilib.h"
#include "ui_diagnostics.h"
#include "../config.h"
#include "../debug.h"
#include "../input/touchabsolute.h"
#include "../keyboardsystem.h"
#include "../video/vita.h"

#include <Limelight.h>
#include <stdio.h>
#include <stdlib.h>
#include <vita2d.h>

typedef enum OverlayPage {
  OVERLAY_PAGE_MAIN = 0,
  OVERLAY_PAGE_STREAM,
  OVERLAY_PAGE_INPUT
} OverlayPage;

enum {
  MAIN_RESUME = 0,
  MAIN_STREAM,
  MAIN_INPUT,
  MAIN_PERFORMANCE,
  MAIN_DIAGNOSTICS,
  MAIN_LOGGING,
  MAIN_KEYBOARD,
  MAIN_CLOSE_GAME,
  MAIN_QUIT_APP,
  MAIN_RECOVER_HOST,
  MAIN_DISCONNECT,
  MAIN_ITEM_COUNT
};

enum {
  STREAM_BACK = 0,
  STREAM_PRESET,
  STREAM_RESOLUTION,
  STREAM_FPS,
  STREAM_BITRATE,
  STREAM_NETWORK,
  STREAM_FRAME_PACER,
  STREAM_PACKET_RECOVERY,
  STREAM_SCALING,
  STREAM_VBLANK,
  STREAM_HOST_OPTIMIZE,
  STREAM_APPLY_RECONNECT,
  STREAM_ITEM_COUNT
};

enum {
  INPUT_BACK = 0,
  INPUT_PROFILE,
  INPUT_PSBUTTON,
  INPUT_TOUCH,
  INPUT_MOTION,
  INPUT_SWAP_SHOULDERS,
  INPUT_DOUBLE_TAP_SPRINT,
  INPUT_APPLY_RECONNECT,
  INPUT_ITEM_COUNT
};

static volatile bool overlay_open = false;
static volatile bool disconnect_requested = false;
static volatile bool close_game_requested = false;
static volatile bool quit_app_requested = false;
static volatile bool recover_host_requested = false;
static volatile bool apply_display_requested = false;
static volatile bool apply_input_requested = false;
static int apply_display_virtual_key = 0;

static OverlayPage page = OVERLAY_PAGE_MAIN;
static int selected_item = MAIN_RESUME;
static int confirmation_item = -1;
static bool settings_changed = false;

static const int resolutions[][2] = {
  {960, 544},
  {960, 540},
  {1280, 720},
};
static const int bitrates[] = {4000, 5000, 8000, 12000, 15000, 20000};
static const int frame_rates[] = {24, 30, 40, 50, 60};
static const char *psbutton_names[] = {
  "Local double-tap",
  "Safe PC Guide",
  "Immediate PC Guide",
  "System / LiveArea"
};
static const char *network_names[] = {
  "Local only",
  "Remote / VPN",
  "Auto detect"
};

static bool pressed(const SceCtrlData *pad,
                    const SceCtrlData *previous,
                    unsigned int button) {
  return pad && previous &&
         (pad->buttons & button) &&
         !(previous->buttons & button);
}

static int next_index(int current, int count, int direction) {
  current += direction < 0 ? -1 : 1;
  if (current < 0) current = count - 1;
  if (current >= count) current = 0;
  return current;
}

static int find_resolution(void) {
  for (unsigned int i = 0;
       i < sizeof(resolutions) / sizeof(resolutions[0]);
       i++) {
    if (config.stream.width == resolutions[i][0] &&
        config.stream.height == resolutions[i][1]) {
      return (int)i;
    }
  }
  return 0;
}

static int find_value(const int values[], int count, int value) {
  int closest = 0;
  int closest_distance =
      value > values[0] ? value - values[0] : values[0] - value;
  for (int i = 1; i < count; i++) {
    int distance =
        value > values[i] ? value - values[i] : values[i] - value;
    if (distance < closest_distance) {
      closest = i;
      closest_distance = distance;
    }
  }
  return closest;
}

static int resolution_virtual_key(void) {
  if (config.stream.width == 960 && config.stream.height == 540) {
    return 0x77; // F8
  }
  if (config.stream.width == 1280 && config.stream.height == 720) {
    return 0x79; // F10
  }
  return 0x78; // F9: native 960x544 and safe fallback
}

static int current_item_count(void) {
  switch (page) {
    case OVERLAY_PAGE_STREAM: return STREAM_ITEM_COUNT;
    case OVERLAY_PAGE_INPUT: return INPUT_ITEM_COUNT;
    default: return MAIN_ITEM_COUNT;
  }
}

static void save_settings(void) {
  if (config_path) config_save(config_path, &config);
  settings_changed = true;
}

static void enter_page(OverlayPage next_page) {
  page = next_page;
  selected_item = 0;
  confirmation_item = -1;
}

static void return_to_main(void) {
  page = OVERLAY_PAGE_MAIN;
  selected_item = MAIN_RESUME;
  confirmation_item = -1;
}

static void adjust_stream_item(int direction) {
  switch (selected_item) {
    case STREAM_PRESET: {
      int preset = config_detect_stream_preset();
      if (preset == STREAM_PRESET_CUSTOM) {
        preset = STREAM_PRESET_RECOMMENDED;
      } else {
        preset = (preset + (direction < 0 ? 3 : 1)) % 4;
      }
      config_apply_stream_preset(preset);
      save_settings();
      break;
    }
    case STREAM_RESOLUTION: {
      int old_recommended = config_recommended_bitrate(
          config.stream.width, config.stream.height, config.stream.fps);
      int index = next_index(
          find_resolution(),
          (int)(sizeof(resolutions) / sizeof(resolutions[0])),
          direction);
      config.stream.width = resolutions[index][0];
      config.stream.height = resolutions[index][1];
      if (config.stream.bitrate == old_recommended) {
        config.stream.bitrate = config_recommended_bitrate(
            config.stream.width, config.stream.height, config.stream.fps);
      }
      save_settings();
      break;
    }
    case STREAM_FPS: {
      int count = (int)(sizeof(frame_rates) / sizeof(frame_rates[0]));
      int old_recommended = config_recommended_bitrate(
          config.stream.width, config.stream.height, config.stream.fps);
      int index = next_index(
          find_value(frame_rates, count, config.stream.fps),
          count,
          direction);
      config.stream.fps = frame_rates[index];
      if (config.stream.bitrate == old_recommended) {
        config.stream.bitrate = config_recommended_bitrate(
            config.stream.width, config.stream.height, config.stream.fps);
      }
      save_settings();
      break;
    }
    case STREAM_BITRATE: {
      int count = (int)(sizeof(bitrates) / sizeof(bitrates[0]));
      int index = next_index(
          find_value(bitrates, count, config.stream.bitrate),
          count,
          direction);
      config.stream.bitrate = bitrates[index];
      save_settings();
      break;
    }
    case STREAM_NETWORK:
      config.stream.streamingRemotely =
          (config.stream.streamingRemotely + (direction < 0 ? 2 : 1)) % 3;
      save_settings();
      break;
    case STREAM_FRAME_PACER:
      config.enable_frame_pacer = !config.enable_frame_pacer;
      save_settings();
      break;
    case STREAM_PACKET_RECOVERY:
      config.enable_ref_frame_invalidation =
          !config.enable_ref_frame_invalidation;
      save_settings();
      break;
    case STREAM_SCALING:
      config.center_region_only = !config.center_region_only;
      save_settings();
      break;
    case STREAM_VBLANK:
      config.enable_vita_vblank_wait = !config.enable_vita_vblank_wait;
      save_settings();
      break;
    case STREAM_HOST_OPTIMIZE:
      config.sops = !config.sops;
      save_settings();
      break;
    default:
      break;
  }
}

static void adjust_input_item(int direction) {
  switch (selected_item) {
    case INPUT_PROFILE: {
      int profile = config_detect_controller_profile();
      if (profile == CONTROLLER_PROFILE_CUSTOM) {
        profile = direction < 0 ? CONTROLLER_PROFILE_STEAM
                                : CONTROLLER_PROFILE_COMPATIBILITY;
      } else {
        profile = profile == CONTROLLER_PROFILE_COMPATIBILITY
            ? CONTROLLER_PROFILE_STEAM
            : CONTROLLER_PROFILE_COMPATIBILITY;
      }
      config_apply_controller_profile(profile);
      touchabsolute_enable(config.touchscreen_mode == 2);
      save_settings();
      break;
    }
    case INPUT_PSBUTTON:
      config.psbutton_mode =
          (config.psbutton_mode +
           (direction < 0 ? PSBUTTON_MODE_COUNT - 1 : 1)) %
          PSBUTTON_MODE_COUNT;
      save_settings();
      break;
    case INPUT_TOUCH:
      config.touchscreen_mode =
          (config.touchscreen_mode + (direction < 0 ? 3 : 1)) % 4;
      touchabsolute_enable(config.touchscreen_mode == 2);
      save_settings();
      break;
    case INPUT_MOTION:
      config.enable_motion_controls = !config.enable_motion_controls;
      save_settings();
      break;
    case INPUT_SWAP_SHOULDERS:
      config.swap_shoulder_buttons = !config.swap_shoulder_buttons;
      if (config.swap_shoulder_buttons && config.mapping) {
        free(config.mapping);
        config.mapping = NULL;
      }
      save_settings();
      break;
    case INPUT_DOUBLE_TAP_SPRINT:
      config.enable_double_tap_sprint = !config.enable_double_tap_sprint;
      save_settings();
      break;
    default:
      break;
  }
}

bool stream_overlay_is_open(void) {
  return overlay_open;
}

void stream_overlay_open(void) {
  if (overlay_open) return;
  return_to_main();
  settings_changed = false;
  overlay_open = true;
  vitavideo_request_redraw();

  // Release every remote button before the menu begins consuming local input.
  LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
  vita_debug_log("Stream menu opened");
}

void stream_overlay_close(void) {
  ui_diagnostics_screen_close();
  overlay_open = false;
  vitavideo_request_redraw();
  vita_debug_log("Stream menu closed");
}

void stream_overlay_reset(void) {
  overlay_open = false;
  disconnect_requested = false;
  close_game_requested = false;
  quit_app_requested = false;
  recover_host_requested = false;
  apply_display_requested = false;
  apply_input_requested = false;
  apply_display_virtual_key = 0;
  ui_diagnostics_screen_close();
  return_to_main();
  settings_changed = false;
}

bool stream_overlay_take_disconnect_request(void) {
  if (!disconnect_requested) return false;
  disconnect_requested = false;
  return true;
}

bool stream_overlay_take_close_game_request(void) {
  if (!close_game_requested) return false;
  close_game_requested = false;
  return true;
}

bool stream_overlay_take_quit_app_request(void) {
  if (!quit_app_requested) return false;
  quit_app_requested = false;
  return true;
}

bool stream_overlay_take_recover_host_request(void) {
  if (!recover_host_requested) return false;
  recover_host_requested = false;
  return true;
}

bool stream_overlay_take_apply_display_request(int *virtual_key) {
  if (!apply_display_requested) return false;
  apply_display_requested = false;
  if (virtual_key) *virtual_key = apply_display_virtual_key;
  apply_display_virtual_key = 0;
  return true;
}

bool stream_overlay_take_apply_input_request(void) {
  if (!apply_input_requested) return false;
  apply_input_requested = false;
  return true;
}

static void confirm_main_action(void) {
  if (selected_item == MAIN_RESUME) {
    stream_overlay_close();
    return;
  }
  if (selected_item == MAIN_STREAM) {
    enter_page(OVERLAY_PAGE_STREAM);
    return;
  }
  if (selected_item == MAIN_INPUT) {
    enter_page(OVERLAY_PAGE_INPUT);
    return;
  }
  if (selected_item == MAIN_PERFORMANCE) {
    ui_diagnostics_cycle_overlay_mode(1);
    save_settings();
    return;
  }
  if (selected_item == MAIN_DIAGNOSTICS) {
    ui_diagnostics_screen_open();
    return;
  }
  if (selected_item == MAIN_LOGGING) {
    bool enabled = !vita_debug_is_logging_enabled();
    vita_debug_set_logging_enabled(enabled);
    save_settings();
    if (enabled) {
      vita_debug_log("[DIAGNOSTICS] Optional file logging enabled by user");
    }
    return;
  }
  if (selected_item == MAIN_KEYBOARD) {
    keyboardsystem_open_keyboard();
    return;
  }
  if (selected_item == MAIN_DISCONNECT) {
    disconnect_requested = true;
    stream_overlay_close();
    return;
  }

  if (confirmation_item != selected_item) {
    confirmation_item = selected_item;
    return;
  }

  if (selected_item == MAIN_CLOSE_GAME) close_game_requested = true;
  if (selected_item == MAIN_QUIT_APP) quit_app_requested = true;
  if (selected_item == MAIN_RECOVER_HOST) recover_host_requested = true;
  confirmation_item = -1;
  stream_overlay_close();
}

void stream_overlay_handle_input(const SceCtrlData *pad,
                                 const SceCtrlData *previous) {
  if (!overlay_open) return;
  bool input_edge =
      pad && previous && pad->buttons != previous->buttons;

  if (ui_diagnostics_screen_is_open()) {
    ui_diagnostics_screen_handle_input(pad, previous);
    if (input_edge) vitavideo_request_redraw();
    return;
  }

  if (pressed(pad, previous, SCE_CTRL_UP)) {
    selected_item =
        next_index(selected_item, current_item_count(), -1);
    confirmation_item = -1;
  } else if (pressed(pad, previous, SCE_CTRL_DOWN)) {
    selected_item =
        next_index(selected_item, current_item_count(), 1);
    confirmation_item = -1;
  }

  if (pressed(pad, previous, SCE_CTRL_LEFT)) {
    confirmation_item = -1;
    if (page == OVERLAY_PAGE_MAIN &&
        selected_item == MAIN_PERFORMANCE) {
      ui_diagnostics_cycle_overlay_mode(-1);
      save_settings();
    } else if (page == OVERLAY_PAGE_MAIN &&
               selected_item == MAIN_LOGGING) {
      vita_debug_set_logging_enabled(!vita_debug_is_logging_enabled());
      save_settings();
    } else if (page == OVERLAY_PAGE_STREAM) {
      adjust_stream_item(-1);
    } else if (page == OVERLAY_PAGE_INPUT) {
      adjust_input_item(-1);
    }
  }
  if (pressed(pad, previous, SCE_CTRL_RIGHT)) {
    confirmation_item = -1;
    if (page == OVERLAY_PAGE_MAIN &&
        selected_item == MAIN_PERFORMANCE) {
      ui_diagnostics_cycle_overlay_mode(1);
      save_settings();
    } else if (page == OVERLAY_PAGE_MAIN &&
               selected_item == MAIN_LOGGING) {
      vita_debug_set_logging_enabled(!vita_debug_is_logging_enabled());
      save_settings();
    } else if (page == OVERLAY_PAGE_STREAM) {
      adjust_stream_item(1);
    } else if (page == OVERLAY_PAGE_INPUT) {
      adjust_input_item(1);
    }
  }

  if (pressed(pad, previous, config.btn_cancel)) {
    if (confirmation_item >= 0) {
      confirmation_item = -1;
    } else if (page != OVERLAY_PAGE_MAIN) {
      return_to_main();
    } else {
      stream_overlay_close();
    }
    if (input_edge) vitavideo_request_redraw();
    return;
  }

  if (!pressed(pad, previous, config.btn_confirm)) {
    if (input_edge) vitavideo_request_redraw();
    return;
  }

  if (page == OVERLAY_PAGE_MAIN) {
    confirm_main_action();
    if (input_edge) vitavideo_request_redraw();
    return;
  }

  if (selected_item == 0) {
    return_to_main();
  } else if (page == OVERLAY_PAGE_STREAM &&
             selected_item == STREAM_APPLY_RECONNECT) {
    apply_display_virtual_key = resolution_virtual_key();
    apply_display_requested = true;
    stream_overlay_close();
  } else if (page == OVERLAY_PAGE_INPUT &&
             selected_item == INPUT_APPLY_RECONNECT) {
    apply_input_requested = true;
    stream_overlay_close();
  } else if (page == OVERLAY_PAGE_STREAM) {
    adjust_stream_item(1);
  } else {
    adjust_input_item(1);
  }
  if (input_edge) vitavideo_request_redraw();
}

static const char *touch_mode_name(void) {
  switch (config.touchscreen_mode) {
    case 1: return "DS4 touchpad";
    case 2: return "Absolute mouse";
    case 3: return "Tablet / Sunshine";
    default: return "Relative mouse";
  }
}

static void draw_row(int index,
                     int first_y,
                     int row_height,
                     const char *label,
                     const char *value) {
  const int x = 190;
  const int y = first_y + index * row_height;
  const int width = 580;
  unsigned int text_color = RGBA8(235, 240, 250, 255);
  if (selected_item == index) {
    vita2d_draw_rectangle(
        x, y - row_height + 5, width, row_height,
        RGBA8(47, 111, 237, 225));
    text_color = RGBA8(255, 255, 255, 255);
  }
  vita2d_font_draw_text(font, x + 12, y, text_color, 17, label);
  if (value && value[0]) {
    int value_width = vita2d_font_text_width(font, 17, value);
    vita2d_font_draw_text(
        font, x + width - value_width - 12, y, text_color, 17, value);
  }
}

static void draw_main_page(void) {
  const int first_y = 119;
  const int row_height = 32;
  draw_row(MAIN_RESUME, first_y, row_height, "Resume stream", "X");
  draw_row(MAIN_STREAM, first_y, row_height, "Stream & virtual display", ">");
  draw_row(MAIN_INPUT, first_y, row_height, "Controller & input", ">");
  draw_row(
      MAIN_PERFORMANCE, first_y, row_height, "Performance overlay",
      ui_diagnostics_overlay_mode_name(ui_diagnostics_get_overlay_mode()));
  draw_row(MAIN_DIAGNOSTICS, first_y, row_height, "Real-time diagnostics", ">");
  draw_row(
      MAIN_LOGGING, first_y, row_height, "Diagnostic file logging",
      vita_debug_is_logging_enabled() ? "On" : "Off");
  draw_row(MAIN_KEYBOARD, first_y, row_height, "Open on-screen keyboard", "X");
  draw_row(MAIN_CLOSE_GAME, first_y, row_height, "Close Windows game", "X");
  draw_row(MAIN_QUIT_APP, first_y, row_height, "End Sunshine app", "X");
  draw_row(MAIN_RECOVER_HOST, first_y, row_height, "Recover host display", "X");
  draw_row(MAIN_DISCONNECT, first_y, row_height, "Disconnect stream", "X");
}

static void draw_stream_page(void) {
  char value[64];
  const int first_y = 108;
  const int row_height = 29;

  draw_row(STREAM_BACK, first_y, row_height, "Back", "O");
  draw_row(
      STREAM_PRESET, first_y, row_height, "Streaming preset",
      config_stream_preset_name(config_detect_stream_preset()));

  snprintf(value, sizeof(value), "%dx%d",
           config.stream.width, config.stream.height);
  draw_row(
      STREAM_RESOLUTION, first_y, row_height,
      "Stream + virtual display", value);

  snprintf(value, sizeof(value), "%d", config.stream.fps);
  draw_row(STREAM_FPS, first_y, row_height, "Frame rate", value);

  snprintf(value, sizeof(value), "%.1f Mbps",
           config.stream.bitrate / 1000.0f);
  draw_row(STREAM_BITRATE, first_y, row_height, "Video bitrate", value);

  draw_row(
      STREAM_NETWORK, first_y, row_height, "Network mode",
      network_names[config.stream.streamingRemotely]);
  draw_row(
      STREAM_FRAME_PACER, first_y, row_height, "Frame pacing",
      config.enable_frame_pacer ? "On" : "Off");
  draw_row(
      STREAM_PACKET_RECOVERY, first_y, row_height, "Packet-loss recovery",
      config.enable_ref_frame_invalidation ? "On" : "Off");
  draw_row(
      STREAM_SCALING, first_y, row_height, "Aspect scaling",
      config.center_region_only ? "Crop / fill" : "Fit entire frame");
  draw_row(
      STREAM_VBLANK, first_y, row_height, "Wait for Vita vblank",
      config.enable_vita_vblank_wait ? "On" : "Off");
  draw_row(
      STREAM_HOST_OPTIMIZE, first_y, row_height, "Host game optimization",
      config.sops ? "On" : "Off");
  draw_row(
      STREAM_APPLY_RECONNECT, first_y, row_height,
      "Apply resolution + reconnect", "X");
}

static void draw_input_page(void) {
  const int first_y = 115;
  const int row_height = 36;
  draw_row(INPUT_BACK, first_y, row_height, "Back", "O");
  draw_row(
      INPUT_PROFILE, first_y, row_height, "Controller preset",
      config_controller_profile_name(config_detect_controller_profile()));
  draw_row(
      INPUT_PSBUTTON, first_y, row_height, "PS button behavior",
      psbutton_names[config.psbutton_mode]);
  draw_row(
      INPUT_TOUCH, first_y, row_height, "Touchscreen mode",
      touch_mode_name());
  draw_row(
      INPUT_MOTION, first_y, row_height, "Gyroscope reporting",
      config.enable_motion_controls ? "On" : "Off");
  draw_row(
      INPUT_SWAP_SHOULDERS, first_y, row_height, "Swap L1/R1 with L2/R2",
      config.swap_shoulder_buttons ? "On" : "Off");
  draw_row(
      INPUT_DOUBLE_TAP_SPRINT, first_y, row_height, "Double-tap sprint helper",
      config.enable_double_tap_sprint ? "On" : "Off");
  draw_row(
      INPUT_APPLY_RECONNECT, first_y, row_height,
      "Apply input changes + reconnect", "X");
}

static const char *footer_text(void) {
  if (confirmation_item == MAIN_CLOSE_GAME) {
    return "Press X again to close the foreground Windows game. O: cancel";
  }
  if (confirmation_item == MAIN_QUIT_APP) {
    return "Press X again to end Sunshine's app and disconnect. O: cancel";
  }
  if (confirmation_item == MAIN_RECOVER_HOST) {
    return "Press X again to reset the VDD, display, and Sunshine. O: cancel";
  }
  if (page == OVERLAY_PAGE_STREAM) {
    return "Apply + reconnect restarts video and the VDD, not the Windows game";
  }
  if (page == OVERLAY_PAGE_INPUT) {
    return "Controller-type changes take full effect after reconnecting";
  }
  if (settings_changed) {
    return "Saved. D-pad changes values; stream-format changes need reconnect";
  }
  return "D-pad: navigate/change   X: select   O: resume/back";
}

void stream_overlay_draw(void) {
  if (!overlay_open) return;
  if (ui_diagnostics_screen_is_open()) {
    ui_diagnostics_screen_draw();
    return;
  }

  const char *subtitle = "Session controls";
  if (page == OVERLAY_PAGE_STREAM) subtitle = "Stream & virtual display";
  if (page == OVERLAY_PAGE_INPUT) subtitle = "Controller & input";

  vita2d_draw_rectangle(0, 0, 960, 544, RGBA8(5, 10, 20, 180));
  vita2d_draw_rectangle(160, 42, 640, 462, RGBA8(20, 28, 44, 245));
  vita2d_draw_rectangle(160, 42, 6, 462, RGBA8(47, 111, 237, 255));
  vita2d_font_draw_text(
      font, 190, 78, RGBA8(255, 255, 255, 255), 27, "Vita Moonlight");
  vita2d_font_draw_text(
      font, 190, 101, RGBA8(166, 181, 208, 255), 16, subtitle);

  if (page == OVERLAY_PAGE_STREAM) {
    draw_stream_page();
  } else if (page == OVERLAY_PAGE_INPUT) {
    draw_input_page();
  } else {
    draw_main_page();
  }

  vita2d_font_draw_text(
      font, 190, 486, RGBA8(166, 181, 208, 255), 14, footer_text());
}
