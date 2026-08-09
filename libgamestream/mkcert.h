/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2026 Vita Moonlight contributors
 *
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 */

#pragma once

#include <stddef.h>

#define MKCERT_CERTIFICATE_PEM_CAPACITY 4096
#define MKCERT_PRIVATE_KEY_PEM_CAPACITY 4096

/* Lengths exclude the terminating NUL byte. */
int mkcert_generate(
    char *certificatePem, size_t certificateCapacity,
    size_t *certificateLength, char *privateKeyPem,
    size_t privateKeyCapacity, size_t *privateKeyLength);
