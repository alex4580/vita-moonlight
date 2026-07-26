#include <stdbool.h>
#include "../config.h"
#include "psp2/motion.h"
#include "psp2/kernel/threadmgr.h"
#include "Limelight.h"
#include "motion.h"
#include "../debug.h"
#include "vita.h"

#define STANDARD_GRAVITY 9.80665f
#define RADIANS_TO_DEGREES 57.29577951308232f
#define MOTION_IDLE_DELAY_US 100000

static SceUID motion_mutex = -1;
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

static void lock_motion_state(void) {
  if (motion_mutex >= 0) sceKernelLockMutex(motion_mutex, 1, NULL);
}

static void unlock_motion_state(void) {
  if (motion_mutex >= 0) sceKernelUnlockMutex(motion_mutex, 1);
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

static unsigned int motion_report_delay_us(uint16_t report_rate) {
  return 1000000U / vita_motion_clamp_report_rate(report_rate);
}

void vita_motion_begin_stream(bool allow_motion) {
  // Never carry a host's requested sensor state across stream sessions.
  lock_motion_state();
  active_motion_threads = false;
  motion_state.motion_type_gyro_enabled = false;
  motion_state.motion_type_accel_enabled = false;
  motion_state.report_rate_gyro = VITA_MOTION_MIN_REPORT_RATE;
  motion_state.report_rate_accel = VITA_MOTION_MIN_REPORT_RATE;
  gyro_events_sent = 0;
  accel_events_sent = 0;
  last_sensor_error = 0;
  active_motion_threads = allow_motion;
  unlock_motion_state();
}

void vita_motion_end_stream(void) {
  lock_motion_state();
  active_motion_threads = false;
  motion_state.motion_type_gyro_enabled = false;
  motion_state.motion_type_accel_enabled = false;
  unlock_motion_state();
}

void vita_motion_set_state(uint8_t motion_type, uint16_t report_rate) {
  lock_motion_state();
  bool enabled = active_motion_threads && report_rate != 0;
  uint16_t clamped_rate = report_rate == 0
      ? VITA_MOTION_MIN_REPORT_RATE
      : vita_motion_clamp_report_rate(report_rate);
  if (motion_type == LI_MOTION_TYPE_GYRO) {
    motion_state.motion_type_gyro_enabled = enabled;
    motion_state.report_rate_gyro = clamped_rate;
  } else if (motion_type == LI_MOTION_TYPE_ACCEL) {
    motion_state.motion_type_accel_enabled = enabled;
    motion_state.report_rate_accel = clamped_rate;
  }
  unlock_motion_state();
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

bool vita_motion_init(void) {
  int ret = sceMotionStartSampling();
  if (ret < 0 && ret != SCE_MOTION_ERROR_ALREADY_SAMPLING) {
    vita_debug_log("Failed to start motion sampling: 0x%08X\n", ret);
    return false;
  }

  ret = sceMotionReset();
  if (ret < 0) {
    vita_debug_log("Failed to reset motion sensors: 0x%08X\n", ret);
    return false;
  }

  motion_mutex = sceKernelCreateMutex("vitainput_motion_mutex", 0, 0, NULL);
  if (motion_mutex < 0) {
    vita_debug_log("Failed to create motion mutex: 0x%08X\n", motion_mutex);
    return false;
  }

  SceUID thid = sceKernelCreateThread("vitainput_motion_gyro_thread", vitainput_motion_gyro_thread, 0, 0x40000, 0, 0, NULL);
  if (thid < 0 || sceKernelStartThread(thid, 0, NULL) < 0) {
    return false;
  }

  thid = sceKernelCreateThread("vitainput_motion_accel_thread", vitainput_motion_accel_thread, 0, 0x40000, 0, 0, NULL);
  if (thid < 0 || sceKernelStartThread(thid, 0, NULL) < 0) {
    return false;
  }

  return true;
}

int vitainput_motion_gyro_thread(SceSize args, void *argp) {
  while (1) {
    lock_motion_state();
    bool enabled = motion_state.motion_type_gyro_enabled && active_motion_threads;
    uint16_t report_rate = motion_state.report_rate_gyro;
    unlock_motion_state();
    if (enabled) {
      motion_process_gyro();
      sceKernelDelayThread(motion_report_delay_us(report_rate));
    } else {
      sceKernelDelayThread(MOTION_IDLE_DELAY_US);
    }
  }

  return 0;
}

int vitainput_motion_accel_thread(SceSize args, void *argp) {
  while (1) {
    lock_motion_state();
    bool enabled = motion_state.motion_type_accel_enabled && active_motion_threads;
    uint16_t report_rate = motion_state.report_rate_accel;
    unlock_motion_state();
    if (enabled) {
      motion_process_accel();
      sceKernelDelayThread(motion_report_delay_us(report_rate));
    } else {
      sceKernelDelayThread(MOTION_IDLE_DELAY_US);
    }
  }

  return 0;
}

void motion_process_gyro(void) {
  SceMotionState motion_state_sample;
  // SceMotion exposes a single device sampler. Serialize the gyro and accel
  // readers so both worker threads cannot query it at the same time.
  lock_motion_state();
  int ret = sceMotionGetState(&motion_state_sample);
  unlock_motion_state();
  if (ret < 0) {
    lock_motion_state();
    last_sensor_error = ret;
    unlock_motion_state();
    return;
  }

  // Vita gyro values follow SDL's axes and are radians/second. Moonlight's
  // controller motion protocol requires degrees/second. In SDL coordinates,
  // X is pitch (vertical aim) and Y is yaw (horizontal aim), matching the
  // sensitivity mapping used by the legacy gyro-to-mouse path.
  float x = motion_state_sample.angularVelocity.x *
      RADIANS_TO_DEGREES * config.motion_controls_scalar_y;
  float y = motion_state_sample.angularVelocity.y *
      RADIANS_TO_DEGREES * config.motion_controls_scalar_x;
  float z = motion_state_sample.angularVelocity.z * RADIANS_TO_DEGREES;
  LiSendControllerMotionEvent(0, LI_MOTION_TYPE_GYRO, x, y, z);
  lock_motion_state();
  gyro_events_sent++;
  last_sensor_error = 0;
  unlock_motion_state();
}

void motion_process_accel(void) {
  SceMotionState motion_state_sample;
  lock_motion_state();
  int ret = sceMotionGetState(&motion_state_sample);
  unlock_motion_state();
  if (ret < 0) {
    lock_motion_state();
    last_sensor_error = ret;
    unlock_motion_state();
    return;
  }

  // Vita accelerometer values follow SDL's axes and are measured in g.
  // Moonlight's controller motion protocol requires m/s^2.
  float x = motion_state_sample.acceleration.x * STANDARD_GRAVITY;
  float y = motion_state_sample.acceleration.y * STANDARD_GRAVITY;
  float z = motion_state_sample.acceleration.z * STANDARD_GRAVITY;
  LiSendControllerMotionEvent(0, LI_MOTION_TYPE_ACCEL, x, y, z);
  lock_motion_state();
  accel_events_sent++;
  last_sensor_error = 0;
  unlock_motion_state();
}
