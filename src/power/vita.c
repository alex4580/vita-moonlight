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

enum {
  ENABLE_ALL = 0,
  DISABLE_SUSPEND = 1,
};

#define POWER_EVENT_WAKE 0x1U
#define POWER_TICK_INTERVAL_US (10U * 1000U * 1000U)
#define POWER_STOP_TIMEOUT_US 1000000U

static uint32_t powermode = ENABLE_ALL;
static uint32_t active_power_thread = 0;
static uint32_t power_worker_running = 0;
static SceUID power_worker_thread = -1;
static SceUID power_event = -1;

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

int vitapower_thread(SceSize args, void *argp) {
  (void)args;
  (void)argp;
  while (power_atomic_load(&power_worker_running)) {
    if (power_atomic_load(&active_power_thread) &&
        (power_atomic_load(&powermode) & DISABLE_SUSPEND)) {
      sceKernelPowerTick(SCE_KERNEL_POWER_TICK_DISABLE_AUTO_SUSPEND);
      sceKernelPowerTick(SCE_KERNEL_POWER_TICK_DISABLE_OLED_OFF);
    }
    if (power_atomic_load(&active_power_thread) &&
        !scePowerIsBatteryCharging() && scePowerIsLowBattery()) {
      // TODO print warning message
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

bool vitapower_init() {
  power_event = sceKernelCreateEventFlag(
      "vitapower_event", SCE_EVENT_WAITSINGLE, 0, NULL);
  if (power_event < 0) return false;

  SceUID thid = sceKernelCreateThread("vitapower_thread", vitapower_thread, 0x10000100, 0x40000, 0, 0, NULL);
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

bool vitapower_shutdown(void) {
  power_atomic_store(&active_power_thread, 0);
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

void vitapower_config(CONFIGURATION config) {
  uint32_t next_mode = ENABLE_ALL;

  if (config.disable_powersave) {
    next_mode |= DISABLE_SUSPEND;
  }
  power_atomic_store(&powermode, next_mode);
  wake_power_worker();
}

void vitapower_start() {
  power_atomic_store(&active_power_thread, 1);
  wake_power_worker();
}

void vitapower_stop() {
  power_atomic_store(&active_power_thread, 0);
  wake_power_worker();
}
