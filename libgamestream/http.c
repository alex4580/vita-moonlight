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

#include "http.h"
#include "errors.h"

#include <ctype.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <curl/curl.h>
#include <openssl/evp.h>
#include <openssl/pem.h>
#include <openssl/sha.h>
#include <openssl/x509.h>

#define HTTP_PATH_MAX 1024
#define HTTP_MAX_RESPONSE_SIZE (1024 * 1024)
#define SPKI_PIN_BUFFER_SIZE 64

static CURL *curl;
static bool debug;
static bool responseTooLarge;
static char keyDirectoryPath[HTTP_PATH_MAX];
static char serverPin[SPKI_PIN_BUFFER_SIZE];

typedef enum _PIN_FILE_STATE {
  PIN_FILE_MISSING,
  PIN_FILE_INVALID,
  PIN_FILE_VALID
} PIN_FILE_STATE;

static bool is_valid_pin(const char *pin) {
  const char prefix[] = "sha256//";
  const size_t prefixLength = sizeof(prefix) - 1;
  const size_t expectedLength = prefixLength + 44; /* SHA-256 in base64 */

  if (pin == NULL || strlen(pin) != expectedLength ||
      strncmp(pin, prefix, prefixLength) != 0) {
    return false;
  }

  for (size_t i = prefixLength; i < expectedLength; ++i) {
    unsigned char c = (unsigned char)pin[i];
    if (!(isalnum(c) || c == '+' || c == '/' ||
          (c == '=' && i == expectedLength - 1))) {
      return false;
    }
  }
  return true;
}

static int apply_server_pin(void) {
  if (curl == NULL) {
    gs_error = "The host connection is not initialized";
    return GS_INVALID;
  }
#if LIBCURL_VERSION_NUM >= 0x072700
  CURLcode result = curl_easy_setopt(
      curl, CURLOPT_PINNEDPUBLICKEY, serverPin[0] ? serverPin : NULL);
  if (result != CURLE_OK) {
    gs_error = "This Vita build cannot enable Sunshine certificate pinning";
    return GS_FAILED;
  }
  return GS_OK;
#else
  gs_error = "The Vita TLS library is too old for secure Sunshine pairing";
  return GS_FAILED;
#endif
}

static int build_pin_path(char *path, size_t pathSize, const char *suffix) {
  int written = snprintf(
      path, pathSize, "%s/%s%s", keyDirectoryPath,
      SERVER_PIN_FILE_NAME, suffix == NULL ? "" : suffix);
  return written > 0 && (size_t)written < pathSize ? GS_OK : GS_FAILED;
}

static PIN_FILE_STATE read_pin_file(
    const char *path, char pin[SPKI_PIN_BUFFER_SIZE]) {
  FILE *file = fopen(path, "rb");
  if (file == NULL) return PIN_FILE_MISSING;

  char storedPin[SPKI_PIN_BUFFER_SIZE];
  if (fgets(storedPin, sizeof(storedPin), file) == NULL) {
    fclose(file);
    return PIN_FILE_INVALID;
  }

  bool completeLine = strchr(storedPin, '\n') != NULL || feof(file);
  bool hasTrailingData = completeLine && fgetc(file) != EOF;
  fclose(file);
  if (!completeLine || hasTrailingData) return PIN_FILE_INVALID;

  storedPin[strcspn(storedPin, "\r\n")] = '\0';
  if (!is_valid_pin(storedPin)) return PIN_FILE_INVALID;

  snprintf(pin, SPKI_PIN_BUFFER_SIZE, "%s", storedPin);
  return PIN_FILE_VALID;
}

int http_reload_server_pin(void) {
  char pinPath[HTTP_PATH_MAX];
  char backupPath[HTTP_PATH_MAX];
  if (build_pin_path(pinPath, sizeof(pinPath), NULL) != GS_OK ||
      build_pin_path(backupPath, sizeof(backupPath), ".bak") != GS_OK) {
    gs_error = "Server identity file path is too long";
    return GS_FAILED;
  }

  serverPin[0] = '\0';
  PIN_FILE_STATE primaryState = read_pin_file(pinPath, serverPin);
  if (primaryState == PIN_FILE_VALID) return apply_server_pin();

  PIN_FILE_STATE backupState = read_pin_file(backupPath, serverPin);
  if (backupState == PIN_FILE_VALID) {
    /*
     * A crash can leave either a missing or incomplete primary. Keep using the
     * authenticated backup even if Vita's rename cannot repair it immediately;
     * the next load will retry the same recovery path.
     */
    remove(pinPath);
    rename(backupPath, pinPath);
    return apply_server_pin();
  }

  int clearResult = apply_server_pin();
  if (clearResult != GS_OK) return clearResult;
  if (primaryState == PIN_FILE_MISSING && backupState == PIN_FILE_MISSING) {
    return GS_OK;
  }

  gs_error = "The saved Sunshine identity is invalid; remove this PC and pair it again";
  return GS_INVALID;
}

static int persist_server_pin(void) {
  char pinPath[HTTP_PATH_MAX];
  char temporaryPath[HTTP_PATH_MAX];
  char backupPath[HTTP_PATH_MAX];
  if (build_pin_path(pinPath, sizeof(pinPath), NULL) != GS_OK ||
      build_pin_path(temporaryPath, sizeof(temporaryPath), ".tmp") != GS_OK ||
      build_pin_path(backupPath, sizeof(backupPath), ".bak") != GS_OK) {
    gs_error = "Server identity file path is too long";
    return GS_FAILED;
  }

  FILE *file = fopen(temporaryPath, "wb");
  if (file == NULL) {
    gs_error = "Could not save the paired Sunshine identity";
    return GS_IO_ERROR;
  }

  bool writeOk = fprintf(file, "%s\n", serverPin) > 0;
  bool closeOk = fclose(file) == 0;
  if (!writeOk || !closeOk) {
    remove(temporaryPath);
    gs_error = "Could not finish saving the paired Sunshine identity";
    return GS_IO_ERROR;
  }

  /* Vita's filesystem does not guarantee replacement-by-rename. */
  remove(backupPath);
  bool hadPreviousPin = rename(pinPath, backupPath) == 0;
  if (rename(temporaryPath, pinPath) != 0) {
    remove(temporaryPath);
    if (hadPreviousPin) rename(backupPath, pinPath);
    gs_error = "Could not install the paired Sunshine identity";
    return GS_IO_ERROR;
  }
  if (hadPreviousPin) remove(backupPath);
  return GS_OK;
}

int http_set_server_pin_from_pem(const char *certificatePem, bool persist) {
  if (curl == NULL || certificatePem == NULL) {
    return GS_INVALID;
  }

  int ret = GS_FAILED;
  BIO *bio = BIO_new_mem_buf((void *)certificatePem, -1);
  X509 *certificate = NULL;
  EVP_PKEY *publicKey = NULL;
  unsigned char *der = NULL;
  if (bio == NULL ||
      (certificate = PEM_read_bio_X509(bio, NULL, NULL, NULL)) == NULL ||
      (publicKey = X509_get_pubkey(certificate)) == NULL) {
    gs_error = "Sunshine returned an invalid pairing certificate";
    goto cleanup;
  }

  int derLength = i2d_PUBKEY(publicKey, &der);
  if (derLength <= 0 || der == NULL) {
    gs_error = "Could not read Sunshine's pairing identity";
    goto cleanup;
  }

  unsigned char digest[SHA256_DIGEST_LENGTH];
  unsigned char encoded[48];
  SHA256(der, (size_t)derLength, digest);
  int encodedLength = EVP_EncodeBlock(encoded, digest, sizeof(digest));
  if (encodedLength != 44) {
    gs_error = "Could not encode Sunshine's pairing identity";
    goto cleanup;
  }

  int written = snprintf(
      serverPin, sizeof(serverPin), "sha256//%.*s", encodedLength, encoded);
  if (written <= 0 || (size_t)written >= sizeof(serverPin) ||
      apply_server_pin() != GS_OK) {
    serverPin[0] = '\0';
    goto cleanup;
  }

  ret = persist ? persist_server_pin() : GS_OK;

cleanup:
  if (der != NULL) OPENSSL_free(der);
  if (publicKey != NULL) EVP_PKEY_free(publicKey);
  if (certificate != NULL) X509_free(certificate);
  if (bio != NULL) BIO_free(bio);
  return ret;
}

bool http_has_server_pin(void) {
  return serverPin[0] != '\0';
}

static size_t _write_curl(void *contents, size_t size, size_t nmemb, void *userp)
{
  if (size != 0 && nmemb > SIZE_MAX / size) {
    responseTooLarge = true;
    return 0;
  }
  size_t realsize = size * nmemb;
  PHTTP_DATA mem = (PHTTP_DATA)userp;

  if (realsize > HTTP_MAX_RESPONSE_SIZE ||
      mem->size > HTTP_MAX_RESPONSE_SIZE - realsize) {
    responseTooLarge = true;
    return 0;
  }

  char *expanded = realloc(mem->memory, mem->size + realsize + 1);
  if (expanded == NULL)
    return 0;
  mem->memory = expanded;

  memcpy(&(mem->memory[mem->size]), contents, realsize);
  mem->size += realsize;
  mem->memory[mem->size] = 0;

  return realsize;
}

int http_init(const char* keyDirectory, int logLevel) {
  http_cleanup();
  if (keyDirectory == NULL) {
    gs_error = "Pairing data path is missing";
    return GS_INVALID;
  }
  curl = curl_easy_init();
  debug = logLevel >= 2;
  if (!curl)
    return GS_FAILED;

  int keyDirectoryLength = snprintf(
      keyDirectoryPath, sizeof(keyDirectoryPath), "%s", keyDirectory);
  if (keyDirectoryLength < 0 ||
      (size_t)keyDirectoryLength >= sizeof(keyDirectoryPath)) {
    gs_error = "Pairing data path is too long";
    http_cleanup();
    return GS_FAILED;
  }

  char certificateFilePath[4096];
  int certificatePathLength = snprintf(
      certificateFilePath, sizeof(certificateFilePath), "%s/%s",
      keyDirectory, CERTIFICATE_FILE_NAME);

  char keyFilePath[4096];
  int keyPathLength = snprintf(
      keyFilePath, sizeof(keyFilePath), "%s/%s", keyDirectory, KEY_FILE_NAME);
  if (certificatePathLength < 0 ||
      (size_t)certificatePathLength >= sizeof(certificateFilePath) ||
      keyPathLength < 0 || (size_t)keyPathLength >= sizeof(keyFilePath)) {
    gs_error = "Pairing credential path is too long";
    http_cleanup();
    return GS_FAILED;
  }

  curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 0L);
  curl_easy_setopt(curl, CURLOPT_SSLENGINE_DEFAULT, 1L);
  curl_easy_setopt(curl, CURLOPT_SSLCERTTYPE,"PEM");
  curl_easy_setopt(curl, CURLOPT_SSLCERT, certificateFilePath);
  curl_easy_setopt(curl, CURLOPT_SSLKEYTYPE, "PEM");
  curl_easy_setopt(curl, CURLOPT_SSLKEY, keyFilePath);
  curl_easy_setopt(curl, CURLOPT_SSL_VERIFYPEER, 0L);
  curl_easy_setopt(curl, CURLOPT_WRITEFUNCTION, _write_curl);
  curl_easy_setopt(curl, CURLOPT_FAILONERROR, 1L);
  curl_easy_setopt(curl, CURLOPT_SSL_SESSIONID_CACHE, 0L);
  curl_easy_setopt(curl, CURLOPT_FOLLOWLOCATION, 0L);
  curl_easy_setopt(curl, CURLOPT_NOSIGNAL, 1L);
  curl_easy_setopt(curl, CURLOPT_CONNECTTIMEOUT, 10L);
  curl_easy_setopt(curl, CURLOPT_TIMEOUT, HTTP_TIMEOUT_ORDINARY_SECONDS);

  int ret = http_reload_server_pin();
  if (ret != GS_OK) http_cleanup();
  return ret;
}

static const char *request_path(const char *url) {
  const char *scheme = strstr(url, "://");
  const char *path = scheme == NULL ? url : strchr(scheme + 3, '/');
  return path == NULL ? "/" : path;
}

int http_request_with_timeout(char* url, PHTTP_DATA data, long timeoutSeconds) {
  if (curl == NULL || url == NULL || data == NULL) {
    gs_error = "The host connection is not initialized";
    return GS_INVALID;
  }
  if (strncmp(url, "https://", 8) == 0 && !http_has_server_pin()) {
    gs_error = "Secure pairing is required before using Sunshine HTTPS";
    return GS_WRONG_STATE;
  }

  free(data->memory);
  data->memory = malloc(1);
  if (data->memory == NULL) {
    data->size = 0;
    return GS_OUT_OF_MEMORY;
  }
  data->memory[0] = '\0';
  data->size = 0;
  responseTooLarge = false;

  if (timeoutSeconds < 0 ||
      curl_easy_setopt(curl, CURLOPT_TIMEOUT, timeoutSeconds) != CURLE_OK) {
    gs_error = "The host request timeout could not be configured";
    return GS_FAILED;
  }

  curl_easy_setopt(curl, CURLOPT_WRITEDATA, data);
  curl_easy_setopt(curl, CURLOPT_URL, url);
//#ifdef __FreeBSD__
  curl_easy_setopt(curl, CURLOPT_FORBID_REUSE, 1);
//#endif

  if (debug) {
    const char *path = request_path(url);
    size_t pathLength = strcspn(path, "?");
    printf("GameStream HTTP request: %.*s\n", (int)pathLength, path);
  }
  CURLcode res = curl_easy_perform(curl);

  if(res != CURLE_OK) {
    if (responseTooLarge) {
      gs_error = "Sunshine returned an unexpectedly large response";
#if LIBCURL_VERSION_NUM >= 0x072700
    } else if (res == CURLE_SSL_PINNEDPUBKEYNOTMATCH) {
      gs_error = "Sunshine's identity changed. Re-pair only if you expected its certificate to change";
#endif
    } else {
      gs_error = curl_easy_strerror(res);
    }
    return GS_FAILED;
  } else if (data->memory == NULL) {
    return GS_OUT_OF_MEMORY;
  }

  if (debug) {
    long responseCode = 0;
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, &responseCode);
    printf("GameStream HTTP response: status=%ld bytes=%u\n",
           responseCode, (unsigned int)data->size);
  }

  return GS_OK;
}

int http_request(char* url, PHTTP_DATA data) {
  return http_request_with_timeout(
      url, data, HTTP_TIMEOUT_ORDINARY_SECONDS);
}

void http_cleanup() {
  if (curl != NULL) {
    curl_easy_cleanup(curl);
    curl = NULL;
  }
  serverPin[0] = '\0';
  keyDirectoryPath[0] = '\0';
}

PHTTP_DATA http_create_data() {
  PHTTP_DATA data = malloc(sizeof(HTTP_DATA));
  if (data == NULL)
    return NULL;

  data->memory = malloc(1);
  if(data->memory == NULL) {
    free(data);
    return NULL;
  }
  data->memory[0] = '\0';
  data->size = 0;

  return data;
}

void http_free_data(PHTTP_DATA data) {
  if (data != NULL) {
    if (data->memory != NULL)
      free(data->memory);

    free(data);
  }
}
