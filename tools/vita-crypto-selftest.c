/* Host-native executable used by CI to verify the Vita pairing crypto ABI. */

#include "crypto.h"
#include "mkcert.h"

#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static int failures;

static void check(bool condition, const char *message) {
  if (!condition) {
    fprintf(stderr, "FAIL: %s\n", message);
    failures++;
  }
}

static char *read_certificate_file(const char *path) {
  if (path == NULL) return NULL;
  FILE *file = fopen(path, "rb");
  if (file == NULL) return NULL;

  char *contents = calloc(1, 65537);
  if (contents == NULL) {
    fclose(file);
    return NULL;
  }
  size_t length = fread(contents, 1, 65536, file);
  bool readOk = !ferror(file) && length != 0 && fgetc(file) == EOF;
  bool closeOk = fclose(file) == 0;
  bool valid = readOk && closeOk;
  if (!valid) {
    free(contents);
    return NULL;
  }
  contents[length] = '\0';
  return contents;
}

int main(int argc, char **argv) {
  static const unsigned char sha1Abc[GS_CRYPTO_SHA1_LENGTH] = {
      0xa9, 0x99, 0x3e, 0x36, 0x47, 0x06, 0x81, 0x6a, 0xba, 0x3e,
      0x25, 0x71, 0x78, 0x50, 0xc2, 0x6c, 0x9c, 0xd0, 0xd8, 0x9d,
  };
  static const unsigned char sha256Abc[GS_CRYPTO_SHA256_LENGTH] = {
      0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea,
      0x41, 0x41, 0x40, 0xde, 0x5d, 0xae, 0x22, 0x23,
      0xb0, 0x03, 0x61, 0xa3, 0x96, 0x17, 0x7a, 0x9c,
      0xb4, 0x10, 0xff, 0x61, 0xf2, 0x00, 0x15, 0xad,
  };
  static const unsigned char aesKey[16] = {
      0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
      0x08, 0x09, 0x0a, 0x0b, 0x0c, 0x0d, 0x0e, 0x0f,
  };
  static const unsigned char aesPlaintext[16] = {
      0x00, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77,
      0x88, 0x99, 0xaa, 0xbb, 0xcc, 0xdd, 0xee, 0xff,
  };
  static const unsigned char aesCiphertext[16] = {
      0x69, 0xc4, 0xe0, 0xd8, 0x6a, 0x7b, 0x04, 0x30,
      0xd8, 0xcd, 0xb7, 0x80, 0x70, 0xb4, 0xc5, 0x5a,
  };

  check(gs_crypto_init() == 0, "crypto initialization");

  unsigned char digest[GS_CRYPTO_SHA256_LENGTH];
  check(gs_crypto_hash(GS_CRYPTO_SHA1_LENGTH, "abc", 3, digest) &&
            memcmp(digest, sha1Abc, sizeof(sha1Abc)) == 0,
        "SHA-1 abc vector");
  check(gs_crypto_hash(GS_CRYPTO_SHA256_LENGTH, "abc", 3, digest) &&
            memcmp(digest, sha256Abc, sizeof(sha256Abc)) == 0,
        "SHA-256 abc vector");
  const GS_CRYPTO_BUFFER segmentedAbc[] = {
      {.data = (const unsigned char *)"a", .length = 1},
      {.data = NULL, .length = 0},
      {.data = (const unsigned char *)"bc", .length = 2},
  };
  check(gs_crypto_hash_segments(
            GS_CRYPTO_SHA256_LENGTH, segmentedAbc,
            sizeof(segmentedAbc) / sizeof(segmentedAbc[0]), digest) &&
            memcmp(digest, sha256Abc, sizeof(sha256Abc)) == 0,
        "zero-length hash segment is skipped safely");

  unsigned char encrypted[sizeof(aesPlaintext)];
  unsigned char decrypted[sizeof(aesPlaintext)];
  check(gs_crypto_aes_128_ecb(
            true, aesKey, aesPlaintext, sizeof(aesPlaintext), encrypted) &&
            memcmp(encrypted, aesCiphertext, sizeof(encrypted)) == 0,
        "AES-128-ECB NIST encrypt vector");
  check(gs_crypto_aes_128_ecb(
            false, aesKey, encrypted, sizeof(encrypted), decrypted) &&
            memcmp(decrypted, aesPlaintext, sizeof(decrypted)) == 0,
        "AES-128-ECB decrypt round trip");
  check(!gs_crypto_aes_128_ecb(
            true, aesKey, aesPlaintext, sizeof(aesPlaintext) - 1, encrypted),
        "AES-ECB rejects a partial block instead of padding it");

  char certificate[MKCERT_CERTIFICATE_PEM_CAPACITY];
  char privateKey[MKCERT_PRIVATE_KEY_PEM_CAPACITY];
  size_t certificateLength = 0;
  size_t privateKeyLength = 0;
  check(mkcert_generate(
            certificate, sizeof(certificate), &certificateLength,
            privateKey, sizeof(privateKey), &privateKeyLength) == 0,
        "RSA/SHA-256 client identity generation");
  GS_CRYPTO_IDENTITY *identity = gs_crypto_identity_load_pem(
      certificate, certificateLength + 1,
      privateKey, privateKeyLength + 1);
  check(identity != NULL, "generated certificate and private key match");

  static const unsigned char proof[] = "Sunshine pairing proof";
  unsigned char signature[GS_CRYPTO_MAX_SIGNATURE_LENGTH];
  size_t signatureLength = 0;
  check(identity != NULL && gs_crypto_identity_sign_sha256(
            identity, proof, sizeof(proof) - 1, signature,
            sizeof(signature), &signatureLength),
        "RSA SHA-256 signing");
  check(signatureLength != 0 && gs_crypto_verify_sha256_pem(
            certificate, proof, sizeof(proof) - 1,
            signature, signatureLength),
        "RSA SHA-256 verification");
  if (signatureLength != 0) {
    signature[0] ^= 0x01;
    check(!gs_crypto_verify_sha256_pem(
              certificate, proof, sizeof(proof) - 1,
              signature, signatureLength),
          "tampered RSA signature rejection");
    signature[0] ^= 0x01;
  }
  unsigned char tamperedProof[sizeof(proof) - 1];
  memcpy(tamperedProof, proof, sizeof(tamperedProof));
  tamperedProof[0] ^= 0x01;
  check(!gs_crypto_verify_sha256_pem(
            certificate, tamperedProof, sizeof(tamperedProof),
            signature, signatureLength),
        "tampered signed data rejection");

  const unsigned char *certificateSignature = NULL;
  size_t certificateSignatureLength = 0;
  check(identity != NULL && gs_crypto_identity_certificate_signature(
            identity, &certificateSignature, &certificateSignatureLength) &&
            certificateSignature != NULL && certificateSignatureLength != 0,
        "X.509 signature extraction");

  char pinOne[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  char pinTwo[GS_CRYPTO_SPKI_PIN_LENGTH + 1];
  check(gs_crypto_spki_pin_from_pem(certificate, pinOne) &&
            strlen(pinOne) == GS_CRYPTO_SPKI_PIN_LENGTH &&
            strncmp(pinOne, "sha256//", 8) == 0,
        "SPKI SHA-256 pin shape");
  check(gs_crypto_spki_pin_from_pem(certificate, pinTwo) &&
            strcmp(pinOne, pinTwo) == 0,
        "SPKI pin stability");

  char secondCertificate[MKCERT_CERTIFICATE_PEM_CAPACITY];
  char secondPrivateKey[MKCERT_PRIVATE_KEY_PEM_CAPACITY];
  size_t secondCertificateLength = 0;
  size_t secondPrivateKeyLength = 0;
  check(mkcert_generate(
            secondCertificate, sizeof(secondCertificate),
            &secondCertificateLength, secondPrivateKey,
            sizeof(secondPrivateKey), &secondPrivateKeyLength) == 0,
        "second identity generation");
  GS_CRYPTO_IDENTITY *mismatch = gs_crypto_identity_load_pem(
      certificate, certificateLength + 1,
      secondPrivateKey, secondPrivateKeyLength + 1);
  check(mismatch == NULL, "certificate/private-key mismatch rejection");
  gs_crypto_identity_free(mismatch);

  static const char malformedPem[] =
      "-----BEGIN CERTIFICATE-----\nnot-base64!\n-----END CERTIFICATE-----\n";
  check(gs_crypto_identity_load_pem(
            malformedPem, sizeof(malformedPem), privateKey,
            privateKeyLength + 1) == NULL,
        "malformed identity PEM rejection");
  check(!gs_crypto_spki_pin_from_pem(malformedPem, pinOne),
        "malformed SPKI PEM rejection");
  unsigned char *copiedSignature = NULL;
  size_t copiedSignatureLength = 0;
  check(!gs_crypto_pem_certificate_signature(
            malformedPem, &copiedSignature, &copiedSignatureLength) &&
            copiedSignature == NULL,
        "malformed certificate signature rejection");
  free(copiedSignature);

  if (argc > 1) {
    char *sunshineCertificate = read_certificate_file(argv[1]);
    check(sunshineCertificate != NULL,
          "read installed Sunshine certificate fixture");
    copiedSignature = NULL;
    copiedSignatureLength = 0;
    check(sunshineCertificate != NULL &&
              gs_crypto_pem_certificate_signature(
                  sunshineCertificate, &copiedSignature,
                  &copiedSignatureLength) &&
              copiedSignature != NULL && copiedSignatureLength != 0,
          "installed Sunshine certificate signature extraction");
    check(sunshineCertificate != NULL &&
              gs_crypto_spki_pin_from_pem(sunshineCertificate, pinOne),
          "installed Sunshine certificate SPKI pin");
    free(copiedSignature);
    free(sunshineCertificate);
  }

  gs_crypto_identity_free(identity);
  memset(privateKey, 0, sizeof(privateKey));
  memset(secondPrivateKey, 0, sizeof(secondPrivateKey));
  gs_crypto_cleanup();

  if (failures != 0) {
    fprintf(stderr, "Vita crypto self-test failed (%d checks).\n", failures);
    return 1;
  }
  puts("Vita crypto self-test passed.");
  return 0;
}
