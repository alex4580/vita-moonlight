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

#include <stdbool.h>
#include <stddef.h>

#define GS_CRYPTO_SHA1_LENGTH 20
#define GS_CRYPTO_SHA256_LENGTH 32
#define GS_CRYPTO_AES_BLOCK_LENGTH 16
#define GS_CRYPTO_MAX_SIGNATURE_LENGTH 1024
#define GS_CRYPTO_SPKI_PIN_LENGTH 52

typedef struct _GS_CRYPTO_IDENTITY GS_CRYPTO_IDENTITY;

typedef struct _GS_CRYPTO_BUFFER {
  const unsigned char *data;
  size_t length;
} GS_CRYPTO_BUFFER;

int gs_crypto_init(void);
void gs_crypto_cleanup(void);

/* This signature is also the RNG callback expected by Mbed TLS. */
int gs_crypto_random_callback(void *context, unsigned char *output,
                              size_t outputLength);
bool gs_crypto_random(void *output, size_t outputLength);

bool gs_crypto_hash(int hashLength, const void *input, size_t inputLength,
                    unsigned char output[GS_CRYPTO_SHA256_LENGTH]);
bool gs_crypto_hash_segments(
    int hashLength, const GS_CRYPTO_BUFFER *segments, size_t segmentCount,
    unsigned char output[GS_CRYPTO_SHA256_LENGTH]);
bool gs_crypto_aes_128_ecb(bool encrypt, const unsigned char key[16],
                           const unsigned char *input, size_t inputLength,
                           unsigned char *output);

GS_CRYPTO_IDENTITY *gs_crypto_identity_load_pem(
    const char *certificatePem, size_t certificatePemLength,
    const char *privateKeyPem, size_t privateKeyPemLength);
void gs_crypto_identity_free(GS_CRYPTO_IDENTITY *identity);
bool gs_crypto_identity_certificate_signature(
    const GS_CRYPTO_IDENTITY *identity, const unsigned char **signature,
    size_t *signatureLength);
bool gs_crypto_pem_certificate_signature(
    const char *certificatePem, unsigned char **signature,
    size_t *signatureLength);
bool gs_crypto_identity_sign_sha256(
    GS_CRYPTO_IDENTITY *identity, const void *data, size_t dataLength,
    unsigned char *signature, size_t signatureCapacity,
    size_t *signatureLength);
bool gs_crypto_verify_sha256_pem(
    const char *certificatePem, const void *data, size_t dataLength,
    const unsigned char *signature, size_t signatureLength);
bool gs_crypto_spki_pin_from_pem(
    const char *certificatePem,
    char output[GS_CRYPTO_SPKI_PIN_LENGTH + 1]);
