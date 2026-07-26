#include "ui_diagnostics.h"

#include "guilib.h"
#include "../config.h"
#include "../connection.h"
#include "../debug.h"
#include "../input/motion.h"
#include "../video/vita.h"

#include <stdio.h>
#include <string.h>

#include <pthread.h>
#include <psp2/kernel/threadmgr.h>
#include <vita2d.h>

#define DIAGNOSTICS_OVERLAY_ALPHA 128
#define DIAGNOSTICS_SAMPLE_US 1000000ULL

enum UiDiagnosticsConsumer {
  UI_DIAGNOSTICS_CONSUMER_OVERLAY = 1U << 0,
  UI_DIAGNOSTICS_CONSUMER_SCREEN = 1U << 1,
  UI_DIAGNOSTICS_CONSUMER_LOGGING = 1U << 2,
};

typedef struct UiDiagnosticsMetrics {
  uint64_t window_started_us;
  uint64_t window_video_bytes;
  uint64_t window_decode_us;
  uint32_t window_frames;
  uint32_t window_presented_frames;
  uint32_t window_dropped_frames;
  uint32_t window_max_decode_us;

  uint32_t measured_video_kbps;
  uint32_t sampled_frames;
  uint32_t sampled_dropped_frames;
  uint32_t average_decode_us;
  uint32_t maximum_decode_us;
  uint32_t total_video_frames;
  uint32_t total_dropped_frames;
  UiDiagnosticsReconnectSettings active_settings;
  bool active_stream_valid;
  uint32_t sample_consumer_generation;
  uint32_t estimated_rtt_ms;
  uint32_t estimated_rtt_variance_ms;
  uint32_t sampled_recovered_packets;
  uint32_t sampled_failed_fec_packets;
  uint32_t sampled_out_of_sequence_packets;
  uint32_t last_recovered_packets;
  uint32_t last_failed_fec_packets;
  uint32_t last_out_of_sequence_packets;
  UiDiagnosticsNetworkState network_state;
} UiDiagnosticsMetrics;

static UiDiagnosticsMetrics metrics;
static pthread_mutex_t metrics_mutex = PTHREAD_MUTEX_INITIALIZER;
static uint32_t metrics_consumer_mask = 0;
static uint32_t metrics_consumer_generation = 1;
static uint32_t diagnostics_screen_open = 0;
static uint32_t diagnostics_overlay_mode = UI_DIAGNOSTICS_OVERLAY_OFF;

static uint32_t atomic_load_u32(const uint32_t *value) {
  return __atomic_load_n(value, __ATOMIC_ACQUIRE);
}

static void atomic_store_u32(uint32_t *value, uint32_t next) {
  __atomic_store_n(value, next, __ATOMIC_RELEASE);
}

bool ui_diagnostics_metrics_needed(void) {
  return atomic_load_u32(&metrics_consumer_mask) != 0;
}

static UiDiagnosticsOverlayMode sanitize_overlay_mode(int mode) {
  if (mode < UI_DIAGNOSTICS_OVERLAY_OFF ||
      mode >= UI_DIAGNOSTICS_OVERLAY_COUNT) {
    return UI_DIAGNOSTICS_OVERLAY_OFF;
  }
  return (UiDiagnosticsOverlayMode)mode;
}

static const char *network_state_name(UiDiagnosticsNetworkState state) {
  switch (state) {
    case UI_DIAGNOSTICS_NETWORK_GOOD:
      return "Good";
    case UI_DIAGNOSTICS_NETWORK_DEGRADED:
      return "Degraded";
    default:
      return "Waiting";
  }
}

static void clear_sampling_locked(void) {
  metrics.window_started_us = 0;
  metrics.window_video_bytes = 0;
  metrics.window_decode_us = 0;
  metrics.window_frames = 0;
  metrics.window_presented_frames = 0;
  metrics.window_dropped_frames = 0;
  metrics.window_max_decode_us = 0;
  metrics.measured_video_kbps = 0;
  metrics.sampled_frames = 0;
  metrics.sampled_dropped_frames = 0;
  metrics.average_decode_us = 0;
  metrics.maximum_decode_us = 0;
  metrics.total_video_frames = 0;
  metrics.total_dropped_frames = 0;
  metrics.estimated_rtt_ms = 0;
  metrics.estimated_rtt_variance_ms = 0;
  metrics.sampled_recovered_packets = 0;
  metrics.sampled_failed_fec_packets = 0;
  metrics.sampled_out_of_sequence_packets = 0;
  metrics.last_recovered_packets = 0;
  metrics.last_failed_fec_packets = 0;
  metrics.last_out_of_sequence_packets = 0;
  metrics.sample_consumer_generation = 0;
}

static void set_metrics_consumer(uint32_t consumer, bool enabled) {
  pthread_mutex_lock(&metrics_mutex);
  uint32_t old_mask = atomic_load_u32(&metrics_consumer_mask);
  uint32_t new_mask = enabled ? old_mask | consumer : old_mask & ~consumer;
  if (new_mask != old_mask) {
    bool was_active = old_mask != 0;
    bool is_active = new_mask != 0;
    if (was_active != is_active) {
      metrics_consumer_generation++;
      if (metrics_consumer_generation == 0) {
        metrics_consumer_generation = 1;
      }
      if (is_active) {
        /*
         * Never publish an old sample while a newly enabled consumer waits for
         * the first decoded frame to establish fresh RTP counter baselines.
         */
        clear_sampling_locked();
      }
    }
    atomic_store_u32(&metrics_consumer_mask, new_mask);
  }
  pthread_mutex_unlock(&metrics_mutex);
}

static bool prepare_sample_locked(uint64_t now_us) {
  if (atomic_load_u32(&metrics_consumer_mask) == 0) return false;
  if (metrics.sample_consumer_generation == metrics_consumer_generation) {
    return true;
  }

  clear_sampling_locked();
  metrics.window_started_us = now_us;
  const RTP_VIDEO_STATS *video_stats = LiGetRTPVideoStats();
  if (video_stats) {
    metrics.last_recovered_packets = video_stats->packetCountFecRecovered;
    metrics.last_failed_fec_packets = video_stats->packetCountFecFailed;
    metrics.last_out_of_sequence_packets = video_stats->packetCountOOS;
  }
  metrics.sample_consumer_generation = metrics_consumer_generation;
  return true;
}

static void finish_sample(uint64_t now_us) {
  if (metrics.window_started_us == 0) {
    metrics.window_started_us = now_us;
    return;
  }

  uint64_t elapsed_us = now_us - metrics.window_started_us;
  if (elapsed_us < DIAGNOSTICS_SAMPLE_US) return;

  metrics.measured_video_kbps = elapsed_us == 0
      ? 0
      : (uint32_t)((metrics.window_video_bytes * 8000ULL) / elapsed_us);
  /*
   * Normalize to elapsed time because a sampling window can be slightly
   * longer than one second. A raw frame count can otherwise read 61+ FPS.
   */
  metrics.sampled_frames = elapsed_us == 0
      ? 0
      : (uint32_t)(
          (metrics.window_presented_frames * 1000000ULL + elapsed_us / 2) /
          elapsed_us);
  metrics.sampled_dropped_frames = elapsed_us == 0
      ? 0
      : (uint32_t)(
          (metrics.window_dropped_frames * 1000000ULL + elapsed_us / 2) /
          elapsed_us);
  metrics.average_decode_us = metrics.window_frames == 0
      ? 0
      : (uint32_t)(metrics.window_decode_us / metrics.window_frames);
  metrics.maximum_decode_us = metrics.window_max_decode_us;

  uint32_t estimated_rtt = 0;
  uint32_t estimated_rtt_variance = 0;
  if (connection_is_connected() &&
      LiGetEstimatedRttInfo(&estimated_rtt, &estimated_rtt_variance)) {
    metrics.estimated_rtt_ms = estimated_rtt;
    metrics.estimated_rtt_variance_ms = estimated_rtt_variance;
  } else {
    metrics.estimated_rtt_ms = 0;
    metrics.estimated_rtt_variance_ms = 0;
  }

  const RTP_VIDEO_STATS *video_stats = LiGetRTPVideoStats();
  if (video_stats) {
    metrics.sampled_recovered_packets =
        video_stats->packetCountFecRecovered - metrics.last_recovered_packets;
    metrics.sampled_failed_fec_packets =
        video_stats->packetCountFecFailed - metrics.last_failed_fec_packets;
    metrics.sampled_out_of_sequence_packets =
        video_stats->packetCountOOS - metrics.last_out_of_sequence_packets;
    metrics.last_recovered_packets = video_stats->packetCountFecRecovered;
    metrics.last_failed_fec_packets = video_stats->packetCountFecFailed;
    metrics.last_out_of_sequence_packets = video_stats->packetCountOOS;
  }

  if (vita_debug_is_logging_enabled()) {
    int active_fps = metrics.active_stream_valid
        ? metrics.active_settings.stream_fps
        : config.stream.fps;
    int active_bitrate = metrics.active_stream_valid
        ? metrics.active_settings.stream_bitrate_kbps
        : config.stream.bitrate;
    vita_debug_log(
        "[PERF] fps=%u/%d video=%u kbps configured=%d kbps "
        "frames=%u dropped=%u decode_avg=%u us decode_max=%u us "
        "network=%s rtt=%u ms variance=%u ms fec_recovered=%u "
        "fec_failed=%u out_of_sequence=%u",
        metrics.sampled_frames,
        active_fps,
        metrics.measured_video_kbps,
        active_bitrate,
        metrics.window_frames,
        metrics.sampled_dropped_frames,
        metrics.average_decode_us,
        metrics.maximum_decode_us,
        network_state_name(metrics.network_state),
        metrics.estimated_rtt_ms,
        metrics.estimated_rtt_variance_ms,
        metrics.sampled_recovered_packets,
        metrics.sampled_failed_fec_packets,
        metrics.sampled_out_of_sequence_packets);
  }

  metrics.window_started_us = now_us;
  metrics.window_video_bytes = 0;
  metrics.window_decode_us = 0;
  metrics.window_frames = 0;
  metrics.window_presented_frames = 0;
  metrics.window_dropped_frames = 0;
  metrics.window_max_decode_us = 0;
}

void ui_diagnostics_init(void) {
  UiDiagnosticsOverlayMode mode =
      sanitize_overlay_mode(config.performance_overlay_mode);
  uint32_t initial_consumers =
      mode >= UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK
          ? UI_DIAGNOSTICS_CONSUMER_OVERLAY
          : 0;
  if (vita_debug_is_logging_enabled()) {
    initial_consumers |= UI_DIAGNOSTICS_CONSUMER_LOGGING;
  }

  pthread_mutex_lock(&metrics_mutex);
  memset(&metrics, 0, sizeof(metrics));
  metrics.network_state = UI_DIAGNOSTICS_NETWORK_UNKNOWN;
  metrics_consumer_generation = 1;
  atomic_store_u32(&metrics_consumer_mask, initial_consumers);
  pthread_mutex_unlock(&metrics_mutex);
  atomic_store_u32(&diagnostics_screen_open, 0);
  atomic_store_u32(&diagnostics_overlay_mode, (uint32_t)mode);
}

void ui_diagnostics_shutdown(void) {
  atomic_store_u32(&diagnostics_screen_open, 0);
  pthread_mutex_lock(&metrics_mutex);
  metrics_consumer_generation++;
  atomic_store_u32(&metrics_consumer_mask, 0);
  pthread_mutex_unlock(&metrics_mutex);
}

void ui_diagnostics_reset_session(void) {
  pthread_mutex_lock(&metrics_mutex);
  UiDiagnosticsNetworkState state = metrics.network_state;
  memset(&metrics, 0, sizeof(metrics));
  metrics.network_state = state;
  pthread_mutex_unlock(&metrics_mutex);
}

void ui_diagnostics_capture_reconnect_settings(
    UiDiagnosticsReconnectSettings *settings) {
  if (!settings) return;
  memset(settings, 0, sizeof(*settings));

  settings->stream_width = config.stream.width;
  settings->stream_height = config.stream.height;
  settings->stream_fps = config.stream.fps;
  settings->stream_bitrate_kbps = config.stream.bitrate;
  settings->packet_size = config.stream.packetSize;
  settings->streaming_remotely = config.stream.streamingRemotely;
  settings->audio_configuration = config.stream.audioConfiguration;
  settings->supported_video_formats = config.stream.supportedVideoFormats;
  settings->client_refresh_rate_x100 = config.stream.clientRefreshRateX100;
  settings->color_space = config.stream.colorSpace;
  settings->color_range = config.stream.colorRange;
  settings->encryption_flags = config.stream.encryptionFlags;
  settings->sops = config.sops;
  settings->local_audio = config.localaudio;
  settings->fullscreen = config.fullscreen;
  settings->reference_frame_invalidation =
      config.enable_ref_frame_invalidation;
  settings->frame_pacer = config.enable_frame_pacer;
  settings->vblank_wait = config.enable_vita_vblank_wait;
  settings->crop_to_fill = config.center_region_only;
  settings->disable_powersave = config.disable_powersave;

  settings->controller_type = config.controller_type;
  settings->motion_enabled = config.enable_motion_controls;
  settings->touchscreen_mode = config.touchscreen_mode;
  settings->psbutton_mode = config.psbutton_mode;
  settings->swap_shoulder_buttons = config.swap_shoulder_buttons;
  settings->mapping_enabled = config.mapping != NULL;
  settings->double_tap_sprint = config.enable_double_tap_sprint;
  settings->double_tap_sprint_step_time =
      config.double_tap_sprint_step_time;
  settings->mouse_acceleration = config.mouse_acceleration;
  settings->motion_scalar_x = config.motion_controls_scalar_x;
  settings->motion_scalar_y = config.motion_controls_scalar_y;
  settings->front_touchzones = config.enable_front_touchzones;
  settings->back_deadzone_top = config.back_deadzone.top;
  settings->back_deadzone_right = config.back_deadzone.right;
  settings->back_deadzone_bottom = config.back_deadzone.bottom;
  settings->back_deadzone_left = config.back_deadzone.left;
  settings->special_keys_size = config.special_keys.size;
  settings->special_keys_offset = config.special_keys.offset;
  settings->special_key_nw = config.special_keys.nw;
  settings->special_key_ne = config.special_keys.ne;
  settings->special_key_sw = config.special_keys.sw;
  settings->special_key_se = config.special_keys.se;
}

void ui_diagnostics_set_active_stream(
    const UiDiagnosticsReconnectSettings *settings) {
  if (!settings) return;
  pthread_mutex_lock(&metrics_mutex);
  metrics.active_settings = *settings;
  metrics.active_stream_valid = true;
  pthread_mutex_unlock(&metrics_mutex);
}

UiDiagnosticsOverlayMode ui_diagnostics_get_overlay_mode(void) {
  return sanitize_overlay_mode(
      (int)atomic_load_u32(&diagnostics_overlay_mode));
}

void ui_diagnostics_set_overlay_mode(UiDiagnosticsOverlayMode mode) {
  UiDiagnosticsOverlayMode sanitized = sanitize_overlay_mode(mode);
  set_metrics_consumer(
      UI_DIAGNOSTICS_CONSUMER_OVERLAY,
      sanitized >= UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK);
  atomic_store_u32(&diagnostics_overlay_mode, (uint32_t)sanitized);
  config.performance_overlay_mode = sanitized;

  /*
   * show_fps is retained only to migrate old configuration files. Keeping it
   * off prevents the old opaque top-left counter from being drawn alongside
   * the new translucent top-right overlay.
   */
  config.show_fps = false;
}

void ui_diagnostics_cycle_overlay_mode(int direction) {
  int mode = (int)ui_diagnostics_get_overlay_mode();
  mode += direction < 0 ? -1 : 1;
  if (mode < 0) mode = UI_DIAGNOSTICS_OVERLAY_COUNT - 1;
  if (mode >= UI_DIAGNOSTICS_OVERLAY_COUNT) mode = 0;
  ui_diagnostics_set_overlay_mode((UiDiagnosticsOverlayMode)mode);
}

const char *ui_diagnostics_overlay_mode_name(UiDiagnosticsOverlayMode mode) {
  switch (sanitize_overlay_mode(mode)) {
    case UI_DIAGNOSTICS_OVERLAY_FPS:
      return "Frame rate";
    case UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK:
      return "Frame rate + network";
    case UI_DIAGNOSTICS_OVERLAY_ADVANCED:
      return "Advanced";
    default:
      return "Off";
  }
}

void ui_diagnostics_set_network_state(UiDiagnosticsNetworkState state) {
  if (state < UI_DIAGNOSTICS_NETWORK_UNKNOWN ||
      state > UI_DIAGNOSTICS_NETWORK_DEGRADED) {
    state = UI_DIAGNOSTICS_NETWORK_UNKNOWN;
  }
  pthread_mutex_lock(&metrics_mutex);
  metrics.network_state = state;
  pthread_mutex_unlock(&metrics_mutex);
}

void ui_diagnostics_set_logging_consumer(bool enabled) {
  set_metrics_consumer(UI_DIAGNOSTICS_CONSUMER_LOGGING, enabled);
}

void ui_diagnostics_record_video_frame(uint32_t encoded_bytes,
                                       uint32_t decode_time_us,
                                       bool presented) {
  if (!ui_diagnostics_metrics_needed()) return;

  pthread_mutex_lock(&metrics_mutex);
  uint64_t now_us = sceKernelGetSystemTimeWide();
  if (!prepare_sample_locked(now_us)) {
    pthread_mutex_unlock(&metrics_mutex);
    return;
  }

  metrics.window_video_bytes += encoded_bytes;
  metrics.window_decode_us += decode_time_us;
  metrics.window_frames++;
  metrics.total_video_frames++;
  if (presented) {
    metrics.window_presented_frames++;
  } else {
    metrics.window_dropped_frames++;
    metrics.total_dropped_frames++;
  }
  if (decode_time_us > metrics.window_max_decode_us) {
    metrics.window_max_decode_us = decode_time_us;
  }

  finish_sample(now_us);
  pthread_mutex_unlock(&metrics_mutex);
}

void ui_diagnostics_record_frame_drop(void) {
  if (!ui_diagnostics_metrics_needed()) return;
  pthread_mutex_lock(&metrics_mutex);
  uint64_t now_us = sceKernelGetSystemTimeWide();
  if (!prepare_sample_locked(now_us)) {
    pthread_mutex_unlock(&metrics_mutex);
    return;
  }
  metrics.window_dropped_frames++;
  metrics.total_dropped_frames++;
  finish_sample(now_us);
  pthread_mutex_unlock(&metrics_mutex);
}

static bool stream_format_settings_equal(
    const UiDiagnosticsReconnectSettings *a,
    const UiDiagnosticsReconnectSettings *b) {
  return a->stream_width == b->stream_width &&
         a->stream_height == b->stream_height &&
         a->stream_fps == b->stream_fps &&
         a->stream_bitrate_kbps == b->stream_bitrate_kbps &&
         a->packet_size == b->packet_size &&
         a->audio_configuration == b->audio_configuration &&
         a->supported_video_formats == b->supported_video_formats &&
         a->client_refresh_rate_x100 == b->client_refresh_rate_x100 &&
         a->color_space == b->color_space &&
         a->color_range == b->color_range &&
         a->encryption_flags == b->encryption_flags;
}

static bool stream_tuning_settings_equal(
    const UiDiagnosticsReconnectSettings *a,
    const UiDiagnosticsReconnectSettings *b) {
  return a->streaming_remotely == b->streaming_remotely &&
         a->sops == b->sops &&
         a->local_audio == b->local_audio &&
         a->fullscreen == b->fullscreen &&
         a->reference_frame_invalidation ==
             b->reference_frame_invalidation &&
         a->frame_pacer == b->frame_pacer &&
         a->vblank_wait == b->vblank_wait &&
         a->crop_to_fill == b->crop_to_fill &&
         a->disable_powersave == b->disable_powersave;
}

static bool controller_capability_settings_equal(
    const UiDiagnosticsReconnectSettings *a,
    const UiDiagnosticsReconnectSettings *b) {
  return a->controller_type == b->controller_type &&
         a->motion_enabled == b->motion_enabled &&
         a->touchscreen_mode == b->touchscreen_mode;
}

static bool input_behavior_settings_equal(
    const UiDiagnosticsReconnectSettings *a,
    const UiDiagnosticsReconnectSettings *b) {
  return a->psbutton_mode == b->psbutton_mode &&
         a->swap_shoulder_buttons == b->swap_shoulder_buttons &&
         a->mapping_enabled == b->mapping_enabled &&
         a->double_tap_sprint == b->double_tap_sprint &&
         a->double_tap_sprint_step_time ==
             b->double_tap_sprint_step_time &&
         a->mouse_acceleration == b->mouse_acceleration &&
         a->motion_scalar_x == b->motion_scalar_x &&
         a->motion_scalar_y == b->motion_scalar_y &&
         a->front_touchzones == b->front_touchzones &&
         a->back_deadzone_top == b->back_deadzone_top &&
         a->back_deadzone_right == b->back_deadzone_right &&
         a->back_deadzone_bottom == b->back_deadzone_bottom &&
         a->back_deadzone_left == b->back_deadzone_left &&
         a->special_keys_size == b->special_keys_size &&
         a->special_keys_offset == b->special_keys_offset &&
         a->special_key_nw == b->special_key_nw &&
         a->special_key_ne == b->special_key_ne &&
         a->special_key_sw == b->special_key_sw &&
         a->special_key_se == b->special_key_se;
}

void ui_diagnostics_get_snapshot(UiDiagnosticsSnapshot *snapshot) {
  if (!snapshot) return;

  memset(snapshot, 0, sizeof(*snapshot));
  UiDiagnosticsReconnectSettings selected;
  ui_diagnostics_capture_reconnect_settings(&selected);
  vitavideo_get_fps(&snapshot->rendered_fps, &snapshot->target_fps);
  if (snapshot->target_fps == 0) {
    snapshot->target_fps = selected.stream_fps;
  }
  bool connected = connection_is_connected();
  pthread_mutex_lock(&metrics_mutex);
  bool active_stream_valid =
      metrics.active_stream_valid && connected;
  const UiDiagnosticsReconnectSettings *active =
      active_stream_valid ? &metrics.active_settings : &selected;
  snapshot->configured_bitrate_kbps = active_stream_valid &&
          active->stream_bitrate_kbps > 0
      ? (uint32_t)active->stream_bitrate_kbps
      : (selected.stream_bitrate_kbps > 0
             ? (uint32_t)selected.stream_bitrate_kbps
             : 0);
  snapshot->measured_video_kbps = metrics.measured_video_kbps;
  snapshot->frames_in_window = metrics.sampled_frames;
  snapshot->dropped_frames_in_window = metrics.sampled_dropped_frames;
  snapshot->average_decode_us = metrics.average_decode_us;
  snapshot->maximum_decode_us = metrics.maximum_decode_us;
  snapshot->total_video_frames = metrics.total_video_frames;
  snapshot->total_dropped_frames = metrics.total_dropped_frames;
  snapshot->estimated_rtt_ms = metrics.estimated_rtt_ms;
  snapshot->estimated_rtt_variance_ms =
      metrics.estimated_rtt_variance_ms;
  snapshot->recovered_packets_in_window =
      metrics.sampled_recovered_packets;
  snapshot->failed_fec_packets_in_window =
      metrics.sampled_failed_fec_packets;
  snapshot->out_of_sequence_packets_in_window =
      metrics.sampled_out_of_sequence_packets;
  snapshot->network_state = metrics.network_state;

  snapshot->stream_connected = connected;
  snapshot->file_logging_enabled = vita_debug_is_logging_enabled();
  snapshot->stream_width = active->stream_width;
  snapshot->stream_height = active->stream_height;
  snapshot->stream_fps = active->stream_fps;
  snapshot->packet_size = active->packet_size;
  snapshot->selected_stream_width = selected.stream_width;
  snapshot->selected_stream_height = selected.stream_height;
  snapshot->selected_stream_fps = selected.stream_fps;
  snapshot->selected_packet_size = selected.packet_size;
  snapshot->selected_bitrate_kbps =
      selected.stream_bitrate_kbps > 0
          ? (uint32_t)selected.stream_bitrate_kbps
          : 0;
  snapshot->stream_format_settings_pending =
      active_stream_valid &&
      !stream_format_settings_equal(active, &selected);
  snapshot->stream_tuning_settings_pending =
      active_stream_valid &&
      !stream_tuning_settings_equal(active, &selected);
  snapshot->stream_settings_pending =
      snapshot->stream_format_settings_pending ||
      snapshot->stream_tuning_settings_pending;
  snapshot->controller_type = active->controller_type;
  snapshot->motion_enabled = active->motion_enabled;
  snapshot->selected_controller_type = selected.controller_type;
  snapshot->selected_motion_enabled = selected.motion_enabled;
  snapshot->controller_capability_settings_pending =
      active_stream_valid &&
      !controller_capability_settings_equal(active, &selected);
  snapshot->input_behavior_settings_pending =
      active_stream_valid &&
      !input_behavior_settings_equal(active, &selected);
  snapshot->controller_settings_pending =
      snapshot->controller_capability_settings_pending ||
      snapshot->input_behavior_settings_pending;
  pthread_mutex_unlock(&metrics_mutex);

  VitaMotionStatus motion;
  vita_motion_get_status(&motion);
  snapshot->gyro_requested = motion.gyro_requested;
  snapshot->gyro_report_rate = motion.gyro_report_rate;
  snapshot->gyro_events_sent = motion.gyro_events_sent;
  snapshot->motion_sensor_error = motion.last_sensor_error;

}

static void draw_overlay_text(int x, int y, const char *text) {
  vita2d_font_draw_text(font, x, y, RGBA8(255, 255, 255, 235), 15, text);
}

void ui_diagnostics_draw_overlay(void) {
  UiDiagnosticsOverlayMode mode = ui_diagnostics_get_overlay_mode();
  if (mode == UI_DIAGNOSTICS_OVERLAY_OFF ||
      ui_diagnostics_screen_is_open()) {
    return;
  }

  UiDiagnosticsSnapshot snapshot;
  bool advanced = mode == UI_DIAGNOSTICS_OVERLAY_ADVANCED;
  if (mode >= UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK) {
    ui_diagnostics_get_snapshot(&snapshot);
  }

  int width = 168;
  int height = 34;
  if (mode == UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK) {
    width = 310;
    height = 58;
  } else if (advanced) {
    width = snapshot.stream_settings_pending ? 390 : 360;
    height = snapshot.stream_settings_pending ? 176 : 153;
  }

  const int x = WIDTH - width - 12;
  const int y = 12;
  vita2d_draw_rectangle(x, y, width, height,
                        RGBA8(8, 12, 20, DIAGNOSTICS_OVERLAY_ALPHA));

  char line[96];
  uint32_t rendered_fps = 0;
  uint32_t target_fps = 0;
  vitavideo_get_fps(&rendered_fps, &target_fps);
  snprintf(line, sizeof(line), "FPS  %u / %u",
           rendered_fps, target_fps ? target_fps : config.stream.fps);
  draw_overlay_text(x + 10, y + 22, line);

  if (mode >= UI_DIAGNOSTICS_OVERLAY_FPS_NETWORK) {
    snprintf(line, sizeof(line), "Network  %s   %u ms   %u kbps",
             network_state_name(snapshot.network_state),
             snapshot.estimated_rtt_ms,
             snapshot.measured_video_kbps);
    draw_overlay_text(x + 10, y + 45, line);
  }

  if (advanced) {
    snprintf(line, sizeof(line), "Active  %dx%d @ %d  %u kbps",
             snapshot.stream_width, snapshot.stream_height,
             snapshot.stream_fps, snapshot.configured_bitrate_kbps);
    draw_overlay_text(x + 10, y + 68, line);
    snprintf(line, sizeof(line), "Decode  avg %.2f ms  max %.2f ms",
             snapshot.average_decode_us / 1000.0f,
             snapshot.maximum_decode_us / 1000.0f);
    draw_overlay_text(x + 10, y + 91, line);
    snprintf(line, sizeof(line), "Drops  %u/s  total %u",
             snapshot.dropped_frames_in_window,
             snapshot.total_dropped_frames);
    draw_overlay_text(x + 10, y + 114, line);
    snprintf(line, sizeof(line), "Packets  recovered %u  failed %u  OOS %u",
             snapshot.recovered_packets_in_window,
             snapshot.failed_fec_packets_in_window,
             snapshot.out_of_sequence_packets_in_window);
    draw_overlay_text(x + 10, y + 137, line);
    if (snapshot.stream_settings_pending) {
      if (snapshot.stream_format_settings_pending) {
        snprintf(
            line, sizeof(line),
            snapshot.stream_tuning_settings_pending
                ? "Next  %dx%d @ %d  %u kbps + stream options"
                : "Next reconnect  %dx%d @ %d  %u kbps",
            snapshot.selected_stream_width,
            snapshot.selected_stream_height,
            snapshot.selected_stream_fps,
            snapshot.selected_bitrate_kbps);
      } else {
        snprintf(line, sizeof(line),
                 "Next reconnect  stream options changed");
      }
      draw_overlay_text(x + 10, y + 160, line);
    }
  }
}

bool ui_diagnostics_screen_is_open(void) {
  return atomic_load_u32(&diagnostics_screen_open) != 0;
}

void ui_diagnostics_screen_open(void) {
  set_metrics_consumer(UI_DIAGNOSTICS_CONSUMER_SCREEN, true);
  atomic_store_u32(&diagnostics_screen_open, 1);
}

void ui_diagnostics_screen_close(void) {
  atomic_store_u32(&diagnostics_screen_open, 0);
  set_metrics_consumer(UI_DIAGNOSTICS_CONSUMER_SCREEN, false);
}

static bool pressed(const SceCtrlData *pad,
                    const SceCtrlData *previous,
                    unsigned int button) {
  return pad && previous &&
         (pad->buttons & button) &&
         !(previous->buttons & button);
}

void ui_diagnostics_screen_handle_input(const SceCtrlData *pad,
                                        const SceCtrlData *previous) {
  if (!ui_diagnostics_screen_is_open()) return;
  if (pressed(pad, previous, SCE_CTRL_TRIANGLE)) {
    bool enabled = !vita_debug_is_logging_enabled();
    vita_debug_set_logging_enabled(enabled);
    if (config_path) config_save(config_path, &config);
    if (enabled) {
      vita_debug_log("[DIAGNOSTICS] Optional file logging enabled by user");
    }
    return;
  }
  if (pressed(pad, previous, config.btn_cancel) ||
      pressed(pad, previous, SCE_CTRL_START)) {
    ui_diagnostics_screen_close();
  }
}

static void draw_screen_row(int y, const char *label, const char *value,
                            unsigned int value_color) {
  vita2d_font_draw_text(font, 196, y, RGBA8(170, 184, 207, 255), 16, label);
  int value_width = vita2d_font_text_width(font, 16, value);
  vita2d_font_draw_text(font, 764 - value_width, y, value_color, 16, value);
}

void ui_diagnostics_screen_draw(void) {
  if (!ui_diagnostics_screen_is_open()) return;

  UiDiagnosticsSnapshot snapshot;
  ui_diagnostics_get_snapshot(&snapshot);

  char value[96];
  vita2d_draw_rectangle(0, 0, WIDTH, HEIGHT, RGBA8(4, 8, 16, 210));
  vita2d_draw_rectangle(166, 38, 628, 468, RGBA8(20, 28, 44, 248));
  vita2d_draw_rectangle(166, 38, 6, 468, RGBA8(47, 111, 237, 255));
  vita2d_font_draw_text(font, 196, 75, RGBA8(255, 255, 255, 255), 27,
                        "Real-time diagnostics");
  vita2d_font_draw_text(font, 196, 98, RGBA8(166, 181, 208, 255), 15,
                        "Live stream, decoder, network, and input health");

  snprintf(value, sizeof(value), "%s",
           snapshot.stream_connected ? "Connected" : "Not connected");
  draw_screen_row(128, "Session", value,
                  snapshot.stream_connected
                      ? RGBA8(116, 230, 160, 255)
                      : RGBA8(255, 194, 112, 255));

  snprintf(value, sizeof(value), "%u / %u", snapshot.rendered_fps,
           snapshot.target_fps);
  draw_screen_row(151, "Rendered FPS", value, RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%s, %u +/- %u ms",
           network_state_name(snapshot.network_state),
           snapshot.estimated_rtt_ms,
           snapshot.estimated_rtt_variance_ms);
  draw_screen_row(174, "Network state", value,
                  snapshot.network_state == UI_DIAGNOSTICS_NETWORK_DEGRADED
                      ? RGBA8(255, 154, 132, 255)
                      : RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%u / %u kbps",
           snapshot.measured_video_kbps,
           snapshot.configured_bitrate_kbps);
  draw_screen_row(197, "Video rate (measured / active)", value,
                  RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%dx%d @ %d, %u kbps, packet %d",
           snapshot.stream_width, snapshot.stream_height,
           snapshot.stream_fps, snapshot.configured_bitrate_kbps,
           snapshot.packet_size);
  draw_screen_row(220, "Active stream", value,
                   RGBA8(235, 240, 250, 255));

  if (snapshot.stream_format_settings_pending) {
    snprintf(
        value, sizeof(value), "%dx%d @ %d, %u kbps, packet %d%s",
        snapshot.selected_stream_width,
        snapshot.selected_stream_height,
        snapshot.selected_stream_fps,
        snapshot.selected_bitrate_kbps,
        snapshot.selected_packet_size,
        snapshot.stream_tuning_settings_pending ? " + options" : "");
  } else if (snapshot.stream_tuning_settings_pending) {
    snprintf(value, sizeof(value), "Stream options changed; format unchanged");
  } else {
    snprintf(value, sizeof(value), "Same as active");
  }
  draw_screen_row(
      243, "Next reconnect", value,
      snapshot.stream_settings_pending
          ? RGBA8(255, 194, 112, 255)
          : RGBA8(170, 184, 207, 255));

  snprintf(value, sizeof(value), "%.2f / %.2f ms",
           snapshot.average_decode_us / 1000.0f,
           snapshot.maximum_decode_us / 1000.0f);
  draw_screen_row(266, "Decode time (avg / max)", value,
                   RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%u current, %u total",
           snapshot.dropped_frames_in_window,
           snapshot.total_dropped_frames);
  draw_screen_row(289, "Dropped frames", value,
                   snapshot.dropped_frames_in_window
                       ? RGBA8(255, 194, 112, 255)
                       : RGBA8(235, 240, 250, 255));

  const char *active_controller =
      snapshot.controller_type == 2 ? "DualShock 4" : "Xbox";
  const char *selected_controller =
      snapshot.selected_controller_type == 2 ? "DualShock 4" : "Xbox";
  if (snapshot.controller_capability_settings_pending) {
    snprintf(
        value, sizeof(value), "%s; next %s, gyro %s%s",
        active_controller, selected_controller,
        snapshot.selected_motion_enabled ? "on" : "off",
        snapshot.input_behavior_settings_pending ? " + input options" : "");
  } else if (snapshot.input_behavior_settings_pending) {
    snprintf(value, sizeof(value), "%s; input options changed",
             active_controller);
  } else {
    snprintf(value, sizeof(value), "%s", active_controller);
  }
  draw_screen_row(312, "Controller profile", value,
                   snapshot.controller_settings_pending
                       ? RGBA8(255, 194, 112, 255)
                       : RGBA8(235, 240, 250, 255));

  if (!snapshot.motion_enabled || snapshot.controller_type != 2) {
    snprintf(value, sizeof(value), "Disabled");
  } else if (snapshot.motion_sensor_error < 0) {
    snprintf(value, sizeof(value), "Sensor error %08X",
             (unsigned int)snapshot.motion_sensor_error);
  } else if (!snapshot.gyro_requested) {
    snprintf(value, sizeof(value), "Awaiting host request");
  } else {
    snprintf(value, sizeof(value), "%u Hz, %u events",
             (unsigned int)snapshot.gyro_report_rate,
             snapshot.gyro_events_sent);
  }
  draw_screen_row(335, "Gyroscope", value,
                   snapshot.motion_sensor_error < 0
                       ? RGBA8(255, 154, 132, 255)
                       : RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%s",
           snapshot.file_logging_enabled ? "Enabled" : "Disabled");
  draw_screen_row(358, "Diagnostic log", value,
                   snapshot.file_logging_enabled
                       ? RGBA8(116, 230, 160, 255)
                       : RGBA8(235, 240, 250, 255));

  snprintf(value, sizeof(value), "%s",
           ui_diagnostics_overlay_mode_name(
               ui_diagnostics_get_overlay_mode()));
  draw_screen_row(381, "Performance overlay", value,
                   RGBA8(235, 240, 250, 255));

  char log_path[96] = "Unavailable";
  vita_debug_get_log_path(log_path, sizeof(log_path));
  draw_screen_row(404, "Log file", log_path,
                  RGBA8(235, 240, 250, 255));

  vita2d_font_draw_text(
      font, 196, 467, RGBA8(166, 181, 208, 255), 15,
      "Triangle: file logging on/off   Cancel / START: back");
}
