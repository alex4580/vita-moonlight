// check_host.c
// Saved-host discovery and bounded reachability checks for Moonlight Vita.

#include "check_host.h"
#include "device.h"
#include "config.h"
#include "util.h"
#include <string.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <errno.h>
#include <fcntl.h>
#include <arpa/inet.h>
#include <netdb.h>
#include <sys/select.h>
#include <sys/socket.h>
#include <unistd.h>
#include "debug.h"

#define HOST_PROBE_TIMEOUT_US 250000

#ifdef __vita__
#include "udp_sniffer_vita.h"
#include <psp2/kernel/threadmgr.h>

/* State used by the synchronous one-host mDNS helper. */
static volatile int mdns_found = 0;
static char mdns_found_ip[64] = "";
static char mdns_hostname_ref[256] = "";

static void mdns_found_cb(int idx, const char *host, const char *pcname,
        const char *ip, int port) {
    (void)idx;
    (void)host;
    (void)port;
    if (pcname != NULL && ip != NULL &&
            device_name_equal(pcname, mdns_hostname_ref)) {
        strncpy(mdns_found_ip, ip, sizeof(mdns_found_ip) - 1);
        mdns_found_ip[sizeof(mdns_found_ip) - 1] = '\0';
        mdns_found = 1;
    }
}

bool find_host_ip_mdns(const char *hostname, char *out_ip, size_t out_len) {
    if (hostname == NULL || out_ip == NULL || out_len == 0) return false;

    mdns_found = 0;
    mdns_found_ip[0] = '\0';
    strncpy(mdns_hostname_ref, hostname, sizeof(mdns_hostname_ref) - 1);
    mdns_hostname_ref[sizeof(mdns_hostname_ref) - 1] = '\0';
    udp_sniffer_vita_deinit();
    udp_sniffer_vita_init();
    udp_sniffer_vita_set_callback(mdns_found_cb);
    int ticks = 0;
    const int max_ticks = 30;
    while (!mdns_found && ticks < max_ticks) {
        udp_sniffer_vita_poll();
        sceKernelDelayThread(100000);
        ticks++;
    }
    udp_sniffer_vita_set_callback(NULL);
    udp_sniffer_vita_deinit();
    if (!mdns_found) return false;

    strncpy(out_ip, mdns_found_ip, out_len - 1);
    out_ip[out_len - 1] = '\0';
    return true;
}

static int g_host_scan_thread_status = 0;
static int g_host_status_changed = 0;
static struct host_status g_host_status[MAX_HOSTS];
static int pending_ip_update_idx = -1;
static char pending_ip_update[64] = "";
static SceUID host_state_mutex = -1;

enum host_scan_state {
    HOST_SCAN_STOPPED = 0,
    HOST_SCAN_RUNNING = 1,
    HOST_SCAN_STOP_REQUESTED = 2,
};

#define HOST_SCAN_TICK_US 100000
#define HOST_ONLINE_RECHECK_TICKS 50
#define HOST_OFFLINE_RETRY_TICKS 10
#define HOST_OFFLINE_MAX_RETRY_TICKS 300

/*
 * The scanner owns exactly one Vita kernel thread. The handle is retained
 * until a successful wait and delete, so a live worker can never be mistaken
 * for a free slot while it still owns the shared mDNS socket and status data.
 */
static SceUID host_scan_thread_id = -1;
static char scan_mdns_ip[MAX_HOSTS][64];
static char cached_host_name[MAX_HOSTS][256];

static bool ping_host(const char *ip, uint16_t port);
static void copy_host_string(
    char *destination, size_t destination_size, const char *source);

static int scan_state_load(void) {
    return __atomic_load_n(&g_host_scan_thread_status, __ATOMIC_ACQUIRE);
}

static void scan_state_store(int state) {
    __atomic_store_n(&g_host_scan_thread_status, state, __ATOMIC_RELEASE);
}

int host_scan_state(void) {
    return scan_state_load();
}

bool host_scan_take_status_changed(void) {
    return __atomic_exchange_n(
        &g_host_status_changed, 0, __ATOMIC_ACQ_REL) != 0;
}

static bool ensure_host_state_mutex(void) {
    if (host_state_mutex >= 0) return true;
    SceUID created = sceKernelCreateMutex(
        "vita-moonlight-host-state", 0, 0, NULL);
    if (created < 0) return false;
    host_state_mutex = created;
    return true;
}

static bool lock_host_state(void) {
    return host_state_mutex >= 0 &&
        sceKernelLockMutex(host_state_mutex, 1, NULL) >= 0;
}

static void unlock_host_state(void) {
    if (host_state_mutex >= 0) {
        (void)sceKernelUnlockMutex(host_state_mutex, 1);
    }
}

static void copy_host_string(char *destination, size_t destination_size,
        const char *source) {
    if (destination_size == 0) return;
    if (source == NULL) source = "";
    strncpy(destination, source, destination_size - 1);
    destination[destination_size - 1] = '\0';
}

static int active_host_count(void) {
    int count = known_devices.count;
    if (count < 0) return 0;
    return count < MAX_HOSTS ? count : MAX_HOSTS;
}

static bool update_cached_host_status(
        int index, int status, const char *ip, bool publish_pending) {
    if (index < 0 || index >= MAX_HOSTS) return false;
    if (ip == NULL) ip = "";

    if (!lock_host_state()) return false;

    bool changed = g_host_status[index].status != status ||
        strcmp(g_host_status[index].current_ip, ip) != 0;
    g_host_status[index].status = status;
    copy_host_string(g_host_status[index].current_ip,
        sizeof(g_host_status[index].current_ip), ip);
    if (publish_pending) {
        copy_host_string(pending_ip_update, sizeof(pending_ip_update), ip);
        pending_ip_update_idx = index;
    } else if (pending_ip_update_idx == index) {
        pending_ip_update_idx = -1;
        pending_ip_update[0] = '\0';
    }
    unlock_host_state();
    if (changed) {
        __atomic_store_n(&g_host_status_changed, 1, __ATOMIC_RELEASE);
    }
    return changed;
}

static void clear_pending_ip_update_for_host(int index) {
    if (!lock_host_state()) return;
    if (index < 0 || pending_ip_update_idx == index) {
        pending_ip_update_idx = -1;
        pending_ip_update[0] = '\0';
    }
    unlock_host_state();
}

void host_scan_clear_pending_ip_update(int index) {
    clear_pending_ip_update_for_host(index);
}

bool host_scan_get_snapshot(
        int index, struct host_status *status, char *pending_ip,
        size_t pending_ip_size, bool *has_pending_ip) {
    if (status == NULL || index < 0 || index >= MAX_HOSTS ||
            !lock_host_state()) {
        return false;
    }
    *status = g_host_status[index];
    bool pending = pending_ip_update_idx == index &&
                   pending_ip_update[0] != '\0';
    if (pending_ip != NULL && pending_ip_size != 0) {
        copy_host_string(pending_ip, pending_ip_size,
                         pending ? pending_ip_update : "");
    }
    if (has_pending_ip != NULL) *has_pending_ip = pending;
    unlock_host_state();
    return true;
}

static int offline_retry_ticks(unsigned int failure_count) {
    unsigned int shift = failure_count > 1 ? failure_count - 1 : 0;
    if (shift > 5) shift = 5;
    int ticks = HOST_OFFLINE_RETRY_TICKS << shift;
    return ticks < HOST_OFFLINE_MAX_RETRY_TICKS
        ? ticks
        : HOST_OFFLINE_MAX_RETRY_TICKS;
}

/*
 * Status is consumed by menu index, while saved-computer edits can shift
 * those indexes. Reconcile by stable credential-directory name so restarting
 * the scanner preserves useful state without briefly showing another PC's
 * cached result.
 */
static void reconcile_host_status_cache(void) {
    struct host_status previous_status[MAX_HOSTS];
    char previous_name[MAX_HOSTS][256];
    if (!lock_host_state()) return;
    memcpy(previous_status, g_host_status, sizeof(previous_status));
    memcpy(previous_name, cached_host_name, sizeof(previous_name));

    int count = active_host_count();
    for (int i = 0; i < count; i++) {
        int previous_index = -1;
        for (int candidate = 0; candidate < MAX_HOSTS; candidate++) {
            if (previous_name[candidate][0] != '\0' &&
                    device_name_equal(previous_name[candidate],
                                      known_devices.devices[i].name)) {
                previous_index = candidate;
                break;
            }
        }

        if (previous_index >= 0) {
            g_host_status[i] = previous_status[previous_index];
        } else {
            g_host_status[i].status = HOST_OFFLINE;
            copy_host_string(g_host_status[i].current_ip,
                sizeof(g_host_status[i].current_ip),
                known_devices.devices[i].internal);
        }
        copy_host_string(cached_host_name[i], sizeof(cached_host_name[i]),
            known_devices.devices[i].name);
    }

    for (int i = count; i < MAX_HOSTS; i++) {
        memset(&g_host_status[i], 0, sizeof(g_host_status[i]));
        cached_host_name[i][0] = '\0';
    }
    unlock_host_state();
}

/* One long-lived listener populates a cache for every saved host. */
static void host_scan_mdns_cb(int idx, const char *host, const char *pcname,
        const char *ip, int port) {
    (void)idx;
    (void)port;
    if (ip == NULL || ip[0] == '\0') return;

    const char *advertised_name =
        pcname != NULL && pcname[0] != '\0' ? pcname : host;
    if (advertised_name == NULL || advertised_name[0] == '\0') return;

    int count = active_host_count();
    for (int i = 0; i < count; i++) {
        if (device_name_equal(
                advertised_name, known_devices.devices[i].name)) {
            copy_host_string(scan_mdns_ip[i], sizeof(scan_mdns_ip[i]), ip);
        }
    }
}

static int host_scan_thread(SceSize args, void *argp) {
    (void)args;
    (void)argp;

    int cooldown_ticks[MAX_HOSTS] = {0};
    unsigned int failure_count[MAX_HOSTS] = {0};
    int next_host = 0;
    bool mdns_active = false;

    memset(scan_mdns_ip, 0, sizeof(scan_mdns_ip));
    vita_debug_log("[SCAN] Host scanner started\n");
    int initial_count = active_host_count();
    for (int i = 0; i < initial_count; i++) {
        if (known_devices.devices[i].paired) {
            mdns_active = true;
            break;
        }
    }
    /* Close any discovery-menu listener before deciding whether this worker
     * needs to open its own. */
    udp_sniffer_vita_deinit();
    if (mdns_active) {
        udp_sniffer_vita_init();
        udp_sniffer_vita_set_callback(host_scan_mdns_cb);
    }

    while (scan_state_load() == HOST_SCAN_RUNNING) {
        if (mdns_active) udp_sniffer_vita_poll();

        int count = active_host_count();
        for (int i = 0; i < count; i++) {
            if (cooldown_ticks[i] > 0) cooldown_ticks[i]--;
        }

        /* Probe at most one host per tick so stop latency remains bounded. */
        int selected = -1;
        for (int offset = 0; offset < count; offset++) {
            int candidate = (next_host + offset) % count;
            if (cooldown_ticks[candidate] == 0) {
                selected = candidate;
                next_host = (candidate + 1) % count;
                break;
            }
        }

        if (selected >= 0 &&
                scan_state_load() == HOST_SCAN_RUNNING) {
            /* Avoid retaining a pointer that UI code can invalidate. */
            device_info_t info = known_devices.devices[selected];
            const char *discovered_ip = scan_mdns_ip[selected];
            const char *reachable_ip = NULL;

            /* Saved-but-unpaired entries are visible for recovery and retry,
             * but must not generate background traffic before trust exists. */
            if (!info.paired) {
                failure_count[selected] = 0;
                cooldown_ticks[selected] = HOST_OFFLINE_MAX_RETRY_TICKS;
                update_cached_host_status(
                    selected, HOST_OFFLINE, info.internal, false);
                goto scanner_tick_complete;
            }

            /* Probe user-saved addresses before any unauthenticated mDNS
             * hint. This prevents a same-name LAN advertisement from
             * delaying or visually replacing a working pinned computer. */
            const char *first_saved =
                info.prefer_external && info.external[0] != '\0'
                    ? info.external : info.internal;
            const char *second_saved = first_saved == info.external
                ? info.internal : info.external;
            if (first_saved[0] != '\0' &&
                    ping_host(first_saved, info.port)) {
                reachable_ip = first_saved;
            }
            if (reachable_ip == NULL && second_saved[0] != '\0' &&
                    strcmp(second_saved, first_saved) != 0 &&
                    scan_state_load() == HOST_SCAN_RUNNING &&
                    ping_host(second_saved, info.port)) {
                reachable_ip = second_saved;
            }
            if (reachable_ip == NULL && discovered_ip[0] != '\0' &&
                    strcmp(discovered_ip, info.internal) != 0 &&
                    strcmp(discovered_ip, info.external) != 0 &&
                    scan_state_load() == HOST_SCAN_RUNNING &&
                    ping_host(discovered_ip, info.port)) {
                reachable_ip = discovered_ip;
            }

            if (reachable_ip != NULL) {
                bool saved_address =
                    strcmp(reachable_ip, info.internal) == 0 ||
                    strcmp(reachable_ip, info.external) == 0;
                int status = saved_address ? HOST_ONLINE : HOST_IP_CHANGED;
                bool changed = update_cached_host_status(
                    selected, status, reachable_ip,
                    status == HOST_IP_CHANGED);
                failure_count[selected] = 0;
                cooldown_ticks[selected] = HOST_ONLINE_RECHECK_TICKS;

                if (changed && status == HOST_IP_CHANGED) {
                    vita_debug_log("[SCAN] Host %s moved from %s to %s\n",
                        info.name, info.internal, reachable_ip);
                } else {
                    if (changed) {
                        vita_debug_log("[SCAN] Host %s is reachable at %s\n",
                            info.name, reachable_ip);
                    }
                }
            } else {
                if (failure_count[selected] < 32) failure_count[selected]++;
                cooldown_ticks[selected] =
                    offline_retry_ticks(failure_count[selected]);
                if (update_cached_host_status(
                        selected, HOST_OFFLINE, info.internal, false)) {
                    vita_debug_log("[SCAN] Host %s is offline; retry in %d ms\n",
                        info.name,
                        cooldown_ticks[selected] * (HOST_SCAN_TICK_US / 1000));
                }
            }
        }

scanner_tick_complete:
        if (scan_state_load() == HOST_SCAN_RUNNING) {
            sceKernelDelayThread(HOST_SCAN_TICK_US);
        }
    }

    if (mdns_active) {
        udp_sniffer_vita_set_callback(NULL);
        udp_sniffer_vita_deinit();
    }
    vita_debug_log("[SCAN] Host scanner stopped\n");
    scan_state_store(HOST_SCAN_STOPPED);
    return 0;
}

void start_host_scan_thread(void) {
    if (!ensure_host_state_mutex()) {
        vita_debug_log("[SCAN] Unable to create host-state mutex\n");
        return;
    }
    if (host_scan_thread_id >= 0) {
        if (scan_state_load() == HOST_SCAN_RUNNING) return;
        stop_host_scan_thread();
        if (host_scan_thread_id >= 0) return;
    }

    reconcile_host_status_cache();

    SceUID tid = sceKernelCreateThread(
        "hostscan", host_scan_thread, 0x10000100, 0x10000, 0, 0, NULL);
    if (tid < 0) {
        vita_debug_log("[SCAN] Unable to create host scanner thread: 0x%08x\n",
            (unsigned int)tid);
        return;
    }

    host_scan_thread_id = tid;
    scan_state_store(HOST_SCAN_RUNNING);
    int result = sceKernelStartThread(tid, 0, NULL);
    if (result < 0) {
        vita_debug_log("[SCAN] Unable to start host scanner thread: 0x%08x\n",
            (unsigned int)result);
        scan_state_store(HOST_SCAN_STOPPED);
        if (sceKernelDeleteThread(tid) >= 0) host_scan_thread_id = -1;
    }
}

void stop_host_scan_thread(void) {
    SceUID tid = host_scan_thread_id;
    if (tid < 0) {
        scan_state_store(HOST_SCAN_STOPPED);
        return;
    }

    scan_state_store(HOST_SCAN_STOP_REQUESTED);
    int result = sceKernelWaitThreadEnd(tid, NULL, NULL);
    if (result < 0) {
        vita_debug_log("[SCAN] Unable to join host scanner thread: 0x%08x\n",
            (unsigned int)result);
        return;
    }

    result = sceKernelDeleteThread(tid);
    if (result < 0) {
        vita_debug_log("[SCAN] Unable to delete joined scanner thread: 0x%08x\n",
            (unsigned int)result);
        return;
    }

    host_scan_thread_id = -1;
    scan_state_store(HOST_SCAN_STOPPED);
}
#endif

/* Bounded TCP reachability probe. */
static bool ping_host(const char *ip, uint16_t port) {
    if (ip == NULL || ip[0] == '\0' || port == 0) return false;

    int sock = socket(AF_INET, SOCK_STREAM, 0);
    if (sock < 0) return false;

#ifdef __vita__
    int nonblocking = 1;
    if (setsockopt(sock, SOL_SOCKET, SO_NONBLOCK,
            (const char *)&nonblocking, sizeof(nonblocking)) < 0) {
        close(sock);
        return false;
    }
#else
    int flags = fcntl(sock, F_GETFL, 0);
    if (flags < 0 || fcntl(sock, F_SETFL, flags | O_NONBLOCK) < 0) {
        close(sock);
        return false;
    }
#endif

    struct sockaddr_in addr = {0};
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    if (inet_pton(AF_INET, ip, &addr.sin_addr) <= 0) {
        close(sock);
        return false;
    }

    int result = connect(sock, (struct sockaddr *)&addr, sizeof(addr));
    if (result == 0) {
        close(sock);
        return true;
    }

    if (errno != EINPROGRESS && errno != EWOULDBLOCK && errno != EAGAIN) {
        close(sock);
        return false;
    }

    fd_set write_fds;
    FD_ZERO(&write_fds);
    FD_SET(sock, &write_fds);
    struct timeval timeout = {
        .tv_sec = 0,
        .tv_usec = HOST_PROBE_TIMEOUT_US,
    };
    result = select(sock + 1, NULL, &write_fds, NULL, &timeout);
    if (result <= 0 || !FD_ISSET(sock, &write_fds)) {
        close(sock);
        return false;
    }

    int socket_error = 0;
    socklen_t socket_error_size = sizeof(socket_error);
    result = getsockopt(sock, SOL_SOCKET, SO_ERROR,
        (char *)&socket_error, &socket_error_size);
    close(sock);
    return result == 0 && socket_error == 0;
}

static bool resolve_host_ip(const char *hostname, char *out_ip, size_t out_len) {
    struct addrinfo hints = {0}, *result = NULL;
    hints.ai_family = AF_INET;
    if (getaddrinfo(hostname, NULL, &hints, &result) != 0) return false;
    struct sockaddr_in *addr = (struct sockaddr_in *)result->ai_addr;
    inet_ntop(AF_INET, &addr->sin_addr, out_ip, out_len);
    freeaddrinfo(result);
    return true;
}

struct host_status check_host_status(const device_info_t *info) {
    struct host_status result = {0};
#ifdef __vita__
    char mdns_ip[64] = "";
    if (find_host_ip_mdns(info->name, mdns_ip, sizeof(mdns_ip))) {
        if (strcmp(mdns_ip, info->internal) == 0) {
            if (ping_host(mdns_ip, info->port)) {
                result.status = HOST_ONLINE;
                strncpy(result.current_ip, mdns_ip, sizeof(result.current_ip));
                return result;
            }
        } else if (ping_host(mdns_ip, info->port)) {
            result.status = HOST_IP_CHANGED;
            strncpy(result.current_ip, mdns_ip, sizeof(result.current_ip));
            return result;
        }
    }
#endif

    if (ping_host(info->internal, info->port)) {
        result.status = HOST_ONLINE;
        strncpy(result.current_ip, info->internal, sizeof(result.current_ip));
        return result;
    }

    if (strcmp(info->name, info->internal) != 0) {
        char resolved_ip[64] = "";
        if (resolve_host_ip(info->name, resolved_ip, sizeof(resolved_ip)) &&
                ping_host(resolved_ip, info->port)) {
            result.status = HOST_IP_CHANGED;
            strncpy(result.current_ip, resolved_ip, sizeof(result.current_ip));
            return result;
        }
    }

    result.status = HOST_OFFLINE;
    strncpy(result.current_ip, info->internal, sizeof(result.current_ip));
    return result;
}
