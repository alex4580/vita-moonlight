#ifndef UI_CONTROLLER_MAPPER_H
#define UI_CONTROLLER_MAPPER_H

#include <stdbool.h>

/*
 * Opens the two Vita-specific graphical input editors. The return value is
 * true when the editor changed a persisted mapping or the main configuration.
 */
bool ui_controller_mapper_menu(void);
bool ui_front_touch_mapper_menu(void);

/*
 * Enables or disables the writable custom controller map and updates the
 * running input mapper immediately. Enabling creates the default file when it
 * does not exist.
 */
bool ui_controller_mapping_set_enabled(bool enabled);

#endif
