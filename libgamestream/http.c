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
#include "crypto.h"
#include "bridge_protocol.h"

#include <errno.h>
#include <stdbool.h>
#include <stdint.h>
#include <stdio.h>
#include <string.h>
#include <sys/stat.h>
#include <pthread.h>
#include <curl/curl.h>

#define HTTP_PATH_MAX 1024
#define HTTP_MAX_RESPONSE_SIZE (1024 * 1024)
#define SPKI_PIN_BUFFER_SIZE 64

static CURL *curl;
static bool debug;
static bool responseTooLarge;
static size_t responseSizeLimit = HTTP_MAX_RESPONSE_SIZE;
static char keyDirectoryPath[HTTP_PATH_MAX];
static char serverPin[SPKI_PIN_BUFFER_SIZE];
/* The media transport does not use this handle, but the lease heartbeat and
 * GameStream control requests can originate on different Vita threads. Keep
 * every easy-handle operation serialized without blocking video decode. */
static pthread_mutex_t httpRequestMutex = PTHREAD_MUTEX_INITIALIZER;

typedef enum _PIN_FILE_STATE {
  PIN_FILE_ABSENT,
  PIN_FILE_INVALID,
  PIN_FILE_IO_ERROR,
  PIN_FILE_VALID
} PIN_FILE_STATE;

bool http_server_pin_is_valid(const char *pin) {
  const char prefix[] = "sha256//";
  const size_t prefixLength = sizeof(prefix) - 1;
  const size_t expectedLength = prefixLength + 44; /* SHA-256 in base64 */

  if (pin == NULL || strlen(pin) != expectedLength ||
      strncmp(pin, prefix, prefixLength) != 0) {
    return false;
  }

  for (size_t i = prefixLength; i + 1 < expectedLength; ++i) {
    unsigned char c = (unsigned char)pin[i];
    if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z') ||
          (c >= 'a' && c <= 'z') || c == '+' || c == '/')) {
      return false;
    }
  }
  return pin[expectedLength - 1] == '=';
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
  struct stat status;
  if (stat(path, &status) != 0) {
    return errno == ENOENT ? PIN_FILE_ABSENT : PIN_FILE_IO_ERROR;
  }
  if (!S_ISREG(status.st_mode)) return PIN_FILE_INVALID;

  FILE *file = fopen(path, "rb");
  if (file == NULL) return PIN_FILE_IO_ERROR;

  char storedPin[SPKI_PIN_BUFFER_SIZE];
  if (fgets(storedPin, sizeof(storedPin), file) == NULL) {
    bool readError = ferror(file) != 0;
    bool ioError = fclose(file) != 0;
    ioError = ioError || readError;
    return ioError ? PIN_FILE_IO_ERROR : PIN_FILE_INVALID;
  }

  bool completeLine = strchr(storedPin, '\n') != NULL || feof(file);
  bool hasTrailingData = completeLine && fgetc(file) != EOF;
  bool readError = ferror(file) != 0;
  bool ioError = fclose(file) != 0;
  ioError = ioError || readError;
  if (ioError) return PIN_FILE_IO_ERROR;
  if (!completeLine || hasTrailingData) return PIN_FILE_INVALID;

  storedPin[strcspn(storedPin, "\r\n")] = '\0';
  if (!http_server_pin_is_valid(storedPin)) return PIN_FILE_INVALID;

  snprintf(pin, SPKI_PIN_BUFFER_SIZE, "%s", storedPin);
  return PIN_FILE_VALID;
}

int http_reload_server_pin(void) {
  char pinPath[HTTP_PATH_MAX];
  char backupPath[HTTP_PATH_MAX];
  char temporaryPath[HTTP_PATH_MAX];
  if (build_pin_path(pinPath, sizeof(pinPath), NULL) != GS_OK ||
      build_pin_path(backupPath, sizeof(backupPath), ".bak") != GS_OK ||
      build_pin_path(temporaryPath, sizeof(temporaryPath), ".tmp") != GS_OK) {
    gs_error = "Server identity file path is too long";
    return GS_FAILED;
  }

  serverPin[0] = '\0';
  char primaryPin[SPKI_PIN_BUFFER_SIZE] = {0};
  char temporaryPin[SPKI_PIN_BUFFER_SIZE] = {0};
  char backupPin[SPKI_PIN_BUFFER_SIZE] = {0};
  PIN_FILE_STATE primaryState = read_pin_file(pinPath, primaryPin);
  PIN_FILE_STATE temporaryState = read_pin_file(temporaryPath, temporaryPin);
  PIN_FILE_STATE backupState = read_pin_file(backupPath, backupPin);

  /* Transaction order is old primary -> .bak, then validated .tmp -> primary.
   * Therefore a valid primary is authoritative; without one, .tmp is the
   * intended next value and .bak is the last committed fallback. A torn
   * lower-authority sibling must not hide an otherwise recoverable host. */
  const char *selectedPin = primaryState == PIN_FILE_VALID ? primaryPin :
      temporaryState == PIN_FILE_VALID ? temporaryPin :
      backupState == PIN_FILE_VALID ? backupPin : NULL;
  if (selectedPin != NULL) {
    snprintf(serverPin, sizeof(serverPin), "%s", selectedPin);
    return apply_server_pin();
  }

  int clearResult = apply_server_pin();
  if (clearResult != GS_OK) return clearResult;
  if (primaryState == PIN_FILE_ABSENT &&
      temporaryState == PIN_FILE_ABSENT &&
      backupState == PIN_FILE_ABSENT) {
    return GS_OK;
  }

  gs_error = "The saved Sunshine identity files are damaged; no files were replaced";
  return GS_IO_ERROR;
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

  char primaryPin[SPKI_PIN_BUFFER_SIZE] = {0};
  char temporaryPin[SPKI_PIN_BUFFER_SIZE] = {0};
  char backupPin[SPKI_PIN_BUFFER_SIZE] = {0};
  PIN_FILE_STATE primaryState = read_pin_file(pinPath, primaryPin);
  PIN_FILE_STATE temporaryState = read_pin_file(temporaryPath, temporaryPin);
  PIN_FILE_STATE backupState = read_pin_file(backupPath, backupPin);

  const char *recoveryPath = temporaryState == PIN_FILE_VALID ? temporaryPath :
      backupState == PIN_FILE_VALID ? backupPath : NULL;
  bool hasValidPin = primaryState == PIN_FILE_VALID || recoveryPath != NULL;
  bool hasAnyArtifact = primaryState != PIN_FILE_ABSENT ||
      temporaryState != PIN_FILE_ABSENT || backupState != PIN_FILE_ABSENT;
  if (!hasValidPin && hasAnyArtifact) {
    gs_error = "The saved Sunshine identity files are damaged; authenticated recovery data was preserved";
    return GS_IO_ERROR;
  }
  if (primaryState != PIN_FILE_VALID && recoveryPath != NULL) {
    bool recoveredTemporary = recoveryPath == temporaryPath;
    if ((primaryState != PIN_FILE_ABSENT && remove(pinPath) != 0) ||
        rename(recoveryPath, pinPath) != 0) {
      gs_error = "Could not normalize the current Sunshine identity";
      return GS_IO_ERROR;
    }
    primaryState = PIN_FILE_VALID;
    if (recoveredTemporary) {
      temporaryState = PIN_FILE_ABSENT;
    } else {
      backupState = PIN_FILE_ABSENT;
    }
  }
  if (temporaryState != PIN_FILE_ABSENT && remove(temporaryPath) != 0) {
    gs_error = "Could not replace the staged Sunshine identity";
    return GS_IO_ERROR;
  }
  temporaryState = PIN_FILE_ABSENT;

  FILE *file = fopen(temporaryPath, "wb");
  if (file == NULL) {
    gs_error = "Could not save the paired Sunshine identity";
    return GS_IO_ERROR;
  }

  bool writeOk = fprintf(file, "%s\n", serverPin) > 0 && fflush(file) == 0;
  bool closeOk = fclose(file) == 0;
  if (!writeOk || !closeOk) {
    gs_error = "Could not finish saving the paired Sunshine identity";
    return GS_IO_ERROR;
  }

  char stagedPin[SPKI_PIN_BUFFER_SIZE];
  if (read_pin_file(temporaryPath, stagedPin) != PIN_FILE_VALID ||
      strcmp(stagedPin, serverPin) != 0) {
    gs_error = "The staged Sunshine identity did not verify";
    return GS_IO_ERROR;
  }

  /* Vita's filesystem does not guarantee replacement-by-rename. */
  bool hadPreviousPin = primaryState == PIN_FILE_VALID;
  if (hadPreviousPin && backupState != PIN_FILE_ABSENT &&
      remove(backupPath) != 0) {
    gs_error = "Could not rotate the previous Sunshine identity backup";
    return GS_IO_ERROR;
  }
  if (hadPreviousPin && rename(pinPath, backupPath) != 0) {
    gs_error = "Could not preserve the previous Sunshine identity";
    return GS_IO_ERROR;
  }
  if (rename(temporaryPath, pinPath) != 0) {
    if (hadPreviousPin) rename(backupPath, pinPath);
    gs_error = "Could not install the paired Sunshine identity";
    return GS_IO_ERROR;
  }
  char committedPin[SPKI_PIN_BUFFER_SIZE];
  if (read_pin_file(pinPath, committedPin) != PIN_FILE_VALID ||
      strcmp(committedPin, serverPin) != 0) {
    gs_error = "The committed Sunshine identity did not verify";
    return GS_IO_ERROR;
  }
  if (hadPreviousPin) remove(backupPath);
  return GS_OK;
}

int http_set_server_pin_from_pem(const char *certificatePem, bool persist) {
  if (curl == NULL || certificatePem == NULL) {
    return GS_INVALID;
  }

  char calculatedPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  if (!gs_crypto_spki_pin_from_pem(certificatePem, calculatedPin)) {
    gs_error = "Sunshine returned an invalid pairing certificate";
    return GS_INVALID;
  }

  return http_set_server_pin(calculatedPin, persist);
}

bool http_has_server_pin(void) {
  return serverPin[0] != '\0';
}

int http_copy_server_pin(char *pin, size_t pinSize) {
  if (pin == NULL || pinSize == 0 || !http_has_server_pin()) return GS_INVALID;
  int written = snprintf(pin, pinSize, "%s", serverPin);
  return written > 0 && (size_t)written < pinSize ? GS_OK : GS_INVALID;
}

int http_set_server_pin(const char *pin, bool persist) {
  if (curl == NULL || !http_server_pin_is_valid(pin)) {
    gs_error = "The authenticated Sunshine identity is invalid";
    return GS_INVALID;
  }

  char previousPin[SPKI_PIN_BUFFER_SIZE];
  snprintf(previousPin, sizeof(previousPin), "%s", serverPin);
  snprintf(serverPin, sizeof(serverPin), "%s", pin);
  int ret = apply_server_pin();
  if (ret == GS_OK && persist) ret = persist_server_pin();
  if (ret != GS_OK) {
    snprintf(serverPin, sizeof(serverPin), "%s", previousPin);
    (void)apply_server_pin();
  }
  return ret;
}

static size_t _write_curl(void *contents, size_t size, size_t nmemb, void *userp)
{
  if (size != 0 && nmemb > SIZE_MAX / size) {
    responseTooLarge = true;
    return 0;
  }
  size_t realsize = size * nmemb;
  PHTTP_DATA mem = (PHTTP_DATA)userp;

  if (realsize > responseSizeLimit ||
      mem->size > responseSizeLimit - realsize) {
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

int http_init_with_recovery(
    const char* keyDirectory, int logLevel, const char* certificatePath,
    const char* keyPath, const char* recoveryServerPin) {
  http_cleanup();
  if (keyDirectory == NULL) {
    gs_error = "Pairing data path is missing";
    return GS_INVALID;
  }
  const curl_version_info_data *curlVersion =
      curl_version_info(CURLVERSION_NOW);
  if (curlVersion == NULL || curlVersion->ssl_version == NULL ||
      strstr(curlVersion->ssl_version, "mbedTLS") == NULL) {
    gs_error = "This Vita build does not contain the required mbedTLS network backend";
    return GS_FAILED;
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
  if (certificatePath != NULL) {
    certificatePathLength = snprintf(
        certificateFilePath, sizeof(certificateFilePath), "%s",
        certificatePath);
  }
  if (keyPath != NULL) {
    keyPathLength = snprintf(
        keyFilePath, sizeof(keyFilePath), "%s", keyPath);
  }
  if (certificatePathLength < 0 ||
      (size_t)certificatePathLength >= sizeof(certificateFilePath) ||
      keyPathLength < 0 || (size_t)keyPathLength >= sizeof(keyFilePath)) {
    gs_error = "Pairing credential path is too long";
    http_cleanup();
    return GS_FAILED;
  }

  curl_easy_setopt(curl, CURLOPT_SSL_VERIFYHOST, 0L);
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

  int ret = recoveryServerPin == NULL
      ? http_reload_server_pin()
      : http_set_server_pin(recoveryServerPin, false);
  if (ret != GS_OK) http_cleanup();
  return ret;
}

int http_init(const char* keyDirectory, int logLevel) {
  return http_init_with_recovery(
      keyDirectory, logLevel, NULL, NULL, NULL);
}

static const char *request_path(const char *url) {
  const char *scheme = strstr(url, "://");
  const char *path = scheme == NULL ? url : strchr(scheme + 3, '/');
  return path == NULL ? "/" : path;
}

static int http_request_with_timeout_locked(
    char* url, PHTTP_DATA data, long timeoutSeconds) {
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
  responseSizeLimit = HTTP_MAX_RESPONSE_SIZE;

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
      return GS_IDENTITY_CHANGED;
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

int http_request_with_timeout(char* url, PHTTP_DATA data, long timeoutSeconds) {
  pthread_mutex_lock(&httpRequestMutex);
  int result = http_request_with_timeout_locked(url, data, timeoutSeconds);
  pthread_mutex_unlock(&httpRequestMutex);
  return result;
}

HTTP_BRIDGE_RESULT http_bridge_request_with_timeout_ms(
    char *url, PHTTP_DATA data, long *responseCode, long timeoutMs) {
  pthread_mutex_lock(&httpRequestMutex);
  HTTP_BRIDGE_RESULT bridgeResult = HTTP_BRIDGE_RESULT_FAILED;
  if (curl == NULL || url == NULL || data == NULL || responseCode == NULL) {
    gs_error = "The secure Vita host bridge is not initialized";
    goto finished;
  }
  if (!http_has_server_pin()) {
    gs_error = "Secure pairing is required before using the Vita host bridge";
    goto finished;
  }
  if (timeoutMs <= 0) {
    gs_error = "The Vita host bridge timeout is invalid";
    goto finished;
  }

  free(data->memory);
  data->memory = malloc(1);
  if (data->memory == NULL) {
    data->size = 0;
    goto finished;
  }
  data->memory[0] = '\0';
  data->size = 0;
  responseTooLarge = false;
  responseSizeLimit = VITA_STREAM_BOUNDARY_MAX_RESPONSE_BYTES;
  *responseCode = 0;

  CURLcode optionResult = CURLE_OK;
#define SET_BRIDGE_OPTION(option, value) do { \
  CURLcode setResult = curl_easy_setopt(curl, (option), (value)); \
  if (setResult != CURLE_OK && optionResult == CURLE_OK) optionResult = setResult; \
} while (0)
  SET_BRIDGE_OPTION(CURLOPT_CONNECTTIMEOUT_MS, 1000L);
  SET_BRIDGE_OPTION(CURLOPT_TIMEOUT_MS, timeoutMs);
  SET_BRIDGE_OPTION(CURLOPT_FAILONERROR, 0L);
  SET_BRIDGE_OPTION(CURLOPT_WRITEDATA, data);
  SET_BRIDGE_OPTION(CURLOPT_URL, url);
  SET_BRIDGE_OPTION(CURLOPT_FORBID_REUSE, 1L);
  if (optionResult != CURLE_OK) {
    gs_error = "The Vita host bridge request could not be configured";
  }

  CURLcode result = optionResult == CURLE_OK
      ? curl_easy_perform(curl) : optionResult;
  curl_off_t appConnectTime = 0;
  if (result != CURLE_OK) {
    (void)curl_easy_getinfo(
        curl, CURLINFO_APPCONNECT_TIME_T, &appConnectTime);
  }
  if (result == CURLE_OK) {
    curl_easy_getinfo(curl, CURLINFO_RESPONSE_CODE, responseCode);
  }

  /* Restore the shared GameStream handle before interpreting the result. */
  CURLcode restoreResult = CURLE_OK;
#define RESTORE_BRIDGE_OPTION(option, value) do { \
  CURLcode setResult = curl_easy_setopt(curl, (option), (value)); \
  if (setResult != CURLE_OK && restoreResult == CURLE_OK) restoreResult = setResult; \
} while (0)
  RESTORE_BRIDGE_OPTION(CURLOPT_CONNECTTIMEOUT, 10L);
  RESTORE_BRIDGE_OPTION(CURLOPT_TIMEOUT, HTTP_TIMEOUT_ORDINARY_SECONDS);
  RESTORE_BRIDGE_OPTION(CURLOPT_FAILONERROR, 1L);
  RESTORE_BRIDGE_OPTION(CURLOPT_FORBID_REUSE, 0L);
  responseSizeLimit = HTTP_MAX_RESPONSE_SIZE;
#undef RESTORE_BRIDGE_OPTION
#undef SET_BRIDGE_OPTION
  if (restoreResult != CURLE_OK) {
    gs_error = "The Vita host bridge could not restore the GameStream HTTP state";
    goto finished;
  }

  STREAM_BOUNDARY_TRANSPORT_FAILURE failure =
      result == CURLE_COULDNT_CONNECT
          ? STREAM_BOUNDARY_TRANSPORT_REFUSED
          : result == CURLE_OPERATION_TIMEDOUT
              ? STREAM_BOUNDARY_TRANSPORT_TIMEOUT
              : STREAM_BOUNDARY_TRANSPORT_OTHER;
  if (stream_boundary_transport_failure_is_optional(
          failure, appConnectTime > 0)) {
    bridgeResult = HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE;
    goto finished;
  }
  if (result != CURLE_OK) {
    if (responseTooLarge) {
      gs_error = "The Vita host bridge returned an unexpectedly large response";
#if LIBCURL_VERSION_NUM >= 0x072700
    } else if (result == CURLE_SSL_PINNEDPUBKEYNOTMATCH) {
      gs_error = "The Vita host bridge did not match the paired Sunshine identity";
#endif
    } else {
      gs_error = "The reached Vita host bridge rejected its authenticated TLS connection";
    }
    goto finished;
  }
  bridgeResult = HTTP_BRIDGE_RESULT_OK;

finished:
  pthread_mutex_unlock(&httpRequestMutex);
  return bridgeResult;
}

HTTP_BRIDGE_RESULT http_bridge_request(
    char *url, PHTTP_DATA data, long *responseCode) {
  return http_bridge_request_with_timeout_ms(
      url, data, responseCode, 65000L);
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
