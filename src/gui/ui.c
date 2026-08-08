#include "ui.h"

#include "guilib.h"
#include "ime.h"

#include "ui_settings.h"
#include "ui_connect.h"
#include "ui_device.h"
#include "ui_check.h"

#include "../config.h"
#include "../device.h"
#include "../connection.h"
#include "../video/vita.h"
#include "../input/vita.h"
#include "../power/vita.h"
#include "../util.h"
#include "../check_host.h"
#include "../debug.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>

#include <psp2/kernel/threadmgr.h>
#include <psp2/ctrl.h>

extern SceNetInitParam net_param;

enum {
  MAIN_MENU_CONNECTED = 100,
  MAIN_MENU_SEARCH,
  MAIN_MENU_CONNECT_RESUME,
  MAIN_MENU_SETTINGS,
  MAIN_MENU_CONNECT,
  MAIN_MENU_CONNECT_PAIRED,
  MAIN_MENU_QUIT = 999,
};

// Variables globales para actualización pendiente de IP
int first_scan_pending = 0;

// Prototipos para el control del escaneo de hosts
#define start_host_scan() start_host_scan_thread()
#define stop_host_scan() stop_host_scan_thread()


enum {
  HOST_MANAGE_INFO = 2000,
  HOST_MANAGE_CONNECT,
  HOST_MANAGE_CHECK_MAC = 3001,
  HOST_MANAGE_WAKE = 3002,
  HOST_MANAGE_DELETE,
  HOST_MANAGE_CHANGE_IP,
  HOST_MANAGE_CHANGE_NAME,
  HOST_MANAGE_FORCE_CONNECT,
  HOST_MANAGE_BACK
};

static const char *device_label(const device_info_t *info) {
  return info->display_name[0] ? info->display_name : info->name;
}

static void connect_saved_device(
    device_info_t *info, int host_index, const char *discovered_ip) {
  if (info->paired) {
    ui_connect_paired_device(info, host_index, discovered_ip);
  } else {
    ui_connect_and_pairing(info);
  }
}

static int ui_host_manage_menu_loop(int cursor, void *context, const input_data *input) {
  device_info_t *info = (device_info_t *)context;
  // Permitir regresar con el botón cancelar (O)
  if ((input->buttons & config.btn_cancel) != 0) {
    return 1; // Salir del menú
  }
  if ((input->buttons & config.btn_confirm) == 0 || (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }
  switch (cursor) {
    case HOST_MANAGE_CHECK_MAC: {
      vita_debug_log("[UI] Menú gestión: check MAC %s", info->name);
      char mac[18] = {0};
      if (info->mac[0]) {
        strncpy(mac, info->mac, sizeof(mac)-1);
        mac[sizeof(mac)-1] = '\0';
        char msg[64];
        snprintf(msg, sizeof(msg), "MAC: %s", mac);
        flash_message(msg);
        vita_debug_log("[UI] MAC guardada para %s: %s", info->internal, mac);
      } else {
        flash_message("No MAC saved for this host");
        vita_debug_log("[UI] No MAC guardada para %s", info->internal);
      }
      sceKernelDelayThread(2*1000*1000); // Esperar 2 segundos para que el mensaje sea visible
      return 1;
    }
    case HOST_MANAGE_WAKE: {
      vita_debug_log("[UI] Menú gestión: Wake-on-LAN %s", info->name);
      if (!info->mac[0]) {
        flash_message("No MAC saved for this host");
        vita_debug_log("[UI] Wake-on-LAN: No MAC for %s", info->name);
        sceKernelDelayThread(2*1000*1000);
        return 1;
      }
      // Calcular broadcast a partir de la IP interna
      /* A limited broadcast works for IPv4 literals and saved DNS names
       * without deriving a subnet from untrusted text. */
      const char *broadcast = "255.255.255.255";
      // Copiar IP y reemplazar el último octeto por 255
      extern bool send_wol_packet(const char *mac, const char *ip_broadcast, int port);
      bool wol_ok = send_wol_packet(info->mac, broadcast, 9);
      if (wol_ok) {
        flash_message("Wake-on-LAN sent!");
        vita_debug_log("[UI] Wake-on-LAN enviado a %s (%s)", info->name, info->mac);
      } else {
        flash_message("Wake-on-LAN failed");
        vita_debug_log("[UI] Wake-on-LAN falló para %s (%s)", info->name, info->mac);
      }
      sceKernelDelayThread(2*1000*1000);
      return 1;
    }
    case HOST_MANAGE_CONNECT: {
      vita_debug_log("[UI] Menú gestión: conectar a %s", info->name);
      stop_host_scan();
      // Si la IP está cambiada, pedir confirmación antes de emparejar
      int host_idx = -1;
      for (int i = 0; i < known_devices.count; i++) {
        if (&known_devices.devices[i] == info) {
          host_idx = i;
          break;
        }
      }
      char discovered_ip[64] = "";
      struct host_status status = {0};
      bool has_discovered_ip = false;
      (void)host_scan_get_snapshot(
          host_idx, &status, discovered_ip, sizeof(discovered_ip),
          &has_discovered_ip);
      /* Saved pinned addresses are always tried first. The unauthenticated
       * mDNS result is only a fallback hint inside the paired connection
       * flow, where it must pass the existing Sunshine certificate pin. */
      connect_saved_device(
          info, host_idx, has_discovered_ip ? discovered_ip : NULL);
      return 1;
    }
    case HOST_MANAGE_DELETE: {
      char msg[320];
      snprintf(msg, sizeof(msg),
               "Forget %s on this Vita?\n\n"
               "This removes local pairing data. To revoke the Vita from "
               "the PC too, remove it from Sunshine's paired clients.",
               device_label(info));
      int res = display_confirm(msg);
      if (res) {
        vita_debug_log("[UI] Host management: delete %s", info->name);
        if (remove_device(info->name)) {
          host_scan_clear_pending_ip_update(-1);
          flash_message("Computer forgotten on this Vita");
        } else {
          flash_message("Error deleting host");
        }
        return 1;
      } else {
        flash_message("Delete cancelled");
        return 0;
      }
    }
    case HOST_MANAGE_CHANGE_IP: {
      vita_debug_log("[UI] Menú gestión: cambiar IP %s", info->name);
      char new_ip[256] = "";
      if (ime_dialog_string(new_ip, sizeof(new_ip), "Enter new IP:",
                            info->internal) == 0 && strlen(new_ip) > 0) {
        if (info->paired &&
            !check_connection(info->name, new_ip, info->port)) {
          display_error("The computer at that address did not match the "
                        "saved Sunshine identity.\n"
                        "The address was not changed.");
          return 1;
        }
        char previous_ip[sizeof(info->internal)];
        strncpy(previous_ip, info->internal, sizeof(previous_ip) - 1);
        previous_ip[sizeof(previous_ip) - 1] = '\0';
        strncpy(info->internal, new_ip, sizeof(info->internal)-1);
        info->internal[sizeof(info->internal)-1] = '\0';
        info->prefer_external = false;
        if (save_device_info(info)) {
          flash_message("IP updated: %s", new_ip);
        } else {
          strncpy(info->internal, previous_ip, sizeof(info->internal) - 1);
          info->internal[sizeof(info->internal) - 1] = '\0';
          display_error("The new address could not be saved.\n"
                        "Check free storage and try again.");
        }
      } else {
        flash_message("IP change cancelled");
      }
      return 1;
    }
    case HOST_MANAGE_CHANGE_NAME: {
      vita_debug_log("[UI] Set display name for %s", info->name);
      char new_name[256] = "";
      if (ime_dialog_string(new_name, sizeof(new_name), "Enter display name:",
                            device_label(info)) == 0 && strlen(new_name) > 0) {
        char previous_name[256];
        strncpy(previous_name, info->display_name,
                sizeof(previous_name) - 1);
        previous_name[sizeof(previous_name) - 1] = '\0';
        strncpy(info->display_name, new_name, sizeof(info->display_name)-1);
        info->display_name[sizeof(info->display_name)-1] = '\0';
        if (save_device_info(info)) {
          flash_message("Display name updated: %s", new_name);
        } else {
          strncpy(info->display_name, previous_name,
                  sizeof(info->display_name) - 1);
          info->display_name[sizeof(info->display_name) - 1] = '\0';
          display_error("The display name could not be saved.\n"
                        "Check free storage and try again.");
        }
      } else {
        flash_message("Name change cancelled");
      }
      return 1;
    }
    case HOST_MANAGE_FORCE_CONNECT:
      vita_debug_log("[UI] Menú gestión: conexión forzada a %s", info->name);
      stop_host_scan();
      connect_saved_device(info, -1, NULL);
      return 1;
    case HOST_MANAGE_BACK:
      return 1;
  }
  return 0;
}

static int ui_host_manage_menu_back(void *context) {
  return 1;
}

void ui_host_manage_menu(device_info_t *info) {
  menu_entry menu[12];
  int idx = 0;
  char title[256];
  snprintf(title, sizeof(title), "Computer: %s", device_label(info));
  // Mensaje principal
  menu[idx++] = (menu_entry){ .name = "Computer", .disabled = true, .color = 0xFFFFFFFF };
  menu[idx++] = (menu_entry){ .name = title, .disabled = true, .color = 0xFF00AAFF };
  // Info IP y estado con punto de color
  char ipinfo[320];
  char status_text[64] = "Unpaired";
  int host_idx = -1;
  for (int i = 0; i < known_devices.count; i++) {
    if (&known_devices.devices[i] == info) {
      host_idx = i;
      break;
    }
  }
  if (host_idx >= 0) {
    struct host_status st = {0};
    char pending_ip[64] = "";
    bool has_pending_ip = false;
    (void)host_scan_get_snapshot(
        host_idx, &st, pending_ip, sizeof(pending_ip), &has_pending_ip);
    if (has_pending_ip) {
      snprintf(status_text, sizeof(status_text), "[IP changed]");
    } else if (st.current_ip[0] && strcmp(info->internal, st.current_ip) != 0) {
      snprintf(status_text, sizeof(status_text), "[IP changed]");
    } else {
      if (st.status == HOST_ONLINE) {
        snprintf(status_text, sizeof(status_text), "%s (Online)", info->paired ? "Paired" : "Unpaired");
      } else {
        snprintf(status_text, sizeof(status_text), "%s (Offline)", info->paired ? "Paired" : "Unpaired");
      }
    }
  }
  // Mostrar IP y puerto juntos
  snprintf(ipinfo, sizeof(ipinfo), "IP: %s  PORT: %d", info->internal, info->port);
  menu[idx] = (menu_entry){ .name = ipinfo, .disabled = true };
  strncpy(menu[idx].subname, status_text, sizeof(menu[idx].subname)-1);
  menu[idx].subname[sizeof(menu[idx].subname)-1] = '\0';
  idx++;
  menu[idx++] = (menu_entry){ .name = "", .disabled = true, .separator = true };
  // Opciones (sin Info)
  menu[idx++] = (menu_entry){ .name = "Connect", .id = HOST_MANAGE_CONNECT };
  // menu[idx++] = (menu_entry){ .name = "Check MAC", .id = HOST_MANAGE_CHECK_MAC }; // Oculta la opción Check MAC
  menu[idx++] = (menu_entry){ .name = "Wake on LAN (WOL)", .id = HOST_MANAGE_WAKE };
// Opción extra para pruebas
  menu[idx++] = (menu_entry){ .name = "Forget on this Vita", .id = HOST_MANAGE_DELETE };
  menu[idx++] = (menu_entry){ .name = "Change IP", .id = HOST_MANAGE_CHANGE_IP };
  menu[idx++] = (menu_entry){ .name = "Set display name", .id = HOST_MANAGE_CHANGE_NAME };
  menu[idx++] = (menu_entry){ .name = "Back", .id = HOST_MANAGE_BACK };
  menu_geom geom = make_geom_centered(600, 320);
  display_menu(menu, idx, &geom, &ui_host_manage_menu_loop, &ui_host_manage_menu_back, NULL, info);
}

int ui_main_menu_loop(int cursor, void *context, const input_data *input) {
  // menu_entry *menu = (menu_entry*)context; // Variable no usada
  bool status_changed = host_scan_take_status_changed();
  // Refresco manual con Triángulo
  if ((input->buttons & SCE_CTRL_TRIANGLE) != 0) {
    vita_debug_log("[UI] Refresco manual solicitado (Triángulo): reiniciando escaneo de hosts");
    stop_host_scan();
    start_host_scan();
    first_scan_pending = 0;
    return 2; // Forzar refresco del menú
  }
  // Refresco automático solo una vez tras el primer escaneo, pero NO si hay IP cambiada
  if (first_scan_pending && status_changed &&
      host_scan_state() != 1) {
    // Si hay IP cambiada, no forzar refresco/reinicio del menú
    int ip_changed = 0;
    for (int i = 0; i < known_devices.count; i++) {
      struct host_status st = {0};
      (void)host_scan_get_snapshot(i, &st, NULL, 0, NULL);
      if (st.status == HOST_IP_CHANGED && strcmp(known_devices.devices[i].internal, st.current_ip) != 0) {
        ip_changed = 1;
        break;
      }
    }
    if (!ip_changed) {
      vita_debug_log("[UI] Refresco automático tras primer escaneo\n");
      first_scan_pending = 0;
      return 2;
    }
    // Si hay IP cambiada, solo limpiar flags pero NO reiniciar menú
    first_scan_pending = 0;
  }
  // Solo refrescar por cambio de estado si el hilo está activo
  if (status_changed && host_scan_state() == 1) {
    vita_debug_log("[UI] Refrescando menú por cambio de estado de host\n");
    return 2; // Forzar refresco del menú
  }

  if ((input->buttons & config.btn_confirm) == 0 || (input->buttons & SCE_CTRL_HOLD) != 0) {
    return 0;
  }
  // Permitir acceder al menú de gestión de host siempre, incluso si está offline o la IP cambió
  if (cursor >= MAIN_MENU_CONNECT_PAIRED && cursor < MAIN_MENU_QUIT) {
    int host_idx = cursor - MAIN_MENU_CONNECT_PAIRED;
    if (host_idx < 0 || host_idx >= known_devices.count) return 0;
    device_info_t *info = &known_devices.devices[host_idx];
    stop_host_scan();
    ui_host_manage_menu(info);
    start_host_scan();
    return 2;
  }

  // Al seleccionar cualquier opción que cambie de menú, detener el escaneo de hosts
  int exit_menu = 0;
  switch (cursor) {
    case MAIN_MENU_CONNECT:
      vita_debug_log("[UI] Seleccionado: Add manually");
      vita_debug_log("[UI] Deteniendo escaneo de hosts antes de entrar a Add manually");
      stop_host_scan();
      ui_connect_manual();
      vita_debug_log("[UI] Deteniendo escaneo de hosts al salir de Add manually");
      stop_host_scan();
      exit_menu = 2;
      break;
    case MAIN_MENU_SEARCH:
      vita_debug_log("[UI] Seleccionado: Search devices");
      vita_debug_log("[UI] Deteniendo escaneo de hosts antes de entrar a Search devices");
      stop_host_scan();
      ui_search_device();
      vita_debug_log("[UI] Deteniendo escaneo de hosts al salir de Search devices");
      stop_host_scan();
      exit_menu = 2;
      break;
    case MAIN_MENU_CONNECT_RESUME:
      vita_debug_log("[UI] Seleccionado: Resume connection");
      vita_debug_log("[UI] Deteniendo escaneo de hosts antes de reanudar conexión");
      stop_host_scan();
      ui_connect_resume();
      exit_menu = 2;
      break;
    case MAIN_MENU_SETTINGS:
      vita_debug_log("[UI] Seleccionado: Settings");
      vita_debug_log("[UI] Deteniendo escaneo de hosts antes de entrar a Settings");
      stop_host_scan();
      ui_settings_menu();
      exit_menu = 0;
      start_host_scan();
      break;
    case MAIN_MENU_QUIT:
      vita_debug_log("[UI] Seleccionado: Quit");
      vita_debug_log("[UI] Deteniendo escaneo de hosts antes de salir");
      stop_host_scan();
      if (connection_get_status() != LI_DISCONNECTED) {
        connection_terminate();
      }
      /* Return through main() so input/motion/power/network resources are
       * released in a defined order and the PS button is always unlocked. */
      exit_menu = 1;
      break;
  }
  // Si se va a salir del menú, detener el escaneo
  if (exit_menu) {
    vita_debug_log("[UI] Saliendo del menú principal, deteniendo escaneo de hosts");
    stop_host_scan();
    return exit_menu;
  }
  return 0;
}

int ui_main_menu_back(void *context) {
  return 1;
}
int ui_main_menu() {
  // Siempre reiniciar escaneo de hosts al entrar al menú principal
  vita_debug_log("[UI] Entrando al menú principal");
  // Solo iniciar escaneo si NO hay conexión activa
  if (!ui_connect_connected()) {
    vita_debug_log("[UI] Iniciando escaneo de hosts (no hay conexión activa)");
    start_host_scan();
    first_scan_pending = 1;
  } else {
    vita_debug_log("[UI] No se inicia escaneo de hosts porque hay conexión activa");
    first_scan_pending = 0;
    stop_host_scan(); // Detener escaneo si por alguna razón sigue activo
  }

  size_t menu_capacity = (size_t)known_devices.count + 10;
  menu_entry *menu = calloc(menu_capacity, sizeof(*menu));
  if (menu == NULL) {
    display_error("Not enough memory to display saved computers.");
    stop_host_scan();
    return 1;
  }
  int idx = 0;

#define MENU_TITLE(NAME) \
  do { \
    menu[idx] = (menu_entry) { .name = "", .disabled = true, .color = 0xff00aa00 }; \
    strcpy(menu[idx].subname, (NAME)); \
    idx++; \
  } while (0)
#define MENU_ENTRY(ID, NAME, DISABLED) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .disabled = (DISABLED), .id = (ID) }; \
    idx++; \
  } while(0)
#define MENU_SEPARATOR(NAME) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .disabled = true, .separator = true }; \
    idx++; \
  } while(0)

  char program_info[256];
#ifdef __vita__
  snprintf(program_info, 256, "Moonlight v%d.%d.%d  [%s]",
           VERSION_MAJOR, VERSION_MINOR, VERSION_PATCH, VITA_BUILD_ID);
  MENU_TITLE(program_info);
#endif

  //char name[256] = {0};
  char addr[256] = {0};
  char resume_msg[512] = {0};
  //if (ui_connect_connected()) {
  //  ui_connect_address(addr);
  //  sprintf(name, "Resume connection to %s", addr);
  //} else {
  //  sprintf(name, "Connect to %s", config.address ? config.address : "none");
  //}

  static char last_ip_logged[256] = "";
  if (ui_connect_connected()) {
    MENU_SEPARATOR("Current connection");
    ui_connect_address(addr, sizeof(addr));
    sprintf(resume_msg, "Resume connection to %s", addr);
    MENU_ENTRY(MAIN_MENU_CONNECT_RESUME, resume_msg, false);
  } else {
    MENU_SEPARATOR("Add new computer");
    MENU_ENTRY(MAIN_MENU_SEARCH, "Search devices ...", false);
    MENU_ENTRY(MAIN_MENU_CONNECT, "Add manually ...", false);

    if (known_devices.count) {
      MENU_SEPARATOR("Saved computers");
      // Usar resultados del hilo de escaneo
      for (int i = 0; i < known_devices.count; i++) {
        device_info_t *cur = &known_devices.devices[i];
        struct host_status st = {0};
        char pending_ip[64] = "";
        bool has_pending_ip = false;
        (void)host_scan_get_snapshot(
            i, &st, pending_ip, sizeof(pending_ip), &has_pending_ip);

        // unsigned int color = 0; // No usado
        // const char* color_str = ""; // No usado
        // bool ip_changed = (st.current_ip[0] && strcmp(cur->internal, st.current_ip) != 0); // No usado
        MENU_ENTRY(MAIN_MENU_CONNECT_PAIRED + i, device_label(cur), false);
        int real_idx = idx - 1;
        menu[real_idx].is_host_entry = true;
        if (!cur->paired) {
          strcpy(menu[real_idx].subname, "Pairing required");
          menu[real_idx].color = 0xFF00FFFF;
          continue;
        }
        // Si hay cambio de IP pendiente para este host, forzar amarillo y saltar el resto
        if (has_pending_ip) {
          strcpy(menu[real_idx].subname, "[IP changed]");
          menu[real_idx].color = 0xFF00FFFF;
          vita_debug_log("[UI] Host %s menu idx=%d, color=0x%08X (YELLOW, IP pendiente), archivo=ui.c", cur->name, real_idx, 0xFF00FFFF);
          continue;
        }
        // ...lógica normal de color...
        if (st.current_ip[0] && strcmp(cur->internal, st.current_ip) != 0) {
          if (strcmp(last_ip_logged, st.current_ip) != 0) {
            vita_debug_log("[UI] Host %s IP changed: old=%s, new=%s", cur->name, cur->internal, st.current_ip);
            strncpy(last_ip_logged, st.current_ip, sizeof(last_ip_logged)-1);
            last_ip_logged[sizeof(last_ip_logged)-1] = '\0';
          }
          strcpy(menu[real_idx].subname, "[IP changed]");
          menu[real_idx].color = 0xFF00FFFF;
          vita_debug_log("[UI] Host %s menu idx=%d, color=0x%08X (YELLOW), archivo=ui.c", cur->name, real_idx, 0xFF00FFFF);
        } else if (strcmp(menu[real_idx].subname, "[IP changed]") == 0) {
          menu[real_idx].color = 0xFF00FFFF;
          vita_debug_log("[UI] Host %s menu idx=%d, color=0x%08X (YELLOW), archivo=ui.c", cur->name, real_idx, 0xFF00FFFF);
        } else if (st.status == HOST_ONLINE) {
          menu[real_idx].color = 0xFF00FF00;
          vita_debug_log("[UI] Host %s menu idx=%d, color=0x%08X (GREEN), archivo=ui.c", cur->name, real_idx, 0xFF00FF00);
        } else {
          menu[real_idx].color = 0xFF0000FF;
          vita_debug_log("[UI] Host %s menu idx=%d, color=0x%08X (RED), archivo=ui.c", cur->name, real_idx, 0xFF0000FF);
        }
      }
    }
  }

  MENU_SEPARATOR("");
  MENU_ENTRY(MAIN_MENU_SETTINGS, "Settings", false);
  MENU_ENTRY(MAIN_MENU_QUIT, "Quit", false);

  // Solo iniciar escaneo si NO hay cambio de IP pendiente y no se ha hecho ya
  menu_geom geom = make_geom_centered(500, 200);
  int result = display_menu(
      menu, idx, &geom, &ui_main_menu_loop, &ui_main_menu_back, NULL, NULL);
  /* Back/O exits display_menu without passing through the explicit Quit
   * entry. Join the scanner on every return path before main() tears down the
   * Vita network stack. stop_host_scan() is idempotent when a menu action
   * already stopped it. */
  stop_host_scan();
  free(menu);
  return result;
}

int global_loop(int cursor, void *ctx, const input_data *input) {
  if (is_rectangle_touched(&input->touch, 0, 0, 150, 150)) {
    if (connection_get_status() == LI_MINIMIZED) {
      vitapower_config(config);
      vitainput_config(config);

      sceKernelDelayThread(500 * 1000);
      connection_resume();

      while (connection_is_connected()) {
        sceKernelDelayThread(500 * 1000);
      }
    }
  }
  return 0;
}

void gui_init() {
  guilib_init(&global_loop, NULL);
}

void gui_loop() {
  gui_init();

  while (ui_main_menu() == 2);

  vita2d_fini();
}
