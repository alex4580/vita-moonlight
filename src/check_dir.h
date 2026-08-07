#ifndef CHECK_DIR_H
#define CHECK_DIR_H

#include <stdbool.h>

#define MOONLIGHT_PATH_MAX 256

/* Select an app-owned writable data directory. Outputs stay empty on error. */
bool check_and_create_moonlight_dir(char* out_path, char* out_key_dir);

#endif // CHECK_DIR_H
