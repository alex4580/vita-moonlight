#include <stdio.h>
#include <string.h>

#include <psp2/appmgr.h>
#include <psp2/apputil.h>
#include <psp2/types.h>
#include <psp2/kernel/processmgr.h>
#include <psp2/message_dialog.h>
#include <psp2/ime_dialog.h>
#include <psp2/display.h>
#include <psp2/apputil.h>

#include <vita2d.h>

#include "guilib.h"
#include "ime_text.h"

#define SCE_IME_DIALOG_MAX_TITLE_LENGTH	(128)
//#define SCE_IME_DIALOG_MAX_TEXT_LENGTH	(512)

#define IME_DIALOG_RESULT_NONE 0
#define IME_DIALOG_RESULT_RUNNING 1
#define IME_DIALOG_RESULT_FINISHED 2
#define IME_DIALOG_RESULT_CANCELED 3


static uint16_t ime_title_utf16[SCE_IME_DIALOG_MAX_TITLE_LENGTH + 1];
static uint16_t ime_initial_text_utf16[SCE_IME_DIALOG_MAX_TEXT_LENGTH];
static uint16_t ime_input_text_utf16[SCE_IME_DIALOG_MAX_TEXT_LENGTH + 1];
static int initImeDialog(SceImeType type, const char *title,
                         const char *initial_text, int max_text_length) {
  // Convert UTF8 to UTF16
  if (!ime_utf8_to_utf16(title, ime_title_utf16,
                         sizeof(ime_title_utf16) /
                             sizeof(ime_title_utf16[0])) ||
      !ime_utf8_to_utf16(initial_text, ime_initial_text_utf16,
                         sizeof(ime_initial_text_utf16) /
                             sizeof(ime_initial_text_utf16[0]))) {
    return -1;
  }
  memset(ime_input_text_utf16, 0, sizeof(ime_input_text_utf16));

  SceImeDialogParam param;
  sceImeDialogParamInit(&param);

  param.sdkVersion = 0x03150021;
  param.supportedLanguages = 0x0001FFFF;
  param.languagesForced = SCE_TRUE;
  param.type = type;
  param.title = ime_title_utf16;
  param.maxTextLength = max_text_length;
  param.initialText = ime_initial_text_utf16;
  param.inputTextBuffer = ime_input_text_utf16;

  return sceImeDialogInit(&param);
}

static int ime_dialog_type(SceImeType type, char *text, size_t text_size,
                           const char *title, const char *def) {
  if (text == NULL || text_size == 0 || title == NULL) return -1;
  sceCommonDialogSetConfigParam(&(SceCommonDialogConfigParam){});

  if (def == NULL) def = "";
  if (initImeDialog(type, title, def, 128) < 0) {
    text[0] = '\0';
    display_error("The Vita on-screen keyboard could not be opened.");
    return -1;
  }

  int ret = 0;

  while (1) {
    vita2d_start_drawing();
    vita2d_clear_screen();

    SceCommonDialogStatus status = sceImeDialogGetStatus();
    if (status == IME_DIALOG_RESULT_FINISHED) {
      SceImeDialogResult result;
      memset(&result, 0, sizeof(SceImeDialogResult));
      sceImeDialogGetResult(&result);

      if (result.button == SCE_IME_DIALOG_BUTTON_CLOSE) {
        ret = -1;
      } else if (!ime_utf16_to_utf8(
                     ime_input_text_utf16,
                     sizeof(ime_input_text_utf16) /
                         sizeof(ime_input_text_utf16[0]),
                     text, text_size)) {
        /* The IME may contain more UTF-8 bytes than the destination can
         * represent. Never truncate into a saved address, name, or setting. */
        text[0] = '\0';
        ret = -2;
      }
      vita2d_end_drawing();
      break;
    }

    vita2d_end_drawing();
    vita2d_common_dialog_update();
    vita2d_swap_buffers();
    sceDisplayWaitVblankStart();
  }

  sceImeDialogTerm();
  if (ret == -2) {
    display_error(
        "That text needs more storage than this field allows.\n"
        "Enter a shorter value and try again.");
  }
  return ret;
}

int ime_dialog_string(char *text, size_t text_size, const char *title,
                      const char *def) {
  return ime_dialog_type(
      SCE_IME_TYPE_DEFAULT, text, text_size, title, def);
}

int ime_dialog_number(char *text, size_t text_size, const char *title,
                      const char *def) {
  return ime_dialog_type(
      SCE_IME_TYPE_EXTENDED_NUMBER, text, text_size, title, def);
}
