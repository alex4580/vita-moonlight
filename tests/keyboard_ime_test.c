#include <assert.h>
#include <stdint.h>
#include <stdio.h>

#include "input/keyboard_ime.h"

static VitaKeyboardImeDecision decide(uint32_t event_id,
                                      uint32_t caret_index,
                                      int32_t edit_length_change,
                                      const uint16_t *text,
                                      size_t text_units,
                                      bool initial_text_update) {
  return vita_keyboard_ime_interpret(event_id, caret_index,
                                     edit_length_change, text, text_units,
                                     initial_text_update);
}

int main(void) {
  const uint16_t typed_a[] = {1, 'a', 1, 0};
  const uint16_t sentinels[] = {1, 1, 1, 0};
  const uint16_t bounded_character[] = {1, '7'};

  VitaKeyboardImeDecision result =
      decide(VITA_KEYBOARD_IME_UPDATE_TEXT, 2, 1,
             typed_a, 4, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_CHARACTER);
  assert(result.character == 'a');

  result = decide(VITA_KEYBOARD_IME_UPDATE_TEXT, 0, -1,
                  sentinels, 4, true);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_NONE);

  result = decide(VITA_KEYBOARD_IME_UPDATE_TEXT, 0, -1,
                  sentinels, 4, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_BACKSPACE);

  result = decide(VITA_KEYBOARD_IME_UPDATE_TEXT, 0, -1,
                  typed_a, 4, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_CHARACTER);

  result = decide(VITA_KEYBOARD_IME_UPDATE_TEXT, 1, 1,
                  bounded_character, 2, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_CHARACTER);
  assert(result.character == '7');

  result = decide(VITA_KEYBOARD_IME_UPDATE_CARET, 0, 0,
                  NULL, 0, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_LEFT);
  result = decide(VITA_KEYBOARD_IME_UPDATE_CARET, 2, 0,
                  NULL, 0, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_RIGHT);
  result = decide(VITA_KEYBOARD_IME_UPDATE_CARET, 1, 0,
                  NULL, 0, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_NONE);

  result = decide(VITA_KEYBOARD_IME_PRESS_ENTER, 0, 0,
                  NULL, 0, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_ENTER);
  result = decide(VITA_KEYBOARD_IME_PRESS_CLOSE, 0, 0,
                  NULL, 0, false);
  assert(result.action == VITA_KEYBOARD_IME_ACTION_CLOSE);

  puts("Vita keyboard IME event tests passed.");
  return 0;
}
