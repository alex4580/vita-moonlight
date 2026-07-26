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

#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <stdarg.h>
#include <string.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/rtc.h>
#include "debug.h"
#include "config.h"
#include "gui/ui_diagnostics.h"

#define LOG_FLUSH_INTERVAL_US 1000000ULL

pthread_mutex_t print_mutex;
static char log_buffer[8192];
static uint32_t logging_enabled = 0;
static uint64_t last_log_flush_us = 0;

static bool logging_enabled_load(void) {
  return __atomic_load_n(&logging_enabled, __ATOMIC_ACQUIRE) != 0;
}

static void logging_enabled_store(bool enabled) {
  __atomic_store_n(&logging_enabled, enabled ? 1U : 0U, __ATOMIC_RELEASE);
}

static bool build_log_path(char *path, size_t path_size) {
  if (!path || path_size == 0 || config.key_dir[0] == '\0') {
    return false;
  }
  size_t key_dir_length = strlen(config.key_dir);
  int written = snprintf(path, path_size,
                         key_dir_length > 0 &&
                                 config.key_dir[key_dir_length - 1] == '/'
                             ? "%smoonlight.log"
                             : "%s/moonlight.log",
                         config.key_dir);
  return written > 0 && (size_t)written < path_size;
}

static bool open_log_locked(void) {
  if (config.log_file) return true;

  char log_path[4096];
  if (!build_log_path(log_path, sizeof(log_path))) return false;

  config.log_file = fopen(log_path, "a");
  if (config.log_file) {
    last_log_flush_us = sceKernelGetSystemTimeWide();
  }
  return config.log_file != NULL;
}

bool vita_debug_init() {
  config.log_file = NULL;
  last_log_flush_us = 0;
  logging_enabled_store(false);
  if (pthread_mutex_init(&print_mutex, NULL) != 0) {
    return false;
  }
  return true;
}

void vita_debug_log(const char *s, ...) {
  // Logging disabled is intentionally just one predictable branch.
  if (!logging_enabled_load()) {
    return;
  }

  pthread_mutex_lock(&print_mutex);
  if (!logging_enabled_load()) {
    pthread_mutex_unlock(&print_mutex);
    return;
  }

  SceDateTime time;
  sceRtcGetCurrentClock(&time, 0);

  int prefix_len = snprintf(log_buffer, sizeof(log_buffer),
                            "%04d%02d%02d %02d:%02d:%02d.%06d ",
                            time.year, time.month, time.day,
                            time.hour, time.minute, time.second,
                            time.microsecond);
  if (prefix_len < 0 || (size_t)prefix_len >= sizeof(log_buffer)) {
    pthread_mutex_unlock(&print_mutex);
    return;
  }

  va_list va;
  va_start(va, s);
  vsnprintf(
      log_buffer + prefix_len,
      sizeof(log_buffer) - (size_t)prefix_len,
      s,
      va);
  va_end(va);

  if (open_log_locked()) {
    size_t length = strlen(log_buffer);
    fwrite(log_buffer, 1, length, config.log_file);
    if (length == 0 || log_buffer[length - 1] != '\n') {
      fputc('\n', config.log_file);
    }
    uint64_t now_us = sceKernelGetSystemTimeWide();
    if (last_log_flush_us == 0 ||
        now_us - last_log_flush_us >= LOG_FLUSH_INTERVAL_US) {
      fflush(config.log_file);
      last_log_flush_us = now_us;
    }
  } else {
    printf("[Moonlight] Could not open the diagnostic log. Message: %s\n",
           log_buffer + prefix_len);
  }

  pthread_mutex_unlock(&print_mutex);
}

bool vita_debug_is_logging_enabled(void) {
  return logging_enabled_load();
}

void vita_debug_set_logging_enabled(bool enabled) {
  pthread_mutex_lock(&print_mutex);
  config.save_debug_log = enabled;
  logging_enabled_store(enabled);
  if (!enabled && config.log_file) {
    fflush(config.log_file);
    fclose(config.log_file);
    config.log_file = NULL;
    last_log_flush_us = 0;
  }
  pthread_mutex_unlock(&print_mutex);

  /*
   * Diagnostics may take its own metrics mutex. Notify only after releasing
   * print_mutex because the sampling path logs while holding metrics_mutex.
   */
  ui_diagnostics_set_logging_consumer(enabled);
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
  logging_enabled_store(false);
  if (config.log_file) {
    fflush(config.log_file);
    fclose(config.log_file);
    config.log_file = NULL;
    last_log_flush_us = 0;
  }
  pthread_mutex_unlock(&print_mutex);
  ui_diagnostics_set_logging_consumer(false);
}

bool vita_debug_get_log_path(char *path, size_t path_size) {
  return build_log_path(path, path_size);
}

