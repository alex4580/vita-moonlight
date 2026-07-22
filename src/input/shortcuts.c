// shortcuts.c
// Gestión de accesos directos físicos para Vita Moonlight
#include "shortcuts.h"
#include <psp2/ctrl.h>
#include <psp2/kernel/threadmgr.h>
#include <stdint.h>
#include <string.h>
#include "../keyboardsystem.h"
#include "../connection.h"
#include "../debug.h"
#include "../gui/ui_stream_overlay.h"

#define OVERLAY_CHORD_MASK (SCE_CTRL_START | SCE_CTRL_L1 | SCE_CTRL_R1)
#define OVERLAY_CHORD_WINDOW_US 300000

enum overlay_chord_state {
    OVERLAY_CHORD_IDLE,
    OVERLAY_CHORD_PENDING,
    OVERLAY_CHORD_PASSTHROUGH,
    OVERLAY_CHORD_CONSUMED,
};

static enum overlay_chord_state overlay_state = OVERLAY_CHORD_IDLE;
static uint64_t overlay_started_at = 0;
static uint32_t overlay_pending_buttons = 0;

void reset_physical_shortcuts(void) {
    overlay_state = OVERLAY_CHORD_IDLE;
    overlay_started_at = 0;
    overlay_pending_buttons = 0;
}

static bool process_overlay_shortcut(SceCtrlData* pad, const SceCtrlData* pad_old) {
    uint64_t now = sceKernelGetSystemTimeWide();
    uint32_t chord_buttons = pad->buttons & OVERLAY_CHORD_MASK;
    bool start_pressed = (chord_buttons & SCE_CTRL_START) != 0;

    if (overlay_state == OVERLAY_CHORD_IDLE && start_pressed &&
        (pad_old->buttons & SCE_CTRL_START) == 0 &&
        (pad_old->buttons & (SCE_CTRL_L1 | SCE_CTRL_R1)) == 0) {
        overlay_state = OVERLAY_CHORD_PENDING;
        overlay_started_at = now;
        overlay_pending_buttons = chord_buttons;
    }

    if (overlay_state == OVERLAY_CHORD_PENDING) {
        overlay_pending_buttons |= chord_buttons;
        if (chord_buttons == OVERLAY_CHORD_MASK) {
            pad->buttons &= ~OVERLAY_CHORD_MASK;
            overlay_state = OVERLAY_CHORD_CONSUMED;
            overlay_pending_buttons = 0;
            vita_debug_log("Shortcut: START+L1+R1 opened the stream overlay without forwarding the chord");
            stream_overlay_open();
            return true;
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
    static bool keyboard_shortcut_blocked = false;
    static uint64_t shortcut_time = 0;
    static int shortcut_state = 0; // 0: nada, 1: uno presionado, 2: ambos presionados
    // Snapshots eliminados: solo se usan en vita.c
    uint64_t now = sceKernelGetSystemTimeWide();
    bool start_now = (pad->buttons & SCE_CTRL_START);
    bool left_now = (pad->buttons & SCE_CTRL_LEFT);
    bool start_prev = (pad_old->buttons & SCE_CTRL_START);
    bool left_prev = (pad_old->buttons & SCE_CTRL_LEFT);

    // Detectar flanco de subida de cualquiera de los dos
    if ((start_now && !start_prev) || (left_now && !left_prev)) {
        vita_debug_log("Shortcut: Flanco de subida detectado (START=%d, LEFT=%d) en t=%llu", start_now, left_now, now);
        shortcut_time = now;
        shortcut_state = 1;
    }
    // Si ambos están presionados dentro de 300ms, activar shortcut
    if (start_now && left_now) {
        if (shortcut_state == 1 && (now - shortcut_time) < 300000) {
            if (!keyboard_shortcut_blocked) {
                vita_debug_log("Shortcut: START+LEFT detectado en t=%llu (delta=%llu)", now, now-shortcut_time);
                // Limpiar input local y en el host ANTES de abrir el teclado
                // Snapshots eliminados: solo se usan en vita.c
                // Obligatorio porque es bloqueante
                memset((void*)pad, 0, sizeof(SceCtrlData));
                LiSendMultiControllerEvent(0, 1, 0, 0, 0, 0, 0, 0, 0);
                vita_debug_log("[SHORTCUT] Overlay activo: ABRIR teclado, frame vacío enviado al host");
                keyboardsystem_open_keyboard();
                keyboard_shortcut_blocked = true;
                shortcut_state = 0;
                return true;
            }
        }
        shortcut_state = 2;
    }
    if (!start_now && !left_now) {
        if (keyboard_shortcut_blocked || shortcut_state != 0) {
            vita_debug_log("Shortcut: START y LEFT liberados, reseteando estado");
        }
        keyboard_shortcut_blocked = false;
        shortcut_state = 0;
    }
    return process_overlay_shortcut(pad, pad_old);
}
