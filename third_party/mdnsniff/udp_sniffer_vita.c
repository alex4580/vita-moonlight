/*
 * SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Vita Moonlight contributors
 */

#include "udp_sniffer_vita.h"

#ifdef __vita__

#include "mdns_parser.h"

#include <stdint.h>
#include <string.h>

#include <psp2/kernel/processmgr.h>
#include <psp2/net/net.h>

#define MDNS_PORT 5353
#define MDNS_PACKET_CAPACITY 2048
#define MDNS_QUERY_INTERVAL_MS 3000u
#define MDNS_REOPEN_INTERVAL_MS 1000u
#define MDNS_MAX_PACKETS_PER_POLL 8u

static const char MDNS_MULTICAST_ADDRESS[] = "224.0.0.251";
static const uint8_t GAMESTREAM_QUERY[] = {
    0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00,
    0x09, '_', 'n', 'v', 's', 't', 'r', 'e', 'a', 'm',
    0x04, '_', 't', 'c', 'p',
    0x05, 'l', 'o', 'c', 'a', 'l',
    0x00, 0x00, 0x0c, 0x00, 0x01,
};

static int mdns_socket = -1;
static int result_index;
static uint32_t last_query_ms;
static uint32_t last_open_attempt_ms;
static moonlight_found_cb result_callback;
static mdns_discovery_state_t discovery_state;

static uint32_t monotonic_ms(void) {
  return (uint32_t)(sceKernelGetProcessTimeLow() / 1000u);
}

static void emit_result(
    void *context,
    const char *host,
    const char *computer_name,
    const char *ipv4,
    uint16_t port) {
  (void)context;
  if (result_callback != NULL) {
    result_index++;
    result_callback(result_index, host, computer_name, ipv4, (int)port);
  }
}

static int send_query(void) {
  SceNetSockaddrIn destination;
  memset(&destination, 0, sizeof(destination));
  destination.sin_family = SCE_NET_AF_INET;
  destination.sin_port = sceNetHtons(MDNS_PORT);
  if (sceNetInetPton(SCE_NET_AF_INET, MDNS_MULTICAST_ADDRESS,
                     &destination.sin_addr.s_addr) <= 0) {
    return -1;
  }
  return sceNetSendto(
      mdns_socket, GAMESTREAM_QUERY, sizeof(GAMESTREAM_QUERY), 0,
      (SceNetSockaddr *)&destination, sizeof(destination));
}

static void close_socket_after_network_change(void) {
  if (mdns_socket >= 0) {
    sceNetSocketClose(mdns_socket);
    mdns_socket = -1;
  }
  /* A socket failure commonly means Wi-Fi was disabled, the Vita resumed,
   * or its address changed.  Do not let tuples learned on the old interface
   * suppress a fresh discovery result after the socket rejoins multicast. */
  mdns_discovery_state_init(&discovery_state);
  last_query_ms = 0;
}

static int open_socket(void) {
  SceNetSockaddrIn local_address;
  SceNetIpMreq membership;
#if defined(SCE_NET_SOL_SOCKET) && defined(SCE_NET_SO_REUSEADDR)
  int reuse_address = 1;
#endif

  last_open_attempt_ms = monotonic_ms();
  mdns_socket = sceNetSocket(
      "vita-moonlight-mdns", SCE_NET_AF_INET, SCE_NET_SOCK_DGRAM, 0);
  if (mdns_socket < 0) return -1;

#if defined(SCE_NET_SOL_SOCKET) && defined(SCE_NET_SO_REUSEADDR)
  /* Permit a short overlap with another mDNS listener during UI/worker
   * handoff on VitaSDK versions that expose SO_REUSEADDR. */
  (void)sceNetSetsockopt(
      mdns_socket, SCE_NET_SOL_SOCKET, SCE_NET_SO_REUSEADDR,
      &reuse_address, sizeof(reuse_address));
#endif

  memset(&local_address, 0, sizeof(local_address));
  local_address.sin_family = SCE_NET_AF_INET;
  local_address.sin_addr.s_addr = sceNetHtonl(SCE_NET_INADDR_ANY);
  local_address.sin_port = sceNetHtons(MDNS_PORT);
  if (sceNetBind(mdns_socket, (SceNetSockaddr *)&local_address,
                 sizeof(local_address)) < 0) {
    close_socket_after_network_change();
    return -1;
  }

  memset(&membership, 0, sizeof(membership));
  if (sceNetInetPton(SCE_NET_AF_INET, MDNS_MULTICAST_ADDRESS,
                     &membership.imr_multiaddr.s_addr) <= 0) {
    close_socket_after_network_change();
    return -1;
  }
  membership.imr_interface.s_addr = sceNetHtonl(SCE_NET_INADDR_ANY);
  if (sceNetSetsockopt(mdns_socket, SCE_NET_IPPROTO_IP,
                       SCE_NET_IP_ADD_MEMBERSHIP,
                       &membership, sizeof(membership)) < 0) {
    close_socket_after_network_change();
    return -1;
  }

  last_query_ms = monotonic_ms();
  if (send_query() < 0) {
    close_socket_after_network_change();
    return -1;
  }
  return 0;
}

void udp_sniffer_vita_init(void) {
  udp_sniffer_vita_deinit();
  mdns_discovery_state_init(&discovery_state);
  result_index = 0;
  last_open_attempt_ms = 0;
  open_socket();
}

void udp_sniffer_vita_poll(void) {
  uint8_t packet[MDNS_PACKET_CAPACITY];
  SceNetSockaddrIn source;
  uint32_t now_ms;

  now_ms = monotonic_ms();
  if (mdns_socket < 0) {
    if (last_open_attempt_ms != 0 &&
        (uint32_t)(now_ms - last_open_attempt_ms) <
            MDNS_REOPEN_INTERVAL_MS) {
      return;
    }
    if (open_socket() < 0) return;
    now_ms = monotonic_ms();
  }

  if ((uint32_t)(now_ms - last_query_ms) >= MDNS_QUERY_INTERVAL_MS) {
    if (send_query() < 0) {
      close_socket_after_network_change();
      return;
    }
    last_query_ms = now_ms;
  }

  /* Drain a bounded burst so a busy LAN cannot starve the scanner thread,
   * while still handling more than one mDNS response per 100 ms UI tick. */
  for (unsigned int i = 0; i < MDNS_MAX_PACKETS_PER_POLL; ++i) {
    unsigned int source_length = sizeof(source);
    memset(&source, 0, sizeof(source));
    int received = sceNetRecvfrom(
        mdns_socket, packet, sizeof(packet), SCE_NET_MSG_DONTWAIT,
        (SceNetSockaddr *)&source, &source_length);
    if (received <= 0) break;
    if (source.sin_family != SCE_NET_AF_INET ||
        source.sin_port != sceNetHtons(MDNS_PORT)) {
      continue;
    }

    mdns_discovery_parse_packet(
        &discovery_state, packet, (size_t)received, now_ms,
        emit_result, NULL);
  }
  mdns_discovery_expire(&discovery_state, now_ms);
}

void udp_sniffer_vita_set_callback(moonlight_found_cb callback) {
  result_callback = callback;
}

void udp_sniffer_vita_deinit(void) {
  close_socket_after_network_change();
  result_callback = NULL;
  result_index = 0;
  last_query_ms = 0;
  last_open_attempt_ms = 0;
  mdns_discovery_state_init(&discovery_state);
}

#else

void udp_sniffer_vita_init(void) {}
void udp_sniffer_vita_poll(void) {}
void udp_sniffer_vita_set_callback(moonlight_found_cb callback) {
  (void)callback;
}
void udp_sniffer_vita_deinit(void) {}

#endif
