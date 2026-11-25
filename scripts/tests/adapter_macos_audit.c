/* macOS-only, offline resource fixture for local display sampling.
 * Never query live route tables or interfaces. */
#include "utils/platform/PlatformUtils.h"
#include "core/States.h"
#include "PlatformInternal.h"
#include <net/route.h>
#include <sys/sysctl.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static unsigned char fixture[512];
static size_t fixture_size, reported_size;
static unsigned query_calls, name_calls, live_allocations;
static bool fail_alloc, fail_read, oversized_read;

static void* audit_malloc(size_t size)
{
    if (fail_alloc) return NULL;
    void* p = malloc(size);
    if (p) ++live_allocations;
    return p;
}
static void audit_free(void* p)
{
    if (p) {
        assert(live_allocations > 0);
        --live_allocations;
        free(p);
    }
}
static int audit_sysctl(int* name, unsigned int namelen, void* oldp,
                        size_t* oldlenp, void* newp, size_t newlen)
{
    (void)name; (void)namelen; (void)newp; (void)newlen;
    ++query_calls;
    if (!oldp) { *oldlenp = reported_size; return 0; }
    if (fail_read) return -1;
    if (oversized_read) { ++*oldlenp; return 0; }
    assert(fixture_size <= *oldlenp && fixture_size <= sizeof(fixture));
    memcpy(oldp, fixture, fixture_size);
    *oldlenp = fixture_size;
    return 0;
}
static char* audit_if_indextoname(unsigned int idx, char* name)
{
    ++name_calls;
    assert(idx == 1);
    strcpy(name, "fixture");
    return name;
}
#define sysctl audit_sysctl
#define if_indextoname audit_if_indextoname
#define malloc audit_malloc
#define free audit_free
#include "NetAdapters.c"
#undef sysctl
#undef if_indextoname
#undef malloc
#undef free

char g_school_network_symbol[SCHOOL_NETWORK_SYMBOL] = "test";
void log_out(log_level_t level, const char* file, uint32_t line, const char* fmt, ...)
{
    (void)level; (void)file; (void)line; (void)fmt;
}

static void reset_fixture(void)
{
    assert(live_allocations == 0);
    memset(fixture, 0, sizeof(fixture));
    fixture_size = reported_size = 0;
    query_calls = name_calls = 0;
    fail_alloc = fail_read = oversized_read = false;
}
static void prepare_ipv4(void)
{
    struct rt_msghdr header = {0};
    struct sockaddr_in dst = {0};
    dst.sin_len = sizeof(dst);
    dst.sin_family = AF_INET;
    header.rtm_msglen = sizeof(header) + sizeof(dst);
    header.rtm_version = RTM_VERSION;
    header.rtm_flags = RTF_UP;
    header.rtm_index = 1;
    header.rtm_addrs = RTA_DST;
    fixture_size = reported_size = header.rtm_msglen;
    assert(fixture_size <= sizeof(fixture));
    memcpy(fixture, &header, sizeof(header));
    memcpy(fixture + sizeof(header), &dst, sizeof(dst));
}
static void run_case(bool found, unsigned queries)
{
    char out[ADAPTER_ID_LEN] = "stale";
    adapter_default_route(out, sizeof(out));
    assert(query_calls == queries && live_allocations == 0);
    if (found) assert(strcmp(out, "fixture") == 0 && name_calls == 1);
    else assert(out[0] == '\0' && name_calls == 0);
}
int main(void)
{
    reset_fixture(); prepare_ipv4(); run_case(true, 2);
    reset_fixture(); run_case(false, 1);
    reset_fixture(); reported_size = ADAPTER_INFO_MAX_BYTES + 1u; run_case(false, 1);
    reset_fixture(); prepare_ipv4(); fail_alloc = true; run_case(false, 1);
    reset_fixture(); prepare_ipv4(); fail_read = true; run_case(false, 2);
    reset_fixture(); prepare_ipv4(); oversized_read = true; run_case(false, 2);
    reset_fixture(); prepare_ipv4(); fixture_size = sizeof(struct rt_msghdr) - 1; run_case(false, 2);
    reset_fixture(); prepare_ipv4(); fixture_size -= 1; run_case(false, 2);
    reset_fixture(); prepare_ipv4();
    struct rt_msghdr header;
    memcpy(&header, fixture, sizeof(header));
    header.rtm_msglen = 1;
    memcpy(fixture, &header, sizeof(header));
    run_case(false, 2);
    reset_fixture(); prepare_ipv4();
    fixture[sizeof(struct rt_msghdr)] = 255;
    run_case(false, 2);
    reset_fixture(); prepare_ipv4();
    fixture[sizeof(struct rt_msghdr)] = 1;
    run_case(false, 2);
    reset_fixture(); prepare_ipv4(); run_case(true, 2);
    puts("MACOS_LOCAL_ADAPTER_RESOURCE_PASS cases=12");
    return 0;
}
