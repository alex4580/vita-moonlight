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

#pragma once

#include <stdbool.h>
#include <stdlib.h>

#define CERTIFICATE_FILE_NAME "client.pem"
#define KEY_FILE_NAME "key.pem"
#define SERVER_PIN_FILE_NAME "server-pin.txt"

/*
 * libcurl expresses these values in seconds. A zero total timeout is
 * intentional for Sunshine's first pairing request: that response is held
 * open while the user enters the PIN in Sunshine's UI.
 */
#define HTTP_TIMEOUT_PAIRING_USER_SECONDS 0L
#define HTTP_TIMEOUT_ORDINARY_SECONDS 30L
#define HTTP_TIMEOUT_LAUNCH_SECONDS 120L

typedef struct _HTTP_DATA {
  char *memory;
  size_t size;
} HTTP_DATA, *PHTTP_DATA;

int http_init(const char* keyDirectory, int logLevel);
PHTTP_DATA http_create_data();
int http_request(char* url, PHTTP_DATA data);
int http_request_with_timeout(char* url, PHTTP_DATA data, long timeoutSeconds);
void http_free_data(PHTTP_DATA data);
void http_cleanup(void);

/*
 * Sunshine uses a self-signed certificate for the GameStream HTTPS API.  We
 * authenticate it with an SPKI SHA-256 pin established by the PIN pairing
 * exchange instead of disabling all server authentication.
 */
bool http_has_server_pin(void);
int http_set_server_pin_from_pem(const char* certificatePem, bool persist);
int http_reload_server_pin(void);
