/*
 * SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Vita Moonlight contributors
 */

#ifndef VITA_MOONLIGHT_MDNS_PARSER_H
#define VITA_MOONLIGHT_MDNS_PARSER_H

#include <stddef.h>
#include <stdint.h>

#define MDNS_DISCOVERY_NAME_CAPACITY 256
#define MDNS_DISCOVERY_IPV4_CAPACITY 16
#define MDNS_DISCOVERY_MAX_SERVICES 16
#define MDNS_DISCOVERY_MAX_ADDRESSES 16

typedef struct mdns_discovery_service {
  int occupied;
  char instance[MDNS_DISCOVERY_NAME_CAPACITY];
  char target[MDNS_DISCOVERY_NAME_CAPACITY];
  char ipv4[MDNS_DISCOVERY_IPV4_CAPACITY];
  char reported_ipv4[MDNS_DISCOVERY_IPV4_CAPACITY];
  uint16_t port;
  uint16_t reported_port;
  uint32_t last_seen_ms;
} mdns_discovery_service_t;

typedef struct mdns_discovery_address {
  int occupied;
  char target[MDNS_DISCOVERY_NAME_CAPACITY];
  char ipv4[MDNS_DISCOVERY_IPV4_CAPACITY];
  uint32_t last_seen_ms;
} mdns_discovery_address_t;

typedef struct mdns_discovery_state {
  mdns_discovery_service_t services[MDNS_DISCOVERY_MAX_SERVICES];
  mdns_discovery_address_t addresses[MDNS_DISCOVERY_MAX_ADDRESSES];
} mdns_discovery_state_t;

typedef void (*mdns_discovery_result_cb)(
    void *context,
    const char *host,
    const char *computer_name,
    const char *ipv4,
    uint16_t port);

void mdns_discovery_state_init(mdns_discovery_state_t *state);
void mdns_discovery_expire(mdns_discovery_state_t *state, uint32_t now_ms);

/*
 * Parses one complete DNS message transactionally. A malformed message
 * returns -1 without changing state or invoking the callback. A valid message
 * returns the number of new or changed complete services it emitted.
 */
int mdns_discovery_parse_packet(
    mdns_discovery_state_t *state,
    const uint8_t *packet,
    size_t packet_size,
    uint32_t now_ms,
    mdns_discovery_result_cb callback,
    void *callback_context);

#endif
