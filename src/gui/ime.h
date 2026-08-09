#pragma once

#include <stddef.h>

int ime_dialog_string(char *text, size_t text_size, const char *title,
                      const char *def);
int ime_dialog_number(char *text, size_t text_size, const char *title,
                      const char *def);
