/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2017 Sunguk Lee
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

#include <ctype.h>
#include <stdarg.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/rtc.h>
#include "debug.h"
#include "config.h"
#include "configuration.h"
#include "connection.h"
#include "gui/ui_diagnostics.h"

#define LOG_FLUSH_INTERVAL_US 1000000ULL
#define LEGACY_REPEAT_INTERVAL_US 10000000ULL
#define LEGACY_RATE_SLOT_COUNT 8
#define SUPPORT_LOG_SCHEMA "vita-support-v1"

pthread_mutex_t print_mutex;
static char log_buffer[8192];
static char fields_buffer[4096];
static uint32_t logging_enabled = 0;
static uint64_t last_log_flush_us = 0;
static uint64_t session_id = 0;
static uint64_t session_started_us = 0;
static uint64_t record_sequence = 0;
static uint32_t warning_count = 0;
static uint32_t error_count = 0;
static uint32_t legacy_suppressed = 0;

typedef struct LegacyRateSlot {
  uint32_t hash;
  uint64_t last_written_us;
  uint32_t repetitions;
} LegacyRateSlot;

static LegacyRateSlot legacy_rate_slots[LEGACY_RATE_SLOT_COUNT];

static bool logging_enabled_load(void) {
  return __atomic_load_n(&logging_enabled, __ATOMIC_ACQUIRE) != 0;
}

static void logging_enabled_store(bool enabled) {
  __atomic_store_n(&logging_enabled, enabled ? 1U : 0U, __ATOMIC_RELEASE);
}

static bool build_named_log_path(char *path, size_t path_size,
                                 const char *filename) {
  if (!path || path_size == 0 || config.key_dir[0] == '\0') {
    return false;
  }
  size_t key_dir_length = strlen(config.key_dir);
  int written = snprintf(path, path_size,
                         key_dir_length > 0 &&
                                 config.key_dir[key_dir_length - 1] == '/'
                             ? "%s%s"
                             : "%s/%s",
                         config.key_dir, filename);
  return written > 0 && (size_t)written < path_size;
}

static bool build_log_path(char *path, size_t path_size) {
  return build_named_log_path(path, path_size, "moonlight.log");
}

static bool open_log_locked(const char *mode) {
  if (config.log_file) return true;

  char log_path[4096];
  if (!build_log_path(log_path, sizeof(log_path))) return false;

  config.log_file = fopen(log_path, mode);
  if (config.log_file) {
    last_log_flush_us = sceKernelGetSystemTimeWide();
  }
  return config.log_file != NULL;
}

static void close_log_locked(void) {
  if (!config.log_file) return;
  fflush(config.log_file);
  fclose(config.log_file);
  config.log_file = NULL;
  last_log_flush_us = 0;
}

static void rotate_log_locked(void) {
  char current_path[4096];
  char previous_path[4096];
  if (!build_log_path(current_path, sizeof(current_path)) ||
      !build_named_log_path(previous_path, sizeof(previous_path),
                            "moonlight.previous.log")) {
    return;
  }

  /*
   * A support capture is intentionally isolated. Keep at most one prior
   * capture for recovery, then start a new current log.
   */
  FILE *current = fopen(current_path, "r");
  if (!current) return;
  fclose(current);
  remove(previous_path);
  rename(current_path, previous_path);
}

static const char *level_name(VitaDebugLevel level) {
  switch (level) {
    case VITA_DEBUG_LEVEL_WARNING:
      return "warn";
    case VITA_DEBUG_LEVEL_ERROR:
      return "error";
    default:
      return "info";
  }
}

static void safe_token(char *output, size_t output_size, const char *input) {
  if (!output || output_size == 0) return;
  size_t written = 0;
  if (!input) input = "unknown";
  while (*input && written + 1 < output_size) {
    unsigned char ch = (unsigned char)*input++;
    output[written++] =
        (isalnum(ch) || ch == '.' || ch == '_' || ch == '-')
            ? (char)ch
            : '_';
  }
  if (written == 0 && output_size > 1) {
    memcpy(output, "unknown", output_size > 8 ? 8 : output_size);
    output[output_size > 8 ? 7 : output_size - 1] = '\0';
    return;
  }
  output[written] = '\0';
}

static void sanitize_fields(char *fields) {
  if (!fields) return;
  for (; *fields; fields++) {
    unsigned char ch = (unsigned char)*fields;
    if (ch == '\r' || ch == '\n' || ch == '\t' ||
        ch == '"' || ch == '\\' || !isprint(ch)) {
      *fields = '_';
    }
  }
}

static bool write_record_locked(VitaDebugLevel level, const char *event,
                                const char *fields) {
  char event_token[96];
  safe_token(event_token, sizeof(event_token), event);

  SceDateTime time;
  sceRtcGetCurrentClock(&time, 0);
  uint64_t next_sequence = record_sequence + 1;
  int length = snprintf(
      log_buffer, sizeof(log_buffer),
      "ts=%04d-%02d-%02dT%02d:%02d:%02d.%06dZ "
      "schema=%s session=%016llx seq=%llu level=%s event=%s%s%s\n",
      time.year, time.month, time.day, time.hour, time.minute, time.second,
      (int)time.microsecond, SUPPORT_LOG_SCHEMA,
      (unsigned long long)session_id,
      (unsigned long long)next_sequence, level_name(level), event_token,
      fields && fields[0] ? " " : "", fields && fields[0] ? fields : "");
  if (length <= 0) return false;
  if ((size_t)length >= sizeof(log_buffer)) {
    length = (int)sizeof(log_buffer) - 2;
    log_buffer[length++] = '\n';
    log_buffer[length] = '\0';
  }

  if (!open_log_locked("a")) {
    printf("[Moonlight] Could not open the support log.\n");
    return false;
  }

  size_t written = fwrite(log_buffer, 1, (size_t)length, config.log_file);
  if (written != (size_t)length) return false;

  record_sequence = next_sequence;
  if (level == VITA_DEBUG_LEVEL_WARNING) warning_count++;
  if (level == VITA_DEBUG_LEVEL_ERROR) error_count++;

  uint64_t now_us = sceKernelGetSystemTimeWide();
  if (level != VITA_DEBUG_LEVEL_INFO ||
      last_log_flush_us == 0 ||
      now_us - last_log_flush_us >= LOG_FLUSH_INTERVAL_US) {
    fflush(config.log_file);
    last_log_flush_us = now_us;
  }
  return true;
}

static bool starts_with_case_insensitive(const char *text,
                                         const char *prefix) {
  if (!text || !prefix) return false;
  while (*prefix) {
    if (!*text ||
        tolower((unsigned char)*text) != tolower((unsigned char)*prefix)) {
      return false;
    }
    text++;
    prefix++;
  }
  return true;
}

static bool contains_case_insensitive(const char *text,
                                      const char *needle) {
  if (!text || !needle || !needle[0]) return false;
  for (; *text; text++) {
    if (starts_with_case_insensitive(text, needle)) return true;
  }
  return false;
}

static bool legacy_source_is_suppressed(const char *format) {
  static const char *const blocked[] = {
      "[ui]", "[ui_", "[scan]", "[mdns]", "[pair]", "[wol]", "[ime",
      "load_device", "save_device", "append_device", "remove_device",
      "folder created", "carpeta seleccionada", "key_dir",
      "shortcut:", "[shortcut]", "condition ", "sprinting",
      "[ds4_touchpad]", "[touchscreen]", "[abs_mouse]",
      "remain frameskip", "overlay activo", "teclado virtual"};
  for (size_t i = 0; i < sizeof(blocked) / sizeof(blocked[0]); i++) {
    if (contains_case_insensitive(format, blocked[i])) return true;
  }
  return false;
}

static bool legacy_message_is_error(const char *format) {
  static const char *const markers[] = {
      "error", "fail", "cannot", "could not", "not enough",
      "out of memory", "invalid", "timeout", "timed out",
      "unexpected", "corrupt", "quarantin", "denied", "unable"};
  for (size_t i = 0; i < sizeof(markers) / sizeof(markers[0]); i++) {
    if (contains_case_insensitive(format, markers[i])) return true;
  }
  return false;
}

static uint32_t message_hash(const char *message) {
  uint32_t hash = 2166136261U;
  while (message && *message) {
    hash ^= (unsigned char)*message++;
    hash *= 16777619U;
  }
  return hash ? hash : 1U;
}

static const char *legacy_error_category(const char *format) {
  if (contains_case_insensitive(format, "opus") ||
      contains_case_insensitive(format, "audio")) {
    return "audio";
  }
  if (contains_case_insensitive(format, "motion") ||
      contains_case_insensitive(format, "gyro")) {
    return "motion";
  }
  if (contains_case_insensitive(format, "decoder") ||
      contains_case_insensitive(format, "avc") ||
      contains_case_insensitive(format, "video") ||
      contains_case_insensitive(format, "frame") ||
      contains_case_insensitive(format, "texture")) {
    return "decoder";
  }
  if (contains_case_insensitive(format, "timeout") ||
      contains_case_insensitive(format, "timed out")) {
    return "timeout";
  }
  if (contains_case_insensitive(format, "socket") ||
      contains_case_insensitive(format, "network") ||
      contains_case_insensitive(format, "connection") ||
      contains_case_insensitive(format, "rtsp") ||
      contains_case_insensitive(format, "udp") ||
      contains_case_insensitive(format, "tcp")) {
    return "transport";
  }
  if (contains_case_insensitive(format, "memory") ||
      contains_case_insensitive(format, "alloc")) {
    return "memory";
  }
  return "internal";
}

static bool legacy_rate_limit_locked(const char *message,
                                     uint32_t *repetitions) {
  uint32_t hash = message_hash(message);
  LegacyRateSlot *slot = NULL;
  LegacyRateSlot *oldest = &legacy_rate_slots[0];
  for (size_t i = 0; i < LEGACY_RATE_SLOT_COUNT; i++) {
    if (legacy_rate_slots[i].hash == hash) {
      slot = &legacy_rate_slots[i];
      break;
    }
    if (legacy_rate_slots[i].hash == 0) {
      slot = &legacy_rate_slots[i];
      break;
    }
    if (legacy_rate_slots[i].last_written_us <
        oldest->last_written_us) {
      oldest = &legacy_rate_slots[i];
    }
  }
  if (!slot) slot = oldest;

  uint64_t now_us = sceKernelGetSystemTimeWide();
  if (slot->hash == hash &&
      now_us - slot->last_written_us < LEGACY_REPEAT_INTERVAL_US) {
    slot->repetitions++;
    legacy_suppressed++;
    return true;
  }
  *repetitions = slot->hash == hash ? slot->repetitions : 0;
  slot->hash = hash;
  slot->last_written_us = now_us;
  slot->repetitions = 0;
  return false;
}

static const char *storage_mount_name(void) {
  static const char *const mounts[] = {
      "ux0", "ur0", "uma0", "imc0", "xmc0", "other"};
  for (size_t i = 0; i + 1 < sizeof(mounts) / sizeof(mounts[0]); i++) {
    size_t length = strlen(mounts[i]);
    if (starts_with_case_insensitive(config.key_dir, mounts[i]) &&
        config.key_dir[length] == ':') {
      return mounts[i];
    }
  }
  return mounts[sizeof(mounts) / sizeof(mounts[0]) - 1];
}

static const char *connection_state_name(int state) {
  switch (state) {
    case LI_READY:
      return "ready";
    case LI_PAIRED:
      return "paired";
    case LI_CONNECTED:
      return "connected";
    case LI_MINIMIZED:
      return "minimized";
    default:
      return "disconnected";
  }
}

static const char *network_mode_name(int mode) {
  switch (mode) {
    case 0:
      return "local";
    case 1:
      return "remote";
    default:
      return "auto";
  }
}

static const char *controller_name(int type) {
  return type == 2 ? "dualshock4" : "xbox";
}

static const char *touch_mode_name(int mode) {
  switch (mode) {
    case 1:
      return "ds4_touchpad";
    case 2:
      return "absolute_mouse";
    case 3:
      return "tablet";
    default:
      return "relative_mouse";
  }
}

static const char *ps_button_mode_name(int mode) {
  switch (mode) {
    case PSBUTTON_MODE_SAFE_GUIDE:
      return "safe_guide";
    case PSBUTTON_MODE_IMMEDIATE_GUIDE:
      return "immediate_guide";
    case PSBUTTON_MODE_SYSTEM:
      return "system_livearea";
    default:
      return "local_double_tap";
  }
}

static const char *keyboard_layout_name(int layout) {
  switch (layout) {
    case 1:
      return "es_es";
    case 2:
      return "es_latam";
    default:
      return "en_us";
  }
}

static const char *performance_overlay_name(int mode) {
  switch (mode) {
    case 1:
      return "framerate";
    case 2:
      return "framerate_network";
    case 3:
      return "advanced";
    default:
      return "off";
  }
}

static bool write_system_snapshot_locked(void) {
  char build_token[64];
  safe_token(build_token, sizeof(build_token), COMPILE_OPTIONS);
  snprintf(
      fields_buffer, sizeof(fields_buffer),
      "app_version=%d.%d.%d build_id=%s build_profile=%s platform=vita model_id=%u "
      "storage_mount=%s decoder_backend=vita_hw_h264 identifiers=redacted",
      VERSION_MAJOR, VERSION_MINOR, VERSION_PATCH, VITA_BUILD_ID, build_token,
      (unsigned int)config.model, storage_mount_name());
  return write_record_locked(
      VITA_DEBUG_LEVEL_INFO, "system.snapshot", fields_buffer);
}

static bool write_config_snapshot_locked(const char *reason) {
  char reason_token[64];
  safe_token(reason_token, sizeof(reason_token), reason);
  snprintf(
      fields_buffer, sizeof(fields_buffer),
      "reason=%s width=%d height=%d fps=%d bitrate_kbps=%d packet_size=%d "
      "network_mode=%s video_formats=0x%x audio_config=0x%x sops=%d "
      "ref_invalidation=%d frame_pacer=%d vblank_wait=%d scaling=%s "
      "local_audio=%d controller=%s motion=%d touch_mode=%s "
      "gyro_horizontal=%.2f gyro_vertical=%.2f ps_mode=%s "
      "shoulder_swap=%d sprint=%d sprint_window_ms=%u "
      "front_touchzones=%d mouse_accel=%d keyboard_layout=%s "
      "mapping_enabled=%d overlay=%s power_save_disabled=%d",
      reason_token, config.stream.width, config.stream.height,
      config.stream.fps, config.stream.bitrate, config.stream.packetSize,
      network_mode_name(config.stream.streamingRemotely),
      (unsigned int)config.stream.supportedVideoFormats,
      (unsigned int)config.stream.audioConfiguration,
      config.sops ? 1 : 0, config.enable_ref_frame_invalidation ? 1 : 0,
      config.enable_frame_pacer ? 1 : 0,
      config.enable_vita_vblank_wait ? 1 : 0,
      config.center_region_only ? "crop_fill" : "fit",
      config.localaudio ? 1 : 0, controller_name(config.controller_type),
      config.enable_motion_controls ? 1 : 0,
      touch_mode_name(config.touchscreen_mode),
      config.motion_controls_scalar_x, config.motion_controls_scalar_y,
      ps_button_mode_name(config.psbutton_mode),
      config.swap_shoulder_buttons ? 1 : 0,
      config.enable_double_tap_sprint ? 1 : 0,
      (unsigned int)config.double_tap_sprint_step_time,
      config.enable_front_touchzones ? 1 : 0, config.mouse_acceleration,
      keyboard_layout_name(config.keyboard_layout),
      config.mapping && config.mapping[0] ? 1 : 0,
      performance_overlay_name(config.performance_overlay_mode),
      config.disable_powersave ? 1 : 0);
  return write_record_locked(
      VITA_DEBUG_LEVEL_INFO, "config.snapshot", fields_buffer);
}

static bool write_connection_snapshot_locked(void) {
  int state = connection_get_status();
  snprintf(fields_buffer, sizeof(fields_buffer),
           "state=%s state_id=%d stage_id=%d connected=%d",
           connection_state_name(state), state, connection_stage,
           state == LI_CONNECTED ? 1 : 0);
  return write_record_locked(
      VITA_DEBUG_LEVEL_INFO, "connection.snapshot", fields_buffer);
}

static void reset_session_locked(void) {
  SceDateTime time;
  sceRtcGetCurrentClock(&time, 0);
  session_started_us = sceKernelGetSystemTimeWide();
  session_id = session_started_us ^
      ((uint64_t)time.year << 48) ^ ((uint64_t)time.month << 40) ^
      ((uint64_t)time.day << 32) ^ ((uint64_t)time.hour << 24) ^
      ((uint64_t)time.minute << 16) ^ ((uint64_t)time.second << 8);
  if (session_id == 0) session_id = 1;
  record_sequence = 0;
  warning_count = 0;
  error_count = 0;
  legacy_suppressed = 0;
  memset(legacy_rate_slots, 0, sizeof(legacy_rate_slots));
}

bool vita_debug_init() {
  config.log_file = NULL;
  last_log_flush_us = 0;
  session_id = 0;
  session_started_us = 0;
  record_sequence = 0;
  warning_count = 0;
  error_count = 0;
  legacy_suppressed = 0;
  memset(legacy_rate_slots, 0, sizeof(legacy_rate_slots));
  config.save_debug_log = false;
  logging_enabled_store(false);
  if (pthread_mutex_init(&print_mutex, NULL) != 0) {
    return false;
  }
  return true;
}

void vita_debug_log(const char *s, ...) {
  /* Disabled support capture is intentionally one predictable branch. */
  if (!logging_enabled_load() || !s) return;
  if (legacy_source_is_suppressed(s) || !legacy_message_is_error(s)) return;

  pthread_mutex_lock(&print_mutex);
  if (!logging_enabled_load()) {
    pthread_mutex_unlock(&print_mutex);
    return;
  }

  const char *category = legacy_error_category(s);
  uint32_t repetitions = 0;
  if (!legacy_rate_limit_locked(category, &repetitions)) {
    snprintf(fields_buffer, sizeof(fields_buffer),
             "source=compat category=%s repeats_suppressed=%u",
             category, repetitions);
    write_record_locked(
        VITA_DEBUG_LEVEL_ERROR, "error.legacy", fields_buffer);
  }

  pthread_mutex_unlock(&print_mutex);
}

void vita_debug_event(VitaDebugLevel level, const char *event,
                      const char *fields_format, ...) {
  if (!logging_enabled_load() || !event) return;

  pthread_mutex_lock(&print_mutex);
  if (!logging_enabled_load()) {
    pthread_mutex_unlock(&print_mutex);
    return;
  }

  fields_buffer[0] = '\0';
  if (fields_format && fields_format[0]) {
    va_list va;
    va_start(va, fields_format);
    vsnprintf(
        fields_buffer, sizeof(fields_buffer), fields_format, va);
    va_end(va);
    sanitize_fields(fields_buffer);
  }
  write_record_locked(level, event, fields_buffer);
  pthread_mutex_unlock(&print_mutex);
}

void vita_debug_log_config_snapshot(const char *reason) {
  if (!logging_enabled_load()) return;
  pthread_mutex_lock(&print_mutex);
  if (logging_enabled_load()) {
    write_config_snapshot_locked(reason ? reason : "requested");
  }
  pthread_mutex_unlock(&print_mutex);
}

bool vita_debug_is_logging_enabled(void) {
  return logging_enabled_load();
}

void vita_debug_set_logging_enabled(bool enabled) {
  bool actual_enabled;
  pthread_mutex_lock(&print_mutex);

  /*
   * Support capture is a deliberate, per-session workflow. Never persist an
   * enabled state into the next application launch.
   */
  config.save_debug_log = false;
  bool was_enabled = logging_enabled_load();
  if (enabled && !was_enabled) {
    close_log_locked();
    rotate_log_locked();
    reset_session_locked();
    if (open_log_locked("w")) {
      logging_enabled_store(true);
      bool capture_started = write_record_locked(
          VITA_DEBUG_LEVEL_INFO, "session.start",
          "reason=user capture=fresh privacy=identifiers_redacted") &&
          write_system_snapshot_locked() &&
          write_config_snapshot_locked("capture_start") &&
          write_connection_snapshot_locked();
      if (capture_started) {
        fflush(config.log_file);
        last_log_flush_us = sceKernelGetSystemTimeWide();
      } else {
        logging_enabled_store(false);
        close_log_locked();
      }
    } else {
      logging_enabled_store(false);
    }
  } else if (!enabled && was_enabled) {
    uint64_t now_us = sceKernelGetSystemTimeWide();
    uint64_t duration_ms = now_us >= session_started_us
        ? (now_us - session_started_us) / 1000ULL
        : 0;
    snprintf(
        fields_buffer, sizeof(fields_buffer),
        "reason=user duration_ms=%llu records=%llu warnings=%u errors=%u "
        "legacy_suppressed=%u",
        (unsigned long long)duration_ms,
        (unsigned long long)(record_sequence + 1),
        warning_count, error_count,
        __atomic_load_n(&legacy_suppressed, __ATOMIC_RELAXED));
    write_record_locked(
        VITA_DEBUG_LEVEL_INFO, "session.end", fields_buffer);
    logging_enabled_store(false);
    close_log_locked();
  }
  actual_enabled = logging_enabled_load();
  pthread_mutex_unlock(&print_mutex);

  /*
   * Diagnostics may take its own metrics mutex. Notify only after releasing
   * print_mutex because the sampling path logs while holding metrics_mutex.
   */
  if (actual_enabled != was_enabled) {
    ui_diagnostics_set_logging_consumer(actual_enabled);
  }
}

void vita_debug_flush(void) {
  pthread_mutex_lock(&print_mutex);
  if (config.log_file) {
    fflush(config.log_file);
    last_log_flush_us = sceKernelGetSystemTimeWide();
  }
  pthread_mutex_unlock(&print_mutex);
}

void vita_debug_shutdown(void) {
  pthread_mutex_lock(&print_mutex);
  config.save_debug_log = false;
  if (logging_enabled_load()) {
    uint64_t now_us = sceKernelGetSystemTimeWide();
    uint64_t duration_ms = now_us >= session_started_us
        ? (now_us - session_started_us) / 1000ULL
        : 0;
    snprintf(
        fields_buffer, sizeof(fields_buffer),
        "reason=shutdown duration_ms=%llu records=%llu warnings=%u errors=%u "
        "legacy_suppressed=%u",
        (unsigned long long)duration_ms,
        (unsigned long long)(record_sequence + 1),
        warning_count, error_count,
        __atomic_load_n(&legacy_suppressed, __ATOMIC_RELAXED));
    write_record_locked(
        VITA_DEBUG_LEVEL_INFO, "session.end", fields_buffer);
  }
  logging_enabled_store(false);
  close_log_locked();
  pthread_mutex_unlock(&print_mutex);
  ui_diagnostics_set_logging_consumer(false);
}

bool vita_debug_get_log_path(char *path, size_t path_size) {
  return build_log_path(path, path_size);
}

