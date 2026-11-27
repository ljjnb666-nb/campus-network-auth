/* Build this standalone fixture with CJSON_BYTE_LIMIT=384, CJSON_NODE_LIMIT=16
 * and CJSON_NESTING_LIMIT=8 in both translation units. */
#include "cJSON.h"
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static size_t live;

static void *count_alloc(size_t bytes)
{
    void *p = malloc(bytes);
    if (p != NULL) live++;
    return p;
}

static void count_free(void *p)
{
    if (p != NULL)
    {
        assert(live > 0);
        live--;
        free(p);
    }
}

int main(void)
{
    cJSON_Hooks hooks = { count_alloc, count_free };
    char large[CJSON_BYTE_LIMIT + 1];
    memset(large, 'a', sizeof(large));
    large[sizeof(large) - 1] = '\0';
    cJSON_InitHooks(&hooks);

    cJSON *ordinary = cJSON_Parse("{\"local\":[1,2],\"ok\":true}");
    assert(ordinary != NULL);
    cJSON *copy = cJSON_Duplicate(ordinary, 1);
    assert(copy != NULL);
    char *printed = cJSON_PrintUnformatted(copy);
    assert(printed != NULL && strcmp(printed, "{\"local\":[1,2],\"ok\":true}") == 0);
    cJSON_free(printed);
    cJSON_Delete(copy);
    cJSON_Delete(ordinary);
    assert(live == 0);

    assert(cJSON_Parse(large) == NULL);
    assert(cJSON_ParseWithLength("null", CJSON_BYTE_LIMIT + 1) == NULL);
    assert(cJSON_Parse("[0,0,0,0,0,0,0,0,0,0,0,0,0,0,0,0]") == NULL);
    assert(cJSON_Parse("{\"unfinished\":[1,2,") == NULL);
    assert(live == 0);

    cJSON *owner = cJSON_Parse("[{\"value\":true}]");
    assert(owner != NULL);
    cJSON *borrower = cJSON_CreateArrayReference(owner->child);
    assert(borrower != NULL);
    cJSON_Delete(borrower);
    assert(cJSON_IsTrue(cJSON_GetObjectItem(cJSON_GetArrayItem(owner, 0), "value")));
    cJSON_Delete(owner);
    assert(live == 0);

    cJSON *array = cJSON_CreateArray();
    assert(array != NULL);
    for (size_t i = 0; i < CJSON_NODE_LIMIT; i++)
        assert(cJSON_AddItemToArray(array, cJSON_CreateNumber((double)i)));
    const size_t before = live;
    assert(cJSON_Duplicate(array, 1) == NULL);
    assert(cJSON_PrintUnformatted(array) == NULL);
    assert(live == before);
    cJSON_Delete(array);
    assert(live == 0);

    cJSON *text = cJSON_CreateString(large);
    assert(text != NULL);
    assert(cJSON_Duplicate(text, 0) == NULL);
    assert(cJSON_Print(text) == NULL);
    assert(!cJSON_PrintPreallocated(text, large, sizeof(large), 0));
    cJSON_Delete(text);
    assert(live == 0);

    cJSON *nested = cJSON_CreateArray();
    cJSON *level = nested;
    assert(nested != NULL);
    for (size_t i = 0; i < CJSON_NESTING_LIMIT; i++)
    {
        cJSON *child = cJSON_CreateArray();
        assert(cJSON_AddItemToArray(level, child));
        level = child;
    }
    assert(cJSON_AddItemToArray(level, cJSON_CreateNull()));
    assert(cJSON_Duplicate(nested, 1) == NULL);
    cJSON_Delete(nested);
    assert(live == 0);
    cJSON_InitHooks(NULL);
    puts("CJSON_RESOURCE_LIMITS_PASS");
    return 0;
}
