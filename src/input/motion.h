#ifndef VITA_MOTION_H
#define VITA_MOTION_H

#include "psp2common/types.h"
#include "stdbool.h"

typedef struct VitaMotionStatus {
  bool stream_active;
  bool gyro_requested;
  bool accel_requested;
  uint16_t gyro_report_rate;
  uint16_t accel_report_rate;
  uint32_t gyro_events_sent;
  uint32_t accel_events_sent;
  int last_sensor_error;
} VitaMotionStatus;

bool vita_motion_init(void);
bool vita_motion_shutdown(void);
bool vita_motion_begin_stream(bool allow_motion);
bool vita_motion_end_stream(void);
/* Returns true only when the requested sensor is actually reporting. */
bool vita_motion_set_state(uint8_t motion_type, uint16_t report_rate);
void vita_motion_get_status(VitaMotionStatus *status);

#define VITA_MOTION_MIN_REPORT_RATE 1
#define VITA_MOTION_MAX_REPORT_RATE 120

uint16_t vita_motion_clamp_report_rate(uint16_t report_rate);

int vitainput_motion_thread(SceSize args, void *argp);

#endif
