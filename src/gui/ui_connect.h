#include <stdbool.h>
#include <stddef.h>
#include "../device.h"

void ui_connect_address(char *addr, size_t addr_size);
device_info_t* ui_connect_and_pairing(device_info_t *info);
bool ui_connect_connected();

void ui_connect_resume();
void ui_connect_manual();
void ui_connect_paired_device(
    device_info_t *info, int host_index, const char *discovered_ip);
bool check_connection(const char *name, const char *addr, uint16_t port);
