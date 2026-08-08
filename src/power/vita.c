/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015 Iwan Timmer, Sunguk Lee
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
#include <stdbool.h>
#include <stdint.h>

#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/power.h>
#include "../config.h"
#include "../debug.h"

enum {
  ENABLE_ALL = 0,
  DISABLE_SUSPEND = 1,
};

#define POWER_EVENT_WAKE 0x1U
#define POWER_TICK_INTERVAL_US (10U * 1000U * 1000U)
#define POWER_STOP_TIMEOUT_US 1000000U
#define POWER_WORKER_STACK_SIZE 0x10000U

static uint32_t powermode = ENABLE_ALL;
static uint32_t power_stream_active = 0;
static uint32_t power_worker_running = 0;
static SceUID power_worker_thread = -1;
static SceUID power_event = -1;
static SceUID power_mutex = -1;

static uint32_t power_atomic_load(const uint32_t *value) {
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}

static void power_atomic_store(uint32_t *value, uint32_t next) {
  __atomic_store_n(value, next, __ATOMIC_RELEASE);
}

static void wake_power_worker(void) {
  if (power_event >= 0) {
    sceKernelSetEventFlag(power_event, POWER_EVENT_WAKE);
  }
}

static void lock_power_state(void) {
  if (power_mutex >= 0) sceKernelLockMutex(power_mutex, 1, NULL);
}

static void unlock_power_state(void) {
  if (power_mutex >= 0) sceKernelUnlockMutex(power_mutex, 1);
}

int vitapower_thread(SceSize args, void *argp) {
  (void)args;
  (void)argp;
  while (power_atomic_load(&power_worker_running)) {
    if (power_atomic_load(&power_stream_active) &&
        (power_atomic_load(&powermode) & DISABLE_SUSPEND)) {
      sceKernelPowerTick(SCE_KERNEL_POWER_TICK_DISABLE_AUTO_SUSPEND);
      sceKernelPowerTick(SCE_KERNEL_POWER_TICK_DISABLE_OLED_OFF);
    }
    SceUInt timeout = POWER_TICK_INTERVAL_US;
    unsigned int event_bits = 0;
    sceKernelWaitEventFlag(
        power_event, POWER_EVENT_WAKE,
        SCE_EVENT_WAITOR | SCE_EVENT_WAITCLEAR_PAT,
        &event_bits, &timeout);
  }

  return 0;
}

static bool stop_power_worker_locked(void) {
  power_atomic_store(&power_worker_running, 0);
  wake_power_worker();

  if (power_worker_thread >= 0) {
    SceUInt timeout = POWER_STOP_TIMEOUT_US;
    int thread_status = 0;
    if (sceKernelWaitThreadEnd(
            power_worker_thread, &thread_status, &timeout) < 0) {
      return false;
    }
    if (sceKernelDeleteThread(power_worker_thread) < 0) return false;
    power_worker_thread = -1;
  }
  if (power_event >= 0) {
    if (sceKernelDeleteEventFlag(power_event) < 0) return false;
    power_event = -1;
  }
  return true;
}

static bool start_power_worker_locked(void) {
  if (power_atomic_load(&power_worker_running) &&
      power_worker_thread >= 0 && power_event >= 0) {
    wake_power_worker();
    return true;
  }
  if (!stop_power_worker_locked()) return false;

  power_event = sceKernelCreateEventFlag(
      "vitapower_event", SCE_EVENT_WAITSINGLE, 0, NULL);
  if (power_event < 0) return false;

  SceUID thid = sceKernelCreateThread(
      "vitapower_thread", vitapower_thread, 0x10000100,
      POWER_WORKER_STACK_SIZE, 0, 0, NULL);
  if (thid < 0) {
    sceKernelDeleteEventFlag(power_event);
    power_event = -1;
    return false;
  }

  power_worker_thread = thid;
  power_atomic_store(&power_worker_running, 1);
  if (sceKernelStartThread(thid, 0, NULL) < 0) {
    power_atomic_store(&power_worker_running, 0);
    sceKernelDeleteThread(thid);
    power_worker_thread = -1;
    sceKernelDeleteEventFlag(power_event);
    power_event = -1;
    return false;
  }

  return true;
}

bool vitapower_init() {
  power_atomic_store(&powermode, ENABLE_ALL);
  power_atomic_store(&power_stream_active, 0);
  power_atomic_store(&power_worker_running, 0);
  power_worker_thread = -1;
  power_event = -1;
  power_mutex = sceKernelCreateMutex("vitapower_mutex", 0, 0, NULL);
  return power_mutex >= 0;
}

bool vitapower_shutdown(void) {
  lock_power_state();
  power_atomic_store(&power_stream_active, 0);
  bool stopped = stop_power_worker_locked();
  unlock_power_state();
  if (!stopped) return false;

  if (power_mutex >= 0) {
    int result = sceKernelDeleteMutex(power_mutex);
    if (result < 0) return false;
    power_mutex = -1;
  }
  return true;
}

void vitapower_config(CONFIGURATION config) {
  uint32_t next_mode = ENABLE_ALL;

  if (config.disable_powersave) {
    next_mode |= DISABLE_SUSPEND;
  }

  lock_power_state();
  power_atomic_store(&powermode, next_mode);
  if (power_atomic_load(&power_stream_active) &&
      (next_mode & DISABLE_SUSPEND)) {
    if (!start_power_worker_locked()) {
      vita_debug_event(
          VITA_DEBUG_LEVEL_WARNING, "power.worker",
          "state=unavailable phase=reconfigure");
    }
  } else if (!stop_power_worker_locked()) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "power.worker",
        "state=cleanup_warning phase=reconfigure");
  }
  unlock_power_state();
}

void vitapower_start() {
  lock_power_state();
  power_atomic_store(&power_stream_active, 1);
  if ((power_atomic_load(&powermode) & DISABLE_SUSPEND) &&
      !start_power_worker_locked()) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "power.worker",
        "state=unavailable phase=stream_start");
  }
  unlock_power_state();
}

void vitapower_stop() {
  lock_power_state();
  power_atomic_store(&power_stream_active, 0);
  if (!stop_power_worker_locked()) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "power.worker",
        "state=cleanup_warning phase=stream_stop");
  }
  unlock_power_state();
}
