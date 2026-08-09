// keyboardsystem.c
// Lógica de apertura de teclado IME sin cuadro de texto para PS Vita
#include <psp2/kernel/processmgr.h>
#include <psp2/kernel/threadmgr.h>
#include <psp2/ctrl.h>
#include <psp2/display.h>
#include <psp2/libime.h>
#include <psp2/sysmodule.h>
#include <psp2/types.h>
#include <psp2/kernel/clib.h>
#include <string.h>
#include "Limelight.h" // Asegúrate de que la ruta sea correcta según tu proyecto
#include "input/keyboardkeys.h"
#include "input/keyboard_ime.h"
#include "config.h"

#define WORK_BUFFER_SIZE (SCE_IME_WORK_BUFFER_SIZE)
#define IME_MAX_TEXT_UNITS 4

static uint8_t work_buffer[WORK_BUFFER_SIZE];
// static SceWChar16 input_text_dummy[1];   // Eliminado: no se usa
/* SceImeParam requires room for maxTextLength units plus the terminator. */
static SceWChar16 output_text[IME_MAX_TEXT_UNITS + 1] = {1, 1, 1, 0, 0};
#include "input/keyboard.h"
#include <stddef.h>

// Layout seleccionado (por defecto EN_US)
static KeyboardLayout g_keyboard_layout = KB_LAYOUT_EN_US;


// Estado interno del overlay de teclado virtual
static uint32_t keyboard_overlay_open = 0;
static uint32_t keyboard_close_requested = 0;

static bool keyboard_flag_load(const uint32_t *flag) {
    return __atomic_load_n(flag, __ATOMIC_ACQUIRE) != 0;
}

static void keyboard_flag_store(uint32_t *flag, bool value) {
    __atomic_store_n(flag, value ? 1U : 0U, __ATOMIC_RELEASE);
}

void keyboardsystem_set_layout(KeyboardLayout layout) {
    g_keyboard_layout = layout;
}

// Exponer el estado para otros módulos
bool keyboardsystem_is_open(void) {
    return keyboard_flag_load(&keyboard_overlay_open);
}

void keyboardsystem_close_keyboard(void) {
    keyboard_flag_store(&keyboard_close_requested, true);
}

void keyboardsystem_prepare_for_stream(void) {
    keyboard_flag_store(&keyboard_close_requested, false);
}

static int find_vk_for_char(wchar_t ch, int* vk, int* needs_shift) {
    int count = 0;
    const struct CharVKMap* dict = get_char_vk_dict(g_keyboard_layout, &count);
    for (int i = 0; i < count; ++i) {
        if (dict[i].ch == ch) {
            *vk = dict[i].vk;
            *needs_shift = dict[i].needs_shift;
            return 1;
        }
    }
    return 0;
}

static SceWChar16 ime_working_buffer[4] = {1, 0, 0, 0};
static SceImeCaret caret_rev;
static int ime_just_opened = 1;
static int forzar_centro = 0;

static void reset_ime_output(void) {
    output_text[0] = 1;
    output_text[1] = 1;
    output_text[2] = 1;
    output_text[3] = 0;
    output_text[4] = 0;
}

static void request_ime_recenter(void) {
    reset_ime_output();
    forzar_centro = 1;
}

static void send_virtual_key(int vk, int needs_shift) {
    if (needs_shift) LiSendKeyboardEvent(0x10, KEY_ACTION_DOWN, 0);
    LiSendKeyboardEvent(vk, KEY_ACTION_DOWN, 0);
    LiSendKeyboardEvent(vk, KEY_ACTION_UP, 0);
    if (needs_shift) LiSendKeyboardEvent(0x10, KEY_ACTION_UP, 0);
}

static void keyboardsystem_ime_event_handler(void *arg, const SceImeEventData *e) {
    /*
     * Never put IME buffers, characters, virtual-key codes, or per-keystroke
     * events in the diagnostic log. Users may type credentials while a
     * capture is active.
     */
    (void)arg;
    if (e == NULL) return;

    uint32_t caret = 1;
    int32_t edit_length_change = 0;
    if (e->id == SCE_IME_EVENT_UPDATE_TEXT) {
        /* UPDATE_TEXT stores SceImeEditText in the event union. Reading the
         * union's top-level caretIndex here aliases preeditIndex instead of
         * the actual text caret and causes ordinary characters to disappear. */
        caret = e->param.text.caretIndex;
        edit_length_change = e->param.text.editLengthChange;
    } else if (e->id == SCE_IME_EVENT_UPDATE_CARET) {
        caret = e->param.caretIndex;
    }
    VitaKeyboardImeDecision ime_decision = vita_keyboard_ime_interpret(
        e->id, caret, edit_length_change,
        (const uint16_t *)output_text,
        sizeof(output_text) / sizeof(output_text[0]),
        ime_just_opened != 0);
    if (e->id == SCE_IME_EVENT_UPDATE_TEXT) {
        ime_just_opened = 0;
    }

    switch (ime_decision.action) {
        case VITA_KEYBOARD_IME_ACTION_CHARACTER: {
            int vk = 0;
            int needs_shift = 0;
            if (find_vk_for_char((wchar_t)ime_decision.character,
                                 &vk, &needs_shift)) {
                send_virtual_key(vk, needs_shift);
            }
            request_ime_recenter();
            break;
        }
        case VITA_KEYBOARD_IME_ACTION_BACKSPACE:
            send_virtual_key(0x08, 0);
            request_ime_recenter();
            break;
        case VITA_KEYBOARD_IME_ACTION_LEFT:
            send_virtual_key(0x25, 0);
            request_ime_recenter();
            break;
        case VITA_KEYBOARD_IME_ACTION_RIGHT:
            send_virtual_key(0x27, 0);
            request_ime_recenter();
            break;
        case VITA_KEYBOARD_IME_ACTION_ENTER:
            send_virtual_key(0x0D, 0);
            request_ime_recenter();
            break;
        case VITA_KEYBOARD_IME_ACTION_CLOSE:
            sceImeClose();
            break;
        case VITA_KEYBOARD_IME_ACTION_NONE:
        default:
            break;
    }
}

void keyboardsystem_open_keyboard(void) {
    /*
     * A disconnect may race a shortcut that was about to open the IME. Keep
     * the close request latched until the next stream starts so teardown can
     * never wait behind a newly opened keyboard.
     */
    if (keyboard_flag_load(&keyboard_close_requested)) {
        return;
    }

    // Marcar overlay como abierto
    keyboard_flag_store(&keyboard_overlay_open, true);

    // Asegura que el layout global esté sincronizado con la config antes de abrir el IME
    keyboardsystem_set_layout((KeyboardLayout)config.keyboard_layout);
    // Inicializar buffer y caret IME robustos para movimiento infinito
    ime_working_buffer[0] = 1;
    ime_working_buffer[1] = 1;
    ime_working_buffer[2] = 1;
    ime_working_buffer[3] = 0;
    reset_ime_output();
    sceClibMemset(&caret_rev, 0, sizeof(SceImeCaret));
    caret_rev.index = 1;
    ime_just_opened = 1;
    forzar_centro = 0;

    // 1) Cargar el módulo de IME (si no está cargado)
    sceSysmoduleLoadModule(SCE_SYSMODULE_IME);

    // 2) Inicializar parámetros de IME a cero y fijar sdkVersion
    SceImeParam param;
    sceImeParamInit(&param);

    // 3) Configurar campos obligatorios de SceImeParam
    KeyboardLayout layout = config.keyboard_layout;
    g_keyboard_layout = layout;
    switch (layout) {
        case KB_LAYOUT_EN_US:
            param.supportedLanguages = SCE_IME_LANGUAGE_ENGLISH;
            break;
        case KB_LAYOUT_ES_ES:
            param.supportedLanguages = SCE_IME_LANGUAGE_SPANISH;
            break;
        case KB_LAYOUT_ES_LATAM:
            param.supportedLanguages = SCE_IME_LANGUAGE_SPANISH;
            break;
        default:
            param.supportedLanguages = SCE_IME_LANGUAGE_ENGLISH;
            break;
    }
    param.languagesForced   = SCE_TRUE;
    param.type              = SCE_IME_TYPE_DEFAULT;
    param.option            = SCE_IME_OPTION_NO_ASSISTANCE;
    param.work              = work_buffer;
    param.handler           = keyboardsystem_ime_event_handler;
    param.initialText       = ime_working_buffer;
    param.maxTextLength     = IME_MAX_TEXT_UNITS;
    param.inputTextBuffer   = output_text;
    param.enterLabel        = SCE_IME_ENTER_LABEL_DEFAULT;

    // 4) Abrir el teclado en pantalla
    int res = sceImeOpen(&param);
    if (res < 0) {
        keyboard_flag_store(&keyboard_overlay_open, false);
        return;
    }
    // Solo aquí es seguro llamar a setText y setCaret
    sceImeSetText(ime_working_buffer, 4);
    sceImeSetCaret(&caret_rev);
    // 5) Bucle de actualización: llamar a sceImeUpdate() hasta que devuelva < 0
    while (1) {
        if (keyboard_flag_load(&keyboard_close_requested)) {
            sceImeClose();
            break;
        }
        if (forzar_centro) {
            SceWChar16 dummy[4] = {1, 1, 1, 0};
            sceImeSetText(dummy, 4);
            ime_working_buffer[0] = 1;
            ime_working_buffer[1] = 1;
            ime_working_buffer[2] = 1;
            ime_working_buffer[3] = 0;
            sceClibMemset(&caret_rev, 0, sizeof(SceImeCaret));
            caret_rev.index = 1;
            sceImeSetCaret(&caret_rev);
            forzar_centro = 0;
        }
        int status = sceImeUpdate();
        if (status < 0) {
            break;
        }
        sceKernelDelayThread(1000); // Esperar 1 ms
    }
    // Marcar overlay como cerrado al salir del bucle
    keyboard_flag_store(&keyboard_overlay_open, false);
}
