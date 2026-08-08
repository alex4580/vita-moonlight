// check_host.h
#ifndef CHECK_HOST_H
#define CHECK_HOST_H

#include "device.h"
#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// Estados posibles para el host
#define HOST_OFFLINE 0
#define HOST_ONLINE  1
#define HOST_IP_CHANGED 2

struct host_status {
    int status; // 0: offline, 1: online, 2: ip changed
    char current_ip[64];
};

// Chequea el estado de un host registrado
struct host_status check_host_status(const device_info_t *info);

#include <psp2/kernel/threadmgr.h>
#define MAX_HOSTS 16

void start_host_scan_thread(void);
void stop_host_scan_thread(void);
bool find_host_ip_mdns(const char *hostname, char *out_ip, size_t out_len);

/* The scanner runs concurrently with the UI. Always consume one locked,
 * immutable snapshot instead of reading its status and IP buffers directly. */
bool host_scan_get_snapshot(
    int index, struct host_status *status, char *pending_ip,
    size_t pending_ip_size, bool *has_pending_ip);
void host_scan_clear_pending_ip_update(int index);
bool host_scan_take_status_changed(void);
int host_scan_state(void);

#ifdef __cplusplus
}
#endif

#endif // CHECK_HOST_H
