/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015 Iwan Timmer
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

#include "mapping.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef __vita__
#include <psp2/io/fcntl.h>
#endif

#define write_config(fd, key, value) \
  fprintf((fd), "%s = %x\n", (key), (unsigned int)(value))
#define write_config_bool(fd, key, value) \
  fprintf((fd), "%s = %s\n", (key), (value) ? "true" : "false")
#define MAPPING_SERIALIZED_FIELD_COUNT 32

static char* mapping_sibling_name(const char* fileName,
                                  const char* suffix) {
  size_t file_name_length;
  size_t suffix_length;
  char* sibling;

  if (!fileName || !suffix) {
    return NULL;
  }
  file_name_length = strlen(fileName);
  suffix_length = strlen(suffix);
  if (file_name_length > SIZE_MAX - suffix_length - 1) {
    return NULL;
  }

  sibling = malloc(file_name_length + suffix_length + 1);
  if (!sibling) {
    return NULL;
  }
  memcpy(sibling, fileName, file_name_length);
  memcpy(
      sibling + file_name_length, suffix, suffix_length + 1);
  return sibling;
}

static bool mapping_path_exists(const char* path) {
  FILE* file = fopen(path, "r");
  if (!file) {
    return false;
  }
  fclose(file);
  return true;
}

static int mapping_rename_file(const char* old_path,
                               const char* new_path) {
#ifdef __vita__
  /*
   * VitaSDK's C rename() removes an existing destination before calling
   * sceIoRename(). Use the kernel operation directly so journal promotion
   * never deletes the destination implicitly.
   */
  return sceIoRename(old_path, new_path);
#else
  return rename(old_path, new_path);
#endif
}

static int mapping_remove_file(const char* path) {
#ifdef __vita__
  return sceIoRemove(path);
#else
  return remove(path);
#endif
}

static bool mapping_temporary_file_complete(const char* path) {
  static const char completion_marker[] = "# mapping_complete";
  char line[64];
  bool complete = false;
  size_t assignment_count = 0;
  FILE* file = fopen(path, "r");

  if (!file) {
    return false;
  }
  while (fgets(line, sizeof(line), file)) {
    if (strstr(line, " = ")) {
      assignment_count++;
    }
    if (strncmp(
            line, completion_marker,
            sizeof(completion_marker) - 1) == 0) {
      complete = true;
    }
  }
  if (fclose(file) != 0) {
    complete = false;
  }
  return complete &&
      assignment_count == MAPPING_SERIALIZED_FIELD_COUNT;
}

bool mapping_load(const char* fileName, struct mapping* map) {
  char* temporary_name;
  char* backup_name;
  const char* load_path;
  bool load_path_is_main = true;

  if (!fileName || !map) {
    return false;
  }

  temporary_name = mapping_sibling_name(fileName, ".tmp");
  backup_name = mapping_sibling_name(fileName, ".bak");
  load_path = fileName;

  if (!mapping_path_exists(fileName)) {
    if (backup_name && mapping_path_exists(backup_name)) {
      if (mapping_rename_file(backup_name, fileName) != 0) {
        load_path = backup_name;
        load_path_is_main = false;
      }
    } else if (temporary_name &&
               mapping_path_exists(temporary_name) &&
               mapping_temporary_file_complete(temporary_name)) {
      if (mapping_rename_file(temporary_name, fileName) != 0) {
        load_path = temporary_name;
        load_path_is_main = false;
      }
    }
  }

  FILE* fd = fopen(load_path, "r");
  if (fd == NULL) {
    printf("Can't open mapping file: %s\n", fileName);
    free(temporary_name);
    free(backup_name);
    return false;
  }

  char *line = NULL;
  size_t len = 0;
  while (__getline(&line, &len, fd) != -1) {
    char key[256], value[256];
    if (sscanf(line, "%255s = %255s", key, value) == 2) {
      unsigned long int_value = strtoul(value, NULL, 16);
      if (strcmp("abs_x", key) == 0)
        map->abs_x = int_value;
      else if (strcmp("abs_y", key) == 0)
        map->abs_y = int_value;
      else if (strcmp("abs_z", key) == 0)
        map->abs_z = int_value;
      else if (strcmp("abs_rx", key) == 0)
        map->abs_rx = int_value;
      else if (strcmp("abs_ry", key) == 0)
        map->abs_ry = int_value;
      else if (strcmp("abs_rz", key) == 0)
        map->abs_rz = int_value;
      else if (strcmp("abs_deadzone", key) == 0)
        map->abs_deadzone = int_value;
      else if (strcmp("abs_dpad_x", key) == 0)
        map->abs_dpad_x = int_value;
      else if (strcmp("abs_dpad_y", key) == 0)
        map->abs_dpad_y = int_value;
      else if (strcmp("btn_south", key) == 0)
        map->btn_south = int_value;
      else if (strcmp("btn_north", key) == 0)
        map->btn_north = int_value;
      else if (strcmp("btn_east", key) == 0)
        map->btn_east = int_value;
      else if (strcmp("btn_west", key) == 0)
        map->btn_west = int_value;
      else if (strcmp("btn_select", key) == 0)
        map->btn_select = int_value;
      else if (strcmp("btn_start", key) == 0)
        map->btn_start = int_value;
      else if (strcmp("btn_mode", key) == 0)
        map->btn_mode = int_value;
      else if (strcmp("btn_thumbl", key) == 0)
        map->btn_thumbl = int_value;
      else if (strcmp("btn_thumbr", key) == 0)
        map->btn_thumbr = int_value;
      else if (strcmp("btn_tl", key) == 0)
        map->btn_tl = int_value;
      else if (strcmp("btn_tr", key) == 0)
        map->btn_tr = int_value;
      else if (strcmp("btn_tl2", key) == 0)
        map->btn_tl2 = int_value;
      else if (strcmp("btn_tr2", key) == 0)
        map->btn_tr2 = int_value;
      else if (strcmp("btn_dpad_up", key) == 0)
        map->btn_dpad_up = int_value;
      else if (strcmp("btn_dpad_down", key) == 0)
        map->btn_dpad_down = int_value;
      else if (strcmp("btn_dpad_left", key) == 0)
        map->btn_dpad_left = int_value;
      else if (strcmp("btn_dpad_right", key) == 0)
        map->btn_dpad_right = int_value;
      else if (strcmp("reverse_x", key) == 0)
        map->reverse_x = strcmp("true", value) == 0;
      else if (strcmp("reverse_y", key) == 0)
        map->reverse_y = strcmp("true", value) == 0;
      else if (strcmp("reverse_rx", key) == 0)
        map->reverse_rx = strcmp("true", value) == 0;
      else if (strcmp("reverse_ry", key) == 0)
        map->reverse_ry = strcmp("true", value) == 0;
      else if (strcmp("reverse_dpad_x", key) == 0)
        map->reverse_dpad_x = strcmp("true", value) == 0;
      else if (strcmp("reverse_dpad_y", key) == 0)
        map->reverse_dpad_y = strcmp("true", value) == 0;
      else
        fprintf(stderr, "Can't map (%s)\n", key);
    }
  }
  free(line);
  bool succeeded = fclose(fd) == 0;
  if (succeeded && load_path_is_main) {
    if (temporary_name) {
      mapping_remove_file(temporary_name);
    }
    if (backup_name) {
      mapping_remove_file(backup_name);
    }
  }
  free(temporary_name);
  free(backup_name);
  return succeeded;
}

bool mapping_save(const char* fileName, const struct mapping* map) {
  char* temporary_name;
  char* backup_name;
  bool had_live_file;

  if (!fileName || !map) {
    return false;
  }

  temporary_name = mapping_sibling_name(fileName, ".tmp");
  backup_name = mapping_sibling_name(fileName, ".bak");
  if (!temporary_name || !backup_name) {
    free(temporary_name);
    free(backup_name);
    return false;
  }

  /*
   * Finish recovery from an interrupted earlier save before starting a new
   * transaction. A .bak file is always the last known live mapping.
   */
  if (!mapping_path_exists(fileName) &&
      mapping_path_exists(backup_name) &&
      mapping_rename_file(backup_name, fileName) != 0) {
    free(temporary_name);
    free(backup_name);
    return false;
  }

  FILE* fd = fopen(temporary_name, "w");
  if (fd == NULL) {
    fprintf(stderr, "Can't open mapping file: %s\n", fileName);
    free(temporary_name);
    free(backup_name);
    return false;
  }

  write_config(fd, "abs_x", map->abs_x);
  write_config(fd, "abs_y", map->abs_y);
  write_config(fd, "abs_z", map->abs_z);

  write_config_bool(fd, "reverse_x", map->reverse_x);
  write_config_bool(fd, "reverse_y", map->reverse_y);

  write_config(fd, "abs_rx", map->abs_rx);
  write_config(fd, "abs_ry", map->abs_ry);
  write_config(fd, "abs_rz", map->abs_rz);

  write_config_bool(fd, "reverse_rx", map->reverse_rx);
  write_config_bool(fd, "reverse_ry", map->reverse_ry);

  write_config(fd, "abs_deadzone", map->abs_deadzone);

  write_config(fd, "abs_dpad_x", map->abs_dpad_x);
  write_config(fd, "abs_dpad_y", map->abs_dpad_y);

  write_config_bool(fd, "reverse_dpad_x", map->reverse_dpad_x);
  write_config_bool(fd, "reverse_dpad_y", map->reverse_dpad_y);

  write_config(fd, "btn_north", map->btn_north);
  write_config(fd, "btn_east", map->btn_east);
  write_config(fd, "btn_south", map->btn_south);
  write_config(fd, "btn_west", map->btn_west);

  write_config(fd, "btn_select", map->btn_select);
  write_config(fd, "btn_start", map->btn_start);
  write_config(fd, "btn_mode", map->btn_mode);

  write_config(fd, "btn_thumbl", map->btn_thumbl);
  write_config(fd, "btn_thumbr", map->btn_thumbr);

  write_config(fd, "btn_tl", map->btn_tl);
  write_config(fd, "btn_tr", map->btn_tr);
  write_config(fd, "btn_tl2", map->btn_tl2);
  write_config(fd, "btn_tr2", map->btn_tr2);

  write_config(fd, "btn_dpad_up", map->btn_dpad_up);
  write_config(fd, "btn_dpad_down", map->btn_dpad_down);
  write_config(fd, "btn_dpad_left", map->btn_dpad_left);
  write_config(fd, "btn_dpad_right", map->btn_dpad_right);
  fprintf(fd, "# mapping_complete\n");

  bool succeeded = ferror(fd) == 0;
  if (fclose(fd) != 0) {
    succeeded = false;
  }
  if (!succeeded) {
    mapping_remove_file(temporary_name);
    free(temporary_name);
    free(backup_name);
    return false;
  }

  had_live_file = mapping_path_exists(fileName);
  if (had_live_file) {
    if (mapping_path_exists(backup_name) &&
        mapping_remove_file(backup_name) != 0) {
      mapping_remove_file(temporary_name);
      free(temporary_name);
      free(backup_name);
      return false;
    }
    if (mapping_rename_file(fileName, backup_name) != 0) {
      mapping_remove_file(temporary_name);
      free(temporary_name);
      free(backup_name);
      return false;
    }
  }

  if (mapping_rename_file(temporary_name, fileName) != 0) {
    if (had_live_file &&
        mapping_rename_file(backup_name, fileName) == 0) {
      mapping_remove_file(temporary_name);
    } else if (!had_live_file) {
      mapping_remove_file(temporary_name);
    }
    free(temporary_name);
    free(backup_name);
    return false;
  }

  if (had_live_file) {
    mapping_remove_file(backup_name);
  }
  free(temporary_name);
  free(backup_name);
  return true;
}
