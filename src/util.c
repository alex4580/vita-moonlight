#include "util.h"
#include "debug.h"

#include <string.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>

void swap_app_list_entries(PAPP_LIST a, PAPP_LIST b) {
    int id = a->id;
    char *name = a->name;

    a->id = b->id;
    a->name = b->name;
    b->id = id;
    b->name = name;
}

void sort_app_list(PAPP_LIST list) {
    if (list == NULL) {
        return;
    }

    int swapped = 0;
    PAPP_LIST cur = NULL;
    PAPP_LIST prev = NULL;

    do {
        swapped = 0;
        cur = list;

        while (cur->next != prev) {
            if (strcmp(cur->name, cur->next->name) > 0) {
                swap_app_list_entries(cur, cur->next);
                swapped = 1;
            }
            cur = cur->next;
        }
        prev = cur;
    } while (swapped);
}

int ensure_buf_size(void **buf, size_t *buf_size, size_t required_size) {
  if (*buf_size >= required_size)
    return 0;

  void *resized = realloc(*buf, required_size);
  if (!resized)
    return -1;

  *buf = resized;
  *buf_size = required_size;

  return 0;
}
