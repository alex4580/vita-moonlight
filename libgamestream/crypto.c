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

#include "crypto.h"

#include <limits.h>
#include <stdint.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#include <mbedtls/aes.h>
#include <mbedtls/asn1.h>
#include <mbedtls/base64.h>
#include <mbedtls/build_info.h>
#include <mbedtls/ctr_drbg.h>
#include <mbedtls/entropy.h>
#include <mbedtls/md.h>
#include <mbedtls/pk.h>
#include <mbedtls/platform_util.h>
#include <mbedtls/sha1.h>
#include <mbedtls/sha256.h>
#include <mbedtls/x509_crt.h>

#if defined(MBEDTLS_USE_PSA_CRYPTO)
#include <mbedtls/psa_util.h>
#include <psa/crypto.h>
#include <psa/crypto_extra.h>
#endif

#ifdef __vita__
#include <psp2/kernel/rng.h>
#endif

struct _GS_CRYPTO_IDENTITY {
  mbedtls_x509_crt certificate;
  mbedtls_pk_context privateKey;
};

static bool cryptoReady;

#ifndef __vita__
static mbedtls_entropy_context entropyContext;
static mbedtls_ctr_drbg_context randomContext;
#endif

int gs_crypto_init(void) {
  if (cryptoReady) return 0;

#if defined(MBEDTLS_USE_PSA_CRYPTO)
  if (psa_crypto_init() != PSA_SUCCESS) return -1;
#endif

#ifndef __vita__
  static const unsigned char personalization[] = "Vita Moonlight pairing";
  mbedtls_entropy_init(&entropyContext);
  mbedtls_ctr_drbg_init(&randomContext);
  if (mbedtls_ctr_drbg_seed(
          &randomContext, mbedtls_entropy_func, &entropyContext,
          personalization, sizeof(personalization) - 1) != 0) {
    mbedtls_ctr_drbg_free(&randomContext);
    mbedtls_entropy_free(&entropyContext);
#if defined(MBEDTLS_USE_PSA_CRYPTO)
    mbedtls_psa_crypto_free();
#endif
    return -1;
  }
#endif

  cryptoReady = true;
  return 0;
}

void gs_crypto_cleanup(void) {
  if (!cryptoReady) return;
#ifndef __vita__
  mbedtls_ctr_drbg_free(&randomContext);
  mbedtls_entropy_free(&entropyContext);
#endif
#if defined(MBEDTLS_USE_PSA_CRYPTO)
  mbedtls_psa_crypto_free();
#endif
  cryptoReady = false;
}

int gs_crypto_random_callback(
    void *context, unsigned char *output, size_t outputLength) {
  (void)context;
  if (!cryptoReady || (output == NULL && outputLength != 0)) {
    return MBEDTLS_ERR_ENTROPY_SOURCE_FAILED;
  }

#ifdef __vita__
  /* Vita's kernel RNG accepts small unsigned lengths. Keeping requests to
   * 64 bytes also avoids SDK/runtime differences in the maximum request. */
  while (outputLength != 0) {
    unsigned int chunk = outputLength > 64 ? 64u : (unsigned int)outputLength;
    if (sceKernelGetRandomNumber(output, chunk) < 0) {
      return MBEDTLS_ERR_ENTROPY_SOURCE_FAILED;
    }
    output += chunk;
    outputLength -= chunk;
  }
  return 0;
#else
  return mbedtls_ctr_drbg_random(&randomContext, output, outputLength);
#endif
}

bool gs_crypto_random(void *output, size_t outputLength) {
  return gs_crypto_random_callback(NULL, output, outputLength) == 0;
}

bool gs_crypto_hash_segments(
    int hashLength, const GS_CRYPTO_BUFFER *segments, size_t segmentCount,
    unsigned char output[GS_CRYPTO_SHA256_LENGTH]) {
  if (output == NULL || (segments == NULL && segmentCount != 0)) return false;
  memset(output, 0, GS_CRYPTO_SHA256_LENGTH);

  if (hashLength == GS_CRYPTO_SHA256_LENGTH) {
    mbedtls_sha256_context context;
    mbedtls_sha256_init(&context);
    int result = mbedtls_sha256_starts(&context, 0);
    for (size_t i = 0; result == 0 && i < segmentCount; ++i) {
      if (segments[i].data == NULL && segments[i].length != 0) {
        result = -1;
      } else if (segments[i].length != 0) {
        result = mbedtls_sha256_update(
            &context, segments[i].data, segments[i].length);
      }
    }
    if (result == 0) result = mbedtls_sha256_finish(&context, output);
    mbedtls_sha256_free(&context);
    return result == 0;
  }

  if (hashLength == GS_CRYPTO_SHA1_LENGTH) {
    mbedtls_sha1_context context;
    mbedtls_sha1_init(&context);
    int result = mbedtls_sha1_starts(&context);
    for (size_t i = 0; result == 0 && i < segmentCount; ++i) {
      if (segments[i].data == NULL && segments[i].length != 0) {
        result = -1;
      } else if (segments[i].length != 0) {
        result = mbedtls_sha1_update(
            &context, segments[i].data, segments[i].length);
      }
    }
    if (result == 0) result = mbedtls_sha1_finish(&context, output);
    mbedtls_sha1_free(&context);
    return result == 0;
  }

  return false;
}

bool gs_crypto_hash(int hashLength, const void *input, size_t inputLength,
                    unsigned char output[GS_CRYPTO_SHA256_LENGTH]) {
  GS_CRYPTO_BUFFER segment = {
      .data = (const unsigned char *)input,
      .length = inputLength,
  };
  return gs_crypto_hash_segments(hashLength, &segment, 1, output);
}

bool gs_crypto_aes_128_ecb(
    bool encrypt, const unsigned char key[16], const unsigned char *input,
    size_t inputLength, unsigned char *output) {
  if (key == NULL || input == NULL || output == NULL || inputLength == 0 ||
      inputLength % GS_CRYPTO_AES_BLOCK_LENGTH != 0) {
    return false;
  }

  mbedtls_aes_context context;
  mbedtls_aes_init(&context);
  int result = encrypt
      ? mbedtls_aes_setkey_enc(&context, key, 128)
      : mbedtls_aes_setkey_dec(&context, key, 128);
  for (size_t offset = 0; result == 0 && offset < inputLength;
       offset += GS_CRYPTO_AES_BLOCK_LENGTH) {
    result = mbedtls_aes_crypt_ecb(
        &context, encrypt ? MBEDTLS_AES_ENCRYPT : MBEDTLS_AES_DECRYPT,
        input + offset, output + offset);
  }
  mbedtls_aes_free(&context);
  return result == 0;
}

GS_CRYPTO_IDENTITY *gs_crypto_identity_load_pem(
    const char *certificatePem, size_t certificatePemLength,
    const char *privateKeyPem, size_t privateKeyPemLength) {
  if (!cryptoReady || certificatePem == NULL || certificatePemLength < 2 ||
      privateKeyPem == NULL || privateKeyPemLength < 2 ||
      certificatePem[certificatePemLength - 1] != '\0' ||
      privateKeyPem[privateKeyPemLength - 1] != '\0') {
    return NULL;
  }

  GS_CRYPTO_IDENTITY *identity = calloc(1, sizeof(*identity));
  if (identity == NULL) return NULL;
  mbedtls_x509_crt_init(&identity->certificate);
  mbedtls_pk_init(&identity->privateKey);

  int certificateResult = mbedtls_x509_crt_parse(
      &identity->certificate, (const unsigned char *)certificatePem,
      certificatePemLength);
  int keyResult = certificateResult == 0
      ? mbedtls_pk_parse_key(
            &identity->privateKey, (const unsigned char *)privateKeyPem,
            privateKeyPemLength, NULL, 0, gs_crypto_random_callback, NULL)
      : -1;
  bool valid = certificateResult == 0 && keyResult == 0 &&
      identity->certificate.next == NULL &&
      mbedtls_pk_can_do(&identity->certificate.pk, MBEDTLS_PK_RSA) &&
      mbedtls_pk_can_do(&identity->privateKey, MBEDTLS_PK_RSA) &&
      mbedtls_pk_check_pair(
          &identity->certificate.pk, &identity->privateKey,
          gs_crypto_random_callback, NULL) == 0;
  if (!valid) {
    gs_crypto_identity_free(identity);
    return NULL;
  }
  return identity;
}

void gs_crypto_identity_free(GS_CRYPTO_IDENTITY *identity) {
  if (identity == NULL) return;
  mbedtls_pk_free(&identity->privateKey);
  mbedtls_x509_crt_free(&identity->certificate);
  mbedtls_platform_zeroize(identity, sizeof(*identity));
  free(identity);
}

static bool certificate_signature_from_der(
    const unsigned char *der, size_t derLength,
    const unsigned char **signature, size_t *signatureLength) {
  if (der == NULL || derLength == 0 || signature == NULL ||
      signatureLength == NULL) {
    return false;
  }

  unsigned char *cursor = (unsigned char *)der;
  const unsigned char *derEnd = der + derLength;
  size_t length = 0;
  const int sequenceTag = MBEDTLS_ASN1_CONSTRUCTED | MBEDTLS_ASN1_SEQUENCE;
  if (mbedtls_asn1_get_tag(&cursor, derEnd, &length, sequenceTag) != 0 ||
      length > (size_t)(derEnd - cursor) || cursor + length != derEnd) {
    return false;
  }
  const unsigned char *certificateEnd = cursor + length;

  if (mbedtls_asn1_get_tag(
          &cursor, certificateEnd, &length, sequenceTag) != 0 ||
      length > (size_t)(certificateEnd - cursor)) {
    return false;
  }
  cursor += length; /* TBSCertificate */

  if (mbedtls_asn1_get_tag(
          &cursor, certificateEnd, &length, sequenceTag) != 0 ||
      length > (size_t)(certificateEnd - cursor)) {
    return false;
  }
  cursor += length; /* signatureAlgorithm */

  if (mbedtls_asn1_get_bitstring_null(
          &cursor, certificateEnd, &length) != 0 ||
      length == 0 || length > GS_CRYPTO_MAX_SIGNATURE_LENGTH ||
      length > (size_t)(certificateEnd - cursor) ||
      cursor + length != certificateEnd) {
    return false;
  }
  *signature = cursor;
  *signatureLength = length;
  return true;
}

bool gs_crypto_identity_certificate_signature(
    const GS_CRYPTO_IDENTITY *identity, const unsigned char **signature,
    size_t *signatureLength) {
  if (identity == NULL) return false;
  return certificate_signature_from_der(
      identity->certificate.raw.p, identity->certificate.raw.len,
      signature, signatureLength);
}

bool gs_crypto_pem_certificate_signature(
    const char *certificatePem, unsigned char **signature,
    size_t *signatureLength) {
  if (!cryptoReady || certificatePem == NULL || signature == NULL ||
      signatureLength == NULL) {
    return false;
  }
  *signature = NULL;
  *signatureLength = 0;

  mbedtls_x509_crt certificate;
  mbedtls_x509_crt_init(&certificate);
  int result = mbedtls_x509_crt_parse(
      &certificate, (const unsigned char *)certificatePem,
      strlen(certificatePem) + 1);
  const unsigned char *view = NULL;
  size_t viewLength = 0;
  bool valid = result == 0 && certificate.next == NULL &&
      certificate_signature_from_der(
          certificate.raw.p, certificate.raw.len, &view, &viewLength);
  if (valid) {
    unsigned char *copy = malloc(viewLength);
    if (copy != NULL) {
      memcpy(copy, view, viewLength);
      *signature = copy;
      *signatureLength = viewLength;
    } else {
      valid = false;
    }
  }
  mbedtls_x509_crt_free(&certificate);
  return valid;
}

bool gs_crypto_identity_sign_sha256(
    GS_CRYPTO_IDENTITY *identity, const void *data, size_t dataLength,
    unsigned char *signature, size_t signatureCapacity,
    size_t *signatureLength) {
  if (identity == NULL || data == NULL || signature == NULL ||
      signatureLength == NULL) {
    return false;
  }
  unsigned char digest[GS_CRYPTO_SHA256_LENGTH];
  if (!gs_crypto_hash(
          GS_CRYPTO_SHA256_LENGTH, data, dataLength, digest)) {
    return false;
  }
  int result = mbedtls_pk_sign(
      &identity->privateKey, MBEDTLS_MD_SHA256, digest, sizeof(digest),
      signature, signatureCapacity, signatureLength,
      gs_crypto_random_callback, NULL);
  mbedtls_platform_zeroize(digest, sizeof(digest));
  return result == 0;
}

bool gs_crypto_verify_sha256_pem(
    const char *certificatePem, const void *data, size_t dataLength,
    const unsigned char *signature, size_t signatureLength) {
  if (!cryptoReady || certificatePem == NULL || data == NULL ||
      signature == NULL || signatureLength == 0) {
    return false;
  }
  mbedtls_x509_crt certificate;
  mbedtls_x509_crt_init(&certificate);
  int result = mbedtls_x509_crt_parse(
      &certificate, (const unsigned char *)certificatePem,
      strlen(certificatePem) + 1);
  unsigned char digest[GS_CRYPTO_SHA256_LENGTH];
  if (result == 0 && certificate.next == NULL &&
      gs_crypto_hash(GS_CRYPTO_SHA256_LENGTH, data, dataLength, digest)) {
    result = mbedtls_pk_verify(
        &certificate.pk, MBEDTLS_MD_SHA256, digest, sizeof(digest),
        signature, signatureLength);
  } else {
    result = -1;
  }
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_x509_crt_free(&certificate);
  return result == 0;
}

bool gs_crypto_spki_pin_from_pem(
    const char *certificatePem,
    char output[GS_CRYPTO_SPKI_PIN_LENGTH + 1]) {
  if (!cryptoReady || certificatePem == NULL || output == NULL) return false;
  output[0] = '\0';

  mbedtls_x509_crt certificate;
  mbedtls_x509_crt_init(&certificate);
  int result = mbedtls_x509_crt_parse(
      &certificate, (const unsigned char *)certificatePem,
      strlen(certificatePem) + 1);
  unsigned char publicKeyDer[4096];
  int publicKeyLength = result == 0 && certificate.next == NULL
      ? mbedtls_pk_write_pubkey_der(
            &certificate.pk, publicKeyDer, sizeof(publicKeyDer))
      : -1;
  unsigned char digest[GS_CRYPTO_SHA256_LENGTH];
  if (publicKeyLength <= 0 ||
      !gs_crypto_hash(
          GS_CRYPTO_SHA256_LENGTH,
          publicKeyDer + sizeof(publicKeyDer) - (size_t)publicKeyLength,
          (size_t)publicKeyLength, digest)) {
    mbedtls_x509_crt_free(&certificate);
    return false;
  }

  unsigned char encoded[45];
  size_t encodedLength = 0;
  result = mbedtls_base64_encode(
      encoded, sizeof(encoded), &encodedLength, digest, sizeof(digest));
  mbedtls_platform_zeroize(digest, sizeof(digest));
  mbedtls_x509_crt_free(&certificate);
  if (result != 0 || encodedLength != 44) return false;
  encoded[encodedLength] = '\0';

  int written = snprintf(
      output, GS_CRYPTO_SPKI_PIN_LENGTH + 1, "sha256//%s", encoded);
  return written == GS_CRYPTO_SPKI_PIN_LENGTH;
}
