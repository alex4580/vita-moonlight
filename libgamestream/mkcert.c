/*
 * Moonlight is free software; you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation; either version 3 of the License, or
 * (at your option) any later version.
 *
 * Moonlight is distributed in the hope that it will be useful,
 * but WITHOUT ANY WARRANTY; without even the implied warranty of
 * MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
 * GNU General Public License for more details.
 */

#include "mkcert.h"
#include "crypto.h"

#include <stdbool.h>
#include <string.h>

#include <mbedtls/md.h>
#include <mbedtls/pk.h>
#include <mbedtls/platform_util.h>
#include <mbedtls/rsa.h>
#include <mbedtls/x509_crt.h>

#define CLIENT_RSA_BITS 2048
#define CLIENT_RSA_EXPONENT 65537
#define CLIENT_CERTIFICATE_NAME "CN=Vita Moonlight Client"

int mkcert_generate(
    char *certificatePem, size_t certificateCapacity,
    size_t *certificateLength, char *privateKeyPem,
    size_t privateKeyCapacity, size_t *privateKeyLength) {
  if (certificatePem == NULL || certificateCapacity < 2 ||
      certificateLength == NULL || privateKeyPem == NULL ||
      privateKeyCapacity < 2 || privateKeyLength == NULL) {
    return -1;
  }
  certificatePem[0] = '\0';
  privateKeyPem[0] = '\0';
  *certificateLength = 0;
  *privateKeyLength = 0;

  mbedtls_pk_context key;
  mbedtls_x509write_cert certificate;
  mbedtls_pk_init(&key);
  mbedtls_x509write_crt_init(&certificate);

  int result = mbedtls_pk_setup(
      &key, mbedtls_pk_info_from_type(MBEDTLS_PK_RSA));
  if (result == 0) {
    mbedtls_rsa_context *rsa = mbedtls_pk_rsa(key);
    result = rsa == NULL ? -1 : mbedtls_rsa_gen_key(
        rsa, gs_crypto_random_callback, NULL,
        CLIENT_RSA_BITS, CLIENT_RSA_EXPONENT);
  }

  unsigned char serial[16] = {0};
  if (result == 0) {
    if (!gs_crypto_random(serial, sizeof(serial))) {
      result = -1;
    } else {
      /* Keep the raw serial positive and non-zero as required by DER INTEGER. */
      serial[0] &= 0x7f;
      serial[0] |= 0x01;
    }
  }

  if (result == 0) {
    mbedtls_x509write_crt_set_version(
        &certificate, MBEDTLS_X509_CRT_VERSION_3);
    mbedtls_x509write_crt_set_md_alg(&certificate, MBEDTLS_MD_SHA256);
    mbedtls_x509write_crt_set_subject_key(&certificate, &key);
    mbedtls_x509write_crt_set_issuer_key(&certificate, &key);
    result = mbedtls_x509write_crt_set_serial_raw(
        &certificate, serial, sizeof(serial));
  }
  if (result == 0) {
    result = mbedtls_x509write_crt_set_subject_name(
        &certificate, CLIENT_CERTIFICATE_NAME);
  }
  if (result == 0) {
    result = mbedtls_x509write_crt_set_issuer_name(
        &certificate, CLIENT_CERTIFICATE_NAME);
  }
  if (result == 0) {
    /* A fixed broad validity window avoids rejecting a sound identity when a
     * Vita has an unset RTC. Sunshine authenticates the key during pairing. */
    result = mbedtls_x509write_crt_set_validity(
        &certificate, "20200101000000", "20491231235959");
  }
  if (result == 0) {
    result = mbedtls_x509write_crt_pem(
        &certificate, (unsigned char *)certificatePem,
        certificateCapacity, gs_crypto_random_callback, NULL);
  }
  if (result == 0) {
    result = mbedtls_pk_write_key_pem(
        &key, (unsigned char *)privateKeyPem, privateKeyCapacity);
  }
  if (result == 0) {
    *certificateLength = strlen(certificatePem);
    *privateKeyLength = strlen(privateKeyPem);
    if (*certificateLength == 0 ||
        *certificateLength + 1 > certificateCapacity ||
        *privateKeyLength == 0 ||
        *privateKeyLength + 1 > privateKeyCapacity) {
      result = -1;
    }
  }

  mbedtls_platform_zeroize(serial, sizeof(serial));
  mbedtls_x509write_crt_free(&certificate);
  mbedtls_pk_free(&key);
  if (result != 0) {
    mbedtls_platform_zeroize(privateKeyPem, privateKeyCapacity);
    certificatePem[0] = '\0';
    *certificateLength = 0;
    *privateKeyLength = 0;
    return -1;
  }
  return 0;
}
