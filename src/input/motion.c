#include <stdbool.h>
#include "../config.h"
#include "psp2/motion.h"
#include "Limelight.h"
#include "motion.h"
#include "../debug.h"
#include "vita.h"

#define STANDARD_GRAVITY 9.80665f
#define RADIANS_TO_DEGREES 57.29577951308232f
#define MOTION_IDLE_DELAY_US 100000

bool active_motion_threads = false;

motion_data_state motion_state = {
    .motion_type_gyro_enabled = false,
    .motion_type_accel_enabled = false,
    .report_rate_gyro = VITA_MOTION_MIN_REPORT_RATE,
    .report_rate_accel = VITA_MOTION_MIN_REPORT_RATE,
};

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
  active_motion_threads = false;
  motion_state.motion_type_gyro_enabled = false;
  motion_state.motion_type_accel_enabled = false;
  motion_state.report_rate_gyro = VITA_MOTION_MIN_REPORT_RATE;
  motion_state.report_rate_accel = VITA_MOTION_MIN_REPORT_RATE;
  active_motion_threads = allow_motion;
}

void vita_motion_end_stream(void) {
  active_motion_threads = false;
  motion_state.motion_type_gyro_enabled = false;
  motion_state.motion_type_accel_enabled = false;
}

bool vita_motion_init() {
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
    if (motion_state.motion_type_gyro_enabled && active_motion_threads) {
      motion_process_gyro();
      sceKernelDelayThread(motion_report_delay_us(motion_state.report_rate_gyro));
    } else {
      sceKernelDelayThread(MOTION_IDLE_DELAY_US);
    }
  }

  return 0;
}

int vitainput_motion_accel_thread(SceSize args, void *argp) {
  while (1) {
    if (motion_state.motion_type_accel_enabled && active_motion_threads) {
      motion_process_accel();
      sceKernelDelayThread(motion_report_delay_us(motion_state.report_rate_accel));
    } else {
      sceKernelDelayThread(MOTION_IDLE_DELAY_US);
    }
  }

  return 0;
}

void motion_process_gyro(void) {
  SceMotionState motion_state_sample;
  if (sceMotionGetState(&motion_state_sample) < 0) {
    return;
  }

  // Vita gyro values follow SDL's axes and are radians/second. Moonlight's
  // controller motion protocol requires degrees/second.
  float x = motion_state_sample.angularVelocity.x * RADIANS_TO_DEGREES;
  float y = motion_state_sample.angularVelocity.y * RADIANS_TO_DEGREES;
  float z = motion_state_sample.angularVelocity.z * RADIANS_TO_DEGREES;
  LiSendControllerMotionEvent(0, LI_MOTION_TYPE_GYRO, x, y, z);
}

void motion_process_accel(void) {
  SceMotionState motion_state_sample;
  if (sceMotionGetState(&motion_state_sample) < 0) {
    return;
  }

  // Vita accelerometer values follow SDL's axes and are measured in g.
  // Moonlight's controller motion protocol requires m/s^2.
  float x = motion_state_sample.acceleration.x * STANDARD_GRAVITY;
  float y = motion_state_sample.acceleration.y * STANDARD_GRAVITY;
  float z = motion_state_sample.acceleration.z * STANDARD_GRAVITY;
  LiSendControllerMotionEvent(0, LI_MOTION_TYPE_ACCEL, x, y, z);
}
