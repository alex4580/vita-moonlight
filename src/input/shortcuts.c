// shortcuts.c
// Gestión de accesos directos físicos para Vita Moonlight
#include "shortcuts.h"
#include <psp2/ctrl.h>
#include <psp2/kernel/threadmgr.h>
#include <stdint.h>
#include <string.h>
#include "../keyboardsystem.h"
#include "../connection.h"
#include "../gui/ui_stream_overlay.h"

#define OVERLAY_CHORD_MASK (SCE_CTRL_START | SCE_CTRL_L1 | SCE_CTRL_R1)
#define OVERLAY_CHORD_WINDOW_US 1000000
#define KEYBOARD_CHORD_MASK (SCE_CTRL_START | SCE_CTRL_LEFT)
#define KEYBOARD_CHORD_WINDOW_US 1000000

enum overlay_chord_state {
    OVERLAY_CHORD_IDLE,
    OVERLAY_CHORD_PENDING,
    OVERLAY_CHORD_PASSTHROUGH,
    OVERLAY_CHORD_CONSUMED,
};

static enum overlay_chord_state overlay_state = OVERLAY_CHORD_IDLE;
static uint64_t overlay_started_at = 0;
static uint32_t overlay_pending_buttons = 0;
static bool keyboard_shortcut_consumed = false;
static uint64_t keyboard_shortcut_started_at = 0;
static int keyboard_shortcut_state = 0;

static void reset_overlay_chord(void) {
    overlay_state = OVERLAY_CHORD_IDLE;
    overlay_started_at = 0;
    overlay_pending_buttons = 0;
}

void reset_physical_shortcuts(void) {
    reset_overlay_chord();
    keyboard_shortcut_consumed = false;
    keyboard_shortcut_started_at = 0;
    keyboard_shortcut_state = 0;
}

static bool open_stream_overlay(SceCtrlData* pad) {
    pad->buttons &= ~OVERLAY_CHORD_MASK;
    overlay_state = OVERLAY_CHORD_CONSUMED;
    overlay_pending_buttons = 0;
    stream_overlay_open();
    return true;
}

static bool process_overlay_shortcut(SceCtrlData* pad, const SceCtrlData* pad_old) {
    uint64_t now = sceKernelGetSystemTimeWide();
    uint32_t chord_buttons = pad->buttons & OVERLAY_CHORD_MASK;
    bool start_pressed = (chord_buttons & SCE_CTRL_START) != 0;

    // Always accept the complete chord, regardless of which physical button
    // the controller sampled first. The START-led path below still buffers the
    // chord so none of it reaches the host; this fallback keeps the overlay
    // accessible when L/R was already down or the one-second window elapsed.
    if (overlay_state != OVERLAY_CHORD_CONSUMED &&
        chord_buttons == OVERLAY_CHORD_MASK) {
        return open_stream_overlay(pad);
    }

    if (overlay_state == OVERLAY_CHORD_IDLE && start_pressed &&
        (pad_old->buttons & SCE_CTRL_START) == 0) {
        overlay_state = OVERLAY_CHORD_PENDING;
        overlay_started_at = now;
        overlay_pending_buttons = chord_buttons;
    }

    if (overlay_state == OVERLAY_CHORD_PENDING) {
        overlay_pending_buttons |= chord_buttons;
        if (chord_buttons == OVERLAY_CHORD_MASK) {
            return open_stream_overlay(pad);
        }
        if (!start_pressed) {
            // Replay a normal short START press if it was not an overlay chord.
            pad->buttons |= overlay_pending_buttons;
            overlay_pending_buttons = 0;
            overlay_state = OVERLAY_CHORD_IDLE;
            return false;
        }
        if (now - overlay_started_at < OVERLAY_CHORD_WINDOW_US) {
            // START is the chord leader. Suppress it and any shoulders pressed
            // after it until the user completes the chord or the window expires.
            pad->buttons &= ~OVERLAY_CHORD_MASK;
            return true;
        }
        overlay_pending_buttons = 0;
        overlay_state = OVERLAY_CHORD_PASSTHROUGH;
    }

    if (overlay_state == OVERLAY_CHORD_PASSTHROUGH) {
        if (!start_pressed) overlay_state = OVERLAY_CHORD_IDLE;
        return false;
    }

    if (overlay_state == OVERLAY_CHORD_CONSUMED) {
        pad->buttons &= ~OVERLAY_CHORD_MASK;
        if (chord_buttons == 0) overlay_state = OVERLAY_CHORD_IDLE;
        return true;
    }
    return false;
}

// Devuelve true si se ejecutó un acceso directo y se debe limpiar el input
bool process_physical_shortcuts(SceCtrlData* pad, const SceCtrlData* pad_old) {
    // Shortcut Start+Left: detectar en cualquier orden y con margen de tiempo
    // Snapshots eliminados: solo se usan en vita.c
    uint64_t now = sceKernelGetSystemTimeWide();
    bool start_now = (pad->buttons & SCE_CTRL_START);
    bool left_now = (pad->buttons & SCE_CTRL_LEFT);
    bool start_prev = (pad_old->buttons & SCE_CTRL_START);

    if (keyboard_shortcut_consumed) {
        /* The IME is blocking. When it returns, consume the shortcut until
         * both members are physically released so START/LEFT cannot reappear
         * as remote input on the next 2 ms sample. */
        uint32_t chord_buttons = pad->buttons & KEYBOARD_CHORD_MASK;
        pad->buttons &= ~KEYBOARD_CHORD_MASK;
        if (chord_buttons == 0) {
            keyboard_shortcut_consumed = false;
            keyboard_shortcut_state = 0;
        }
        return true;
    }

    /* START is the leader. This avoids delaying ordinary D-pad Left input. */
    if (start_now && !start_prev) {
        keyboard_shortcut_started_at = now;
        keyboard_shortcut_state = 1;
    }
    // If both shortcut members arrive within the one-second window, open it.
    if (start_now && left_now) {
        if (keyboard_shortcut_state == 1 &&
            (now - keyboard_shortcut_started_at) <
                KEYBOARD_CHORD_WINDOW_US) {
                // Limpiar input local y en el host ANTES de abrir el teclado
                // Snapshots eliminados: solo se usan en vita.c
                // Obligatorio porque es bloqueante
                memset((void*)pad, 0, sizeof(SceCtrlData));
                LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
                /* START may also be buffered by the overlay shortcut. Drop
                 * that pending state so it cannot be replayed after IME. */
                reset_overlay_chord();
                keyboardsystem_open_keyboard();
                keyboard_shortcut_consumed = true;
                keyboard_shortcut_state = 0;
                return true;
        }
        keyboard_shortcut_state = 2;
    }
    if (!start_now) {
        keyboard_shortcut_state = 0;
    } else if (keyboard_shortcut_state == 1 &&
               now - keyboard_shortcut_started_at >=
                   KEYBOARD_CHORD_WINDOW_US) {
        keyboard_shortcut_state = 2;
    }
    return process_overlay_shortcut(pad, pad_old);
}
