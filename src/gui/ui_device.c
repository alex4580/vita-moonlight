#include "ui_settings.h"

#include "guilib.h"
#include "ime.h"

#include "../config.h"
#include "../device.h"
#include "../connection.h"
//#include "../debug.h"
#include "../input/vita.h"
#include "ui_connect.h"
#include "debug.h"
#include "udp_sniffer_vita.h"

#include <assert.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <netdb.h>

#include <psp2/ctrl.h>
#include <psp2/rtc.h>
#include <psp2/touch.h>
#include <psp2/io/stat.h>
#include <psp2/kernel/processmgr.h>
#include <vita2d.h>
#include <Limelight.h>
#include <client.h>
#include <errors.h>

#define MILLISECOND 1000
#define SECOND      (1000 * MILLISECOND)
#define MAX_DISCOVERED_DEVICES 60
#define DEVICE_SUFFIX_LENGTH 128

enum {
  DEVICE_EXIT_SEARCH = 100,
  DEVICE_ITEM,
};

enum {
  SEARCH_THREAD_IDLE,
  SEARCH_THREAD_RUNNING,
  SEARCH_THREAD_REQ_STOP,
};

static int search_thread_status = SEARCH_THREAD_IDLE;

static device_info_t devices[MAX_DISCOVERED_DEVICES];
static char device_suffixes[MAX_DISCOVERED_DEVICES][DEVICE_SUFFIX_LENGTH];
static int found_device = 0;

static int search_status_load(void) {
  return __atomic_load_n(&search_thread_status, __ATOMIC_ACQUIRE);
}

static void search_status_store(int status) {
  __atomic_store_n(&search_thread_status, status, __ATOMIC_RELEASE);
}

static int discovered_device_count(void) {
  return __atomic_load_n(&found_device, __ATOMIC_ACQUIRE);
}

static void publish_device_count(int count) {
  __atomic_store_n(&found_device, count, __ATOMIC_RELEASE);
}

void ipv4_address_to_string(const struct sockaddr_in *addr, char *ip, const size_t len) {
  inet_ntop(AF_INET, &addr->sin_addr.s_addr, ip, len);
}

// part of publib. BSD license
// https://github.com/ajkaijanaho/publib/blob/master/strutil/strrstr.c
char* strrstr(const char *str, const char *pat) {
  size_t len, patlen;
  const char *p;

  assert(str != NULL);
  assert(pat != NULL);

  len = strlen(str);
  patlen = strlen(pat);

  if (patlen > len)
    return NULL;
  for (p = str + (len - patlen); p > str; --p)
    if (*p == *pat && strncmp(p, pat, patlen) == 0)
      return (char *) p;
  return NULL;
}

static void moonlight_found_callback(int idx, const char* host, const char* pcname, const char* ip, int port) {
    (void)idx;
    (void)host;
    if (pcname == NULL || pcname[0] == '\0' || ip == NULL || ip[0] == '\0' ||
        port <= 0 || port > UINT16_MAX) {
        vita_debug_log("[mDNS] Ignoring an incomplete discovery response\n");
        return;
    }
    vita_debug_log("[mDNS] Dispositivo encontrado: %s (%s:%d)\n", pcname, ip, port);
    int count = discovered_device_count();
    // Verificar si ya existe un dispositivo con el mismo nombre y misma IP
    for (int i = 0; i < count; i++) {
        if (strncmp(devices[i].name, pcname, sizeof(devices[i].name)) == 0 &&
            strncmp(devices[i].internal, ip, sizeof(devices[i].internal)) == 0) {
            vita_debug_log("[mDNS] Dispositivo duplicado ignorado: %s (%s)\n", pcname, ip);
            return;
        }
    }
    // Si el nombre es igual pero la IP es diferente, sí se agrega
    if (count >= MAX_DISCOVERED_DEVICES) {
        vita_debug_log("[mDNS] Device limit reached; ignoring %s\n", pcname);
        return;
    }
    memset(&devices[count], 0, sizeof(device_info_t));
    strncpy(devices[count].name, pcname, sizeof(devices[count].name)-1);
    strncpy(devices[count].internal, ip, sizeof(devices[count].internal)-1);
    devices[count].port = port;
    /* Publish only after every field in this immutable discovery slot is
     * complete. The menu acquires the count before reading the slot. */
    publish_device_count(count + 1);
}

int mdns_discovery_main(SceSize args, void *argp) {
  vita_debug_log("[mDNS] Iniciando búsqueda de dispositivos...\n");
  if (search_status_load() != SEARCH_THREAD_IDLE) {
    vita_debug_log("[mDNS] search_thread_status != IDLE, saliendo.\n");
    return 0;
  }

  search_status_store(SEARCH_THREAD_RUNNING);
  publish_device_count(0);

  udp_sniffer_vita_deinit(); // Cierra socket y limpia estado
  udp_sniffer_vita_init();   // Limpia estado (opcional, por simetría)
  udp_sniffer_vita_set_callback(moonlight_found_callback);

  // Esperar y procesar durante 10 segundos máximo
  int ticks = 0;
  int max_ticks = 100; // 100 * 100ms = 10s
  while (search_status_load() == SEARCH_THREAD_RUNNING && ticks < max_ticks) {
      udp_sniffer_vita_poll();
      sceKernelDelayThread(1000 * 100); // 100ms
      ticks++;
  }

  udp_sniffer_vita_set_callback(NULL);
  udp_sniffer_vita_deinit();
  search_status_store(SEARCH_THREAD_IDLE);
  vita_debug_log("[mDNS] Búsqueda finalizada. found_device=%d\n",
                 discovered_device_count());
  return 0;
}

static SceUID search_thread_id = -1;
static int end_search_thread(SceUID thid);

static int stop_search_thread_if_running() {
  vita_debug_log("[mDNS] stop_search_thread_if_running: status=%d, thid=%d\n",
                 search_status_load(), search_thread_id);
  if (search_thread_id >= 0) {
    vita_debug_log("[mDNS] Llamando a end_search_thread(%d)\n", search_thread_id);
    int result = end_search_thread(search_thread_id);
    if (result < 0) {
      vita_debug_log("[mDNS] Unable to join discovery thread %d: %d\n",
                     search_thread_id, result);
      return result;
    }
    vita_debug_log("[mDNS] end_search_thread terminado\n");
    search_thread_id = -1;
  }
  return 0;
}

static void clear_devices() {
  vita_debug_log("[mDNS] Limpiando lista de dispositivos\n");
  memset(devices, 0, sizeof(devices));
  memset(device_suffixes, 0, sizeof(device_suffixes));
  publish_device_count(0);
}

SceUID start_search_thread() {
  vita_debug_log("[mDNS] start_search_thread: limpiando y creando hilo\n");
  if (stop_search_thread_if_running() < 0) {
    return -1;
  }
  clear_devices();
  SceUID thid = sceKernelCreateThread("mdns", mdns_discovery_main, 0x10000100, 0x10000, 0, 0, NULL);
  if (thid < 0) {
    vita_debug_log("[mDNS] Error creando hilo de búsqueda\n");
    return -1;
  }
  int start_result = sceKernelStartThread(thid, 0, 0);
  if (start_result < 0) {
    sceKernelDeleteThread(thid);
    vita_debug_log("[mDNS] Unable to start discovery thread: %d\n",
                   start_result);
    return -1;
  }
  search_thread_id = thid;
  vita_debug_log("[mDNS] Hilo de búsqueda iniciado: thid=%d\n", thid);
  return thid;
}

static int end_search_thread(SceUID thid) {
  vita_debug_log("[mDNS] end_search_thread: solicitando parada de hilo %d\n", thid);
  search_status_store(SEARCH_THREAD_REQ_STOP);

  int result = sceKernelWaitThreadEnd(thid, NULL, NULL);
  if (result < 0) return result;
  result = sceKernelDeleteThread(thid);
  if (result < 0) return result;
  udp_sniffer_vita_set_callback(NULL);
  udp_sniffer_vita_deinit();
  search_status_store(SEARCH_THREAD_IDLE);
  vita_debug_log("[mDNS] Hilo %d eliminado\n", thid);
  return 0;
}

static int ui_search_device_callback(int id, void *context, const input_data *input) {
  int count = discovered_device_count();
  int rendered_count = context ? *(const int *)context : count;
  if ((input->buttons & SCE_CTRL_TRIANGLE) != 0) {
    vita_debug_log("[mDNS] TRIÁNGULO presionado: refrescando búsqueda\n");
    if (stop_search_thread_if_running() < 0 || start_search_thread() < 0) {
      display_error("Computer discovery could not restart.\n"
                    "Return to the main menu and try again.");
    }
    return 2; // Fuerza refresco del menú
  }
  if ((input->buttons & config.btn_confirm) == 0 || (input->buttons & SCE_CTRL_HOLD) != 0) {
    /* Rebuild only when discovery published a new immutable snapshot. The old
     * tag sentinel aliased the Return slot when count was zero and retriggered
     * forever when the last result was an already-paired host. */
    if (count != rendered_count) {
      return 2;
    }
    return 0;
  }
  if (id == DEVICE_EXIT_SEARCH) {
    return 1;
  }
  if (id >= DEVICE_ITEM) {
    int device_index = id - DEVICE_ITEM;
    if (device_index < 0 || device_index >= count) {
      return 2;
    }

    // Usar IP y puerto detectados por mDNS
    device_info_t *dev = &devices[device_index];
    // La MAC ya está en dev->mac si mDNS la proveyó
    /* Discovery owns the shared mDNS socket. Fully join it before the
     * synchronous PIN/TLS flow starts. */
    if (stop_search_thread_if_running() < 0) {
      display_error("Computer discovery is still stopping.\n"
                    "Return to the main menu and try pairing again.");
      return 0;
    }
    device_info_t *info = ui_connect_and_pairing(dev);
    if (info == NULL) {
      return 0;
    }

    unsigned int extern_addr = 0;
    if (LiFindExternalAddressIP4("stun.stunprotocol.org", 3478, &extern_addr) == 0) {
      struct sockaddr_in addr;
      addr.sin_family = AF_INET;
      addr.sin_addr.s_addr = extern_addr;
      ipv4_address_to_string(&addr, info->external, 16);
    }

    if (!save_device_info(info)) {
      display_error("The external address could not be saved.\n"
                    "Local streaming is still available.");
    }

    return 1;
  }
  //TODO: Return proper on all code paths
  return 0;
}

static int ui_search_device_back(void *context) {
  return 0;
}

int ui_search_device_loop() {
  int idx = 0;
  int count = discovered_device_count();
  int rendered_count = count;
  menu_entry menu[MAX_DISCOVERED_DEVICES + 4];

#define MENU_CATEGORY(NAME) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .disabled = true, .separator = true }; \
    idx++; \
  } while (0)
#define MENU_ENTRY(ID, NAME, SUFFIX) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .id = (ID), .suffix = (SUFFIX) }; \
    idx++; \
  } while(0)
#define MENU_MESSAGE(MESSAGE) \
  do { \
    menu[idx] = (menu_entry) { .name = "", .disabled = true, .subname = (MESSAGE) }; \
    idx++; \
  } while(0)
#define MENU_SEPARATOR() \
  do { \
    menu[idx] = (menu_entry) { .name = "", .disabled = true, .separator = true }; \
    idx++; \
  } while(0)

  // Mostrar mensaje de refresco con triángulo
  MENU_MESSAGE("Press TRIANGLE to refresh the search");

  MENU_CATEGORY("Search device ...");
  for (int i = 0; i < count; i++) {
    if (devices[i].internal[0] == '\0') {
      continue;
    }
    device_info_t *p = find_device(devices[i].name);
    if (p && p->paired) {
      continue;
    }
    // Mostrar MAC en el sufijo si está disponible
    if (devices[i].mac[0]) {
      snprintf(
          device_suffixes[i], sizeof(device_suffixes[i]), "%s [%s]",
          devices[i].internal, devices[i].mac);
    } else {
      snprintf(
          device_suffixes[i], sizeof(device_suffixes[i]), "%s",
          devices[i].internal);
    }
    MENU_ENTRY(DEVICE_ITEM + i, devices[i].name, device_suffixes[i]);
  }
  MENU_SEPARATOR();
  MENU_ENTRY(DEVICE_EXIT_SEARCH, "Return", "");

  return display_menu(
      menu, idx, NULL, &ui_search_device_callback,
      &ui_search_device_back, NULL, &rendered_count);
}

void ui_search_device() {
  stop_search_thread_if_running(); // Siempre detener cualquier búsqueda previa
  if (start_search_thread() < 0) { // Siempre iniciar búsqueda al entrar
    display_error("Computer discovery could not start.\n"
                  "Return to the main menu and try again.");
    return;
  }

  while (ui_search_device_loop() == 2);

  stop_search_thread_if_running(); // Detener búsqueda al salir
}
