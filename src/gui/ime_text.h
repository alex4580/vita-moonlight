#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

/*
 * Small, allocation-free UTF helpers shared by the Vita IME and its native
 * regression test.  Both capacities include the trailing NUL.  A failure
 * always leaves a terminated prefix in the destination.
 */
static bool ime_utf8_to_utf16(const char *source, uint16_t *destination,
                              size_t destination_units) {
  size_t input = 0;
  size_t output = 0;

  if (destination == NULL || destination_units == 0) return false;
  destination[0] = 0;
  if (source == NULL) return true;

  while (source[input] != '\0') {
    const unsigned char *bytes =
        (const unsigned char *)source + input;
    uint32_t codepoint;
    size_t consumed;

    if (bytes[0] <= 0x7f) {
      codepoint = bytes[0];
      consumed = 1;
    } else if (bytes[0] >= 0xc2 && bytes[0] <= 0xdf &&
               (bytes[1] & 0xc0) == 0x80) {
      codepoint = ((uint32_t)(bytes[0] & 0x1f) << 6) |
                  (uint32_t)(bytes[1] & 0x3f);
      consumed = 2;
    } else if (bytes[0] >= 0xe0 && bytes[0] <= 0xef &&
               bytes[1] != 0 && bytes[2] != 0 &&
               (bytes[1] & 0xc0) == 0x80 &&
               (bytes[2] & 0xc0) == 0x80 &&
               !(bytes[0] == 0xe0 && bytes[1] < 0xa0) &&
               !(bytes[0] == 0xed && bytes[1] >= 0xa0)) {
      codepoint = ((uint32_t)(bytes[0] & 0x0f) << 12) |
                  ((uint32_t)(bytes[1] & 0x3f) << 6) |
                  (uint32_t)(bytes[2] & 0x3f);
      consumed = 3;
    } else if (bytes[0] >= 0xf0 && bytes[0] <= 0xf4 &&
               bytes[1] != 0 && bytes[2] != 0 && bytes[3] != 0 &&
               (bytes[1] & 0xc0) == 0x80 &&
               (bytes[2] & 0xc0) == 0x80 &&
               (bytes[3] & 0xc0) == 0x80 &&
               !(bytes[0] == 0xf0 && bytes[1] < 0x90) &&
               !(bytes[0] == 0xf4 && bytes[1] >= 0x90)) {
      codepoint = ((uint32_t)(bytes[0] & 0x07) << 18) |
                  ((uint32_t)(bytes[1] & 0x3f) << 12) |
                  ((uint32_t)(bytes[2] & 0x3f) << 6) |
                  (uint32_t)(bytes[3] & 0x3f);
      consumed = 4;
    } else {
      destination[output] = 0;
      return false;
    }

    if (codepoint <= 0xffff) {
      if (output + 1 >= destination_units) {
        destination[output] = 0;
        return false;
      }
      destination[output++] = (uint16_t)codepoint;
    } else {
      if (output + 2 >= destination_units) {
        destination[output] = 0;
        return false;
      }
      codepoint -= 0x10000;
      destination[output++] =
          (uint16_t)(0xd800u | ((codepoint >> 10) & 0x3ffu));
      destination[output++] =
          (uint16_t)(0xdc00u | (codepoint & 0x3ffu));
    }
    input += consumed;
  }

  destination[output] = 0;
  return true;
}

static bool ime_utf16_to_utf8(const uint16_t *source, size_t source_units,
                              char *destination,
                              size_t destination_bytes) {
  size_t input = 0;
  size_t output = 0;

  if (destination == NULL || destination_bytes == 0) return false;
  destination[0] = '\0';
  if (source == NULL) return true;

  while (input < source_units && source[input] != 0) {
    uint32_t codepoint = source[input++];
    if (codepoint >= 0xd800 && codepoint <= 0xdbff) {
      if (input >= source_units || source[input] < 0xdc00 ||
          source[input] > 0xdfff) {
        destination[output] = '\0';
        return false;
      }
      codepoint = 0x10000u + ((codepoint - 0xd800u) << 10) +
                  ((uint32_t)source[input++] - 0xdc00u);
    } else if (codepoint >= 0xdc00 && codepoint <= 0xdfff) {
      destination[output] = '\0';
      return false;
    }

    size_t required = codepoint <= 0x7f   ? 1
                      : codepoint <= 0x7ff ? 2
                      : codepoint <= 0xffff ? 3
                                           : 4;
    if (output + required >= destination_bytes) {
      destination[output] = '\0';
      return false;
    }

    if (required == 1) {
      destination[output++] = (char)codepoint;
    } else if (required == 2) {
      destination[output++] = (char)(0xc0u | (codepoint >> 6));
      destination[output++] = (char)(0x80u | (codepoint & 0x3fu));
    } else if (required == 3) {
      destination[output++] = (char)(0xe0u | (codepoint >> 12));
      destination[output++] =
          (char)(0x80u | ((codepoint >> 6) & 0x3fu));
      destination[output++] = (char)(0x80u | (codepoint & 0x3fu));
    } else {
      destination[output++] = (char)(0xf0u | (codepoint >> 18));
      destination[output++] =
          (char)(0x80u | ((codepoint >> 12) & 0x3fu));
      destination[output++] =
          (char)(0x80u | ((codepoint >> 6) & 0x3fu));
      destination[output++] = (char)(0x80u | (codepoint & 0x3fu));
    }
  }

  destination[output] = '\0';
  return true;
}
