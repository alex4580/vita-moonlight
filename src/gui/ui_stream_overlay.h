#ifndef UI_STREAM_OVERLAY_H
#define UI_STREAM_OVERLAY_H

#include <stdbool.h>
#include <psp2/ctrl.h>

bool stream_overlay_is_open(void);
void stream_overlay_open(void);
void stream_overlay_close(void);
void stream_overlay_reset(void);
void stream_overlay_handle_input(const SceCtrlData *pad, const SceCtrlData *previous);
bool stream_overlay_take_disconnect_request(void);
bool stream_overlay_take_close_game_request(void);
bool stream_overlay_take_quit_app_request(void);
bool stream_overlay_take_recover_host_request(void);
void stream_overlay_draw(void);

#endif
