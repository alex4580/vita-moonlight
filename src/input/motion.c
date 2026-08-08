#include <stdbool.h>
#include <stdint.h>

#include "../config.h"
#include "psp2/motion.h"
#include "psp2/kernel/threadmgr.h"
#include "Limelight.h"
#include "motion.h"
#include "../debug.h"
#include "vita.h"

#define STANDARD_GRAVITY 9.80665f
#define RADIANS_TO_DEGREES 57.29577951308232f
#define MOTION_EVENT_STATE_CHANGED 0x1U
#define MOTION_WORKER_STACK_SIZE 0x10000
#define MOTION_WORKER_STOP_TIMEOUT_US 1000000U

static SceUID motion_mutex = -1;
static SceUID motion_event = -1;
static SceUID motion_thread = -1;
static bool motion_resources_ready = false;
static bool motion_sampling_owned = false;
static bool motion_sampling_active = false;
static bool active_motion_threads = false;
static motion_data_state motion_state = {
    .motion_type_gyro_enabled = false,
    .motion_type_accel_enabled = false,
    .report_rate_gyro = VITA_MOTION_MIN_REPORT_RATE,
    .report_rate_accel = VITA_MOTION_MIN_REPORT_RATE,
};
static uint32_t gyro_events_sent = 0;
static uint32_t accel_events_sent = 0;
static int last_sensor_error = 0;
static int motion_init_error = 0;
static float stream_motion_scalar_x = 1.2f;
static float stream_motion_scalar_y = 0.8f;

static void lock_motion_state(void) {
  if (motion_mutex >= 0) sceKernelLockMutex(motion_mutex, 1, NULL);
}

static void unlock_motion_state(void) {
  if (motion_mutex >= 0) sceKernelUnlockMutex(motion_mutex, 1);
}

static void signal_motion_worker(void) {
  if (motion_event >= 0) {
    sceKernelSetEventFlag(motion_event, MOTION_EVENT_STATE_CHANGED);
  }
}

static void reset_motion_requests_locked(void) {
  motion_state.motion_type_gyro_enabled = false;
  motion_state.motion_type_accel_enabled = false;
  motion_state.report_rate_gyro = VITA_MOTION_MIN_REPORT_RATE;
  motion_state.report_rate_accel = VITA_MOTION_MIN_REPORT_RATE;
}

uint16_t vita_motion_clamp_report_rate(uint16_t report_rate) {
  if (report_rate < VITA_MOTION_MIN_REPORT_RATE) {
    return VITA_MOTION_MIN_REPORT_RATE;
  }
  if (report_rate > VITA_MOTION_MAX_REPORT_RATE) {
    return VITA_MOTION_MAX_REPORT_RATE;
  }
  return report_rate;
}

static uint32_t motion_report_delay_us(uint16_t report_rate) {
  return 1000000U / vita_motion_clamp_report_rate(report_rate);
}

static uint64_t advance_motion_deadline(
    uint64_t deadline, uint64_t now, uint32_t interval_us) {
  if (deadline == 0) {
    return now + interval_us;
  }
  if (deadline <= now) {
    uint64_t missed_intervals = (now - deadline) / interval_us + 1;
    return deadline + missed_intervals * interval_us;
  }
  return deadline;
}

bool vita_motion_init(void) {
  motion_mutex =
      sceKernelCreateMutex("vitainput_motion_mutex", 0, 0, NULL);
  if (motion_mutex < 0) {
    motion_init_error = motion_mutex;
    last_sensor_error = motion_mutex;
    vita_debug_log("Failed to create motion mutex: 0x%08X\n", motion_mutex);
    return false;
  }

  motion_event = sceKernelCreateEventFlag(
      "vitainput_motion_event", SCE_EVENT_WAITSINGLE, 0, NULL);
  if (motion_event < 0) {
    motion_init_error = motion_event;
    last_sensor_error = motion_event;
    vita_debug_log(
        "Failed to create motion event flag: 0x%08X\n", motion_event);
    sceKernelDeleteMutex(motion_mutex);
    motion_mutex = -1;
    return false;
  }

  motion_resources_ready = true;
  motion_init_error = 0;
  return true;
}

bool vita_motion_begin_stream(bool allow_motion) {
  /* A reconnect must never inherit a prior host's request or sampler. */
  if (!vita_motion_end_stream()) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "motion.state",
        "state=unavailable phase=previous_worker_cleanup code=pending");
    return false;
  }

  lock_motion_state();
  reset_motion_requests_locked();
  /* Sensitivity changes are a reconnect setting. Snapshot them so the motion
   * worker never races the UI's mutable global configuration. */
  stream_motion_scalar_x = config.motion_controls_scalar_x;
  stream_motion_scalar_y = config.motion_controls_scalar_y;
  gyro_events_sent = 0;
  accel_events_sent = 0;
  last_sensor_error = motion_resources_ready ? 0 : motion_init_error;
  unlock_motion_state();

  if (!allow_motion || !motion_resources_ready) {
    return false;
  }

  SceUID thid = sceKernelCreateThread(
      "vitainput_motion_thread", vitainput_motion_thread, 0,
      MOTION_WORKER_STACK_SIZE, 0, 0, NULL);
  if (thid < 0) {
    lock_motion_state();
    last_sensor_error = thid;
    unlock_motion_state();
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "motion.state",
        "state=unavailable phase=create_worker code=0x%08x",
        (unsigned int)thid);
    return false;
  }

  lock_motion_state();
  motion_thread = thid;
  active_motion_threads = true;
  unlock_motion_state();

  int ret = sceKernelStartThread(thid, 0, NULL);
  if (ret < 0) {
    lock_motion_state();
    active_motion_threads = false;
    motion_thread = -1;
    last_sensor_error = ret;
    unlock_motion_state();
    sceKernelDeleteThread(thid);
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "motion.state",
        "state=unavailable phase=start_worker code=0x%08x",
        (unsigned int)ret);
    return false;
  }

  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "motion.state",
      "state=armed worker_count=1 stack_bytes=%u sampling=deferred",
      (unsigned int)MOTION_WORKER_STACK_SIZE);
  return true;
}

bool vita_motion_end_stream(void) {
  lock_motion_state();
  active_motion_threads = false;
  reset_motion_requests_locked();
  SceUID thid = motion_thread;
  unlock_motion_state();

  if (thid >= 0) {
    signal_motion_worker();
    SceUInt timeout = MOTION_WORKER_STOP_TIMEOUT_US;
    int thread_status = 0;
    int ret = sceKernelWaitThreadEnd(thid, &thread_status, &timeout);
    if (ret < 0) {
      lock_motion_state();
      last_sensor_error = ret;
      unlock_motion_state();
      vita_debug_event(
          VITA_DEBUG_LEVEL_WARNING, "motion.state",
          "state=cleanup_warning phase=wait_worker code=0x%08x",
          (unsigned int)ret);
      /* Keep both the handle and sampler ownership. A later teardown may
       * retry the join, but begin_stream() must not overwrite either. */
      return false;
    }

    int delete_result = sceKernelDeleteThread(thid);
    if (delete_result < 0) {
      lock_motion_state();
      last_sensor_error = delete_result;
      unlock_motion_state();
      vita_debug_event(
          VITA_DEBUG_LEVEL_WARNING, "motion.state",
          "state=cleanup_warning phase=delete_worker code=0x%08x",
          (unsigned int)delete_result);
      /* Preserve the ended worker's handle until deletion succeeds. */
      return false;
    }

    lock_motion_state();
    if (motion_thread == thid) motion_thread = -1;
    unlock_motion_state();
  }

  lock_motion_state();
  bool sampling_active = motion_sampling_active;
  bool sampling_owned = motion_sampling_owned;
  unlock_motion_state();
  if (sampling_active && sampling_owned) {
    int ret = sceMotionStopSampling();
    if (ret < 0 && ret != SCE_MOTION_ERROR_NOT_SAMPLING) {
      lock_motion_state();
      last_sensor_error = ret;
      unlock_motion_state();
      vita_debug_event(
          VITA_DEBUG_LEVEL_WARNING, "motion.state",
          "state=cleanup_warning phase=stop_sampling code=0x%08x",
          (unsigned int)ret);
      /* Retain active ownership so shutdown or the next reconnect can retry. */
      return false;
    }
  }
  lock_motion_state();
  motion_sampling_owned = false;
  motion_sampling_active = false;
  unlock_motion_state();
  return true;
}

bool vita_motion_shutdown(void) {
  if (!vita_motion_end_stream()) return false;
  if (motion_event >= 0) {
    int ret = sceKernelDeleteEventFlag(motion_event);
    if (ret < 0) return false;
    motion_event = -1;
  }
  if (motion_mutex >= 0) {
    int ret = sceKernelDeleteMutex(motion_mutex);
    if (ret < 0) return false;
    motion_mutex = -1;
  }
  motion_resources_ready = false;
  return true;
}

bool vita_motion_set_state(uint8_t motion_type, uint16_t report_rate) {
  lock_motion_state();
  bool enabled = active_motion_threads && report_rate != 0;
  uint16_t clamped_rate = report_rate == 0
      ? VITA_MOTION_MIN_REPORT_RATE
      : vita_motion_clamp_report_rate(report_rate);
  bool recognized = motion_type == LI_MOTION_TYPE_GYRO ||
                    motion_type == LI_MOTION_TYPE_ACCEL;
  int sampling_error = 0;
  int stop_error = 0;
  if (recognized && enabled && !motion_sampling_active) {
    int ret = sceMotionStartSampling();
    if (ret < 0 && ret != SCE_MOTION_ERROR_ALREADY_SAMPLING) {
      sampling_error = ret;
    } else {
      bool sampling_owned = ret == 0;
      ret = sceMotionReset();
      if (ret < 0) {
        sampling_error = ret;
        if (sampling_owned) {
          int stop_result = sceMotionStopSampling();
          if (stop_result < 0 &&
              stop_result != SCE_MOTION_ERROR_NOT_SAMPLING) {
            /* Preserve ownership so idle/stream teardown can retry. */
            motion_sampling_active = true;
            motion_sampling_owned = true;
          }
        }
      } else {
        motion_sampling_active = true;
        motion_sampling_owned = sampling_owned;
        last_sensor_error = 0;
      }
    }
    if (sampling_error < 0) {
      enabled = false;
      last_sensor_error = sampling_error;
    }
  }
  if (motion_type == LI_MOTION_TYPE_GYRO) {
    motion_state.motion_type_gyro_enabled = enabled;
    motion_state.report_rate_gyro = clamped_rate;
  } else if (motion_type == LI_MOTION_TYPE_ACCEL) {
    motion_state.motion_type_accel_enabled = enabled;
    motion_state.report_rate_accel = clamped_rate;
  }

  /* Stop sampling as soon as Sunshine disables its final sensor. An already
   * active sampler that this process did not start is merely released rather
   * than stopped, so another Vita component retains ownership. */
  if (recognized &&
      !motion_state.motion_type_gyro_enabled &&
      !motion_state.motion_type_accel_enabled &&
      motion_sampling_active) {
    if (motion_sampling_owned) {
      int ret = sceMotionStopSampling();
      if (ret < 0 && ret != SCE_MOTION_ERROR_NOT_SAMPLING) {
        stop_error = ret;
        last_sensor_error = ret;
      } else {
        motion_sampling_active = false;
        motion_sampling_owned = false;
        if (sampling_error == 0) last_sensor_error = 0;
      }
    } else {
      motion_sampling_active = false;
      motion_sampling_owned = false;
      if (sampling_error == 0) last_sensor_error = 0;
    }
  }

  bool actually_enabled = false;
  if (motion_sampling_active) {
    if (motion_type == LI_MOTION_TYPE_GYRO) {
      actually_enabled = motion_state.motion_type_gyro_enabled;
    } else if (motion_type == LI_MOTION_TYPE_ACCEL) {
      actually_enabled = motion_state.motion_type_accel_enabled;
    }
  }
  unlock_motion_state();

  if (sampling_error < 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "motion.state",
        "state=unavailable phase=host_request code=0x%08x",
        (unsigned int)sampling_error);
  }
  if (stop_error < 0) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_WARNING, "motion.state",
        "state=cleanup_warning phase=host_disable code=0x%08x",
        (unsigned int)stop_error);
  }
  if (recognized) signal_motion_worker();
  return actually_enabled;
}

void vita_motion_get_status(VitaMotionStatus *status) {
  if (!status) return;
  lock_motion_state();
  status->stream_active = active_motion_threads;
  status->gyro_requested = motion_state.motion_type_gyro_enabled;
  status->accel_requested = motion_state.motion_type_accel_enabled;
  status->gyro_report_rate = motion_state.report_rate_gyro;
  status->accel_report_rate = motion_state.report_rate_accel;
  status->gyro_events_sent = gyro_events_sent;
  status->accel_events_sent = accel_events_sent;
  status->last_sensor_error = last_sensor_error;
  unlock_motion_state();
}

static void motion_process_sample(bool send_gyro, bool send_accel,
                                  float scalar_x, float scalar_y) {
  SceMotionState sample;
  /* Serialize the hardware read with sampler shutdown. This prevents the
   * worker from issuing sceMotionGetState() after the last host request has
   * stopped an owned sampler. */
  lock_motion_state();
  if (!motion_sampling_active) {
    unlock_motion_state();
    return;
  }
  int ret = sceMotionGetState(&sample);
  if (ret < 0) {
    last_sensor_error = ret;
    unlock_motion_state();
    return;
  }
  unlock_motion_state();

  if (send_gyro) {
    /* Vita angular velocity follows SDL axes and is radians/second;
     * Sunshine's controller protocol expects degrees/second. */
    float x = sample.angularVelocity.x *
        RADIANS_TO_DEGREES * scalar_y;
    float y = sample.angularVelocity.y *
        RADIANS_TO_DEGREES * scalar_x;
    float z = sample.angularVelocity.z * RADIANS_TO_DEGREES;
    LiSendControllerMotionEvent(0, LI_MOTION_TYPE_GYRO, x, y, z);
  }

  if (send_accel) {
    /* Vita acceleration is measured in g; Sunshine expects m/s^2. */
    float x = sample.acceleration.x * STANDARD_GRAVITY;
    float y = sample.acceleration.y * STANDARD_GRAVITY;
    float z = sample.acceleration.z * STANDARD_GRAVITY;
    LiSendControllerMotionEvent(0, LI_MOTION_TYPE_ACCEL, x, y, z);
  }

  lock_motion_state();
  if (send_gyro) gyro_events_sent++;
  if (send_accel) accel_events_sent++;
  last_sensor_error = 0;
  unlock_motion_state();
}

int vitainput_motion_thread(SceSize args, void *argp) {
  (void)args;
  (void)argp;
  uint64_t next_gyro_us = 0;
  uint64_t next_accel_us = 0;

  while (true) {
    lock_motion_state();
    bool stream_active = active_motion_threads;
    bool gyro_enabled = motion_state.motion_type_gyro_enabled;
    bool accel_enabled = motion_state.motion_type_accel_enabled;
    uint16_t gyro_rate = motion_state.report_rate_gyro;
    uint16_t accel_rate = motion_state.report_rate_accel;
    float scalar_x = stream_motion_scalar_x;
    float scalar_y = stream_motion_scalar_y;
    unlock_motion_state();

    if (!stream_active) break;

    if (!gyro_enabled) next_gyro_us = 0;
    if (!accel_enabled) next_accel_us = 0;

    uint64_t now = sceKernelGetSystemTimeWide();
    bool gyro_due =
        gyro_enabled && (next_gyro_us == 0 || now >= next_gyro_us);
    bool accel_due =
        accel_enabled && (next_accel_us == 0 || now >= next_accel_us);

    if (gyro_due || accel_due) {
      motion_process_sample(gyro_due, accel_due, scalar_x, scalar_y);
      if (gyro_due) {
        next_gyro_us = advance_motion_deadline(
            next_gyro_us, now, motion_report_delay_us(gyro_rate));
      }
      if (accel_due) {
        next_accel_us = advance_motion_deadline(
            next_accel_us, now, motion_report_delay_us(accel_rate));
      }
      continue;
    }

    SceUInt timeout = 0;
    SceUInt *timeout_ptr = NULL;
    if (gyro_enabled || accel_enabled) {
      uint64_t next_due_us = gyro_enabled ? next_gyro_us : next_accel_us;
      if (accel_enabled && next_accel_us < next_due_us) {
        next_due_us = next_accel_us;
      }
      uint64_t wait_us = next_due_us > now ? next_due_us - now : 1;
      timeout = wait_us > UINT32_MAX ? UINT32_MAX : (SceUInt)wait_us;
      timeout_ptr = &timeout;
    }

    unsigned int event_bits = 0;
    int wait_result = sceKernelWaitEventFlag(
        motion_event, MOTION_EVENT_STATE_CHANGED,
        SCE_EVENT_WAITOR | SCE_EVENT_WAITCLEAR_PAT,
        &event_bits, timeout_ptr);
    if (wait_result >= 0) {
      /* Apply host enable/disable/rate changes immediately. */
      next_gyro_us = 0;
      next_accel_us = 0;
    }
  }

  return 0;
}
