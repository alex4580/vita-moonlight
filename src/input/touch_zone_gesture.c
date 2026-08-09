#include "touch_zone_gesture.h"

#include <stddef.h>

void vita_touch_zone_gesture_reset(vita_touch_zone_gesture *gesture) {
  if (gesture == NULL) {
    return;
  }

  gesture->state = VITA_TOUCH_ZONE_IDLE;
  gesture->candidate_zone = VITA_TOUCH_ZONE_NONE;
  gesture->start_x = 0;
  gesture->start_y = 0;
  gesture->started_at_us = 0;
}

static bool moved_beyond_tap_slop(
    const vita_touch_zone_gesture *gesture, int x, int y) {
  int64_t dx = (int64_t)x - gesture->start_x;
  int64_t dy = (int64_t)y - gesture->start_y;
  int64_t limit = VITA_TOUCH_ZONE_TAP_SLOP_PX;
  return dx * dx + dy * dy > limit * limit;
}

vita_touch_zone_decision vita_touch_zone_gesture_step(
    vita_touch_zone_gesture *gesture,
    bool enabled,
    int zone_at_contact,
    int finger_count,
    int x,
    int y,
    uint64_t now_us,
    int *triggered_zone) {
  if (triggered_zone != NULL) {
    *triggered_zone = VITA_TOUCH_ZONE_NONE;
  }
  if (gesture == NULL) {
    return VITA_TOUCH_ZONE_PASS;
  }

  if (!enabled) {
    vita_touch_zone_gesture_reset(gesture);
    return VITA_TOUCH_ZONE_PASS;
  }

  if (finger_count < 0) {
    finger_count = 0;
  }

  switch (gesture->state) {
    case VITA_TOUCH_ZONE_IDLE:
      if (finger_count == 0) {
        return VITA_TOUCH_ZONE_PASS;
      }
      if (finger_count == 1 &&
          zone_at_contact >= 0 &&
          zone_at_contact < VITA_TOUCH_ZONE_COUNT) {
        gesture->state = VITA_TOUCH_ZONE_TAP_PENDING;
        gesture->candidate_zone = zone_at_contact;
        gesture->start_x = x;
        gesture->start_y = y;
        gesture->started_at_us = now_us;
        return VITA_TOUCH_ZONE_SUPPRESS;
      }
      gesture->state = VITA_TOUCH_ZONE_PASSTHROUGH;
      return VITA_TOUCH_ZONE_PASS;

    case VITA_TOUCH_ZONE_TAP_PENDING:
      if (finger_count == 0) {
        int completed_zone = gesture->candidate_zone;
        bool completed_in_time =
            now_us - gesture->started_at_us <= VITA_TOUCH_ZONE_TAP_MAX_US;
        vita_touch_zone_gesture_reset(gesture);
        if (!completed_in_time) {
          return VITA_TOUCH_ZONE_PASS;
        }
        if (triggered_zone != NULL) {
          *triggered_zone = completed_zone;
        }
        return VITA_TOUCH_ZONE_TRIGGER;
      }
      if (finger_count != 1 ||
          now_us - gesture->started_at_us > VITA_TOUCH_ZONE_TAP_MAX_US ||
          moved_beyond_tap_slop(gesture, x, y)) {
        gesture->state = VITA_TOUCH_ZONE_PASSTHROUGH;
        gesture->candidate_zone = VITA_TOUCH_ZONE_NONE;
        return VITA_TOUCH_ZONE_PASS;
      }
      return VITA_TOUCH_ZONE_SUPPRESS;

    case VITA_TOUCH_ZONE_PASSTHROUGH:
      if (finger_count == 0) {
        vita_touch_zone_gesture_reset(gesture);
      }
      return VITA_TOUCH_ZONE_PASS;

    default:
      vita_touch_zone_gesture_reset(gesture);
      return VITA_TOUCH_ZONE_PASS;
  }
}
