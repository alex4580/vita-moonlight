/*
 * This file is part of Moonlight Embedded.
 *
 * Copyright (C) 2015-2017 Iwan Timmer
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
#include "xml.h"
#include "mkcert.h"
#include "client.h"
#include "errors.h"
#include "limits.h"

#include <Limelight.h>

#include <sys/stat.h>
#include <ctype.h>
#include <errno.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <arpa/inet.h>
#include <uuid.h>
#include <openssl/sha.h>
#include <openssl/aes.h>
#include <openssl/rand.h>
#include <openssl/evp.h>
#include <openssl/x509.h>
#include <openssl/pem.h>
#include <openssl/err.h>
#include <psp2/kernel/threadmgr.h>

#include "../src/debug.h"

#define UNIQUE_FILE_NAME "uniqueid.dat"
#define P12_FILE_NAME "client.p12"
#define LEGACY_SHARED_UNIQUE_ID "0123456789ABCDEF"

//
#define printf vita_debug_log
//

#define UNIQUEID_BYTES 8
#define UNIQUEID_CHARS (UNIQUEID_BYTES*2)

static char unique_id[UNIQUEID_CHARS+1];
static X509 *cert;
static char cert_hex[8192];
static EVP_PKEY *privateKey;

const char* gs_error;

#define LEN_AS_HEX_STR(x) ((x) * 2 + 1)
#define SIZEOF_AS_HEX_STR(x) LEN_AS_HEX_STR(sizeof(x))

#define UUID_STRLEN 37

#define PATH_MAX 1024
#define SERVERINFO_NUMERIC_TEXT_MAX 64u

static void bytes_to_hex(unsigned char *in, char *out, size_t len);

static bool constant_time_equal(
    const unsigned char *left, const unsigned char *right, size_t length) {
  volatile unsigned int difference = 0;
  for (size_t i = 0; i < length; ++i) {
    difference |= (unsigned int)(left[i] ^ right[i]);
  }
  return difference == 0;
}

static bool hash_pairing_challenge_binding(
    const unsigned char challenge[16],
    const unsigned char *serverCertificateSignature,
    size_t serverCertificateSignatureLength,
    const unsigned char serverSecret[16],
    int hashLength,
    unsigned char digest[SHA256_DIGEST_LENGTH]) {
  if (challenge == NULL || serverCertificateSignature == NULL ||
      serverCertificateSignatureLength == 0 || serverSecret == NULL ||
      digest == NULL) {
    return false;
  }

  memset(digest, 0, SHA256_DIGEST_LENGTH);
  if (hashLength == SHA256_DIGEST_LENGTH) {
    SHA256_CTX context;
    return SHA256_Init(&context) == 1 &&
        SHA256_Update(&context, challenge, 16) == 1 &&
        SHA256_Update(&context, serverCertificateSignature,
                      serverCertificateSignatureLength) == 1 &&
        SHA256_Update(&context, serverSecret, 16) == 1 &&
        SHA256_Final(digest, &context) == 1;
  }
  if (hashLength == SHA_DIGEST_LENGTH) {
    SHA_CTX context;
    return SHA1_Init(&context) == 1 &&
        SHA1_Update(&context, challenge, 16) == 1 &&
        SHA1_Update(&context, serverCertificateSignature,
                    serverCertificateSignatureLength) == 1 &&
        SHA1_Update(&context, serverSecret, 16) == 1 &&
        SHA1_Final(digest, &context) == 1;
  }
  return false;
}

static bool certificate_signature_view(
    X509 *certificate, const unsigned char **signature, size_t *signatureLength) {
  if (certificate == NULL || signature == NULL || signatureLength == NULL) {
    return false;
  }

#if OPENSSL_VERSION_NUMBER < 0x10002000L
  ASN1_BIT_STRING *asnSignature = certificate->signature;
#elif OPENSSL_VERSION_NUMBER < 0x10100000L
  ASN1_BIT_STRING *asnSignature = NULL;
  X509_get0_signature(&asnSignature, NULL, certificate);
#else
  const ASN1_BIT_STRING *asnSignature = NULL;
  X509_get0_signature(&asnSignature, NULL, certificate);
#endif

  if (asnSignature == NULL || ASN1_STRING_length(asnSignature) <= 0 ||
      ASN1_STRING_length(asnSignature) > 1024) {
    return false;
  }

#if OPENSSL_VERSION_NUMBER < 0x10100000L
  *signature = ASN1_STRING_data(asnSignature);
#else
  *signature = ASN1_STRING_get0_data(asnSignature);
#endif
  *signatureLength = (size_t)ASN1_STRING_length(asnSignature);
  return *signature != NULL;
}

static int copy_pem_certificate_signature(
    const char *certificatePem, unsigned char **signature,
    size_t *signatureLength) {
  if (certificatePem == NULL || signature == NULL || signatureLength == NULL) {
    return GS_INVALID;
  }

  *signature = NULL;
  *signatureLength = 0;
  BIO *bio = BIO_new_mem_buf((void *)certificatePem, -1);
  X509 *certificate = NULL;
  const unsigned char *signatureView = NULL;
  size_t viewLength = 0;
  if (bio == NULL ||
      (certificate = PEM_read_bio_X509(bio, NULL, NULL, NULL)) == NULL ||
      !certificate_signature_view(certificate, &signatureView, &viewLength)) {
    if (certificate != NULL) X509_free(certificate);
    if (bio != NULL) BIO_free(bio);
    gs_error = "Sunshine returned an invalid pairing certificate signature";
    return GS_INVALID;
  }

  unsigned char *copy = malloc(viewLength);
  if (copy == NULL) {
    X509_free(certificate);
    BIO_free(bio);
    return GS_OUT_OF_MEMORY;
  }
  memcpy(copy, signatureView, viewLength);
  *signature = copy;
  *signatureLength = viewLength;

  X509_free(certificate);
  BIO_free(bio);
  return GS_OK;
}

static int secure_random(void *buffer, size_t size, const char *purpose) {
  if (size > INT_MAX || RAND_bytes(buffer, (int)size) != 1) {
    (void)purpose;
    gs_error = "The Vita could not generate secure pairing data";
    return GS_FAILED;
  }
  return GS_OK;
}

static bool bounded_text_length(const char *text, size_t maximum) {
  if (text == NULL) return false;
  for (size_t i = 0; i <= maximum; ++i) {
    if (text[i] == '\0') return true;
  }
  return false;
}

static bool parse_unsigned_decimal_field(
    const char *text, unsigned long maximum, unsigned long *value) {
  if (value == NULL ||
      !bounded_text_length(text, SERVERINFO_NUMERIC_TEXT_MAX)) {
    return false;
  }

  while (*text != '\0' && isspace((unsigned char)*text)) text++;
  if (*text == '\0' || *text == '+' || *text == '-') return false;

  errno = 0;
  char *end = NULL;
  unsigned long parsed = strtoul(text, &end, 10);
  if (errno == ERANGE || end == text || parsed > maximum) return false;
  while (*end != '\0' && isspace((unsigned char)*end)) end++;
  if (*end != '\0') return false;

  *value = parsed;
  return true;
}

static bool parse_version_major_field(const char *text, int *majorVersion) {
  if (majorVersion == NULL ||
      !bounded_text_length(text, SERVERINFO_NUMERIC_TEXT_MAX)) {
    return false;
  }

  while (*text != '\0' && isspace((unsigned char)*text)) text++;
  if (*text == '\0' || *text == '+' || *text == '-') return false;

  errno = 0;
  char *end = NULL;
  unsigned long parsed = strtoul(text, &end, 10);
  if (errno == ERANGE || end == text || parsed > INT_MAX) return false;

  /* Sunshine uses a signed sentinel in versions such as 7.1.431.-1. The
   * major version must remain non-negative, but subsequent components follow
   * Moonlight common's signed-decimal version-quad contract. */
  const char *component = end;
  while (*component == '.') {
    component++;
    const char *digits = component;
    if (*digits == '-') digits++;
    if (!isdigit((unsigned char)*digits)) return false;

    errno = 0;
    char *componentEnd = NULL;
    long componentValue = strtol(component, &componentEnd, 10);
    if (errno == ERANGE || componentEnd == component ||
        componentValue < INT_MIN || componentValue > INT_MAX) {
      return false;
    }
    component = componentEnd;
  }
  while (*component != '\0' && isspace((unsigned char)*component)) component++;
  if (*component != '\0') return false;

  *majorVersion = (int)parsed;
  return true;
}

static void cleanup_client_credentials(void) {
  if (cert != NULL) {
    X509_free(cert);
    cert = NULL;
  }
  if (privateKey != NULL) {
    EVP_PKEY_free(privateKey);
    privateKey = NULL;
  }
  cert_hex[0] = '\0';
}

static int mkdirtree(const char* directory) {
  char buffer[PATH_MAX];
  char* p = buffer;

  if (directory == NULL || strlen(directory) >= sizeof(buffer)) {
    return -1;
  }

  // The passed in string could be a string literal
  // so we must copy it first
  strncpy(p, directory, PATH_MAX - 1);
  buffer[PATH_MAX - 1] = '\0';

  while (*p != 0) {
    // Find the end of the path element
    do {
      p++;
    } while (*p != 0 && *p != '/');

    char oldChar = *p;
    *p = 0;

    // Create the directory if it doesn't exist already
    if (mkdir(buffer, 0775) == -1 && errno != EEXIST) {
        return -1;
    }

    *p = oldChar;
  }

  return 0;
}

static bool build_unique_id_path(
    char *path, size_t pathSize, const char *keyDirectory,
    const char *suffix) {
  int written = snprintf(
      path, pathSize, "%s/%s%s", keyDirectory, UNIQUE_FILE_NAME,
      suffix == NULL ? "" : suffix);
  return written > 0 && (size_t)written < pathSize;
}

static bool read_unique_id_file(
    const char *path, char value[UNIQUEID_CHARS + 1]) {
  FILE *file = fopen(path, "rb");
  if (file == NULL) return false;

  size_t bytesRead = fread(value, 1, UNIQUEID_CHARS, file);
  bool valid = bytesRead == UNIQUEID_CHARS && fgetc(file) == EOF;
  fclose(file);
  for (size_t i = 0; valid && i < UNIQUEID_CHARS; ++i) {
    valid = isxdigit((unsigned char)value[i]) != 0;
  }
  value[UNIQUEID_CHARS] = '\0';
  return valid;
}

static int persist_unique_id_atomic(
    const char *uniqueFilePath,
    const char value[UNIQUEID_CHARS + 1]) {
  char temporaryPath[PATH_MAX];
  char backupPath[PATH_MAX];
  int temporaryLength = snprintf(
      temporaryPath, sizeof(temporaryPath), "%s.tmp", uniqueFilePath);
  int backupLength = snprintf(
      backupPath, sizeof(backupPath), "%s.bak", uniqueFilePath);
  if (temporaryLength <= 0 || (size_t)temporaryLength >= sizeof(temporaryPath) ||
      backupLength <= 0 || (size_t)backupLength >= sizeof(backupPath)) {
    gs_error = "The Vita pairing identity path is too long";
    return GS_FAILED;
  }

  FILE *file = fopen(temporaryPath, "wb");
  if (file == NULL) {
    gs_error = "Could not save the Vita pairing identity";
    return GS_IO_ERROR;
  }
  bool writeOk = fwrite(value, 1, UNIQUEID_CHARS, file) == UNIQUEID_CHARS;
  bool closeOk = fclose(file) == 0;
  if (!writeOk || !closeOk) {
    remove(temporaryPath);
    gs_error = "Could not finish saving the Vita pairing identity";
    return GS_IO_ERROR;
  }

  remove(backupPath);
  errno = 0;
  bool hadPreviousIdentity = rename(uniqueFilePath, backupPath) == 0;
  if (!hadPreviousIdentity && errno != ENOENT) {
    remove(temporaryPath);
    gs_error = "Could not preserve the previous Vita pairing identity";
    return GS_IO_ERROR;
  }
  if (rename(temporaryPath, uniqueFilePath) != 0) {
    remove(temporaryPath);
    if (hadPreviousIdentity) rename(backupPath, uniqueFilePath);
    gs_error = "Could not install the Vita pairing identity";
    return GS_IO_ERROR;
  }
  if (hadPreviousIdentity) remove(backupPath);
  return GS_OK;
}

static int load_unique_id(
    const char* keyDirectory, bool hasAuthenticatedServerPin) {
  char uniqueFilePath[PATH_MAX];
  char backupPath[PATH_MAX];
  if (!build_unique_id_path(
          uniqueFilePath, sizeof(uniqueFilePath), keyDirectory, NULL) ||
      !build_unique_id_path(
          backupPath, sizeof(backupPath), keyDirectory, ".bak")) {
    gs_error = "The Vita pairing identity path is too long";
    return GS_FAILED;
  }

  bool valid = read_unique_id_file(uniqueFilePath, unique_id);
  if (!valid && read_unique_id_file(backupPath, unique_id)) {
    /* Recover the last complete identity before considering any migration. */
    remove(uniqueFilePath);
    if (rename(backupPath, uniqueFilePath) != 0) {
      gs_error = "Could not recover the Vita pairing identity";
      return GS_IO_ERROR;
    }
    valid = true;
  }

  bool legacySharedIdentity =
      valid && strcmp(unique_id, LEGACY_SHARED_UNIQUE_ID) == 0;
  /*
   * The original Vita client wrote one shared ID on every installation.
   * Rotate it during the mandatory no-pin migration, but preserve it when a
   * valid pin exists because earlier secure beta builds may already have
   * paired that exact ID successfully.
   */
  bool rotateLegacyIdentity =
      legacySharedIdentity && !hasAuthenticatedServerPin;
  if (valid && !rotateLegacyIdentity) return GS_OK;

  unsigned char randomId[UNIQUEID_BYTES];
  if (secure_random(randomId, sizeof(randomId), "client identity") != GS_OK) {
    return GS_FAILED;
  }
  char generatedId[UNIQUEID_CHARS + 1];
  bytes_to_hex(randomId, generatedId, sizeof(randomId));

  int ret = persist_unique_id_atomic(uniqueFilePath, generatedId);
  if (ret != GS_OK) return ret;
  memcpy(unique_id, generatedId, sizeof(unique_id));
  return GS_OK;
}

static int load_cert(const char* keyDirectory) {
  cleanup_client_credentials();

  char certificateFilePath[PATH_MAX];
  char keyFilePath[PATH_MAX];
  char p12FilePath[PATH_MAX];
  int certificatePathLength = snprintf(
      certificateFilePath, sizeof(certificateFilePath), "%s/%s",
      keyDirectory, CERTIFICATE_FILE_NAME);
  int keyPathLength = snprintf(
      keyFilePath, sizeof(keyFilePath), "%s/%s",
      keyDirectory, KEY_FILE_NAME);
  int p12PathLength = snprintf(
      p12FilePath, sizeof(p12FilePath), "%s/%s",
      keyDirectory, P12_FILE_NAME);
  if (certificatePathLength < 0 ||
      (size_t)certificatePathLength >= sizeof(certificateFilePath) ||
      keyPathLength < 0 || (size_t)keyPathLength >= sizeof(keyFilePath) ||
      p12PathLength < 0 || (size_t)p12PathLength >= sizeof(p12FilePath)) {
    gs_error = "The Vita pairing credential path is too long";
    return GS_INVALID;
  }

  FILE *fd = fopen(certificateFilePath, "r");
  if (fd == NULL) {
    printf("Generating certificate...");
    CERT_KEY_PAIR generated = mkcert_generate();
    printf("done\n");

    if (generated.x509 == NULL || generated.pkey == NULL || generated.p12 == NULL) {
      mkcert_free(generated);
      gs_error = "Could not generate the Vita pairing certificate";
      return GS_FAILED;
    }

    if (mkcert_save(certificateFilePath, p12FilePath, keyFilePath, generated) != 0) {
      mkcert_free(generated);
      gs_error = "Could not save the Vita pairing certificate";
      return GS_IO_ERROR;
    }
    mkcert_free(generated);
    fd = fopen(certificateFilePath, "r");
  }

  if (fd == NULL) {
    gs_error = "Can't open certificate file";
    return GS_FAILED;
  }

  if (!(cert = PEM_read_X509(fd, NULL, NULL, NULL))) {
    fclose(fd);
    gs_error = "Error loading cert into memory";
    return GS_FAILED;
  }

  rewind(fd);

  int c;
  int length = 0;
  while ((c = fgetc(fd)) != EOF) {
    if ((size_t)length + 2 >= sizeof(cert_hex)) {
      fclose(fd);
      cleanup_client_credentials();
      gs_error = "The Vita pairing certificate is unexpectedly large";
      return GS_INVALID;
    }
    snprintf(cert_hex + length, sizeof(cert_hex) - (size_t)length, "%02x", c);
    length += 2;
  }
  cert_hex[length] = 0;

  fclose(fd);

  fd = fopen(keyFilePath, "r");
  if (fd == NULL) {
    cleanup_client_credentials();
    gs_error = "Error loading key into memory";
    return GS_FAILED;
  }

  privateKey = PEM_read_PrivateKey(fd, NULL, NULL, NULL);
  fclose(fd);

  if (privateKey == NULL || X509_check_private_key(cert, privateKey) != 1) {
    cleanup_client_credentials();
    gs_error = "The Vita pairing key does not match its certificate";
    return GS_INVALID;
  }

  return GS_OK;
}

static int load_serverinfo(PSERVER_DATA server, bool https) {
  uuid_t uuid;
  char uuid_str[UUID_STRLEN];
  char url[4096];
  int ret = GS_INVALID;
  char *pairedText = NULL;
  char *currentGameText = NULL;
  char *stateText = NULL;
  char *serverCodecModeSupportText = NULL;
  char *httpsPortText = NULL;
  char *macText = NULL;

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);

  snprintf(url, sizeof(url), "%s://%s:%d/serverinfo?uniqueid=%s&uuid=%s",
    https ? "https" : "http", server->serverInfo.address, https ? server->httpsPort : server->httpPort, unique_id, uuid_str);

  PHTTP_DATA data = http_create_data();
  if (data == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }
  if (http_request(url, data) != GS_OK) {
    ret = GS_IO_ERROR;
    goto cleanup;
  }

  if ((ret = xml_status(data->memory, data->size)) != GS_OK) {
    goto cleanup;
  }

  if (xml_search(data->memory, data->size, "currentgame", &currentGameText) != GS_OK) {
    goto cleanup;
  }

  if (xml_search(data->memory, data->size, "PairStatus", &pairedText) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "appversion", (char**) &server->serverInfo.serverInfoAppVersion) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "state", &stateText) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "ServerCodecModeSupport", &serverCodecModeSupportText) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "gputype", &server->gpuType) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "GsVersion", &server->gsVersion) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "GfeVersion", (char**) &server->serverInfo.serverInfoGfeVersion) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "HttpsPort", &httpsPortText) != GS_OK)
    goto cleanup;

  if (xml_search(data->memory, data->size, "mac", &macText) == GS_OK && macText != NULL) {
    strncpy(server->mac, macText, sizeof(server->mac) - 1);
    server->mac[sizeof(server->mac) - 1] = '\0';
  } else {
    server->mac[0] = '\0';
  }

  if (xml_modelist(data->memory, data->size, &server->modes) != GS_OK)
    goto cleanup;

  // These fields are present on all version of GFE that this client supports
  if (!strlen(currentGameText) || !strlen(pairedText) || !strlen(server->serverInfo.serverInfoAppVersion) || !strlen(stateText))
    goto cleanup;

  unsigned long currentGame = 0;
  unsigned long codecModeSupport = SCM_H264;
  unsigned long httpsPort = 0;
  if (!parse_unsigned_decimal_field(currentGameText, INT_MAX, &currentGame)) {
    gs_error = "Sunshine returned an invalid currentgame field";
    ret = GS_INVALID;
    goto cleanup;
  }
  if (serverCodecModeSupportText[0] != '\0' &&
      !parse_unsigned_decimal_field(
          serverCodecModeSupportText, INT_MAX, &codecModeSupport)) {
    gs_error = "Sunshine returned an invalid ServerCodecModeSupport field";
    ret = GS_INVALID;
    goto cleanup;
  }
  if (httpsPortText[0] != '\0' &&
      !parse_unsigned_decimal_field(httpsPortText, 65535u, &httpsPort)) {
    gs_error = "Sunshine returned an invalid HttpsPort field";
    ret = GS_INVALID;
    goto cleanup;
  }
  if (!parse_version_major_field(
          server->serverInfo.serverInfoAppVersion,
          &server->serverMajorVersion)) {
    gs_error = "Sunshine returned an invalid appversion field";
    ret = GS_INVALID;
    goto cleanup;
  }

  server->paired = strcmp(pairedText, "1") == 0;
  server->currentGame = (int)currentGame;
  server->serverInfo.serverCodecModeSupport = (int)codecModeSupport;
  server->isNvidiaSoftware = strstr(stateText, "MJOLNIR") != NULL;

  server->httpsPort = httpsPort == 0 ? 47984 : (unsigned short)httpsPort;

  if (strstr(stateText, "_SERVER_BUSY") == NULL) {
    // After GFE 2.8, current game remains set even after streaming
    // has ended. We emulate the old behavior by forcing it to zero
    // if streaming is not active.
    server->currentGame = 0;
  }
  ret = GS_OK;

  cleanup:

  if (data != NULL)
    http_free_data(data);

  if (pairedText != NULL)
    free(pairedText);

  if (currentGameText != NULL)
    free(currentGameText);

  if (stateText != NULL)
    free(stateText);

  if (serverCodecModeSupportText != NULL)
    free(serverCodecModeSupportText);

  if (httpsPortText != NULL)
    free(httpsPortText);

  if (macText != NULL)
    free(macText);

  return ret;
}

static void free_server_status_data(PSERVER_DATA server);

static int load_server_status(PSERVER_DATA server) {
  int ret;
  bool hasAuthenticatedServerPin = http_has_server_pin();

  /*
   * Unpinned clients must always refresh discovery over HTTP. gs_refresh()
   * carries the known HTTPS port into a fresh structure, but none of the other
   * server fields; skipping this request would return a blank server record.
   */
  if (!server->httpsPort || !hasAuthenticatedServerPin) {
    if (!hasAuthenticatedServerPin) free_server_status_data(server);
    ret = load_serverinfo(server, false);
    if (ret != GS_OK) {
      free_server_status_data(server);
      return ret;
    }
  }

  if (!hasAuthenticatedServerPin) {
    /*
     * Existing Vita installs did not authenticate Sunshine's HTTPS endpoint.
     * Keep HTTP discovery available, but require one new PIN exchange before
     * any privileged request or stream launch. This safely migrates upgrades.
     */
    server->paired = false;
    server->securePairingRequired = true;
    ret = GS_OK;
  } else {
    // Never fall back to unauthenticated HTTP after a pinned HTTPS failure.
    free_server_status_data(server);
    ret = load_serverinfo(server, true);
    server->securePairingRequired = false;
  }

  if (ret == GS_OK && !server->allowUnsupportedVersion) {
    if (server->serverMajorVersion > MAX_SUPPORTED_GFE_VERSION) {
      gs_error = "Update Vita Moonlight or use a supported Sunshine/Apollo host version and try again";
      ret = GS_UNSUPPORTED_VERSION;
    } else if (server->serverMajorVersion < MIN_SUPPORTED_GFE_VERSION) {
      gs_error = "Vita Moonlight requires a newer supported Sunshine/Apollo host version.";
      ret = GS_UNSUPPORTED_VERSION;
    }
  }

  if (ret != GS_OK) {
    free_server_status_data(server);
  }
  return ret;
}

static void free_server_status_data(PSERVER_DATA server) {
  if (server == NULL) return;

  free(server->gpuType);
  server->gpuType = NULL;
  free(server->gsVersion);
  server->gsVersion = NULL;
  free((void *)server->serverInfo.serverInfoAppVersion);
  server->serverInfo.serverInfoAppVersion = NULL;
  free((void *)server->serverInfo.serverInfoGfeVersion);
  server->serverInfo.serverInfoGfeVersion = NULL;
  free((void *)server->serverInfo.rtspSessionUrl);
  server->serverInfo.rtspSessionUrl = NULL;

  PDISPLAY_MODE mode = server->modes;
  while (mode != NULL) {
    PDISPLAY_MODE next = mode->next;
    free(mode);
    mode = next;
  }
  server->modes = NULL;
}

static void bytes_to_hex(unsigned char *in, char *out, size_t len) {
  for (int i = 0; i < len; i++) {
    sprintf(out + i * 2, "%02x", in[i]);
  }
  out[len * 2] = 0;
}

static bool hex_to_bytes(const char *in, unsigned char* out, size_t outputLength) {
  if (in == NULL || strlen(in) != outputLength * 2) {
    return false;
  }
  for (size_t count = 0; count < outputLength; ++count) {
    unsigned char high = (unsigned char)in[count * 2];
    unsigned char low = (unsigned char)in[count * 2 + 1];
    if (!isxdigit(high) || !isxdigit(low) ||
        sscanf(&in[count * 2], "%2hhx", &out[count]) != 1) {
      return false;
    }
  }
  return true;
}

static int sign_it(const char *msg, size_t mlen, unsigned char **sig, size_t *slen, EVP_PKEY *pkey) {
  int result = GS_FAILED;

  *sig = NULL;
  *slen = 0;

  EVP_MD_CTX *ctx = EVP_MD_CTX_create();
  if (ctx == NULL)
    return GS_FAILED;

  int rc = EVP_DigestSignInit(ctx, NULL, EVP_sha256(), NULL, pkey);
  if (rc != 1)
    goto cleanup;

  rc = EVP_DigestSignUpdate(ctx, msg, mlen);
  if (rc != 1)
    goto cleanup;

  size_t req = 0;
  rc = EVP_DigestSignFinal(ctx, NULL, &req);
  if (rc != 1 || !(req > 0))
    goto cleanup;

  *sig = OPENSSL_malloc(req);
  if (*sig == NULL)
    goto cleanup;

  *slen = req;
  rc = EVP_DigestSignFinal(ctx, *sig, slen);
  if (rc != 1 || req != *slen)
    goto cleanup;

  result = GS_OK;

  cleanup:
  if (result != GS_OK && *sig != NULL) {
    OPENSSL_free(*sig);
    *sig = NULL;
    *slen = 0;
  }
  EVP_MD_CTX_destroy(ctx);
  ctx = NULL;

  return result;
}

static bool verifySignature(const char *data, int dataLength, char *signature, int signatureLength, const char *cert) {
    X509* x509 = NULL;
    BIO* bio = BIO_new(BIO_s_mem());
    if (bio == NULL || BIO_puts(bio, cert) <= 0) {
        BIO_free(bio);
        return false;
    }
    x509 = PEM_read_bio_X509(bio, NULL, NULL, NULL);

    BIO_free(bio);

    if (!x509) {
        return false;
    }

    EVP_PKEY* pubKey = X509_get_pubkey(x509);
    EVP_MD_CTX *mdctx = EVP_MD_CTX_create();
    int result = pubKey != NULL && mdctx != NULL &&
        EVP_DigestVerifyInit(mdctx, NULL, EVP_sha256(), NULL, pubKey) == 1 &&
        EVP_DigestVerifyUpdate(mdctx, data, dataLength) == 1 &&
        EVP_DigestVerifyFinal(mdctx, signature, signatureLength) == 1;

    X509_free(x509);
    EVP_PKEY_free(pubKey);
    if (mdctx != NULL) EVP_MD_CTX_destroy(mdctx);

    return result;
}

static bool encrypt(const unsigned char *plaintext, int plaintextLen, const unsigned char *key, unsigned char *ciphertext) {
  EVP_CIPHER_CTX* cipher = EVP_CIPHER_CTX_new();
  if (cipher == NULL) return false;

  if (EVP_EncryptInit(cipher, EVP_aes_128_ecb(), key, NULL) != 1 ||
      EVP_CIPHER_CTX_set_padding(cipher, 0) != 1) {
    EVP_CIPHER_CTX_free(cipher);
    return false;
  }

  int ciphertextLen = 0;
  bool ok = EVP_EncryptUpdate(
      cipher, ciphertext, &ciphertextLen, plaintext, plaintextLen) == 1 &&
      ciphertextLen == plaintextLen;

  EVP_CIPHER_CTX_free(cipher);
  return ok;
}

static bool decrypt(const unsigned char *ciphertext, int ciphertextLen, const unsigned char *key, unsigned char *plaintext) {
  EVP_CIPHER_CTX* cipher = EVP_CIPHER_CTX_new();
  if (cipher == NULL) return false;

  if (EVP_DecryptInit(cipher, EVP_aes_128_ecb(), key, NULL) != 1 ||
      EVP_CIPHER_CTX_set_padding(cipher, 0) != 1) {
    EVP_CIPHER_CTX_free(cipher);
    return false;
  }

  int plaintextLen = 0;
  bool ok = EVP_DecryptUpdate(
      cipher, plaintext, &plaintextLen, ciphertext, ciphertextLen) == 1 &&
      plaintextLen == ciphertextLen;

  EVP_CIPHER_CTX_free(cipher);
  return ok;
}

int gs_unpair(PSERVER_DATA server) {
  int ret = GS_OK;
  char url[4096];
  uuid_t uuid;
  char uuid_str[UUID_STRLEN];
  PHTTP_DATA data = http_create_data();
  if (data == NULL)
    return GS_OUT_OF_MEMORY;

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, sizeof(url), "http://%s:%u/unpair?uniqueid=%s&uuid=%s", server->serverInfo.address, server->httpPort, unique_id, uuid_str);
  ret = http_request(url, data);

  http_free_data(data);
  return ret;
}

int gs_pair(PSERVER_DATA server, char* pin) {
  const size_t urlSize = 16384;
  int ret = GS_OK;
  char *result = NULL;
  char *url = NULL;
  char *plaincert = NULL;
  unsigned char *pairingSecret = NULL;
  unsigned char *serverCertificateSignature = NULL;
  size_t serverCertificateSignatureLength = 0;
  unsigned char *signature = NULL;
  unsigned char *clientPairingSecret = NULL;
  char *clientPairingSecretHex = NULL;
  PHTTP_DATA data = NULL;
  uuid_t uuid;
  char uuid_str[UUID_STRLEN];
  bool ephemeralPinInstalled = false;
  bool pairingStarted = false;

  if (server == NULL || pin == NULL || strlen(pin) != 4) {
    gs_error = "Pairing requires a four-digit PIN";
    return GS_INVALID;
  }
  for (int i = 0; i < 4; ++i) {
    if (!isdigit((unsigned char)pin[i])) {
      gs_error = "Pairing requires a four-digit PIN";
      return GS_INVALID;
    }
  }
  if (server->paired) {
    gs_error = "Already paired";
    return GS_WRONG_STATE;
  }

  url = malloc(urlSize);
  data = http_create_data();
  if (url == NULL || data == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }

  unsigned char salt_data[16];
  char salt_hex[SIZEOF_AS_HEX_STR(salt_data)];
  if (secure_random(salt_data, sizeof(salt_data), "pairing salt") != GS_OK) {
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(salt_data, salt_hex, sizeof(salt_data));

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&phrase=getservercert&salt=%s&clientcert=%s", server->serverInfo.address, server->httpPort, unique_id, uuid_str, salt_hex, cert_hex);
  pairingStarted = true;
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)) != GS_OK) {
    goto cleanup;
  }
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    gs_error = "Sunshine rejected the pairing request";
    ret = GS_FAILED;
    goto cleanup;
  }

  free(result);
  result = NULL;
  if ((ret = xml_search(data->memory, data->size, "plaincert", &result)) != GS_OK) goto cleanup;
  size_t certificateHexLength = strlen(result);
  if (certificateHexLength == 0 || (certificateHexLength & 1) != 0 ||
      certificateHexLength / 2 > 8192) {
    gs_error = "Sunshine returned an invalid pairing certificate";
    ret = GS_INVALID;
    goto cleanup;
  }
  size_t certificateLength = certificateHexLength / 2;
  plaincert = malloc(certificateLength + 1);
  if (plaincert == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }
  if (!hex_to_bytes(result, (unsigned char *)plaincert, certificateLength)) {
    gs_error = "Sunshine returned a malformed pairing certificate";
    ret = GS_INVALID;
    goto cleanup;
  }
  plaincert[certificateLength] = '\0';

  unsigned char salt_pin[sizeof(salt_data) + 4];
  unsigned char aes_key[32] = {0};
  memcpy(salt_pin, salt_data, sizeof(salt_data));
  memcpy(salt_pin + sizeof(salt_data), pin, 4);
  int hash_length = server->serverMajorVersion >= 7 ? 32 : 20;
  if (server->serverMajorVersion >= 7)
    SHA256(salt_pin, sizeof(salt_pin), aes_key);
  else
    SHA1(salt_pin, sizeof(salt_pin), aes_key);

  unsigned char challenge_data[16];
  unsigned char challenge_enc[sizeof(challenge_data)];
  char challenge_hex[SIZEOF_AS_HEX_STR(challenge_enc)];
  if (secure_random(challenge_data, sizeof(challenge_data), "pairing challenge") != GS_OK ||
      !encrypt(challenge_data, sizeof(challenge_data), aes_key, challenge_enc)) {
    gs_error = "Could not create the Sunshine pairing challenge";
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(challenge_enc, challenge_hex, sizeof(challenge_enc));

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&clientchallenge=%s", server->serverInfo.address, server->httpPort, unique_id, uuid_str, challenge_hex);
  if ((ret = http_request(url, data)) != GS_OK) goto cleanup;
  free(result);
  result = NULL;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    gs_error = "The PIN was not accepted by Sunshine";
    ret = GS_FAILED;
    goto cleanup;
  }

  free(result);
  result = NULL;
  if ((ret = xml_search(data->memory, data->size, "challengeresponse", &result)) != GS_OK) goto cleanup;
  size_t responseHexLength = strlen(result);
  size_t responseLength = responseHexLength / 2;
  unsigned char challengeResponseEncrypted[64] = {0};
  unsigned char challengeResponse[64] = {0};
  unsigned char serverResponse[SHA256_DIGEST_LENGTH] = {0};
  if ((responseHexLength & 1) != 0 || responseLength > sizeof(challengeResponse) ||
      responseLength < (size_t)hash_length + 16 || (responseLength & 15) != 0 ||
      !hex_to_bytes(result, challengeResponseEncrypted, responseLength) ||
      !decrypt(challengeResponseEncrypted, (int)responseLength, aes_key, challengeResponse)) {
    gs_error = "Sunshine returned an invalid challenge response";
    ret = GS_INVALID;
    goto cleanup;
  }
  memcpy(serverResponse, challengeResponse, (size_t)hash_length);

  unsigned char clientSecret[16];
  if (secure_random(clientSecret, sizeof(clientSecret), "pairing secret") != GS_OK) {
    ret = GS_FAILED;
    goto cleanup;
  }

  const unsigned char *clientCertificateSignature = NULL;
  size_t clientCertificateSignatureLength = 0;
  if (!certificate_signature_view(
          cert, &clientCertificateSignature,
          &clientCertificateSignatureLength)) {
    gs_error = "The Vita pairing certificate has an invalid signature";
    ret = GS_INVALID;
    goto cleanup;
  }

  size_t challengeMaterialLength =
      16 + clientCertificateSignatureLength + sizeof(clientSecret);
  unsigned char *challengeMaterial = malloc(challengeMaterialLength);
  if (challengeMaterial == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }
  memcpy(challengeMaterial, challengeResponse + hash_length, 16);
  memcpy(challengeMaterial + 16, clientCertificateSignature,
         clientCertificateSignatureLength);
  memcpy(challengeMaterial + 16 + clientCertificateSignatureLength,
         clientSecret, sizeof(clientSecret));

  unsigned char challengeHash[32] = {0};
  unsigned char challengeHashEncrypted[sizeof(challengeHash)];
  char challengeResponseHex[SIZEOF_AS_HEX_STR(challengeHashEncrypted)];
  if (server->serverMajorVersion >= 7)
    SHA256(challengeMaterial, challengeMaterialLength, challengeHash);
  else
    SHA1(challengeMaterial, challengeMaterialLength, challengeHash);
  free(challengeMaterial);
  if (!encrypt(challengeHash, sizeof(challengeHash), aes_key, challengeHashEncrypted)) {
    gs_error = "Could not encrypt the Sunshine challenge response";
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(challengeHashEncrypted, challengeResponseHex, sizeof(challengeHashEncrypted));

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&serverchallengeresp=%s", server->serverInfo.address, server->httpPort, unique_id, uuid_str, challengeResponseHex);
  if ((ret = http_request(url, data)) != GS_OK) goto cleanup;
  free(result);
  result = NULL;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    gs_error = "Sunshine could not verify the Vita pairing response";
    ret = GS_FAILED;
    goto cleanup;
  }

  free(result);
  result = NULL;
  if ((ret = xml_search(data->memory, data->size, "pairingsecret", &result)) != GS_OK) goto cleanup;
  size_t pairingSecretHexLength = strlen(result);
  size_t pairingSecretLength = pairingSecretHexLength / 2;
  if ((pairingSecretHexLength & 1) != 0 || pairingSecretLength <= 16 ||
      pairingSecretLength > 2048) {
    gs_error = "Sunshine returned an invalid pairing proof";
    ret = GS_INVALID;
    goto cleanup;
  }
  pairingSecret = malloc(pairingSecretLength);
  if (pairingSecret == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }
  if (!hex_to_bytes(result, pairingSecret, pairingSecretLength) ||
      !verifySignature((char *)pairingSecret, 16,
                       (char *)pairingSecret + 16,
                       (int)pairingSecretLength - 16, plaincert)) {
    gs_error = "Sunshine's pairing identity could not be verified";
    ret = GS_FAILED;
    goto cleanup;
  }

  ret = copy_pem_certificate_signature(
      plaincert, &serverCertificateSignature,
      &serverCertificateSignatureLength);
  if (ret != GS_OK) goto cleanup;

  unsigned char expectedServerResponse[SHA256_DIGEST_LENGTH] = {0};
  if (!hash_pairing_challenge_binding(
          challenge_data, serverCertificateSignature,
          serverCertificateSignatureLength, pairingSecret, hash_length,
          expectedServerResponse) ||
      !constant_time_equal(
          expectedServerResponse, serverResponse, (size_t)hash_length)) {
    gs_error = "The pairing PIN proof from Sunshine did not match";
    ret = GS_FAILED;
    goto cleanup;
  }
  free(serverCertificateSignature);
  serverCertificateSignature = NULL;
  serverCertificateSignatureLength = 0;
  free(pairingSecret);
  pairingSecret = NULL;

  // Only the verified PIN binding authorizes this Sunshine identity for TLS.
  if ((ret = http_set_server_pin_from_pem(plaincert, false)) != GS_OK) goto cleanup;
  ephemeralPinInstalled = true;

  size_t signatureLength = 0;
  if (sign_it((char *)clientSecret, sizeof(clientSecret), &signature,
              &signatureLength, privateKey) != GS_OK || signatureLength > 2048) {
    gs_error = "The Vita could not sign the pairing response";
    ret = GS_FAILED;
    goto cleanup;
  }
  size_t clientPairingSecretLength = sizeof(clientSecret) + signatureLength;
  clientPairingSecret = malloc(clientPairingSecretLength);
  clientPairingSecretHex = malloc(clientPairingSecretLength * 2 + 1);
  if (clientPairingSecret == NULL || clientPairingSecretHex == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }
  memcpy(clientPairingSecret, clientSecret, sizeof(clientSecret));
  memcpy(clientPairingSecret + sizeof(clientSecret), signature, signatureLength);
  bytes_to_hex(clientPairingSecret, clientPairingSecretHex, clientPairingSecretLength);

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&clientpairingsecret=%s", server->serverInfo.address, server->httpPort, unique_id, uuid_str, clientPairingSecretHex);
  if ((ret = http_request(url, data)) != GS_OK) goto cleanup;
  free(result);
  result = NULL;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    gs_error = "Sunshine rejected the Vita pairing proof";
    ret = GS_FAILED;
    goto cleanup;
  }

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "https://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&phrase=pairchallenge", server->serverInfo.address, server->httpsPort, unique_id, uuid_str);
  if ((ret = http_request(url, data)) != GS_OK) goto cleanup;
  free(result);
  result = NULL;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    gs_error = "Sunshine did not complete secure pairing";
    ret = GS_FAILED;
    goto cleanup;
  }

  if ((ret = http_set_server_pin_from_pem(plaincert, true)) != GS_OK) goto cleanup;
  server->paired = true;
  server->securePairingRequired = false;

  // Refresh through pinned HTTPS so paired status, app state, and MAC are trusted.
  int refreshRet = GS_FAILED;
  for (int attempt = 0; attempt < 3; ++attempt) {
    refreshRet = gs_refresh(server);
    if (refreshRet == GS_OK) break;
    sceKernelDelayThread(200 * 1000);
  }
  if (refreshRet != GS_OK) {
    ret = refreshRet;
    goto cleanup;
  }
  ret = GS_OK;

cleanup:
  if (ret != GS_OK) {
    const char *pairingError = gs_error;
    if (pairingStarted) gs_unpair(server);
    if (ephemeralPinInstalled) http_reload_server_pin();
    gs_error = pairingError;
    server->paired = false;
    server->securePairingRequired = !http_has_server_pin();
  }
  free(result);
  free(url);
  free(plaincert);
  free(pairingSecret);
  free(serverCertificateSignature);
  if (signature != NULL) OPENSSL_free(signature);
  free(clientPairingSecret);
  free(clientPairingSecretHex);
  http_free_data(data);
  return ret;
}

int gs_applist(PSERVER_DATA server, PAPP_LIST *list) {
  int ret = GS_OK;
  char url[4096];
  uuid_t uuid;
  char uuid_str[UUID_STRLEN];
  PHTTP_DATA data = http_create_data();
  if (data == NULL)
    return GS_OUT_OF_MEMORY;

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, sizeof(url), "https://%s:%u/applist?uniqueid=%s&uuid=%s", server->serverInfo.address, server->httpsPort, unique_id, uuid_str);
  if ((ret = http_request(url, data)) != GS_OK)
    goto cleanup;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK)
    goto cleanup;
  ret = xml_applist(data->memory, data->size, list);

  cleanup:
  http_free_data(data);
  return ret;
}

static bool is_vita_contract_mode(const STREAM_CONFIGURATION *config) {
  static const unsigned short resolutions[][2] = {
    {960, 544}, {960, 540}, {1280, 720}
  };
  static const unsigned char frameRates[] = {24, 30, 40, 50, 60};

  bool resolutionSupported = false;
  bool frameRateSupported = false;
  for (size_t i = 0; i < sizeof(resolutions) / sizeof(resolutions[0]); ++i) {
    if (config->width == resolutions[i][0] && config->height == resolutions[i][1]) {
      resolutionSupported = true;
      break;
    }
  }
  for (size_t i = 0; i < sizeof(frameRates) / sizeof(frameRates[0]); ++i) {
    if (config->fps == frameRates[i]) {
      frameRateSupported = true;
      break;
    }
  }
  return resolutionSupported && frameRateSupported;
}

int gs_start_app(PSERVER_DATA server, STREAM_CONFIGURATION *config, int appId, bool sops, bool localaudio, int gamepad_mask) {
  int ret = GS_OK;
  uuid_t uuid;
  char* result = NULL;
  char uuid_str[UUID_STRLEN];

  PDISPLAY_MODE mode = server->modes;
  bool correct_mode = false;
  while (mode != NULL) {
    if (mode->width == config->width && mode->height == config->height) {
      if (mode->refresh == config->fps)
        correct_mode = true;
    }

    mode = mode->next;
  }

  /*
   * Sunshine's mode list describes desktop modes, not every encoder cadence.
   * Our host contract keeps the VDD at 60 Hz while streaming at 24/30/40/50/60.
   * Permit only that bounded SOPS contract when a mode isn't advertised.
   */
  if (!correct_mode && !(sops && is_vita_contract_mode(config)))
    return GS_NOT_SUPPORTED_MODE;

  if (secure_random(config->remoteInputAesKey,
                    sizeof(config->remoteInputAesKey),
                    "remote input key") != GS_OK) {
    return GS_FAILED;
  }
  memset(config->remoteInputAesIv, 0, sizeof(config->remoteInputAesIv));

  char url[4096];
  u_int32_t rikeyid = 0;
  if (secure_random(config->remoteInputAesIv, sizeof(rikeyid),
                    "remote input key identifier") != GS_OK) {
    return GS_FAILED;
  }
  memcpy(&rikeyid, config->remoteInputAesIv, sizeof(rikeyid));
  rikeyid = htonl(rikeyid);
  char rikey_hex[SIZEOF_AS_HEX_STR(config->remoteInputAesKey)];
  bytes_to_hex(config->remoteInputAesKey, rikey_hex, sizeof(config->remoteInputAesKey));

  PHTTP_DATA data = http_create_data();
  if (data == NULL)
    return GS_OUT_OF_MEMORY;

  // Using an FPS value over 60 causes SOPS to default to 720p60,
  // so force it to 0 to ensure the correct resolution is set. We
  // used to use 60 here but that locked the frame rate to 60 FPS
  // on GFE 3.20.3.
  int fps = (server->isNvidiaSoftware && config->fps > 60) ? 0 : config->fps;

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  int surround_info = SURROUNDAUDIOINFO_FROM_AUDIO_CONFIGURATION(config->audioConfiguration);
  snprintf(url, sizeof(url), "https://%s:%u/%s?uniqueid=%s&uuid=%s&appid=%d&mode=%dx%dx%d&additionalStates=1&sops=%d&rikey=%s&rikeyid=%d&localAudioPlayMode=%d&surroundAudioInfo=%d&remoteControllersBitmap=%d&gcmap=%d%s%s",
           server->serverInfo.address, server->httpsPort, server->currentGame ? "resume" : "launch", unique_id, uuid_str, appId, config->width, config->height, fps, sops, rikey_hex, rikeyid, localaudio, surround_info, gamepad_mask, gamepad_mask,
           (config->supportedVideoFormats & VIDEO_FORMAT_MASK_10BIT) ? "&hdrMode=1&clientHdrCapVersion=0&clientHdrCapSupportedFlagsInUint32=0&clientHdrCapMetaDataId=NV_STATIC_METADATA_TYPE_1&clientHdrCapDisplayData=0x0x0x0x0x0x0x0x0x0x0" : "",
           LiGetLaunchUrlQueryParameters());
  long requestTimeout = server->currentGame
      ? HTTP_TIMEOUT_ORDINARY_SECONDS
      : HTTP_TIMEOUT_LAUNCH_SECONDS;
  if ((ret = http_request_with_timeout(url, data, requestTimeout)) != GS_OK)
    goto cleanup;

  if ((ret = xml_status(data->memory, data->size)) != GS_OK)
    goto cleanup;
  if ((ret = xml_search(
           data->memory, data->size, "gamesession", &result)) != GS_OK)
    goto cleanup;
  if (result == NULL || result[0] == '\0') {
    free(result);
    result = NULL;
    if ((ret = xml_search(
             data->memory, data->size, "resume", &result)) != GS_OK)
      goto cleanup;
  }

  if (result == NULL || strcmp(result, "1") != 0) {
    ret = GS_FAILED;
    goto cleanup;
  }

  free(result);
  result = NULL;

  if (xml_search(data->memory, data->size, "sessionUrl0", &result) == GS_OK &&
      result != NULL && result[0] != '\0') {
    free((void *)server->serverInfo.rtspSessionUrl);
    server->serverInfo.rtspSessionUrl = result;
    result = NULL;
  }
  server->currentGame = appId;

  cleanup:
  if (result != NULL)
    free(result);

  http_free_data(data);
  return ret;
}

int gs_quit_app(PSERVER_DATA server) {
  int ret = GS_OK;
  char url[4096];
  uuid_t uuid;
  char uuid_str[UUID_STRLEN];
  char* result = NULL;
  PHTTP_DATA data = http_create_data();
  if (data == NULL)
    return GS_OUT_OF_MEMORY;

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, sizeof(url), "https://%s:%u/cancel?uniqueid=%s&uuid=%s", server->serverInfo.address, server->httpsPort, unique_id, uuid_str);
  if ((ret = http_request(url, data)) != GS_OK)
    goto cleanup;

  if ((ret = xml_status(data->memory, data->size)) != GS_OK)
    goto cleanup;
  else if ((ret = xml_search(data->memory, data->size, "cancel", &result)) != GS_OK)
    goto cleanup;

  if (result == NULL || strcmp(result, "1") != 0) {
    ret = GS_FAILED;
    goto cleanup;
  }

  cleanup:
  if (result != NULL)
    free(result);

  http_free_data(data);
  return ret;
}

int gs_init(PSERVER_DATA server, char *address, unsigned short httpPort, const char *keyDirectory, int log_level, bool allowUnsupportedVersion) {
  if (server == NULL || address == NULL || keyDirectory == NULL) {
    return GS_INVALID;
  }

  free_server_status_data(server);
  if (mkdirtree(keyDirectory) != 0) {
    gs_error = "Could not create the Vita pairing data directory";
    return GS_IO_ERROR;
  }
  int ret = load_cert(keyDirectory);
  if (ret != GS_OK)
    return ret;

  ret = http_init(keyDirectory, log_level);
  if (ret != GS_OK) {
    cleanup_client_credentials();
    return ret;
  }

  ret = load_unique_id(keyDirectory, http_has_server_pin());
  if (ret != GS_OK) {
    http_cleanup();
    cleanup_client_credentials();
    return ret;
  }

  LiInitializeServerInformation(&server->serverInfo);
  server->serverInfo.address = address;
  server->allowUnsupportedVersion = allowUnsupportedVersion;
  server->securePairingRequired = false;
  server->httpPort = httpPort ? httpPort : 47989;
  server->httpsPort = 0; /* Populated by load_server_status() */
  ret = load_server_status(server);
  if (ret != GS_OK) {
    free_server_status_data(server);
    http_cleanup();
    cleanup_client_credentials();
  }
  return ret;
}

void gs_cleanup(PSERVER_DATA server) {
  if (server != NULL) {
    free_server_status_data(server);
    memset(server, 0, sizeof(*server));
    LiInitializeServerInformation(&server->serverInfo);
  }
  http_cleanup();
  cleanup_client_credentials();
}

int gs_refresh(PSERVER_DATA server) {
  if (server == NULL || server->serverInfo.address == NULL) {
    return GS_INVALID;
  }

  SERVER_DATA refreshed = {0};
  LiInitializeServerInformation(&refreshed.serverInfo);
  refreshed.serverInfo.address = server->serverInfo.address;
  refreshed.allowUnsupportedVersion = server->allowUnsupportedVersion;
  refreshed.securePairingRequired = server->securePairingRequired;
  refreshed.httpPort = server->httpPort;
  refreshed.httpsPort = server->httpsPort;

  int ret = load_server_status(&refreshed);
  if (ret != GS_OK) {
    free_server_status_data(&refreshed);
    return ret;
  }

  // Sunshine may report the app as idle between video sessions. Preserve the
  // app ID so the caller resumes it instead of launching a duplicate.
  int running_app = server->currentGame;
  free_server_status_data(server);
  *server = refreshed;
  server->currentGame = running_app;
  return GS_OK;
}

int gs_generate_pin(char pin[5]) {
  if (pin == NULL) return GS_INVALID;

  unsigned char randomByte;
  for (int i = 0; i < 4; ++i) {
    /* Rejection sampling avoids modulo bias (250 is divisible by 10). */
    do {
      if (secure_random(&randomByte, sizeof(randomByte), "pairing PIN") != GS_OK) {
        pin[0] = '\0';
        return GS_FAILED;
      }
    } while (randomByte >= 250);
    pin[i] = (char)('0' + (randomByte % 10));
  }
  pin[4] = '\0';
  return GS_OK;
}

void gs_free_applist(PAPP_LIST *app_list) {
  if (app_list == NULL) return;
  PAPP_LIST app = *app_list;
  while (app != NULL) {
    PAPP_LIST next = app->next;
    free(app->name);
    free(app);
    app = next;
  }
  *app_list = NULL;
}

int gs_get_server_mac(PSERVER_DATA server, char *mac, unsigned int size) {
  if (!server || !mac || size == 0) return GS_INVALID;
  if (!server->mac[0]) {
    if (gs_refresh(server) != GS_OK) {
      return GS_INVALID;
    }
  }
  if (!server->mac[0])
    return GS_INVALID;
  strncpy(mac, server->mac, size - 1);
  mac[size - 1] = '\0';
  return GS_OK;
}
