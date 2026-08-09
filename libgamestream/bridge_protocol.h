#pragma once

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define VITA_STREAM_BOUNDARY_PROTOCOL "vita-moonlight-stream-boundary/1"
#define VITA_STREAM_BOUNDARY_PREPARE_PATH "/v1/prepare"
#define VITA_STREAM_BOUNDARY_STARTED_PATH "/v1/started"
#define VITA_STREAM_BOUNDARY_HEARTBEAT_PATH "/v1/heartbeat"
#define VITA_STREAM_BOUNDARY_STOP_PATH "/v1/stop"
#define VITA_STREAM_BOUNDARY_PORT_OFFSET 23u
#define VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS 32u
#define VITA_STREAM_BOUNDARY_GENERATION_CAPACITY \
  (VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS + 1u)
#define VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES 256u
#define VITA_STREAM_BOUNDARY_HEARTBEAT_INTERVAL_MS 10000u
#define VITA_STREAM_BOUNDARY_HEARTBEAT_TIMEOUT_MS 3000L

typedef enum _STREAM_BOUNDARY_TRANSPORT_FAILURE {
  STREAM_BOUNDARY_TRANSPORT_REFUSED = 1,
  STREAM_BOUNDARY_TRANSPORT_TIMEOUT = 2,
  STREAM_BOUNDARY_TRANSPORT_OTHER = 3
} STREAM_BOUNDARY_TRANSPORT_FAILURE;

bool stream_boundary_port(
    uint16_t sunshine_http_port, uint16_t *bridge_port);
bool stream_boundary_parse_prepared(
    const char *body, size_t body_size,
    char generation[VITA_STREAM_BOUNDARY_GENERATION_CAPACITY]);
bool stream_boundary_parse_started(const char *body, size_t body_size);
bool stream_boundary_parse_heartbeat(const char *body, size_t body_size);
bool stream_boundary_parse_stopped(const char *body, size_t body_size);
bool stream_boundary_transport_failure_is_optional(
    STREAM_BOUNDARY_TRANSPORT_FAILURE failure, bool tls_established);
