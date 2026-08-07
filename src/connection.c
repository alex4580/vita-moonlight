
/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015 Iwan Timmer
 *
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * Moonlight is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 * GNU General Public License for more details.
 *
 * You should have received a copy of the GNU General Public License
 * along with Moonlight; if not, see <http://www.gnu.org/licenses/>.
 */

#include "connection.h"
#include "Limelight.h"
#include "config.h"
#include "power/vita.h"
#include "input/vita.h"
#include "input/motion.h"
#include "video/vita.h"
#include "audio/vita.h"
#include <stdbool.h>
#include <stdint.h>
#include <pthread.h>
#include "connection_overlay.h"
#include "debug.h"
#include "gui/ui_stream_overlay.h"
#include "gui/ui_diagnostics.h"

static int connection_status = LI_DISCONNECTED;
static uint32_t termination_in_progress = 0;
static pthread_mutex_t lifecycle_mutex = PTHREAD_MUTEX_INITIALIZER;

int connection_stage = 0;

static int connection_state_load(void) {
  return __atomic_load_n(&connection_status, __ATOMIC_ACQUIRE);
}

static bool begin_termination(void) {
  return __atomic_exchange_n(
      &termination_in_progress, 1U, __ATOMIC_ACQ_REL) == 0;
}

static void end_termination(void) {
  __atomic_store_n(&termination_in_progress, 0U, __ATOMIC_RELEASE);
}

static const char *connection_state_token(int state) {
  switch (state) {
    case LI_READY:
      return "ready";
    case LI_PAIRED:
      return "paired";
    case LI_CONNECTED:
      return "connected";
    case LI_MINIMIZED:
      return "minimized";
    default:
      return "disconnected";
  }
}

static void log_invalid_transition(const char *operation) {
  int state = connection_state_load();
  vita_debug_event(
      VITA_DEBUG_LEVEL_ERROR, "connection.state",
      "previous=%s state=%s reason=invalid_%s code=-1",
      connection_state_token(state),
      connection_state_token(state), operation);
}

static void set_connection_state(int next, const char *reason,
                                 VitaDebugLevel level, int code) {
  int previous = __atomic_exchange_n(
      &connection_status, next, __ATOMIC_ACQ_REL);
  vita_debug_event(
      level, "connection.state",
      "previous=%s state=%s reason=%s code=%d",
      connection_state_token(previous), connection_state_token(next),
      reason, code);
}

bool pause_overlay_is_open(void) {
    return stream_overlay_is_open();
}

void pause_output() {
  vitainput_stop();
  vitavideo_stop();
  vitaaudio_stop();
}

void stop_output() {
  vitainput_stop();
  vitapower_stop();
  vitavideo_stop();
  vitaaudio_stop();
}

void start_output() {
  vitainput_start();
  vitapower_start();
  vitavideo_start();
  vitaaudio_start();
}

void connection_connection_started() {
  pthread_mutex_lock(&lifecycle_mutex);
  if (__atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE) ||
      connection_state_load() != LI_PAIRED) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("stream_started");
    return;
  }
  set_connection_state(
      LI_CONNECTED, "stream_started", VITA_DEBUG_LEVEL_INFO, 0);
  stream_overlay_reset();
  ui_diagnostics_reset_session();
  ui_diagnostics_set_network_state(UI_DIAGNOSTICS_NETWORK_GOOD);
  start_output();
  vitavideo_hide_poor_net_indicator();
  pthread_mutex_unlock(&lifecycle_mutex);
}

static void connection_connection_terminated_internal(int error_code,
                                                       bool requested) {
  if (!begin_termination()) return;

  pthread_mutex_lock(&lifecycle_mutex);
  int state = connection_state_load();
  if (state == LI_DISCONNECTED) {
    pthread_mutex_unlock(&lifecycle_mutex);
    end_termination();
    return;
  }
  if (state != LI_PAIRED && state != LI_CONNECTED &&
      state != LI_MINIMIZED) {
    log_invalid_transition("terminate_callback");
  }

  const char *reason =
      requested ? "requested" : (error_code == 0 ? "graceful" : "error");
  bool graceful = requested || error_code == 0 ||
                  error_code == ML_ERROR_GRACEFUL_TERMINATION;
  VitaDebugLevel level = graceful
      ? VITA_DEBUG_LEVEL_INFO
      : VITA_DEBUG_LEVEL_ERROR;
  if (!requested) {
    switch (error_code) {
      case ML_ERROR_GRACEFUL_TERMINATION:
        break;
      case ML_ERROR_NO_VIDEO_TRAFFIC:
        reason = "no_video_traffic";
        break;
      case ML_ERROR_NO_VIDEO_FRAME:
        reason = "no_video_frame";
        break;
      case ML_ERROR_UNEXPECTED_EARLY_TERMINATION:
        reason = "unexpected_early_termination";
        break;
      case ML_ERROR_PROTECTED_CONTENT:
        reason = "protected_content";
        break;
      default:
        break;
    }
  }

  if (state == LI_CONNECTED) {
    stop_output();
  } else if (state == LI_MINIMIZED) {
    /* pause_output() already stopped input/video/audio. Power inhibition is
     * intentionally retained for resume, but must end during teardown. */
    vitapower_stop();
  }
  set_connection_state(LI_DISCONNECTED, reason, level, error_code);
  pthread_mutex_unlock(&lifecycle_mutex);

  /* Do not hold the lifecycle mutex here. Moonlight may invoke the
   * termination callback reentrantly; the gate above makes that a no-op. */
  LiStopConnection();
  vita_debug_flush();
  stream_overlay_reset();
  ui_diagnostics_reset_session();
  ui_diagnostics_set_network_state(UI_DIAGNOSTICS_NETWORK_UNKNOWN);
  end_termination();
}

static void connection_connection_terminated(int error_code) {
  connection_connection_terminated_internal(error_code, false);
}

int connection_reset() {
  pthread_mutex_lock(&lifecycle_mutex);
  if (connection_state_load() != LI_DISCONNECTED ||
      __atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE)) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("reset");
    return -1;
  }
  set_connection_state(
      LI_READY, "attempt_begin", VITA_DEBUG_LEVEL_INFO, 0);
  pthread_mutex_unlock(&lifecycle_mutex);
  return 0;
}

int connection_abort_attempt() {
  pthread_mutex_lock(&lifecycle_mutex);
  if (connection_state_load() != LI_READY ||
      __atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE)) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("abort_attempt");
    return -1;
  }

  connection_stage = 0;
  set_connection_state(
      LI_DISCONNECTED, "attempt_aborted", VITA_DEBUG_LEVEL_INFO, 0);
  pthread_mutex_unlock(&lifecycle_mutex);
  vita_debug_flush();
  return 0;
}

int connection_paired() {
  pthread_mutex_lock(&lifecycle_mutex);
  int state = connection_state_load();
  if (__atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE) ||
      (state != LI_READY && state != LI_PAIRED &&
       state != LI_CONNECTED)) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("paired");
    return -1;
  }
  if (state != LI_PAIRED) {
    set_connection_state(
        LI_PAIRED, "pairing_ready", VITA_DEBUG_LEVEL_INFO, 0);
  }
  pthread_mutex_unlock(&lifecycle_mutex);
  return 0;
}

int connection_minimize() {
  pthread_mutex_lock(&lifecycle_mutex);
  if (connection_state_load() != LI_CONNECTED ||
      __atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE)) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("minimize");
    return -1;
  }
  pause_output();
  set_connection_state(
      LI_MINIMIZED, "output_paused", VITA_DEBUG_LEVEL_INFO, 0);
  pthread_mutex_unlock(&lifecycle_mutex);
  return 0;
}

int connection_resume() {
  pthread_mutex_lock(&lifecycle_mutex);
  if (connection_state_load() != LI_MINIMIZED ||
      __atomic_load_n(&termination_in_progress, __ATOMIC_ACQUIRE)) {
    pthread_mutex_unlock(&lifecycle_mutex);
    log_invalid_transition("resume");
    return -1;
  }
  start_output();
  set_connection_state(
      LI_CONNECTED, "output_resumed", VITA_DEBUG_LEVEL_INFO, 0);
  pthread_mutex_unlock(&lifecycle_mutex);
  return 0;
}

int connection_terminate() {
  int state = connection_state_load();
  if (state != LI_PAIRED && state != LI_CONNECTED &&
      state != LI_MINIMIZED) {
    log_invalid_transition("terminate_request");
    return -1;
  }
  connection_connection_terminated_internal(0, true);
  return 0;
}

void connection_stage_starting(int stage) {
  connection_stage = stage;
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "connection.stage",
      "stage_id=%d state=starting", stage);
}
void connection_stage_complate(int stage) {
  connection_stage = stage;
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "connection.stage",
      "stage_id=%d state=complete", stage);
}

void connection_stage_failed(int stage, int code) {
  connection_stage = stage;
  vita_debug_event(
      VITA_DEBUG_LEVEL_ERROR, "connection.stage",
      "stage_id=%d state=failed code=%d", stage, code);
}

bool connection_is_ready() {
  return connection_state_load() != LI_DISCONNECTED;
}

bool connection_is_connected() {
  return connection_state_load() == LI_CONNECTED;
}

int connection_get_status() {
  return connection_state_load();
}

void connection_status_update(int status) {
  int state = connection_state_load();
  if (state != LI_CONNECTED && state != LI_MINIMIZED) return;
  switch (status) {
    case CONN_STATUS_POOR:
      vitavideo_show_poor_net_indicator();
      ui_diagnostics_set_network_state(UI_DIAGNOSTICS_NETWORK_DEGRADED);
      break;
    case CONN_STATUS_OKAY:
      vitavideo_hide_poor_net_indicator();
      ui_diagnostics_set_network_state(UI_DIAGNOSTICS_NETWORK_GOOD);
      break;
  }
}

void connection_set_motion_state(uint16_t controller, uint8_t motion_type, uint16_t report_rate) {
  (void)controller;

  //TODO: Multicontroller support here someday? Can't afford pstv tho
  if (!config.enable_motion_controls || config.controller_type != 2) {
    vita_debug_event(
        VITA_DEBUG_LEVEL_INFO, "motion.state",
        "state=ignored reason=profile_disabled sensor_type=%u",
        (unsigned int)motion_type);
    return;
  }

  vita_motion_set_state(motion_type, report_rate);
  vita_debug_event(
      VITA_DEBUG_LEVEL_INFO, "motion.state",
      "state=%s sensor_type=%u report_hz=%u",
      report_rate == 0 ? "disabled" : "enabled",
      (unsigned int)motion_type,
      (unsigned int)(report_rate == 0
          ? 0
          : vita_motion_clamp_report_rate(report_rate)));
}

CONNECTION_LISTENER_CALLBACKS connection_callbacks = {
  .stageStarting = connection_stage_starting,
  .stageComplete = connection_stage_complate,
  .stageFailed = connection_stage_failed,
  .connectionStarted = connection_connection_started,
  .connectionTerminated = connection_connection_terminated,
  .connectionStatusUpdate = connection_status_update,
  .logMessage = vita_debug_log,
  .setMotionEventState = connection_set_motion_state
};
