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
#include <pthread.h>
#include <stdbool.h>
#include <stddef.h>

void vita_debug_log(const char *s, ...);

bool vita_debug_init();
bool vita_debug_is_logging_enabled(void);
void vita_debug_set_logging_enabled(bool enabled);
void vita_debug_flush(void);
void vita_debug_shutdown(void);
bool vita_debug_get_log_path(char *path, size_t path_size);
