#include "bridge_protocol.h"

#include <assert.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

int main(void) {
  uint16_t port = 0;
  assert(stream_boundary_port(47989, &port));
  assert(port == 48012);
  assert(!stream_boundary_port(0, &port));
  assert(!stream_boundary_port(65520, &port));
  assert(!stream_boundary_port(47989, NULL));

  static const char prepared[] =
      VITA_STREAM_BOUNDARY_PROTOCOL
      " prepared generation=0123456789abcdef0123456789abcdef\n";
  char generation[VITA_STREAM_BOUNDARY_GENERATION_CAPACITY];
  assert(stream_boundary_parse_prepared(
      prepared, sizeof(prepared) - 1u, generation));
  assert(strcmp(generation, "0123456789abcdef0123456789abcdef") == 0);

  char uppercase[sizeof(prepared)];
  memcpy(uppercase, prepared, sizeof(prepared));
  uppercase[sizeof(VITA_STREAM_BOUNDARY_PROTOCOL
                   " prepared generation=") - 1u] = 'A';
  assert(!stream_boundary_parse_prepared(
      uppercase, sizeof(uppercase) - 1u, generation));
  assert(generation[0] == '\0');
  assert(!stream_boundary_parse_prepared(
      prepared, sizeof(prepared) - 2u, generation));
  assert(!stream_boundary_parse_prepared(
      prepared, sizeof(prepared), generation));

  static const char stopped[] =
      VITA_STREAM_BOUNDARY_PROTOCOL " stopped\n";
  static const char started[] =
      VITA_STREAM_BOUNDARY_PROTOCOL " started\n";
  static const char heartbeat[] =
      VITA_STREAM_BOUNDARY_PROTOCOL " heartbeat\n";
  assert(stream_boundary_parse_started(started, sizeof(started) - 1u));
  assert(!stream_boundary_parse_started(started, sizeof(started)));
  assert(stream_boundary_parse_heartbeat(
      heartbeat, sizeof(heartbeat) - 1u));
  assert(!stream_boundary_parse_heartbeat(
      heartbeat, sizeof(heartbeat)));
  assert(stream_boundary_parse_stopped(
      stopped, sizeof(stopped) - 1u));
  assert(!stream_boundary_parse_stopped(stopped, sizeof(stopped)));
  assert(!stream_boundary_parse_stopped("stopped\n", 8u));

  assert(stream_boundary_transport_failure_is_optional(
      STREAM_BOUNDARY_TRANSPORT_REFUSED, false));
  assert(stream_boundary_transport_failure_is_optional(
      STREAM_BOUNDARY_TRANSPORT_TIMEOUT, false));
  assert(!stream_boundary_transport_failure_is_optional(
      STREAM_BOUNDARY_TRANSPORT_TIMEOUT, true));
  assert(!stream_boundary_transport_failure_is_optional(
      STREAM_BOUNDARY_TRANSPORT_OTHER, false));

  puts("stream-boundary protocol tests passed");
  return 0;
}
