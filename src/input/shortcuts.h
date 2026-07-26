// shortcuts.h
#pragma once
#include <psp2/ctrl.h>
#include <stdbool.h>

// Devuelve true si se ejecutó un acceso directo y se debe limpiar el input
typedef struct SceCtrlData SceCtrlData;
void reset_physical_shortcuts(void);
bool process_physical_shortcuts(SceCtrlData* pad, const SceCtrlData* pad_old);
