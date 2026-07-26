#ifndef UI_DIAGNOSTICS_H
#define UI_DIAGNOSTICS_H

#include <stdbool.h>
#include <stdint.h>

#include <psp2/ctrl.h>

/*
 * The lightweight performance overlay is deliberately separate from the
 * stream-control menu. OFF performs no metric collection unless file logging
 * or the full diagnostics screen is enabled.
 */
typedef enum UiDiagnosticsOverlayMode {
  UI_DIAGNOSTICS_OVERLAY_OFF = 0,
  UI_DIAGNOSTICS_OVERLAY_FPS = 1,
  UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK = 2,
  UI_DIAGNOSTICS_OVERLAY_ADVANCED = 3,
  UI_DIAGNOSTICS_OVERLAY_COUNT
} UiDiagnosticsOverlayMode;

typedef enum UiDiagnosticsNetworkState {
  UI_DIAGNOSTICS_NETWORK_UNKNOWN = 0,
  UI_DIAGNOSTICS_NETWORK_GOOD = 1,
  UI_DIAGNOSTICS_NETWORK_DEGRADED = 2
} UiDiagnosticsNetworkState;

/*
 * Pointer-free copy of the settings whose effect is established when the
 * video/input session starts. Keeping this scalar-only allows the selected
 * configuration to change (and mapping strings to be freed) without changing
 * the active-session record.
 */
typedef struct UiDiagnosticsReconnectSettings {
  int stream_width;
  int stream_height;
  int stream_fps;
  int stream_bitrate_kbps;
  int packet_size;
  int streaming_remotely;
  int audio_configuration;
  int supported_video_formats;
  int client_refresh_rate_x100;
  int color_space;
  int color_range;
  int encryption_flags;
  bool sops;
  bool local_audio;
  bool fullscreen;
  bool reference_frame_invalidation;
  bool frame_pacer;
  bool vblank_wait;
  bool crop_to_fill;
  bool disable_powersave;

  int controller_type;
  bool motion_enabled;
  int touchscreen_mode;
  int psbutton_mode;
  bool swap_shoulder_buttons;
  bool mapping_enabled;
  bool double_tap_sprint;
  uint32_t double_tap_sprint_step_time;
  int mouse_acceleration;
  float motion_scalar_x;
  float motion_scalar_y;
  bool front_touchzones;
  int back_deadzone_top;
  int back_deadzone_right;
  int back_deadzone_bottom;
  int back_deadzone_left;
  int special_keys_size;
  int special_keys_offset;
  uint32_t special_key_nw;
  uint32_t special_key_ne;
  uint32_t special_key_sw;
  uint32_t special_key_se;
} UiDiagnosticsReconnectSettings;

typedef struct UiDiagnosticsSnapshot {
  uint32_t rendered_fps;
  uint32_t target_fps;
  uint32_t configured_bitrate_kbps;
  uint32_t measured_video_kbps;
  uint32_t frames_in_window;
  uint32_t dropped_frames_in_window;
  uint32_t average_decode_us;
  uint32_t maximum_decode_us;
  uint32_t total_video_frames;
  uint32_t total_dropped_frames;
  uint32_t estimated_rtt_ms;
  uint32_t estimated_rtt_variance_ms;
  uint32_t recovered_packets_in_window;
  uint32_t failed_fec_packets_in_window;
  uint32_t out_of_sequence_packets_in_window;
  UiDiagnosticsNetworkState network_state;

  bool stream_connected;
  bool file_logging_enabled;
  int stream_width;
  int stream_height;
  int stream_fps;
  int packet_size;
  int selected_stream_width;
  int selected_stream_height;
  int selected_stream_fps;
  int selected_packet_size;
  uint32_t selected_bitrate_kbps;
  bool stream_settings_pending;
  bool stream_format_settings_pending;
  bool stream_tuning_settings_pending;
  int controller_type;
  bool motion_enabled;
  int selected_controller_type;
  bool selected_motion_enabled;
  bool controller_settings_pending;
  bool controller_capability_settings_pending;
  bool input_behavior_settings_pending;
  bool gyro_requested;
  uint16_t gyro_report_rate;
  uint32_t gyro_events_sent;
  int motion_sensor_error;
} UiDiagnosticsSnapshot;

void ui_diagnostics_init(void);
void ui_diagnostics_shutdown(void);
void ui_diagnostics_reset_session(void);
void ui_diagnostics_capture_reconnect_settings(
    UiDiagnosticsReconnectSettings *settings);
void ui_diagnostics_set_active_stream(
    const UiDiagnosticsReconnectSettings *settings);

UiDiagnosticsOverlayMode ui_diagnostics_get_overlay_mode(void);
void ui_diagnostics_set_overlay_mode(UiDiagnosticsOverlayMode mode);
void ui_diagnostics_cycle_overlay_mode(int direction);
const char *ui_diagnostics_overlay_mode_name(UiDiagnosticsOverlayMode mode);

/*
 * These event hooks are cheap no-ops while the overlay, diagnostics screen,
 * and optional file logging are all disabled.
 */
void ui_diagnostics_set_network_state(UiDiagnosticsNetworkState state);
void ui_diagnostics_set_logging_consumer(bool enabled);
bool ui_diagnostics_metrics_needed(void);
void ui_diagnostics_record_video_frame(uint32_t encoded_bytes,
                                       uint32_t decode_time_us,
                                       bool presented);
void ui_diagnostics_record_frame_drop(void);

void ui_diagnostics_get_snapshot(UiDiagnosticsSnapshot *snapshot);

/* Draw after the decoded frame and before vita2d_end_drawing(). */
void ui_diagnostics_draw_overlay(void);

/*
 * Dedicated real-time diagnostics screen. The caller retains ownership of
 * opening/closing its parent menu and should route controller input here while
 * this screen is open. Triangle starts or stops a fresh support-log capture.
 */
bool ui_diagnostics_screen_is_open(void);
void ui_diagnostics_screen_open(void);
void ui_diagnostics_screen_close(void);
void ui_diagnostics_screen_handle_input(const SceCtrlData *pad,
                                        const SceCtrlData *previous);
void ui_diagnostics_screen_draw(void);

#endif
