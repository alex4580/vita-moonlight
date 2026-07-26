/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015, 2016 Iwan Timmer
 *
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * Moonlight is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Moonlight; if not, see <http://www.gnu.org/licenses/>.
 */

#include <Limelight.h>

#include <stdio.h>
#include <stdbool.h>

#include <psp2/ctrl.h>

#define MAX_INPUTS 6

struct input_config {
  char* path;
  char* mapping;
};

struct touchscreen_deadzone {
  int top, bottom, left, right;
};

struct special_keys {
  int size, offset;
  unsigned int nw, ne, sw, se;
};

enum psbutton_mode {
  PSBUTTON_MODE_LOCAL_ESCAPE = 0,
  PSBUTTON_MODE_SAFE_GUIDE = 1,
  PSBUTTON_MODE_IMMEDIATE_GUIDE = 2,
  PSBUTTON_MODE_SYSTEM = 3,
  PSBUTTON_MODE_COUNT
};

enum stream_preset {
  STREAM_PRESET_RELIABLE = 0,
  STREAM_PRESET_RECOMMENDED = 1,
  STREAM_PRESET_QUALITY = 2,
  STREAM_PRESET_REMOTE = 3,
  STREAM_PRESET_CUSTOM = 4,
  STREAM_PRESET_COUNT
};

enum controller_profile {
  CONTROLLER_PROFILE_COMPATIBILITY = 0,
  CONTROLLER_PROFILE_STEAM = 1,
  CONTROLLER_PROFILE_CUSTOM = 2,
  CONTROLLER_PROFILE_COUNT
};

typedef struct _CONFIGURATION {
  // static configuration, value will be saved to config file
  int config_version;
  STREAM_CONFIGURATION stream;
  char* app;
  char* action;
  char* address;
  char* mapping;
  char* platform;
  uint32_t model;
  char* config_file;
  char key_dir[4096];
  bool sops;
  bool localaudio;
  bool fullscreen;
  bool forcehw;
  bool unsupported_version;
  struct touchscreen_deadzone back_deadzone;
  bool enable_front_touchzones;
  struct special_keys special_keys;
  bool disable_powersave;
  bool jp_layout;
  bool show_fps;
  int performance_overlay_mode; // 0=off, 1=FPS, 2=FPS+network, 3=advanced
  bool enable_frame_pacer;
  bool center_region_only;
  bool save_debug_log;
  struct input_config inputs[MAX_INPUTS];
  int inputsCount;
  int mouse_acceleration;
  bool enable_ref_frame_invalidation;
  bool enable_vita_vblank_wait;
  bool enable_motion_controls; //Metalface
  int psbutton_mode;
  bool enable_double_tap_sprint; //**
  uint32_t double_tap_sprint_step_time; //** -IN MILLISECONDS
  float motion_controls_scalar_x;//**
  float motion_controls_scalar_y;// **/
  FILE *log_file;
  // runtime configuration, value will be recreated at launch
  SceCtrlButtons btn_confirm;
  SceCtrlButtons btn_cancel;
  int pin;
  uint16_t port;
  int keyboard_layout; // 0=EN_US, 1=ES_ES, 2=ES_LATAM
  int touchscreen_mode; // 0=relative mouse, 1=DS4, 2=absolute mouse, 3=multitouch tablet
  int controller_type; // 1=Xbox (compatibility default), 2=DS4
  bool swap_shoulder_buttons; // Nuevo: swap R1/L1 <-> R2/L2
} CONFIGURATION, *PCONFIGURATION;

extern CONFIGURATION config;
extern char *config_path;

extern bool inputAdded;

bool config_file_parse(char* filename, PCONFIGURATION config);
void config_parse(int argc, char* argv[], PCONFIGURATION config);
void config_sanitize(PCONFIGURATION config);
void config_save(const char* filename, PCONFIGURATION config);
int config_recommended_bitrate(int width, int height, int fps);
int config_detect_stream_preset(void);
void config_apply_stream_preset(int preset);
const char *config_stream_preset_name(int preset);
int config_detect_controller_profile(void);
void config_apply_controller_profile(int profile);
const char *config_controller_profile_name(int profile);
void update_layout();
