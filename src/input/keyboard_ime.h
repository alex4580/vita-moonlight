#ifndef VITA_KEYBOARD_IME_H
#define VITA_KEYBOARD_IME_H

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/* Numeric values are part of Vita's public SceImeEvent ABI. Keeping this
 * helper independent of VitaSDK types lets host-native CI exercise the event
 * interpretation used by the in-stream keyboard. */
enum VitaKeyboardImeEvent {
  VITA_KEYBOARD_IME_UPDATE_TEXT = 1,
  VITA_KEYBOARD_IME_UPDATE_CARET = 2,
  VITA_KEYBOARD_IME_PRESS_CLOSE = 4,
  VITA_KEYBOARD_IME_PRESS_ENTER = 5,
};

typedef enum VitaKeyboardImeAction {
  VITA_KEYBOARD_IME_ACTION_NONE = 0,
  VITA_KEYBOARD_IME_ACTION_CHARACTER,
  VITA_KEYBOARD_IME_ACTION_BACKSPACE,
  VITA_KEYBOARD_IME_ACTION_LEFT,
  VITA_KEYBOARD_IME_ACTION_RIGHT,
  VITA_KEYBOARD_IME_ACTION_ENTER,
  VITA_KEYBOARD_IME_ACTION_CLOSE,
} VitaKeyboardImeAction;

typedef struct VitaKeyboardImeDecision {
  VitaKeyboardImeAction action;
  uint16_t character;
} VitaKeyboardImeDecision;

static inline uint16_t vita_keyboard_ime_payload_character(
    const uint16_t *text, size_t text_units) {
  if (text == NULL) return 0;
  for (size_t i = 0; i < text_units; ++i) {
    /* U+0001 is the private sentinel which keeps a character on both sides of
     * the IME caret. NUL terminates the bounded buffer. */
    if (text[i] == 0) break;
    if (text[i] != 1) return text[i];
  }
  return 0;
}

static inline VitaKeyboardImeDecision vita_keyboard_ime_interpret(
    uint32_t event_id, uint32_t caret_index, int32_t edit_length_change,
    const uint16_t *text, size_t text_units, bool initial_text_update) {
  VitaKeyboardImeDecision decision = {
      VITA_KEYBOARD_IME_ACTION_NONE, 0};

  switch (event_id) {
    case VITA_KEYBOARD_IME_UPDATE_TEXT: {
      uint16_t character =
          vita_keyboard_ime_payload_character(text, text_units);
      if (character != 0) {
        decision.action = VITA_KEYBOARD_IME_ACTION_CHARACTER;
        decision.character = character;
      } else if (!initial_text_update &&
                 (edit_length_change < 0 || caret_index == 0)) {
        decision.action = VITA_KEYBOARD_IME_ACTION_BACKSPACE;
      }
      break;
    }
    case VITA_KEYBOARD_IME_UPDATE_CARET:
      if (caret_index == 0) {
        decision.action = VITA_KEYBOARD_IME_ACTION_LEFT;
      } else if (caret_index == 2) {
        decision.action = VITA_KEYBOARD_IME_ACTION_RIGHT;
      }
      break;
    case VITA_KEYBOARD_IME_PRESS_ENTER:
      decision.action = VITA_KEYBOARD_IME_ACTION_ENTER;
      break;
    case VITA_KEYBOARD_IME_PRESS_CLOSE:
      decision.action = VITA_KEYBOARD_IME_ACTION_CLOSE;
      break;
    default:
      break;
  }
  return decision;
}

#endif
