#include "psp2common/types.h"
#include "stdbool.h"

bool vita_motion_init();
void vita_motion_begin_stream(bool allow_motion);
void vita_motion_end_stream(void);

#define VITA_MOTION_MIN_REPORT_RATE 1
#define VITA_MOTION_MAX_REPORT_RATE 120

uint16_t vita_motion_clamp_report_rate(uint16_t report_rate);

int vitainput_motion_gyro_thread(SceSize args, void *argp);
int vitainput_motion_accel_thread(SceSize args, void *argp);

void motion_process_gyro(void);
void motion_process_accel(void);
