#include "../libgamestream/errors.h"
#include "../libgamestream/xml.h"

#include <stdio.h>
#include <string.h>

const char *gs_error;

static int expect_status(
    char *xml, int expected, const char *expected_error, const char *name) {
  gs_error = NULL;
  int actual = xml_status(xml, strlen(xml));
  if (actual != expected) {
    fprintf(stderr, "%s: expected %d, got %d\n", name, expected, actual);
    return 1;
  }
  if (expected_error != NULL &&
      (gs_error == NULL || strcmp(gs_error, expected_error) != 0)) {
    fprintf(stderr, "%s: status message was not preserved\n", name);
    return 1;
  }
  return 0;
}

int main(void) {
  char unauthorized[] =
      "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n"
      "<root status_code=\"401\" query=\"/serverinfo\" "
      "status_message=\"The client is not authorized. Certificate verification failed.\"/>";
  char rejected[] =
      "<root status_code=\"403\" status_message=\"Request rejected\"/>";
  char accepted[] = "<root status_code=\"200\"/>";

  if (expect_status(
          unauthorized, GS_CLIENT_UNAUTHORIZED,
          "The client is not authorized. Certificate verification failed.",
          "current Sunshine unauthorized serverinfo") != 0 ||
      expect_status(rejected, GS_ERROR, "Request rejected", "ordinary rejection") != 0 ||
      expect_status(accepted, GS_OK, NULL, "successful response") != 0) {
    return 1;
  }

  puts("Sunshine XML status protocol vectors: PASS");
  return 0;
}
