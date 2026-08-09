#pragma once

#include <stdbool.h>
#include <stdint.h>

/*
 * Front-touch actions are gestures, not hit-test buttons. A mapped corner is
 * eligible only when a single contact begins there and is released promptly
 * without moving. Once a gesture becomes normal touch input, it remains
 * normal input until every finger is lifted; entering another zone mid-drag
 * can therefore never fire an action.
 */

#define VITA_TOUCH_ZONE_COUNT 4
#define VITA_TOUCH_ZONE_NONE (-1)
#define VITA_TOUCH_ZONE_TAP_SLOP_PX 24
#define VITA_TOUCH_ZONE_TAP_MAX_US 250000ULL

typedef enum vita_touch_zone_state {
  VITA_TOUCH_ZONE_IDLE = 0,
  VITA_TOUCH_ZONE_TAP_PENDING,
  VITA_TOUCH_ZONE_PASSTHROUGH
} vita_touch_zone_state;

typedef enum vita_touch_zone_decision {
  VITA_TOUCH_ZONE_PASS = 0,
  VITA_TOUCH_ZONE_SUPPRESS,
  VITA_TOUCH_ZONE_TRIGGER
} vita_touch_zone_decision;

typedef struct vita_touch_zone_gesture {
  vita_touch_zone_state state;
  int candidate_zone;
  int start_x;
  int start_y;
  uint64_t started_at_us;
} vita_touch_zone_gesture;

void vita_touch_zone_gesture_reset(vita_touch_zone_gesture *gesture);

vita_touch_zone_decision vita_touch_zone_gesture_step(
    vita_touch_zone_gesture *gesture,
    bool enabled,
    int zone_at_contact,
    int finger_count,
    int x,
    int y,
    uint64_t now_us,
    int *triggered_zone);
