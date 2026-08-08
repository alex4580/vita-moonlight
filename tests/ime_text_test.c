#include <stdint.h>
#include <stdio.h>
#include <string.h>

#include "../src/gui/ime_text.h"

static int failures = 0;

#define EXPECT(condition, description)                                      \
  do {                                                                      \
    if (!(condition)) {                                                     \
      fprintf(stderr, "FAIL: %s\n", (description));                        \
      failures++;                                                           \
    }                                                                       \
  } while (0)

static void expect_round_trip(const char *input) {
  uint16_t utf16[64];
  char output[128];
  EXPECT(ime_utf8_to_utf16(input, utf16, sizeof(utf16) / sizeof(utf16[0])),
         "valid UTF-8 converts to UTF-16");
  EXPECT(ime_utf16_to_utf8(utf16, sizeof(utf16) / sizeof(utf16[0]), output,
                           sizeof(output)),
         "valid UTF-16 converts to UTF-8");
  EXPECT(strcmp(input, output) == 0, "UTF text survives a round trip");
}

int main(void) {
  expect_round_trip("Vita Moonlight");
  expect_round_trip("caf\xc3\xa9 \xe6\x9c\x88 \xf0\x9f\x8e\xae");

  uint16_t utf16[16] = {0};
  char utf8[32] = {0};
  EXPECT(!ime_utf8_to_utf16("\xc0\x80", utf16,
                            sizeof(utf16) / sizeof(utf16[0])),
         "overlong UTF-8 is rejected");
  EXPECT(!ime_utf8_to_utf16("\xed\xa0\x80", utf16,
                            sizeof(utf16) / sizeof(utf16[0])),
         "UTF-8 surrogate encoding is rejected");
  EXPECT(!ime_utf8_to_utf16("\xf4\x90\x80\x80", utf16,
                            sizeof(utf16) / sizeof(utf16[0])),
         "UTF-8 above U+10FFFF is rejected");
  EXPECT(!ime_utf8_to_utf16("abcd", utf16, 4),
         "UTF-16 capacity reserves a terminator");
  EXPECT(utf16[3] == 0, "failed UTF-16 conversion is terminated");

  const uint16_t unpaired_high[] = {0xd83c, 0};
  EXPECT(!ime_utf16_to_utf8(unpaired_high, 2, utf8, sizeof(utf8)),
         "unpaired high surrogate is rejected");
  const uint16_t unpaired_low[] = {0xdfae, 0};
  EXPECT(!ime_utf16_to_utf8(unpaired_low, 2, utf8, sizeof(utf8)),
         "unpaired low surrogate is rejected");

  const uint16_t controller[] = {0xd83c, 0xdfae, 0};
  char exact[5];
  EXPECT(ime_utf16_to_utf8(controller, 3, exact, sizeof(exact)),
         "four-byte code point fits with its terminator");
  EXPECT(memcmp(exact, "\xf0\x9f\x8e\xae", sizeof(exact)) == 0,
         "surrogate pair has the expected UTF-8 encoding");
  char too_small[4] = {'x', 'x', 'x', 'x'};
  EXPECT(!ime_utf16_to_utf8(controller, 3, too_small, sizeof(too_small)),
         "UTF-8 capacity reserves a terminator");
  EXPECT(too_small[0] == '\0',
         "failed UTF-8 conversion leaves a terminated prefix");

  if (failures != 0) return 1;
  puts("Vita IME bounded UTF conversion tests passed.");
  return 0;
}
