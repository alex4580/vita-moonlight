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
#include "bridge_protocol.h"
#include "crypto.h"
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
#include <psp2/kernel/threadmgr.h>

#include "../src/debug.h"

#define UNIQUE_FILE_NAME "uniqueid.dat"
#define PAIRING_PENDING_FILE_NAME "pairing-pending.dat"
#define LEGACY_SHARED_UNIQUE_ID "0123456789ABCDEF"
#define PAIRING_JOURNAL_VERSION 1u
#define PAIRING_JOURNAL_CAPACITY 768u

//
#define printf vita_debug_log
//

#define UNIQUEID_BYTES 8
#define UNIQUEID_CHARS (UNIQUEID_BYTES*2)

static char unique_id[UNIQUEID_CHARS+1];
static GS_CRYPTO_IDENTITY *identity;
static char cert_hex[8192];
static char client_identity_pin[GS_CRYPTO_SPKI_PIN_LENGTH + 1];

const char* gs_error;

#define LEN_AS_HEX_STR(x) ((x) * 2 + 1)
#define SIZEOF_AS_HEX_STR(x) LEN_AS_HEX_STR(sizeof(x))

#define UUID_STRLEN 37

#define PATH_MAX 1024
#define SERVERINFO_NUMERIC_TEXT_MAX 64u

static char unique_id_path[PATH_MAX];
static char pairing_pending_path[PATH_MAX];
static char active_certificate_path[PATH_MAX];
static char active_key_path[PATH_MAX];

typedef enum _ARTIFACT_STATE {
  ARTIFACT_ABSENT,
  ARTIFACT_VALID,
  ARTIFACT_INVALID,
  ARTIFACT_IO_ERROR
} ARTIFACT_STATE;

typedef enum _PAIRING_STAGE {
  PAIRING_STAGE_NONE = 0,
  PAIRING_STAGE_OPENED = 1,
  PAIRING_STAGE_AUTHENTICATED = 2,
  PAIRING_STAGE_AUTHORIZING = 3,
  PAIRING_STAGE_AUTHORIZED = 4,
  PAIRING_STAGE_LOCAL_COMMITTED = 5,
  PAIRING_STAGE_COMPLETE = 6
} PAIRING_STAGE;

typedef struct _PAIRING_JOURNAL {
  unsigned long generation;
  PAIRING_STAGE stage;
  char pairId[UNIQUEID_CHARS + 1];
  char serverPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  char clientPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  unsigned short httpsPort;
  bool legacy;
} PAIRING_JOURNAL;

typedef enum _PAIRING_JOURNAL_STATE {
  PAIRING_JOURNAL_ABSENT,
  PAIRING_JOURNAL_VALID,
  PAIRING_JOURNAL_INVALID
} PAIRING_JOURNAL_STATE;

static PAIRING_JOURNAL pairing_journal;
static PAIRING_JOURNAL_STATE pairing_journal_state = PAIRING_JOURNAL_ABSENT;

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
    unsigned char digest[GS_CRYPTO_SHA256_LENGTH]) {
  if (challenge == NULL || serverCertificateSignature == NULL ||
      serverCertificateSignatureLength == 0 || serverSecret == NULL ||
      digest == NULL) {
    return false;
  }

  const GS_CRYPTO_BUFFER transcript[] = {
      {.data = challenge, .length = 16},
      {.data = serverCertificateSignature,
       .length = serverCertificateSignatureLength},
      {.data = serverSecret, .length = 16},
  };
  return gs_crypto_hash_segments(
      hashLength, transcript, sizeof(transcript) / sizeof(transcript[0]),
      digest);
}

static int copy_pem_certificate_signature(
    const char *certificatePem, unsigned char **signature,
    size_t *signatureLength) {
  if (certificatePem == NULL || signature == NULL || signatureLength == NULL) {
    return GS_INVALID;
  }

  if (!gs_crypto_pem_certificate_signature(
          certificatePem, signature, signatureLength)) {
    gs_error = "Sunshine returned an invalid pairing certificate signature";
    return GS_INVALID;
  }
  return GS_OK;
}

static int secure_random(void *buffer, size_t size, const char *purpose) {
  if (!gs_crypto_random(buffer, size)) {
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
  gs_crypto_identity_free(identity);
  identity = NULL;
  cert_hex[0] = '\0';
  client_identity_pin[0] = '\0';
  active_certificate_path[0] = '\0';
  active_key_path[0] = '\0';
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

static bool build_pairing_pending_path(
    char *path, size_t pathSize, const char *keyDirectory,
    const char *suffix) {
  int written = snprintf(
      path, pathSize, "%s/%s%s", keyDirectory, PAIRING_PENDING_FILE_NAME,
      suffix == NULL ? "" : suffix);
  return written > 0 && (size_t)written < pathSize;
}

static ARTIFACT_STATE artifact_file_state(const char *path) {
  struct stat status;
  if (path == NULL) return ARTIFACT_INVALID;
  if (stat(path, &status) != 0) {
    return errno == ENOENT ? ARTIFACT_ABSENT : ARTIFACT_IO_ERROR;
  }
  return S_ISREG(status.st_mode) ? ARTIFACT_VALID : ARTIFACT_INVALID;
}

static bool is_lower_hex_text(const char *value, size_t length) {
  if (value == NULL || strlen(value) != length) return false;
  for (size_t i = 0; i < length; ++i) {
    if (!((value[i] >= '0' && value[i] <= '9') ||
          (value[i] >= 'a' && value[i] <= 'f'))) {
      return false;
    }
  }
  return true;
}

static bool is_hex_text(const char *value, size_t length) {
  if (value == NULL || strlen(value) != length) return false;
  for (size_t i = 0; i < length; ++i) {
    if (!isxdigit((unsigned char)value[i])) return false;
  }
  return true;
}

static ARTIFACT_STATE read_unique_id_file(
    const char *path, char value[UNIQUEID_CHARS + 1]) {
  ARTIFACT_STATE state = artifact_file_state(path);
  if (state != ARTIFACT_VALID) return state;

  FILE *file = fopen(path, "rb");
  if (file == NULL) return ARTIFACT_IO_ERROR;
  size_t bytesRead = fread(value, 1, UNIQUEID_CHARS, file);
  int trailing = fgetc(file);
  bool readOk = !ferror(file);
  bool ioOk = fclose(file) == 0;
  ioOk = ioOk && readOk;
  value[bytesRead < UNIQUEID_CHARS ? bytesRead : UNIQUEID_CHARS] = '\0';
  if (!ioOk) return ARTIFACT_IO_ERROR;
  if (bytesRead != UNIQUEID_CHARS || trailing != EOF ||
      !is_hex_text(value, UNIQUEID_CHARS)) {
    return ARTIFACT_INVALID;
  }
  return ARTIFACT_VALID;
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

  char primaryId[UNIQUEID_CHARS + 1] = {0};
  char temporaryId[UNIQUEID_CHARS + 1] = {0};
  char backupId[UNIQUEID_CHARS + 1] = {0};
  ARTIFACT_STATE primaryState =
      read_unique_id_file(uniqueFilePath, primaryId);
  ARTIFACT_STATE temporaryState =
      read_unique_id_file(temporaryPath, temporaryId);
  ARTIFACT_STATE backupState = read_unique_id_file(backupPath, backupId);
  if (!is_hex_text(value, UNIQUEID_CHARS)) {
    gs_error = "The Vita pairing ID to save is invalid";
    return GS_INVALID;
  }

  /* A valid primary is authoritative. If it is missing or torn, normalize the
   * next valid transaction artifact before replacing any sibling. This keeps
   * an interrupted atomic write recoverable without accepting an all-corrupt
   * identity set as a new installation. */
  const char *recoveryPath = temporaryState == ARTIFACT_VALID ? temporaryPath :
      backupState == ARTIFACT_VALID ? backupPath : NULL;
  bool hasValidIdentity = primaryState == ARTIFACT_VALID || recoveryPath != NULL;
  bool hasAnyArtifact = primaryState != ARTIFACT_ABSENT ||
      temporaryState != ARTIFACT_ABSENT || backupState != ARTIFACT_ABSENT;
  if (!hasValidIdentity && hasAnyArtifact) {
    gs_error = "The saved Vita pairing ID files are damaged; no files were replaced";
    return GS_IO_ERROR;
  }
  if (primaryState != ARTIFACT_VALID && recoveryPath != NULL) {
    bool recoveredTemporary = recoveryPath == temporaryPath;
    if ((primaryState != ARTIFACT_ABSENT &&
         remove(uniqueFilePath) != 0) ||
        rename(recoveryPath, uniqueFilePath) != 0) {
      gs_error = "Could not normalize the current Vita pairing identity";
      return GS_IO_ERROR;
    }
    primaryState = ARTIFACT_VALID;
    if (recoveredTemporary) {
      memcpy(primaryId, temporaryId, sizeof(primaryId));
      temporaryState = ARTIFACT_ABSENT;
    } else {
      memcpy(primaryId, backupId, sizeof(primaryId));
      backupState = ARTIFACT_ABSENT;
    }
  }
  if (temporaryState != ARTIFACT_ABSENT && remove(temporaryPath) != 0) {
    gs_error = "Could not replace the staged Vita pairing ID";
    return GS_IO_ERROR;
  }
  temporaryState = ARTIFACT_ABSENT;

  FILE *file = fopen(temporaryPath, "wb");
  if (file == NULL) {
    gs_error = "Could not save the Vita pairing identity";
    return GS_IO_ERROR;
  }
  bool writeOk = fwrite(value, 1, UNIQUEID_CHARS, file) == UNIQUEID_CHARS &&
      fflush(file) == 0;
  bool closeOk = fclose(file) == 0;
  if (!writeOk || !closeOk) {
    (void)remove(temporaryPath);
    gs_error = "Could not finish saving the Vita pairing identity";
    return GS_IO_ERROR;
  }

  char stagedId[UNIQUEID_CHARS + 1];
  if (read_unique_id_file(temporaryPath, stagedId) != ARTIFACT_VALID ||
      strcmp(stagedId, value) != 0) {
    (void)remove(temporaryPath);
    gs_error = "The staged Vita pairing ID did not verify";
    return GS_IO_ERROR;
  }

  bool hadPreviousIdentity = primaryState == ARTIFACT_VALID;
  if (hadPreviousIdentity && backupState != ARTIFACT_ABSENT &&
      remove(backupPath) != 0) {
    remove(temporaryPath);
    gs_error = "Could not rotate the previous Vita pairing ID backup";
    return GS_IO_ERROR;
  }
  if (hadPreviousIdentity && rename(uniqueFilePath, backupPath) != 0) {
    remove(temporaryPath);
    gs_error = "Could not preserve the previous Vita pairing identity";
    return GS_IO_ERROR;
  }
  if (rename(temporaryPath, uniqueFilePath) != 0) {
    if (hadPreviousIdentity) rename(backupPath, uniqueFilePath);
    gs_error = "Could not install the Vita pairing identity";
    return GS_IO_ERROR;
  }
  char committedId[UNIQUEID_CHARS + 1];
  if (read_unique_id_file(uniqueFilePath, committedId) != ARTIFACT_VALID ||
      strcmp(committedId, value) != 0) {
    /* A successful rename is not a successful transaction until the new
     * primary reads back exactly. Remove the unverifiable primary and restore
     * the last verified identity. If either operation fails, keep the backup
     * intact so startup recovery still has a known-good candidate. */
    bool invalidPrimaryRemoved = remove(uniqueFilePath) == 0;
    bool previousIdentityRestored = !hadPreviousIdentity;
    if (invalidPrimaryRemoved && hadPreviousIdentity) {
      previousIdentityRestored = rename(backupPath, uniqueFilePath) == 0;
    }
    gs_error = invalidPrimaryRemoved && previousIdentityRestored
        ? "The committed Vita pairing ID did not verify; the previous identity was restored"
        : "The committed Vita pairing ID did not verify and automatic recovery failed";
    return GS_IO_ERROR;
  }
  if (hadPreviousIdentity) remove(backupPath);
  return GS_OK;
}

static bool pairing_pending_sibling_path(
    char *path, size_t pathSize, const char *suffix) {
  if (pairing_pending_path[0] == '\0') return false;
  int written = snprintf(
      path, pathSize, "%s%s", pairing_pending_path,
      suffix == NULL ? "" : suffix);
  return written > 0 && (size_t)written < pathSize;
}

static bool pairing_journals_equal(
    const PAIRING_JOURNAL *left, const PAIRING_JOURNAL *right) {
  return left->generation == right->generation && left->stage == right->stage &&
      left->httpsPort == right->httpsPort && left->legacy == right->legacy &&
      strcmp(left->pairId, right->pairId) == 0 &&
      strcmp(left->serverPin, right->serverPin) == 0 &&
      strcmp(left->clientPin, right->clientPin) == 0;
}

static int format_pairing_journal(
    const PAIRING_JOURNAL *journal, char *output, size_t outputSize) {
  if (journal == NULL || output == NULL || outputSize == 0 ||
      journal->generation == 0 ||
      journal->stage < PAIRING_STAGE_OPENED ||
      journal->stage > PAIRING_STAGE_COMPLETE ||
      !is_lower_hex_text(journal->pairId, UNIQUEID_CHARS) ||
      !http_server_pin_is_valid(journal->clientPin) ||
      (journal->serverPin[0] != '\0' &&
       !http_server_pin_is_valid(journal->serverPin)) ||
      (journal->stage == PAIRING_STAGE_OPENED &&
       journal->serverPin[0] != '\0') ||
      (journal->stage >= PAIRING_STAGE_AUTHENTICATED &&
       journal->stage <= PAIRING_STAGE_LOCAL_COMMITTED &&
       (journal->serverPin[0] == '\0' || journal->httpsPort == 0))) {
    return GS_INVALID;
  }

  char canonical[PAIRING_JOURNAL_CAPACITY];
  int canonicalLength = snprintf(
      canonical, sizeof(canonical),
      "version=%u\n"
      "generation=%lu\n"
      "stage=%u\n"
      "pair_id=%s\n"
      "server_pin=%s\n"
      "client_pin=%s\n"
      "https_port=%u\n",
      PAIRING_JOURNAL_VERSION, journal->generation,
      (unsigned int)journal->stage, journal->pairId,
      journal->serverPin[0] == '\0' ? "-" : journal->serverPin,
      journal->clientPin, (unsigned int)journal->httpsPort);
  if (canonicalLength <= 0 ||
      (size_t)canonicalLength >= sizeof(canonical)) {
    return GS_INVALID;
  }

  unsigned char digest[GS_CRYPTO_SHA256_LENGTH];
  char checksum[GS_CRYPTO_SHA256_LENGTH * 2 + 1];
  if (!gs_crypto_hash(
          GS_CRYPTO_SHA256_LENGTH, canonical, (size_t)canonicalLength,
          digest)) {
    return GS_FAILED;
  }
  bytes_to_hex(digest, checksum, sizeof(digest));
  int written = snprintf(
      output, outputSize, "%schecksum=%s\n", canonical, checksum);
  return written > 0 && (size_t)written < outputSize ? GS_OK : GS_INVALID;
}

static ARTIFACT_STATE read_pairing_journal_file(
    const char *path, PAIRING_JOURNAL *journal) {
  ARTIFACT_STATE state = artifact_file_state(path);
  if (state != ARTIFACT_VALID) return state;

  FILE *file = fopen(path, "rb");
  if (file == NULL) return ARTIFACT_IO_ERROR;
  char contents[PAIRING_JOURNAL_CAPACITY];
  size_t length = fread(contents, 1, sizeof(contents) - 1, file);
  int trailing = fgetc(file);
  bool readOk = !ferror(file);
  bool ioOk = fclose(file) == 0;
  ioOk = ioOk && readOk;
  if (!ioOk) return ARTIFACT_IO_ERROR;
  if (length == 0 || trailing != EOF) return ARTIFACT_INVALID;
  contents[length] = '\0';

  memset(journal, 0, sizeof(*journal));
  if (length == UNIQUEID_CHARS &&
      is_lower_hex_text(contents, UNIQUEID_CHARS)) {
    journal->stage = PAIRING_STAGE_OPENED;
    memcpy(journal->pairId, contents, UNIQUEID_CHARS + 1);
    journal->legacy = true;
    return ARTIFACT_VALID;
  }

  unsigned int version = 0;
  unsigned int stageValue = 0;
  unsigned int portValue = 0;
  char serverPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1] = {0};
  char clientPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1] = {0};
  char checksum[GS_CRYPTO_SHA256_LENGTH * 2 + 1] = {0};
  int consumed = 0;
  int fields = sscanf(
      contents,
      "version=%u\n"
      "generation=%lu\n"
      "stage=%u\n"
      "pair_id=%16[0-9a-f]\n"
      "server_pin=%52[^\n]\n"
      "client_pin=%52[^\n]\n"
      "https_port=%u\n"
      "checksum=%64[0-9a-f]\n%n",
      &version, &journal->generation, &stageValue, journal->pairId,
      serverPin, clientPin, &portValue, checksum, &consumed);
  if (fields != 8 || consumed != (int)length ||
      version != PAIRING_JOURNAL_VERSION ||
      journal->generation == 0 ||
      stageValue < PAIRING_STAGE_OPENED ||
      stageValue > PAIRING_STAGE_COMPLETE || portValue > 65535u ||
      !is_lower_hex_text(journal->pairId, UNIQUEID_CHARS) ||
      !is_lower_hex_text(checksum, GS_CRYPTO_SHA256_LENGTH * 2) ||
      !http_server_pin_is_valid(clientPin) ||
      (strcmp(serverPin, "-") != 0 &&
       !http_server_pin_is_valid(serverPin))) {
    return ARTIFACT_INVALID;
  }

  journal->stage = (PAIRING_STAGE)stageValue;
  journal->httpsPort = (unsigned short)portValue;
  snprintf(journal->clientPin, sizeof(journal->clientPin), "%s", clientPin);
  if (strcmp(serverPin, "-") != 0) {
    snprintf(journal->serverPin, sizeof(journal->serverPin), "%s", serverPin);
  }
  if (journal->stage >= PAIRING_STAGE_AUTHENTICATED &&
      journal->stage <= PAIRING_STAGE_LOCAL_COMMITTED &&
      (journal->serverPin[0] == '\0' || journal->httpsPort == 0)) {
    return ARTIFACT_INVALID;
  }
  if (journal->stage == PAIRING_STAGE_OPENED &&
      journal->serverPin[0] != '\0') {
    return ARTIFACT_INVALID;
  }

  char expected[PAIRING_JOURNAL_CAPACITY];
  if (format_pairing_journal(journal, expected, sizeof(expected)) != GS_OK ||
      strcmp(contents, expected) != 0) {
    return ARTIFACT_INVALID;
  }
  return ARTIFACT_VALID;
}

static PAIRING_JOURNAL_STATE load_pairing_journal(void) {
  static const char *suffixes[] = {NULL, ".tmp", ".bak"};
  PAIRING_JOURNAL selected;
  bool foundValid = false;
  bool foundCorrupt = false;
  bool generationConflict = false;
  memset(&selected, 0, sizeof(selected));

  for (size_t i = 0; i < sizeof(suffixes) / sizeof(suffixes[0]); ++i) {
    char path[PATH_MAX];
    if (!pairing_pending_sibling_path(path, sizeof(path), suffixes[i])) {
      foundCorrupt = true;
      continue;
    }
    PAIRING_JOURNAL candidate;
    ARTIFACT_STATE state = read_pairing_journal_file(path, &candidate);
    if (state == ARTIFACT_INVALID || state == ARTIFACT_IO_ERROR) {
      foundCorrupt = true;
      continue;
    }
    if (state != ARTIFACT_VALID) continue;
    if (!foundValid || candidate.generation > selected.generation) {
      selected = candidate;
      foundValid = true;
      generationConflict = false;
    } else if (candidate.generation == selected.generation &&
               !pairing_journals_equal(&candidate, &selected)) {
      generationConflict = true;
    }
  }

  memset(&pairing_journal, 0, sizeof(pairing_journal));
  if (generationConflict || (!foundValid && foundCorrupt)) {
    pairing_journal_state = PAIRING_JOURNAL_INVALID;
  } else if (foundValid) {
    pairing_journal = selected;
    pairing_journal_state = PAIRING_JOURNAL_VALID;
  } else {
    pairing_journal_state = PAIRING_JOURNAL_ABSENT;
  }
  return pairing_journal_state;
}

static int persist_pairing_journal(const PAIRING_JOURNAL *journal) {
  char primaryPath[PATH_MAX];
  char temporaryPath[PATH_MAX];
  char backupPath[PATH_MAX];
  if (!pairing_pending_sibling_path(primaryPath, sizeof(primaryPath), NULL) ||
      !pairing_pending_sibling_path(
          temporaryPath, sizeof(temporaryPath), ".tmp") ||
      !pairing_pending_sibling_path(backupPath, sizeof(backupPath), ".bak")) {
    gs_error = "The Sunshine pairing recovery path is unavailable";
    return GS_IO_ERROR;
  }

  PAIRING_JOURNAL primaryJournal;
  PAIRING_JOURNAL temporaryJournal;
  PAIRING_JOURNAL backupJournal;
  ARTIFACT_STATE primaryState =
      read_pairing_journal_file(primaryPath, &primaryJournal);
  ARTIFACT_STATE temporaryState =
      read_pairing_journal_file(temporaryPath, &temporaryJournal);
  ARTIFACT_STATE backupState =
      read_pairing_journal_file(backupPath, &backupJournal);
  /* Normalize the selected generation to primary before staging its successor.
   * This matters after a crash between primary->.bak and .tmp->primary: the
   * selected .tmp must never be deleted before an equally valid primary exists.
   * A torn, lower-authority sibling does not invalidate a verified generation. */
  if (pairing_journal_state == PAIRING_JOURNAL_VALID) {
    bool primaryIsCurrent = primaryState == ARTIFACT_VALID &&
        pairing_journals_equal(&primaryJournal, &pairing_journal);
    bool temporaryIsCurrent = temporaryState == ARTIFACT_VALID &&
        pairing_journals_equal(&temporaryJournal, &pairing_journal);
    bool backupIsCurrent = backupState == ARTIFACT_VALID &&
        pairing_journals_equal(&backupJournal, &pairing_journal);
    if (!primaryIsCurrent) {
      const char *sourcePath = temporaryIsCurrent ? temporaryPath :
          backupIsCurrent ? backupPath : NULL;
      if (sourcePath == NULL ||
          (primaryState != ARTIFACT_ABSENT && remove(primaryPath) != 0) ||
          rename(sourcePath, primaryPath) != 0) {
        gs_error = "Could not normalize the current pairing recovery generation";
        return GS_IO_ERROR;
      }
      primaryState = ARTIFACT_VALID;
      primaryJournal = pairing_journal;
      if (temporaryIsCurrent) temporaryState = ARTIFACT_ABSENT;
      if (backupIsCurrent) backupState = ARTIFACT_ABSENT;
    }
  } else if (primaryState != ARTIFACT_ABSENT ||
             temporaryState != ARTIFACT_ABSENT ||
             backupState != ARTIFACT_ABSENT) {
    gs_error = "Pairing recovery artifacts appeared without a loaded transaction";
    return GS_IO_ERROR;
  }

  char serialized[PAIRING_JOURNAL_CAPACITY];
  int ret = format_pairing_journal(journal, serialized, sizeof(serialized));
  if (ret != GS_OK) {
    gs_error = "The pairing recovery transaction is invalid";
    return ret;
  }
  if (temporaryState != ARTIFACT_ABSENT && remove(temporaryPath) != 0) {
    gs_error = "Could not rotate the pairing recovery staging file";
    return GS_IO_ERROR;
  }
  FILE *file = fopen(temporaryPath, "wb");
  if (file == NULL) {
    gs_error = "Could not stage pairing recovery data";
    return GS_IO_ERROR;
  }
  size_t serializedLength = strlen(serialized);
  bool writeOk = fwrite(serialized, 1, serializedLength, file) ==
          serializedLength &&
      fflush(file) == 0;
  bool closeOk = fclose(file) == 0;
  if (!writeOk || !closeOk) {
    gs_error = "Could not finish staging pairing recovery data";
    return GS_IO_ERROR;
  }

  PAIRING_JOURNAL staged;
  if (read_pairing_journal_file(temporaryPath, &staged) != ARTIFACT_VALID ||
      !pairing_journals_equal(&staged, journal)) {
    gs_error = "The staged pairing recovery transaction did not verify";
    return GS_IO_ERROR;
  }

  bool hadPrimary = primaryState == ARTIFACT_VALID;
  if (hadPrimary && backupState != ARTIFACT_ABSENT &&
      remove(backupPath) != 0) {
    gs_error = "Could not rotate the previous pairing recovery backup";
    return GS_IO_ERROR;
  }
  if (hadPrimary && rename(primaryPath, backupPath) != 0) {
    gs_error = "Could not preserve the previous pairing recovery state";
    return GS_IO_ERROR;
  }
  if (rename(temporaryPath, primaryPath) != 0) {
    if (hadPrimary) (void)rename(backupPath, primaryPath);
    gs_error = "Could not commit the pairing recovery transaction";
    return GS_IO_ERROR;
  }
  PAIRING_JOURNAL committed;
  if (read_pairing_journal_file(primaryPath, &committed) != ARTIFACT_VALID ||
      !pairing_journals_equal(&committed, journal)) {
    gs_error = "The committed pairing recovery transaction did not verify";
    return GS_IO_ERROR;
  }
  if (hadPrimary) (void)remove(backupPath);
  return GS_OK;
}

static int transition_pairing_journal(
    PAIRING_STAGE stage, const char *pairId, const char *serverPin,
    unsigned short httpsPort) {
  if (stage < PAIRING_STAGE_OPENED || stage > PAIRING_STAGE_COMPLETE ||
      !is_lower_hex_text(pairId, UNIQUEID_CHARS)) {
    gs_error = "The requested pairing recovery transition is invalid";
    return GS_INVALID;
  }
  if (pairing_journal_state == PAIRING_JOURNAL_INVALID ||
      client_identity_pin[0] == '\0') {
    gs_error = "Pairing recovery data is unavailable or damaged";
    return GS_IO_ERROR;
  }
  if (pairing_journal_state == PAIRING_JOURNAL_VALID &&
      pairing_journal.generation == ULONG_MAX) {
    gs_error = "The pairing recovery generation cannot advance";
    return GS_IO_ERROR;
  }
  if (pairing_journal_state == PAIRING_JOURNAL_VALID &&
      pairing_journal.stage != PAIRING_STAGE_COMPLETE) {
    if (strcmp(pairing_journal.pairId, pairId) != 0 ||
        (stage != PAIRING_STAGE_COMPLETE && stage < pairing_journal.stage) ||
        (pairing_journal.stage >= PAIRING_STAGE_AUTHENTICATED &&
         pairing_journal.serverPin[0] != '\0' &&
         (serverPin == NULL ||
          strcmp(pairing_journal.serverPin, serverPin) != 0))) {
      gs_error = "The pairing recovery transition would change authenticated transaction identity";
      return GS_INVALID;
    }
  } else if (pairing_journal_state == PAIRING_JOURNAL_VALID &&
             pairing_journal.stage == PAIRING_STAGE_COMPLETE &&
             stage != PAIRING_STAGE_OPENED &&
             stage != PAIRING_STAGE_COMPLETE) {
    gs_error = "A completed pairing transaction can only start a new attempt";
    return GS_WRONG_STATE;
  }

  PAIRING_JOURNAL next;
  memset(&next, 0, sizeof(next));
  next.generation = pairing_journal_state == PAIRING_JOURNAL_VALID
      ? pairing_journal.generation + 1 : 1;
  next.stage = stage;
  snprintf(next.pairId, sizeof(next.pairId), "%s", pairId);
  if (serverPin != NULL) {
    snprintf(next.serverPin, sizeof(next.serverPin), "%s", serverPin);
  }
  snprintf(
      next.clientPin, sizeof(next.clientPin), "%s", client_identity_pin);
  next.httpsPort = httpsPort;
  int ret = persist_pairing_journal(&next);
  if (ret == GS_OK) {
    pairing_journal = next;
    pairing_journal_state = PAIRING_JOURNAL_VALID;
  }
  return ret;
}

static int load_unique_id(
    const char* keyDirectory, bool hasAuthenticatedServerPin,
    const char *recoveryId) {
  char temporaryPath[PATH_MAX];
  char backupPath[PATH_MAX];
  if (!build_unique_id_path(
          unique_id_path, sizeof(unique_id_path), keyDirectory, NULL) ||
      !build_unique_id_path(
          temporaryPath, sizeof(temporaryPath), keyDirectory, ".tmp") ||
      !build_unique_id_path(
          backupPath, sizeof(backupPath), keyDirectory, ".bak") ||
      !build_pairing_pending_path(
          pairing_pending_path, sizeof(pairing_pending_path), keyDirectory,
          NULL)) {
    gs_error = "The Vita pairing identity path is too long";
    return GS_FAILED;
  }

  char primaryId[UNIQUEID_CHARS + 1] = {0};
  char temporaryId[UNIQUEID_CHARS + 1] = {0};
  char backupId[UNIQUEID_CHARS + 1] = {0};
  ARTIFACT_STATE primaryState =
      read_unique_id_file(unique_id_path, primaryId);
  ARTIFACT_STATE temporaryState =
      read_unique_id_file(temporaryPath, temporaryId);
  ARTIFACT_STATE backupState = read_unique_id_file(backupPath, backupId);

  if (recoveryId != NULL) {
    if (!is_lower_hex_text(recoveryId, UNIQUEID_CHARS)) {
      gs_error = "The pairing recovery ID is invalid";
      return GS_INVALID;
    }
    snprintf(unique_id, sizeof(unique_id), "%s", recoveryId);
    return GS_OK;
  }

  const char *selectedId = primaryState == ARTIFACT_VALID ? primaryId :
      temporaryState == ARTIFACT_VALID ? temporaryId :
      backupState == ARTIFACT_VALID ? backupId : NULL;
  bool valid = selectedId != NULL;
  if (valid) snprintf(unique_id, sizeof(unique_id), "%s", selectedId);
  bool allAbsent = primaryState == ARTIFACT_ABSENT &&
      temporaryState == ARTIFACT_ABSENT && backupState == ARTIFACT_ABSENT;
  if (!valid && !allAbsent) {
    gs_error = "The saved Vita pairing ID files are damaged; no files were replaced";
    return GS_IO_ERROR;
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
  if (!valid && hasAuthenticatedServerPin) {
    gs_error = "The Vita pairing ID is missing for this authenticated Sunshine host; no replacement was generated";
    return GS_IO_ERROR;
  }

  unsigned char randomId[UNIQUEID_BYTES];
  if (secure_random(randomId, sizeof(randomId), "client identity") != GS_OK) {
    return GS_FAILED;
  }
  char generatedId[UNIQUEID_CHARS + 1];
  bytes_to_hex(randomId, generatedId, sizeof(randomId));

  int ret = persist_unique_id_atomic(unique_id_path, generatedId);
  if (ret != GS_OK) return ret;
  memcpy(unique_id, generatedId, sizeof(unique_id));
  return GS_OK;
}

static bool credential_sibling_path(
    char *path, size_t pathSize, const char *basePath, const char *suffix) {
  int written = snprintf(
      path, pathSize, "%s%s", basePath, suffix == NULL ? "" : suffix);
  return written > 0 && (size_t)written < pathSize;
}

static bool read_bounded_credential(
    const char *path, char *contents, size_t capacity, size_t *length) {
  if (path == NULL || contents == NULL || capacity < 2 || length == NULL) {
    return false;
  }
  contents[0] = '\0';
  *length = 0;
  FILE *file = fopen(path, "rb");
  if (file == NULL) return false;

  size_t bytesRead = fread(contents, 1, capacity - 1, file);
  bool complete = bytesRead != 0 && fgetc(file) == EOF && !ferror(file);
  bool closed = fclose(file) == 0;
  if (!complete || !closed) {
    memset(contents, 0, capacity);
    return false;
  }
  contents[bytesRead] = '\0';
  *length = bytesRead;
  return true;
}

static bool write_credential_file(
    const char *path, const char *contents, size_t length) {
  FILE *file = fopen(path, "wb");
  if (file == NULL) return false;
  bool valid = fwrite(contents, 1, length, file) == length &&
      fflush(file) == 0;
  bool closed = fclose(file) == 0;
  return valid && closed;
}

static GS_CRYPTO_IDENTITY *load_identity_files(
    const char *certificatePath, const char *keyPath,
    char certificateContents[MKCERT_CERTIFICATE_PEM_CAPACITY],
    size_t *certificateLength,
    char keyContents[MKCERT_PRIVATE_KEY_PEM_CAPACITY], size_t *keyLength) {
  if (!read_bounded_credential(
          certificatePath, certificateContents,
          MKCERT_CERTIFICATE_PEM_CAPACITY, certificateLength) ||
      !read_bounded_credential(
          keyPath, keyContents, MKCERT_PRIVATE_KEY_PEM_CAPACITY,
          keyLength)) {
    return NULL;
  }
  return gs_crypto_identity_load_pem(
      certificateContents, *certificateLength + 1,
      keyContents, *keyLength + 1);
}

static bool identity_files_match(
    const char *certificatePath, const char *keyPath) {
  char certificateContents[MKCERT_CERTIFICATE_PEM_CAPACITY];
  char keyContents[MKCERT_PRIVATE_KEY_PEM_CAPACITY];
  size_t certificateLength = 0;
  size_t keyLength = 0;
  GS_CRYPTO_IDENTITY *candidate = load_identity_files(
      certificatePath, keyPath, certificateContents, &certificateLength,
      keyContents, &keyLength);
  gs_crypto_identity_free(candidate);
  memset(keyContents, 0, sizeof(keyContents));
  return candidate != NULL;
}

static int persist_identity_atomic(
    const char *certificatePath, const char *keyPath,
    const char *certificateContents, size_t certificateLength,
    const char *keyContents, size_t keyLength) {
  char certificateTemporaryPath[PATH_MAX];
  char certificateBackupPath[PATH_MAX];
  char keyTemporaryPath[PATH_MAX];
  char keyBackupPath[PATH_MAX];
  if (!credential_sibling_path(
          certificateTemporaryPath, sizeof(certificateTemporaryPath),
          certificatePath, ".tmp") ||
      !credential_sibling_path(
          certificateBackupPath, sizeof(certificateBackupPath),
          certificatePath, ".bak") ||
      !credential_sibling_path(
          keyTemporaryPath, sizeof(keyTemporaryPath), keyPath, ".tmp") ||
      !credential_sibling_path(
          keyBackupPath, sizeof(keyBackupPath), keyPath, ".bak")) {
    gs_error = "The Vita pairing credential path is too long";
    return GS_INVALID;
  }

  const char *artifacts[] = {
      certificatePath, certificateTemporaryPath, certificateBackupPath,
      keyPath, keyTemporaryPath, keyBackupPath,
  };
  for (size_t i = 0; i < sizeof(artifacts) / sizeof(artifacts[0]); ++i) {
    ARTIFACT_STATE state = artifact_file_state(artifacts[i]);
    if (state != ARTIFACT_ABSENT) {
      gs_error = state == ARTIFACT_IO_ERROR
          ? "The Vita pairing identity files could not be inspected"
          : "Existing Vita pairing identity evidence was preserved instead of being replaced";
      return GS_IO_ERROR;
    }
  }

  if (!write_credential_file(
          certificateTemporaryPath, certificateContents,
          certificateLength) ||
      !write_credential_file(keyTemporaryPath, keyContents, keyLength) ||
      !identity_files_match(certificateTemporaryPath, keyTemporaryPath)) {
    gs_error = "Could not safely stage the Vita pairing identity";
    return GS_IO_ERROR;
  }

  if (rename(keyTemporaryPath, keyPath) != 0) {
    gs_error = "Could not install the Vita pairing private key";
    return GS_IO_ERROR;
  }
  if (rename(certificateTemporaryPath, certificatePath) != 0) {
    /* On a first install, key.pem + client.pem.tmp remains a valid recovery
     * candidate on the next launch. */
    gs_error = "Could not install the Vita pairing certificate";
    return GS_IO_ERROR;
  }
  if (!identity_files_match(certificatePath, keyPath)) {
    gs_error = "The saved Vita pairing identity did not verify";
    return GS_IO_ERROR;
  }

  return GS_OK;
}

static int load_cert(const char* keyDirectory) {
  cleanup_client_credentials();
  client_identity_pin[0] = '\0';
  active_certificate_path[0] = '\0';
  active_key_path[0] = '\0';

  char certificatePath[PATH_MAX];
  char keyPath[PATH_MAX];
  int certificatePathLength = snprintf(
      certificatePath, sizeof(certificatePath), "%s/%s",
      keyDirectory, CERTIFICATE_FILE_NAME);
  int keyPathLength = snprintf(
      keyPath, sizeof(keyPath), "%s/%s", keyDirectory, KEY_FILE_NAME);
  if (certificatePathLength < 0 ||
      (size_t)certificatePathLength >= sizeof(certificatePath) ||
      keyPathLength < 0 || (size_t)keyPathLength >= sizeof(keyPath)) {
    gs_error = "The Vita pairing credential path is too long";
    return GS_INVALID;
  }

  char certificatePaths[3][PATH_MAX];
  char keyPaths[3][PATH_MAX];
  static const char *suffixes[] = {NULL, ".bak", ".tmp"};
  for (size_t i = 0; i < 3; ++i) {
    if (!credential_sibling_path(
            certificatePaths[i], sizeof(certificatePaths[i]),
            certificatePath, suffixes[i]) ||
        !credential_sibling_path(
            keyPaths[i], sizeof(keyPaths[i]), keyPath, suffixes[i])) {
      gs_error = "The Vita pairing credential path is too long";
      return GS_INVALID;
    }
  }

  bool foundArtifact = false;
  for (size_t i = 0; i < 3; ++i) {
    ARTIFACT_STATE certificateState =
        artifact_file_state(certificatePaths[i]);
    ARTIFACT_STATE keyState = artifact_file_state(keyPaths[i]);
    if (certificateState == ARTIFACT_IO_ERROR ||
        keyState == ARTIFACT_IO_ERROR) {
      gs_error = "The Vita pairing identity files could not be inspected; no files were replaced";
      return GS_IO_ERROR;
    }
    foundArtifact = foundArtifact || certificateState != ARTIFACT_ABSENT ||
        keyState != ARTIFACT_ABSENT;
  }

  /* Exact pairs are preferred. Cross pairs cover a power loss between the
   * two filesystem renames in an otherwise atomic credential transaction. */
  static const unsigned char candidatePairs[][2] = {
      {0, 0}, {1, 1}, {2, 2},
      {2, 0}, {0, 2}, {1, 0}, {0, 1}, {2, 1}, {1, 2},
  };
  char recoveredCertificate[MKCERT_CERTIFICATE_PEM_CAPACITY];
  char recoveredKey[MKCERT_PRIVATE_KEY_PEM_CAPACITY];
  size_t recoveredCertificateLength = 0;
  size_t recoveredKeyLength = 0;
  size_t selectedCandidate = sizeof(candidatePairs) / sizeof(candidatePairs[0]);
  for (size_t i = 0; i < sizeof(candidatePairs) / sizeof(candidatePairs[0]);
       ++i) {
    identity = load_identity_files(
        certificatePaths[candidatePairs[i][0]],
        keyPaths[candidatePairs[i][1]], recoveredCertificate,
        &recoveredCertificateLength, recoveredKey, &recoveredKeyLength);
    if (identity != NULL) {
      selectedCandidate = i;
      break;
    }
  }

  if (identity == NULL && foundArtifact) {
    memset(recoveredKey, 0, sizeof(recoveredKey));
    gs_error = "The saved Vita pairing certificate/key artifacts are incomplete or damaged; no files were replaced";
    return GS_IO_ERROR;
  }

  if (identity == NULL) {
    printf("Generating Vita pairing identity...");
    if (mkcert_generate(
            recoveredCertificate, sizeof(recoveredCertificate),
            &recoveredCertificateLength, recoveredKey,
            sizeof(recoveredKey), &recoveredKeyLength) != 0) {
      memset(recoveredKey, 0, sizeof(recoveredKey));
      gs_error = "Could not generate the Vita pairing identity";
      return GS_FAILED;
    }
    identity = gs_crypto_identity_load_pem(
        recoveredCertificate, recoveredCertificateLength + 1,
        recoveredKey, recoveredKeyLength + 1);
    printf(identity == NULL ? "failed\n" : "done\n");
    if (identity == NULL) {
      memset(recoveredKey, 0, sizeof(recoveredKey));
      gs_error = "The generated Vita pairing identity did not verify";
      return GS_FAILED;
    }
    if (persist_identity_atomic(
            certificatePath, keyPath, recoveredCertificate,
            recoveredCertificateLength, recoveredKey,
            recoveredKeyLength) != GS_OK) {
      gs_crypto_identity_free(identity);
      identity = NULL;
      memset(recoveredKey, 0, sizeof(recoveredKey));
      return GS_IO_ERROR;
    }
    selectedCandidate = 0;
  }

  if (recoveredCertificateLength * 2 + 1 > sizeof(cert_hex)) {
    gs_crypto_identity_free(identity);
    identity = NULL;
    memset(recoveredKey, 0, sizeof(recoveredKey));
    gs_error = "The Vita pairing certificate is unexpectedly large";
    return GS_INVALID;
  }
  bytes_to_hex(
      (unsigned char *)recoveredCertificate, cert_hex,
      recoveredCertificateLength);
  if (!gs_crypto_spki_pin_from_pem(
          recoveredCertificate, client_identity_pin)) {
    gs_crypto_identity_free(identity);
    identity = NULL;
    memset(recoveredKey, 0, sizeof(recoveredKey));
    gs_error = "The Vita pairing certificate identity could not be derived";
    return GS_INVALID;
  }
  if (selectedCandidate >=
      sizeof(candidatePairs) / sizeof(candidatePairs[0])) {
    gs_crypto_identity_free(identity);
    identity = NULL;
    memset(recoveredKey, 0, sizeof(recoveredKey));
    gs_error = "The Vita pairing identity recovery source is unavailable";
    return GS_IO_ERROR;
  }
  const unsigned char certificateIndex = candidatePairs[selectedCandidate][0];
  const unsigned char keyIndex = candidatePairs[selectedCandidate][1];
  snprintf(
      active_certificate_path, sizeof(active_certificate_path), "%s",
      certificatePaths[certificateIndex]);
  snprintf(active_key_path, sizeof(active_key_path), "%s", keyPaths[keyIndex]);
  memset(recoveredKey, 0, sizeof(recoveredKey));
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
  /* Preserve typed transport failures such as GS_IDENTITY_CHANGED. The
   * saved-host UI needs that distinction to warn about a replaced Sunshine
   * certificate instead of misreporting the computer as merely offline. */
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_ORDINARY_SECONDS)) != GS_OK) {
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
  unsigned long pairStatus = 0;
  unsigned long codecModeSupport = SCM_H264;
  unsigned long httpsPort = 0;
  if (!parse_unsigned_decimal_field(currentGameText, INT_MAX, &currentGame)) {
    gs_error = "Sunshine returned an invalid currentgame field";
    ret = GS_INVALID;
    goto cleanup;
  }
  if (!parse_unsigned_decimal_field(pairedText, 1u, &pairStatus)) {
    gs_error = "Sunshine returned an invalid PairStatus field";
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

  server->paired = pairStatus == 1u;
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
    /* A pinned TLS connection can still be rejected because Sunshine no
     * longer authorizes this Vita certificate (for example after the PC's
     * paired-client list is cleared). The verified server identity makes that
     * different from a certificate change or an offline host. Refresh only
     * public metadata over HTTP and require a new PIN before any privileged
     * request. All other pinned HTTPS failures remain fail-closed. */
    free_server_status_data(server);
    ret = load_serverinfo(server, true);
    if (ret == GS_CLIENT_UNAUTHORIZED) {
      free_server_status_data(server);
      ret = load_serverinfo(server, false);
      if (ret == GS_OK) {
        server->paired = false;
        server->securePairingRequired = true;
        gs_error = NULL;
      }
    } else {
      server->securePairingRequired = false;
    }
  }

  if (ret == GS_OK && !server->allowUnsupportedVersion) {
    if (server->serverMajorVersion > MAX_SUPPORTED_GFE_VERSION) {
      gs_error = "Update Vita Moonlight or use a supported Sunshine host version and try again";
      ret = GS_UNSUPPORTED_VERSION;
    } else if (server->serverMajorVersion < MIN_SUPPORTED_GFE_VERSION) {
      gs_error = "Vita Moonlight requires a newer supported Sunshine host version.";
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

static int hex_nibble(unsigned char value) {
  if (value >= '0' && value <= '9') return value - '0';
  if (value >= 'a' && value <= 'f') return value - 'a' + 10;
  if (value >= 'A' && value <= 'F') return value - 'A' + 10;
  return -1;
}

static bool hex_to_bytes(const char *in, unsigned char *out,
                         size_t outputLength) {
  if (in == NULL || out == NULL) {
    return false;
  }
  size_t inputLength = strlen(in);
  if ((inputLength & 1u) != 0 || inputLength / 2 != outputLength) return false;

  for (size_t count = 0; count < outputLength; ++count) {
    int high = hex_nibble((unsigned char)in[count * 2]);
    int low = hex_nibble((unsigned char)in[count * 2 + 1]);
    if (high < 0 || low < 0) return false;
    out[count] = (unsigned char)((high << 4) | low);
  }
  return true;
}

static int sign_it(
    const char *msg, size_t mlen, unsigned char **sig, size_t *slen,
    GS_CRYPTO_IDENTITY *signingIdentity) {
  if (msg == NULL || sig == NULL || slen == NULL || signingIdentity == NULL) {
    return GS_INVALID;
  }
  *sig = NULL;
  *slen = 0;
  *sig = malloc(GS_CRYPTO_MAX_SIGNATURE_LENGTH);
  if (*sig == NULL) return GS_OUT_OF_MEMORY;
  if (!gs_crypto_identity_sign_sha256(
          signingIdentity, msg, mlen, *sig,
          GS_CRYPTO_MAX_SIGNATURE_LENGTH, slen) ||
      *slen == 0 || *slen > GS_CRYPTO_MAX_SIGNATURE_LENGTH) {
    free(*sig);
    *sig = NULL;
    *slen = 0;
    return GS_FAILED;
  }
  return GS_OK;
}

static bool verifySignature(
    const char *data, int dataLength, const char *signature,
    int signatureLength, const char *certificatePem) {
  return dataLength >= 0 && signatureLength > 0 &&
      gs_crypto_verify_sha256_pem(
          certificatePem, data, (size_t)dataLength,
          (const unsigned char *)signature, (size_t)signatureLength);
}

static bool encrypt(const unsigned char *plaintext, int plaintextLen, const unsigned char *key, unsigned char *ciphertext) {
  return plaintextLen > 0 && gs_crypto_aes_128_ecb(
      true, key, plaintext, (size_t)plaintextLen, ciphertext);
}

static bool decrypt(const unsigned char *ciphertext, int ciphertextLen, const unsigned char *key, unsigned char *plaintext) {
  return ciphertextLen > 0 && gs_crypto_aes_128_ecb(
      false, key, ciphertext, (size_t)ciphertextLen, plaintext);
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
  ret = http_request_with_timeout(
      url, data, HTTP_TIMEOUT_ORDINARY_SECONDS);

  http_free_data(data);
  return ret;
}

static int abort_pairing_session(
    PSERVER_DATA server, const char pendingId[UNIQUEID_CHARS + 1],
    char *url, size_t urlSize, PHTTP_DATA data) {
  uuid_t abortUuid;
  char abortUuidString[UUID_STRLEN];
  uuid_generate_random(abortUuid);
  uuid_unparse(abortUuid, abortUuidString);
  int written = snprintf(
      url, urlSize,
      "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight"
      "&updateState=1&clientpairingsecret=00",
      server->serverInfo.address, server->httpPort, pendingId,
      abortUuidString);
  if (written <= 0 || (size_t)written >= urlSize) {
    gs_error = "The Sunshine pairing recovery request is too long";
    return GS_INVALID;
  }

  /* Sunshine intentionally reports an XML pairing status of 400 for the
   * too-short/out-of-order secret, while removing this exact pending session.
   * Transport completion is authoritative; do not parse that status. An
   * already-removed ID is equally safe and cannot remove an authorized cert. */
  int ret = http_request_with_timeout(
      url, data, HTTP_TIMEOUT_PAIRING_ABORT_SECONDS);
  return ret;
}

static int finish_pairing_challenge(
    PSERVER_DATA server, const char pairId[UNIQUEID_CHARS + 1]) {
  if (server == NULL || server->serverInfo.address == NULL ||
      server->httpsPort == 0 ||
      !is_lower_hex_text(pairId, UNIQUEID_CHARS)) {
    gs_error = "The final Sunshine pairing proof is unavailable";
    return GS_INVALID;
  }

  char url[4096];
  uuid_t uuid;
  char uuidString[UUID_STRLEN];
  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuidString);
  int written = snprintf(
      url, sizeof(url),
      "https://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight"
      "&updateState=1&phrase=pairchallenge",
      server->serverInfo.address, server->httpsPort, pairId, uuidString);
  if (written <= 0 || (size_t)written >= sizeof(url)) {
    gs_error = "The final Sunshine pairing request is too long";
    return GS_INVALID;
  }

  int ret = GS_OK;
  char *paired = NULL;
  PHTTP_DATA data = http_create_data();
  if (data == NULL) return GS_OUT_OF_MEMORY;
  if ((ret = http_request_with_timeout(
           url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)) != GS_OK ||
      (ret = xml_status(data->memory, data->size)) != GS_OK ||
      (ret = xml_search(data->memory, data->size, "paired", &paired)) !=
          GS_OK) {
    goto cleanup;
  }
  if (paired == NULL || strcmp(paired, "1") != 0) {
    gs_error = "Sunshine did not complete secure pairing";
    ret = GS_FAILED;
  }

cleanup:
  free(paired);
  http_free_data(data);
  return ret;
}

static int reconcile_pairing_journal(
    PSERVER_DATA server, bool *handled, bool *resolvedUnpaired) {
  if (handled == NULL || resolvedUnpaired == NULL) return GS_INVALID;
  *handled = false;
  *resolvedUnpaired = false;
  if (pairing_journal_state != PAIRING_JOURNAL_VALID ||
      pairing_journal.stage < PAIRING_STAGE_AUTHORIZING ||
      pairing_journal.stage > PAIRING_STAGE_LOCAL_COMMITTED) {
    return GS_OK;
  }
  *handled = true;

  /* The journal pin is already active in curl and unique_id already names the
   * exact attempted identity. This HTTPS query is therefore the authoritative
   * answer to the ambiguous clientpairingsecret crash boundary. */
  server->httpsPort = pairing_journal.httpsPort;
  int ret = load_serverinfo(server, true);
  if (ret == GS_CLIENT_UNAUTHORIZED) {
    /* Sunshine's current HTTPS verifier emits XML status 401 when the exact
     * pinned server did not authorize (or no longer authorizes) this client
     * certificate. PairStatus=0 is therefore unreachable over HTTPS. Resolve
     * the ambiguous transaction and let normal discovery offer a fresh PIN. */
    ret = transition_pairing_journal(
        PAIRING_STAGE_COMPLETE, pairing_journal.pairId,
        pairing_journal.serverPin, pairing_journal.httpsPort);
    if (ret == GS_OK) {
      *resolvedUnpaired = true;
      gs_error = NULL;
    }
    return ret;
  }
  if (ret != GS_OK) {
    if (ret == GS_IDENTITY_CHANGED) {
      gs_error = "Sunshine changed identity while an interrupted pairing transaction was pending";
    } else if (gs_error == NULL) {
      gs_error = "Could not verify Sunshine's interrupted pairing transaction over pinned HTTPS";
    }
    return ret;
  }

  if (!server->paired) {
    ret = transition_pairing_journal(
        PAIRING_STAGE_COMPLETE, pairing_journal.pairId,
        pairing_journal.serverPin, pairing_journal.httpsPort);
    if (ret == GS_OK) *resolvedUnpaired = true;
    return ret;
  }

  if (pairing_journal.stage < PAIRING_STAGE_AUTHORIZED &&
      (ret = transition_pairing_journal(
           PAIRING_STAGE_AUTHORIZED, pairing_journal.pairId,
           pairing_journal.serverPin, pairing_journal.httpsPort)) != GS_OK) {
    return ret;
  }

  /* Commit is intentionally idempotent. If power failed after one local file
   * was installed, the validated journal and pinned HTTPS result still bind
   * both values to this exact host/client pair. */
  if ((ret = http_set_server_pin(pairing_journal.serverPin, true)) != GS_OK ||
      (ret = persist_unique_id_atomic(
           unique_id_path, pairing_journal.pairId)) != GS_OK) {
    return ret;
  }
  snprintf(unique_id, sizeof(unique_id), "%s", pairing_journal.pairId);

  if (pairing_journal.stage < PAIRING_STAGE_LOCAL_COMMITTED &&
      (ret = transition_pairing_journal(
           PAIRING_STAGE_LOCAL_COMMITTED, pairing_journal.pairId,
           pairing_journal.serverPin, pairing_journal.httpsPort)) != GS_OK) {
    return ret;
  }
  if ((ret = finish_pairing_challenge(
           server, pairing_journal.pairId)) != GS_OK) {
    return ret;
  }
  ret = transition_pairing_journal(
      PAIRING_STAGE_COMPLETE, pairing_journal.pairId,
      pairing_journal.serverPin, pairing_journal.httpsPort);
  if (ret == GS_OK) {
    server->paired = true;
    server->securePairingRequired = false;
  }
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
  bool pairingSessionOpen = false;
  bool pairingWasRequired = false;
  unsigned char pairingIdBytes[UNIQUEID_BYTES];
  char pairingUniqueId[UNIQUEID_CHARS + 1];

  if (server == NULL || pin == NULL || strlen(pin) != 4) {
    gs_error = "Pairing requires a four-digit PIN";
    return GS_INVALID;
  }
  pairingWasRequired = server->securePairingRequired;
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
  if (server->currentGame != 0) {
    gs_error = "Stop the running Sunshine application before pairing this Vita";
    return GS_WRONG_STATE;
  }
  url = malloc(urlSize);
  data = http_create_data();
  if (url == NULL || data == NULL) {
    ret = GS_OUT_OF_MEMORY;
    goto cleanup;
  }

  /* Sunshine's PIN UI currently applies a PIN to the first pending session.
   * Journal each attempt before opening it, and clear an exact abandoned
   * session before creating another. This covers wrong PINs, timeouts, and an
   * application kill during the held getservercert request. */
  PAIRING_JOURNAL_STATE pendingState = load_pairing_journal();
  if (pendingState == PAIRING_JOURNAL_INVALID) {
    /* The unknown ID may still name a live Sunshine session. Keep the damaged
     * evidence fail-closed so an immediate retry cannot create another
     * session and let the stale one consume its PIN. Forgetting the saved PC
     * after a Sunshine restart removes this app-owned directory safely. */
    gs_error = "Pairing recovery data was damaged. Restart Sunshine, forget this PC on the Vita, then add it again";
    ret = GS_IO_ERROR;
    goto cleanup;
  }
  if (pendingState == PAIRING_JOURNAL_VALID &&
      pairing_journal.stage != PAIRING_STAGE_COMPLETE) {
    if (!pairing_journal.legacy &&
        strcmp(pairing_journal.clientPin, client_identity_pin) != 0) {
      gs_error = "Pairing recovery data belongs to a different Vita identity; no files were replaced";
      ret = GS_IO_ERROR;
      goto cleanup;
    }
    if (pairing_journal.stage >= PAIRING_STAGE_AUTHORIZING) {
      gs_error = "An authorized Sunshine pairing transaction still needs pinned recovery; reopen this PC before pairing again";
      ret = GS_WRONG_STATE;
      goto cleanup;
    }
    if (abort_pairing_session(
            server, pairing_journal.pairId, url, urlSize, data) != GS_OK) {
      gs_error = "Could not clear the previous Sunshine pairing attempt. Restart Sunshine, then try again";
      ret = GS_FAILED;
      goto cleanup;
    }
    if ((ret = transition_pairing_journal(
             PAIRING_STAGE_COMPLETE, pairing_journal.pairId,
             pairing_journal.serverPin[0] == '\0'
                 ? NULL : pairing_journal.serverPin,
             pairing_journal.httpsPort)) != GS_OK) {
      goto cleanup;
    }
  }

  if (secure_random(
          pairingIdBytes, sizeof(pairingIdBytes), "pairing identity") !=
      GS_OK) {
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(
      pairingIdBytes, pairingUniqueId, sizeof(pairingIdBytes));
  if (pairing_pending_path[0] == '\0' ||
      (ret = transition_pairing_journal(
           PAIRING_STAGE_OPENED, pairingUniqueId, NULL,
           server->httpsPort)) != GS_OK) {
    if (pairing_pending_path[0] == '\0') {
      gs_error = "The Sunshine pairing recovery path is unavailable";
      ret = GS_IO_ERROR;
    }
    goto cleanup;
  }
  pairingSessionOpen = true;

  unsigned char salt_data[16];
  char salt_hex[SIZEOF_AS_HEX_STR(salt_data)];
  if (secure_random(salt_data, sizeof(salt_data), "pairing salt") != GS_OK) {
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(salt_data, salt_hex, sizeof(salt_data));

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&phrase=getservercert&salt=%s&clientcert=%s", server->serverInfo.address, server->httpPort, pairingUniqueId, uuid_str, salt_hex, cert_hex);
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
    gs_error = "Sunshine returned a non-hex pairing certificate";
    ret = GS_INVALID;
    goto cleanup;
  }
  plaincert[certificateLength] = '\0';

  unsigned char salt_pin[sizeof(salt_data) + 4];
  unsigned char aes_key[32] = {0};
  memcpy(salt_pin, salt_data, sizeof(salt_data));
  memcpy(salt_pin + sizeof(salt_data), pin, 4);
  int hash_length = server->serverMajorVersion >= 7
      ? GS_CRYPTO_SHA256_LENGTH : GS_CRYPTO_SHA1_LENGTH;
  if (!gs_crypto_hash(
          hash_length, salt_pin, sizeof(salt_pin), aes_key)) {
    gs_error = "Could not derive the Sunshine pairing key";
    ret = GS_FAILED;
    goto cleanup;
  }

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
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&clientchallenge=%s", server->serverInfo.address, server->httpPort, pairingUniqueId, uuid_str, challenge_hex);
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)) != GS_OK) {
    goto cleanup;
  }
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
  unsigned char serverResponse[GS_CRYPTO_SHA256_LENGTH] = {0};
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
  if (!gs_crypto_identity_certificate_signature(
          identity, &clientCertificateSignature,
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
  bool challengeHashValid = gs_crypto_hash(
      hash_length, challengeMaterial, challengeMaterialLength, challengeHash);
  free(challengeMaterial);
  if (!challengeHashValid ||
      !encrypt(challengeHash, sizeof(challengeHash), aes_key,
               challengeHashEncrypted)) {
    gs_error = "Could not encrypt the Sunshine challenge response";
    ret = GS_FAILED;
    goto cleanup;
  }
  bytes_to_hex(challengeHashEncrypted, challengeResponseHex, sizeof(challengeHashEncrypted));

  uuid_generate_random(uuid);
  uuid_unparse(uuid, uuid_str);
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&serverchallengeresp=%s", server->serverInfo.address, server->httpPort, pairingUniqueId, uuid_str, challengeResponseHex);
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)) != GS_OK) {
    goto cleanup;
  }
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

  unsigned char expectedServerResponse[GS_CRYPTO_SHA256_LENGTH] = {0};
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
  char authenticatedServerPin[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  if (http_copy_server_pin(
          authenticatedServerPin, sizeof(authenticatedServerPin)) != GS_OK ||
      (ret = transition_pairing_journal(
           PAIRING_STAGE_AUTHENTICATED, pairingUniqueId,
           authenticatedServerPin, server->httpsPort)) != GS_OK) {
    if (ret == GS_OK) ret = GS_IO_ERROR;
    goto cleanup;
  }

  size_t signatureLength = 0;
  if (sign_it((char *)clientSecret, sizeof(clientSecret), &signature,
              &signatureLength, identity) != GS_OK || signatureLength > 2048) {
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
  snprintf(url, urlSize, "http://%s:%u/pair?uniqueid=%s&uuid=%s&devicename=VitaMoonlight&updateState=1&clientpairingsecret=%s", server->serverInfo.address, server->httpPort, pairingUniqueId, uuid_str, clientPairingSecretHex);
  if ((ret = transition_pairing_journal(
           PAIRING_STAGE_AUTHORIZING, pairingUniqueId,
           authenticatedServerPin, server->httpsPort)) != GS_OK) {
    goto cleanup;
  }
  /* From this boundary onward, transport failure is ambiguous: Sunshine may
   * have accepted the proof even if the response never reached the Vita.
   * Never send the pre-authorization abort after this point. */
  pairingSessionOpen = false;
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_PAIRING_USER_SECONDS)) != GS_OK) {
    goto cleanup;
  }
  /* Sunshine removes the pending phase after processing clientpairingsecret,
   * whether it accepts the proof or rejects it. */
  free(result);
  result = NULL;
  if ((ret = xml_status(data->memory, data->size)) != GS_OK) goto cleanup;
  if ((ret = xml_search(data->memory, data->size, "paired", &result)) != GS_OK) goto cleanup;
  if (strcmp(result, "1") != 0) {
    (void)transition_pairing_journal(
        PAIRING_STAGE_COMPLETE, pairingUniqueId, authenticatedServerPin,
        server->httpsPort);
    gs_error = "Sunshine rejected the Vita pairing proof";
    ret = GS_FAILED;
    goto cleanup;
  }
  if ((ret = transition_pairing_journal(
           PAIRING_STAGE_AUTHORIZED, pairingUniqueId,
           authenticatedServerPin, server->httpsPort)) != GS_OK) {
    goto cleanup;
  }

  /* Sunshine has now authorized this exact unique ID and certificate. Commit
   * the already PIN-authenticated TLS pin and ID before the final HTTPS probe.
   * If that probe is interrupted, the saved unpaired entry can reconnect and
   * reconcile the host-authorized state instead of creating an orphan pair. */
  if ((ret = http_set_server_pin(authenticatedServerPin, true)) != GS_OK) {
    goto cleanup;
  }
  if (unique_id_path[0] == '\0') {
    gs_error = "The Vita pairing identity path is unavailable";
    ret = GS_IO_ERROR;
    goto cleanup;
  }
  if ((ret = persist_unique_id_atomic(
          unique_id_path, pairingUniqueId)) != GS_OK) {
    goto cleanup;
  }
  memcpy(unique_id, pairingUniqueId, sizeof(unique_id));
  if ((ret = transition_pairing_journal(
           PAIRING_STAGE_LOCAL_COMMITTED, pairingUniqueId,
           authenticatedServerPin, server->httpsPort)) != GS_OK ||
      (ret = finish_pairing_challenge(server, pairingUniqueId)) != GS_OK ||
      (ret = transition_pairing_journal(
           PAIRING_STAGE_COMPLETE, pairingUniqueId,
           authenticatedServerPin, server->httpsPort)) != GS_OK) {
    goto cleanup;
  }

  server->paired = true;
  server->securePairingRequired = false;

  /* The final pinned pairchallenge commits the pairing in Sunshine. Metadata
   * refresh is deliberately deferred to later requests: making a transient
   * serverinfo failure transactional here reports a completed host pairing as
   * failed and can strand the Vita's local saved-computer state. */
  ret = GS_OK;
  gs_error = NULL;

cleanup:
  if (ret != GS_OK) {
    const char *pairingError = gs_error;
    if (pairingSessionOpen) {
      if (abort_pairing_session(
              server, pairingUniqueId, url, urlSize, data) == GS_OK) {
        (void)transition_pairing_journal(
            PAIRING_STAGE_COMPLETE, pairingUniqueId,
            pairing_journal.serverPin[0] == '\0'
                ? NULL : pairing_journal.serverPin,
            pairing_journal.httpsPort);
      }
    }
    if (ephemeralPinInstalled) http_reload_server_pin();
    gs_error = pairingError;
    server->paired = false;
    server->securePairingRequired =
        pairingWasRequired || !http_has_server_pin();
  }
  free(result);
  free(url);
  free(plaincert);
  free(pairingSecret);
  free(serverCertificateSignature);
  free(signature);
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
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_ORDINARY_SECONDS)) != GS_OK)
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

static int format_stream_boundary_authority(
    const char *address, char *authority, size_t authoritySize) {
  if (address == NULL || authority == NULL || authoritySize == 0) {
    return GS_INVALID;
  }
  bool needsBrackets = strchr(address, ':') != NULL && address[0] != '[';
  int written = snprintf(
      authority, authoritySize, needsBrackets ? "[%s]" : "%s", address);
  if (written < 1 || (size_t)written >= authoritySize) {
    gs_error = "The Vita host bridge address is too long";
    return GS_INVALID;
  }
  return GS_OK;
}

int gs_prepare_stream_boundary(
    PSERVER_DATA server, PSTREAM_CONFIGURATION config,
    char generation[VITA_STREAM_BOUNDARY_GENERATION_CAPACITY],
    bool *bridgeActive) {
  if (server == NULL || config == NULL || generation == NULL ||
      bridgeActive == NULL || server->serverInfo.address == NULL) {
    return GS_INVALID;
  }
  generation[0] = '\0';
  *bridgeActive = false;
  uint16_t port;
  if (!stream_boundary_port(server->httpPort, &port)) {
    gs_error = "The Sunshine HTTP port cannot produce a Vita host bridge port";
    return GS_INVALID;
  }
  char authority[1024];
  int ret = format_stream_boundary_authority(
      server->serverInfo.address, authority, sizeof(authority));
  if (ret != GS_OK) return ret;

  char url[1536];
  int written = snprintf(
      url, sizeof(url),
      "https://%s:%u" VITA_STREAM_BOUNDARY_PREPARE_PATH
      "?width=%d&height=%d&fps=%d",
      authority, (unsigned int)port,
      config->width, config->height, config->fps);
  if (written < 1 || (size_t)written >= sizeof(url)) {
    gs_error = "The Vita host bridge preparation request is too long";
    return GS_INVALID;
  }
  PHTTP_DATA data = http_create_data();
  if (data == NULL) return GS_OUT_OF_MEMORY;
  long responseCode = 0;
  HTTP_BRIDGE_RESULT bridgeResult =
      http_bridge_request(url, data, &responseCode);
  if (bridgeResult == HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE) {
    ret = GS_OK;
    goto cleanup;
  }
  if (bridgeResult != HTTP_BRIDGE_RESULT_OK) {
    ret = GS_FAILED;
    goto cleanup;
  }
  if (responseCode != 200) {
    gs_error = "The authenticated Vita host bridge rejected display preparation";
    ret = GS_FAILED;
    goto cleanup;
  }
  if (!stream_boundary_parse_prepared(
          data->memory, data->size, generation)) {
    gs_error = "The Vita host bridge returned an invalid preparation response";
    ret = GS_INVALID;
    goto cleanup;
  }
  *bridgeActive = true;
  ret = GS_OK;

cleanup:
  http_free_data(data);
  return ret;
}

typedef bool (*stream_boundary_response_parser)(
    const char *body, size_t body_size);

static int gs_stream_boundary_generation_action(
    PSERVER_DATA server, const char *generation, const char *path,
    const char *rejectedMessage, const char *invalidMessage,
    stream_boundary_response_parser parseResponse, long timeoutMs) {
  if (server == NULL || generation == NULL ||
      path == NULL || rejectedMessage == NULL || invalidMessage == NULL ||
      parseResponse == NULL || timeoutMs <= 0 ||
      strlen(generation) != VITA_STREAM_BOUNDARY_GENERATION_HEX_CHARS ||
      server->serverInfo.address == NULL) {
    return GS_INVALID;
  }
  uint16_t port;
  if (!stream_boundary_port(server->httpPort, &port)) {
    gs_error = "The Sunshine HTTP port cannot produce a Vita host bridge port";
    return GS_INVALID;
  }
  char authority[1024];
  int ret = format_stream_boundary_authority(
      server->serverInfo.address, authority, sizeof(authority));
  if (ret != GS_OK) return ret;
  char url[1536];
  int written = snprintf(
      url, sizeof(url),
      "https://%s:%u%s?generation=%s",
      authority, (unsigned int)port, path, generation);
  if (written < 1 || (size_t)written >= sizeof(url)) {
    gs_error = "The Vita host bridge lease request is too long";
    return GS_INVALID;
  }
  PHTTP_DATA data = http_create_data();
  if (data == NULL) return GS_OUT_OF_MEMORY;
  long responseCode = 0;
  HTTP_BRIDGE_RESULT bridgeResult =
      http_bridge_request_with_timeout_ms(
          url, data, &responseCode, timeoutMs);
  if (bridgeResult != HTTP_BRIDGE_RESULT_OK) {
    if (bridgeResult == HTTP_BRIDGE_RESULT_OPTIONAL_UNAVAILABLE) {
      gs_error = "The active Vita host bridge became unavailable";
    }
    ret = GS_FAILED;
    goto cleanup;
  }
  if (responseCode != 200) {
    gs_error = rejectedMessage;
    ret = GS_FAILED;
    goto cleanup;
  }
  if (!parseResponse(data->memory, data->size)) {
    gs_error = invalidMessage;
    ret = GS_INVALID;
    goto cleanup;
  }
  ret = GS_OK;

cleanup:
  http_free_data(data);
  return ret;
}

int gs_started_stream_boundary(PSERVER_DATA server, const char *generation) {
  return gs_stream_boundary_generation_action(
      server, generation, VITA_STREAM_BOUNDARY_STARTED_PATH,
      "The authenticated Vita host bridge rejected stream start",
      "The Vita host bridge returned an invalid stream-start response",
      stream_boundary_parse_started, 65000L);
}

int gs_heartbeat_stream_boundary(PSERVER_DATA server, const char *generation) {
  return gs_stream_boundary_generation_action(
      server, generation, VITA_STREAM_BOUNDARY_HEARTBEAT_PATH,
      "The authenticated Vita host bridge rejected the stream lease",
      "The Vita host bridge returned an invalid heartbeat response",
      stream_boundary_parse_heartbeat,
      VITA_STREAM_BOUNDARY_HEARTBEAT_TIMEOUT_MS);
}

int gs_stop_stream_boundary(PSERVER_DATA server, const char *generation) {
  return gs_stream_boundary_generation_action(
      server, generation, VITA_STREAM_BOUNDARY_STOP_PATH,
      "The authenticated Vita host bridge rejected display restore",
      "The Vita host bridge returned an invalid restore response",
      stream_boundary_parse_stopped, 65000L);
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
  if ((ret = http_request_with_timeout(
          url, data, HTTP_TIMEOUT_ORDINARY_SECONDS)) != GS_OK)
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
  gs_error = NULL;
  /* The Vita executable initializes this at process startup. Keep the
   * standalone gamestream library safe for callers that do not own that
   * lifecycle; initialization is idempotent. */
  if (gs_crypto_init() != 0) {
    gs_error = "The secure pairing runtime could not be initialized";
    return GS_FAILED;
  }

  free_server_status_data(server);
  if (mkdirtree(keyDirectory) != 0) {
    gs_error = "Could not create the Vita pairing data directory";
    return GS_IO_ERROR;
  }
  int ret = load_cert(keyDirectory);
  if (ret != GS_OK)
    return ret;

  if (!build_unique_id_path(
          unique_id_path, sizeof(unique_id_path), keyDirectory, NULL) ||
      !build_pairing_pending_path(
          pairing_pending_path, sizeof(pairing_pending_path), keyDirectory,
          NULL)) {
    cleanup_client_credentials();
    gs_error = "The Vita pairing recovery path is too long";
    return GS_INVALID;
  }
  PAIRING_JOURNAL_STATE journalState = load_pairing_journal();
  if (journalState == PAIRING_JOURNAL_INVALID) {
    cleanup_client_credentials();
    gs_error = "Pairing recovery data is damaged; no identity files were replaced";
    return GS_IO_ERROR;
  }
  if (journalState == PAIRING_JOURNAL_VALID &&
      !pairing_journal.legacy &&
      strcmp(pairing_journal.clientPin, client_identity_pin) != 0) {
    cleanup_client_credentials();
    gs_error = "Pairing recovery data belongs to a different Vita identity; no files were replaced";
    return GS_IO_ERROR;
  }

  bool recoveryActive = journalState == PAIRING_JOURNAL_VALID &&
      pairing_journal.stage >= PAIRING_STAGE_AUTHORIZING &&
      pairing_journal.stage <= PAIRING_STAGE_LOCAL_COMMITTED;
  ret = http_init_with_recovery(
      keyDirectory, log_level, active_certificate_path, active_key_path,
      recoveryActive ? pairing_journal.serverPin : NULL);
  if (ret != GS_OK) {
    cleanup_client_credentials();
    return ret;
  }

  ret = load_unique_id(
      keyDirectory, http_has_server_pin(),
      recoveryActive ? pairing_journal.pairId : NULL);
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

  bool recoveryHandled = false;
  bool recoveryResolvedUnpaired = false;
  if (recoveryActive) {
    ret = reconcile_pairing_journal(
        server, &recoveryHandled, &recoveryResolvedUnpaired);
    if (ret != GS_OK) {
      free_server_status_data(server);
      http_cleanup();
      cleanup_client_credentials();
      return ret;
    }
  }

  if (recoveryHandled && recoveryResolvedUnpaired) {
    /* The pinned host proved that authorization never completed. Drop the
     * journal-only pin and return to the ordinary committed-state loader. */
    free_server_status_data(server);
    http_cleanup();
    ret = http_init_with_recovery(
        keyDirectory, log_level, active_certificate_path, active_key_path,
        NULL);
    if (ret != GS_OK ||
        (ret = load_unique_id(
             keyDirectory, http_has_server_pin(), NULL)) != GS_OK) {
      http_cleanup();
      cleanup_client_credentials();
      return ret;
    }
    LiInitializeServerInformation(&server->serverInfo);
    server->serverInfo.address = address;
    server->allowUnsupportedVersion = allowUnsupportedVersion;
    server->securePairingRequired = false;
    server->httpPort = httpPort ? httpPort : 47989;
    server->httpsPort = 0;
  }
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
  unique_id_path[0] = '\0';
  pairing_pending_path[0] = '\0';
  memset(&pairing_journal, 0, sizeof(pairing_journal));
  pairing_journal_state = PAIRING_JOURNAL_ABSENT;
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
