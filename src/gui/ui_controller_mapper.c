#include "ui_controller_mapper.h"

#include "guilib.h"
#include "ime.h"

#include "../config.h"
#include "../input/mapping.h"
#include "../input/swap_shoulder_buttons.h"
#include "../input/vita.h"

#include <Limelight.h>
#include <psp2/ctrl.h>
#include <psp2/io/stat.h>
#include <psp2/touch.h>
#include <vita2d.h>

#include <stddef.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define ARRAY_COUNT(array) (sizeof(array) / sizeof((array)[0]))
#define CUSTOM_MAPPING_RELATIVE_PATH "mappings/vita.conf"
#define MAPPING_PATH_CAPACITY 4352
#define VITA_ERROR_ALREADY_EXISTS UINT32_C(0x80010011)

#define PANEL_COLOR RGBA8(10, 16, 24, 230)
#define PANEL_BORDER_COLOR RGBA8(46, 168, 91, 255)
#define TEXT_COLOR RGBA8(238, 244, 248, 255)
#define MUTED_TEXT_COLOR RGBA8(164, 176, 185, 255)
#define ACTIVE_COLOR RGBA8(40, 220, 105, 255)
#define TOUCH_ZONE_COLOR RGBA8(28, 166, 91, 104)
#define TOUCH_ZONE_ACTIVE_COLOR RGBA8(47, 235, 119, 174)

static bool join_key_directory(char *output,
                               size_t output_size,
                               const char *relative_path) {
  size_t key_dir_length;
  int written;

  if (!output || output_size == 0 || !relative_path) {
    return false;
  }

  key_dir_length = strlen(config.key_dir);
  written = snprintf(
      output, output_size, "%s%s%s",
      config.key_dir,
      key_dir_length > 0 && config.key_dir[key_dir_length - 1] != '/'
          ? "/"
          : "",
      relative_path);
  return written >= 0 && (size_t)written < output_size;
}

static bool ensure_mapping_directory(void) {
  char directory[MAPPING_PATH_CAPACITY];
  int result;

  if (!join_key_directory(
          directory, sizeof(directory), "mappings")) {
    return false;
  }

  result = sceIoMkdir(directory, 0777);
  return result >= 0 ||
      (uint32_t)result == VITA_ERROR_ALREADY_EXISTS;
}

static const char *configured_mapping_path(void) {
  return config.mapping && config.mapping[0] != '\0'
      ? config.mapping
      : CUSTOM_MAPPING_RELATIVE_PATH;
}

static bool prepare_mapping_file(struct mapping *target,
                                 char *path,
                                 size_t path_size) {
  const char *relative_path = configured_mapping_path();

  if (!target || !path ||
      !ensure_mapping_directory() ||
      !join_key_directory(path, path_size, relative_path)) {
    return false;
  }

  vitainput_default_mapping(target, config.model);
  if (mapping_load(path, target)) {
    return true;
  }
  return mapping_save(path, target);
}

static char *duplicate_string(const char *text) {
  size_t length;
  char *copy;

  if (!text) {
    return NULL;
  }
  length = strlen(text) + 1;
  copy = malloc(length);
  if (copy) {
    memcpy(copy, text, length);
  }
  return copy;
}

bool ui_controller_mapping_set_enabled(bool enabled) {
  struct mapping candidate;
  char path[MAPPING_PATH_CAPACITY];

  if (!enabled) {
    if (config.mapping) {
      free(config.mapping);
      config.mapping = NULL;
    }
    vitainput_default_mapping(&candidate, config.model);
    vitainput_apply_mapping(&candidate);
    return true;
  }

  if (!prepare_mapping_file(&candidate, path, sizeof(path))) {
    return false;
  }

  if (!config.mapping) {
    char *relative_path =
        duplicate_string(CUSTOM_MAPPING_RELATIVE_PATH);
    if (!relative_path) {
      return false;
    }
    config.mapping = relative_path;
  }

  config.swap_shoulder_buttons = false;
  swap_shoulder_buttons = false;
  vitainput_apply_mapping(&candidate);
  return true;
}

typedef struct controller_binding {
  int id;
  char *menu_name;
  char *diagram_name;
  size_t field_offset;
  bool allows_analog_trigger;
  int diagram_x;
  int diagram_y;
} controller_binding;

enum {
  MAPPER_FACE_A = 1000,
  MAPPER_FACE_B,
  MAPPER_FACE_X,
  MAPPER_FACE_Y,
  MAPPER_DPAD_UP,
  MAPPER_DPAD_DOWN,
  MAPPER_DPAD_LEFT,
  MAPPER_DPAD_RIGHT,
  MAPPER_VIEW,
  MAPPER_MENU,
  MAPPER_GUIDE,
  MAPPER_LB,
  MAPPER_RB,
  MAPPER_LT,
  MAPPER_RT,
  MAPPER_LS,
  MAPPER_RS,
  MAPPER_RESET
};

#define CONTROLLER_BINDING(id_value, menu, diagram, field, analog, x, y) \
  {                                                                    \
    (id_value), (menu), (diagram), offsetof(struct mapping, field),     \
    (analog), (x), (y)                                                  \
  }

static controller_binding controller_bindings[] = {
  CONTROLLER_BINDING(
      MAPPER_FACE_A, "A / south", "A", btn_south, false, 820, 284),
  CONTROLLER_BINDING(
      MAPPER_FACE_B, "B / east", "B", btn_east, false, 852, 252),
  CONTROLLER_BINDING(
      MAPPER_FACE_X, "X / west", "X", btn_west, false, 788, 252),
  CONTROLLER_BINDING(
      MAPPER_FACE_Y, "Y / north", "Y", btn_north, false, 820, 220),
  CONTROLLER_BINDING(
      MAPPER_DPAD_UP, "D-pad up", "U", btn_dpad_up, false, 540, 220),
  CONTROLLER_BINDING(
      MAPPER_DPAD_DOWN, "D-pad down", "D", btn_dpad_down, false, 540, 284),
  CONTROLLER_BINDING(
      MAPPER_DPAD_LEFT, "D-pad left", "L", btn_dpad_left, false, 508, 252),
  CONTROLLER_BINDING(
      MAPPER_DPAD_RIGHT, "D-pad right", "R", btn_dpad_right, false, 572, 252),
  CONTROLLER_BINDING(
      MAPPER_VIEW, "View / select", "V", btn_select, false, 648, 252),
  CONTROLLER_BINDING(
      MAPPER_MENU, "Menu / start", "M", btn_start, false, 716, 252),
  CONTROLLER_BINDING(
      MAPPER_GUIDE, "PC Guide", "G", btn_mode, false, 682, 218),
  CONTROLLER_BINDING(
      MAPPER_LB, "LB", "LB", btn_thumbl, false, 510, 168),
  CONTROLLER_BINDING(
      MAPPER_RB, "RB", "RB", btn_thumbr, false, 856, 168),
  CONTROLLER_BINDING(
      MAPPER_LT, "LT", "LT", btn_tl, true, 470, 135),
  CONTROLLER_BINDING(
      MAPPER_RT, "RT", "RT", btn_tr, true, 896, 135),
  CONTROLLER_BINDING(
      MAPPER_LS, "Left stick click", "LS", btn_tl2, false, 610, 326),
  CONTROLLER_BINDING(
      MAPPER_RS, "Right stick click", "RS", btn_tr2, false, 754, 326),
};

typedef struct source_option {
  uint32_t code;
  char *name;
  bool category;
  bool trigger_only;
  bool handheld_only;
} source_option;

#define SOURCE(code_value, label) \
  {(code_value), (label), false, false, false}
#define TRIGGER_SOURCE(code_value, label) \
  {(code_value), (label), false, true, false}
#define HANDHELD_SOURCE(code_value, label) \
  {(code_value), (label), false, false, true}
#define SOURCE_CATEGORY(label) \
  {0, (label), true, false, false}
#define TRIGGER_CATEGORY(label) \
  {0, (label), true, true, false}
#define HANDHELD_CATEGORY(label) \
  {0, (label), true, false, true}

static source_option controller_sources[] = {
  SOURCE(0, "None"),
  SOURCE_CATEGORY("Face buttons"),
  SOURCE(SCE_CTRL_CROSS | INPUT_TYPE_GAMEPAD, "Cross"),
  SOURCE(SCE_CTRL_CIRCLE | INPUT_TYPE_GAMEPAD, "Circle"),
  SOURCE(SCE_CTRL_SQUARE | INPUT_TYPE_GAMEPAD, "Square"),
  SOURCE(SCE_CTRL_TRIANGLE | INPUT_TYPE_GAMEPAD, "Triangle"),
  SOURCE_CATEGORY("Directional pad"),
  SOURCE(SCE_CTRL_UP | INPUT_TYPE_GAMEPAD, "D-pad up"),
  SOURCE(SCE_CTRL_DOWN | INPUT_TYPE_GAMEPAD, "D-pad down"),
  SOURCE(SCE_CTRL_LEFT | INPUT_TYPE_GAMEPAD, "D-pad left"),
  SOURCE(SCE_CTRL_RIGHT | INPUT_TYPE_GAMEPAD, "D-pad right"),
  SOURCE_CATEGORY("Controls and shoulders"),
  SOURCE(SCE_CTRL_SELECT | INPUT_TYPE_GAMEPAD, "Select"),
  SOURCE(SCE_CTRL_START | INPUT_TYPE_GAMEPAD, "Start"),
  SOURCE(SCE_CTRL_L1 | INPUT_TYPE_GAMEPAD, "L1"),
  SOURCE(SCE_CTRL_R1 | INPUT_TYPE_GAMEPAD, "R1"),
  SOURCE(SCE_CTRL_L3 | INPUT_TYPE_GAMEPAD, "L3"),
  SOURCE(SCE_CTRL_R3 | INPUT_TYPE_GAMEPAD, "R3"),
  HANDHELD_CATEGORY("Back touch"),
  HANDHELD_SOURCE(
      TOUCHSEC_NORTHWEST | INPUT_TYPE_TOUCHSCREEN, "Back top-left"),
  HANDHELD_SOURCE(
      TOUCHSEC_NORTHEAST | INPUT_TYPE_TOUCHSCREEN, "Back top-right"),
  HANDHELD_SOURCE(
      TOUCHSEC_SOUTHWEST | INPUT_TYPE_TOUCHSCREEN, "Back bottom-left"),
  HANDHELD_SOURCE(
      TOUCHSEC_SOUTHEAST | INPUT_TYPE_TOUCHSCREEN, "Back bottom-right"),
  TRIGGER_CATEGORY("Analog triggers"),
  TRIGGER_SOURCE(
      LEFT_TRIGGER | INPUT_TYPE_ANALOG, "Analog L2"),
  TRIGGER_SOURCE(
      RIGHT_TRIGGER | INPUT_TYPE_ANALOG, "Analog R2"),
};

static struct mapping controller_mapping;
static char controller_mapping_file[MAPPING_PATH_CAPACITY];
static bool controller_mapper_changed;
static int controller_selected_id = MAPPER_FACE_A;

static controller_binding *find_controller_binding(int id) {
  for (size_t i = 0; i < ARRAY_COUNT(controller_bindings); i++) {
    if (controller_bindings[i].id == id) {
      return &controller_bindings[i];
    }
  }
  return NULL;
}

static uint32_t *controller_binding_value(
    struct mapping *mapping,
    const controller_binding *binding) {
  return (uint32_t *)(
      (unsigned char *)mapping + binding->field_offset);
}

static void controller_source_name(uint32_t code,
                                   char *output,
                                   size_t output_size) {
  for (size_t i = 0; i < ARRAY_COUNT(controller_sources); i++) {
    if (!controller_sources[i].category &&
        controller_sources[i].code == code) {
      snprintf(output, output_size, "%s", controller_sources[i].name);
      return;
    }
  }
  snprintf(output, output_size, "0x%08X", (unsigned int)code);
}

typedef struct source_picker_context {
  bool selected;
  uint32_t value;
} source_picker_context;

static int source_picker_loop(int id,
                              void *context,
                              const input_data *input) {
  source_picker_context *picker = context;

  if ((input->buttons & config.btn_confirm) == 0 ||
      (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }
  picker->value = (uint32_t)id;
  picker->selected = true;
  return 1;
}

static bool select_controller_source(
    const controller_binding *binding,
    uint32_t *selected_value) {
  menu_entry menu[40];
  source_picker_context picker = {0};
  int count = 0;

  for (size_t i = 0; i < ARRAY_COUNT(controller_sources); i++) {
    source_option *source = &controller_sources[i];
    if (source->trigger_only && !binding->allows_analog_trigger) {
      continue;
    }
    if (source->handheld_only &&
        config.model == SCE_KERNEL_MODEL_VITATV) {
      continue;
    }
    menu[count++] = (menu_entry) {
      .id = (int)source->code,
      .name = source->name,
      .disabled = source->category,
      .separator = source->category
    };
  }

  display_menu(
      menu, count, NULL, &source_picker_loop,
      NULL, NULL, &picker);
  if (!picker.selected) {
    return false;
  }
  *selected_value = picker.value;
  return true;
}

static bool persist_controller_mapping(void) {
  if (!mapping_save(controller_mapping_file, &controller_mapping)) {
    display_error(
        "Could not save the custom controller map.\n\n%s",
        controller_mapping_file);
    return false;
  }

  if (config.mapping) {
    config.swap_shoulder_buttons = false;
    swap_shoulder_buttons = false;
    vitainput_apply_mapping(&controller_mapping);
  }
  return true;
}

static void refresh_controller_menu(menu_entry *menu) {
  char source_name[96];

  for (size_t i = 0; i < ARRAY_COUNT(controller_bindings); i++) {
    uint32_t value = *controller_binding_value(
        &controller_mapping, &controller_bindings[i]);
    controller_source_name(
        value, source_name, sizeof(source_name));
    snprintf(
        menu[i].subname, sizeof(menu[i].subname),
        "%s", source_name);
  }
}

static int controller_mapper_loop(int id,
                                  void *context,
                                  const input_data *input) {
  menu_entry *menu = context;
  controller_binding *binding = find_controller_binding(id);

  controller_selected_id = id;
  if ((input->buttons & config.btn_confirm) == 0 ||
      (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }

  if (binding) {
    uint32_t *value =
        controller_binding_value(&controller_mapping, binding);
    uint32_t selected_value;
    uint32_t previous_value = *value;
    if (select_controller_source(binding, &selected_value) &&
        selected_value != previous_value) {
      *value = selected_value;
      if (persist_controller_mapping()) {
        controller_mapper_changed = true;
      } else {
        *value = previous_value;
      }
    }
  } else if (id == MAPPER_RESET &&
             display_confirm(
                 "Restore the hardware-default controller mapping?")) {
    struct mapping defaults;
    vitainput_default_mapping(&defaults, config.model);
    if (memcmp(
            &defaults, &controller_mapping,
            sizeof(controller_mapping)) != 0) {
      struct mapping previous = controller_mapping;
      controller_mapping = defaults;
      if (persist_controller_mapping()) {
        controller_mapper_changed = true;
      } else {
        controller_mapping = previous;
      }
    }
  }

  refresh_controller_menu(menu);
  return 0;
}

static void draw_panel(int x, int y, int width, int height) {
  vita2d_draw_rectangle(x, y, width, height, PANEL_COLOR);
  vita2d_draw_line(x, y, x + width, y, PANEL_BORDER_COLOR);
  vita2d_draw_line(
      x, y + height, x + width, y + height, PANEL_BORDER_COLOR);
  vita2d_draw_line(x, y, x, y + height, PANEL_BORDER_COLOR);
  vita2d_draw_line(
      x + width, y, x + width, y + height, PANEL_BORDER_COLOR);
}

static void draw_fitted_text(int x,
                             int baseline_y,
                             int max_width,
                             int font_size,
                             unsigned int color,
                             const char *text) {
  char fitted[256];
  guilib_fit_text(
      fitted, sizeof(fitted), text, font_size, max_width);
  vita2d_font_draw_text(
      font, x, baseline_y, color, font_size, fitted);
}

static void draw_controller_marker(
    const controller_binding *binding) {
  bool selected = binding->id == controller_selected_id;
  unsigned int fill =
      selected ? ACTIVE_COLOR : RGBA8(42, 59, 70, 255);
  unsigned int text =
      selected ? RGBA8(5, 20, 12, 255) : TEXT_COLOR;
  int radius = strlen(binding->diagram_name) > 1 ? 19 : 17;
  int width;

  vita2d_draw_fill_circle(
      binding->diagram_x, binding->diagram_y, radius, fill);
  width = vita2d_font_text_width(
      font, 15, binding->diagram_name);
  vita2d_font_draw_text(
      font,
      binding->diagram_x - width / 2,
      binding->diagram_y + 6,
      text,
      15,
      binding->diagram_name);
}

static void controller_mapper_draw(void) {
  controller_binding *selected =
      find_controller_binding(controller_selected_id);
  char source_name[96];
  char status[256];
  const int panel_x = 420;
  const int panel_y = 52;
  const int panel_width = 526;
  const int panel_height = 440;

  draw_panel(
      panel_x, panel_y, panel_width, panel_height);
  draw_fitted_text(
      panel_x + 16, panel_y + 29, panel_width - 32,
      20, TEXT_COLOR, "Remote controller layout");
  draw_fitted_text(
      panel_x + 16, panel_y + 52, panel_width - 32,
      16, MUTED_TEXT_COLOR,
      "Choose the Vita input sent for each PC button.");

  vita2d_draw_fill_circle(
      520, 252, 90, RGBA8(31, 43, 52, 255));
  vita2d_draw_fill_circle(
      840, 252, 90, RGBA8(31, 43, 52, 255));
  vita2d_draw_rectangle(
      520, 162, 320, 180, RGBA8(31, 43, 52, 255));
  vita2d_draw_rectangle(
      594, 177, 176, 102, RGBA8(8, 13, 18, 255));

  for (size_t i = 0; i < ARRAY_COUNT(controller_bindings); i++) {
    draw_controller_marker(&controller_bindings[i]);
  }

  if (selected) {
    uint32_t value =
        *controller_binding_value(&controller_mapping, selected);
    controller_source_name(
        value, source_name, sizeof(source_name));
    snprintf(
        status, sizeof(status), "%s  <-  %s",
        selected->menu_name, source_name);
    draw_fitted_text(
        panel_x + 16, panel_y + 338, panel_width - 32,
        18, ACTIVE_COLOR, status);
  } else {
    draw_fitted_text(
        panel_x + 16, panel_y + 338, panel_width - 32,
        18, ACTIVE_COLOR, "Restore every target to its default.");
  }

  draw_fitted_text(
      panel_x + 16, panel_y + 367, panel_width - 32,
      16, TEXT_COLOR,
      config.mapping
          ? "Saved changes apply now; no reconnect is needed."
          : "Saved to vita.conf; enable Custom mapping to apply.");
  draw_fitted_text(
      panel_x + 16, panel_y + 394, panel_width - 32,
      16, MUTED_TEXT_COLOR,
      "Unknown file values stay unchanged until edited.");
  draw_fitted_text(
      panel_x + 16, panel_y + 421, panel_width - 32,
      16, MUTED_TEXT_COLOR,
      "Confirm: choose input   Cancel: back");
}

bool ui_controller_mapper_menu(void) {
  menu_entry menu[24];
  menu_geom geometry = {
    .x = 14,
    .y = 52,
    .width = 390,
    .height = 440,
    .el = 23,
    .total_y = 492
  };
  int count = 0;

  if (!prepare_mapping_file(
          &controller_mapping,
          controller_mapping_file,
          sizeof(controller_mapping_file))) {
    display_error(
        "Could not create or load the writable controller map.\n\n%s",
        config.key_dir);
    return false;
  }

  controller_mapper_changed = false;
  controller_selected_id = MAPPER_FACE_A;
  for (size_t i = 0; i < ARRAY_COUNT(controller_bindings); i++) {
    menu[count++] = (menu_entry) {
      .id = controller_bindings[i].id,
      .name = controller_bindings[i].menu_name,
      .suffix = ""
    };
  }
  menu[count++] = (menu_entry) {
    .id = MAPPER_RESET,
    .name = "Reset to hardware defaults"
  };
  refresh_controller_menu(menu);

  display_menu(
      menu, count, &geometry, &controller_mapper_loop,
      NULL, &controller_mapper_draw, menu);
  return controller_mapper_changed;
}

typedef struct action_option {
  uint32_t code;
  char *name;
  bool category;
} action_option;

#define ACTION(code_value, label) {(code_value), (label), false}
#define ACTION_CATEGORY(label) {0, (label), true}

static action_option front_actions[] = {
  ACTION(0, "None"),
  ACTION_CATEGORY("Local actions"),
  ACTION(
      INPUT_SPECIAL_KEY_PAUSE | INPUT_TYPE_SPECIAL,
      "Open stream menu"),
  ACTION(
      INPUT_SPECIAL_KEY_KEYBOARD | INPUT_TYPE_SPECIAL,
      "Open keyboard"),
  ACTION_CATEGORY("PC gamepad"),
  ACTION(A_FLAG | INPUT_TYPE_GAMEPAD, "A"),
  ACTION(B_FLAG | INPUT_TYPE_GAMEPAD, "B"),
  ACTION(X_FLAG | INPUT_TYPE_GAMEPAD, "X"),
  ACTION(Y_FLAG | INPUT_TYPE_GAMEPAD, "Y"),
  ACTION(UP_FLAG | INPUT_TYPE_GAMEPAD, "D-pad up"),
  ACTION(DOWN_FLAG | INPUT_TYPE_GAMEPAD, "D-pad down"),
  ACTION(LEFT_FLAG | INPUT_TYPE_GAMEPAD, "D-pad left"),
  ACTION(RIGHT_FLAG | INPUT_TYPE_GAMEPAD, "D-pad right"),
  ACTION(BACK_FLAG | INPUT_TYPE_GAMEPAD, "View / Back"),
  ACTION(PLAY_FLAG | INPUT_TYPE_GAMEPAD, "Menu / Start"),
  ACTION(SPECIAL_FLAG | INPUT_TYPE_GAMEPAD, "PC Guide"),
  ACTION(LB_FLAG | INPUT_TYPE_GAMEPAD, "LB"),
  ACTION(RB_FLAG | INPUT_TYPE_GAMEPAD, "RB"),
  ACTION(LS_CLK_FLAG | INPUT_TYPE_GAMEPAD, "Left stick click"),
  ACTION(RS_CLK_FLAG | INPUT_TYPE_GAMEPAD, "Right stick click"),
  ACTION(LEFT_TRIGGER | INPUT_TYPE_ANALOG, "LT"),
  ACTION(RIGHT_TRIGGER | INPUT_TYPE_ANALOG, "RT"),
  ACTION_CATEGORY("Mouse buttons"),
  ACTION(BUTTON_LEFT | INPUT_TYPE_MOUSE, "Mouse left"),
  ACTION(BUTTON_RIGHT | INPUT_TYPE_MOUSE, "Mouse right"),
  ACTION(BUTTON_MIDDLE | INPUT_TYPE_MOUSE, "Mouse middle"),
  ACTION(BUTTON_X1 | INPUT_TYPE_MOUSE, "Mouse X1"),
  ACTION(BUTTON_X2 | INPUT_TYPE_MOUSE, "Mouse X2"),
  ACTION_CATEGORY("Keyboard"),
  ACTION(27, "Esc"),
  ACTION(73, "I"),
  ACTION(77, "M"),
  ACTION(9, "Tab"),
  ACTION(112, "F1"),
  ACTION(113, "F2"),
  ACTION(114, "F3"),
  ACTION(115, "F4"),
  ACTION(116, "F5"),
  ACTION(117, "F6"),
  ACTION(118, "F7"),
  ACTION(119, "F8"),
  ACTION(120, "F9"),
  ACTION(121, "F10"),
  ACTION(122, "F11"),
  ACTION(123, "F12"),
};

enum {
  FRONT_MAPPER_ENABLED = 2000,
  FRONT_MAPPER_OFFSET,
  FRONT_MAPPER_SIZE,
  FRONT_MAPPER_NW,
  FRONT_MAPPER_NE,
  FRONT_MAPPER_SW,
  FRONT_MAPPER_SE,
  FRONT_MAPPER_RESET,
  FRONT_ACTION_MANUAL = -2000
};

static bool front_mapper_changed;
static int front_selected_id = FRONT_MAPPER_ENABLED;

static void front_action_name(uint32_t code,
                              char *output,
                              size_t output_size) {
  for (size_t i = 0; i < ARRAY_COUNT(front_actions); i++) {
    if (!front_actions[i].category &&
        front_actions[i].code == code) {
      snprintf(output, output_size, "%s", front_actions[i].name);
      return;
    }
  }
  snprintf(output, output_size, "Code 0x%08X", (unsigned int)code);
}

typedef struct action_picker_context {
  bool selected;
  uint32_t value;
} action_picker_context;

static int action_picker_loop(int id,
                              void *context,
                              const input_data *input) {
  action_picker_context *picker = context;

  if ((input->buttons & config.btn_confirm) == 0 ||
      (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }

  if (id == FRONT_ACTION_MANUAL) {
    char text[64];
    char *end = NULL;
    unsigned long value;

    if (ime_dialog_number(
            text, sizeof(text), "Enter keyboard key code:", "") != 0) {
      return 0;
    }
    value = strtoul(text, &end, 10);
    if (!text[0] || !end || *end != '\0' ||
        value > INPUT_VALUE_MASK) {
      display_error("Invalid key code: %s", text);
      return 0;
    }
    picker->value = (uint32_t)value;
  } else {
    picker->value = (uint32_t)id;
  }

  picker->selected = true;
  return 1;
}

static bool select_front_action(uint32_t *selected_value) {
  menu_entry menu[64];
  action_picker_context picker = {0};
  int count = 0;

  for (size_t i = 0; i < ARRAY_COUNT(front_actions); i++) {
    menu[count++] = (menu_entry) {
      .id = (int)front_actions[i].code,
      .name = front_actions[i].name,
      .disabled = front_actions[i].category,
      .separator = front_actions[i].category
    };
  }
  menu[count++] = (menu_entry) {
    .name = "",
    .disabled = true,
    .separator = true
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_ACTION_MANUAL,
    .name = "Enter keyboard code"
  };

  display_menu(
      menu, count, NULL, &action_picker_loop,
      NULL, NULL, &picker);
  if (!picker.selected) {
    return false;
  }
  *selected_value = picker.value;
  return true;
}

static unsigned int *front_action_value(int id) {
  switch (id) {
    case FRONT_MAPPER_NW:
      return &config.special_keys.nw;
    case FRONT_MAPPER_NE:
      return &config.special_keys.ne;
    case FRONT_MAPPER_SW:
      return &config.special_keys.sw;
    case FRONT_MAPPER_SE:
      return &config.special_keys.se;
    default:
      return NULL;
  }
}

static void refresh_front_menu(menu_entry *menu) {
  char action_name[96];

  snprintf(
      menu[0].subname, sizeof(menu[0].subname), "%s",
      config.enable_front_touchzones ? "yes" : "no");
  snprintf(
      menu[1].subname, sizeof(menu[1].subname), "%d px",
      config.special_keys.offset);
  snprintf(
      menu[2].subname, sizeof(menu[2].subname), "%d px",
      config.special_keys.size);

  front_action_name(
      config.special_keys.nw, action_name, sizeof(action_name));
  snprintf(menu[4].subname, sizeof(menu[4].subname), "%s", action_name);
  front_action_name(
      config.special_keys.ne, action_name, sizeof(action_name));
  snprintf(menu[5].subname, sizeof(menu[5].subname), "%s", action_name);
  front_action_name(
      config.special_keys.sw, action_name, sizeof(action_name));
  snprintf(menu[6].subname, sizeof(menu[6].subname), "%s", action_name);
  front_action_name(
      config.special_keys.se, action_name, sizeof(action_name));
  snprintf(menu[7].subname, sizeof(menu[7].subname), "%s", action_name);
}

static void note_front_change(void) {
  config_sanitize(&config);
  vitainput_refresh_touchzones();
  front_mapper_changed = true;
}

static int front_mapper_loop(int id,
                             void *context,
                             const input_data *input) {
  menu_entry *menu = context;
  bool left = (input->buttons & SCE_CTRL_LEFT) != 0;
  bool right = (input->buttons & SCE_CTRL_RIGHT) != 0;
  bool confirm =
      (input->buttons & config.btn_confirm) != 0 &&
      (input->buttons & SCE_CTRL_HOLD) == 0;
  bool changed = false;

  front_selected_id = id;
  switch (id) {
    case FRONT_MAPPER_ENABLED:
      if (confirm) {
        config.enable_front_touchzones =
            !config.enable_front_touchzones;
        changed = true;
      }
      break;
    case FRONT_MAPPER_OFFSET:
      if (left || right) {
        int previous = config.special_keys.offset;
        config.special_keys.offset += left ? -8 : 8;
        config_sanitize(&config);
        changed = previous != config.special_keys.offset;
      }
      break;
    case FRONT_MAPPER_SIZE:
      if (left || right) {
        int previous = config.special_keys.size;
        config.special_keys.size += left ? -8 : 8;
        config_sanitize(&config);
        changed = previous != config.special_keys.size;
      }
      break;
    case FRONT_MAPPER_NW:
    case FRONT_MAPPER_NE:
    case FRONT_MAPPER_SW:
    case FRONT_MAPPER_SE:
      if (confirm) {
        unsigned int *action = front_action_value(id);
        uint32_t selected_value;
        if (action &&
            select_front_action(&selected_value) &&
            *action != selected_value) {
          *action = selected_value;
          changed = true;
        }
      }
      break;
    case FRONT_MAPPER_RESET:
      if (confirm &&
          display_confirm(
              "Restore default front-touch zones and actions?")) {
        config.special_keys.offset = 0;
        config.special_keys.size = 150;
        config.special_keys.nw =
            INPUT_SPECIAL_KEY_PAUSE | INPUT_TYPE_SPECIAL;
        config.special_keys.ne = 0;
        config.special_keys.sw =
            SPECIAL_FLAG | INPUT_TYPE_GAMEPAD;
        config.special_keys.se = 0;
        changed = true;
      }
      break;
  }

  if (changed) {
    note_front_change();
  }
  refresh_front_menu(menu);
  return 0;
}

static int front_selected_zone(void) {
  switch (front_selected_id) {
    case FRONT_MAPPER_NW:
      return 0;
    case FRONT_MAPPER_NE:
      return 1;
    case FRONT_MAPPER_SW:
      return 2;
    case FRONT_MAPPER_SE:
      return 3;
    default:
      return -1;
  }
}

static void draw_zone_border(int x,
                             int y,
                             int width,
                             int height,
                             unsigned int color) {
  vita2d_draw_line(x, y, x + width, y, color);
  vita2d_draw_line(x, y + height, x + width, y + height, color);
  vita2d_draw_line(x, y, x, y + height, color);
  vita2d_draw_line(x + width, y, x + width, y + height, color);
}

static void front_mapper_draw(void) {
  static char *corner_names[] = {"TL", "TR", "BL", "BR"};
  unsigned int actions[] = {
    config.special_keys.nw,
    config.special_keys.ne,
    config.special_keys.sw,
    config.special_keys.se
  };
  const int panel_x = 420;
  const int panel_y = 52;
  const int panel_width = 526;
  const int panel_height = 440;
  const int screen_x = 449;
  const int screen_y = 123;
  const int screen_width = 468;
  const int screen_height = 265;
  int zone_size_x;
  int zone_size_y;
  int offset_x;
  int offset_y;
  int selected_zone = front_selected_zone();
  int zone_x[4];
  int zone_y[4];
  char action_name[96];
  char status[192];

  draw_panel(
      panel_x, panel_y, panel_width, panel_height);
  draw_fitted_text(
      panel_x + 16, panel_y + 29, panel_width - 32,
      20, TEXT_COLOR, "Front-touch zone preview");
  draw_fitted_text(
      panel_x + 16, panel_y + 52, panel_width - 32,
      16,
      config.enable_front_touchzones
          ? ACTIVE_COLOR
          : MUTED_TEXT_COLOR,
      config.enable_front_touchzones
          ? "Zones are active during streaming."
          : "Zones are disabled; layout is still editable.");

  vita2d_draw_rectangle(
      screen_x - 3, screen_y - 3,
      screen_width + 6, screen_height + 6,
      RGBA8(72, 86, 96, 255));
  vita2d_draw_rectangle(
      screen_x, screen_y,
      screen_width, screen_height,
      RGBA8(4, 8, 12, 255));

  zone_size_x =
      config.special_keys.size * screen_width / WIDTH;
  zone_size_y =
      config.special_keys.size * screen_height / HEIGHT;
  offset_x =
      config.special_keys.offset * screen_width / WIDTH;
  offset_y =
      config.special_keys.offset * screen_height / HEIGHT;
  if (zone_size_x < 1) {
    zone_size_x = 1;
  }
  if (zone_size_y < 1) {
    zone_size_y = 1;
  }

  zone_x[0] = screen_x + offset_x;
  zone_y[0] = screen_y + offset_y;
  zone_x[1] =
      screen_x + screen_width - offset_x - zone_size_x;
  zone_y[1] = screen_y + offset_y;
  zone_x[2] = screen_x + offset_x;
  zone_y[2] =
      screen_y + screen_height - offset_y - zone_size_y;
  zone_x[3] =
      screen_x + screen_width - offset_x - zone_size_x;
  zone_y[3] =
      screen_y + screen_height - offset_y - zone_size_y;

  for (int i = 0; i < 4; i++) {
    bool selected =
        selected_zone == i ||
        front_selected_id == FRONT_MAPPER_OFFSET ||
        front_selected_id == FRONT_MAPPER_SIZE;
    unsigned int fill =
        selected ? TOUCH_ZONE_ACTIVE_COLOR : TOUCH_ZONE_COLOR;
    unsigned int border =
        selected ? ACTIVE_COLOR : PANEL_BORDER_COLOR;
    int label_width;

    vita2d_draw_rectangle(
        zone_x[i], zone_y[i], zone_size_x, zone_size_y, fill);
    draw_zone_border(
        zone_x[i], zone_y[i], zone_size_x, zone_size_y, border);
    label_width =
        vita2d_font_text_width(font, 14, corner_names[i]);
    if (zone_size_x >= label_width + 6 && zone_size_y >= 18) {
      vita2d_font_draw_text(
          font,
          zone_x[i] + (zone_size_x - label_width) / 2,
          zone_y[i] + 16,
          TEXT_COLOR,
          14,
          corner_names[i]);
    }
  }

  {
    SceTouchData touch_data = {0};
    int report_count;
    int report_capacity;

    sceTouchPeek(SCE_TOUCH_PORT_FRONT, &touch_data, 1);
    report_count = touch_data.reportNum;
    report_capacity =
        (int)(sizeof(touch_data.report) /
              sizeof(touch_data.report[0]));
    if (report_count > report_capacity) {
      report_count = report_capacity;
    }
    for (int i = 0; i < report_count; i++) {
      int x =
          screen_x +
          touch_data.report[i].x * screen_width / 1919;
      int y =
          screen_y +
          touch_data.report[i].y * screen_height / 1087;
      vita2d_draw_fill_circle(x, y, 9, TEXT_COLOR);
      vita2d_draw_fill_circle(x, y, 5, ACTIVE_COLOR);
    }
  }

  if (selected_zone >= 0) {
    front_action_name(
        actions[selected_zone],
        action_name,
        sizeof(action_name));
    snprintf(
        status, sizeof(status), "%s: %s",
        corner_names[selected_zone], action_name);
  } else {
    snprintf(
        status, sizeof(status), "Inset %d px   Zone %d px",
        config.special_keys.offset,
        config.special_keys.size);
  }
  draw_fitted_text(
      panel_x + 16, panel_y + 365, panel_width - 32,
      18, ACTIVE_COLOR, status);
  draw_fitted_text(
      panel_x + 16, panel_y + 394, panel_width - 32,
      16, TEXT_COLOR,
      "White dots show live touches; corners consume them.");
  draw_fitted_text(
      panel_x + 16, panel_y + 421, panel_width - 32,
      16, MUTED_TEXT_COLOR,
      "Left/right: resize   Confirm: choose action");
}

static int front_mapper_back(void *context) {
  (void)context;
  config_sanitize(&config);
  vitainput_refresh_touchzones();
  return 0;
}

bool ui_front_touch_mapper_menu(void) {
  menu_entry menu[16];
  menu_geom geometry = {
    .x = 14,
    .y = 88,
    .width = 390,
    .height = 318,
    .el = 25,
    .total_y = 406
  };
  int count = 0;

  config_sanitize(&config);
  sceTouchSetSamplingState(
      SCE_TOUCH_PORT_FRONT, SCE_TOUCH_SAMPLING_STATE_START);
  front_mapper_changed = false;
  front_selected_id = FRONT_MAPPER_ENABLED;

  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_ENABLED,
    .name = "Enabled"
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_OFFSET,
    .name = "Edge inset",
    .suffix = ICON_LEFT_RIGHT_ARROWS
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_SIZE,
    .name = "Zone size",
    .suffix = ICON_LEFT_RIGHT_ARROWS
  };
  menu[count++] = (menu_entry) {
    .name = "Corner actions",
    .disabled = true,
    .separator = true
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_NW,
    .name = "Top-left"
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_NE,
    .name = "Top-right"
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_SW,
    .name = "Bottom-left"
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_SE,
    .name = "Bottom-right"
  };
  menu[count++] = (menu_entry) {
    .id = FRONT_MAPPER_RESET,
    .name = "Reset zones and actions"
  };
  refresh_front_menu(menu);

  display_menu(
      menu, count, &geometry, &front_mapper_loop,
      &front_mapper_back, &front_mapper_draw, menu);
  return front_mapper_changed;
}
