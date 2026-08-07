#include <psp2/io/fcntl.h>
#include <psp2/io/stat.h>

#include <stdbool.h>
#include <stdio.h>

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

bool check_and_create_moonlight_dir(char *out_path, char *out_key_dir) {
    if (!out_path || !out_key_dir) return false;
    out_path[0] = '\0';
    out_key_dir[0] = '\0';

#ifdef USE_DIR_UMA0
    const char *test_paths[] = {
        "uma0:data/moonlight",
    };
#else
    const char *test_paths[] = {
        "ux0:data/moonlight",
        "ux0:moonlight",
        "uma0:data/moonlight",
    };
#endif
    const int path_count = sizeof(test_paths) / sizeof(test_paths[0]);

    for (int i = 0; i < path_count; i++) {
        const char *directory = test_paths[i];
        if (!ensure_directory(directory) ||
            !verify_writable_directory(directory)) {
            continue;
        }

        int key_length = snprintf(out_key_dir, MOONLIGHT_PATH_MAX,
                                  "%s/", directory);
        int config_length = snprintf(out_path, MOONLIGHT_PATH_MAX,
                                     "%s/moonlight.conf", directory);
        if (key_length < 0 || key_length >= MOONLIGHT_PATH_MAX ||
            config_length < 0 || config_length >= MOONLIGHT_PATH_MAX) {
            out_path[0] = '\0';
            out_key_dir[0] = '\0';
            continue;
        }

        SceIoStat config_status = {0};
        if (sceIoGetstat(out_path, &config_status) >= 0 &&
            SCE_S_ISDIR(config_status.st_mode)) {
            out_path[0] = '\0';
            out_key_dir[0] = '\0';
            continue;
        }
        return true;
    }

    return false;
}
