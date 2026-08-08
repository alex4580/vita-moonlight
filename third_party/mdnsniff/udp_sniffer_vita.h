/*
 * SPDX-License-Identifier: GPL-3.0-only
 * Copyright (C) 2026 Vita Moonlight contributors
 */

#ifndef VITA_MOONLIGHT_UDP_SNIFFER_VITA_H
#define VITA_MOONLIGHT_UDP_SNIFFER_VITA_H

typedef void (*moonlight_found_cb)(
    int index,
    const char *service_instance,
    const char *computer_name,
    const char *ipv4,
    int port);

void udp_sniffer_vita_init(void);
void udp_sniffer_vita_poll(void);
void udp_sniffer_vita_set_callback(moonlight_found_cb callback);
void udp_sniffer_vita_deinit(void);

#endif
