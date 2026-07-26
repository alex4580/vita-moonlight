#include <psp2/io/stat.h>
#include <stdio.h>
#include <string.h>
#include "config.h"
#include "check_dir.h"

// Intenta crear la carpeta en varias rutas y retorna la ruta exitosa en out_path
// out_path y out_key_dir deben tener espacio suficiente (>=MOONLIGHT_PATH_MAX)
void check_and_create_moonlight_dir(char* out_path, char* out_key_dir) {
    #ifdef USE_DIR_UMA0
    const char* test_paths[] = {
        "uma0:data/moonlight"
    };
    int path_count = 1;
    #else
    const char* test_paths[] = {
        "ux0:data/moonlight",
        "ux0:moonlight",
        "uma0:data/moonlight"
    };
    int path_count = sizeof(test_paths)/sizeof(test_paths[0]);
    #endif
    int success_idx = -1;
    int mkdir_result = 0;
    for (int i = 0; i < path_count; i++) {
        mkdir_result = sceIoMkdir(test_paths[i], 0777);
        if (mkdir_result >= 0 || mkdir_result == 0x80010011) { // 0x80010011 = ya existe
            success_idx = i;
            break;
        }
    }
    if (success_idx >= 0) {
        snprintf(out_key_dir, MOONLIGHT_PATH_MAX, "%s/", test_paths[success_idx]);
        out_key_dir[MOONLIGHT_PATH_MAX-1] = '\0';
        snprintf(out_path, MOONLIGHT_PATH_MAX, "%s/moonlight.conf", test_paths[success_idx]);
        out_path[MOONLIGHT_PATH_MAX-1] = '\0';
    } else {
        snprintf(out_key_dir, MOONLIGHT_PATH_MAX, "ux0:data/");
        out_key_dir[MOONLIGHT_PATH_MAX-1] = '\0';
        snprintf(out_path, MOONLIGHT_PATH_MAX, "ux0:data/moonlight.conf");
        out_path[MOONLIGHT_PATH_MAX-1] = '\0';
    }
    // Crear el archivo moonlight.conf si no existe
    FILE* conf_file = fopen(out_path, "a");
    if (conf_file) fclose(conf_file);

    // Guardar solo key_dir en moonlight.conf
    FILE* conf_save = fopen(out_path, "r+");
    if (conf_save) {
        // Buscar si ya existe la línea key_dir
        char line[512];
        int found = 0;
        while (fgets(line, sizeof(line), conf_save)) {
            if (strncmp(line, "key_dir = ", 10) == 0) {
                found = 1;
                break;
            }
        }
        if (!found) {
            fseek(conf_save, 0, SEEK_END);
            fprintf(conf_save, "key_dir = %s\n", out_key_dir);
        }
        fclose(conf_save);
    }
}
