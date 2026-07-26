#ifndef KEYBOARDSYSTEM_H
#define KEYBOARDSYSTEM_H

#include <stdbool.h>

void keyboardsystem_open_keyboard(void);
void keyboardsystem_close_keyboard(void);
void keyboardsystem_prepare_for_stream(void);
// Devuelve true si el overlay de teclado virtual está abierto
bool keyboardsystem_is_open(void);

#endif // KEYBOARDSYSTEM_H
