#include <stdlib.h>
#include <errno.h>
#include <string.h>
#include <dirent.h>
#include <unistd.h>
#include <sys/stat.h>
#include <ini.h>
#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>
#include <psp2/io/dirent.h>
#include "wake_on_lan.h"

#include "device.h"
#include "debug.h"
#include "config.h"

#define DEVICE_FILE "device.ini"


bool device_name_equal(const char *left, const char *right) {
  if (left == NULL || right == NULL) return left == right;
  while (*left != '\0' && *right != '\0') {
    unsigned char a = (unsigned char)*left++;
    unsigned char b = (unsigned char)*right++;
    if (a >= 'A' && a <= 'Z') a = (unsigned char)(a + ('a' - 'A'));
    if (b >= 'A' && b <= 'Z') b = (unsigned char)(b + ('a' - 'A'));
    if (a != b) return false;
  }
  return *left == *right;
}

static void copy_text(char *out, size_t out_size, const char *value) {
  if (out == NULL || out_size == 0) return;
  if (value == NULL) value = "";
  strncpy(out, value, out_size - 1);
  out[out_size - 1] = '\0';
}

static void copy_device(device_info_t *out, const device_info_t *info) {
  if (out == info) return;
  memset(out, 0, sizeof(*out));
  copy_text(out->name, sizeof(out->name), info->name);
  copy_text(out->display_name, sizeof(out->display_name),
            info->display_name[0] ? info->display_name : info->name);
  out->paired = info->paired;
  copy_text(out->internal, sizeof(out->internal), info->internal);
  copy_text(out->external, sizeof(out->external), info->external);
  copy_text(out->mac, sizeof(out->mac), info->mac);
  out->port = info->port;
  out->prefer_external = info->prefer_external;
}

static bool device_records_equal(const device_info_t *left,
                                 const device_info_t *right) {
  if (left == NULL || right == NULL) return false;

  device_info_t normalized_left;
  device_info_t normalized_right;
  copy_device(&normalized_left, left);
  copy_device(&normalized_right, right);
  return strcmp(normalized_left.name, normalized_right.name) == 0 &&
         strcmp(normalized_left.display_name,
                normalized_right.display_name) == 0 &&
         normalized_left.paired == normalized_right.paired &&
         strcmp(normalized_left.internal, normalized_right.internal) == 0 &&
         strcmp(normalized_left.external, normalized_right.external) == 0 &&
         strcmp(normalized_left.mac, normalized_right.mac) == 0 &&
         normalized_left.port == normalized_right.port &&
         normalized_left.prefer_external == normalized_right.prefer_external;
}

static bool valid_device_directory(const char *name) {
  if (name == NULL || name[0] == '\0' || !strcmp(name, ".") ||
      !strcmp(name, "..")) {
    return false;
  }
  for (const unsigned char *p = (const unsigned char *)name; *p; ++p) {
    if (*p < 0x20 || *p == 0x7f || *p == ':' || *p == '/' || *p == '\\') {
      return false;
    }
  }
  return true;
}

static bool valid_ini_value(const char *value) {
  return value != NULL && strchr(value, '\r') == NULL &&
         strchr(value, '\n') == NULL;
}

static bool parse_ini_bool(const char *value, bool *out) {
  if (value == NULL || out == NULL) return false;
  if (strcmp(value, "true") == 0) {
    *out = true;
    return true;
  }
  if (strcmp(value, "false") == 0) {
    *out = false;
    return true;
  }
  return false;
}

static bool device_directory_path(char *out, size_t out_size,
                                  const char *name) {
  if (out == NULL || out_size == 0 || !valid_device_directory(name)) {
    return false;
  }
  if (config.key_dir[0] == '\0') return false;
  size_t base_length = strlen(config.key_dir);
  const char *separator =
      base_length > 0 && config.key_dir[base_length - 1] == '/' ? "" : "/";
  int written = snprintf(
      out, out_size, "%s%s%s", config.key_dir, separator, name);
  return written >= 0 && (size_t)written < out_size;
}

// Elimina la carpeta y el archivo del dispositivo
bool remove_device(const char *name) {
  int idx = -1;
  for (int i = 0; i < known_devices.count; i++) {
    if (device_name_equal(known_devices.devices[i].name, name)) {
      idx = i;
      break;
    }
  }
  if (idx == -1) {
    vita_debug_log("remove_device: device %s not found\n", name);
    return false;
  }
  // Eliminar del disco
  char dir_path[DEVICE_PATH_CAPACITY];
  char file_path[DEVICE_PATH_CAPACITY];
  if (!device_directory_path(dir_path, sizeof(dir_path), name) ||
      !device_file_path(file_path, sizeof(file_path), name)) {
    vita_debug_log("remove_device: unsafe or overlong device path\n");
    return false;
  }
  sceIoRemove(file_path); // Elimina device.ini
  bool storage_cleared = true;
  // Elimina todos los archivos dentro de la carpeta antes de borrar la carpeta
  SceIoDirent dirent;
  SceUID dfd = sceIoDopen(dir_path);
  if (dfd >= 0) {
    while (sceIoDread(dfd, &dirent) > 0) {
      if (strcmp(dirent.d_name, ".") == 0 || strcmp(dirent.d_name, "..") == 0) continue;
      char full_path[DEVICE_PATH_CAPACITY];
      int written = snprintf(
          full_path, sizeof(full_path), "%s/%s", dir_path, dirent.d_name);
      if (written >= 0 && (size_t)written < sizeof(full_path)) {
        if (sceIoRemove(full_path) < 0) storage_cleared = false;
      } else {
        storage_cleared = false;
      }
    }
    sceIoDclose(dfd);
  }
  if (dfd >= 0 && sceIoRmdir(dir_path) < 0) storage_cleared = false;
  SceIoStat remaining = {0};
  if (sceIoGetstat(file_path, &remaining) >= 0 ||
      sceIoGetstat(dir_path, &remaining) >= 0) {
    storage_cleared = false;
  }
  if (!storage_cleared) {
    vita_debug_log("remove_device: local pairing files could not be removed\n");
    return false;
  }
  // Remove the canonical in-memory record only after all paths are validated.
  for (int i = idx; i < known_devices.count - 1; i++) {
    known_devices.devices[i] = known_devices.devices[i + 1];
  }
  known_devices.count--;
  vita_debug_log("remove_device: device %s removed from memory and disk\n", name);
  return true;
}


device_infos_t known_devices = {0};

device_info_t* find_device(const char *name) {
  // TODO: mutex
  for (int i = 0; i < known_devices.count; i++) {
    if (device_name_equal(name, known_devices.devices[i].name)) {
      return &known_devices.devices[i];
    }
  }
  return NULL;
}

device_info_t* find_device_by_address(const char *address) {
  if (address == NULL)
    return NULL;
  for (int i = 0; i < known_devices.count; i++) {
    device_info_t *d = &known_devices.devices[i];
    if (strcmp(d->internal, address) == 0 || strcmp(d->external, address) == 0) {
      return d;
    }
  }
  return NULL;
}

bool device_file_path(char *out, size_t out_size, const char *dir) {
  if (out == NULL || out_size == 0 || !valid_device_directory(dir)) {
    return false;
  }
  if (config.key_dir[0] == '\0') return false;
  size_t base_length = strlen(config.key_dir);
  const char *separator =
      base_length > 0 && config.key_dir[base_length - 1] == '/' ? "" : "/";
  int written = snprintf(
      out, out_size, "%s%s%s/%s", config.key_dir, separator, dir,
      DEVICE_FILE);
  return written >= 0 && (size_t)written < out_size;
}

static int device_ini_handle(void *out, const char *section, const char *name,
                             const char *value) {
  device_info_t *info = out;

  if (strcmp(name, "paired") == 0) {
    if (!parse_ini_bool(value, &info->paired)) return 0;
  } else if (strcmp(name, "display_name") == 0) {
    copy_text(info->display_name, sizeof(info->display_name), value);
  } else if (strcmp(name, "internal") == 0) {
    copy_text(info->internal, sizeof(info->internal), value);
  } else if (strcmp(name, "external") == 0) {
    copy_text(info->external, sizeof(info->external), value);
  } else if (strcmp(name, "mac") == 0) {
    copy_text(info->mac, sizeof(info->mac), value);
  } else if (strcmp(name, "port") == 0) {
    errno = 0;
    char *end = NULL;
    long port = strtol(value, &end, 10);
    if (errno == 0 && end != value && *end == '\0' &&
        port > 0 && port <= UINT16_MAX) {
      info->port = (uint16_t)port;
    } else {
      return 0;
    }
  } else if (strcmp(name, "prefer_external") == 0) {
    if (!parse_ini_bool(value, &info->prefer_external)) return 0;
  }
  return 1;
}

static bool parse_device_candidate(const char *path, const char *device_name,
                                   device_info_t *out, int *parse_result) {
  if (path == NULL || device_name == NULL || out == NULL) return false;
  memset(out, 0, sizeof(*out));
  copy_text(out->name, sizeof(out->name), device_name);
  /* Older device.ini files did not persist the port. */
  out->port = 47989;
  int result = ini_parse(path, device_ini_handle, out);
  if (parse_result != NULL) *parse_result = result;
  return result == 0 && out->internal[0] != '\0' && out->port != 0;
}

device_info_t* append_device(const device_info_t *info) {
  if (info == NULL || !valid_device_directory(info->name)) return NULL;
  if (find_device(info->name)) {
    vita_debug_log("append_device: device %s is already in the list\n", info->name);
    return NULL;
  }
  if (known_devices.count >= DEVICE_MAX_COUNT) {
    vita_debug_log("append_device: saved device limit (%d) reached\n",
                   DEVICE_MAX_COUNT);
    return NULL;
  }
  // The UI stops the host scanner before mutating this collection.
  if (known_devices.size == 0) {
    vita_debug_log("append_device: allocating memory for the initial device list...\n");
    known_devices.devices = malloc(sizeof(device_info_t) * 4);
    if (known_devices.devices == NULL) {
      vita_debug_log("append_device: failed to allocate memory for the initial device list\n");
      return NULL;
    }
    known_devices.size = 4;
  } else if (known_devices.size == known_devices.count) {
    vita_debug_log("append_device: the device list is full, resizing...\n");
    //if (known_devices.size == 64) {
    //  return false;
    //}
    size_t new_size = sizeof(device_info_t) * (known_devices.size * 2);
    device_info_t *tmp = realloc(known_devices.devices, new_size);
    if (tmp == NULL) {
      vita_debug_log("append_device: failed to resize the device list\n");
      return NULL;
    }
    known_devices.devices = tmp;
    known_devices.size *= 2;
  }
  device_info_t *p = &known_devices.devices[known_devices.count];

  copy_device(p, info);
  vita_debug_log("append_device: device %s is added to the list\n", p->name);

  known_devices.count++;
  return p;
}

device_info_t* upsert_device(const device_info_t *info) {
  if (info == NULL) return NULL;
  device_info_t *p = find_device(info->name);
  if (p == NULL) return append_device(info);

  /* Discovery records are intentionally incomplete. Never let a rediscovery
   * clear authentication, a user-facing alias, the learned external address,
   * or a MAC address. Authoritative pairing code may update those fields on
   * the returned canonical record after this merge. */
  device_info_t merged;
  copy_device(&merged, info);
  if (p->paired) merged.paired = true;
  if (merged.display_name[0] == '\0' ||
      !strcmp(merged.display_name, merged.name)) {
    copy_text(merged.display_name, sizeof(merged.display_name),
              p->display_name);
  }
  if (merged.external[0] == '\0') {
    copy_text(merged.external, sizeof(merged.external), p->external);
    merged.prefer_external = p->prefer_external;
  }
  if (merged.mac[0] == '\0') {
    copy_text(merged.mac, sizeof(merged.mac), p->mac);
  }
  if (merged.port == 0) merged.port = p->port;
  /* merged is already normalized and fully self-contained. Assigning the
   * value directly also makes it unambiguous that the returned pointer is the
   * canonical collection entry, never the temporary merge buffer. */
  *p = merged;
  return p;
}

void load_all_known_devices() {
  //struct stat st;
  device_info_t info;

  SceUID dfd = sceIoDopen(config.key_dir);
  if (dfd < 0) {
    return;
  }
  do {
    SceIoDirent ent = {0};
    if (sceIoDread(dfd, &ent) <= 0) {
      break;
    }
    if (strcmp(".", ent.d_name) == 0 || strcmp("..", ent.d_name) == 0) {
      continue;
    }
    if (!SCE_S_ISDIR(ent.d_stat.st_mode)) {
      continue;
    }

    memset(&info, 0, sizeof(device_info_t));
    copy_text(info.name, sizeof(info.name), ent.d_name);
    if (!load_device_info(&info)) {
      continue;
    }
    if (info.display_name[0] == '\0') {
      copy_text(info.display_name, sizeof(info.display_name), info.name);
    }
    upsert_device(&info);
  } while(true);

  sceIoDclose(dfd);
  return;
}

bool load_device_info(device_info_t *info) {
  char path[DEVICE_PATH_CAPACITY] = {0};
  char backup_path[DEVICE_PATH_CAPACITY] = {0};
  char temporary_path[DEVICE_PATH_CAPACITY] = {0};
  if (info == NULL || !device_file_path(path, sizeof(path), info->name)) {
    return false;
  }
  int backup_length = snprintf(
      backup_path, sizeof(backup_path), "%s.bak", path);
  int temporary_length = snprintf(
      temporary_path, sizeof(temporary_path), "%s.tmp", path);
  if (backup_length < 0 || (size_t)backup_length >= sizeof(backup_path) ||
      temporary_length < 0 ||
      (size_t)temporary_length >= sizeof(temporary_path)) {
    return false;
  }
  vita_debug_log("load_device_info: reading %s\n", path);

  char preserved_name[sizeof(info->name)];
  copy_text(preserved_name, sizeof(preserved_name), info->name);
  /* A valid primary is the last committed record. If it is absent or torn,
   * the transaction order is primary -> .bak, then .tmp -> primary, so the
   * complete .tmp is the intended next record and must beat the older backup.
   * Reversing those siblings can silently resurrect paired=false after a
   * successful pairing interrupted in the final rename window. */
  const char *candidates[] = {path, temporary_path, backup_path};
  int selected = -1;
  int ret = -1;
  device_info_t parsed;
  for (size_t i = 0; i < sizeof(candidates) / sizeof(candidates[0]); ++i) {
    if (parse_device_candidate(
            candidates[i], preserved_name, &parsed, &ret)) {
      selected = (int)i;
      break;
    }
  }

  bool valid = selected >= 0;
  if (valid) {
    copy_device(info, &parsed);
    if (selected != 0) {
      /* Recover a complete backup, or the complete first-save .tmp left by a
       * power loss.  Continue using the parsed record even if promotion is
       * temporarily unavailable; its source remains intact for next boot. */
      sceIoRemove(path);
      (void)sceIoRename(candidates[selected], path);
    }
  }
  if (valid) {
    vita_debug_log("load_device_info: device found\n");
    vita_debug_log("load_device_info:   info->name = %s\n", info->name);
    vita_debug_log("load_device_info:   info->paired = %s\n", info->paired ? "true" : "false");
    vita_debug_log("load_device_info:   info->internal = %s\n", info->internal);
    vita_debug_log("load_device_info:   info->external = %s\n", info->external);
    vita_debug_log("load_device_info:   info->port= %d\n", info->port);
    vita_debug_log("load_device_info:   info->prefer_external = %s\n", info->prefer_external ? "true" : "false");
    return true;
  } else {
    vita_debug_log(
        "load_device_info: no complete primary, backup, or temporary record "
        "(last parser result %d)\n", ret);
    return false;
  }
}

bool save_device_info(const device_info_t *info) {
  char path[DEVICE_PATH_CAPACITY] = {0};
  char temporary_path[DEVICE_PATH_CAPACITY] = {0};
  char backup_path[DEVICE_PATH_CAPACITY] = {0};
  if (info == NULL || info->internal[0] == '\0' || info->port == 0 ||
      !valid_ini_value(info->display_name) ||
      !valid_ini_value(info->internal) || !valid_ini_value(info->external) ||
      !valid_ini_value(info->mac) ||
      !device_file_path(path, sizeof(path), info->name)) {
    vita_debug_log("save_device_info: unsafe or overlong device path\n");
    return false;
  }
  int temporary_length = snprintf(
      temporary_path, sizeof(temporary_path), "%s.tmp", path);
  int backup_length = snprintf(
      backup_path, sizeof(backup_path), "%s.bak", path);
  if (temporary_length < 0 ||
      (size_t)temporary_length >= sizeof(temporary_path) ||
      backup_length < 0 || (size_t)backup_length >= sizeof(backup_path)) {
    vita_debug_log("save_device_info: device journal path is too long\n");
    return false;
  }
  vita_debug_log("save_device_info: device file path: %s\n", path);

  // Ya no se intenta obtener la MAC por ARP. Solo se guarda la que esté en info->mac.

  sceIoRemove(temporary_path);
  FILE* fd = fopen(temporary_path, "w");
  if (!fd) {
    vita_debug_log("save_device_info: cannot open device file\n");
    return false;
  }

  bool ok = true;
  vita_debug_log("save_device_info: paired = %s\n", info->paired ? "true" : "false");
  ok &= fprintf(fd, "paired = %s\n", info->paired ? "true" : "false") >= 0;

  vita_debug_log("save_device_info: display_name = %s\n", info->display_name);
  ok &= fprintf(fd, "display_name = %s\n",
                info->display_name[0] ? info->display_name : info->name) >= 0;

  vita_debug_log("save_device_info: internal = %s\n", info->internal);
  ok &= fprintf(fd, "internal = %s\n", info->internal) >= 0;

  vita_debug_log("save_device_info: external = %s\n", info->external);
  ok &= fprintf(fd, "external = %s\n", info->external) >= 0;

  vita_debug_log("save_device_info: mac = %s\n", info->mac);
  ok &= fprintf(fd, "mac = %s\n", info->mac) >= 0;

  vita_debug_log("save_device_info: port = %d\n", info->port);
  ok &= fprintf(fd, "port = %d\n", info->port) >= 0;

  vita_debug_log("save_device_info: prefer_external = %s\n", info->prefer_external ? "true" : "false");
  ok &= fprintf(fd, "prefer_external = %s\n",
                info->prefer_external ? "true" : "false") >= 0;

  ok &= fflush(fd) == 0;
  ok &= fclose(fd) == 0;
  if (!ok) {
    sceIoRemove(temporary_path);
    vita_debug_log("save_device_info: write failed\n");
    return false;
  }

  /* Verify the exact staged record before rotating the only committed copy.
   * This also prevents a caller from receiving success for a file that the
   * next launch would reject and omit from Saved computers. */
  device_info_t staged;
  int staged_parse_result = -1;
  if (!parse_device_candidate(
          temporary_path, info->name, &staged, &staged_parse_result) ||
      !device_records_equal(&staged, info)) {
    sceIoRemove(temporary_path);
    vita_debug_log(
        "save_device_info: staged record did not verify (parser %d)\n",
        staged_parse_result);
    return false;
  }

  SceIoStat existing = {0};
  bool had_existing = sceIoGetstat(path, &existing) >= 0;
  sceIoRemove(backup_path);
  if (had_existing && sceIoRename(path, backup_path) < 0) {
    sceIoRemove(temporary_path);
    vita_debug_log("save_device_info: could not prepare existing file\n");
    return false;
  }
  if (sceIoRename(temporary_path, path) < 0) {
    if (had_existing) sceIoRename(backup_path, path);
    sceIoRemove(temporary_path);
    vita_debug_log("save_device_info: could not commit device file\n");
    return false;
  }
  device_info_t committed;
  int committed_parse_result = -1;
  if (!parse_device_candidate(
          path, info->name, &committed, &committed_parse_result) ||
      !device_records_equal(&committed, info)) {
    /* Keep the operation transactional even if storage reported a successful
     * rename but the installed record cannot be read back exactly. */
    (void)sceIoRemove(path);
    if (had_existing) (void)sceIoRename(backup_path, path);
    vita_debug_log(
        "save_device_info: committed record did not verify (parser %d)\n",
        committed_parse_result);
    return false;
  }
  if (had_existing) sceIoRemove(backup_path);
  vita_debug_log("save_device_info: file committed\n");
  return true;
}
