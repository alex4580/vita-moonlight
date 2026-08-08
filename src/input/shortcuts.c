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

#define LOCAL_SHORTCUT_LEADER SCE_CTRL_SELECT
#define OVERLAY_CHORD_MASK (LOCAL_SHORTCUT_LEADER | SCE_CTRL_L1 | SCE_CTRL_R1)
#define OVERLAY_CHORD_WINDOW_US 1000000
#define KEYBOARD_CHORD_MASK (LOCAL_SHORTCUT_LEADER | SCE_CTRL_LEFT)
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
    bool leader_pressed =
        (chord_buttons & LOCAL_SHORTCUT_LEADER) != 0;

    if (overlay_state == OVERLAY_CHORD_IDLE && leader_pressed &&
        (pad_old->buttons & LOCAL_SHORTCUT_LEADER) == 0) {
        uint32_t previously_held_members =
            pad_old->buttons & (SCE_CTRL_L1 | SCE_CTRL_R1);
        if (previously_held_members == 0) {
            overlay_state = OVERLAY_CHORD_PENDING;
            overlay_started_at = now;
            overlay_pending_buttons = chord_buttons;
        } else {
            /* Do not turn shoulders already delivered to the PC into half of
             * a local chord. Only a leader-first/simultaneous sequence arms. */
            overlay_state = OVERLAY_CHORD_PASSTHROUGH;
        }
    }

    if (overlay_state == OVERLAY_CHORD_PENDING) {
        overlay_pending_buttons |= chord_buttons;
        if (chord_buttons == OVERLAY_CHORD_MASK) {
            return open_stream_overlay(pad);
        }
        if (!leader_pressed) {
            // Replay a normal short SELECT press if it was not a local chord.
            pad->buttons |= overlay_pending_buttons;
            overlay_pending_buttons = 0;
            overlay_state = OVERLAY_CHORD_IDLE;
            return false;
        }
        if (now - overlay_started_at < OVERLAY_CHORD_WINDOW_US) {
            // SELECT is the chord leader. Suppress it and any shoulders pressed
            // after it until the user completes the chord or the window expires.
            pad->buttons &= ~OVERLAY_CHORD_MASK;
            return true;
        }
        overlay_pending_buttons = 0;
        overlay_state = OVERLAY_CHORD_PASSTHROUGH;
    }

    if (overlay_state == OVERLAY_CHORD_PASSTHROUGH) {
        if (!leader_pressed) overlay_state = OVERLAY_CHORD_IDLE;
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
    // SELECT leads both local shortcuts so ordinary START is never delayed.
    uint64_t now = sceKernelGetSystemTimeWide();
    bool leader_now = (pad->buttons & LOCAL_SHORTCUT_LEADER);
    bool left_now = (pad->buttons & SCE_CTRL_LEFT);
    bool leader_prev = (pad_old->buttons & LOCAL_SHORTCUT_LEADER);

    if (keyboard_shortcut_consumed) {
        /* The IME is blocking. When it returns, consume the shortcut until
         * both members are physically released so SELECT/LEFT cannot reappear
         * as remote input on the next 2 ms sample. */
        uint32_t chord_buttons = pad->buttons & KEYBOARD_CHORD_MASK;
        pad->buttons &= ~KEYBOARD_CHORD_MASK;
        if (chord_buttons == 0) {
            keyboard_shortcut_consumed = false;
            keyboard_shortcut_state = 0;
        }
        return true;
    }

    /* SELECT is the leader. D-pad Left and START remain immediate. */
    if (leader_now && !leader_prev &&
        (pad_old->buttons & SCE_CTRL_LEFT) == 0) {
        keyboard_shortcut_started_at = now;
        keyboard_shortcut_state = 1;
    } else if (leader_now && !leader_prev) {
        /* LEFT was already remote input, so this cannot become a local chord. */
        keyboard_shortcut_state = 2;
    }
    // If both shortcut members arrive within the one-second window, open it.
    if (leader_now && left_now) {
        if (keyboard_shortcut_state == 1 &&
            (now - keyboard_shortcut_started_at) <
                KEYBOARD_CHORD_WINDOW_US) {
                // Limpiar input local y en el host ANTES de abrir el teclado
                // Snapshots eliminados: solo se usan en vita.c
                // Obligatorio porque es bloqueante
                memset((void*)pad, 0, sizeof(SceCtrlData));
                LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
                /* SELECT may also be buffered by the overlay shortcut. Drop
                 * that pending state so it cannot be replayed after IME. */
                reset_overlay_chord();
                keyboardsystem_open_keyboard();
                keyboard_shortcut_consumed = true;
                keyboard_shortcut_state = 0;
                return true;
        }
        keyboard_shortcut_state = 2;
    }
    if (!leader_now) {
        keyboard_shortcut_state = 0;
    } else if (keyboard_shortcut_state == 1 &&
               now - keyboard_shortcut_started_at >=
                   KEYBOARD_CHORD_WINDOW_US) {
        keyboard_shortcut_state = 2;
    }
    return process_overlay_shortcut(pad, pad_old);
}
