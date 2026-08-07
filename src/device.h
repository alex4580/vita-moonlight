#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define DEVICE_MAX_COUNT 16
#define DEVICE_PATH_CAPACITY 1024

typedef struct device_info device_info_t;
struct device_info {
  char name[256];
  char display_name[256];
  uint16_t port;
  bool paired;
  char internal[256];
  char external[256];
  char mac[18]; // XX:XX:XX:XX:XX:XX\0
  bool prefer_external;
};

typedef struct device_infos device_infos_t;
struct device_infos {
  int size;
  int count;
  device_info_t *devices;
};

extern device_infos_t known_devices;

device_info_t* find_device(const char *name);
device_info_t* find_device_by_address(const char *address);
device_info_t* append_device(const device_info_t *info);
device_info_t* upsert_device(const device_info_t *info);
void load_all_known_devices();
bool load_device_info(device_info_t *info);
bool save_device_info(const device_info_t *info);
bool remove_device(const char *name);
bool device_file_path(char *out, size_t out_size, const char *dir);
