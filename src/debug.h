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
#ifndef VITA_MOONLIGHT_DEBUG_H
#define VITA_MOONLIGHT_DEBUG_H

#include <pthread.h>
#include <stdbool.h>
#include <stddef.h>

typedef enum VitaDebugLevel {
  VITA_DEBUG_LEVEL_INFO = 0,
  VITA_DEBUG_LEVEL_WARNING = 1,
  VITA_DEBUG_LEVEL_ERROR = 2
} VitaDebugLevel;

/*
 * Compatibility sink for existing callers and Limelight. While a support
 * capture is active, only error-like messages are retained. Repetitive and
 * privacy-sensitive legacy sources are suppressed; remaining errors are
 * recorded only as coarse categories, never as arbitrary message text.
 */
void vita_debug_log(const char *s, ...);

/*
 * Structured support-log record. Event names and fields must be stable,
 * privacy-safe key=value tokens. This is a cheap no-op when capture is off.
 */
void vita_debug_event(VitaDebugLevel level, const char *event,
                      const char *fields_format, ...);
void vita_debug_log_config_snapshot(const char *reason);

bool vita_debug_init();
bool vita_debug_is_logging_enabled(void);
void vita_debug_set_logging_enabled(bool enabled);
void vita_debug_flush(void);
void vita_debug_shutdown(void);
bool vita_debug_get_log_path(char *path, size_t path_size);

#endif
