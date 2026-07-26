// touchabsolute.c
// Exclusive front-touch modes used by Moonlight Vita.

#include "touchabsolute.h"
#include "../config.h"
#include "vita.h"
#include "../debug.h"
#include <stdbool.h>
#include <stdint.h>
#include <stdlib.h>

#define STORED_TOUCH_POINTS \
    ((int)(sizeof(((TouchData *)0)->points) / sizeof(((TouchData *)0)->points[0])))

static bool absolute_mouse_enabled = false;
static int active_touch_mode = 0;

static uint8_t ds4_finger_active[STORED_TOUCH_POINTS] = {0};
static int ds4_x[STORED_TOUCH_POINTS] = {0};
static int ds4_y[STORED_TOUCH_POINTS] = {0};
static uint8_t tablet_finger_active[STORED_TOUCH_POINTS] = {0};
static int tablet_x[STORED_TOUCH_POINTS] = {0};
static int tablet_y[STORED_TOUCH_POINTS] = {0};
static bool tablet_mouse_released = false;

static bool absolute_left_down = false;
static bool absolute_two_finger_active = false;
static bool absolute_two_finger_scroll = false;
static bool absolute_suppress_one_finger = false;
static int absolute_two_finger_start_y = 0;
static int absolute_two_finger_last_y = 0;

static float normalized_x(int x) {
    return (float)x / 960.0f;
}

static float normalized_y(int y) {
    return (float)y / 544.0f;
}

static void release_ds4_touches(void) {
    for (int i = 0; i < STORED_TOUCH_POINTS; ++i) {
        if (ds4_finger_active[i]) {
            LiSendControllerTouchEvent(0, LI_TOUCH_EVENT_UP, i,
                                       normalized_x(ds4_x[i]),
                                       normalized_y(ds4_y[i]), 0.0f);
        }
        ds4_finger_active[i] = 0;
        ds4_x[i] = 0;
        ds4_y[i] = 0;
    }
}

static void release_tablet_touches(void) {
    for (int i = 0; i < STORED_TOUCH_POINTS; ++i) {
        if (tablet_finger_active[i]) {
            LiSendTouchEvent(LI_TOUCH_EVENT_UP, i,
                             normalized_x(tablet_x[i]),
                             normalized_y(tablet_y[i]),
                             0.0f, 0.0f, 0.0f, LI_ROT_UNKNOWN);
        }
        tablet_finger_active[i] = 0;
        tablet_x[i] = 0;
        tablet_y[i] = 0;
    }
    tablet_mouse_released = false;
}

static void reset_absolute_mouse(void) {
    if (absolute_left_down) {
        LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT);
    }
    absolute_left_down = false;
    absolute_two_finger_active = false;
    absolute_two_finger_scroll = false;
    absolute_suppress_one_finger = false;
    absolute_two_finger_start_y = 0;
    absolute_two_finger_last_y = 0;
}

static void release_active_mode(void) {
    switch (active_touch_mode) {
        case 1:
            release_ds4_touches();
            break;
        case 2:
            reset_absolute_mouse();
            break;
        case 3:
            release_tablet_touches();
            break;
        default:
            break;
    }
    active_touch_mode = 0;
}

static void activate_mode(int mode) {
    if (active_touch_mode != mode) {
        release_active_mode();
        active_touch_mode = mode;
    }
}

void touchabsolute_enable(bool enable) {
    /*
     * This setter is called for every touch-mode change. Releasing the old
     * mode here prevents a DS4 contact, tablet contact, or mouse button from
     * remaining held when the user switches modes.
     */
    release_active_mode();
    absolute_mouse_enabled = enable;
}

bool touchabsolute_is_enabled() {
    return absolute_mouse_enabled;
}

void touchabsolute_release_all(void) {
    release_active_mode();
    /*
     * Also clear inactive mode storage. This is idempotent and makes stream
     * stop/start safe even if a mode transition was interrupted.
     */
    release_ds4_touches();
    reset_absolute_mouse();
    release_tablet_touches();
}

// DS4 touchpad mode.
void touchabsolute_handle_ds4(const TouchData* touch, SceRtcTick* current) {
    (void)current;
    activate_mode(1);

    const int move_threshold = 1;
    for (int i = 0; i < STORED_TOUCH_POINTS; ++i) {
        int is_active = i < touch->finger;
        int x = is_active ? touch->points[i].x : 0;
        int y = is_active ? touch->points[i].y : 0;
        float norm_x = normalized_x(x);
        float norm_y = normalized_y(y);

        if (is_active && !ds4_finger_active[i]) {
            LiSendControllerTouchEvent(0, LI_TOUCH_EVENT_DOWN, i,
                                       norm_x, norm_y, 1.0f);
            vita_debug_log("[DS4_TOUCHPAD] DOWN finger=%d", i);
        } else if (is_active && ds4_finger_active[i]) {
            int dx = abs(x - ds4_x[i]);
            int dy = abs(y - ds4_y[i]);
            if (dx >= move_threshold || dy >= move_threshold) {
                LiSendControllerTouchEvent(0, LI_TOUCH_EVENT_MOVE, i,
                                           norm_x, norm_y, 1.0f);
            }
        } else if (!is_active && ds4_finger_active[i]) {
            LiSendControllerTouchEvent(0, LI_TOUCH_EVENT_UP, i,
                                       normalized_x(ds4_x[i]),
                                       normalized_y(ds4_y[i]), 0.0f);
            vita_debug_log("[DS4_TOUCHPAD] UP finger=%d", i);
        }

        ds4_finger_active[i] = is_active;
        ds4_x[i] = x;
        ds4_y[i] = y;
    }
}

// Absolute mouse mode: one-finger drag, two-finger scroll or right tap.
void touchabsolute_handle_absolute(
        const TouchData* touch,
        SceRtcTick* current,
        int* front_state,
        short* finger_count,
        TouchData* swipe,
        TouchData* touch_old) {
    (void)current;
    (void)front_state;
    (void)finger_count;
    (void)swipe;
    (void)touch_old;

    if (!touchabsolute_is_enabled()) {
        reset_absolute_mouse();
        return;
    }

    activate_mode(2);

    if (touch->finger <= 0) {
        if (absolute_left_down) {
            LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT);
            absolute_left_down = false;
        }
        if (absolute_two_finger_active && !absolute_two_finger_scroll) {
            vita_debug_log("[ABS_MOUSE] two-finger right click");
            LiSendMouseButtonEvent(BUTTON_ACTION_PRESS, BUTTON_RIGHT);
            LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_RIGHT);
        }
        absolute_two_finger_active = false;
        absolute_two_finger_scroll = false;
        absolute_suppress_one_finger = false;
        return;
    }

    LiSendMousePositionEvent(
        touch->points[0].x, touch->points[0].y, 960, 544);

    if (touch->finger >= 2) {
        if (absolute_left_down) {
            LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT);
            absolute_left_down = false;
        }

        int avg_y = (touch->points[0].y + touch->points[1].y) / 2;
        if (!absolute_two_finger_active) {
            absolute_two_finger_active = true;
            absolute_two_finger_scroll = false;
            absolute_two_finger_start_y = avg_y;
            absolute_two_finger_last_y = avg_y;
        } else {
            const int scroll_threshold = 12;
            int delta = avg_y - absolute_two_finger_last_y;
            if (abs(avg_y - absolute_two_finger_start_y) >
                    scroll_threshold) {
                absolute_two_finger_scroll = true;
            }
            if (absolute_two_finger_scroll && delta != 0) {
                LiSendScrollEvent(delta);
            }
            absolute_two_finger_last_y = avg_y;
        }
        absolute_suppress_one_finger = true;
        return;
    }

    if (absolute_two_finger_active) {
        if (!absolute_two_finger_scroll) {
            vita_debug_log("[ABS_MOUSE] two-finger right click");
            LiSendMouseButtonEvent(BUTTON_ACTION_PRESS, BUTTON_RIGHT);
            LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_RIGHT);
        }
        absolute_two_finger_active = false;
        absolute_two_finger_scroll = false;
    }

    /*
     * After a two-finger gesture, do not turn the remaining contact into a
     * fresh left-button drag. The user must lift all contacts first.
     */
    if (!absolute_suppress_one_finger && !absolute_left_down) {
        LiSendMouseButtonEvent(BUTTON_ACTION_PRESS, BUTTON_LEFT);
        absolute_left_down = true;
    }
}

// Sunshine pen/touch mode.
void touchabsolute_handle_tablet(const TouchData* touch) {
    activate_mode(3);

    if (!tablet_mouse_released) {
        LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_LEFT);
        LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_RIGHT);
        LiSendMouseButtonEvent(BUTTON_ACTION_RELEASE, BUTTON_MIDDLE);
        LiSendMousePositionEvent(-1, -1, 960, 544);
        vita_debug_log(
            "[TOUCHSCREEN] mouse released and moved off-screen");
        tablet_mouse_released = true;
    }

    for (int i = 0; i < STORED_TOUCH_POINTS; ++i) {
        int is_active = i < touch->finger;
        int x = is_active ? touch->points[i].x : 0;
        int y = is_active ? touch->points[i].y : 0;
        float norm_x = normalized_x(x);
        float norm_y = normalized_y(y);

        if (is_active && !tablet_finger_active[i]) {
            LiSendTouchEvent(LI_TOUCH_EVENT_DOWN, i,
                             norm_x, norm_y,
                             1.0f, 0.0f, 0.0f, LI_ROT_UNKNOWN);
            vita_debug_log("[TOUCHSCREEN] DOWN finger=%d", i);
        } else if (is_active && tablet_finger_active[i]) {
            LiSendTouchEvent(LI_TOUCH_EVENT_MOVE, i,
                             norm_x, norm_y,
                             1.0f, 0.0f, 0.0f, LI_ROT_UNKNOWN);
        } else if (!is_active && tablet_finger_active[i]) {
            LiSendTouchEvent(LI_TOUCH_EVENT_UP, i,
                             normalized_x(tablet_x[i]),
                             normalized_y(tablet_y[i]),
                             0.0f, 0.0f, 0.0f, LI_ROT_UNKNOWN);
            vita_debug_log("[TOUCHSCREEN] UP finger=%d", i);
        }

        tablet_finger_active[i] = is_active;
        tablet_x[i] = x;
        tablet_y[i] = y;
    }
}
