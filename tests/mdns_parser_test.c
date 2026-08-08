/*
 * SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Vita Moonlight contributors
 */

#include "mdns_parser.h"

#include <assert.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>

typedef struct packet_writer {
  uint8_t bytes[1024];
  size_t length;
} packet_writer_t;

typedef struct captured_result {
  int count;
  char host[MDNS_DISCOVERY_NAME_CAPACITY];
  char computer_name[MDNS_DISCOVERY_NAME_CAPACITY];
  char ipv4[MDNS_DISCOVERY_IPV4_CAPACITY];
  uint16_t port;
} captured_result_t;

static void put_u8(packet_writer_t *writer, uint8_t value) {
  assert(writer->length < sizeof(writer->bytes));
  writer->bytes[writer->length++] = value;
}

static void put_u16(packet_writer_t *writer, uint16_t value) {
  put_u8(writer, (uint8_t)(value >> 8));
  put_u8(writer, (uint8_t)value);
}

static void put_u32(packet_writer_t *writer, uint32_t value) {
  put_u8(writer, (uint8_t)(value >> 24));
  put_u8(writer, (uint8_t)(value >> 16));
  put_u8(writer, (uint8_t)(value >> 8));
  put_u8(writer, (uint8_t)value);
}

static void patch_u16(packet_writer_t *writer, size_t offset, uint16_t value) {
  assert(offset + 1 < writer->length);
  writer->bytes[offset] = (uint8_t)(value >> 8);
  writer->bytes[offset + 1] = (uint8_t)value;
}

static size_t put_name(packet_writer_t *writer, const char *name) {
  size_t start = writer->length;
  const char *label = name;
  while (*label != '\0') {
    const char *dot = strchr(label, '.');
    size_t length = dot == NULL ? strlen(label) : (size_t)(dot - label);
    assert(length > 0 && length <= 63);
    put_u8(writer, (uint8_t)length);
    for (size_t i = 0; i < length; i++) put_u8(writer, (uint8_t)label[i]);
    if (dot == NULL) break;
    label = dot + 1;
  }
  put_u8(writer, 0);
  return start;
}

static void put_pointer(packet_writer_t *writer, size_t offset) {
  assert(offset < 0x4000u);
  put_u8(writer, (uint8_t)(0xc0u | (offset >> 8)));
  put_u8(writer, (uint8_t)offset);
}

static void begin_response(packet_writer_t *writer, uint16_t answers) {
  memset(writer, 0, sizeof(*writer));
  put_u16(writer, 0);
  put_u16(writer, 0x8400);
  put_u16(writer, 0);
  put_u16(writer, answers);
  put_u16(writer, 0);
  put_u16(writer, 0);
}

static size_t add_a_record_with_ttl(
    packet_writer_t *writer,
    const char *owner,
    uint8_t a,
    uint8_t b,
    uint8_t c,
    uint8_t d,
    uint32_t ttl) {
  size_t owner_offset = put_name(writer, owner);
  put_u16(writer, 1);
  put_u16(writer, 1);
  put_u32(writer, ttl);
  put_u16(writer, 4);
  put_u8(writer, a);
  put_u8(writer, b);
  put_u8(writer, c);
  put_u8(writer, d);
  return owner_offset;
}

static size_t add_a_record(
    packet_writer_t *writer,
    const char *owner,
    uint8_t a,
    uint8_t b,
    uint8_t c,
    uint8_t d) {
  return add_a_record_with_ttl(writer, owner, a, b, c, d, 120);
}

static void build_complete_response(packet_writer_t *writer) {
  size_t service_offset;
  size_t instance_offset;
  size_t target_offset;
  size_t length_offset;
  size_t data_offset;

  begin_response(writer, 3);

  service_offset = put_name(writer, "_nvstream._tcp.local");
  put_u16(writer, 12);
  put_u16(writer, 1);
  put_u32(writer, 120);
  length_offset = writer->length;
  put_u16(writer, 0);
  data_offset = writer->length;
  instance_offset = writer->length;
  put_u8(writer, 9);
  for (const char *p = "Gaming-PC"; *p != '\0'; p++) put_u8(writer, (uint8_t)*p);
  put_pointer(writer, service_offset);
  patch_u16(writer, length_offset, (uint16_t)(writer->length - data_offset));

  put_pointer(writer, instance_offset);
  put_u16(writer, 33);
  put_u16(writer, 1);
  put_u32(writer, 120);
  length_offset = writer->length;
  put_u16(writer, 0);
  data_offset = writer->length;
  put_u16(writer, 0);
  put_u16(writer, 0);
  put_u16(writer, 47989);
  target_offset = put_name(writer, "Gaming-PC.local");
  patch_u16(writer, length_offset, (uint16_t)(writer->length - data_offset));

  put_pointer(writer, target_offset);
  put_u16(writer, 1);
  put_u16(writer, 0x8001);
  put_u32(writer, 120);
  put_u16(writer, 4);
  put_u8(writer, 192);
  put_u8(writer, 168);
  put_u8(writer, 50);
  put_u8(writer, 20);
}

static void build_srv_response(
    packet_writer_t *writer,
    const char *instance,
    const char *target,
    uint16_t port) {
  size_t length_offset;
  size_t data_offset;
  begin_response(writer, 1);
  put_name(writer, instance);
  put_u16(writer, 33);
  put_u16(writer, 1);
  put_u32(writer, 120);
  length_offset = writer->length;
  put_u16(writer, 0);
  data_offset = writer->length;
  put_u16(writer, 0);
  put_u16(writer, 0);
  put_u16(writer, port);
  put_name(writer, target);
  patch_u16(writer, length_offset, (uint16_t)(writer->length - data_offset));
}

static void build_srv_only(packet_writer_t *writer) {
  build_srv_response(
      writer, "Gaming-PC._nvstream._tcp.local",
      "Gaming-PC.local", 47989);
}

static void capture(
    void *context,
    const char *host,
    const char *computer_name,
    const char *ipv4,
    uint16_t port) {
  captured_result_t *result = (captured_result_t *)context;
  result->count++;
  snprintf(result->host, sizeof(result->host), "%s", host);
  snprintf(result->computer_name, sizeof(result->computer_name),
           "%s", computer_name);
  snprintf(result->ipv4, sizeof(result->ipv4), "%s", ipv4);
  result->port = port;
}

static void test_complete_compressed_response(void) {
  packet_writer_t packet;
  mdns_discovery_state_t state;
  captured_result_t result = {0};
  build_complete_response(&packet);
  mdns_discovery_state_init(&state);

  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 1000,
             capture, &result) == 1);
  assert(result.count == 1);
  assert(strcmp(result.host, "Gaming-PC.local") == 0);
  assert(strcmp(result.computer_name, "Gaming-PC") == 0);
  assert(strcmp(result.ipv4, "192.168.50.20") == 0);
  assert(result.port == 47989);

  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 1100,
             capture, &result) == 0);
  assert(result.count == 1);
}

static void test_cross_packet_correlation_and_change(void) {
  packet_writer_t address;
  packet_writer_t service;
  mdns_discovery_state_t state;
  captured_result_t result = {0};
  begin_response(&address, 1);
  add_a_record(&address, "Gaming-PC.local", 10, 0, 0, 8);
  build_srv_only(&service);
  mdns_discovery_state_init(&state);

  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 2000,
             capture, &result) == 0);
  assert(mdns_discovery_parse_packet(
             &state, service.bytes, service.length, 2100,
             capture, &result) == 1);
  assert(strcmp(result.ipv4, "10.0.0.8") == 0);
  assert(strcmp(result.computer_name, "Gaming-PC") == 0);

  begin_response(&address, 1);
  add_a_record(&address, "Gaming-PC.local", 10, 0, 0, 9);
  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 2200,
             capture, &result) == 1);
  assert(result.count == 2);
  assert(strcmp(result.ipv4, "10.0.0.9") == 0);
}

static void test_srv_target_change_does_not_reuse_old_address(void) {
  packet_writer_t packet;
  packet_writer_t address;
  mdns_discovery_state_t state;
  captured_result_t result = {0};
  build_complete_response(&packet);
  mdns_discovery_state_init(&state);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 2300,
             capture, &result) == 1);

  build_srv_response(
      &packet, "Gaming-PC._nvstream._tcp.local",
      "Replacement.local", 47989);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 2400,
             capture, &result) == 0);
  assert(result.count == 1);

  begin_response(&address, 1);
  add_a_record(&address, "Replacement.local", 192, 168, 50, 20);
  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 2500,
             capture, &result) == 1);
  assert(result.count == 2);
  assert(strcmp(result.host, "Replacement.local") == 0);
  assert(strcmp(result.ipv4, "192.168.50.20") == 0);
}

static void test_address_goodbye_and_expiry_allow_same_tuple_again(void) {
  packet_writer_t packet;
  packet_writer_t address;
  mdns_discovery_state_t state;
  captured_result_t result = {0};
  build_complete_response(&packet);
  mdns_discovery_state_init(&state);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 3000,
             capture, &result) == 1);

  begin_response(&address, 1);
  add_a_record_with_ttl(
      &address, "Gaming-PC.local", 192, 168, 50, 20, 0);
  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 3100,
             capture, &result) == 0);
  begin_response(&address, 1);
  add_a_record(&address, "Gaming-PC.local", 192, 168, 50, 20);
  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 3200,
             capture, &result) == 1);
  assert(result.count == 2);

  /* Refresh only the SRV record near the cache deadline. The A record then
   * expires independently; the unchanged tuple must emit after reannounce. */
  build_srv_only(&packet);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 32999,
             capture, &result) == 0);
  mdns_discovery_expire(&state, 33201);
  bool retained_service_without_address = false;
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    if (state.services[i].occupied) {
      retained_service_without_address = true;
      assert(state.services[i].ipv4[0] == '\0');
      assert(state.services[i].reported_ipv4[0] == '\0');
      assert(state.services[i].reported_port == 0);
    }
  }
  assert(retained_service_without_address);
  begin_response(&address, 1);
  add_a_record(&address, "Gaming-PC.local", 192, 168, 50, 20);
  assert(mdns_discovery_parse_packet(
             &state, address.bytes, address.length, 33300,
             capture, &result) == 1);
  assert(result.count == 3);
}

static void test_all_truncations_are_rejected(void) {
  packet_writer_t packet;
  build_complete_response(&packet);
  for (size_t length = 0; length < packet.length; length++) {
    mdns_discovery_state_t state;
    captured_result_t result = {0};
    mdns_discovery_state_init(&state);
    assert(mdns_discovery_parse_packet(
               &state, packet.bytes, length, 3000,
               capture, &result) == -1);
    assert(result.count == 0);
  }
}

static void test_bad_compression_and_count_bounds(void) {
  packet_writer_t packet;
  mdns_discovery_state_t state;
  captured_result_t result = {0};

  begin_response(&packet, 1);
  put_pointer(&packet, 12);
  put_u16(&packet, 1);
  put_u16(&packet, 1);
  put_u32(&packet, 120);
  put_u16(&packet, 4);
  put_u32(&packet, 0x7f000001u);
  mdns_discovery_state_init(&state);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 4000,
             capture, &result) == -1);

  begin_response(&packet, 129);
  mdns_discovery_state_init(&state);
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 4000,
             capture, &result) == -1);
  assert(result.count == 0);
}

static void test_queries_and_truncated_responses_are_rejected(void) {
  packet_writer_t packet;
  mdns_discovery_state_t state;
  captured_result_t result = {0};
  build_complete_response(&packet);
  mdns_discovery_state_init(&state);

  packet.bytes[2] = 0;
  packet.bytes[3] = 0;
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 4500,
             capture, &result) == -1);
  assert(result.count == 0);

  build_complete_response(&packet);
  packet.bytes[2] |= 0x02;
  assert(mdns_discovery_parse_packet(
             &state, packet.bytes, packet.length, 4600,
             capture, &result) == -1);
  assert(result.count == 0);
}

static void test_transactional_failure_and_expiration(void) {
  packet_writer_t valid;
  packet_writer_t malformed;
  mdns_discovery_state_t state;
  mdns_discovery_state_t before;
  captured_result_t result = {0};
  build_complete_response(&valid);
  mdns_discovery_state_init(&state);
  assert(mdns_discovery_parse_packet(
             &state, valid.bytes, valid.length, 5000,
             capture, &result) == 1);

  malformed = valid;
  /* Inflate the final A-record RDLENGTH from four to eight bytes without
   * adding payload. The parser must reject the whole packet transactionally. */
  malformed.bytes[malformed.length - 5] = 8;
  before = state;
  assert(mdns_discovery_parse_packet(
             &state, malformed.bytes, malformed.length, 5100,
             capture, &result) == -1);
  assert(memcmp(&state, &before, sizeof(state)) == 0);
  assert(result.count == 1);

  mdns_discovery_expire(&state, 5000 + 30001);
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    assert(!state.services[i].occupied);
  }
}

int main(void) {
  test_complete_compressed_response();
  test_cross_packet_correlation_and_change();
  test_srv_target_change_does_not_reuse_old_address();
  test_address_goodbye_and_expiry_allow_same_tuple_again();
  test_all_truncations_are_rejected();
  test_bad_compression_and_count_bounds();
  test_queries_and_truncated_responses_are_rejected();
  test_transactional_failure_and_expiration();
  puts("mDNS parser tests passed");
  return 0;
}
