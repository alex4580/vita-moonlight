#include "ui_stream_overlay.h"

#include "guilib.h"
#include "../config.h"
#include "../debug.h"
#include "../input/touchabsolute.h"
#include "../input/motion.h"
#include "../input/vita.h"

#include <Limelight.h>
#include <stdio.h>
#include <vita2d.h>

enum {
  OVERLAY_RESUME = 0,
  OVERLAY_RESOLUTION,
  OVERLAY_QUALITY,
  OVERLAY_FPS,
  OVERLAY_CONTROLLER,
  OVERLAY_PSBUTTON,
  OVERLAY_TOUCH,
  OVERLAY_SHOW_FPS,
  OVERLAY_CLOSE_GAME,
  OVERLAY_QUIT_APP,
  OVERLAY_RECOVER_HOST,
  OVERLAY_DISCONNECT,
  OVERLAY_ITEM_COUNT
};

static volatile bool overlay_open = false;
static volatile bool disconnect_requested = false;
static volatile bool close_game_requested = false;
static volatile bool quit_app_requested = false;
static volatile bool recover_host_requested = false;
static int selected_item = OVERLAY_RESUME;
static int confirmation_item = -1;
static bool settings_changed = false;

static const int resolutions[][2] = {
  {960, 544},
  {960, 540},
  {1280, 720},
};
static const int bitrates[] = {5000, 8000, 12000, 15000};
static const int frame_rates[] = {30, 60};
static const char *psbutton_names[] = {"Local", "Safe Steam Guide", "Immediate Guide", "LiveArea"};

static bool pressed(const SceCtrlData *pad, const SceCtrlData *previous, unsigned int button) {
  return (pad->buttons & button) && !(previous->buttons & button);
}

static int next_index(int current, int count, int direction) {
  current += direction;
  if (current < 0) current = count - 1;
  if (current >= count) current = 0;
  return current;
}

static int find_resolution(void) {
  for (unsigned int i = 0; i < sizeof(resolutions) / sizeof(resolutions[0]); i++) {
    if (config.stream.width == resolutions[i][0] && config.stream.height == resolutions[i][1]) return (int)i;
  }
  return 0;
}

static int find_value(const int values[], int count, int value) {
  int closest = 0;
  int closest_distance = value > values[0] ? value - values[0] : values[0] - value;
  for (int i = 1; i < count; i++) {
    int distance = value > values[i] ? value - values[i] : values[i] - value;
    if (distance < closest_distance) {
      closest = i;
      closest_distance = distance;
    }
  }
  return closest;
}

static void save_settings(void) {
  if (config_path) config_save(config_path, &config);
  settings_changed = true;
}

static void adjust_selected(int direction) {
  switch (selected_item) {
    case OVERLAY_RESOLUTION: {
      int index = next_index(find_resolution(), (int)(sizeof(resolutions) / sizeof(resolutions[0])), direction);
      config.stream.width = resolutions[index][0];
      config.stream.height = resolutions[index][1];
      config.stream.bitrate = config_recommended_bitrate(config.stream.width, config.stream.height, config.stream.fps);
      save_settings();
      break;
    }
    case OVERLAY_QUALITY: {
      int count = (int)(sizeof(bitrates) / sizeof(bitrates[0]));
      int index = next_index(find_value(bitrates, count, config.stream.bitrate), count, direction);
      config.stream.bitrate = bitrates[index];
      save_settings();
      break;
    }
    case OVERLAY_FPS: {
      int count = (int)(sizeof(frame_rates) / sizeof(frame_rates[0]));
      int index = next_index(find_value(frame_rates, count, config.stream.fps), count, direction);
      config.stream.fps = frame_rates[index];
      save_settings();
      break;
    }
    case OVERLAY_CONTROLLER:
      config.controller_type = config.controller_type == 1 ? 2 : 1;
      if (config.controller_type == 2) {
        // Steam profile: expose a complete DS4 instead of requiring four
        // separate settings changes that are easy to miss.
        config.enable_motion_controls = true;
        config.touchscreen_mode = 1;
        config.psbutton_mode = PSBUTTON_MODE_SAFE_GUIDE;
      } else {
        config.touchscreen_mode = 0;
        config.psbutton_mode = PSBUTTON_MODE_LOCAL_ESCAPE;
      }
      touchabsolute_enable(false);
      save_settings();
      break;
    case OVERLAY_PSBUTTON:
      config.psbutton_mode += direction;
      if (config.psbutton_mode < 0) config.psbutton_mode = PSBUTTON_MODE_COUNT - 1;
      if (config.psbutton_mode >= PSBUTTON_MODE_COUNT) config.psbutton_mode = 0;
      save_settings();
      break;
    case OVERLAY_TOUCH:
      config.touchscreen_mode += direction;
      if (config.touchscreen_mode < 0) config.touchscreen_mode = 3;
      if (config.touchscreen_mode > 3) config.touchscreen_mode = 0;
      touchabsolute_enable(config.touchscreen_mode == 2);
      save_settings();
      break;
    case OVERLAY_SHOW_FPS:
      config.show_fps = !config.show_fps;
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
  selected_item = OVERLAY_RESUME;
  confirmation_item = -1;
  settings_changed = false;
  overlay_open = true;
  LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
  vita_debug_log("Stream overlay opened");
}

void stream_overlay_close(void) {
  overlay_open = false;
  vita_debug_log("Stream overlay closed");
}

void stream_overlay_reset(void) {
  overlay_open = false;
  disconnect_requested = false;
  close_game_requested = false;
  quit_app_requested = false;
  recover_host_requested = false;
  selected_item = OVERLAY_RESUME;
  confirmation_item = -1;
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

void stream_overlay_handle_input(const SceCtrlData *pad, const SceCtrlData *previous) {
  if (!overlay_open) return;

  if (pressed(pad, previous, SCE_CTRL_UP)) {
    selected_item = next_index(selected_item, OVERLAY_ITEM_COUNT, -1);
    confirmation_item = -1;
  } else if (pressed(pad, previous, SCE_CTRL_DOWN)) {
    selected_item = next_index(selected_item, OVERLAY_ITEM_COUNT, 1);
    confirmation_item = -1;
  }

  if (pressed(pad, previous, SCE_CTRL_LEFT)) {
    confirmation_item = -1;
    adjust_selected(-1);
  }
  if (pressed(pad, previous, SCE_CTRL_RIGHT)) {
    confirmation_item = -1;
    adjust_selected(1);
  }

  if (pressed(pad, previous, config.btn_cancel)) {
    if (confirmation_item >= 0) {
      confirmation_item = -1;
      return;
    }
    stream_overlay_close();
    return;
  }
  if (!pressed(pad, previous, config.btn_confirm)) return;

  if (selected_item == OVERLAY_RESUME) {
    stream_overlay_close();
  } else if (selected_item == OVERLAY_CLOSE_GAME ||
             selected_item == OVERLAY_QUIT_APP ||
             selected_item == OVERLAY_RECOVER_HOST) {
    if (confirmation_item != selected_item) {
      confirmation_item = selected_item;
      return;
    }
    if (selected_item == OVERLAY_CLOSE_GAME) close_game_requested = true;
    if (selected_item == OVERLAY_QUIT_APP) quit_app_requested = true;
    if (selected_item == OVERLAY_RECOVER_HOST) recover_host_requested = true;
    confirmation_item = -1;
    stream_overlay_close();
  } else if (selected_item == OVERLAY_DISCONNECT) {
    disconnect_requested = true;
    stream_overlay_close();
  } else {
    adjust_selected(1);
  }
}

static const char *touch_mode_name(void) {
  switch (config.touchscreen_mode) {
    case 1: return "DS4 touchpad";
    case 2: return "Absolute mouse";
    case 3: return "Tablet";
    default: return "Relative mouse";
  }
}

static void draw_row(int index, const char *label, const char *value) {
  const int x = 190;
  const int y = 116 + index * 27;
  const int width = 580;
  unsigned int text_color = RGBA8(235, 240, 250, 255);
  if (selected_item == index) {
    vita2d_draw_rectangle(x, y - 23, width, 29, RGBA8(47, 111, 237, 225));
    text_color = RGBA8(255, 255, 255, 255);
  }
  vita2d_font_draw_text(font, x + 12, y, text_color, 19, label);
  if (value && value[0]) {
    int value_width = vita2d_font_text_width(font, 19, value);
    vita2d_font_draw_text(font, x + width - value_width - 12, y, text_color, 19, value);
  }
}

void stream_overlay_draw(void) {
  if (!overlay_open) return;

  char resolution[32];
  char quality[32];
  char fps[16];
  char motion_status[64];
  char input_status[64];
  VitaMotionStatus motion;
  VitaInputDiagnostics input;
  vita_motion_get_status(&motion);
  vitainput_get_diagnostics(&input);
  snprintf(resolution, sizeof(resolution), "%dx%d", config.stream.width, config.stream.height);
  snprintf(quality, sizeof(quality), "%.1f Mbps", config.stream.bitrate / 1000.0f);
  snprintf(fps, sizeof(fps), "%d", config.stream.fps);
  if (config.controller_type != 2 || !config.enable_motion_controls) {
    snprintf(motion_status, sizeof(motion_status), "Gyro: off");
  } else if (motion.last_sensor_error < 0) {
    snprintf(motion_status, sizeof(motion_status), "Gyro: sensor error %08X", (unsigned int)motion.last_sensor_error);
  } else if (motion.gyro_events_sent > 0) {
    snprintf(motion_status, sizeof(motion_status), "Gyro: live (%u events)", (unsigned int)motion.gyro_events_sent);
  } else if (motion.gyro_requested) {
    snprintf(motion_status, sizeof(motion_status), "Gyro: requested, waiting for samples");
  } else {
    snprintf(motion_status, sizeof(motion_status), "Gyro: host has not requested it");
  }
  snprintf(input_status, sizeof(input_status), "Circle: %u down / %u up%s",
           (unsigned int)input.circle_presses, (unsigned int)input.circle_releases,
           input.circle_held ? " (held)" : "");

  vita2d_draw_rectangle(0, 0, 960, 544, RGBA8(5, 10, 20, 180));
  vita2d_draw_rectangle(160, 42, 640, 462, RGBA8(20, 28, 44, 245));
  vita2d_draw_rectangle(160, 42, 6, 462, RGBA8(47, 111, 237, 255));
  vita2d_font_draw_text(font, 190, 78, RGBA8(255, 255, 255, 255), 27, "Vita Moonlight");
  vita2d_font_draw_text(font, 190, 101, RGBA8(166, 181, 208, 255), 16, "Stream controls");

  draw_row(OVERLAY_RESUME, "Resume", "X");
  draw_row(OVERLAY_RESOLUTION, "Resolution (next stream)", resolution);
  draw_row(OVERLAY_QUALITY, "Video quality (next stream)", quality);
  draw_row(OVERLAY_FPS, "Frame rate (next stream)", fps);
  draw_row(OVERLAY_CONTROLLER, "Controller preset (next stream)", config.controller_type == 1 ? "Xbox / local PS" : "Steam DS4 / gyro");
  draw_row(OVERLAY_PSBUTTON, "PS button", psbutton_names[config.psbutton_mode]);
  draw_row(OVERLAY_TOUCH, "Touchscreen", touch_mode_name());
  draw_row(OVERLAY_SHOW_FPS, "FPS counter", config.show_fps ? "On" : "Off");
  draw_row(OVERLAY_CLOSE_GAME, "Close Windows game", "X");
  draw_row(OVERLAY_QUIT_APP, "End Sunshine app", "X");
  draw_row(OVERLAY_RECOVER_HOST, "Recover display + Sunshine", "X");
  draw_row(OVERLAY_DISCONNECT, "Disconnect stream", "X");

  vita2d_font_draw_text(font, 190, 459, RGBA8(124, 207, 255, 255), 14, motion_status);
  int input_width = vita2d_font_text_width(font, 14, input_status);
  vita2d_font_draw_text(font, 770 - input_width, 459, RGBA8(124, 207, 255, 255), 14, input_status);

  const char *footer = settings_changed
    ? "Saved. Stream settings apply after reconnecting."
    : "D-pad: navigate/change   X: select   O: resume";
  if (confirmation_item == OVERLAY_CLOSE_GAME) {
    footer = "Press X again to close the foreground Windows game. O: cancel";
  } else if (confirmation_item == OVERLAY_QUIT_APP) {
    footer = "Press X again to end Sunshine's app and disconnect. O: cancel";
  } else if (confirmation_item == OVERLAY_RECOVER_HOST) {
    footer = "Press X again to reset display, VDD, and Sunshine. O: cancel";
  }
  vita2d_font_draw_text(font, 190, 486, RGBA8(166, 181, 208, 255), 15, footer);
}
