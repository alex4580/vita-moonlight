#include <psp2/io/fcntl.h>
#include <psp2/io/dirent.h>
#include <psp2/io/stat.h>

#include <stdbool.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <ini.h>

#include "check_dir.h"

static bool directory_exists(const char *path) {
    SceIoStat status = {0};
    return sceIoGetstat(path, &status) >= 0 && SCE_S_ISDIR(status.st_mode);
}

static bool ensure_directory(const char *path) {
    if (directory_exists(path)) return true;
    if (sceIoMkdir(path, 0777) >= 0) return true;
    /* Another creator or an interrupted first run may have won the race. */
    return directory_exists(path);
}

static bool verify_writable_directory(const char *path) {
    char probe_path[MOONLIGHT_PATH_MAX];
    int written = snprintf(probe_path, sizeof(probe_path),
                           "%s/.vita-moonlight-write-test", path);
    if (written < 0 || (size_t)written >= sizeof(probe_path)) return false;

    FILE *probe = fopen(probe_path, "wb");
    if (!probe) return false;
    static const char marker[] = "vita-moonlight-storage-check\n";
    bool ok = fwrite(marker, 1, sizeof(marker) - 1, probe) ==
              sizeof(marker) - 1;
    if (fflush(probe) != 0) ok = false;
    if (fclose(probe) != 0) ok = false;
    if (sceIoRemove(probe_path) < 0) ok = false;
    return ok;
}

static bool artifact_exists(const char *directory, const char *name) {
    char path[MOONLIGHT_PATH_MAX];
    int written = snprintf(path, sizeof(path), "%s/%s", directory, name);
    if (written < 0 || (size_t)written >= sizeof(path)) return false;

    SceIoStat status = {0};
    return sceIoGetstat(path, &status) >= 0 &&
           !SCE_S_ISDIR(status.st_mode);
}

#define STORAGE_ROOT_MARKER ".vita-moonlight-root"
#define STORAGE_ROOT_MARKER_CONTENT "vita-moonlight-root-v1\n"

typedef struct saved_host_probe {
    bool paired;
    bool internal_present;
    bool port_valid;
} saved_host_probe_t;

typedef struct storage_inventory {
    const char *path;
    bool exists;
    bool marker_valid;
    bool config_artifact;
    unsigned int device_artifacts;
    unsigned int valid_hosts;
    unsigned int paired_hosts;
    unsigned int credential_artifacts;
} storage_inventory_t;

static const char *storage_error =
    "No writable Vita Moonlight data directory is available.";

const char *moonlight_storage_error(void) {
    return storage_error;
}

static int saved_host_probe_handler(
    void *output, const char *section, const char *name, const char *value) {
    (void)section;
    saved_host_probe_t *probe = (saved_host_probe_t *)output;
    if (!strcmp(name, "paired")) {
        if (!strcmp(value, "true")) {
            probe->paired = true;
        } else if (!strcmp(value, "false")) {
            probe->paired = false;
        } else {
            return 0;
        }
    } else if (!strcmp(name, "internal")) {
        probe->internal_present = value != NULL && value[0] != '\0';
    } else if (!strcmp(name, "port")) {
        errno = 0;
        char *end = NULL;
        long parsed = strtol(value, &end, 10);
        probe->port_valid = errno == 0 && end != value && *end == '\0' &&
                            parsed > 0 && parsed <= 65535;
    }
    return 1;
}

static bool valid_device_record(const char *host_directory, bool *paired) {
    static const char *names[] = {
        "device.ini", "device.ini.bak", "device.ini.tmp",
    };
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); ++i) {
        char path[MOONLIGHT_PATH_MAX];
        int written = snprintf(
            path, sizeof(path), "%s/%s", host_directory, names[i]);
        if (written < 0 || (size_t)written >= sizeof(path)) continue;

        saved_host_probe_t probe = {
            .paired = false,
            .internal_present = false,
            /* Historical records may omit port and default to 47989. */
            .port_valid = true,
        };
        if (ini_parse(path, saved_host_probe_handler, &probe) == 0 &&
            probe.internal_present && probe.port_valid) {
            if (paired != NULL) *paired = probe.paired;
            return true;
        }
    }
    return false;
}

static bool root_marker_valid(const char *directory) {
    char path[MOONLIGHT_PATH_MAX];
    int written = snprintf(
        path, sizeof(path), "%s/%s", directory, STORAGE_ROOT_MARKER);
    if (written < 0 || (size_t)written >= sizeof(path)) return false;

    char contents[sizeof(STORAGE_ROOT_MARKER_CONTENT)] = {0};
    FILE *file = fopen(path, "rb");
    if (file == NULL) return false;
    size_t length = fread(contents, 1, sizeof(contents) - 1, file);
    bool valid = length == sizeof(STORAGE_ROOT_MARKER_CONTENT) - 1 &&
                 fgetc(file) == EOF && !ferror(file) &&
                 !memcmp(contents, STORAGE_ROOT_MARKER_CONTENT, length);
    if (fclose(file) != 0) valid = false;
    return valid;
}

static bool persist_root_marker(const char *directory) {
    char path[MOONLIGHT_PATH_MAX];
    char temporary_path[MOONLIGHT_PATH_MAX];
    int path_length = snprintf(
        path, sizeof(path), "%s/%s", directory, STORAGE_ROOT_MARKER);
    int temporary_length = snprintf(
        temporary_path, sizeof(temporary_path), "%s/%s.tmp", directory,
        STORAGE_ROOT_MARKER);
    if (path_length < 0 || (size_t)path_length >= sizeof(path) ||
        temporary_length < 0 ||
        (size_t)temporary_length >= sizeof(temporary_path)) {
        return false;
    }
    if (root_marker_valid(directory)) return true;

    sceIoRemove(temporary_path);
    FILE *file = fopen(temporary_path, "wb");
    if (file == NULL) return false;
    bool valid = fwrite(
        STORAGE_ROOT_MARKER_CONTENT, 1,
        sizeof(STORAGE_ROOT_MARKER_CONTENT) - 1, file) ==
        sizeof(STORAGE_ROOT_MARKER_CONTENT) - 1;
    if (fflush(file) != 0) valid = false;
    if (fclose(file) != 0) valid = false;
    if (!valid) {
        sceIoRemove(temporary_path);
        return false;
    }
    sceIoRemove(path);
    if (sceIoRename(temporary_path, path) < 0) {
        sceIoRemove(temporary_path);
        return false;
    }
    return root_marker_valid(directory);
}

static void inventory_storage_root(
    storage_inventory_t *inventory, bool legacy_root) {
    inventory->exists = directory_exists(inventory->path);
    if (!inventory->exists) return;

    static const char *config_artifacts[] = {
        "moonlight.conf", "moonlight.conf.bak", "moonlight.conf.tmp",
    };
    for (size_t i = 0;
         i < sizeof(config_artifacts) / sizeof(config_artifacts[0]); ++i) {
        if (artifact_exists(inventory->path, config_artifacts[i])) {
            inventory->config_artifact = true;
            break;
        }
    }
    /* The historical whole-ux0:data fallback is only app-owned when its exact
     * Moonlight settings file exists. Never scan arbitrary Vita app folders. */
    if (legacy_root && !inventory->config_artifact) return;

    inventory->marker_valid = root_marker_valid(inventory->path);
    SceUID directory_handle = sceIoDopen(inventory->path);
    if (directory_handle < 0) return;

    static const char *device_artifacts[] = {
        "device.ini", "device.ini.bak", "device.ini.tmp",
    };
    static const char *credential_artifacts[] = {
        "server-pin.txt", "server-pin.txt.bak", "server-pin.txt.tmp",
        "client.pem", "client.pem.bak", "client.pem.tmp",
        "key.pem", "key.pem.bak", "key.pem.tmp",
        "uniqueid.dat", "uniqueid.dat.bak", "uniqueid.dat.tmp",
        "pairing-pending.dat", "pairing-pending.dat.bak",
        "pairing-pending.dat.tmp",
    };
    SceIoDirent entry;
    while (true) {
        memset(&entry, 0, sizeof(entry));
        if (sceIoDread(directory_handle, &entry) <= 0) break;
        if (!strcmp(entry.d_name, ".") || !strcmp(entry.d_name, "..") ||
            !SCE_S_ISDIR(entry.d_stat.st_mode)) {
            continue;
        }

        char host_directory[MOONLIGHT_PATH_MAX];
        int written = snprintf(host_directory, sizeof(host_directory),
                               "%s/%s", inventory->path, entry.d_name);
        if (written < 0 || (size_t)written >= sizeof(host_directory)) {
            continue;
        }

        bool has_device_artifact = false;
        for (size_t i = 0;
             i < sizeof(device_artifacts) / sizeof(device_artifacts[0]); ++i) {
            if (artifact_exists(host_directory, device_artifacts[i])) {
                has_device_artifact = true;
                break;
            }
        }
        if (has_device_artifact) {
            inventory->device_artifacts++;
            bool paired = false;
            if (valid_device_record(host_directory, &paired)) {
                inventory->valid_hosts++;
                if (paired) inventory->paired_hosts++;
            }
        }

        for (size_t i = 0;
             i < sizeof(credential_artifacts) /
                     sizeof(credential_artifacts[0]); ++i) {
            if (artifact_exists(host_directory, credential_artifacts[i])) {
                inventory->credential_artifacts++;
                break;
            }
        }
    }
    sceIoDclose(directory_handle);
}

static bool inventory_has_host_state(const storage_inventory_t *inventory) {
    return inventory->device_artifacts != 0 ||
           inventory->credential_artifacts != 0;
}

static int choose_existing_root(
    const storage_inventory_t *inventories, size_t count, bool *ambiguous) {
    *ambiguous = false;

    /* Once written, a valid marker is the user's previous unambiguous choice.
     * Only host-bearing roots participate so a stale empty marker can never
     * hide a recovered legacy computer. */
    int selected = -1;
    for (size_t i = 0; i < count; ++i) {
        if (inventories[i].marker_valid &&
            inventory_has_host_state(&inventories[i])) {
            if (selected >= 0) {
                *ambiguous = true;
                return -1;
            }
            selected = (int)i;
        }
    }
    if (selected >= 0) return selected;

    /* A single root with a complete paired record beats newly created
     * unpaired remnants. Multiple paired stores may carry different pins and
     * must be resolved explicitly rather than scored or merged. */
    for (int tier = 0; tier < 4; ++tier) {
        selected = -1;
        for (size_t i = 0; i < count; ++i) {
            bool matches = false;
            if (tier == 0) matches = inventories[i].paired_hosts != 0;
            if (tier == 1) matches = inventories[i].valid_hosts != 0;
            if (tier == 2) matches = inventories[i].device_artifacts != 0;
            if (tier == 3) matches = inventories[i].credential_artifacts != 0;
            if (!matches) continue;
            if (selected >= 0) {
                *ambiguous = true;
                return -1;
            }
            selected = (int)i;
        }
        if (selected >= 0) return selected;
    }

    /* Settings alone are not pairing authority. Prefer the modern canonical
     * order and let config.c validate/recover the selected journal. */
    for (size_t i = 0; i < count; ++i) {
        if (inventories[i].config_artifact) return (int)i;
    }
    return -1;
}

static bool publish_storage_paths(
    const char *directory, char *out_path, char *out_key_dir) {
    int key_length = snprintf(out_key_dir, MOONLIGHT_PATH_MAX,
                              "%s/", directory);
    int config_length = snprintf(out_path, MOONLIGHT_PATH_MAX,
                                 "%s/moonlight.conf", directory);
    if (key_length < 0 || key_length >= MOONLIGHT_PATH_MAX ||
        config_length < 0 || config_length >= MOONLIGHT_PATH_MAX) {
        out_path[0] = '\0';
        out_key_dir[0] = '\0';
        return false;
    }

    SceIoStat config_status = {0};
    if (sceIoGetstat(out_path, &config_status) >= 0 &&
        SCE_S_ISDIR(config_status.st_mode)) {
        out_path[0] = '\0';
        out_key_dir[0] = '\0';
        return false;
    }
    return true;
}

bool check_and_create_moonlight_dir(char *out_path, char *out_key_dir) {
    if (!out_path || !out_key_dir) return false;
    out_path[0] = '\0';
    out_key_dir[0] = '\0';

    storage_error =
        "No writable Vita Moonlight data directory is available.";
    storage_inventory_t inventories[] = {
        {.path = "ux0:data/moonlight"},
        {.path = "ux0:moonlight"},
        {.path = "uma0:data/moonlight"},
        /* Exact compatibility with the pre-directory fallback. */
        {.path = "ux0:data"},
    };
    const size_t inventory_count =
        sizeof(inventories) / sizeof(inventories[0]);
    for (size_t i = 0; i < inventory_count; ++i) {
        inventory_storage_root(&inventories[i], i == inventory_count - 1);
    }

    bool ambiguous = false;
    int selected = choose_existing_root(
        inventories, inventory_count, &ambiguous);
    if (ambiguous) {
        storage_error =
            "Saved computers exist in more than one Vita Moonlight data "
            "folder. Pairing identities were not merged or overwritten. "
            "Use VitaShell to keep one of ux0:data/moonlight, "
            "ux0:moonlight, uma0:data/moonlight, or the legacy "
            "ux0:data/moonlight.conf store, then restart.";
        return false;
    }
    if (selected >= 0) {
        const char *directory = inventories[selected].path;
        if (!verify_writable_directory(directory)) {
            storage_error =
                "The Vita Moonlight folder containing saved computers is "
                "not writable. Reconnect that storage or repair its "
                "permissions; no alternate pairing store was used.";
            return false;
        }
        if (!persist_root_marker(directory) ||
            !publish_storage_paths(directory, out_path, out_key_dir)) {
            storage_error =
                "The selected Vita Moonlight data folder could not be "
                "verified. Existing pairing data was left unchanged.";
            return false;
        }
        return true;
    }

#ifdef USE_DIR_UMA0
    const char *creation_paths[] = {"uma0:data/moonlight"};
#else
    const char *creation_paths[] = {
        "ux0:data/moonlight", "ux0:moonlight", "uma0:data/moonlight",
    };
#endif
    const size_t creation_count =
        sizeof(creation_paths) / sizeof(creation_paths[0]);
    for (size_t i = 0; i < creation_count; ++i) {
        const char *directory = creation_paths[i];
        if (!ensure_directory(directory) ||
            !verify_writable_directory(directory)) {
            continue;
        }

        if (persist_root_marker(directory) &&
            publish_storage_paths(directory, out_path, out_key_dir)) {
            return true;
        }
    }

    return false;
}
