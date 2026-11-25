/* Standalone offline test: system adapter APIs and collection allocation
 * are replaced only in this translation unit. No live network calls. */
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#include <winsock2.h>
#include <ws2ipdef.h>
#include <windows.h>
#include <iphlpapi.h>
#include <netioapi.h>
#include <ws2tcpip.h>
#include <assert.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static size_t allocations, frees, live, fail_at;
static void* tracked_malloc(size_t n)
{
    ++allocations;
    if (allocations == fail_at) return NULL;
    void* p = malloc(n);
    if (p) ++live;
    return p;
}
static void* tracked_realloc(void* p, size_t n)
{
    ++allocations;
    if (allocations == fail_at) return NULL;
    const bool fresh = p == NULL;
    void* q = realloc(p, n);
    if (q && fresh) ++live;
    return q;
}
static void tracked_free(void* p)
{
    if (p) { assert(live > 0); --live; ++frees; free(p); }
}

enum test_mode { NORMAL, EMPTY, API_ERROR, HUGE, ZERO, CHURN, GROW };
static enum test_mode mode;
static unsigned info_calls, address_calls;
static bool fail_entry;

static DWORD WINAPI fake_info(PIP_ADAPTER_INFO out, PULONG size)
{
    ++info_calls;
    if (mode == EMPTY) return ERROR_NO_DATA;
    if (mode == API_ERROR && out) return ERROR_INVALID_DATA;
    if (mode == HUGE) { *size = 0xffffffffu; return ERROR_BUFFER_OVERFLOW; }
    if (mode == ZERO) { *size = 0; return ERROR_BUFFER_OVERFLOW; }
    if (!out || mode == CHURN || (mode == GROW && info_calls == 2))
    {
        *size = sizeof(*out) * 2u;
        return ERROR_BUFFER_OVERFLOW;
    }
    assert(*size >= sizeof(*out));
    memset(out, 0, sizeof(*out));
    strcpy(out->Description, "synthetic");
    strcpy(out->IpAddressList.IpAddress.String, "192.0.2.1");
    return NO_ERROR;
}
static ULONG WINAPI fake_addresses(ULONG family, ULONG flags, PVOID reserved,
                                   PIP_ADAPTER_ADDRESSES out, PULONG size)
{
    (void)family; (void)flags; (void)reserved;
    ++address_calls;
    if (mode == EMPTY) return ERROR_NO_DATA;
    if (mode == API_ERROR) return ERROR_INVALID_DATA;
    if (mode == HUGE) { *size = 0xffffffffu; return ERROR_BUFFER_OVERFLOW; }
    if (mode == ZERO) { *size = 0; return ERROR_BUFFER_OVERFLOW; }
    if (mode == CHURN || (mode == GROW && address_calls == 1))
    {
        *size = 32768;
        return ERROR_BUFFER_OVERFLOW;
    }
    assert(*size >= sizeof(*out));
    memset(out, 0, sizeof(*out));
    out->Length = sizeof(*out);
    out->IfIndex = 17;
    out->IfType = IF_TYPE_ETHERNET_CSMACD;
    out->OperStatus = IfOperStatusUp;
    out->AdapterName = (char*)"{TEST-ADAPTER}";
    out->FriendlyName = L"synthetic";
    return NO_ERROR;
}
static DWORD WINAPI fake_best(IPAddr addr, PDWORD index)
{
    (void)addr;
    *index = 17;
    return NO_ERROR;
}
static NETIO_STATUS WINAPI fake_entry(PMIB_IF_ROW2 row)
{
    if (fail_entry) return ERROR_NOT_FOUND;
    row->InterfaceAndOperStatusFlags.HardwareInterface = 1;
    row->MediaConnectState = MediaConnectStateConnected;
    row->InOctets = 100;
    row->OutOctets = 200;
    return NO_ERROR;
}
#define GetAdaptersInfo fake_info
#define GetAdaptersAddresses fake_addresses
#define GetBestInterface fake_best
#define GetIfEntry2 fake_entry
#define malloc tracked_malloc
#define realloc tracked_realloc
#define free tracked_free
#include "NetAdapters.c"
#undef malloc
#undef realloc
#undef free

char g_school_network_symbol[SCHOOL_NETWORK_SYMBOL] = "test";
void log_out(log_level_t level, const char* file, uint32_t line, const char* fmt, ...)
{
    (void)level; (void)file; (void)line; (void)fmt;
}
static void reset(enum test_mode next, size_t fail)
{
    assert(live == 0);
    allocations = frees = info_calls = address_calls = 0;
    mode = next;
    fail_at = fail;
    fail_entry = false;
}
static void list_case(enum test_mode next, size_t fail, int count, unsigned calls)
{
    reset(next, fail);
    char* json = get_adapters_json();
    if (count < 0) assert(json == NULL);
    else
    {
        assert(json);
        cJSON* root = cJSON_Parse(json);
        cJSON_free(json);
        assert(root);
        const cJSON* rows = cJSON_GetObjectItemCaseSensitive(root, "adapters");
        assert(cJSON_IsArray(rows) && cJSON_GetArraySize(rows) == count);
        cJSON_Delete(root);
    }
    assert(info_calls == calls && live == 0);
}
static void traffic_case(enum test_mode next, size_t fail, bool entry_fail,
                         uint8_t count, unsigned calls)
{
    reset(next, fail);
    fail_entry = entry_fail;
    struct { adapter_stats_t row; unsigned guard; } sample = {0};
    sample.guard = 0x12345678u;
    char id[ADAPTER_ID_LEN] = "stale";
    assert(get_adapters_stats(&sample.row, 1, id, sizeof(id)) == count);
    assert(address_calls == calls && live == 0 && sample.guard == 0x12345678u);
    if (count)
    {
        assert(strcmp(id, "test-adapter") == 0);
        assert(sample.row.rx_bytes == 100 && sample.row.tx_bytes == 200);
    }
    else assert(id[0] == '\0');
}
int main(void)
{
    list_case(NORMAL, 0, 1, 2);
    list_case(EMPTY, 0, 0, 1);
    list_case(API_ERROR, 0, -1, 2);
    list_case(HUGE, 0, -1, 1);
    list_case(ZERO, 0, -1, 1);
    list_case(CHURN, 0, -1, 4);
    list_case(GROW, 0, 1, 3);
    list_case(NORMAL, 1, -1, 1);
    list_case(NORMAL, 2, -1, 2);
    list_case(GROW, 2, -1, 2);
    list_case(NORMAL, 0, 1, 2);
    traffic_case(NORMAL, 0, false, 1, 1);
    traffic_case(EMPTY, 0, false, 0, 1);
    traffic_case(API_ERROR, 0, false, 0, 1);
    traffic_case(HUGE, 0, false, 0, 1);
    traffic_case(ZERO, 0, false, 0, 1);
    traffic_case(CHURN, 0, false, 0, 3);
    traffic_case(GROW, 0, false, 1, 2);
    traffic_case(NORMAL, 1, false, 0, 0);
    traffic_case(GROW, 2, false, 0, 1);
    traffic_case(NORMAL, 0, true, 0, 1);
    traffic_case(NORMAL, 0, false, 1, 1);
    puts("WINDOWS_LOCAL_ADAPTER_RESOURCE_PASS cases=22");
    return 0;
}
