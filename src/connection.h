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

#include <stdbool.h>
#include <Limelight.h>

//.DISCONNECTED <-.
//      |         |
//      v         |
//    READY       |
//      |         |
//      v         |
//    PAIRED   ---|
//     | ^        |
//     v |        |
//  CONNECTED  ---|
//     | ^        |
//     v |        |
//  MINIMISED  ---'

enum {
  LI_DISCONNECTED,
  LI_READY,
  LI_PAIRED,
  LI_CONNECTED,
  LI_MINIMIZED
};

extern CONNECTION_LISTENER_CALLBACKS connection_callbacks;

extern int connection_stage;

int connection_reset();
/* Cancel host setup before a stream exists. This is the only legal
 * LI_READY -> LI_DISCONNECTED transition and never calls LiStopConnection(). */
int connection_abort_attempt();
int connection_paired();
int connection_minimize();
int connection_resume();
/* Request media teardown, or join an asynchronous Moonlight teardown already
 * in progress. Success means every Moonlight media/renderer worker is gone. */
int connection_terminate();
/* Await an already-requested teardown without starting one. This is the
 * barrier that must precede host HTTP/CURL cleanup. */
int connection_wait_for_termination();

bool connection_is_ready();
bool connection_is_connected();
bool connection_is_terminating();
int connection_get_status();
