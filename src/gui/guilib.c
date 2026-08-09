#include "guilib.h"

#include "../config.h"
#include "../platform.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>

#include <psp2/net/net.h>
#include <psp2/sysmodule.h>

#include <psp2/ctrl.h>
#include <psp2/touch.h>
#include <psp2/rtc.h>
#include <psp2/power.h>

#define BUTTON_DELAY 150 * 1000
#define MENU_FONT_SIZE 18
#define MENU_TEXT_GAP 12
#define ALERT_FONT_SIZE 18
#define ALERT_LINE_HEIGHT 23
#define ALERT_HORIZONTAL_PADDING 20
#define ALERT_TOP_PADDING 22
#define ALERT_BOTTOM_PADDING 48

static gui_draw_callback gui_global_draw_callback;
static gui_loop_callback gui_global_loop_callback;

vita2d_font *font;

menu_geom make_geom_centered(int w, int h) {
  menu_geom geom = {0};
  geom.x = WIDTH  / 2 - w / 2;
  geom.y = HEIGHT / 2 - h / 2;
  geom.width = w;
  geom.height = h;
  geom.total_y = geom.y + geom.height;
  geom.el = 24;
  return geom;
}

void draw_border(menu_geom geom, unsigned int border_color) {
  vita2d_draw_line(geom.x, geom.y, geom.x+geom.width, geom.y, border_color);
  vita2d_draw_line(geom.x, geom.y, geom.x, geom.y+geom.height, border_color);
  vita2d_draw_line(geom.x+geom.width, geom.y, geom.x+geom.width, geom.y+geom.height, border_color);
  vita2d_draw_line(geom.x, geom.y+geom.height, geom.x+geom.width, geom.y+geom.height, border_color);
}

void draw_text_hcentered(int x, int y, unsigned int color, char *text) {
  int width = vita2d_font_text_width(font, 18, text);
  vita2d_font_draw_text(font, x - width / 2, y, color, 18, text);
}

static size_t utf8_codepoint_size(const char *text) {
  const unsigned char lead = (unsigned char)text[0];
  size_t expected = 1;

  if ((lead & 0xe0) == 0xc0) {
    expected = 2;
  } else if ((lead & 0xf0) == 0xe0) {
    expected = 3;
  } else if ((lead & 0xf8) == 0xf0) {
    expected = 4;
  }

  for (size_t i = 1; i < expected; i++) {
    if (text[i] == '\0' ||
        ((unsigned char)text[i] & 0xc0) != 0x80) {
      return 1;
    }
  }
  return expected;
}

int guilib_fit_text(char *output,
                    size_t output_size,
                    const char *text,
                    int font_size,
                    int max_width) {
  static const char ellipsis[] = "...";
  size_t input_length;
  size_t input_offset = 0;
  size_t output_length = 0;
  int ellipsis_width;

  if (!output || output_size == 0) {
    return 0;
  }
  output[0] = '\0';
  if (!text || max_width <= 0) {
    return 0;
  }

  input_length = strlen(text);
  if (input_length < output_size &&
      vita2d_font_text_width(font, font_size, text) <= max_width) {
    memcpy(output, text, input_length + 1);
    return vita2d_font_text_width(font, font_size, output);
  }

  ellipsis_width = vita2d_font_text_width(font, font_size, ellipsis);
  if (ellipsis_width > max_width || output_size < sizeof(ellipsis)) {
    return 0;
  }

  while (input_offset < input_length) {
    size_t codepoint_size = utf8_codepoint_size(text + input_offset);
    if (output_length + codepoint_size + sizeof(ellipsis) > output_size) {
      break;
    }

    memcpy(output + output_length, text + input_offset, codepoint_size);
    output_length += codepoint_size;
    output[output_length] = '\0';
    if (vita2d_font_text_width(font, font_size, output) >
        max_width - ellipsis_width) {
      output_length -= codepoint_size;
      output[output_length] = '\0';
      break;
    }
    input_offset += codepoint_size;
  }

  while (output_length > 0 && output[output_length - 1] == ' ') {
    output[--output_length] = '\0';
  }
  memcpy(output + output_length, ellipsis, sizeof(ellipsis));
  return vita2d_font_text_width(font, font_size, output);
}

static int battery_percent;
static bool battery_charging;
static SceRtcTick battery_tick;

void draw_statusbar(menu_geom geom) {
  SceRtcTick current_tick;
  sceRtcGetCurrentTick(&current_tick);
  if (current_tick.tick - battery_tick.tick > 10 * 1000 * 1000) {
    battery_percent = scePowerGetBatteryLifePercent();
    battery_charging = scePowerIsBatteryCharging();

    battery_tick = current_tick;
  }

  SceDateTime time;
  sceRtcGetCurrentClockLocalTime(&time);

  char dt_text[256];
  sprintf(dt_text, "%02d:%02d", time.hour, time.minute);
  int dt_width = vita2d_font_text_width(font, 18, dt_text);
  int battery_width = 30,
      battery_height = 16,
      battery_padding = 2,
      battery_plus_height = 4,
      battery_y_offset = 4,
      battery_charge_width = (float) battery_percent / 100 * battery_width;
  unsigned int battery_color = battery_charging ? 0xff99ffff : (battery_percent < 20 ? 0xff0000ff : 0xff00ff00);

  vita2d_font_draw_text(font, geom.x + geom.width - dt_width - battery_width - 5, geom.y - 5, 0xffffffff, 18, dt_text);

  vita2d_draw_rectangle(
      geom.x + geom.width - battery_width,
      geom.y - battery_height - battery_y_offset,
      battery_width,
      battery_height,
      0xffffffff);

  vita2d_draw_rectangle(
      geom.x + geom.width - battery_width - battery_padding,
      geom.y - battery_y_offset - ((float) battery_height / 2 + (float) battery_plus_height / 2),
      battery_padding,
      battery_plus_height,
      0xffffffff
      );

  vita2d_draw_rectangle(
      geom.x + geom.width - battery_width + battery_padding,
      geom.y - battery_height + battery_padding - battery_y_offset,
      battery_width - battery_padding * 2,
      battery_height - battery_padding * 2,
      0xff000000);

  vita2d_draw_rectangle(
      geom.x + geom.width - battery_charge_width + battery_padding,
      geom.y - battery_height + battery_padding - battery_y_offset,
      battery_charge_width - battery_padding * 2,
      battery_height - battery_padding * 2,
      battery_color);
}

SceTouchData touch_data;
SceCtrlData ctrl_new_pad;

static SceRtcTick button_current_tick, button_until_tick;
bool was_button_pressed(short id) {
  sceRtcGetCurrentTick(&button_current_tick);

  if (ctrl_new_pad.buttons & id) {
    if (sceRtcCompareTick(&button_current_tick, &button_until_tick) > 0) {
      sceRtcTickAddMicroseconds(&button_until_tick, &button_current_tick, BUTTON_DELAY);
      return true;
    }
  }

  return false;
}

bool is_button_down(short id) {
  return ctrl_new_pad.buttons & id;
}

#define lerp(value, from_max, to_max) ((((value*10) * (to_max*10))/(from_max*10))/10)
bool is_rectangle_touched(const SceTouchData *touch, int lx, int ly, int rx, int ry) {
  if (!touch) {
    return false;
  }
  int report_count = touch->reportNum;
  int report_capacity =
      (int)(sizeof(touch->report) / sizeof(touch->report[0]));
  if (report_count > report_capacity) {
    report_count = report_capacity;
  }
  for (int i = 0; i < report_count; i++) {
    int x = lerp(touch->report[i].x, 1919, WIDTH);
    int y = lerp(touch->report[i].y, 1087, HEIGHT);
    if (x < lx || x > rx || y < ly || y > ry) continue;
    return true;
  }

  return false;
}

void draw_menu(menu_entry menu[],
               int total_elements,
               menu_geom geom,
               int cursor,
               int offset) {
  vita2d_draw_rectangle(
      geom.x, geom.y, geom.width, geom.height, RGBA8(10, 16, 24, 230));

  draw_border(geom, 0xff006000);
  draw_statusbar(geom);

  for (int i = 0, cursor_idx = 0; i < total_elements; i++) {
    const char *name = menu[i].name ? menu[i].name : "";
    char label_text[1024];
    char suffix_text[256];
    char subname_text[1024];
    long text_color = 0xffffffff;
    unsigned int dot_color =
        menu[i].color ? menu[i].color : 0xffaaaaaa;
    int row_top = geom.y + i * geom.el - offset;
    int el_x = geom.x + 10;
    int baseline_y = row_top + (geom.el + MENU_FONT_SIZE) / 2 - 1;
    int label_x = el_x + 2;
    int right_edge = geom.x + geom.width - 10;
    int right_cursor = right_edge;
    int label_width = 0;

    if (cursor == cursor_idx) {
      text_color = 0xff00ff00;
    }
    if (!menu[i].disabled) {
      cursor_idx++;
    } else {
      text_color = 0xffaaaaaa;
    }

    if (row_top < geom.y || row_top > geom.total_y - geom.el) {
      continue;
    }

    /*
     * Informational rows intentionally have no label. Draw their message from
     * the left instead of right-aligning it into another row's visual space.
     */
    if (name[0] == '\0' && menu[i].subname[0] != '\0') {
      guilib_fit_text(
          subname_text, sizeof(subname_text), menu[i].subname,
          MENU_FONT_SIZE, right_edge - label_x);
      vita2d_font_draw_text(
          font, label_x, baseline_y, text_color,
          MENU_FONT_SIZE, subname_text);
      continue;
    }

    if (menu[i].suffix && menu[i].suffix[0] != '\0') {
      int suffix_width = guilib_fit_text(
          suffix_text, sizeof(suffix_text), menu[i].suffix,
          MENU_FONT_SIZE, geom.width / 3);
      if (suffix_width > 0) {
        vita2d_font_draw_text(
            font, right_cursor - suffix_width, baseline_y,
            text_color, MENU_FONT_SIZE, suffix_text);
        right_cursor -= suffix_width + 10;
      }
    }

    if (menu[i].subname[0] != '\0') {
      int subname_width = guilib_fit_text(
          subname_text, sizeof(subname_text), menu[i].subname,
          MENU_FONT_SIZE, geom.width / 2);
      if (subname_width > 0) {
        vita2d_font_draw_text(
            font, right_cursor - subname_width, baseline_y,
            text_color, MENU_FONT_SIZE, subname_text);
        right_cursor -= subname_width + MENU_TEXT_GAP;
      }
    }

    if (menu[i].is_host_entry && name[0] != '\0') {
      int dot_radius = 8;
      int dot_x = el_x + dot_radius;
      int dot_y = row_top + geom.el / 2;
      unsigned int visible_dot_color =
          (dot_color & 0x00ffffff) | 0xff000000;
      vita2d_draw_fill_circle(
          dot_x, dot_y, dot_radius + 2, 0xff000000);
      vita2d_draw_fill_circle(
          dot_x, dot_y, dot_radius, visible_dot_color);
      label_x = dot_x + dot_radius + 6;
    }

    if (name[0] != '\0') {
      int label_max_width =
          right_cursor - label_x - MENU_TEXT_GAP;
      label_width = guilib_fit_text(
          label_text, sizeof(label_text), name,
          MENU_FONT_SIZE, label_max_width);
      if (label_width > 0) {
        vita2d_font_draw_text(
            font, label_x, baseline_y, text_color,
            MENU_FONT_SIZE, label_text);
      }
    }

    if (menu[i].separator) {
      int border = name[0] != '\0' ? 7 : 0;
      int line_start = label_x + label_width + border;
      int line_y =
          name[0] != '\0' ? baseline_y : row_top + geom.el / 2;
      if (line_start < right_edge) {
        vita2d_draw_line(
            line_start, line_y, right_edge, line_y, 0xffaaaaaa);
      }
    }
  }
}

#define ALERT_MAX_LINES 128

typedef struct alert_lines {
  char *storage;
  char *items[ALERT_MAX_LINES];
  int count;
} alert_lines;

static void alert_lines_free(alert_lines *lines) {
  free(lines->storage);
  lines->storage = NULL;
  lines->count = 0;
}

static void alert_lines_add(
    alert_lines *lines,
    char *line) {
  if (lines->count < ALERT_MAX_LINES) {
    lines->items[lines->count++] = line;
  }
}

/*
 * Wraps in-place at ASCII spaces while leaving UTF-8 code points intact.
 * Exceptionally long words are later ellipsized by the renderer.
 */
static alert_lines wrap_alert_text(
    const char *message,
    int max_width) {
  alert_lines lines = {0};
  char *line_start;
  char *cursor;
  char *last_space = NULL;

  if (!message) {
    message = "";
  }
  lines.storage = malloc(strlen(message) + 1);
  if (!lines.storage) {
    return lines;
  }
  strcpy(lines.storage, message);

  line_start = lines.storage;
  cursor = lines.storage;
  while (*cursor != '\0' && lines.count < ALERT_MAX_LINES) {
    if (*cursor == '\n') {
      *cursor = '\0';
      alert_lines_add(&lines, line_start);
      line_start = ++cursor;
      last_space = NULL;
      continue;
    }

    {
      size_t codepoint_size = utf8_codepoint_size(cursor);
      char *candidate_end = cursor + codepoint_size;
      char saved = *candidate_end;
      if (*cursor == ' ' || *cursor == '\t') {
        last_space = cursor;
      }
      *candidate_end = '\0';
      int line_width = vita2d_font_text_width(
          font, ALERT_FONT_SIZE, line_start);
      *candidate_end = saved;

      if (line_width > max_width && last_space) {
        *last_space = '\0';
        alert_lines_add(&lines, line_start);
        line_start = last_space + 1;
        while (*line_start == ' ' || *line_start == '\t') {
          line_start++;
        }
        cursor = line_start;
        last_space = NULL;
      } else {
        cursor = candidate_end;
      }
    }
  }

  if (lines.count < ALERT_MAX_LINES) {
    alert_lines_add(&lines, line_start);
  }
  return lines;
}

static int alert_visible_line_count(menu_geom geom) {
  int available =
      geom.height - ALERT_TOP_PADDING - ALERT_BOTTOM_PADDING;
  int count = available / ALERT_LINE_HEIGHT;
  return count > 0 ? count : 1;
}

static void draw_alert_lines(
    const alert_lines *lines,
    int first_line,
    menu_geom geom,
    char *buttons_captions[],
    int buttons_count) {
  char caption[256] = {0};
  char fitted_caption[256];
  char scroll_text[80];
  char fitted_scroll[80];
  const char *o_layout[4] =
      {"O", "X", "Triangle", "Square"};
  const char *x_layout[4] =
      {"X", "O", "Triangle", "Square"};
  const char **icons =
      config.jp_layout ? o_layout : x_layout;
  const char *default_captions[4] =
      {"OK", "Cancel", "Options", "Delete"};
  int visible_lines = alert_visible_line_count(geom);
  int last_line;
  int caption_width;

  vita2d_draw_rectangle(
      geom.x, geom.y, geom.width, geom.height,
      RGBA8(10, 16, 24, 242));
  draw_border(geom, 0xff006000);

  if (first_line < 0) {
    first_line = 0;
  }
  if (first_line > lines->count - visible_lines) {
    first_line = lines->count - visible_lines;
  }
  if (first_line < 0) {
    first_line = 0;
  }
  last_line = first_line + visible_lines;
  if (last_line > lines->count) {
    last_line = lines->count;
  }

  if (lines->count == 1) {
    char fitted_line[1024];
    guilib_fit_text(
        fitted_line, sizeof(fitted_line), lines->items[0],
        ALERT_FONT_SIZE,
        geom.width - ALERT_HORIZONTAL_PADDING * 2);
    draw_text_hcentered(
        geom.x + geom.width / 2,
        geom.y +
            (geom.height - ALERT_BOTTOM_PADDING +
             ALERT_FONT_SIZE) / 2,
        0xffffffff,
        fitted_line);
  } else {
    for (int i = first_line; i < last_line; i++) {
      char fitted_line[1024];
      guilib_fit_text(
          fitted_line, sizeof(fitted_line), lines->items[i],
          ALERT_FONT_SIZE,
          geom.width - ALERT_HORIZONTAL_PADDING * 2);
      vita2d_font_draw_text(
          font,
          geom.x + ALERT_HORIZONTAL_PADDING,
          geom.y + ALERT_TOP_PADDING + ALERT_FONT_SIZE +
              (i - first_line) * ALERT_LINE_HEIGHT,
          0xffffffff,
          ALERT_FONT_SIZE,
          fitted_line);
    }
  }

  if (buttons_count > 4) {
    buttons_count = 4;
  }
  for (int i = 0; i < buttons_count; i++) {
    const char *button_caption =
        buttons_captions && buttons_captions[i]
            ? buttons_captions[i]
            : default_captions[i];
    size_t used = strlen(caption);
    if (used < sizeof(caption) - 1) {
      snprintf(
          caption + used, sizeof(caption) - used,
          "%s %s  ", icons[i], button_caption);
    }
  }

  caption_width = guilib_fit_text(
      fitted_caption, sizeof(fitted_caption), caption,
      ALERT_FONT_SIZE,
      geom.width - ALERT_HORIZONTAL_PADDING * 2);
  vita2d_font_draw_text(
      font,
      geom.x + geom.width - ALERT_HORIZONTAL_PADDING -
          caption_width,
      geom.total_y - 12,
      0xffffffff,
      ALERT_FONT_SIZE,
      fitted_caption);

  if (lines->count > visible_lines) {
    int scroll_max_width =
        geom.width - ALERT_HORIZONTAL_PADDING * 3 -
        caption_width;
    snprintf(
        scroll_text, sizeof(scroll_text),
        "UP/DOWN scroll  %d-%d / %d",
        first_line + 1, last_line, lines->count);
    guilib_fit_text(
        fitted_scroll, sizeof(fitted_scroll), scroll_text,
        15, scroll_max_width);
    vita2d_font_draw_text(
        font,
        geom.x + ALERT_HORIZONTAL_PADDING,
        geom.total_y - 12,
        0xffaaaaaa,
        15,
        fitted_scroll);
  }
}

static void draw_alert(
    char *message,
    menu_geom geom,
    char *buttons_captions[],
    int buttons_count) {
  alert_lines lines = wrap_alert_text(
      message, geom.width - ALERT_HORIZONTAL_PADDING * 2);
  draw_alert_lines(
      &lines, 0, geom, buttons_captions, buttons_count);
  alert_lines_free(&lines);
}

void ui_start() {
  vita2d_start_drawing();
  vita2d_clear_screen();
}

void ui_end() {
  vita2d_end_drawing();
  vita2d_wait_rendering_done();
  vita2d_swap_buffers();
}

int read_buttons() {
    SceCtrlData pad = {0};
    static int old;
    static int hold_times;
    int curr, btn;

    sceCtrlSetSamplingMode(SCE_CTRL_MODE_ANALOG_WIDE);
    sceCtrlPeekBufferPositive(0, &pad, 1);

    if (pad.ly < 0x10) {
        pad.buttons |= SCE_CTRL_UP;
    } else if (pad.ly > 0xef) {
        pad.buttons |= SCE_CTRL_DOWN;
    }
    curr = pad.buttons;
    btn = pad.buttons & ~old;
    if (curr && old == curr) {
        hold_times += 1;
        if (hold_times >= 10) {
            btn = curr;
            hold_times = 8;
            btn |= SCE_CTRL_HOLD;
        }
    } else {
        hold_times = 0;
        old = curr;
    }
    return btn;
}

int display_menu(menu_entry menu[], int total_elements, menu_geom *geom_ptr,
                 gui_loop_callback cb, gui_back_callback back_cb,
                 gui_draw_callback draw_callback,
                 void *context) {
  ui_end();

  int offset = 0;
  int cursor = 0;

  menu_geom geom;

  if (!geom_ptr) {
    geom = make_geom_centered(600, 400);
    geom.el = 24;
  } else {
    geom = *geom_ptr;
  }

  int tick_number = 0;
  int exit_code = 0;

  while (true) {
    int active_elements = 0;
    for (int i = 0; i < total_elements; i++) {
        active_elements += menu[i].disabled ? 0 : 1;
    }

    ui_start();
    tick_number++;

    if (tick_number > 3) {
      if (draw_callback) {
        draw_callback();
      }
      if (gui_global_draw_callback) {
        gui_global_draw_callback();
      }
    }

    draw_menu(menu, total_elements, geom, cursor, offset);

    int real_cursor = 0;

    for (int c = 0; real_cursor < total_elements; real_cursor++) {
      if (menu[real_cursor].disabled) {
        continue;
      }
      if (cursor == c) {
        break;
      }
      c++;
    }

    // select item
    input_data input = {0};
    input.buttons = read_buttons();
    sceTouchPeek(SCE_TOUCH_PORT_FRONT, &input.touch, 1);
    if (input.buttons & SCE_CTRL_DOWN) {
      cursor += 1;
    }
    if (input.buttons & SCE_CTRL_UP) {
      cursor -= 1;
    }
    cursor = cursor < 0 ? 0 : cursor;
    cursor = cursor > active_elements - 1 ? active_elements - 1 : cursor;

    int cursor_y = geom.y + ((cursor == 0 ? 0 : real_cursor) * geom.el) - offset;
    offset -= cursor_y < geom.y ? 8 : 0;
    offset -= cursor_y > geom.total_y - geom.el * 2 ? -8 : 0;

    if (cb) {
      exit_code = cb(menu[real_cursor].id, context, &input);
      if (exit_code) {
        goto error;
      }
    }

    if (gui_global_loop_callback) {
      gui_global_loop_callback(menu[real_cursor].id, context, &input);
    }

    if (input.buttons & config.btn_cancel && (input.buttons & SCE_CTRL_HOLD) == 0) {
      if (!back_cb || back_cb(context) == 0) {
        exit_code = 1;
        goto error;
      }
    }

    ui_end();
  }

  return 0;

error:
  ui_end();
  return exit_code;
}


void display_alert(char *message, char *button_captions[], int buttons_count,
                   gui_loop_callback cb, void *context) {
  menu_geom alert_geom = make_geom_centered(840, 430);
  alert_lines lines = wrap_alert_text(
      message, alert_geom.width - ALERT_HORIZONTAL_PADDING * 2);
  int first_line = 0;
  int visible_lines = alert_visible_line_count(alert_geom);
  int max_first_line = lines.count - visible_lines;
  if (max_first_line < 0) {
    max_first_line = 0;
  }

  while (true) {
    ui_start();

    draw_alert_lines(
        &lines, first_line, alert_geom,
        button_captions, buttons_count);

    input_data input = {0};
    input.buttons = read_buttons();
    sceTouchPeek(SCE_TOUCH_PORT_FRONT, &input.touch, 1);

    int result = -1;

    if ((input.buttons & SCE_CTRL_UP) && first_line > 0) {
      first_line--;
    } else if ((input.buttons & SCE_CTRL_DOWN) &&
               first_line < max_first_line) {
      first_line++;
    }

    if (input.buttons & SCE_CTRL_HOLD) {
      ui_end();
      continue;
    }

    if (input.buttons & config.btn_confirm) {
      result = 0;
    } else if (input.buttons & config.btn_cancel) {
      result = 1;
    } else if (input.buttons & SCE_CTRL_TRIANGLE) {
      result = 2;
    } else if (input.buttons & SCE_CTRL_SQUARE) {
      result = 3;
    }

    if (cb && result != -1 && result < buttons_count) {
      switch(cb(result, context, &input)) {
        case 1:
          ui_end();
          alert_lines_free(&lines);
          return;
      }
    } else if (result == 0) {
      ui_end();
      alert_lines_free(&lines);
      return;
    }

    ui_end();
  }
}

void display_error(char *format, ...) {
  char buf[0x1000];

  va_list opt;
  va_start(opt, format);
  vsnprintf(buf, sizeof(buf), format, opt);
  display_alert(buf, NULL, 1, NULL, NULL);
  va_end(opt);
}

void flash_message(char *format, ...) {
  char buf[0x1000];

  va_list opt;
  va_start(opt, format);
  vsnprintf(buf, sizeof(buf), format, opt);
  va_end(opt);

  ui_end();

  menu_geom alert_geom = make_geom_centered(400, 200);
  ui_start();

  vita2d_draw_rectangle(0, 0, WIDTH, HEIGHT, 0xff000000);
  draw_alert(buf, alert_geom, NULL, 0);

  ui_end();
}

void drw() {
  vita2d_draw_rectangle(0, 0, 150, 150, 0xffffffff);
}

void guilib_init(gui_loop_callback global_loop_cb, gui_draw_callback global_draw_cb) {
  vita2d_init();
  vita2d_set_clear_color(0xff000000);
  font = vita2d_load_font_file("app0:assets/mononoki-Regular.ttf");

  gui_global_draw_callback = global_draw_cb;
  gui_global_loop_callback = global_loop_cb;
}

static int confirm_alert_callback(
    int id,
    void *context,
    const input_data *input) {
  (void)input;
  *(int *)context = id == 0;
  return 1;
}

int display_confirm(const char* message) {
  char *buttons[] = {"Yes", "No"};
  int confirmed = 0;
  display_alert(
      (char *)message, buttons, 2,
      &confirm_alert_callback, &confirmed);
  return confirmed;
}
