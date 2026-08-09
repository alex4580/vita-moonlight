#include <stdio.h>
#include "ui_check.h"
#include "ui_connect.h"
#include "guilib.h"
#include "../check_host.h"
#include "../debug.h"
#include <string.h>

// Declarar las variables globales para poder limpiarlas aquí
static void clear_pending_ip_update(void) {
    host_scan_clear_pending_ip_update(-1);
}

int ui_check_ip_update(device_info_t *info, const char *new_ip) {
    vita_debug_log("[UI_CHECK] Al entrar: name=%s, internal=%s, external=%s", info->name, info->internal, info->external);
    // Siempre pedir confirmación, aunque internal esté vacío
    char msg[256];
    snprintf(msg, sizeof(msg),
        "Your host changed its IP address.\nOld: %s\nNew: %s",
        info->internal[0] ? info->internal : "(none)", new_ip);
    int confirm = display_confirm(msg);
    vita_debug_log("[UI_CHECK] Host %s display_confirm result: %d", info->name, confirm);
    if (confirm) {
        /* A matching mDNS name is only a discovery hint. The existing HTTPS
         * pin must authenticate the computer at the new address. */
        if (!check_connection(info->name, new_ip, info->port)) {
            clear_pending_ip_update();
            display_error("The computer at the new address did not match the "
                          "saved Sunshine identity.\n\n"
                          "The saved address was not changed.");
            return 0;
        }
        char previous_ip[sizeof(info->internal)];
        strncpy(previous_ip, info->internal, sizeof(previous_ip) - 1);
        previous_ip[sizeof(previous_ip) - 1] = '\0';
        strncpy(info->internal, new_ip, sizeof(info->internal)-1);
        info->internal[sizeof(info->internal)-1] = '\0';
        info->prefer_external = false;
        info->port = info->port ? info->port : 47989;
        vita_debug_log("[UI_CHECK] Antes de guardar: name=%s, internal=%s, external=%s, port=%d, paired=%d, prefer_external=%d", info->name, info->internal, info->external, info->port, info->paired, info->prefer_external);
        if (!save_device_info(info)) {
            strncpy(info->internal, previous_ip, sizeof(info->internal) - 1);
            info->internal[sizeof(info->internal) - 1] = '\0';
            display_error("The verified address could not be saved.\n"
                          "Check free storage and try again.");
            return 0;
        }
        vita_debug_log("[UI_CHECK] Después de guardar y recargar: name=%s, internal=%s, external=%s, port=%d, paired=%d, prefer_external=%d", info->name, info->internal, info->external, info->port, info->paired, info->prefer_external);
        flash_message("Host IP updated!");
        clear_pending_ip_update();
        return 1;
    } else {
        vita_debug_log("[UI_CHECK] Host %s IP update cancelled by user.", info->name);
        flash_message("Host IP not updated.");
        clear_pending_ip_update();
        return 0;
    }
}
