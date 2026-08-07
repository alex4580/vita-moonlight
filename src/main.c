/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015, 2016 Iwan Timmer
 *
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * Moonlight is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Moonlight; if not, see <http://www.gnu.org/licenses/>.
 */

#include "connection.h"
#include "configuration.h"
#include "audio.h"
#include "psp2/net/netctl.h"
#include "video.h"
#include "config.h"
#include "platform.h"

#include "input/vita.h"
#include "input/touchabsolute.h"
#include "input/keyboardkeys.h"
#include "keyboardsystem.h"

#include <Limelight.h>

#include <stdio.h>
#include <stdlib.h>
#include <stdbool.h>
#include <string.h>
#include <unistd.h>
#include <sys/types.h>
#include <openssl/rand.h>
#include <openssl/evp.h>
#include "curl/curl.h"

#include <psp2/kernel/rng.h>
#include <psp2/kernel/threadmgr.h>

#include <psp2/net/net.h>


#include <psp2/io/stat.h>

#include <psp2/shellutil.h>
#include <psp2/sysmodule.h>
#include <psp2/ctrl.h>
#include <psp2/touch.h>
#include <psp2/rtc.h>

#include "graphics.h"
#include "device.h"
#include "gui/ui.h"
#include "gui/ui_diagnostics.h"
#include "util.h"
#include "power/vita.h"
#include "input/motion.h"

#include "debug.h"
#include "check_dir.h"

#define VITA_NET_MEM_SIZE 1 * 1024 * 1024

SceNetInitParam net_param = {
  .memory = NULL,
  .size = VITA_NET_MEM_SIZE,
  .flags = 0
};

typedef struct VitaRuntimeState {
  bool net_module_loaded;
  bool net_initialized;
  bool netctl_initialized;
  bool curl_initialized;
  bool debug_initialized;
} VitaRuntimeState;

static VitaRuntimeState runtime_state = {0};

static void vita_runtime_shutdown(void) {
  if (runtime_state.debug_initialized) {
    vita_debug_shutdown();
    runtime_state.debug_initialized = false;
  }
  if (runtime_state.curl_initialized) {
    curl_global_cleanup();
    runtime_state.curl_initialized = false;
  }
  if (runtime_state.netctl_initialized) {
    sceNetCtlTerm();
    runtime_state.netctl_initialized = false;
  }
  if (runtime_state.net_initialized) {
    sceNetTerm();
    runtime_state.net_initialized = false;
  }
  if (runtime_state.net_module_loaded) {
    sceSysmoduleUnloadModule(SCE_SYSMODULE_NET);
    runtime_state.net_module_loaded = false;
  }
  free(net_param.memory);
  net_param.memory = NULL;
}

static int startup_failed(const char *message) {
  printf("\nVita Moonlight could not start.\n%s\n\n"
         "The app will close without starting a stream.\n", message);
  /* Leave the actionable error visible before returning safely to LiveArea. */
  sceKernelDelayThread(3000 * 1000);
  return EXIT_FAILURE;
}

static bool vita_workers_shutdown(void) {
  bool stopped = vita_motion_shutdown();
  stopped = vitainput_shutdown() && stopped;
  stopped = vitapower_shutdown() && stopped;
  return stopped;
}

static bool vita_init() {
  sceShellUtilInitEvents(0);

  // Seed OpenSSL with Sony-grade random number generator
  char random_seed[0x40] = {0};
  sceKernelGetRandomNumber(random_seed, sizeof(random_seed));
  RAND_seed(random_seed, sizeof(random_seed));
  OpenSSL_add_all_algorithms();

  // This is only used for PIN codes, doesn't really matter
  srand(time(NULL));

  #ifdef __vita__
  printf("Vita Moonlight %d.%d.%d (%s)\n", VERSION_MAJOR, VERSION_MINOR, VERSION_PATCH, COMPILE_OPTIONS);
  #endif

  int ret;

  // Load Vita network 
  net_param.memory = malloc(net_param.size);
  if (net_param.memory == NULL) {
    printf("Could not allocate net memory!");
    goto fail;
  }
 
  ret = sceSysmoduleLoadModule(SCE_SYSMODULE_NET);
  if (ret < 0) {
    printf("Net module was unable to load!");
    goto fail;
  }
  runtime_state.net_module_loaded = true;

  ret = sceNetInit(&net_param);
  if (ret < 0) {
    printf("Net init failed!");
    goto fail;
  }
  runtime_state.net_initialized = true;
  
  ret = sceNetCtlInit();
  if (ret < 0) {
    printf("Net Ctl init failed!");
    goto fail;
  }
  runtime_state.netctl_initialized = true;

  ret = curl_global_init(CURL_GLOBAL_ALL);
  if (ret != CURLE_OK) {
    printf("CURL init failed!");
    goto fail;
  }
  runtime_state.curl_initialized = true;

  ret = vita_debug_init();
  if (ret != true) {
    printf("Debug log mutex init failed!");
    goto fail;
  }
  runtime_state.debug_initialized = true;
  return true;

fail:
  vita_runtime_shutdown();
  return false;
}


int main(int argc, char* argv[]) {
  psvDebugScreenInit();
  if (!vita_init()) {
    return startup_failed("A required network or runtime service failed.");
  }

  if (!vitapower_init()) {
    vita_runtime_shutdown();
    return startup_failed("The Vita power-management worker failed.");
  }

  if (!vitainput_init()) {
    vitapower_shutdown();
    vita_runtime_shutdown();
    return startup_failed("The Vita input worker failed.");
  }

  if (!vita_motion_init()) {
    /* Gyro is optional. Keep the rest of the client usable if the motion
     * service or its synchronization objects are unavailable. */
    printf("Motion input unavailable; continuing without gyro.\n");
  }

  char out_path[MOONLIGHT_PATH_MAX] = {0};
  char out_key_dir[MOONLIGHT_PATH_MAX] = {0};
  if (!check_and_create_moonlight_dir(out_path, out_key_dir)) {
    bool workers_stopped = vita_workers_shutdown();
    if (workers_stopped) vita_runtime_shutdown();
    return startup_failed(
        "No writable Vita data directory is available. Free space on ux0: "
        "or reconnect the configured uma0: storage, then try again.");
  }
  config_path = out_path;
  strcpy(config.key_dir, out_key_dir);
  if (!config_parse(argc, argv, &config)) {
    bool workers_stopped = vita_workers_shutdown();
    if (workers_stopped) vita_runtime_shutdown();
    return startup_failed(
        "Saved settings could not be read, recovered, or safely written. "
        "Free Vita storage and preserve moonlight.conf plus its .bak file "
        "before trying again.");
  }
  /* Support logs are explicit per-run captures and never resume at startup. */
  config.save_debug_log = false;
  vita_debug_set_logging_enabled(false);

  // Restaurar estado de Absolute Touch al iniciar
  touchabsolute_enable(config.touchscreen_mode == 2);

  // Aplicar el layout guardado después de cargar la config
  keyboardsystem_set_layout((KeyboardLayout)config.keyboard_layout);

  vitapower_config(config);
  vitainput_config(config);

  ui_diagnostics_init();

  load_all_known_devices();

  gui_loop();

  if (connection_get_status() != LI_DISCONNECTED) {
    connection_terminate();
  }
  ui_diagnostics_shutdown();
  bool workers_stopped = vita_workers_shutdown();
  if (workers_stopped) {
    vita_runtime_shutdown();
  }
  return workers_stopped ? EXIT_SUCCESS : EXIT_FAILURE;
}
