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

#include "xml.h"
#include "errors.h"

#include <ctype.h>
#include <errno.h>
#include <expat.h>
#include <limits.h>
#include <stdlib.h>
#include <string.h>

#define STATUS_OK 200

/* HTTP already rejects responses above 1 MiB. Keep the same boundary here so
 * direct callers cannot bypass it, then bound the allocations created from a
 * valid-sized document. These limits cover practical Sunshine responses while
 * protecting the Vita's smaller memory budget from hostile list expansion. */
#define XML_MAX_DOCUMENT_BYTES (1024u * 1024u)
#define XML_MAX_SEARCH_TEXT_BYTES (256u * 1024u)
#define XML_MAX_APP_TITLE_BYTES 255u
#define XML_MAX_NUMBER_TEXT_BYTES 64u
#define XML_MAX_STATUS_MESSAGE_BYTES 1024u
#define XML_MAX_APPS 512u
#define XML_MAX_MODES 512u
#define XML_MAX_DEPTH 64u

struct xml_parse_state {
  XML_Parser parser;
  unsigned int depth;
  int failure;
};

struct xml_search_query {
  struct xml_parse_state parse;
  const char *node;
  char *memory;
  size_t size;
  unsigned int capture_depth;
};

enum app_field {
  APP_FIELD_NONE,
  APP_FIELD_ID,
  APP_FIELD_TITLE
};

struct xml_app_query {
  struct xml_parse_state parse;
  PAPP_LIST list;
  PAPP_LIST current;
  char *memory;
  size_t size;
  unsigned int app_depth;
  unsigned int field_depth;
  unsigned int count;
  enum app_field field;
  int have_id;
  int have_title;
};

enum mode_field {
  MODE_FIELD_NONE,
  MODE_FIELD_HEIGHT,
  MODE_FIELD_WIDTH,
  MODE_FIELD_REFRESH
};

struct xml_mode_query {
  struct xml_parse_state parse;
  PDISPLAY_MODE list;
  PDISPLAY_MODE current;
  char *memory;
  size_t size;
  unsigned int mode_depth;
  unsigned int field_depth;
  unsigned int count;
  enum mode_field field;
  int have_height;
  int have_width;
  int have_refresh;
};

struct xml_status_query {
  struct xml_parse_state parse;
  int status;
  int saw_root;
  int saw_status;
  int saw_message;
  char message[XML_MAX_STATUS_MESSAGE_BYTES + 1];
};

/* gs_error is a borrowed pointer. A fixed buffer avoids leaking a heap copy on
 * every unsuccessful host request while keeping the message valid on return. */
static char xml_status_error[XML_MAX_STATUS_MESSAGE_BYTES + 1];

static void xml_fail(struct xml_parse_state *parse, int code, const char *message) {
  if (parse->failure != GS_OK)
    return;

  parse->failure = code;
  gs_error = message;
  if (parse->parser != NULL)
    XML_StopParser(parse->parser, XML_FALSE);
}

static int xml_enter_element(struct xml_parse_state *parse) {
  if (parse->failure != GS_OK)
    return 0;

  if (parse->depth >= XML_MAX_DEPTH) {
    xml_fail(parse, GS_INVALID, "Sunshine returned XML nested too deeply");
    return 0;
  }

  parse->depth++;
  return 1;
}

static void xml_leave_element(struct xml_parse_state *parse) {
  if (parse->depth > 0)
    parse->depth--;
}

static int xml_append_text(struct xml_parse_state *parse, char **memory,
                           size_t *size, const XML_Char *text, int length,
                           size_t limit) {
  char *resized;
  size_t append_size;
  size_t new_size;

  if (parse->failure != GS_OK || length <= 0)
    return parse->failure == GS_OK;

  append_size = (size_t) length;
  if (*size > limit || append_size > limit - *size) {
    xml_fail(parse, GS_INVALID, "Sunshine returned an XML text value that is too large");
    return 0;
  }

  new_size = *size + append_size;
  resized = (char *) realloc(*memory, new_size + 1);
  if (resized == NULL) {
    /* Do not assign realloc() directly to the owned pointer. The original
     * allocation remains available for deterministic cleanup on OOM. */
    xml_fail(parse, GS_OUT_OF_MEMORY, "Not enough memory to parse Sunshine XML");
    return 0;
  }

  memcpy(resized + *size, text, append_size);
  resized[new_size] = '\0';
  *memory = resized;
  *size = new_size;
  return 1;
}

static int xml_begin_text(struct xml_parse_state *parse, char **memory,
                          size_t *size) {
  *memory = (char *) calloc(1, 1);
  *size = 0;
  if (*memory == NULL) {
    xml_fail(parse, GS_OUT_OF_MEMORY, "Not enough memory to parse Sunshine XML");
    return 0;
  }
  return 1;
}

static int xml_input_is_valid(char *data, size_t len) {
  if (data == NULL || len == 0) {
    gs_error = "Sunshine returned an empty XML response";
    return 0;
  }
  if (len > XML_MAX_DOCUMENT_BYTES) {
    gs_error = "Sunshine returned an unexpectedly large XML response";
    return 0;
  }
  return 1;
}

static XML_Parser xml_create_parser(struct xml_parse_state *parse) {
  parse->parser = XML_ParserCreate("UTF-8");
  if (parse->parser == NULL) {
    parse->failure = GS_OUT_OF_MEMORY;
    gs_error = "Not enough memory to create the Sunshine XML parser";
  }
  return parse->parser;
}

static int xml_parse_document(struct xml_parse_state *parse, char *data,
                              size_t len) {
  if (XML_Parse(parse->parser, data, (int) len, XML_TRUE) == XML_STATUS_ERROR) {
    enum XML_Error error;
    if (parse->failure != GS_OK)
      return parse->failure;
    error = XML_GetErrorCode(parse->parser);
    if (error == XML_ERROR_NO_MEMORY) {
      gs_error = "Not enough memory to parse Sunshine XML";
      return GS_OUT_OF_MEMORY;
    }
    gs_error = XML_ErrorString(error);
    return GS_INVALID;
  }
  return parse->failure;
}

static int parse_unsigned_text(const char *text, unsigned int *value,
                               int allow_zero) {
  const char *start;
  char *end;
  unsigned long parsed;

  if (text == NULL || value == NULL)
    return 0;

  start = text;
  while (*start != '\0' && isspace((unsigned char) *start))
    start++;
  if (*start == '\0' || *start == '+' || *start == '-')
    return 0;

  errno = 0;
  parsed = strtoul(start, &end, 10);
  if (errno == ERANGE || end == start || parsed > UINT_MAX)
    return 0;

  while (*end != '\0' && isspace((unsigned char) *end))
    end++;
  if (*end != '\0' || (!allow_zero && parsed == 0))
    return 0;

  *value = (unsigned int) parsed;
  return 1;
}

static void free_app_list(PAPP_LIST list) {
  while (list != NULL) {
    PAPP_LIST next = list->next;
    free(list->name);
    free(list);
    list = next;
  }
}

static void free_mode_list(PDISPLAY_MODE list) {
  while (list != NULL) {
    PDISPLAY_MODE next = list->next;
    free(list);
    list = next;
  }
}

static void XMLCALL xml_start_search_element(void *user_data, const char *name,
                                              const char **attributes) {
  struct xml_search_query *query = (struct xml_search_query *) user_data;
  (void) attributes;

  if (!xml_enter_element(&query->parse))
    return;

  if (query->capture_depth == 0 && strcmp(query->node, name) == 0) {
    query->capture_depth = query->parse.depth;
  }
}

static void XMLCALL xml_end_search_element(void *user_data, const char *name) {
  struct xml_search_query *query = (struct xml_search_query *) user_data;

  if (query->capture_depth == query->parse.depth &&
      strcmp(query->node, name) == 0)
    query->capture_depth = 0;
  xml_leave_element(&query->parse);
}

static void XMLCALL xml_write_search_data(void *user_data, const XML_Char *text,
                                           int length) {
  struct xml_search_query *query = (struct xml_search_query *) user_data;
  if (query->capture_depth != 0)
    xml_append_text(&query->parse, &query->memory, &query->size, text,
                    length, XML_MAX_SEARCH_TEXT_BYTES);
}

static void XMLCALL xml_start_app_element(void *user_data, const char *name,
                                          const char **attributes) {
  struct xml_app_query *query = (struct xml_app_query *) user_data;
  PAPP_LIST app;
  (void) attributes;

  if (!xml_enter_element(&query->parse))
    return;

  if (strcmp("App", name) == 0) {
    if (query->current != NULL) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a nested App entry");
      return;
    }
    if (query->count >= XML_MAX_APPS) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned too many App entries");
      return;
    }

    app = (PAPP_LIST) calloc(1, sizeof(APP_LIST));
    if (app == NULL) {
      xml_fail(&query->parse, GS_OUT_OF_MEMORY, "Not enough memory for the Sunshine App list");
      return;
    }
    app->next = query->list;
    query->list = app;
    query->current = app;
    query->app_depth = query->parse.depth;
    query->have_id = 0;
    query->have_title = 0;
    query->count++;
    return;
  }

  if (query->current == NULL || query->field != APP_FIELD_NONE)
    return;

  if (strcmp("ID", name) == 0) {
    if (query->have_id) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a duplicate App ID");
      return;
    }
    if (xml_begin_text(&query->parse, &query->memory, &query->size)) {
      query->field = APP_FIELD_ID;
      query->field_depth = query->parse.depth;
    }
  }
  else if (strcmp("AppTitle", name) == 0) {
    if (query->have_title) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a duplicate App title");
      return;
    }
    if (xml_begin_text(&query->parse, &query->memory, &query->size)) {
      query->field = APP_FIELD_TITLE;
      query->field_depth = query->parse.depth;
    }
  }
}

static void XMLCALL xml_end_app_element(void *user_data, const char *name) {
  struct xml_app_query *query = (struct xml_app_query *) user_data;
  unsigned int id;

  if (query->parse.failure != GS_OK) {
    xml_leave_element(&query->parse);
    return;
  }

  if (query->field != APP_FIELD_NONE &&
      query->field_depth == query->parse.depth) {
    if (query->field == APP_FIELD_ID && strcmp("ID", name) == 0) {
      if (!parse_unsigned_text(query->memory, &id, 0) || id > INT_MAX) {
        xml_fail(&query->parse, GS_INVALID, "Sunshine returned an invalid App ID");
      }
      else {
        query->current->id = (int) id;
        query->have_id = 1;
      }
      free(query->memory);
      query->memory = NULL;
    }
    else if (query->field == APP_FIELD_TITLE && strcmp("AppTitle", name) == 0) {
      if (query->size == 0) {
        xml_fail(&query->parse, GS_INVALID, "Sunshine returned an App without a title");
        free(query->memory);
      }
      else {
        query->current->name = query->memory;
        query->have_title = 1;
      }
      query->memory = NULL;
    }
    query->size = 0;
    query->field = APP_FIELD_NONE;
    query->field_depth = 0;
  }

  if (query->parse.failure == GS_OK && query->current != NULL &&
      query->app_depth == query->parse.depth && strcmp("App", name) == 0) {
    if (!query->have_id || !query->have_title) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned an incomplete App entry");
    }
    else {
      query->current = NULL;
      query->app_depth = 0;
    }
  }

  xml_leave_element(&query->parse);
}

static void XMLCALL xml_write_app_data(void *user_data, const XML_Char *text,
                                       int length) {
  struct xml_app_query *query = (struct xml_app_query *) user_data;
  size_t limit;

  if (query->field == APP_FIELD_NONE)
    return;
  limit = query->field == APP_FIELD_TITLE ? XML_MAX_APP_TITLE_BYTES :
                                            XML_MAX_NUMBER_TEXT_BYTES;
  xml_append_text(&query->parse, &query->memory, &query->size, text,
                  length, limit);
}

static void XMLCALL xml_start_mode_element(void *user_data, const char *name,
                                           const char **attributes) {
  struct xml_mode_query *query = (struct xml_mode_query *) user_data;
  PDISPLAY_MODE mode;
  (void) attributes;

  if (!xml_enter_element(&query->parse))
    return;

  if (strcmp("DisplayMode", name) == 0) {
    if (query->current != NULL) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a nested display mode");
      return;
    }
    if (query->count >= XML_MAX_MODES) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned too many display modes");
      return;
    }

    mode = (PDISPLAY_MODE) calloc(1, sizeof(DISPLAY_MODE));
    if (mode == NULL) {
      xml_fail(&query->parse, GS_OUT_OF_MEMORY, "Not enough memory for Sunshine display modes");
      return;
    }
    mode->next = query->list;
    query->list = mode;
    query->current = mode;
    query->mode_depth = query->parse.depth;
    query->have_height = 0;
    query->have_width = 0;
    query->have_refresh = 0;
    query->count++;
    return;
  }

  if (query->current == NULL || query->field != MODE_FIELD_NONE)
    return;

  if (strcmp("Height", name) == 0) {
    if (query->have_height) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a duplicate display height");
      return;
    }
    query->field = MODE_FIELD_HEIGHT;
  }
  else if (strcmp("Width", name) == 0) {
    if (query->have_width) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a duplicate display width");
      return;
    }
    query->field = MODE_FIELD_WIDTH;
  }
  else if (strcmp("RefreshRate", name) == 0) {
    if (query->have_refresh) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a duplicate refresh rate");
      return;
    }
    query->field = MODE_FIELD_REFRESH;
  }
  else {
    return;
  }

  if (xml_begin_text(&query->parse, &query->memory, &query->size))
    query->field_depth = query->parse.depth;
  else
    query->field = MODE_FIELD_NONE;
}

static void XMLCALL xml_end_mode_element(void *user_data, const char *name) {
  struct xml_mode_query *query = (struct xml_mode_query *) user_data;
  unsigned int value;

  if (query->parse.failure != GS_OK) {
    xml_leave_element(&query->parse);
    return;
  }

  if (query->field != MODE_FIELD_NONE &&
      query->field_depth == query->parse.depth) {
    if (!parse_unsigned_text(query->memory, &value, 0)) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned an invalid display mode value");
    }
    else if (query->field == MODE_FIELD_HEIGHT && strcmp("Height", name) == 0) {
      query->current->height = value;
      query->have_height = 1;
    }
    else if (query->field == MODE_FIELD_WIDTH && strcmp("Width", name) == 0) {
      query->current->width = value;
      query->have_width = 1;
    }
    else if (query->field == MODE_FIELD_REFRESH && strcmp("RefreshRate", name) == 0) {
      query->current->refresh = value;
      query->have_refresh = 1;
    }
    else {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned a malformed display mode");
    }
    free(query->memory);
    query->memory = NULL;
    query->size = 0;
    query->field = MODE_FIELD_NONE;
    query->field_depth = 0;
  }

  if (query->parse.failure == GS_OK && query->current != NULL &&
      query->mode_depth == query->parse.depth &&
      strcmp("DisplayMode", name) == 0) {
    if (!query->have_height || !query->have_width || !query->have_refresh) {
      xml_fail(&query->parse, GS_INVALID, "Sunshine returned an incomplete display mode");
    }
    else {
      query->current = NULL;
      query->mode_depth = 0;
    }
  }

  xml_leave_element(&query->parse);
}

static void XMLCALL xml_write_mode_data(void *user_data, const XML_Char *text,
                                        int length) {
  struct xml_mode_query *query = (struct xml_mode_query *) user_data;
  if (query->field != MODE_FIELD_NONE)
    xml_append_text(&query->parse, &query->memory, &query->size, text,
                    length, XML_MAX_NUMBER_TEXT_BYTES);
}

static void XMLCALL xml_start_status_element(void *user_data, const char *name,
                                             const char **attributes) {
  struct xml_status_query *query = (struct xml_status_query *) user_data;
  unsigned int status;
  size_t message_length;
  int i;

  if (!xml_enter_element(&query->parse))
    return;
  if (query->parse.depth != 1 || strcmp("root", name) != 0)
    return;

  query->saw_root = 1;
  for (i = 0; attributes[i] != NULL; i += 2) {
    if (strcmp("status_code", attributes[i]) == 0) {
      if (query->saw_status ||
          !parse_unsigned_text(attributes[i + 1], &status, 1) ||
          status > INT_MAX) {
        /* Some callers historically distinguish a rejected status from XML
         * syntax errors. Treat malformed status metadata as a rejection so it
         * can never fall through into parsing the response payload. */
        xml_fail(&query->parse, GS_ERROR, "Sunshine returned an invalid XML status code");
        return;
      }
      query->status = (int) status;
      query->saw_status = 1;
    }
    else if (strcmp("status_message", attributes[i]) == 0) {
      if (query->saw_message) {
        xml_fail(&query->parse, GS_ERROR, "Sunshine returned duplicate XML status text");
        return;
      }
      message_length = strlen(attributes[i + 1]);
      if (message_length > XML_MAX_STATUS_MESSAGE_BYTES) {
        xml_fail(&query->parse, GS_ERROR, "Sunshine returned an XML status message that is too large");
        return;
      }
      memcpy(query->message, attributes[i + 1], message_length + 1);
      query->saw_message = 1;
    }
  }
}

static void XMLCALL xml_end_status_element(void *user_data, const char *name) {
  struct xml_status_query *query = (struct xml_status_query *) user_data;
  (void) name;
  xml_leave_element(&query->parse);
}

int xml_search(char *data, size_t len, char *node, char **result) {
  struct xml_search_query query;
  XML_Parser parser;
  int ret;

  if (result == NULL || node == NULL || node[0] == '\0') {
    gs_error = "Invalid XML search request";
    return GS_INVALID;
  }
  *result = NULL;
  if (!xml_input_is_valid(data, len))
    return GS_INVALID;

  memset(&query, 0, sizeof(query));
  query.node = node;
  if (!xml_begin_text(&query.parse, &query.memory, &query.size))
    return query.parse.failure;

  parser = xml_create_parser(&query.parse);
  if (parser == NULL) {
    free(query.memory);
    return query.parse.failure;
  }

  XML_SetUserData(parser, &query);
  XML_SetElementHandler(parser, xml_start_search_element,
                        xml_end_search_element);
  XML_SetCharacterDataHandler(parser, xml_write_search_data);
  ret = xml_parse_document(&query.parse, data, len);
  XML_ParserFree(parser);

  if (ret != GS_OK) {
    free(query.memory);
    return ret;
  }
  /* Preserve the GameStream API's historical optional-field behavior: a
   * missing node is an owned empty string. Required-field callers already
   * reject empty values, while older Sunshine/GFE variants can omit optional
   * fields such as codec capabilities and MAC address. */
  *result = query.memory;
  return GS_OK;
}

int xml_applist(char *data, size_t len, PAPP_LIST *app_list) {
  struct xml_app_query query;
  XML_Parser parser;
  int ret;

  if (app_list == NULL) {
    gs_error = "Invalid Sunshine App list output";
    return GS_INVALID;
  }
  *app_list = NULL;
  if (!xml_input_is_valid(data, len))
    return GS_INVALID;

  memset(&query, 0, sizeof(query));
  parser = xml_create_parser(&query.parse);
  if (parser == NULL)
    return query.parse.failure;

  XML_SetUserData(parser, &query);
  XML_SetElementHandler(parser, xml_start_app_element, xml_end_app_element);
  XML_SetCharacterDataHandler(parser, xml_write_app_data);
  ret = xml_parse_document(&query.parse, data, len);
  XML_ParserFree(parser);

  if (ret != GS_OK) {
    free(query.memory);
    free_app_list(query.list);
    return ret;
  }

  *app_list = query.list;
  return GS_OK;
}

int xml_modelist(char *data, size_t len, PDISPLAY_MODE *mode_list) {
  struct xml_mode_query query;
  XML_Parser parser;
  int ret;

  if (mode_list == NULL) {
    gs_error = "Invalid Sunshine display mode output";
    return GS_INVALID;
  }
  *mode_list = NULL;
  if (!xml_input_is_valid(data, len))
    return GS_INVALID;

  memset(&query, 0, sizeof(query));
  parser = xml_create_parser(&query.parse);
  if (parser == NULL)
    return query.parse.failure;

  XML_SetUserData(parser, &query);
  XML_SetElementHandler(parser, xml_start_mode_element,
                        xml_end_mode_element);
  XML_SetCharacterDataHandler(parser, xml_write_mode_data);
  ret = xml_parse_document(&query.parse, data, len);
  XML_ParserFree(parser);

  if (ret != GS_OK) {
    free(query.memory);
    free_mode_list(query.list);
    return ret;
  }

  *mode_list = query.list;
  return GS_OK;
}

int xml_status(char *data, size_t len) {
  struct xml_status_query query;
  XML_Parser parser;
  int ret;

  if (!xml_input_is_valid(data, len))
    return GS_INVALID;

  memset(&query, 0, sizeof(query));
  parser = xml_create_parser(&query.parse);
  if (parser == NULL)
    return query.parse.failure;

  XML_SetUserData(parser, &query);
  XML_SetElementHandler(parser, xml_start_status_element,
                        xml_end_status_element);
  ret = xml_parse_document(&query.parse, data, len);
  XML_ParserFree(parser);
  if (ret != GS_OK)
    return ret;

  if (!query.saw_root || !query.saw_status) {
    gs_error = "Sunshine response is missing an XML status code";
    return GS_ERROR;
  }
  if (query.status == STATUS_OK)
    return GS_OK;

  if (query.saw_message && query.message[0] != '\0') {
    memcpy(xml_status_error, query.message, sizeof(xml_status_error));
    xml_status_error[sizeof(xml_status_error) - 1] = '\0';
    gs_error = xml_status_error;
  }
  else {
    gs_error = "Sunshine rejected the request without a status message";
  }
  /* Current Sunshine returns this exact status from its HTTPS certificate
   * verifier when the pinned server is still genuine but no longer trusts the
   * Vita certificate.  Preserve that distinction so saved hosts can offer a
   * fresh PIN exchange instead of being misreported as offline. */
  return query.status == 401 ? GS_CLIENT_UNAUTHORIZED : GS_ERROR;
}
