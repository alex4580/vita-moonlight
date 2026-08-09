#include "bridge_protocol.h"

#include <stdio.h>
#include <string.h>

bool stream_boundary_port(
    uint16_t sunshine_http_port, uint16_t *bridge_port) {
  if (bridge_port == NULL || sunshine_http_port == 0 ||
      sunshine_http_port > UINT16_MAX - VITA_STREAM_BOUNDARY_PORT_OFFSET) {
    return false;
  }
  *bridge_port =
      (uint16_t)(sunshine_http_port + VITA_STREAM_BOUNDARY_PORT_OFFSET);
  return true;
}

static bool is_lower_hex_generation(const char *generation) {
  for (size_t index = 0;
       index < VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS; ++index) {
    char value = generation[index];
    if (!((value >= '0' && value <= '9') ||
          (value >= 'a' && value <= 'f'))) {
      return false;
    }
  }
  return generation[VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS] == '\0';
}

bool stream_boundary_parse_prepared(
    const char *body, size_t body_size,
    char generation[VITA_STREAM_BOUNDARY_GENERATION_CAPACITY]) {
  static const char prefix[] =
      VITA_STREAM_BOUNDARY_PROTOCOL " prepared generation=";
  const size_t prefix_size = sizeof(prefix) - 1u;
  const size_t expected_size =
      prefix_size + VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS + 1u;
  if (body == NULL || generation == NULL || body_size != expected_size ||
      body_size > VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES ||
      memcmp(body, prefix, prefix_size) != 0 ||
      body[body_size - 1u] != '\n') {
    return false;
  }
  memcpy(
      generation, body + prefix_size,
      VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS);
  generation[VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS] = '\0';
  if (!is_lower_hex_generation(generation)) {
    generation[0] = '\0';
    return false;
  }
  return true;
}

bool stream_boundary_parse_stopped(const char *body, size_t body_size) {
  static const char expected[] =
      VITA_STREAM_BOUNDARY_PROTOCOL " stopped\n";
  return body != NULL && body_size == sizeof(expected) - 1u &&
      body_size <= VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES &&
      memcmp(body, expected, sizeof(expected) - 1u) == 0;
}

static bool stream_boundary_parse_exact_action(
    const char *body, size_t body_size, const char *action) {
  char expected[VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES];
  int written = snprintf(
      expected, sizeof(expected), "%s %s\n",
      VITA_STREAM_BOUNDARY_PROTOCOL, action);
  return body != NULL && written > 0 &&
      (size_t)written < sizeof(expected) &&
      body_size == (size_t)written &&
      memcmp(body, expected, body_size) == 0;
}

bool stream_boundary_parse_started(const char *body, size_t body_size) {
  return stream_boundary_parse_exact_action(body, body_size, "started");
}

bool stream_boundary_parse_heartbeat(const char *body, size_t body_size) {
  return stream_boundary_parse_exact_action(body, body_size, "heartbeat");
}

bool stream_boundary_transport_failure_is_optional(
    STREAM_BOUNDARY_TRANSPORT_FAILURE failure, bool tls_established) {
  return !tls_established &&
      (failure == STREAM_BOUNDARY_TRANSPORT_REFUSED ||
       failure == STREAM_BOUNDARY_TRANSPORT_TIMEOUT);
}
