/*
 * SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Vita Moonlight contributors
 *
 * Length-safe DNS-SD parser written for Vita Moonlight. This implementation
 * was developed from the DNS wire format and DNS-SD service model; it does
 * not contain code from the former mdnsniff submodule.
 */

#include "mdns_parser.h"

#include <stdbool.h>
#include <stdio.h>
#include <string.h>

#define DNS_HEADER_SIZE 12u
#define DNS_MAX_QUESTIONS 64u
#define DNS_MAX_RESOURCE_RECORDS 128u
#define DNS_CLASS_IN 1u
#define DNS_FLAG_RESPONSE 0x8000u
#define DNS_FLAG_OPCODE_MASK 0x7800u
#define DNS_FLAG_TRUNCATED 0x0200u
#define DNS_TYPE_A 1u
#define DNS_TYPE_PTR 12u
#define DNS_TYPE_SRV 33u
#define MDNS_CACHE_LIFETIME_MS 30000u

static const char GAMESTREAM_SERVICE[] = "_nvstream._tcp.local";

static uint16_t read_u16(const uint8_t *bytes) {
  return (uint16_t)(((uint16_t)bytes[0] << 8) | bytes[1]);
}

static uint32_t read_u32(const uint8_t *bytes) {
  return ((uint32_t)bytes[0] << 24) |
         ((uint32_t)bytes[1] << 16) |
         ((uint32_t)bytes[2] << 8) |
         (uint32_t)bytes[3];
}

static int ascii_equal_ignore_case(const char *left, const char *right) {
  while (*left != '\0' && *right != '\0') {
    unsigned char a = (unsigned char)*left++;
    unsigned char b = (unsigned char)*right++;
    if (a >= 'A' && a <= 'Z') a = (unsigned char)(a + ('a' - 'A'));
    if (b >= 'A' && b <= 'Z') b = (unsigned char)(b + ('a' - 'A'));
    if (a != b) return 0;
  }
  return *left == *right;
}

static int ascii_ends_with_ignore_case(
    const char *value, const char *suffix) {
  size_t value_length = strlen(value);
  size_t suffix_length = strlen(suffix);
  if (value_length <= suffix_length) return 0;
  if (value[value_length - suffix_length - 1] != '.') return 0;
  return ascii_equal_ignore_case(
      value + value_length - suffix_length, suffix);
}

static int computer_name_from_instance(
    const char *instance, char *output, size_t output_size) {
  static const char suffix[] = "._nvstream._tcp.local";
  size_t instance_length;
  size_t suffix_length = sizeof(suffix) - 1u;
  size_t name_length;
  if (instance == NULL || output == NULL || output_size == 0) return -1;
  instance_length = strlen(instance);
  if (instance_length <= suffix_length ||
      !ascii_equal_ignore_case(
          instance + instance_length - suffix_length, suffix)) {
    return -1;
  }
  name_length = instance_length - suffix_length;
  if (name_length == 0 || name_length >= output_size) return -1;
  memcpy(output, instance, name_length);
  output[name_length] = '\0';
  return 0;
}

/*
 * DNS compression pointers can form cycles in hostile packets. The hop bound
 * is independent of output capacity and guarantees termination even when a
 * pointer jumps forward or points to itself.
 */
static int decode_dns_name(
    const uint8_t *packet,
    size_t packet_size,
    size_t offset,
    char *output,
    size_t output_size,
    size_t *consumed) {
  size_t position = offset;
  size_t output_length = 0;
  size_t encoded_length = 0;
  size_t pointer_hops = 0;
  int jumped = 0;

  if (packet == NULL || output == NULL || output_size == 0 ||
      consumed == NULL || offset >= packet_size) {
    return -1;
  }
  output[0] = '\0';

  for (;;) {
    uint8_t label_length;
    if (position >= packet_size) return -1;
    label_length = packet[position];

    if (label_length == 0) {
      if (!jumped) encoded_length++;
      output[output_length] = '\0';
      *consumed = encoded_length;
      return 0;
    }

    if ((label_length & 0xc0u) == 0xc0u) {
      size_t pointer;
      if (position + 1 >= packet_size) return -1;
      pointer = ((size_t)(label_length & 0x3fu) << 8) |
                packet[position + 1];
      if (pointer >= packet_size || ++pointer_hops > packet_size) return -1;
      if (!jumped) encoded_length += 2;
      position = pointer;
      jumped = 1;
      continue;
    }

    if ((label_length & 0xc0u) != 0 || label_length > 63u) return -1;
    if (position + 1u + label_length > packet_size) return -1;
    if (!jumped) encoded_length += 1u + label_length;

    if (output_length != 0) {
      if (output_length + 1 >= output_size) return -1;
      output[output_length++] = '.';
    }
    if (output_length + label_length >= output_size) return -1;

    for (size_t i = 0; i < label_length; i++) {
      uint8_t character = packet[position + 1u + i];
      /* Embedded NUL/control bytes cannot be represented safely by callers. */
      if (character < 0x20u || character == 0x7fu) return -1;
      output[output_length++] = (char)character;
    }
    output[output_length] = '\0';
    position += 1u + label_length;
  }
}

static void copy_string(char *destination, size_t capacity,
                        const char *source) {
  if (capacity == 0) return;
  snprintf(destination, capacity, "%s", source == NULL ? "" : source);
}

static void clear_service(mdns_discovery_service_t *service) {
  memset(service, 0, sizeof(*service));
}

static void clear_address(mdns_discovery_address_t *address) {
  memset(address, 0, sizeof(*address));
}

static mdns_discovery_service_t *find_service(
    mdns_discovery_state_t *state, const char *instance) {
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    if (state->services[i].occupied &&
        ascii_equal_ignore_case(state->services[i].instance, instance)) {
      return &state->services[i];
    }
  }
  return NULL;
}

static mdns_discovery_service_t *ensure_service(
    mdns_discovery_state_t *state,
    const char *instance,
    uint32_t now_ms) {
  mdns_discovery_service_t *oldest = NULL;
  mdns_discovery_service_t *service = find_service(state, instance);
  if (service != NULL) return service;

  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    if (!state->services[i].occupied) {
      service = &state->services[i];
      break;
    }
    if (oldest == NULL ||
        (uint32_t)(now_ms - state->services[i].last_seen_ms) >
        (uint32_t)(now_ms - oldest->last_seen_ms)) {
      oldest = &state->services[i];
    }
  }
  if (service == NULL) service = oldest;
  if (service == NULL) return NULL;
  clear_service(service);
  service->occupied = 1;
  service->last_seen_ms = now_ms;
  copy_string(service->instance, sizeof(service->instance), instance);
  return service;
}

static mdns_discovery_address_t *find_address(
    mdns_discovery_state_t *state, const char *target) {
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_ADDRESSES; i++) {
    if (state->addresses[i].occupied &&
        ascii_equal_ignore_case(state->addresses[i].target, target)) {
      return &state->addresses[i];
    }
  }
  return NULL;
}

static mdns_discovery_address_t *ensure_address(
    mdns_discovery_state_t *state,
    const char *target,
    uint32_t now_ms) {
  mdns_discovery_address_t *oldest = NULL;
  mdns_discovery_address_t *address = find_address(state, target);
  if (address != NULL) return address;

  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_ADDRESSES; i++) {
    if (!state->addresses[i].occupied) {
      address = &state->addresses[i];
      break;
    }
    if (oldest == NULL ||
        (uint32_t)(now_ms - state->addresses[i].last_seen_ms) >
        (uint32_t)(now_ms - oldest->last_seen_ms)) {
      oldest = &state->addresses[i];
    }
  }
  if (address == NULL) address = oldest;
  if (address == NULL) return NULL;
  clear_address(address);
  address->occupied = 1;
  address->last_seen_ms = now_ms;
  copy_string(address->target, sizeof(address->target), target);
  return address;
}

static void apply_cached_address(
    mdns_discovery_state_t *state,
    mdns_discovery_service_t *service) {
  mdns_discovery_address_t *address;
  if (service->target[0] == '\0') return;
  address = find_address(state, service->target);
  if (address != NULL) {
    copy_string(service->ipv4, sizeof(service->ipv4), address->ipv4);
  }
}

static void reset_reported_service(mdns_discovery_service_t *service) {
  service->reported_ipv4[0] = '\0';
  service->reported_port = 0;
}

static void apply_address_to_services(
    mdns_discovery_state_t *state,
    const char *target,
    const char *ipv4,
    uint32_t now_ms) {
  (void)now_ms;
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    mdns_discovery_service_t *service = &state->services[i];
    if (service->occupied && service->target[0] != '\0' &&
        ascii_equal_ignore_case(service->target, target)) {
      copy_string(service->ipv4, sizeof(service->ipv4), ipv4);
      if (ipv4[0] == '\0') {
        /* A goodbye/expiry must allow the same address to be announced
         * again. Keeping the previous reported tuple would suppress that
         * recovery callback even though the live address was cleared. */
        reset_reported_service(service);
      }
    }
  }
}

void mdns_discovery_state_init(mdns_discovery_state_t *state) {
  if (state != NULL) memset(state, 0, sizeof(*state));
}

void mdns_discovery_expire(mdns_discovery_state_t *state, uint32_t now_ms) {
  if (state == NULL) return;
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
    if (state->services[i].occupied &&
        (uint32_t)(now_ms - state->services[i].last_seen_ms) >
            MDNS_CACHE_LIFETIME_MS) {
      clear_service(&state->services[i]);
    }
  }
  for (size_t i = 0; i < MDNS_DISCOVERY_MAX_ADDRESSES; i++) {
    if (state->addresses[i].occupied &&
        (uint32_t)(now_ms - state->addresses[i].last_seen_ms) >
            MDNS_CACHE_LIFETIME_MS) {
      char expired_target[MDNS_DISCOVERY_NAME_CAPACITY];
      copy_string(expired_target, sizeof(expired_target),
                  state->addresses[i].target);
      clear_address(&state->addresses[i]);
      apply_address_to_services(state, expired_target, "", now_ms);
    }
  }
}

int mdns_discovery_parse_packet(
    mdns_discovery_state_t *state,
    const uint8_t *packet,
    size_t packet_size,
    uint32_t now_ms,
    mdns_discovery_result_cb callback,
    void *callback_context) {
  mdns_discovery_state_t next;
  size_t emitted_indices[MDNS_DISCOVERY_MAX_SERVICES];
  size_t emitted_count = 0;
  size_t position = DNS_HEADER_SIZE;
  uint16_t question_count;
  uint16_t flags;
  size_t record_count;

  if (state == NULL || packet == NULL || packet_size < DNS_HEADER_SIZE) {
    return -1;
  }

  flags = read_u16(packet + 2);
  if ((flags & DNS_FLAG_RESPONSE) == 0 ||
      (flags & DNS_FLAG_OPCODE_MASK) != 0 ||
      (flags & DNS_FLAG_TRUNCATED) != 0) {
    return -1;
  }

  question_count = read_u16(packet + 4);
  record_count = (size_t)read_u16(packet + 6) +
                 (size_t)read_u16(packet + 8) +
                 (size_t)read_u16(packet + 10);
  if (question_count > DNS_MAX_QUESTIONS ||
      record_count > DNS_MAX_RESOURCE_RECORDS) {
    return -1;
  }

  next = *state;
  mdns_discovery_expire(&next, now_ms);

  for (size_t i = 0; i < question_count; i++) {
    char ignored_name[MDNS_DISCOVERY_NAME_CAPACITY];
    size_t encoded_length;
    if (decode_dns_name(packet, packet_size, position,
                        ignored_name, sizeof(ignored_name),
                        &encoded_length) < 0 ||
        encoded_length > packet_size - position) {
      return -1;
    }
    position += encoded_length;
    if (packet_size - position < 4u) return -1;
    position += 4u;
  }

  for (size_t i = 0; i < record_count; i++) {
    char owner[MDNS_DISCOVERY_NAME_CAPACITY];
    size_t owner_length;
    uint16_t type;
    uint16_t record_class;
    uint32_t ttl;
    uint16_t data_length;
    size_t data_offset;

    if (decode_dns_name(packet, packet_size, position,
                        owner, sizeof(owner), &owner_length) < 0 ||
        owner_length > packet_size - position) {
      return -1;
    }
    position += owner_length;
    if (packet_size - position < 10u) return -1;

    type = read_u16(packet + position);
    record_class = (uint16_t)(read_u16(packet + position + 2) & 0x7fffu);
    ttl = read_u32(packet + position + 4);
    data_length = read_u16(packet + position + 8);
    position += 10u;
    data_offset = position;
    if ((size_t)data_length > packet_size - data_offset) return -1;
    position += data_length;

    if (record_class != DNS_CLASS_IN) continue;

    if (type == DNS_TYPE_PTR &&
        ascii_equal_ignore_case(owner, GAMESTREAM_SERVICE)) {
      char instance[MDNS_DISCOVERY_NAME_CAPACITY];
      size_t encoded_length;
      mdns_discovery_service_t *service;
      if (decode_dns_name(packet, packet_size, data_offset,
                          instance, sizeof(instance), &encoded_length) < 0 ||
          encoded_length != data_length) {
        return -1;
      }
      service = find_service(&next, instance);
      if (ttl == 0) {
        if (service != NULL) clear_service(service);
      } else if (ascii_ends_with_ignore_case(
                     instance, GAMESTREAM_SERVICE)) {
        service = ensure_service(&next, instance, now_ms);
        if (service != NULL) service->last_seen_ms = now_ms;
      }
    } else if (type == DNS_TYPE_SRV &&
               ascii_ends_with_ignore_case(owner, GAMESTREAM_SERVICE)) {
      char target[MDNS_DISCOVERY_NAME_CAPACITY];
      size_t encoded_length;
      mdns_discovery_service_t *service = find_service(&next, owner);
      if (ttl == 0) {
        if (service != NULL) clear_service(service);
        continue;
      }
      if (data_length < 7u ||
          decode_dns_name(packet, packet_size, data_offset + 6u,
                          target, sizeof(target), &encoded_length) < 0 ||
          encoded_length != (size_t)data_length - 6u ||
          target[0] == '\0') {
        return -1;
      }
      service = ensure_service(&next, owner, now_ms);
      if (service != NULL) {
        bool target_changed = service->target[0] != '\0' &&
            !ascii_equal_ignore_case(service->target, target);
        if (target_changed) {
          /* Never carry an address or callback-deduplication tuple across an
           * SRV target change. A new target must be correlated with its own A
           * record, even when it later resolves to the same numeric address. */
          service->ipv4[0] = '\0';
          reset_reported_service(service);
        }
        service->port = read_u16(packet + data_offset + 4u);
        copy_string(service->target, sizeof(service->target), target);
        service->last_seen_ms = now_ms;
        apply_cached_address(&next, service);
      }
    } else if (type == DNS_TYPE_A && data_length == 4u && owner[0] != '\0') {
      mdns_discovery_address_t *address = find_address(&next, owner);
      char ipv4[MDNS_DISCOVERY_IPV4_CAPACITY];
      snprintf(ipv4, sizeof(ipv4), "%u.%u.%u.%u",
               (unsigned int)packet[data_offset],
               (unsigned int)packet[data_offset + 1u],
               (unsigned int)packet[data_offset + 2u],
               (unsigned int)packet[data_offset + 3u]);
      if (ttl == 0) {
        if (address != NULL) clear_address(address);
        apply_address_to_services(&next, owner, "", now_ms);
        continue;
      }
      address = ensure_address(&next, owner, now_ms);
      if (address != NULL) {
        copy_string(address->ipv4, sizeof(address->ipv4), ipv4);
        address->last_seen_ms = now_ms;
      }
      apply_address_to_services(&next, owner, ipv4, now_ms);
    }
  }

  if (callback != NULL) {
    for (size_t i = 0; i < MDNS_DISCOVERY_MAX_SERVICES; i++) {
      mdns_discovery_service_t *service = &next.services[i];
      if (!service->occupied || service->target[0] == '\0' ||
          service->ipv4[0] == '\0' || service->port == 0) {
        continue;
      }
      if (service->reported_port == service->port &&
          strcmp(service->reported_ipv4, service->ipv4) == 0) {
        continue;
      }
      copy_string(service->reported_ipv4,
                  sizeof(service->reported_ipv4), service->ipv4);
      service->reported_port = service->port;
      emitted_indices[emitted_count++] = i;
    }
  }

  *state = next;
  for (size_t i = 0; i < emitted_count; i++) {
    const mdns_discovery_service_t *service =
        &state->services[emitted_indices[i]];
    char computer_name[MDNS_DISCOVERY_NAME_CAPACITY];
    if (computer_name_from_instance(
            service->instance, computer_name, sizeof(computer_name)) == 0) {
      /* Match the established Vita discovery API: host is the SRV target,
       * while pcname is the user-facing DNS-SD service-instance label. */
      callback(callback_context, service->target, computer_name,
               service->ipv4, service->port);
    }
  }
  return (int)emitted_count;
}
