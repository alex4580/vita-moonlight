#include "ui_connect.h"

#include "Limelight.h"
#include "errors.h"
#include "guilib.h"
#include "ime.h"
#include "ui_settings.h"
#include "ui_diagnostics.h"
#include "ui_stream_overlay.h"

#include "../connection.h"
#include "../configuration.h"
#include "../video.h"
#include "../config.h"
#include "../util.h"
#include "../device.h"
#include "../debug.h"

#include "client.h"
#include "errors.h"
#include "../platform.h"

#include "../power/vita.h"
#include "../input/vita.h"

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>

#include <psp2/kernel/threadmgr.h>
#include <psp2/ctrl.h>
#include <psp2/io/stat.h>
#include <vita2d.h>

static void send_host_rescue_hotkey(int virtual_key) {
  LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
  LiSendKeyboardEvent(0x11, KEY_ACTION_DOWN, 0); // Control
  LiSendKeyboardEvent(0x12, KEY_ACTION_DOWN, 0); // Alt
  LiSendKeyboardEvent(0x10, KEY_ACTION_DOWN, 0); // Shift
  LiSendKeyboardEvent(virtual_key, KEY_ACTION_DOWN, 0);
  LiSendKeyboardEvent(virtual_key, KEY_ACTION_UP, 0);
  LiSendKeyboardEvent(0x10, KEY_ACTION_UP, 0);
  LiSendKeyboardEvent(0x12, KEY_ACTION_UP, 0);
  LiSendKeyboardEvent(0x11, KEY_ACTION_UP, 0);
}

static int send_windows_task_manager_shortcut(void) {
  int result = 0;
#define SEND_TASK_MANAGER_KEY(key, action) do { \
  int send_result = LiSendKeyboardEvent((key), (action), 0); \
  if (send_result != 0 && result == 0) result = send_result; \
} while (0)

  /* Ctrl+Shift+Esc is handled entirely through Moonlight's encrypted input
   * channel. Neutralize the controller first, then always send key-up events
   * so a partial queue failure cannot intentionally leave modifiers held. */
  if (LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0) != 0) {
    result = -1;
  }
  SEND_TASK_MANAGER_KEY(0x11, KEY_ACTION_DOWN); // Control
  SEND_TASK_MANAGER_KEY(0x10, KEY_ACTION_DOWN); // Shift
  SEND_TASK_MANAGER_KEY(0x1B, KEY_ACTION_DOWN); // Escape
  SEND_TASK_MANAGER_KEY(0x1B, KEY_ACTION_UP);
  SEND_TASK_MANAGER_KEY(0x10, KEY_ACTION_UP);
  SEND_TASK_MANAGER_KEY(0x11, KEY_ACTION_UP);
#undef SEND_TASK_MANAGER_KEY
  return result;
}

SERVER_DATA server;
PAPP_LIST server_applist;
int pos[2];

#define HOST_KEY_DIRECTORY_CAPACITY 1024u
#define HOST_KEY_FILE_SUFFIX_RESERVE 32u
#define HOST_KEY_COMPONENT_MAX 255u

static const char *connection_error_message(void) {
  if (gs_error != NULL &&
      (strstr(gs_error, "identity changed") != NULL ||
       strstr(gs_error, "saved Sunshine identity") != NULL)) {
    return "This PC's Sunshine identity no longer matches the saved PC.\n"
           "Delete the saved PC in Vita Moonlight, add it again, then choose "
           "Pair securely.";
  }
  return gs_error == NULL ? "No additional details" : gs_error;
}

static bool build_host_key_directory(char *output, size_t output_size,
                                     const char *host_name) {
  size_t base_length;
  size_t name_length;
  const char *separator;
  int written;

  if (output == NULL || output_size == 0 || host_name == NULL) {
    gs_error = "The saved PC name is invalid. Delete it and add the PC again.";
    return false;
  }

  for (name_length = 0;
       name_length <= HOST_KEY_COMPONENT_MAX && host_name[name_length] != '\0';
       name_length++) {
    unsigned char character = (unsigned char) host_name[name_length];
    if (character < 0x20 || character == 0x7f || character == '/' ||
        character == '\\' || character == ':') {
      gs_error = "The saved PC name contains a path character. Delete it, add "
                 "the PC again, and use a simple name without /, \\, or :.";
      return false;
    }
  }
  if (name_length == 0 || name_length > HOST_KEY_COMPONENT_MAX ||
      strcmp(host_name, ".") == 0 || strcmp(host_name, "..") == 0) {
    gs_error = "The saved PC name cannot be used safely. Delete it, add the PC "
               "again, and use a name from 1 to 255 characters.";
    return false;
  }

  base_length = strlen(config.key_dir);
  if (base_length == 0) {
    gs_error = "The Vita pairing-data directory is not configured.";
    return false;
  }
  separator = config.key_dir[base_length - 1] == '/' ? "" : "/";
  written = snprintf(output, output_size, "%s%s%s",
                     config.key_dir, separator, host_name);
  if (written < 0 || (size_t) written >= output_size ||
      output_size < HOST_KEY_FILE_SUFFIX_RESERVE ||
      (size_t) written >= output_size - HOST_KEY_FILE_SUFFIX_RESERVE) {
    output[0] = '\0';
    gs_error = "The saved PC name makes the Vita pairing-data path too long. "
               "Delete it and add the PC again with a shorter name.";
    return false;
  }
  return true;
}

static bool release_host_client_state(void) {
  int status = connection_get_status();
  int transition_result = 0;

  if (status == LI_READY)
    transition_result = connection_abort_attempt();
  else if (status != LI_DISCONNECTED)
    transition_result = connection_terminate();

  if (transition_result != 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "connection.state",
        "state=release_blocked reason=transition_failed code=%d",
        transition_result);
    return false;
  }

  gs_free_applist(&server_applist);
  gs_cleanup(&server);
  return true;
}

int get_app_id(PAPP_LIST list, char *name) {
  while (list != NULL) {
    if (strcmp(list->name, name) == 0)
      return list->id;

    list = list->next;
  }
  return -1;
}

int get_app_name(PAPP_LIST list, int id, char *name, size_t name_size) {
  if (name == NULL || name_size == 0) return 0;
  while (list != NULL) {
    if (list->id == id) {
      snprintf(name, name_size, "%s", list->name);
      return 1;
    }

    list = list->next;
  }

  return 0;
}

void ui_connect_stream(int appId) {
  // TODO support force controller id
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "stream.action",
      "action=connect state=starting phase=app_start width=%d height=%d "
      "fps=%d bitrate_kbps=%d",
      config.stream.width, config.stream.height,
      config.stream.fps, config.stream.bitrate);
  int ret = gs_start_app(&server, &config.stream, appId, config.sops, config.localaudio, 1);
  if (ret < 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "stream.action",
        "action=connect state=failed phase=app_start code=%d", ret);
    if (ret == GS_NOT_SUPPORTED_4K)
      display_error("Server doesn't support 4K\n");
    else if (ret == GS_NOT_SUPPORTED_MODE)
      display_error("Server doesn't support %dx%d (%d fps)\n", config.stream.width, config.stream.height, config.stream.fps);
    else
      display_error("Errorcode starting app: %d\n", ret);

    return;
  }
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "stream.action",
      "action=connect state=ready phase=app_start");

  enum platform system = VITA;
  int drFlags = 0;

  if (config.fullscreen)
    drFlags |= DISPLAY_FULLSCREEN;

  DECODER_RENDERER_CALLBACKS *video_callback = platform_get_video(system);
  /* The Vita decoder rewrites num_ref_frames/max_dec_frame_buffering to one.
   * RFI is invalid with a patched reference structure and can corrupt video
   * after packet loss, so recovery must use a clean IDR frame instead. */
  video_callback->capabilities &=
      ~CAPABILITY_REFERENCE_FRAME_INVALIDATION_AVC;

  // --- Ajuste para soporte Host Resolution: no modificar resolución si es -1 ---
  // Eliminados g_requested_width y g_requested_height, lógica simplificada
  UiDiagnosticsReconnectSettings active_settings;
  ui_diagnostics_capture_reconnect_settings(&active_settings);
  int orig_width = config.stream.width;
  int orig_height = config.stream.height;
  // Si se seleccionó una resolución específica, se mantiene; si es -1, se deja al host
  // (No se modifica config.stream.width/height aquí)

  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "stream.action",
      "action=connect state=starting phase=stream_start");
  ret = LiStartConnection(&server.serverInfo, &config.stream, &connection_callbacks,
                          video_callback, platform_get_audio(system),
                          NULL, drFlags, NULL, 0);

  // Restaurar resolución real para el framebuffer/render
  config.stream.width = orig_width;
  config.stream.height = orig_height;

  if (ret == 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_INFO, "stream.action",
        "action=connect state=complete phase=stream_start");
    ui_diagnostics_set_active_stream(&active_settings);
    server.currentGame = appId;
  } else {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "stream.action",
        "action=connect state=failed phase=stream_start stage_id=%d code=%d",
        connection_stage, ret);
    
    const char* connection_failed_stage_name = LiGetStageName(connection_stage);

    display_error("Failed to start stream:\nFailed stage: %s\n(error code %d)",
                  connection_failed_stage_name, ret);
    return;
  }
}

enum {
  /* Sunshine App IDs are positive. Keep local menu actions in a separate
   * namespace so an ordinary App can never trigger a control action. */
  CONNECT_PAIRUNPAIR = -1001,
  CONNECT_DISCONNECT = -1002,
  CONNECT_QUITAPP = -1003
};

#define QUIT_RELOAD 2

int ui_connect_loop(int id, void *context, const input_data *input) {
  int status = connection_get_status();

  if (status == LI_DISCONNECTED) {
      goto disconnect;
  }

  menu_entry *menu = context;
  for (int i = pos[0]; i < pos[1]; i += 1) {
    menu[i].disabled = (server.currentGame != 0);
  }

  if ((input->buttons & config.btn_confirm) == 0 || input->buttons & SCE_CTRL_HOLD) {
    return 0;
  }

  int ret;

  switch (id) {
    case CONNECT_PAIRUNPAIR:
      if (server.paired) {
        flash_message("Unpairing...");
        ret = gs_unpair(&server);
        if (ret == GS_OK) {
          if (connection_terminate()) {
            display_error("Reconnect failed: %d", -1);
            return 0;
          }
          return QUIT_RELOAD;
        }
        display_error("Unpairing failed: %d", ret);
        return 0;
      }

      char pin[5];
      if (gs_generate_pin(pin) != GS_OK) {
        display_error("Could not create a secure pairing PIN\n%s",
                      connection_error_message());
        return 0;
      }
      flash_message("Please enter the following PIN\non the target PC:\n\n%s", pin);
      ret = gs_pair(&server, &pin[0]);
      if (ret == 0) {
        if (connection_paired() != 0) {
          display_error("Pairing completed, but the Vita connection state "
                        "could not be updated. Reconnect and try again.");
          release_host_client_state();
          return 1;
        }
        // After pairing, save server MAC into known device if present
        device_info_t *dev = find_device_by_address(server.serverInfo.address);
        if (dev) {
          dev->paired = true;
          char mac[18] = {0};
          if (gs_get_server_mac(&server, mac, sizeof(mac)) == GS_OK && mac[0]) {
            strncpy(dev->mac, mac, 17);
            dev->mac[17] = '\0';
          }
          if (!save_device_info(dev)) {
            display_error("Pairing succeeded, but the Vita could not save it.\n"
                          "Check free storage before restarting Moonlight.");
          }
          // Notify user pairing succeeded: show a short message so the PIN dialog
          // (which was drawn earlier) is replaced by a success message.
          flash_message("Paired: %s", dev->name);
        }
        /* No media connection exists yet. Remain in LI_PAIRED while the
         * menu reloads so the applications view can open immediately. */
        return QUIT_RELOAD;
      }
      display_error("Pairing failed: %d\n%s", ret,
                    connection_error_message());
      return 0;

    case CONNECT_DISCONNECT:
      goto disconnect;

    case CONNECT_QUITAPP:
      flash_message("Quitting...");
      ret = gs_quit_app(&server);
      if (ret == GS_OK) {
        if (connection_paired() != 0) {
          display_error("The app closed, but the Vita connection state could "
                        "not be updated. Reconnect and try again.");
          release_host_client_state();
          return 1;
        }
        server.currentGame = 0;
        return QUIT_RELOAD;
      }
      display_error("Quitting failed: %d", ret);
      return 0;

    // application launcher / resume
    default:
      vitapower_config(config);
      vitainput_config(config);

      switch (status) {
        case LI_MINIMIZED:
          if (server.currentGame == id) {
            sceKernelDelayThread(500 * 1000);
            connection_resume();
            break;
          }
          // TODO: stop previous stream
        case LI_PAIRED:
          flash_message("Stream starting...");
          ui_connect_stream(id);
          break;
      }

//mainloop:
      while (connection_is_connected()) {
        bool apply_display = stream_overlay_take_apply_display_request();
        bool apply_input = stream_overlay_take_apply_input_request();
        if (apply_display || apply_input) {
          int reconnect_app = server.currentGame != 0
              ? server.currentGame
              : id;
          if (apply_display) {
            vita_debug_event(
                VITA_DEBUG_LEVEL_INFO, "stream.action",
                "action=reconnect state=requested reason=display_settings "
                "width=%d height=%d "
                "fps=%d",
                config.stream.width, config.stream.height, config.stream.fps);

          } else {
            vita_debug_event(
                VITA_DEBUG_LEVEL_INFO, "stream.action",
                "action=reconnect state=requested reason=input_settings");
          }
          connection_terminate();
          sceKernelDelayThread(500 * 1000);

          ret = gs_refresh(&server);
          if (ret != GS_OK) {
            vita_debug_event(
                VITA_DEBUG_LEVEL_ERROR, "stream.action",
                "action=reconnect state=failed phase=refresh reason=%s code=%d",
                apply_display ? "display_settings" : "input_settings", ret);
            display_error(
                "%s reconnect refresh failed: %d\n%s",
                apply_display ? "Display" : "Input", ret,
                connection_error_message());
            break;
          }

          ret = connection_reset();
          if (ret == 0)
            ret = connection_paired();
          if (ret != 0) {
            vita_debug_event(
                VITA_DEBUG_LEVEL_ERROR, "stream.action",
                "action=reconnect state=failed phase=state_reset reason=%s "
                "code=%d",
                apply_display ? "display_settings" : "input_settings", ret);
            if (connection_get_status() == LI_READY)
              connection_abort_attempt();
            break;
          }
          vitapower_config(config);
          vitainput_config(config);
          ui_connect_stream(reconnect_app);
          if (!connection_is_connected()) {
            vita_debug_event(
                VITA_DEBUG_LEVEL_ERROR, "stream.action",
                "action=reconnect state=failed phase=stream_start reason=%s",
                apply_display ? "display_settings" : "input_settings");
            break;
          }
          vita_debug_event(
              VITA_DEBUG_LEVEL_INFO, "stream.action",
              "action=reconnect state=complete reason=%s",
              apply_display ? "display_settings" : "input_settings");
          continue;
        }
        if (stream_overlay_take_task_manager_request()) {
          int task_manager_result = send_windows_task_manager_shortcut();
          vita_debug_event(
              task_manager_result == 0
                  ? VITA_DEBUG_LEVEL_INFO
                  : VITA_DEBUG_LEVEL_ERROR,
              "stream.action",
              "action=open_task_manager state=%s code=%d",
              task_manager_result == 0 ? "sent" : "failed",
              task_manager_result);
        }
        if (stream_overlay_take_quit_app_request()) {
          vita_debug_event(
              VITA_DEBUG_LEVEL_INFO, "stream.action",
              "action=stop_stream_app state=requested");
          ret = gs_quit_app(&server);
          if (ret == GS_OK) {
            vita_debug_event(
                VITA_DEBUG_LEVEL_INFO, "stream.action",
                "action=stop_stream_app state=complete");
            server.currentGame = 0;
            connection_terminate();
            break;
          }
          vita_debug_event(
              VITA_DEBUG_LEVEL_ERROR, "stream.action",
              "action=stop_stream_app state=failed code=%d", ret);
        }
        if (stream_overlay_take_recover_host_request()) {
          vita_debug_event(
              VITA_DEBUG_LEVEL_WARNING, "stream.action",
              "action=recover_display state=requested");
          send_host_rescue_hotkey(0x7A); // F11
          sceKernelDelayThread(350 * 1000);
          connection_terminate();
          break;
        }
        if (stream_overlay_take_disconnect_request()) {
          connection_terminate();
          break;
        }
        sceKernelDelayThread(50 * 1000);
      }

      int status = connection_get_status();

      if (status == LI_DISCONNECTED) {
          goto disconnect;
      }

      return QUIT_RELOAD;
  }

disconnect:
  flash_message("Disconnecting...");
  status = connection_get_status();
  if (status == LI_READY)
    connection_abort_attempt();
  else if (status != LI_DISCONNECTED)
    connection_terminate();
  sceKernelDelayThread(1000 * 1000);
  release_host_client_state();
  return 1;
}

int ui_connect(char *name, char *address, uint16_t port) {
  int ret;
  if (!connection_is_ready()) {
    flash_message("Connecting to:\n %s:%d...", address, port);
    vita_debug_event(
        VITA_DEBUG_LEVEL_INFO, "stream.action",
        "action=connect state=starting phase=host_init");

    char key_dir[HOST_KEY_DIRECTORY_CAPACITY];
    if (!build_host_key_directory(key_dir, sizeof(key_dir), name)) {
      display_error("Can't prepare this saved PC\n%s",
                    connection_error_message());
      return 0;
    }

    ret = gs_init(
        &server, address, port, key_dir,
        vita_debug_is_logging_enabled() ? 3 : 0,
        config.unsupported_version);
    if (ret != GS_OK && ret != GS_UNSUPPORTED_VERSION) {
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "stream.action",
          "action=connect state=failed phase=host_init code=%d", ret);
    }
    if (ret == GS_OUT_OF_MEMORY) {
      display_error("Not enough memory");
      release_host_client_state();
      return 0;
    } else if (ret == GS_INVALID) {
      display_error("Invalid data received from server: %s\n%s", address,
                    connection_error_message());
      release_host_client_state();
      return 0;
    } else if (ret == GS_UNSUPPORTED_VERSION) {
      if (!config.unsupported_version) {
        vita_debug_event(
            VITA_DEBUG_LEVEL_ERROR, "stream.action",
            "action=connect state=failed phase=host_init code=%d", ret);
        display_error("Unsupported version: %s\n",
                      connection_error_message());
        release_host_client_state();
        return 0;
      }
    } else if (ret == GS_ERROR) {
      display_error("Gamestream error: %s\n", connection_error_message());
      release_host_client_state();
      return 0;
    } else if (ret != GS_OK) {
      display_error("Can't connect to server\n%s\n%s", address,
                    connection_error_message());
      release_host_client_state();
      return 0;
    }

    vita_debug_event(
        ret == GS_OK ? VITA_DEBUG_LEVEL_INFO : VITA_DEBUG_LEVEL_WARNING,
        "stream.action",
        "action=connect state=ready phase=host_init code=%d", ret);
    if (connection_reset() != 0) {
      display_error("Could not begin the Vita connection attempt.\n"
                    "Disconnect and try this PC again.");
      release_host_client_state();
      return 0;
    }
  }
  return 1;
}

int ui_connected_menu() {
  int ret;
  int app_count = 0;
  pos[0] = 0;
  pos[1] = 0;
  gs_free_applist(&server_applist);
  if (server.paired) {
    ret = gs_applist(&server, &server_applist);
    if (ret != GS_OK) {
      display_error("Can't load this PC's applications.\n%d\n%s", ret,
                    connection_error_message());
      release_host_client_state();
      return 0;
    }

    if (server_applist != NULL) {
      sort_app_list(server_applist);
      PAPP_LIST list = server_applist;
      while (list) {
        list = list->next;
        app_count += 1;
      }
    }
  }

  /* App data is supplied by the host. Keep the potentially large menu off the
   * Vita's main-thread stack even though the XML parser also bounds its list. */
  struct menu_entry *menu = (struct menu_entry *) calloc(
      (size_t) app_count + 16u, sizeof(*menu));
  if (menu == NULL) {
    display_error("Not enough memory to show this PC's applications.");
    release_host_client_state();
    return 0;
  }

  int idx = 0;

#define MENU_CATEGORY(NAME) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .disabled = true, .separator = true }; \
    idx++; \
  } while (0)
#define MENU_ENTRY(ID, NAME) \
  do { \
    menu[idx] = (menu_entry) { .name = (NAME), .id = (ID) }; \
    idx++; \
  } while(0)
#define MENU_MESSAGE(MESSAGE, COLOR) \
  do { \
    menu[idx] = (menu_entry) { .name = (MESSAGE), .disabled = true, .color = (COLOR) }; \
    idx++; \
  } while(0)

  //header
  //MENU
  MENU_MESSAGE("Connected to the server:", 0xffffffff);
  char server_info[256];
  unsigned short port = server.httpPort ? server.httpPort : server.httpsPort;
  snprintf(server_info, sizeof(server_info),
           "IP: %s  PORT: %d, GPU %s, GFE %s",
           server.serverInfo.address,
           port,
           server.gpuType,
           server.serverInfo.serverInfoGfeVersion);

  MENU_MESSAGE(server_info, 0xffffffff);
  MENU_MESSAGE("", 0);

  if (!server.paired) {
    // pairing
    MENU_CATEGORY(server.securePairingRequired
        ? "Secure pairing required"
        : "Not paired");
    MENU_ENTRY(CONNECT_PAIRUNPAIR, "Pair securely");

    MENU_ENTRY(CONNECT_DISCONNECT, "Disconnect");
  } else {
    // current stream
    if (server.currentGame != 0) {
      char current_appname[256];
      char current_status[256];

      if (!get_app_name(server_applist, server.currentGame,
                        current_appname, sizeof(current_appname))) {
        snprintf(current_appname, sizeof(current_appname), "%s", "unknown");
      }
      snprintf(current_status, sizeof(current_status),
               "Streaming %s", current_appname);

      MENU_CATEGORY(current_status);
      MENU_ENTRY(server.currentGame, "Resume");
      MENU_ENTRY(CONNECT_QUITAPP, "Quit");
    }

    // pairing
    MENU_CATEGORY("Paired");
    if (connection_paired() != 0) {
      display_error("The Vita connection state could not be prepared.\n"
                    "Disconnect and try this PC again.");
      free(menu);
      release_host_client_state();
      return 0;
    }
    // FIXME: unpair not work
    // MENU_ENTRY(CONNECT_PAIRUNPAIR, "Unpair");

    MENU_ENTRY(CONNECT_DISCONNECT, "Disconnect");

    // app list
    if (server_applist != NULL) {
      MENU_CATEGORY("Applications");

      pos[0] = idx;

      PAPP_LIST list = server_applist;
      while (list) {
        MENU_ENTRY(list->id, list->name);
        list = list->next;
      }

      pos[1] = idx;
    } else {
      pos[0] = 0;
      pos[1] = 0;
    }
  }

  ret = display_menu(menu, idx, NULL, &ui_connect_loop, NULL, NULL, menu);
  free(menu);
  return ret;
}

device_info_t* ui_connect_and_pairing(device_info_t *info) {
  flash_message("Test connecting to:\n %s...", info->internal);
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "stream.action",
      "action=connect state=starting phase=host_init");
  char key_dir[HOST_KEY_DIRECTORY_CAPACITY];
  if (!build_host_key_directory(key_dir, sizeof(key_dir), info->name)) {
    display_error("Can't save pairing data for this PC.\n%s",
                  connection_error_message());
    return NULL;
  }
  sceIoMkdir(key_dir, 0777);

  int ret = gs_init(
      &server, info->internal, info->port, key_dir,
      vita_debug_is_logging_enabled() ? 3 : 0,
      config.unsupported_version);
  if (ret != GS_OK && ret != GS_UNSUPPORTED_VERSION) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_ERROR, "stream.action",
        "action=connect state=failed phase=host_init code=%d", ret);
  }

  if (ret == GS_OUT_OF_MEMORY) {
    display_error("Not enough memory");
    release_host_client_state();
    return NULL;
  } else if (ret == GS_INVALID) {
    display_error("Invalid data received from server: %s\n%s",
                  info->internal, connection_error_message());
    release_host_client_state();
    return NULL;
  } else if (ret == GS_UNSUPPORTED_VERSION) {
    if (!config.unsupported_version) {
      vita_debug_event(
          VITA_DEBUG_LEVEL_ERROR, "stream.action",
          "action=connect state=failed phase=host_init code=%d", ret);
      display_error("Unsupported version: %s\n",
                    connection_error_message());
      release_host_client_state();
      return NULL;
    }
  } else if (ret == GS_ERROR) {
    display_error("Gamestream error: %s\n", connection_error_message());
    release_host_client_state();
    return NULL;
  } else if (ret != GS_OK) {
    display_error("Can't connect to server\n%s\n%s", info->internal,
                  connection_error_message());
    release_host_client_state();
    return NULL;
  }

  vita_debug_event(
      ret == GS_OK ? VITA_DEBUG_LEVEL_INFO : VITA_DEBUG_LEVEL_WARNING,
      "stream.action",
      "action=connect state=ready phase=host_init code=%d", ret);
  if (connection_reset() != 0) {
    display_error("Could not begin the Vita connection attempt.\n"
                  "Disconnect and try adding this PC again.");
    release_host_client_state();
    return NULL;
  }

  /* Always continue with the canonical saved record. A failed attempt may
   * already have created an unpaired entry with this name. Updating a
   * transient discovery copy made successful retry pairings disappear from
   * the main menu until the application restarted. */
  info = upsert_device(info);
  if (info == NULL) {
    display_error("Could not save this PC in memory.\n"
                  "Remove an unused saved PC and try again.");
    release_host_client_state();
    return NULL;
  }
  /* gs_init() is the authoritative view for this exact pinned host. A saved
   * flag is only a UI hint and must not override a required re-pair. */
  info->paired = server.paired;

  // connectable address
  if (!save_device_info(info)) {
    display_error("Could not save this PC on the Vita.\n"
                  "Check free storage and try again.");
    release_host_client_state();
    return NULL;
  }
  flash_message("PC saved: %s", info->name);

  if (server.paired) {
    // no more need, move next action
    goto paired;
  }

  char pin[5];
  if (gs_generate_pin(pin) != GS_OK) {
    display_error("Could not create a secure pairing PIN\n%s",
                  connection_error_message());
    release_host_client_state();
    return NULL;
  }
  flash_message("Please enter the following PIN\non the target PC:\n\n%s", pin);

  ret = gs_pair(&server, pin);
  if (ret != GS_OK) {
    display_error("Pairing failed: %d\n%s", ret,
                  connection_error_message());
    release_host_client_state();
    return NULL;
  }
paired:
  /* Pairing is authoritative at this point. Persist it before any local
   * connection-state transition that can fail independently. */
  info->paired = true;
  flash_message("Securely paired: %s", info->name);

  // Preferimos usar la MAC ya obtenida en serverinfo (XML) si está disponible
  char mac[18] = {0};
  if (gs_get_server_mac(&server, mac, sizeof(mac)) == GS_OK && mac[0]) {
    strncpy(info->mac, mac, 17);
    info->mac[17] = '\0';
  }
  if (!save_device_info(info)) {
    display_error("Pairing succeeded, but the Vita could not save it.\n"
                  "The PC is available for this session only. Check free "
                  "storage before restarting Moonlight.");
  }

  if (connection_paired() != 0) {
    display_error("Pairing succeeded and was saved, but this connection "
                  "session could not continue.\n"
                  "Return to Saved computers and connect again.");
    release_host_client_state();
    return info;
  }

  if (connection_terminate()) {
    display_error("Reconnect failed: %d", -2);
    release_host_client_state();
    return info;
  }

  release_host_client_state();
  return info;
}

void ui_connect_resume() {
  while (ui_connected_menu() == QUIT_RELOAD);
}

void ui_connect_manual() {
  device_info_t info = {0};
  if (ime_dialog_string(info.name, "Enter Name:", "") != 0) {
    return;
  }
  if (ime_dialog_string(info.internal, "Enter IP or Address:", "") != 0) {
    return;
  }
  info.port = 47989;
  ui_connect_and_pairing(&info);
}

bool check_connection(const char *name, const char *addr, uint16_t port) {
  // someone already connected
  if (connection_is_ready() || addr == NULL || addr[0] == '\0') {
    return false;
  }

  flash_message("Check connecting to:\n %s:%d...", addr, port);

  int log_level = 0;
  if (vita_debug_is_logging_enabled()) {
    log_level = 3;
  }

  char key_dir[HOST_KEY_DIRECTORY_CAPACITY];
  if (!build_host_key_directory(key_dir, sizeof(key_dir), name))
    return false;

  SERVER_DATA probe = {0};
  int ret = gs_init(
      &probe, addr, port, key_dir, log_level,
      config.unsupported_version);
  bool reachable = ret == GS_OK && probe.paired;
  gs_cleanup(&probe);
  return reachable;
}

void ui_connect_paired_device(device_info_t *info) {
  if (!info->paired) {
    display_error("Unpaired device\n%s", info->name);
    return;
  }

  char *addr = NULL;
  if (info->prefer_external && check_connection(info->name, info->external, info->port)) {
    addr = info->external;
  } else if (check_connection(info->name, info->internal, info->port)) {
    addr = info->internal;
  } else if (check_connection(info->name, info->external, info->port)) {
    addr = info->external;
  }
  info->prefer_external = addr == info->external;
  if (!save_device_info(info)) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "host.state",
        "action=save_preferred_address state=failed");
  }

  if (addr == NULL) {
    display_error("Can't connect to server\n%s\n%s", info->name,
                  gs_error == NULL ? "Check that Sunshine is running"
                                   : connection_error_message());
    return;
  }

  if (!ui_connect(info->name, addr, info->port)) {
    return;
  }

  while (ui_connected_menu() == QUIT_RELOAD);
}

bool ui_connect_connected() {
  return connection_is_ready();
}

void ui_connect_address(char *addr) {
  strcpy(addr, server.serverInfo.address);
}
