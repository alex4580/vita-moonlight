#include "ui_settings.h"

#include "guilib.h"
#include "ime.h"
#include "ui_controller_mapper.h"
#include "ui_diagnostics.h"
#include "ui_keyboard.h"

#include "../config.h"
#include "../input/vita.h"
#include "../input/swap_shoulder_buttons.h"
#include "../video/vita.h"
#include "../debug.h"
#include "../input/touchabsolute.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <psp2/ctrl.h>
#include <psp2/touch.h>
#include <psp2/videodec.h>
#include <vita2d.h>

#define MAX_RESOLUTION 3
static int RESOLUTIONS[MAX_RESOLUTION][2] = {
  {960, 544},   // VITA original
  {960, 540},   // strict 16:9 compatibility
  {1280, 720},  // 16:9, 720p HD
};

int support_resolution_idx[MAX_RESOLUTION] = {-1};
char *support_resolutions[MAX_RESOLUTION] = {0};
int support_resolution_count = 0;
static bool support_resolution_probe_complete = false;

static bool settings_loop_setup = 1;

/*
 * Deadzone
 */

#define lerp(value, from_max, to_max) ((((value*10) * (to_max*10))/(from_max*10))/10)
static void deadzone_draw() {
  int vertical = (WIDTH - config.back_deadzone.left - config.back_deadzone.right) / 2 + config.back_deadzone.left,
      horizontal = (HEIGHT - config.back_deadzone.top - config.back_deadzone.bottom) / 2 + config.back_deadzone.top;

  vita2d_draw_rectangle(
      config.back_deadzone.left,
      config.back_deadzone.top,
      WIDTH - config.back_deadzone.right - config.back_deadzone.left,
      HEIGHT - config.back_deadzone.bottom - config.back_deadzone.top,
      0x3000ff00
      );

  vita2d_draw_line(vertical, config.back_deadzone.top, vertical, HEIGHT - config.back_deadzone.bottom, 0xffffffff);
  vita2d_draw_line(config.back_deadzone.left, horizontal, WIDTH - config.back_deadzone.right, horizontal, 0xffffffff);

  SceTouchData touch_data;
  sceTouchPeek(SCE_TOUCH_PORT_BACK, &touch_data, 1);

  for (int i = 0; i < touch_data.reportNum; i++) {
    int x = lerp(touch_data.report[i].x, 1919, 960);
    int y = lerp(touch_data.report[i].y, 1087, 544);
    if (x < config.back_deadzone.left || x > WIDTH - config.back_deadzone.right)
      continue;

    if (y < config.back_deadzone.top || y > HEIGHT - config.back_deadzone.bottom)
      continue;

    vita2d_draw_fill_circle(x, y, 30, 0xffffffff);
  }
}

static int deadzone_loop(int cursor, void *context, const input_data *input) {
  menu_entry *menu = context;

  bool left = input->buttons & SCE_CTRL_LEFT;
  bool right = input->buttons & SCE_CTRL_RIGHT;

  int delta = left ? -15 : (right ? 15 : 0);
  switch (cursor) {
    case 0: config.back_deadzone.top += delta; break;
    case 1: config.back_deadzone.left += delta; break;
    case 2: config.back_deadzone.bottom += delta; break;
    case 3: config.back_deadzone.right += delta; break;
  }
  config_sanitize(&config);

  settings_loop_setup = 0;
  char current[256];

  int numbers[] = {
    config.back_deadzone.top,
    config.back_deadzone.left,
    config.back_deadzone.bottom,
    config.back_deadzone.right
  };
  for (int i = 0; i < 4; i++) {
    sprintf(current, "%dpx", numbers[i]);
    strcpy(menu[i].subname, current);
  }

  return 0;
}

static int deadzone_settings_menu() {
  config_sanitize(&config);
  sceTouchSetSamplingState(SCE_TOUCH_PORT_BACK, SCE_TOUCH_SAMPLING_STATE_START);

  menu_entry menu[16];
  int idx = 0;
  menu[idx++] = (menu_entry) { .name = "Top: ", .disabled = false, .id = 0, .suffix = ICON_LEFT_RIGHT_ARROWS };
  menu[idx++] = (menu_entry) { .name = "Left: ", .disabled = false, .id = 1, .suffix = ICON_LEFT_RIGHT_ARROWS };
  menu[idx++] = (menu_entry) { .name = "Bottom: ", .disabled = false, .id = 2, .suffix = ICON_LEFT_RIGHT_ARROWS };
  menu[idx++] = (menu_entry) { .name = "Right: ", .disabled = false, .id = 3, .suffix = ICON_LEFT_RIGHT_ARROWS };

  menu_geom geom = make_geom_centered(250, 120);
  geom.x = 50;
  geom.y = 50;
  geom.el = 25;
  return display_menu(
      menu, idx, &geom, &deadzone_loop, NULL, &deadzone_draw, menu);
}

static const char* touch_mode_names[] = {
  "Relative mouse",
  "DS4 touchpad",
  "Absolute mouse",
  "Sunshine tablet"
};
static const char* psbutton_mode_names[] = {
  "Local; double-PS exits",
  "PC Guide; double-PS exits",
  "PC Guide immediately",
  "Vita system / LiveArea"
};
static const char* network_mode_names[] = {
  "Local only",
  "Remote / VPN",
  "Auto detect"
};

static const char *on_off(bool enabled) {
  return enabled ? "On" : "Off";
}

static void format_sensitivity(
    char *output, size_t output_size, float sensitivity) {
  int hundredths = (int)(sensitivity * 100.0f + 0.5f);
  snprintf(
      output, output_size, "%d.%02dx",
      hundredths / 100, hundredths % 100);
}

static bool parse_sensitivity(const char *text, float *sensitivity) {
  char *end = NULL;
  float value;

  if (!text || !text[0] || !sensitivity) return false;
  value = strtof(text, &end);
  if (!end || end == text || *end != '\0' ||
      !(value >= 0.1f && value <= 5.0f)) {
    return false;
  }
  *sensitivity = value;
  return true;
}

static void mapping_location_text(char *output, size_t output_size) {
  size_t key_dir_length = strlen(config.key_dir);
  snprintf(
      output, output_size, "Button map: %s%s%s",
      config.key_dir,
      key_dir_length > 0 && config.key_dir[key_dir_length - 1] != '/'
          ? "/"
          : "",
      config.mapping && config.mapping[0] != '\0'
          ? config.mapping
          : "mappings/vita.conf");
}

/*
 * Main menu
 */

enum {
  SETTINGS_RESOLUTION = 100,
  SETTINGS_STREAM_PRESET,
  SETTINGS_STREAM_HELP,
  SETTINGS_RESOLUTION_HELP,
  SETTINGS_ADVANCED_STREAM_HELP,
  SETTINGS_INPUT_HELP,
  SETTINGS_FPS,
  SETTINGS_BITRATE,
  SETTINGS_SOPS,
  SETTINGS_ENABLE_FRAME_INVAL,
  SETTINGS_ENABLE_STREAM_OPTIMIZE,
  SETTINGS_ENABLE_VITA_VBLANK_WAIT,
  SETTINGS_ENABLE_MOTION_CONTROLS,
  SETTINGS_MOTION_CONTROLS_SCALAR_X,
  SETTINGS_MOTION_CONTROLS_SCALAR_Y,
  SETTINGS_ENABLE_DOUBLE_TAP_SPRINT,
  SETTINGS_DOUBLE_TAP_SPRINT_STEP_TIME,
  SETTINGS_SAVE_DEBUG_LOG,
  SETTINGS_DISABLE_POWERSAVE,
  SETTINGS_JP_LAYOUT,
  SETTINGS_SHOW_FPS,
  SETTINGS_LOCAL_AUDIO,
  SETTINGS_ENABLE_FRAME_PACER,
  SETTINGS_CENTER_REGION_ONLY,
  SETTINGS_ENABLE_MAPPING,
  SETTINGS_CONTROLLER_MAPPER,
  SETTINGS_BACK_DEADZONE,
  SETTINGS_SPECIAL_KEYS,
  SETTINGS_ENABLE_SPECIAL_KEYS,
  SETTINGS_PSBUTTON_MODE,
  SETTINGS_CONTROLLER_TYPE,
  SETTINGS_SWAP_SHOULDER_BUTTONS,
  SETTINGS_MOUSE_ACCEL,
  SETTINGS_KEYBOARD_LAYOUT,
  SETTINGS_TOUCH_MODE_SELECT
};

enum {
  SETTINGS_VIEW_RESOLUTION,
  SETTINGS_VIEW_STREAM_PRESET,
  SETTINGS_VIEW_FPS,
  SETTINGS_VIEW_BITRATE,
  SETTINGS_VIEW_SOPS,
  SETTINGS_VIEW_ENABLE_FRAME_INVAL,
  SETTINGS_VIEW_ENABLE_STREAM_OPTIMIZE,
  SETTINGS_VIEW_ENABLE_VITA_VBLANK_WAIT,
  SETTINGS_VIEW_ENABLE_MOTION_CONTROLS,
  SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_X,
  SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_Y,
  SETTINGS_VIEW_ENABLE_DOUBLE_TAP_SPRINT,
  SETTINGS_VIEW_DOUBLE_TAP_SPRINT_STEP_TIME,
  SETTINGS_VIEW_SAVE_DEBUG_LOG,
  SETTINGS_VIEW_DISABLE_POWERSAVE,
  SETTINGS_VIEW_JP_LAYOUT,
  SETTINGS_VIEW_SHOW_FPS,
  SETTINGS_VIEW_LOCAL_AUDIO,
  SETTINGS_VIEW_ENABLE_FRAME_PACER,
  SETTINGS_VIEW_CENTER_REGION_ONLY,
  SETTINGS_VIEW_ENABLE_MAPPING,
  SETTINGS_VIEW_MAPPING_LOCATION,
  SETTINGS_VIEW_BACK_DEADZONE,
  SETTINGS_VIEW_ENABLE_SPECIAL_KEYS,
  SETTINGS_VIEW_PSBUTTON_MODE,
  SETTINGS_VIEW_CONTROLLER_TYPE,
  SETTINGS_VIEW_SWAP_SHOULDER_BUTTONS,
  SETTINGS_VIEW_MOUSE_ACCEL,
  SETTINGS_VIEW_KEYBOARD_LAYOUT,
  SETTINGS_VIEW_TOUCH_MODE_SELECT,

  SETTINGS_VIEW_MAX_COUNT,
};

static int SETTINGS_VIEW_IDX[SETTINGS_VIEW_MAX_COUNT];
static int settings_view_menu_count = 0;

enum {
  SETTINGS_ROOT_RECOMMENDED = 1000,
  SETTINGS_ROOT_STREAM,
  SETTINGS_ROOT_CONTROLLER,
  SETTINGS_ROOT_TOUCH_KEYBOARD,
  SETTINGS_ROOT_SYSTEM_SUPPORT,
  SETTINGS_ROOT_ADVANCED
};

static bool settings_view_available(int view) {
  return view >= 0 && view < SETTINGS_VIEW_MAX_COUNT &&
         SETTINGS_VIEW_IDX[view] >= 0 &&
         SETTINGS_VIEW_IDX[view] < settings_view_menu_count;
}

static void settings_set_subname(
    menu_entry *menu, int view, const char *value) {
  if (!menu || !value || !settings_view_available(view)) return;
  snprintf(
      menu[SETTINGS_VIEW_IDX[view]].subname,
      sizeof(menu[SETTINGS_VIEW_IDX[view]].subname),
      "%s", value);
}

static int settings_category_for_id(int id) {
  switch (id) {
    case SETTINGS_STREAM_PRESET:
    case SETTINGS_STREAM_HELP:
    case SETTINGS_RESOLUTION:
    case SETTINGS_RESOLUTION_HELP:
    case SETTINGS_FPS:
    case SETTINGS_BITRATE:
    case SETTINGS_CENTER_REGION_ONLY:
      return SETTINGS_ROOT_STREAM;

    case SETTINGS_INPUT_HELP:
    case SETTINGS_ENABLE_MOTION_CONTROLS:
    case SETTINGS_MOTION_CONTROLS_SCALAR_X:
    case SETTINGS_MOTION_CONTROLS_SCALAR_Y:
    case SETTINGS_ENABLE_DOUBLE_TAP_SPRINT:
    case SETTINGS_DOUBLE_TAP_SPRINT_STEP_TIME:
    case SETTINGS_CONTROLLER_TYPE:
    case SETTINGS_SWAP_SHOULDER_BUTTONS:
    case SETTINGS_ENABLE_MAPPING:
    case SETTINGS_CONTROLLER_MAPPER:
    case SETTINGS_PSBUTTON_MODE:
      return SETTINGS_ROOT_CONTROLLER;

    case SETTINGS_MOUSE_ACCEL:
    case SETTINGS_BACK_DEADZONE:
    case SETTINGS_SPECIAL_KEYS:
    case SETTINGS_ENABLE_SPECIAL_KEYS:
    case SETTINGS_TOUCH_MODE_SELECT:
    case SETTINGS_KEYBOARD_LAYOUT:
      return SETTINGS_ROOT_TOUCH_KEYBOARD;

    case SETTINGS_SAVE_DEBUG_LOG:
    case SETTINGS_DISABLE_POWERSAVE:
    case SETTINGS_JP_LAYOUT:
    case SETTINGS_SHOW_FPS:
    case SETTINGS_LOCAL_AUDIO:
      return SETTINGS_ROOT_SYSTEM_SUPPORT;

    case SETTINGS_SOPS:
    case SETTINGS_ENABLE_FRAME_INVAL:
    case SETTINGS_ENABLE_STREAM_OPTIMIZE:
    case SETTINGS_ENABLE_VITA_VBLANK_WAIT:
    case SETTINGS_ENABLE_FRAME_PACER:
    case SETTINGS_ADVANCED_STREAM_HELP:
      return SETTINGS_ROOT_ADVANCED;

    default:
      return -1;
  }
}

// Shared with the input path so shoulder swapping can update immediately.
bool swap_shoulder_buttons = false;

// _countof only works for variable allocated on the stack, not from malloc (sizeof(i) will be incorrect).
#define _countof(i) (sizeof(i) / sizeof((i)[0]))
#define _move_idx_in_array(a, f, i) move_idx_in_array((a), _countof(a), (f), (i))
static int move_idx_in_array(char *array[], int count, char *find, int index_dist) {
  int i = 0;
  for (; i < count; i++) {
    if (strcmp(find, array[i]) == 0) {
      i += index_dist;
      break;
    }
  }

  if (i >= count) {
    return count - 1;
  } else if (i < 0) {
    return 0;
  } else {
    return i;
  }
}


static int settings_loop(int id, void *context, const input_data *input) {
  menu_entry *menu = context;
  bool did_change = 0;
  bool left = (input->buttons & SCE_CTRL_LEFT) && (input->buttons & SCE_CTRL_HOLD) == 0;
  bool right = (input->buttons & SCE_CTRL_RIGHT) && (input->buttons & SCE_CTRL_HOLD) == 0;

  char current[256];
  int new_idx;

  if (id == SETTINGS_STREAM_HELP &&
      (input->buttons & config.btn_confirm) != 0 && (input->buttons & SCE_CTRL_HOLD) == 0) {
    display_alert(
        "Presets reset the complete stream path: resolution, FPS, bitrate, "
        "packet size, network detection, H.264/SDR color, stereo audio, "
        "host optimization, loss recovery, pacing, scaling, and power behavior.\n\n"
        "Recommended: native 960x544, 60 FPS, 8 Mbps.\n"
        "Reliable: 30 FPS/5 Mbps for unstable Wi-Fi.\n"
        "High quality: 12 Mbps for cleaner motion on a strong link.\n"
        "Remote/VPN: 30 FPS/4 Mbps plus remote-network handling.",
        NULL, 1, NULL, NULL);
    return 0;
  }
  if (id == SETTINGS_RESOLUTION_HELP &&
      (input->buttons & config.btn_confirm) != 0 && (input->buttons & SCE_CTRL_HOLD) == 0) {
    display_alert(
        "960x544 is the Vita panel's native resolution and the recommended default.\n\n"
        "960x540 is strict 16:9 compatibility and may leave a two-pixel border. "
        "1280x720 can help games with tiny interfaces, but costs more bandwidth "
        "and decoder work without adding panel detail.\n\n"
        "Resolution, FPS, and bitrate are negotiated when a stream starts. "
        "Use Apply + reconnect in the in-stream menu to change both Sunshine's "
        "encoder and the Windows virtual display.",
        NULL, 1, NULL, NULL);
    return 0;
  }
  if (id == SETTINGS_ADVANCED_STREAM_HELP &&
      (input->buttons & config.btn_confirm) != 0 && (input->buttons & SCE_CTRL_HOLD) == 0) {
    display_alert(
        "Bitrate improves detail during movement, but a value your Wi-Fi cannot "
        "sustain creates queues, delay, and packet loss. FPS 60 feels smoother "
        "and more responsive; FPS 30 halves the frame cadence and is easier to carry.\n\n"
        "Frame pacing evens delivery. Packet-loss recovery requests clean reference "
        "frames. Fit shows the whole desktop; Crop fills the panel by trimming edges. "
        "Vblank can reduce tearing but may add latency. Auto network mode is safest "
        "unless you know the host is local or reached through a VPN.",
        NULL, 1, NULL, NULL);
    return 0;
  }
  if (id == SETTINGS_INPUT_HELP &&
      (input->buttons & config.btn_confirm) != 0 && (input->buttons & SCE_CTRL_HOLD) == 0) {
    display_alert(
        "Maximum compatibility presents an Xbox controller, uses relative mouse "
        "touch, keeps PS local, and disables gyro/mappings/sprint helpers.\n\n"
        "Steam/DS4 + gyro presents a DualShock 4, enables gyro and DS4 touchpad, "
        "and uses Safe Guide. Horizontal (yaw/Y) and vertical (pitch/X) "
        "sensitivity scale the motion sent by the Vita; Steam Input can refine "
        "it further. Reconnect after "
        "changing profiles.\n\n"
        "Safe Guide delays the PC Guide press so double-PS can remain a reliable "
        "local escape. Immediate Guide can trigger Windows or media shortcuts.",
        NULL, 1, NULL, NULL);
    return 0;
  }

  if (id == SETTINGS_TOUCH_MODE_SELECT) {
    if ((input->buttons & config.btn_confirm) != 0 &&
        (input->buttons & SCE_CTRL_HOLD) == 0) {
      config.touchscreen_mode = (config.touchscreen_mode + 1) % 4;
      touchabsolute_enable(config.touchscreen_mode == 2);
      did_change = 1;
    }
    settings_set_subname(
        menu, SETTINGS_VIEW_TOUCH_MODE_SELECT,
        touch_mode_names[config.touchscreen_mode]);
  }
  switch (id) {
    case SETTINGS_SWAP_SHOULDER_BUTTONS: {
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      swap_shoulder_buttons = !swap_shoulder_buttons;
      config.swap_shoulder_buttons = swap_shoulder_buttons;
      did_change = 1;
      settings_set_subname(
          menu, SETTINGS_VIEW_SWAP_SHOULDER_BUTTONS,
          on_off(swap_shoulder_buttons));
      // Shoulder swapping and a custom map are mutually exclusive.
      if (swap_shoulder_buttons) {
        if (config.mapping) {
          ui_controller_mapping_set_enabled(false);
          settings_set_subname(
              menu, SETTINGS_VIEW_ENABLE_MAPPING, "Off");
        }
      }
      break;
    }
    case SETTINGS_ENABLE_MAPPING: {
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      bool enable_mapping = config.mapping == NULL;
      if (!ui_controller_mapping_set_enabled(enable_mapping)) {
        display_error(
            "Could not create or load the custom controller map.");
        break;
      }
      did_change = 1;
      settings_set_subname(
          menu, SETTINGS_VIEW_ENABLE_MAPPING,
          on_off(enable_mapping));
      settings_set_subname(
          menu, SETTINGS_VIEW_SWAP_SHOULDER_BUTTONS,
          on_off(config.swap_shoulder_buttons));
      break;
    }
  }

  if (id == SETTINGS_RESOLUTION &&
      !vitavideo_initialized() &&
      !support_resolution_probe_complete) {
    for (int i = 0; i < MAX_RESOLUTION; i++) {
      SceVideodecQueryInitInfoHwAvcdec dec = {0};
      dec.size = sizeof(SceVideodecQueryInitInfoHwAvcdec);
      dec.horizontal = VITA_DECODER_RESOLUTION(RESOLUTIONS[i][0]);
      dec.vertical = VITA_DECODER_RESOLUTION(RESOLUTIONS[i][1]);
      dec.numOfRefFrames = 5;
      dec.numOfStreams = 1;
      int ret = sceVideodecInitLibrary(SCE_VIDEODEC_TYPE_HW_AVCDEC, &dec);
      if (ret < 0) {
        continue;
      }
      sceVideodecTermLibrary(SCE_VIDEODEC_TYPE_HW_AVCDEC);
      support_resolutions[support_resolution_count] = calloc(10, sizeof(char));
      if (!support_resolutions[support_resolution_count]) {
        display_error("Not enough memory to check Vita video modes.");
        break;
      }
      snprintf(
          support_resolutions[support_resolution_count], 10,
          "%dx%d", RESOLUTIONS[i][0], RESOLUTIONS[i][1]);
      support_resolution_idx[support_resolution_count++] = i;
    }
    support_resolution_probe_complete = true;
  }

  switch (id) {
    case SETTINGS_STREAM_PRESET: {
      int preset = config_detect_stream_preset();
      if (!left && !right) break;
      if (preset == STREAM_PRESET_CUSTOM) {
        preset = STREAM_PRESET_RECOMMENDED;
      } else {
        preset = (preset + (left ? 3 : 1)) % 4;
      }
      config_apply_stream_preset(preset);
      did_change = 1;
      break;
    }
    case SETTINGS_CONTROLLER_TYPE: {
      int profile = config_detect_controller_profile();
      if (!left && !right) {
        break;
      }
      if (profile == CONTROLLER_PROFILE_CUSTOM) {
        profile = left ? CONTROLLER_PROFILE_STEAM
                       : CONTROLLER_PROFILE_COMPATIBILITY;
      } else {
        profile = profile == CONTROLLER_PROFILE_COMPATIBILITY
            ? CONTROLLER_PROFILE_STEAM
            : CONTROLLER_PROFILE_COMPATIBILITY;
      }
      config_apply_controller_profile(profile);
      ui_controller_mapping_set_enabled(false);
      swap_shoulder_buttons = false;
      touchabsolute_enable(config.touchscreen_mode == 2);
      did_change = 1;
      ui_settings_save_config();
      break;
    }
    case SETTINGS_RESOLUTION:
      if (!left && !right) {
        break;
      }
      if (vitavideo_initialized()) {
        break;
      }
      if (support_resolution_count <= 0) {
        display_error("No supported Vita video modes were detected.");
        break;
      }
      //char *resolutions[] = {"960x540", "960x544", "1280x540", "1280x720", "1920x1080"};
      int old_recommended_bitrate = config_recommended_bitrate(config.stream.width, config.stream.height, config.stream.fps);
      sprintf(current, "%dx%d", config.stream.width, config.stream.height);

      new_idx = move_idx_in_array(support_resolutions, support_resolution_count, current, left ? -1 : +1);
      config.stream.width = RESOLUTIONS[support_resolution_idx[new_idx]][0];
      config.stream.height = RESOLUTIONS[support_resolution_idx[new_idx]][1];
      if (config.stream.bitrate == old_recommended_bitrate) {
        config.stream.bitrate = config_recommended_bitrate(config.stream.width, config.stream.height, config.stream.fps);
      }

      did_change = 1;
      break;
    case SETTINGS_FPS:
      if (!left && !right) {
          break;
      }
      int old_fps_recommended_bitrate = config_recommended_bitrate(config.stream.width, config.stream.height, config.stream.fps);
      char *settings[] = {"24", "30", "40", "50", "60"};
      sprintf(current, "%d", config.stream.fps);
      new_idx = _move_idx_in_array(settings, current, left ? -1 : +1);

      switch (new_idx) {
        case 0: config.stream.fps = 24; break; // Movies
        case 1: config.stream.fps = 30; break;
        case 2: config.stream.fps = 40; break;
        case 3: config.stream.fps = 50; break; // PAL
        case 4: config.stream.fps = 60; break; // NTSC
      }

      if (config.stream.bitrate == old_fps_recommended_bitrate) {
        config.stream.bitrate = config_recommended_bitrate(config.stream.width, config.stream.height, config.stream.fps);
      }

      did_change = 1;
      break;
    case SETTINGS_BITRATE:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      {
        char value[512];
        int ret;
        if ((ret = ime_dialog_number(
                 value, "Enter bitrate in Kbps (1000-30000)", "")) == 0) {
          int bitrate = atoi(value);
          if (bitrate >= 1000 && bitrate <= 30000) {
            config.stream.bitrate = bitrate;
            did_change = 1;
          } else {
            display_error("Bitrate must be 1000-30000 Kbps: %s", value);
          }
        }
      }
      break;
    case SETTINGS_SOPS:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.sops = !config.sops;
      break;
    case SETTINGS_ENABLE_FRAME_INVAL:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      config.enable_ref_frame_invalidation = false;
      display_alert(
          "The Vita hardware decoder requires a one-reference-frame H.264 "
          "stream, so reference-frame invalidation is unavailable.\n\n"
          "Moonlight still requests a clean keyframe automatically when "
          "decoding must recover from packet loss.",
          NULL, 1, NULL, NULL);
      break;
    case SETTINGS_ENABLE_STREAM_OPTIMIZE:
      if (!left && !right) {
        break;
      }
      did_change = 1;
      config.stream.streamingRemotely =
          (config.stream.streamingRemotely + (left ? 2 : 1)) % 3;
      break;
    case SETTINGS_ENABLE_VITA_VBLANK_WAIT:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.enable_vita_vblank_wait = config.enable_vita_vblank_wait ? 0 : 1;
      break;
    case SETTINGS_ENABLE_MOTION_CONTROLS:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
          break;
      }
      did_change = 1;
      config.enable_motion_controls = config.enable_motion_controls ? 0 : 1;
      break;
    case SETTINGS_MOTION_CONTROLS_SCALAR_X: {
      if ((input->buttons & config.btn_confirm) == 0 ||
          input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      char value[512];
      if (ime_dialog_number(
              value, "Enter horizontal gyro sensitivity (0.1-5.0)", "") == 0) {
        float scalar;
        if (parse_sensitivity(value, &scalar)) {
          config.motion_controls_scalar_x = scalar;
          did_change = 1;
        } else {
          display_error(
              "Horizontal gyro sensitivity must be 0.1-5.0: %s",
              value);
        }
      }
      break;
    }
    case SETTINGS_MOTION_CONTROLS_SCALAR_Y: {
      if ((input->buttons & config.btn_confirm) == 0 ||
          input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      char value[512];
      if (ime_dialog_number(
              value, "Enter vertical gyro sensitivity (0.1-5.0)", "") == 0) {
        float scalar;
        if (parse_sensitivity(value, &scalar)) {
          config.motion_controls_scalar_y = scalar;
          did_change = 1;
        } else {
          display_error(
              "Vertical gyro sensitivity must be 0.1-5.0: %s",
              value);
        }
      }
      break;
    }
    case SETTINGS_ENABLE_DOUBLE_TAP_SPRINT:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
          break;
      }
      did_change = 1;
      config.enable_double_tap_sprint = config.enable_double_tap_sprint ? 0 : 1;
      break;
    case SETTINGS_DOUBLE_TAP_SPRINT_STEP_TIME:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
          break;
      }
      {
        char value[512];
        int ret;
        if ((ret = ime_dialog_number(
                 value, "Enter sprint double-tap window in milliseconds", "")) == 0) {
          int stp = atoi(value);
          if (stp >= 50 && stp <= 1000) {
            config.double_tap_sprint_step_time = stp;
            did_change = 1;
          } else {
            display_error(
                "Sprint double-tap window must be 50-1000 ms: %s",
                value);
          }
        }
      }
      break;
    case SETTINGS_SAVE_DEBUG_LOG: {
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      bool was_logging = vita_debug_is_logging_enabled();
      bool enable_log = !was_logging;
      char log_path[256] = "ux0:data/moonlight/moonlight.log";
      char log_message[768];

      vita_debug_set_logging_enabled(enable_log);
      bool logging_now = vita_debug_is_logging_enabled();
      if (enable_log && !logging_now) {
        display_error(
            "Support capture could not start. Check that the memory card has "
            "free space, then try again.");
        break;
      }
      did_change = logging_now != was_logging;
      vita_debug_get_log_path(log_path, sizeof(log_path));
      if (logging_now) {
        snprintf(
            log_message, sizeof(log_message),
            "Support capture is on.\n\n"
            "Reproduce the issue, return here, then choose Stop and save "
            "support log. The log records concise system, stream, and network "
            "summaries, not every button or touch.\n\n"
            "A fresh log was started at:\n%s",
            log_path);
      } else {
        snprintf(
            log_message, sizeof(log_message),
            "Support capture is off. Your log is saved at:\n%s\n\n"
            "Copy that file with VitaShell when reporting an issue.",
            log_path);
      }
      display_alert(log_message, NULL, 1, NULL, NULL);
      break;
    }
    case SETTINGS_DISABLE_POWERSAVE:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.disable_powersave = !config.disable_powersave;
      break;
    case SETTINGS_JP_LAYOUT:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.jp_layout = !config.jp_layout;
      break;
    case SETTINGS_SHOW_FPS:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      ui_diagnostics_cycle_overlay_mode(1);
      break;
    case SETTINGS_LOCAL_AUDIO:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.localaudio = !config.localaudio;
      break;
    case SETTINGS_ENABLE_FRAME_PACER:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.enable_frame_pacer = !config.enable_frame_pacer;
      break;
    case SETTINGS_CENTER_REGION_ONLY:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      did_change = 1;
      config.center_region_only = !config.center_region_only;
      break;
    case SETTINGS_BACK_DEADZONE:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      deadzone_settings_menu();
      did_change = 1;
      break;
    case SETTINGS_CONTROLLER_MAPPER:
      if ((input->buttons & config.btn_confirm) == 0 ||
          input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      if (ui_controller_mapper_menu()) {
        did_change = 1;
      }
      break;
    case SETTINGS_SPECIAL_KEYS:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      if (ui_front_touch_mapper_menu()) {
        did_change = 1;
      }
      break;
    case SETTINGS_ENABLE_SPECIAL_KEYS:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }

      config.enable_front_touchzones = !config.enable_front_touchzones;
      vitainput_refresh_touchzones();
      did_change = 1;
      break;
    case SETTINGS_PSBUTTON_MODE:
      if (!left && !right) {
        break;
      }
      config.psbutton_mode = (config.psbutton_mode + (left ? PSBUTTON_MODE_COUNT - 1 : 1)) % PSBUTTON_MODE_COUNT;
      did_change = 1;
      break;
    case SETTINGS_MOUSE_ACCEL:
      left = input->buttons & SCE_CTRL_LEFT;
      right = input->buttons & SCE_CTRL_RIGHT;
      if (!left && !right) {
          break;
      }
      if (left) {
        config.mouse_acceleration -= 15;
        if (config.mouse_acceleration < 15) {
          config.mouse_acceleration = 15;
        }
      } else {
        config.mouse_acceleration += 15;
        if (config.mouse_acceleration > 300) {
          config.mouse_acceleration = 300;
        }
      }

      did_change = 1;
      break;
    case SETTINGS_KEYBOARD_LAYOUT:
      if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
        break;
      }
      keyboard_layout_menu();
      did_change = 1;
      break;
  }

  if (!did_change && !settings_loop_setup) {
    return 0;
  }
  settings_loop_setup = 0;

#define MENU_REPLACE(ID, MESSAGE) \
  settings_set_subname(menu, (ID), (MESSAGE))

  sprintf(current, "%dx%d", config.stream.width, config.stream.height);
  MENU_REPLACE(SETTINGS_VIEW_RESOLUTION, current);

  MENU_REPLACE(SETTINGS_VIEW_STREAM_PRESET,
               config_stream_preset_name(config_detect_stream_preset()));

  sprintf(current, "%d", config.stream.fps);
  MENU_REPLACE(SETTINGS_VIEW_FPS, current);

  sprintf(current, "%d", config.stream.bitrate);
  MENU_REPLACE(SETTINGS_VIEW_BITRATE, current);

  MENU_REPLACE(SETTINGS_VIEW_SOPS, on_off(config.sops));

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_FRAME_INVAL,
      "Automatic IDR");

  sprintf(current, "%s", network_mode_names[config.stream.streamingRemotely]);
  MENU_REPLACE(SETTINGS_VIEW_ENABLE_STREAM_OPTIMIZE, current);

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_VITA_VBLANK_WAIT,
      on_off(config.enable_vita_vblank_wait));

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_MOTION_CONTROLS,
      on_off(config.enable_motion_controls));

  format_sensitivity(
      current, sizeof(current), config.motion_controls_scalar_x);
  MENU_REPLACE(SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_X, current);

  format_sensitivity(
      current, sizeof(current), config.motion_controls_scalar_y);
  MENU_REPLACE(SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_Y, current);

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_DOUBLE_TAP_SPRINT,
      on_off(config.enable_double_tap_sprint));

  sprintf(current, "%u", config.double_tap_sprint_step_time);
  MENU_REPLACE(SETTINGS_VIEW_DOUBLE_TAP_SPRINT_STEP_TIME, current);
  MENU_REPLACE(
      SETTINGS_VIEW_DISABLE_POWERSAVE,
      on_off(config.disable_powersave));

  MENU_REPLACE(SETTINGS_VIEW_JP_LAYOUT, on_off(config.jp_layout));

  sprintf(current, "%s", ui_diagnostics_overlay_mode_name(
      ui_diagnostics_get_overlay_mode()));
  MENU_REPLACE(SETTINGS_VIEW_SHOW_FPS, current);

  MENU_REPLACE(SETTINGS_VIEW_LOCAL_AUDIO, on_off(config.localaudio));

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_FRAME_PACER,
      on_off(config.enable_frame_pacer));

  sprintf(current, "%s",
          config.center_region_only ? "Crop / fill" : "Fit entire frame");
  MENU_REPLACE(SETTINGS_VIEW_CENTER_REGION_ONLY, current);

  bool support_log_active = vita_debug_is_logging_enabled();
  if (settings_view_available(SETTINGS_VIEW_SAVE_DEBUG_LOG)) {
    menu[SETTINGS_VIEW_IDX[SETTINGS_VIEW_SAVE_DEBUG_LOG]].name =
        support_log_active
            ? "Stop and save support log"
            : "Start support log";
  }
  sprintf(current, "%s", support_log_active ? "Capturing" : "Off");
  MENU_REPLACE(SETTINGS_VIEW_SAVE_DEBUG_LOG, current);

  sprintf(current, "%s", config_controller_profile_name(
      config_detect_controller_profile()));
  MENU_REPLACE(SETTINGS_VIEW_CONTROLLER_TYPE, current);

  sprintf(current, "%s", psbutton_mode_names[config.psbutton_mode]);
  MENU_REPLACE(SETTINGS_VIEW_PSBUTTON_MODE, current);

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_SPECIAL_KEYS,
      on_off(config.enable_front_touchzones));

  MENU_REPLACE(
      SETTINGS_VIEW_ENABLE_MAPPING,
      on_off(config.mapping != NULL));

  mapping_location_text(current, sizeof(current));
  MENU_REPLACE(SETTINGS_VIEW_MAPPING_LOCATION, current);

  MENU_REPLACE(
      SETTINGS_VIEW_SWAP_SHOULDER_BUTTONS,
      on_off(config.swap_shoulder_buttons));

  sprintf(current, "T:%d L:%d B:%d R:%d",
          config.back_deadzone.top,
          config.back_deadzone.left,
          config.back_deadzone.bottom,
          config.back_deadzone.right);
  MENU_REPLACE(SETTINGS_VIEW_BACK_DEADZONE, current);

  sprintf(current, "%d", config.mouse_acceleration);
  MENU_REPLACE(SETTINGS_VIEW_MOUSE_ACCEL, current);

  settings_set_subname(
      menu, SETTINGS_VIEW_TOUCH_MODE_SELECT,
      touch_mode_names[config.touchscreen_mode]);
#undef MENU_REPLACE
  return 0;
}

static int settings_back(void *context) {
  ui_settings_save_config();
  update_layout();
  return 0;
}

static int ui_settings_category_menu(int category) {
  menu_entry menu[24];
  int idx = 0;
  settings_view_menu_count = 0;
  if (category < SETTINGS_ROOT_STREAM ||
      category > SETTINGS_ROOT_ADVANCED) {
    return 1;
  }
  for (int i = 0; i < SETTINGS_VIEW_MAX_COUNT; i++) {
    SETTINGS_VIEW_IDX[i] = -1;
  }
  const char *category_title =
      category == SETTINGS_ROOT_STREAM ? "Stream quality" :
      category == SETTINGS_ROOT_CONTROLLER ? "Controller" :
      category == SETTINGS_ROOT_TOUCH_KEYBOARD ? "Touch and keyboard" :
      category == SETTINGS_ROOT_SYSTEM_SUPPORT ? "System and support" :
      category == SETTINGS_ROOT_ADVANCED ? "Advanced streaming" :
      "Settings";
  menu[idx++] = (menu_entry) {
    .name = (char *)category_title,
    .disabled = true,
    .separator = true
  };
#define MENU_ENTRY(ID, TAG, NAME, SUFFIX) \
  do { \
    if (settings_category_for_id((ID)) == category) { \
      if (idx >= (int)_countof(menu)) return 1; \
      menu[idx] = (menu_entry) { \
        .name = (NAME), .id = (ID), .suffix = (SUFFIX) \
      }; \
      SETTINGS_VIEW_IDX[(TAG)] = idx; \
      idx++; \
    } \
  } while(0)
#define MENU_ACTION(ID, NAME) \
  do { \
    if (settings_category_for_id((ID)) == category) { \
      if (idx >= (int)_countof(menu)) return 1; \
      menu[idx++] = (menu_entry) { .name = (NAME), .id = (ID) }; \
    } \
  } while(0)

  MENU_ENTRY(SETTINGS_STREAM_PRESET, SETTINGS_VIEW_STREAM_PRESET, "Streaming preset", ICON_LEFT_RIGHT_ARROWS);
  MENU_ACTION(SETTINGS_STREAM_HELP, "What do the presets change?");
  MENU_ENTRY(SETTINGS_RESOLUTION, SETTINGS_VIEW_RESOLUTION, "Stream + virtual display", ICON_LEFT_RIGHT_ARROWS);
  MENU_ACTION(SETTINGS_RESOLUTION_HELP, "Resolution and quality guide");
  MENU_ENTRY(SETTINGS_FPS, SETTINGS_VIEW_FPS, "Frame rate", ICON_LEFT_RIGHT_ARROWS);
  MENU_ENTRY(SETTINGS_BITRATE, SETTINGS_VIEW_BITRATE, "Video bitrate (Kbps)", "");
  MENU_ENTRY(SETTINGS_SOPS, SETTINGS_VIEW_SOPS, "Optimize games for streaming", "");
  MENU_ENTRY(SETTINGS_ENABLE_FRAME_INVAL, SETTINGS_VIEW_ENABLE_FRAME_INVAL, "Packet-loss recovery", "");
  MENU_ENTRY(SETTINGS_ENABLE_STREAM_OPTIMIZE, SETTINGS_VIEW_ENABLE_STREAM_OPTIMIZE, "Network mode", ICON_LEFT_RIGHT_ARROWS);
  MENU_ENTRY(SETTINGS_ENABLE_VITA_VBLANK_WAIT, SETTINGS_VIEW_ENABLE_VITA_VBLANK_WAIT, "Sync video to Vita display", "");
  MENU_ENTRY(SETTINGS_ENABLE_FRAME_PACER, SETTINGS_VIEW_ENABLE_FRAME_PACER, "Frame pacing", "");
  MENU_ENTRY(SETTINGS_CENTER_REGION_ONLY, SETTINGS_VIEW_CENTER_REGION_ONLY, "Aspect scaling", "");
  MENU_ACTION(SETTINGS_ADVANCED_STREAM_HELP, "Latency and recovery guide");

  MENU_ENTRY(SETTINGS_SHOW_FPS, SETTINGS_VIEW_SHOW_FPS, "Performance overlay", "");
  MENU_ENTRY(
      SETTINGS_SAVE_DEBUG_LOG, SETTINGS_VIEW_SAVE_DEBUG_LOG,
      vita_debug_is_logging_enabled()
          ? "Stop and save support log"
          : "Start support log",
      "");
  MENU_ENTRY(SETTINGS_LOCAL_AUDIO, SETTINGS_VIEW_LOCAL_AUDIO, "Play audio on PC too", "");
  MENU_ENTRY(SETTINGS_DISABLE_POWERSAVE, SETTINGS_VIEW_DISABLE_POWERSAVE, "Keep Vita awake while streaming", "");
  MENU_ENTRY(SETTINGS_JP_LAYOUT, SETTINGS_VIEW_JP_LAYOUT, "Swap X and O in Moonlight", "");

  MENU_ACTION(SETTINGS_INPUT_HELP, "Controller and gyro guide");

  MENU_ENTRY(SETTINGS_CONTROLLER_TYPE, SETTINGS_VIEW_CONTROLLER_TYPE, "Controller preset", ICON_LEFT_RIGHT_ARROWS);
  MENU_ENTRY(SETTINGS_PSBUTTON_MODE, SETTINGS_VIEW_PSBUTTON_MODE, "PS button behavior", ICON_LEFT_RIGHT_ARROWS);
  MENU_ENTRY(SETTINGS_ENABLE_MOTION_CONTROLS, SETTINGS_VIEW_ENABLE_MOTION_CONTROLS, "Send Vita gyro to PC", "");
  MENU_ENTRY(SETTINGS_MOTION_CONTROLS_SCALAR_X, SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_X, "Gyro horizontal sensitivity", "");
  MENU_ENTRY(SETTINGS_MOTION_CONTROLS_SCALAR_Y, SETTINGS_VIEW_MOTION_CONTROLS_SCALAR_Y, "Gyro vertical sensitivity", "");
  MENU_ENTRY(SETTINGS_ENABLE_DOUBLE_TAP_SPRINT, SETTINGS_VIEW_ENABLE_DOUBLE_TAP_SPRINT, "Double-tap sprint helper", "");
  MENU_ENTRY(SETTINGS_DOUBLE_TAP_SPRINT_STEP_TIME, SETTINGS_VIEW_DOUBLE_TAP_SPRINT_STEP_TIME, "Sprint double-tap window (ms)", "");
  MENU_ENTRY(SETTINGS_SWAP_SHOULDER_BUTTONS, SETTINGS_VIEW_SWAP_SHOULDER_BUTTONS, "Swap L1/R1 with L2/R2", "");
  MENU_ENTRY(SETTINGS_ENABLE_MAPPING, SETTINGS_VIEW_ENABLE_MAPPING, "Use custom button mapping", "");
  MENU_ACTION(SETTINGS_CONTROLLER_MAPPER, "Graphical button mapper");
  char mapping_location_msg[256];
  mapping_location_text(
      mapping_location_msg, sizeof(mapping_location_msg));
  if (category == SETTINGS_ROOT_CONTROLLER) {
    if (idx >= (int)_countof(menu)) return 1;
    SETTINGS_VIEW_IDX[SETTINGS_VIEW_MAPPING_LOCATION] = idx;
    menu[idx] = (menu_entry) { .name = "", .disabled = true };
    snprintf(
        menu[idx].subname, sizeof(menu[idx].subname),
        "%s", mapping_location_msg);
    idx++;
  }
  MENU_ENTRY(SETTINGS_TOUCH_MODE_SELECT, SETTINGS_VIEW_TOUCH_MODE_SELECT, "Touchscreen mode", "");
  MENU_ENTRY(SETTINGS_ENABLE_SPECIAL_KEYS, SETTINGS_VIEW_ENABLE_SPECIAL_KEYS, "Front-touch zones", "");
  MENU_ACTION(SETTINGS_SPECIAL_KEYS, "Front-touch zone mapper");
  MENU_ENTRY(SETTINGS_BACK_DEADZONE, SETTINGS_VIEW_BACK_DEADZONE, "Back touchscreen deadzone", "");
  MENU_ENTRY(SETTINGS_MOUSE_ACCEL, SETTINGS_VIEW_MOUSE_ACCEL, "Mouse acceleration", ICON_LEFT_RIGHT_ARROWS);
  MENU_ENTRY(SETTINGS_KEYBOARD_LAYOUT, SETTINGS_VIEW_KEYBOARD_LAYOUT, "Keyboard layout", "");

  settings_view_menu_count = idx;
  swap_shoulder_buttons = config.swap_shoulder_buttons;
  settings_loop_setup = 1;
  input_data no_input = {0};
  if (idx > 1) {
    settings_loop(menu[1].id, menu, &no_input);
  }
#undef MENU_ACTION
#undef MENU_ENTRY
  menu_geom geom = make_geom_centered(760, 400);
  geom.el = 32;
  int ret = display_menu(
      menu, idx, &geom, &settings_loop, &settings_back, NULL, menu);
  settings_view_menu_count = 0;
  return ret;
}

static int settings_root_loop(
    int id, void *context, const input_data *input) {
  (void)context;
  if ((input->buttons & config.btn_confirm) == 0 ||
      (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }

  if (id == SETTINGS_ROOT_RECOMMENDED) {
    if (!display_confirm(
            "Restore the Recommended stream preset and Maximum compatibility "
            "controller preset, and turn off the overlay and support capture? "
            "Custom stream, controller, and button-map choices will be "
            "replaced. Touch zones and keyboard layout are preserved.")) {
      return 0;
    }
    config_apply_stream_preset(STREAM_PRESET_RECOMMENDED);
    config_apply_controller_profile(CONTROLLER_PROFILE_COMPATIBILITY);
    config.motion_controls_scalar_x = 1.2f;
    config.motion_controls_scalar_y = 0.8f;
    config.double_tap_sprint_step_time = 200;
    ui_controller_mapping_set_enabled(false);
    swap_shoulder_buttons = false;
    ui_diagnostics_set_overlay_mode(UI_DIAGNOSTICS_OVERLAY_OFF);
    vita_debug_set_logging_enabled(false);
    touchabsolute_enable(false);
    ui_settings_save_config();
    update_layout();
    display_alert(
        "Recommended settings were restored. Start a new stream for "
        "resolution or controller-capability changes to take effect.",
        NULL, 1, NULL, NULL);
    return 0;
  }

  ui_settings_category_menu(id);
  return 0;
}

int ui_settings_menu() {
  menu_entry menu[6];
  int idx = 0;
  config_sanitize(&config);

#define ROOT_ENTRY(ID, NAME, DETAIL, SUFFIX) \
  do { \
    if (idx >= (int)_countof(menu)) return 1; \
    menu[idx] = (menu_entry) { \
      .name = (NAME), .id = (ID), .suffix = (SUFFIX) \
    }; \
    snprintf(menu[idx].subname, sizeof(menu[idx].subname), "%s", (DETAIL)); \
    idx++; \
  } while (0)

  ROOT_ENTRY(
      SETTINGS_ROOT_RECOMMENDED,
      "Restore recommended defaults",
      "Safe stream and controller setup", "");
  ROOT_ENTRY(
      SETTINGS_ROOT_STREAM,
      "Stream quality",
      "Preset, resolution, FPS, bitrate", ICON_RIGHT_ARROW);
  ROOT_ENTRY(
      SETTINGS_ROOT_CONTROLLER,
      "Controller",
      "Profile, gyro, PS, button mapping", ICON_RIGHT_ARROW);
  ROOT_ENTRY(
      SETTINGS_ROOT_TOUCH_KEYBOARD,
      "Touch and keyboard",
      "Touch modes, zones, typing", ICON_RIGHT_ARROW);
  ROOT_ENTRY(
      SETTINGS_ROOT_SYSTEM_SUPPORT,
      "System and support",
      "Overlay, support log, audio, power", ICON_RIGHT_ARROW);
  ROOT_ENTRY(
      SETTINGS_ROOT_ADVANCED,
      "Advanced streaming",
      "Network, pacing, loss recovery", ICON_RIGHT_ARROW);

#undef ROOT_ENTRY

  menu_geom geom = make_geom_centered(760, 330);
  geom.el = 44;
  return display_menu(
      menu, idx, &geom, &settings_root_loop, &settings_back, NULL, menu);
}

void ui_settings_save_config() {
  config_save(config_path, &config);
  vita_debug_log_config_snapshot("settings_saved");
}

