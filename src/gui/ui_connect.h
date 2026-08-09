#include <stdbool.h>
#include <stddef.h>
#include "../device.h"

void ui_connect_address(char *addr, size_t addr_size);
device_info_t* ui_connect_and_pairing(device_info_t *info);
bool ui_connect_connected();
/* Idempotently stops the lease worker and asks the authenticated host to
 * restore the exact display handoff. Safe to call during process shutdown. */
bool ui_connect_release_stream_boundary(bool show_error);
/* False only while local heartbeat resources may still reference server/CURL
 * state. A remote stop failure does not make local cleanup unsafe. */
bool ui_connect_stream_boundary_local_cleanup_ready(void);
/* Ordered process shutdown: join media teardown, stop the lease heartbeat,
 * notify the host, then release GameStream HTTP/pairing state. */
bool ui_connect_shutdown(void);

void ui_connect_resume();
void ui_connect_manual();
void ui_connect_paired_device(
    device_info_t *info, int host_index, const char *discovered_ip);
bool check_connection(const char *name, const char *addr, uint16_t port);
