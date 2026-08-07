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

#include "config.h"
#include "audio.h"

#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <unistd.h>
#include <string.h>
#include <getopt.h>
#include <ini.h>
#include "input/vita.h"
#include "input/keyboardkeys.h"

#ifdef __vita__
#include <psp2/io/fcntl.h>
#endif

extern char* strdup(const char*);

#include <psp2/kernel/sysmem.h>

#define MOONLIGHT_PATH "/moonlight"
#define USER_PATHS "."
#define DEFAULT_CONFIG_DIR "/.config"
#define DEFAULT_CACHE_DIR "/.cache"
#define CURRENT_CONFIG_VERSION 5
#define DEFAULT_STREAM_WIDTH 960
#define DEFAULT_STREAM_HEIGHT 544
#define DEFAULT_STREAM_FPS 60
#define DEFAULT_PACKET_SIZE 1024
#define MIN_BITRATE_KBPS 1000
#define MAX_BITRATE_KBPS 30000
#define VITA_TOUCH_WIDTH 960
#define VITA_TOUCH_HEIGHT 544
#define CONFIG_COMPLETION_MARKER "# config_complete"

/*
 * Keep loaded/legacy configuration inside the same finite mode contract as
 * both Vita settings surfaces and the Windows host companion. The JSON
 * contract and CI checker intentionally mirror these values.
 */
static const int VITA_STREAM_CONTRACT_RESOLUTIONS[][2] = {
  {960, 544},
  {960, 540},
  {1280, 720},
};
static const int VITA_STREAM_CONTRACT_FRAME_RATES[] = {24, 30, 40, 50, 60};

static bool contract_supports_resolution(int width, int height) {
  for (unsigned int i = 0;
       i < sizeof(VITA_STREAM_CONTRACT_RESOLUTIONS) /
               sizeof(VITA_STREAM_CONTRACT_RESOLUTIONS[0]);
       i++) {
    if (VITA_STREAM_CONTRACT_RESOLUTIONS[i][0] == width &&
        VITA_STREAM_CONTRACT_RESOLUTIONS[i][1] == height) {
      return true;
    }
  }
  return false;
}

static bool contract_supports_frame_rate(int fps) {
  for (unsigned int i = 0;
       i < sizeof(VITA_STREAM_CONTRACT_FRAME_RATES) /
               sizeof(VITA_STREAM_CONTRACT_FRAME_RATES[0]);
       i++) {
    if (VITA_STREAM_CONTRACT_FRAME_RATES[i] == fps) return true;
  }
  return false;
}

#define write_config_string(fd, key, value) fprintf(fd, "%s = %s\n", key, value)
#define write_config_int(fd, key, value) fprintf(fd, "%s = %d\n", key, value)
#define write_config_hex(fd, key, value) fprintf(fd, "%s = %X\n", key, value)
#define write_config_bool(fd, key, value) fprintf(fd, "%s = %s\n", key, value?"true":"false")
#define write_config_float(fd, key, value) fprintf(fd, "%s = %f\n", key, value)
#define write_config_section(fd, key) fprintf(fd, "\n[%s]\n", key)

CONFIGURATION config;
char *config_path;

int config_recommended_bitrate(int width, int height, int fps) {
  (void)width;

  if (height <= 544) {
    return fps >= 60 ? 8000 : 5000;
  }
  if (height <= 720) {
    return fps >= 60 ? 10000 : 6000;
  }
  if (height < 1080) {
    return fps >= 60 ? 12000 : 8000;
  }
  return fps >= 60 ? 16000 : 10000;
}

static bool stream_preset_base_matches(void) {
  return config.stream.width == 960 &&
         config.stream.height == 544 &&
         config.stream.packetSize == 1024 &&
         config.stream.audioConfiguration == AUDIO_CONFIGURATION_STEREO &&
         config.stream.supportedVideoFormats == VIDEO_FORMAT_H264 &&
         config.stream.clientRefreshRateX100 == 6000 &&
         config.stream.colorSpace == COLORSPACE_REC_709 &&
         config.stream.colorRange == COLOR_RANGE_LIMITED &&
         config.sops &&
         !config.localaudio &&
         !config.enable_ref_frame_invalidation &&
         !config.enable_frame_pacer &&
         !config.enable_vita_vblank_wait &&
         !config.center_region_only &&
         config.disable_powersave;
}

int config_detect_stream_preset(void) {
  if (!stream_preset_base_matches()) return STREAM_PRESET_CUSTOM;

  if (config.stream.fps == 30 &&
      config.stream.bitrate == 5000 &&
      config.stream.streamingRemotely == STREAM_CFG_AUTO) {
    return STREAM_PRESET_RELIABLE;
  }
  if (config.stream.fps == 60 &&
      config.stream.bitrate == 8000 &&
      config.stream.streamingRemotely == STREAM_CFG_AUTO) {
    return STREAM_PRESET_RECOMMENDED;
  }
  if (config.stream.fps == 60 &&
      config.stream.bitrate == 12000 &&
      config.stream.streamingRemotely == STREAM_CFG_AUTO) {
    return STREAM_PRESET_QUALITY;
  }
  if (config.stream.fps == 30 &&
      config.stream.bitrate == 4000 &&
      config.stream.streamingRemotely == STREAM_CFG_REMOTE) {
    return STREAM_PRESET_REMOTE;
  }
  return STREAM_PRESET_CUSTOM;
}

void config_apply_stream_preset(int preset) {
  if (preset < STREAM_PRESET_RELIABLE || preset > STREAM_PRESET_REMOTE) {
    preset = STREAM_PRESET_RECOMMENDED;
  }

  config.stream.width = 960;
  config.stream.height = 544;
  config.stream.packetSize = 1024;
  config.stream.streamingRemotely =
      preset == STREAM_PRESET_REMOTE ? STREAM_CFG_REMOTE : STREAM_CFG_AUTO;
  config.stream.audioConfiguration = AUDIO_CONFIGURATION_STEREO;
  config.stream.supportedVideoFormats = VIDEO_FORMAT_H264;
  config.stream.clientRefreshRateX100 = 6000;
  config.stream.colorSpace = COLORSPACE_REC_709;
  config.stream.colorRange = COLOR_RANGE_LIMITED;
  config.sops = true;
  config.localaudio = false;
  /* The Vita hardware decoder requires the SPS one-reference-frame fixup.
   * moonlight-common explicitly forbids advertising RFI with that rewrite. */
  config.enable_ref_frame_invalidation = false;
  config.enable_frame_pacer = false;
  config.enable_vita_vblank_wait = false;
  config.center_region_only = false;
  config.disable_powersave = true;

  switch (preset) {
    case STREAM_PRESET_RELIABLE:
      config.stream.fps = 30;
      config.stream.bitrate = 5000;
      break;
    case STREAM_PRESET_QUALITY:
      config.stream.fps = 60;
      config.stream.bitrate = 12000;
      break;
    case STREAM_PRESET_REMOTE:
      config.stream.fps = 30;
      config.stream.bitrate = 4000;
      break;
    default:
      config.stream.fps = 60;
      config.stream.bitrate = 8000;
      break;
  }
}

const char *config_stream_preset_name(int preset) {
  switch (preset) {
    case STREAM_PRESET_RELIABLE: return "Reliable";
    case STREAM_PRESET_RECOMMENDED: return "Recommended";
    case STREAM_PRESET_QUALITY: return "High quality";
    case STREAM_PRESET_REMOTE: return "Remote / VPN";
    default: return "Custom";
  }
}

int config_detect_controller_profile(void) {
  bool common = !config.swap_shoulder_buttons &&
                config.mapping == NULL &&
                !config.enable_double_tap_sprint;
  if (common &&
      config.controller_type == 1 &&
      !config.enable_motion_controls &&
      config.touchscreen_mode == 0 &&
      config.psbutton_mode == PSBUTTON_MODE_LOCAL_ESCAPE) {
    return CONTROLLER_PROFILE_COMPATIBILITY;
  }
  if (common &&
      config.controller_type == 2 &&
      config.enable_motion_controls &&
      config.touchscreen_mode == 1 &&
      config.psbutton_mode == PSBUTTON_MODE_SAFE_GUIDE) {
    return CONTROLLER_PROFILE_STEAM;
  }
  return CONTROLLER_PROFILE_CUSTOM;
}

void config_apply_controller_profile(int profile) {
  if (config.mapping) {
    free(config.mapping);
    config.mapping = NULL;
  }
  config.swap_shoulder_buttons = false;
  config.enable_double_tap_sprint = false;

  if (profile == CONTROLLER_PROFILE_STEAM) {
    config.controller_type = 2;
    config.enable_motion_controls = true;
    config.touchscreen_mode = 1;
    config.psbutton_mode = PSBUTTON_MODE_SAFE_GUIDE;
  } else {
    config.controller_type = 1;
    config.enable_motion_controls = false;
    config.touchscreen_mode = 0;
    config.psbutton_mode = PSBUTTON_MODE_LOCAL_ESCAPE;
  }
}

const char *config_controller_profile_name(int profile) {
  switch (profile) {
    case CONTROLLER_PROFILE_COMPATIBILITY: return "Maximum compatibility";
    case CONTROLLER_PROFILE_STEAM: return "Steam / DS4 + gyro";
    default: return "Custom";
  }
}

bool inputAdded = false;
static bool mapped = true;
const char* audio_device = NULL;

static int ini_handle(void *out, const char *section, const char *name,
                      const char *value) {
#define HEX(v) strtol((v), NULL, 16)
#define INT(v) atoi((v))
#define BOOL(v) strcmp((v), "true") == 0
#define STR(v) strdup((v))
#define FLT(v) atof((v))

  PCONFIGURATION config = (PCONFIGURATION)out;
  if (strcmp(section, "backtouchscreen_deadzone") == 0) {
    if (strcmp(name, "top") == 0) {
      config->back_deadzone.top = INT(value);
    } else if (strcmp(name, "right") == 0) {
      config->back_deadzone.right = INT(value);
    } else if (strcmp(name, "bottom") == 0) {
      config->back_deadzone.bottom = INT(value);
    } else if (strcmp(name, "left") == 0) {
      config->back_deadzone.left = INT(value);
    }
    else if (strcmp(name, "controller_type") == 0) {
      config->controller_type = INT(value);
    } else if (strcmp(name, "swap_shoulder_buttons") == 0) {
      config->swap_shoulder_buttons = BOOL(value);
    } else if (strcmp(name, "key_dir") == 0) {
      /*
       * Legacy builds persisted the storage root. Never let an old config
       * redirect credentials after an SD/ux0/uma0 layout change. main.c has
       * already selected and validated the current writable data root.
       */
    }
  } else if (strcmp(section, "special_keys") == 0) {
    if (strcmp(name, "nw") == 0) {
      config->special_keys.nw = HEX(value);
    } else if (strcmp(name, "ne") == 0) {
      config->special_keys.ne = HEX(value);
    } else if (strcmp(name, "sw") == 0) {
      config->special_keys.sw = HEX(value);
    } else if (strcmp(name, "se") == 0) {
      config->special_keys.se = HEX(value);
    } else if (strcmp(name, "offset") == 0) {
      config->special_keys.offset = INT(value);
    } else if (strcmp(name, "size") == 0) {
      config->special_keys.size = INT(value);
    }
  } else {
    if (strcmp(name, "config_version") == 0) {
      config->config_version = INT(value);
    } else if (strcmp(name, "address") == 0) {
      config->address = STR(value);
    } else if (strcmp(name, "app") == 0) {
      config->app = STR(value);
    } else if (strcmp(name, "width") == 0) {
      config->stream.width = INT(value);
    } else if (strcmp(name, "height") == 0) {
      config->stream.height = INT(value);
    } else if (strcmp(name, "fps") == 0) {
      config->stream.fps = INT(value);
    } else if (strcmp(name, "bitrate") == 0) {
      config->stream.bitrate = INT(value);
    } else if (strcmp(name, "packetsize") == 0) {
      config->stream.packetSize = INT(value);
    } else if (strcmp(name, "sops") == 0) {
      config->sops = BOOL(value);
    } else if (strcmp(name, "localaudio") == 0) {
      config->localaudio = BOOL(value);
    } else if (strcmp(name, "enable_frame_pacer") == 0) {
      config->enable_frame_pacer = BOOL(value);
    } else if (strcmp(name, "center_region_only") == 0) {
      config->center_region_only = BOOL(value);
    } else if (strcmp(name, "disable_powersave") == 0) {
      config->disable_powersave = BOOL(value);
    } else if (strcmp(name, "jp_layout") == 0) {
      config->jp_layout = BOOL(value);
    } else if (strcmp(name, "show_fps") == 0) {
      config->show_fps = BOOL(value);
    } else if (strcmp(name, "performance_overlay_mode") == 0) {
      config->performance_overlay_mode = INT(value);
    } else if (strcmp(name, "save_debug_log") == 0) {
      /* Legacy preference: support-log capture is now always opt-in per run. */
      config->save_debug_log = false;
    } else if (strcmp(name, "mapping") == 0) {
      config->mapping = STR(value);
    } else if (strcmp(name, "mouse_acceleration") == 0) {
      config->mouse_acceleration = INT(value);
    } else if (strcmp(name, "enable_ref_frame_invalidation") == 0) {
      config->enable_ref_frame_invalidation = BOOL(value);
    } else if (strcmp(name, "enable_remote_stream_optimization") == 0) {
      config->stream.streamingRemotely = INT(value);
    } else if (strcmp(name, "enable_vita_vblank_wait") == 0) {
      config->enable_vita_vblank_wait = BOOL(value);
    } else if (strcmp(name, "enable_motion_controls") == 0) {
      config->enable_motion_controls = BOOL(value);
    } else if (strcmp(name, "enable_front_touchzones") == 0) {
      config->enable_front_touchzones = BOOL(value);
    } else if (strcmp(name, "psbutton_mode") == 0) {
      config->psbutton_mode = INT(value);
    } else if(strcmp(name, "enable_psbutton_capture") == 0) {
      // Migrate the old boolean to the safest equivalent. Captured PS presses
      // stay local by default instead of becoming a Windows Guide press.
      config->psbutton_mode = BOOL(value) ? PSBUTTON_MODE_LOCAL_ESCAPE : PSBUTTON_MODE_SYSTEM;
    } else if (strcmp(name, "enable_double_tap_sprint") == 0) {
      config->enable_double_tap_sprint = BOOL(value);
    } else if (strcmp(name, "double_tap_sprint_step_time") == 0) {
      config->double_tap_sprint_step_time = INT(value);
    } else if (strcmp(name, "motion_controls_scalar_x") == 0) {
      config->motion_controls_scalar_x = FLT(value);
    } else if (strcmp(name, "motion_controls_scalar_y") == 0) {
      config->motion_controls_scalar_y = FLT(value);
    } else if (strcmp(name, "keyboard_layout") == 0) {
      config->keyboard_layout = INT(value);
    } else if (strcmp(name, "touchscreen_mode") == 0) {
      config->touchscreen_mode = atoi(value);
    } else if (strcmp(name, "controller_type") == 0) {
      config->controller_type = INT(value);
    } else if (strcmp(name, "swap_shoulder_buttons") == 0) {
      config->swap_shoulder_buttons = BOOL(value);
    }
  }
  /* inih handlers return non-zero to continue parsing. Returning zero marks
   * the current line as an error (even when INI_STOP_ON_FIRST_ERROR is off). */
  return 1;
}

static char* config_sibling_name(const char* filename, const char* suffix) {
  if (!filename || !suffix) return NULL;
  size_t filename_length = strlen(filename);
  size_t suffix_length = strlen(suffix);
  if (filename_length > SIZE_MAX - suffix_length - 1) return NULL;

  char* result = malloc(filename_length + suffix_length + 1);
  if (!result) return NULL;
  memcpy(result, filename, filename_length);
  memcpy(result + filename_length, suffix, suffix_length + 1);
  return result;
}

static bool config_path_exists(const char* path) {
  FILE* file = fopen(path, "r");
  if (!file) return false;
  fclose(file);
  return true;
}

static int config_rename_file(const char* old_path, const char* new_path) {
#ifdef __vita__
  return sceIoRename(old_path, new_path);
#else
  return rename(old_path, new_path);
#endif
}

static int config_remove_file(const char* path) {
#ifdef __vita__
  return sceIoRemove(path);
#else
  return remove(path);
#endif
}

static bool config_temporary_file_complete(const char* path) {
  char line[64];
  bool complete = false;
  FILE* file = fopen(path, "r");
  if (!file) return false;
  while (fgets(line, sizeof(line), file)) {
    if (strncmp(line, CONFIG_COMPLETION_MARKER,
                sizeof(CONFIG_COMPLETION_MARKER) - 1) == 0) {
      complete = true;
    }
  }
  if (fclose(file) != 0) complete = false;
  return complete;
}

static void config_discard_candidate_strings(
    PCONFIGURATION candidate, const PCONFIGURATION base) {
  if (candidate->address != base->address) free(candidate->address);
  if (candidate->app != base->app) free(candidate->app);
  if (candidate->mapping != base->mapping) free(candidate->mapping);
}

static bool config_parse_candidate(
    const char* path, const PCONFIGURATION base, PCONFIGURATION candidate) {
  *candidate = *base;
  int parse_result = ini_parse(path, ini_handle, candidate);
  /* Every historical Vita Moonlight config written by this project contains
   * a positive config_version. Version 5 and newer are journaled and must also
   * contain the completion marker written last. This prevents an empty file,
   * or a current-format file truncated after an otherwise valid prefix, from
   * silently winning over its complete .bak/.tmp recovery candidate. */
  bool has_known_version = candidate->config_version > 0;
  bool current_file_complete =
      candidate->config_version < CURRENT_CONFIG_VERSION ||
      config_temporary_file_complete(path);
  if (parse_result == 0 && has_known_version && current_file_complete) {
    return true;
  }
  config_discard_candidate_strings(candidate, base);
  return false;
}

static bool config_promote_recovery(
    const char* source, const char* filename) {
  if (strcmp(source, filename) == 0) return true;
  if (config_path_exists(filename) && config_remove_file(filename) != 0) {
    return false;
  }
  return config_rename_file(source, filename) == 0;
}

static bool config_saved_state_exists(const char* filename) {
  if (config_path_exists(filename)) return true;
  char* backup_name = config_sibling_name(filename, ".bak");
  char* temporary_name = config_sibling_name(filename, ".tmp");
  bool exists = backup_name && config_path_exists(backup_name);
  if (!exists && temporary_name && config_path_exists(temporary_name)) {
    exists = config_temporary_file_complete(temporary_name);
  }
  free(backup_name);
  free(temporary_name);
  return exists;
}

bool config_file_parse(char* filename, PCONFIGURATION config) {
  if (!filename || !config) return false;

  char* temporary_name = config_sibling_name(filename, ".tmp");
  char* backup_name = config_sibling_name(filename, ".bak");
  if (!temporary_name || !backup_name) {
    free(temporary_name);
    free(backup_name);
    return false;
  }

  const char* candidates[3] = {filename, backup_name, temporary_name};
  CONFIGURATION parsed;
  const char* selected = NULL;
  for (unsigned int i = 0; i < 3; i++) {
    const char* path = candidates[i];
    if (!config_path_exists(path)) continue;
    if (path == temporary_name &&
        !config_temporary_file_complete(temporary_name)) {
      continue;
    }
    if (config_parse_candidate(path, config, &parsed)) {
      selected = path;
      break;
    }
  }

  if (!selected) {
    free(temporary_name);
    free(backup_name);
    return false;
  }

  *config = parsed;
  bool promoted = config_promote_recovery(selected, filename);
  if (!promoted) {
    fprintf(stderr, "Loaded recovery configuration but could not promote %s\n",
            selected);
  } else {
    /* Once a validated configuration is live, stale journal files are no
     * longer needed and must not win a later recovery decision. */
    if (strcmp(temporary_name, filename) != 0) {
      config_remove_file(temporary_name);
    }
    if (strcmp(backup_name, filename) != 0) config_remove_file(backup_name);
  }

  free(temporary_name);
  free(backup_name);
  return true;
}

static int clamp_int(int value, int minimum, int maximum) {
  if (value < minimum) {
    return minimum;
  }
  if (value > maximum) {
    return maximum;
  }
  return value;
}

/*
 * Keep the usable area at least one pixel wide while retaining the relative
 * shape of an invalid legacy deadzone as closely as possible.
 */
static void sanitize_deadzone_axis(int *leading, int *trailing, int extent) {
  *leading = clamp_int(*leading, 0, extent - 1);
  *trailing = clamp_int(*trailing, 0, extent - 1);

  int total = *leading + *trailing;
  if (total >= extent) {
    int usable_margin = extent - 1;
    int scaled_leading = (*leading * usable_margin) / total;
    *leading = scaled_leading;
    *trailing = usable_margin - scaled_leading;
  }
}

void config_sanitize(PCONFIGURATION config) {
  /* The managed host relies on Sunshine honoring the Vita launch mode. Old
   * builds exposed SOPS as a toggle, so migrate saved configurations to the
   * supported behavior instead of silently streaming the physical desktop. */
  config->sops = true;
  /* Migrate legacy installs that persisted RFI=On. Decoder errors still
   * request a clean IDR frame, which is safe with the Vita SPS rewrite. */
  config->enable_ref_frame_invalidation = false;
  /* The legacy "frame pacer" dropped future presentations based on a coarse
   * one-second count. It increased stutter and latency instead of spacing
   * frames, so current builds always use immediate presentation. */
  config->enable_frame_pacer = false;

  if (!contract_supports_resolution(
          config->stream.width, config->stream.height)) {
    config->stream.width = DEFAULT_STREAM_WIDTH;
    config->stream.height = DEFAULT_STREAM_HEIGHT;
  }
  if (!contract_supports_frame_rate(config->stream.fps)) {
    config->stream.fps = DEFAULT_STREAM_FPS;
  }
  if (config->stream.bitrate != -1 &&
      (config->stream.bitrate < MIN_BITRATE_KBPS || config->stream.bitrate > MAX_BITRATE_KBPS)) {
    config->stream.bitrate = -1;
  }
  if (config->stream.packetSize < 512 || config->stream.packetSize > 1400) {
    config->stream.packetSize = DEFAULT_PACKET_SIZE;
  }
  if (config->performance_overlay_mode < 0 ||
      config->performance_overlay_mode > 3) {
    config->performance_overlay_mode = 0;
  }
  if (config->stream.streamingRemotely < STREAM_CFG_LOCAL ||
      config->stream.streamingRemotely > STREAM_CFG_AUTO) {
    config->stream.streamingRemotely = STREAM_CFG_AUTO;
  }
  if (config->controller_type != 1 && config->controller_type != 2) {
    config->controller_type = 1;
  }
  if (config->touchscreen_mode < 0 || config->touchscreen_mode > 3) {
    config->touchscreen_mode = 0;
  }
  if (config->psbutton_mode < 0 || config->psbutton_mode >= PSBUTTON_MODE_COUNT) {
    config->psbutton_mode = PSBUTTON_MODE_LOCAL_ESCAPE;
  }
  if (config->keyboard_layout < 0 ||
      config->keyboard_layout >= KB_LAYOUT_COUNT) {
    config->keyboard_layout = KB_LAYOUT_EN_US;
  }
  if (config->mouse_acceleration < 15 || config->mouse_acceleration > 300) {
    config->mouse_acceleration = 150;
  }
  if (!(config->motion_controls_scalar_x >= 0.1f && config->motion_controls_scalar_x <= 5.0f)) {
    config->motion_controls_scalar_x = 1.2f;
  }
  if (!(config->motion_controls_scalar_y >= 0.1f && config->motion_controls_scalar_y <= 5.0f)) {
    config->motion_controls_scalar_y = 0.8f;
  }
  if (config->double_tap_sprint_step_time < 50 || config->double_tap_sprint_step_time > 1000) {
    config->double_tap_sprint_step_time = 200;
  }

  sanitize_deadzone_axis(&config->back_deadzone.left,
                         &config->back_deadzone.right,
                         VITA_TOUCH_WIDTH);
  sanitize_deadzone_axis(&config->back_deadzone.top,
                         &config->back_deadzone.bottom,
                         VITA_TOUCH_HEIGHT);

  /*
   * Special zones are square and mirrored into all four corners. Restrict
   * their offset and size to the smaller Vita touch dimension so every
   * generated rectangle remains positive and on-screen.
   */
  int special_extent =
      VITA_TOUCH_WIDTH < VITA_TOUCH_HEIGHT ? VITA_TOUCH_WIDTH : VITA_TOUCH_HEIGHT;
  config->special_keys.offset =
      clamp_int(config->special_keys.offset, 0, special_extent - 1);
  config->special_keys.size =
      clamp_int(config->special_keys.size, 1,
                special_extent - config->special_keys.offset);
}

bool config_save(const char* filename, PCONFIGURATION config) {
  /*
   * Settings can be edited after initial parsing. Validate again before
   * persisting so an invalid UI or legacy value cannot be used for the next
   * input configuration in this process.
   */
  if (!filename || !config) return false;
  config_sanitize(config);

  char* temporary_name = config_sibling_name(filename, ".tmp");
  char* backup_name = config_sibling_name(filename, ".bak");
  if (!temporary_name || !backup_name) {
    free(temporary_name);
    free(backup_name);
    return false;
  }

  /* Finish recovery from a save interrupted after live -> backup. */
  if (!config_path_exists(filename) && config_path_exists(backup_name) &&
      config_rename_file(backup_name, filename) != 0) {
    free(temporary_name);
    free(backup_name);
    return false;
  }
  config_remove_file(temporary_name);

  FILE* fd = fopen(temporary_name, "w");
  if (fd == NULL) {
    fprintf(stderr, "Can't open temporary configuration file: %s\n",
            temporary_name);
    free(temporary_name);
    free(backup_name);
    return false;
  }

  write_config_int(fd, "config_version", CURRENT_CONFIG_VERSION);

  if (config->address)
    write_config_string(fd, "address", config->address);


  if (config->mapping)
    write_config_string(fd, "mapping", config->mapping);

  if (config->stream.width != 960)
    write_config_int(fd, "width", config->stream.width);
  if (config->stream.height != 544)
    write_config_int(fd, "height", config->stream.height);
  if (config->stream.fps != 60)
    write_config_int(fd, "fps", config->stream.fps);
  if (config->stream.bitrate != -1)
    write_config_int(fd, "bitrate", config->stream.bitrate);
  if (config->stream.packetSize != 1024)
    write_config_int(fd, "packetsize", config->stream.packetSize);
  if (!config->sops)
    write_config_bool(fd, "sops", config->sops);
  if (config->localaudio)
    write_config_bool(fd, "localaudio", config->localaudio);

  if (config->app && strcmp(config->app, "Steam") != 0)
    write_config_string(fd, "app", config->app);

  /* key_dir is runtime-selected storage state and must not be persisted. */
  write_config_bool(fd, "enable_frame_pacer", config->enable_frame_pacer);
  write_config_bool(fd, "center_region_only", config->center_region_only);
  write_config_bool(fd, "disable_powersave", config->disable_powersave);
  write_config_bool(fd, "jp_layout", config->jp_layout);
  write_config_bool(fd, "show_fps", config->show_fps);
  write_config_int(fd, "performance_overlay_mode", config->performance_overlay_mode);
  write_config_bool(fd, "enable_front_touchzones", config->enable_front_touchzones);

  write_config_int(fd, "mouse_acceleration", config->mouse_acceleration);
  write_config_bool(fd, "enable_ref_frame_invalidation", config->enable_ref_frame_invalidation);
  write_config_int(fd, "enable_remote_stream_optimization", config->stream.streamingRemotely);
  write_config_bool(fd, "enable_vita_vblank_wait", config->enable_vita_vblank_wait);
  write_config_bool(fd, "enable_motion_controls", config->enable_motion_controls);
  write_config_int(fd, "psbutton_mode", config->psbutton_mode);
  write_config_bool(fd, "enable_double_tap_sprint", config->enable_double_tap_sprint);
  write_config_int(fd, "double_tap_sprint_step_time", config->double_tap_sprint_step_time);
  write_config_float(fd, "motion_controls_scalar_x", config->motion_controls_scalar_x);
  write_config_float(fd, "motion_controls_scalar_y", config->motion_controls_scalar_y);
  write_config_int(fd, "keyboard_layout", config->keyboard_layout);
  write_config_int(fd, "touchscreen_mode", config->touchscreen_mode);
  write_config_bool(fd, "swap_shoulder_buttons", config->swap_shoulder_buttons); // Guardar swap_shoulder_buttons en la raíz
  write_config_int(fd, "controller_type", config->controller_type); // Guardar controller_type en la raíz
  

  write_config_section(fd, "backtouchscreen_deadzone");
  write_config_int(fd, "top",     config->back_deadzone.top);
  write_config_int(fd, "right",   config->back_deadzone.right);
  write_config_int(fd, "bottom",  config->back_deadzone.bottom);
  write_config_int(fd, "left",    config->back_deadzone.left);

  write_config_section(fd, "special_keys");
  write_config_hex(fd, "nw",      config->special_keys.nw);
  write_config_hex(fd, "ne",      config->special_keys.ne);
  write_config_hex(fd, "sw",      config->special_keys.sw);
  write_config_hex(fd, "se",      config->special_keys.se);
  write_config_int(fd, "offset",  config->special_keys.offset);
  write_config_int(fd, "size",    config->special_keys.size);
  fprintf(fd, "%s\n", CONFIG_COMPLETION_MARKER);

  bool succeeded = ferror(fd) == 0;
  if (fflush(fd) != 0) succeeded = false;
  if (fclose(fd) != 0) succeeded = false;
  if (!succeeded) {
    config_remove_file(temporary_name);
    free(temporary_name);
    free(backup_name);
    return false;
  }

  bool had_live_file = config_path_exists(filename);
  if (had_live_file) {
    if (config_path_exists(backup_name) &&
        config_remove_file(backup_name) != 0) {
      config_remove_file(temporary_name);
      free(temporary_name);
      free(backup_name);
      return false;
    }
    if (config_rename_file(filename, backup_name) != 0) {
      config_remove_file(temporary_name);
      free(temporary_name);
      free(backup_name);
      return false;
    }
  }

  if (config_rename_file(temporary_name, filename) != 0) {
    if (had_live_file && config_rename_file(backup_name, filename) == 0) {
      config_remove_file(temporary_name);
    } else if (!had_live_file) {
      config_remove_file(temporary_name);
    }
    free(temporary_name);
    free(backup_name);
    return false;
  }

  if (had_live_file) config_remove_file(backup_name);
  free(temporary_name);
  free(backup_name);
  return true;
}

void update_layout() {
  if (config.jp_layout) {
    config.btn_confirm = SCE_CTRL_CIRCLE;
    config.btn_cancel = SCE_CTRL_CROSS;
  }
  else {
    config.btn_confirm = SCE_CTRL_CROSS;
    config.btn_cancel = SCE_CTRL_CIRCLE;
  }
}

bool config_parse(int argc, char* argv[], PCONFIGURATION config) {
  LiInitializeStreamConfiguration(&config->stream);

  config->config_version = 0;
  config->stream.width = DEFAULT_STREAM_WIDTH;
  config->stream.height = DEFAULT_STREAM_HEIGHT;
  config->stream.fps = DEFAULT_STREAM_FPS;
  config->stream.bitrate = -1;
  config->stream.packetSize = DEFAULT_PACKET_SIZE;
  config->stream.streamingRemotely = STREAM_CFG_AUTO;
  config->stream.audioConfiguration = AUDIO_CONFIGURATION_STEREO;
  config->stream.supportedVideoFormats = VIDEO_FORMAT_H264;
  config->stream.clientRefreshRateX100 = 6000;
  config->stream.colorSpace = COLORSPACE_REC_709;
  config->stream.colorRange = COLOR_RANGE_LIMITED;

  config->platform = "vita";
  config->model = sceKernelGetModelForCDialog();
  config->app = "Steam";
  config->action = NULL;
  config->address = NULL;
  config->config_file = NULL;
  config->sops = true;
  config->localaudio = false;
  config->fullscreen = true;
  config->unsupported_version = false;
  config->save_debug_log = false;
  config->disable_powersave = true;
  config->jp_layout = false;
  config->show_fps = false;
  config->performance_overlay_mode = 0;
  config->enable_frame_pacer = false;
  config->center_region_only = false;

  config->enable_front_touchzones = false;
  config->special_keys.nw = INPUT_SPECIAL_KEY_PAUSE | INPUT_TYPE_SPECIAL;
  config->special_keys.sw = SPECIAL_FLAG | INPUT_TYPE_GAMEPAD;
  config->special_keys.offset = 0;
  config->special_keys.size = 150;

  config->mouse_acceleration = 150;
  config->enable_ref_frame_invalidation = false;
  config->enable_vita_vblank_wait = false;
  config->enable_motion_controls = false;
  config->psbutton_mode = PSBUTTON_MODE_LOCAL_ESCAPE;
  config->enable_double_tap_sprint = false;
  config->touchscreen_mode = 0;
  config->controller_type = 1;
  config->keyboard_layout = 0;

  config->double_tap_sprint_step_time = 200;

  config->motion_controls_scalar_x = 1.2;
  config->motion_controls_scalar_y = 0.8;

  config->inputsCount = 0;
  config->mapping = NULL;
  // No sobrescribir key_dir si ya fue asignado por main.c
  // config->key_dir[0] = 0;
  // Xbox/XInput is the compatibility-first controller default. Explicit saved
  // selections are loaded below and preserved.

  char* config_file = config_path;
  if (config_file) {
    bool had_saved_state = config_saved_state_exists(config_file);
    if (!config_file_parse(config_file, config) && had_saved_state) {
      fprintf(stderr, "Saved configuration is invalid and has no valid recovery file: %s\n",
              config_file);
      return false;
    }
  }

  // Preserve the old FPS-counter preference without drawing both overlays.
  if (config->show_fps && config->performance_overlay_mode == 0) {
    config->performance_overlay_mode = 1;
    config->show_fps = false;
  }

  config_sanitize(config);

  if (config->config_version < CURRENT_CONFIG_VERSION) {
    // Version 4 exposed network routing as a boolean. Migrate configurations
    // matching the old built-in profiles to the safer automatic route without
    // changing genuinely custom or explicit remote configurations.
    if (config->config_version < 5 &&
        config->stream.streamingRemotely == STREAM_CFG_LOCAL &&
        stream_preset_base_matches() &&
        ((config->stream.fps == 30 && config->stream.bitrate == 5000) ||
         (config->stream.fps == 60 && config->stream.bitrate == 8000) ||
         (config->stream.fps == 60 && config->stream.bitrate == 12000))) {
      config->stream.streamingRemotely = STREAM_CFG_AUTO;
    }
    if (config->config_version < 5 &&
        config->controller_type == 1 &&
        config->touchscreen_mode == 0 &&
        config->psbutton_mode == PSBUTTON_MODE_LOCAL_ESCAPE &&
        !config->swap_shoulder_buttons &&
        config->mapping == NULL &&
        !config->enable_double_tap_sprint) {
      config->enable_motion_controls = false;
    }
    if (config->config_version < 2 &&
        config->stream.width <= 960 && config->stream.height <= 544 &&
        config->stream.bitrate > 0 && config->stream.bitrate <= 5000) {
      config->stream.bitrate = 8000;
    }
    config->config_version = CURRENT_CONFIG_VERSION;
    if (config_file) {
      if (!config_save(config_file, config)) {
        fprintf(stderr, "Could not persist migrated configuration: %s\n",
                config_file);
        return false;
      }
    }
  }

  update_layout();

  if (config->config_file != NULL &&
      !config_save(config->config_file, config)) {
    fprintf(stderr, "Could not persist configuration: %s\n",
            config->config_file);
    return false;
  }

  // Solo asignar valor por defecto si sigue vacío
  if (config->key_dir[0] == 0x0) {
    const char *xdg_cache_dir = getenv("XDG_CACHE_DIR");
    if (xdg_cache_dir && xdg_cache_dir[0] != '\0') {
      snprintf(config->key_dir, sizeof(config->key_dir), "%s" MOONLIGHT_PATH, xdg_cache_dir);
    } else {
      const char *home_dir = getenv("HOME");
      if (home_dir && home_dir[0] != '\0') {
        snprintf(config->key_dir, sizeof(config->key_dir), "%s" DEFAULT_CACHE_DIR MOONLIGHT_PATH, home_dir);
      } else {
        snprintf(config->key_dir, sizeof(config->key_dir), "%s" DEFAULT_CACHE_DIR MOONLIGHT_PATH, "");
      }
    }
  }

  if (config->stream.fps == -1)
    config->stream.fps = config->stream.height >= 1080 ? 30 : 60;

  if (config->stream.bitrate == -1)
    config->stream.bitrate = config_recommended_bitrate(config->stream.width, config->stream.height, config->stream.fps);

  if (inputAdded) {
    if (!mapped) {
        fprintf(stderr, "Mapping option should be followed by the input to be mapped.\n");
        return false;
    } else if (config->mapping == NULL) {
        fprintf(stderr, "Please specify mapping file as default mapping could not be found.\n");
        return false;
    }
  }
  return true;
}
