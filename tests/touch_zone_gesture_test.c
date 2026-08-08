#include <assert.h>
#include <stdint.h>
#include <stdio.h>

#include "input/touch_zone_gesture.h"

static vita_touch_zone_decision step(
    vita_touch_zone_gesture *gesture,
    bool enabled,
    int zone,
    int fingers,
    int x,
    int y,
    uint64_t at,
    int *triggered) {
  return vita_touch_zone_gesture_step(
      gesture, enabled, zone, fingers, x, y, at, triggered);
}

static void stationary_short_tap_triggers_once(void) {
  vita_touch_zone_gesture gesture = {0};
  int triggered = 99;

  assert(step(&gesture, true, 2, 1, 40, 500, 1000, &triggered) ==
         VITA_TOUCH_ZONE_SUPPRESS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);
  assert(step(&gesture, true, 2, 1, 48, 506, 90000, &triggered) ==
         VITA_TOUCH_ZONE_SUPPRESS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 120000,
              &triggered) == VITA_TOUCH_ZONE_TRIGGER);
  assert(triggered == 2);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 122000,
              &triggered) == VITA_TOUCH_ZONE_PASS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);
}

static void swipe_from_zone_is_passthrough_until_release(void) {
  vita_touch_zone_gesture gesture = {0};
  int triggered = 99;

  assert(step(&gesture, true, 0, 1, 10, 10, 0, &triggered) ==
         VITA_TOUCH_ZONE_SUPPRESS);
  assert(step(&gesture, true, 0, 1, 60, 10, 15000, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  /* Re-entering a mapped corner during the same drag cannot arm it. */
  assert(step(&gesture, true, 3, 1, 940, 520, 30000, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 40000,
              &triggered) == VITA_TOUCH_ZONE_PASS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);
}

static void hold_and_multitouch_are_normal_touch(void) {
  vita_touch_zone_gesture gesture = {0};
  int triggered = 99;

  assert(step(&gesture, true, 1, 1, 900, 20, 0, &triggered) ==
         VITA_TOUCH_ZONE_SUPPRESS);
  assert(step(&gesture, true, 1, 1, 900, 20,
              VITA_TOUCH_ZONE_TAP_MAX_US + 1, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0,
              VITA_TOUCH_ZONE_TAP_MAX_US + 2, &triggered) ==
         VITA_TOUCH_ZONE_PASS);

  assert(step(&gesture, true, 1, 2, 900, 20, 400000, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 410000,
              &triggered) == VITA_TOUCH_ZONE_PASS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);

  /* A release can be the first sample after the deadline. */
  assert(step(&gesture, true, 3, 1, 940, 520, 500000, &triggered) ==
         VITA_TOUCH_ZONE_SUPPRESS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0,
              500000 + VITA_TOUCH_ZONE_TAP_MAX_US + 1, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);
}

static void outside_and_disabled_zones_never_capture(void) {
  vita_touch_zone_gesture gesture = {0};
  int triggered = 99;

  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 1, 480, 272, 0,
              &triggered) == VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, true, 0, 1, 10, 10, 10000, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, true, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 20000,
              &triggered) == VITA_TOUCH_ZONE_PASS);

  assert(step(&gesture, false, 0, 1, 10, 10, 30000, &triggered) ==
         VITA_TOUCH_ZONE_PASS);
  assert(step(&gesture, false, VITA_TOUCH_ZONE_NONE, 0, 0, 0, 40000,
              &triggered) == VITA_TOUCH_ZONE_PASS);
  assert(triggered == VITA_TOUCH_ZONE_NONE);
}

int main(void) {
  stationary_short_tap_triggers_once();
  swipe_from_zone_is_passthrough_until_release();
  hold_and_multitouch_are_normal_touch();
  outside_and_disabled_zones_never_capture();
  puts("Vita front-touch zone gesture tests passed.");
  return 0;
}
