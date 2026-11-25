#ifdef _WIN32
// GetIfEntry2 / MIB_IF_ROW2 (64 位计数器) 要 Vista 以上的头才会露出来
#ifndef _WIN32_WINNT
#define _WIN32_WINNT 0x0601
#endif
#endif

#include "utils/platform/PlatformUtils.h"

#ifdef _WIN32
// 必须排在任何可能带进 windows.h 的头之前
#include <winsock2.h>
// netioapi.h 里的 MIB_IF_ROW2 / GetIfEntry2 (64 位计数器) 要求已经包含过 ws2ipdef.h,
// 而 iphlpapi.h 是通过 __IPHLPAPI_H__ 分支包含 netioapi.h 的、不会替你补上这一头
#include <ws2ipdef.h>
#include <windows.h>
#endif

#include "core/States.h"

#include "PlatformInternal.h"
#include "utils/log/Logger.h"

#ifdef _WIN32
#include <netioapi.h>
#include <ws2tcpip.h> // inet_ntop
#else
#include <arpa/inet.h> // inet_ntop
#endif

#include "cJSON/cJSON.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

typedef struct
{
    char ip[IP_LEN];
    char name[NAME_LENGTH];
} adapter_t;

/* UI enumeration owns its list per call. Keep a bounded collection;
 * failure never publishes the previous call's rows or a partial document. */
#define ADAPTER_LIST_MAX 255u
#define ADAPTER_INFO_MAX_BYTES (4u * 1024u * 1024u)
typedef struct
{
    adapter_t* rows;
    size_t count;
    size_t capacity;
} adapter_list_t;

static void adapter_list_clear(adapter_list_t* list)
{
    free(list->rows);
    *list = (adapter_list_t){0};
}

static bool adapter_list_append(adapter_list_t* list, const char* name, const char* ip)
{
    if (!name || !ip || list->count >= ADAPTER_LIST_MAX) return false;
    if (list->count == list->capacity)
    {
        size_t capacity = list->capacity ? list->capacity * 2u : 8u;
        if (capacity > ADAPTER_LIST_MAX) capacity = ADAPTER_LIST_MAX;
        adapter_t* rows = realloc(list->rows, capacity * sizeof(*rows));
        if (!rows) return false;
        list->rows = rows;
        list->capacity = capacity;
    }
    adapter_t* row = &list->rows[list->count++];
    snprintf(row->name, sizeof(row->name), "%s", name);
    snprintf(row->ip, sizeof(row->ip), "%s", ip);
    return true;
}

static bool get_adapters(adapter_list_t* list)
{
#ifdef _WIN32
    ULONG size = 0;
    DWORD rc = GetAdaptersInfo(NULL, &size);
    if (rc == ERROR_NO_DATA) return true;
    if (rc != ERROR_BUFFER_OVERFLOW) return false;
    /* Adapter topology can change between size query and enumeration. */
    for (unsigned attempt = 0; attempt < 3; ++attempt)
    {
        if (size == 0 || size > ADAPTER_INFO_MAX_BYTES) return false;
        PIP_ADAPTER_INFO buffer = malloc(size);
        if (!buffer) return false;
        rc = GetAdaptersInfo(buffer, &size);
        if (rc == ERROR_BUFFER_OVERFLOW)
        {
            free(buffer);
            continue;
        }
        bool ok = rc == NO_ERROR || rc == ERROR_NO_DATA;
        if (rc == NO_ERROR)
        {
            for (PIP_ADAPTER_INFO row = buffer; row; row = row->Next)
            {
                if (!adapter_list_append(list, row->Description, row->IpAddressList.IpAddress.String))
                {
                    ok = false;
                    break;
                }
            }
        }
        free(buffer);
        return ok;
    }
    return false;
#else
    struct ifaddrs* entries = NULL;
    if (getifaddrs(&entries) != 0) return false;
    bool ok = true;
    for (struct ifaddrs* ifa = entries; ifa; ifa = ifa->ifa_next)
    {
        if (!ifa->ifa_name || !ifa->ifa_addr || ifa->ifa_addr->sa_family != AF_INET) continue;
        if (strcmp(ifa->ifa_name, "lo") == 0) continue;
        char ip[INET_ADDRSTRLEN];
        const struct sockaddr_in* addr = (const struct sockaddr_in*)ifa->ifa_addr;
        if (!inet_ntop(AF_INET, &addr->sin_addr, ip, sizeof(ip))) continue;
        if (!adapter_list_append(list, ifa->ifa_name, ip))
        {
            ok = false;
            break;
        }
    }
    freeifaddrs(entries);
    return ok;
#endif
}

static char* render_adapter_list(const adapter_list_t* list, const char* symbol)
{
    cJSON* root = NULL;
    cJSON* adapters = NULL;
    cJSON* item = NULL;
    char* json = NULL;
    if (!list || !symbol || list->count > ADAPTER_LIST_MAX ||
        list->count > list->capacity || (list->count && !list->rows)) return NULL;
    if (!(root = cJSON_CreateObject()) || !(adapters = cJSON_CreateArray())) goto cleanup;
    for (size_t i = 0; i < list->count; ++i)
    {
        if (!list->rows[i].name[0]) break; /* preserve existing display semantics */
        if (!(item = cJSON_CreateObject()) ||
            !cJSON_AddStringToObject(item, "name", list->rows[i].name) ||
            !cJSON_AddStringToObject(item, "ip", list->rows[i].ip) ||
            !cJSON_AddItemToArray(adapters, item)) goto cleanup;
        item = NULL;
    }
    if (!cJSON_AddItemToObject(root, "adapters", adapters)) goto cleanup;
    adapters = NULL;
    if (!cJSON_AddStringToObject(root, "school_network_symbol", symbol)) goto cleanup;
    json = cJSON_Print(root);
cleanup:
    cJSON_Delete(item);
    cJSON_Delete(adapters);
    cJSON_Delete(root);
    return json;
}

char* get_adapters_json(void)
{
    adapter_list_t list = {0};
    char* json = NULL;
    if (get_adapters(&list)) json = render_adapter_list(&list, g_school_network_symbol);
    adapter_list_clear(&list);
    return json;
}

#ifdef _WIN32

/**
 * @brief 网卡 GUID ("{xxxx-...}") 转成界面用的 id: 小写, 去掉花括号
 *
 * 与 Windows 上 NetworkInterface.Id 的写法保持一致, 网页里存过的"上次选的网卡"
 * 换到这个接口之后还能继续用
 */
static void adapter_id_from_guid(const char* guid, char* out, const size_t out_len)
{
    if (out_len == 0) return;
    size_t n = 0;
    for (const char* p = guid; p != NULL && *p != '\0' && n + 1 < out_len; p++)
    {
        if (*p == '{' || *p == '}') continue;
        char c = *p;
        if (c >= 'A' && c <= 'Z') c = (char)(c - 'A' + 'a');
        out[n++] = c;
    }
    out[n] = '\0';
}

/**
 * @brief UTF-16 转 UTF-8 (网卡显示名可能是中文)
 */
static void adapter_wide_to_utf8(const wchar_t* wide, char* out, const size_t out_len)
{
    if (out_len == 0) return;
    out[0] = '\0';
    if (wide == NULL || wide[0] == L'\0') return;
    WideCharToMultiByte(CP_UTF8, 0, wide, -1, out, (int)out_len, NULL, NULL);
    out[out_len - 1] = '\0';
}

uint8_t get_adapters_stats(adapter_stats_t* out, const uint8_t max, char* default_id, const size_t default_id_len)
{
    if (default_id != NULL && default_id_len > 0) default_id[0] = '\0';
    if (out == NULL || max == 0) return 0;

    ULONG size = 16 * 1024;
    PIP_ADAPTER_ADDRESSES addrs = NULL;
    const ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER |
                        GAA_FLAG_INCLUDE_GATEWAYS;
    ULONG rc = ERROR_BUFFER_OVERFLOW;
    /* Local traffic sampling only. Bound both allocation and topology-change retries. */
    for (unsigned attempt = 0; attempt < 3 && rc == ERROR_BUFFER_OVERFLOW; ++attempt)
    {
        if (size == 0 || size > ADAPTER_INFO_MAX_BYTES) return 0;
        addrs = malloc(size);
        if (addrs == NULL) return 0;
        rc = GetAdaptersAddresses(AF_UNSPEC, flags, NULL, addrs, &size);
        if (rc != NO_ERROR)
        {
            free(addrs);
            addrs = NULL;
        }
    }
    if (rc != NO_ERROR)
    {
        LOG_WARN("枚举网卡失败 (错误码 %lu)", (unsigned long)rc);
        return 0;
    }

    // 默认出口: 拿一个公网地址问系统走哪块网卡 (取不到就留空, 界面会退化成选第一块)
    DWORD best_index = 0;
    if (GetBestInterface(htonl(0x08080808), &best_index) != NO_ERROR) best_index = 0;

    uint8_t count = 0;
    for (PIP_ADAPTER_ADDRESSES addr = addrs; addr != NULL && count < max; addr = addr->Next)
    {
        // 只要真实联网网卡: 有线 / Wi-Fi / 蜂窝; 回环、隧道、桥接之类直接跳过
        if (addr->IfType != IF_TYPE_ETHERNET_CSMACD && addr->IfType != IF_TYPE_IEEE80211 &&
            addr->IfType != IF_TYPE_WWANPP && addr->IfType != IF_TYPE_WWANPP2)
        {
            continue;
        }

        MIB_IF_ROW2 row;
        memset(&row, 0, sizeof(row));
        row.InterfaceIndex = addr->IfIndex;
        if (GetIfEntry2(&row) != NO_ERROR) continue;
        // HardwareInterface 为假的是软件网卡 (VMware / Hyper-V / 蓝牙个人区域网)
        if (row.InterfaceAndOperStatusFlags.HardwareInterface == 0) continue;

        adapter_stats_t* item = &out[count];
        memset(item, 0, sizeof(*item));
        adapter_id_from_guid(addr->AdapterName, item->id, sizeof(item->id));
        // 只取 FriendlyName: 用户看到的就是系统设置里那个名字 (Description 各家 SDK 宽窄不一)
        adapter_wide_to_utf8(addr->FriendlyName, item->name, sizeof(item->name));
        // 未连接的网卡也列出来 (界面上标"未连接"), 免得用户以为没识别到
        item->connected = (addr->OperStatus == IfOperStatusUp && row.MediaConnectState == MediaConnectStateConnected);
        item->rx_bytes = row.InOctets;
        item->tx_bytes = row.OutOctets;

        if (best_index != 0 && addr->IfIndex == best_index && default_id != NULL && default_id_len > 0)
        {
            snprintf(default_id, default_id_len, "%s", item->id);
        }
        count++;
    }

    free(addrs);
    return count;
}

#elif defined(__linux__) && !defined(__OPENWRT__)

/**
 * @brief 从 sysfs 读一块网卡的累计收发字节数
 *
 * /sys/class/net/<网卡>/statistics 是内核给的 64 位计数器, OpenWrt (busybox/musl)
 * 上也一定有, 不用起进程、不用解析 /proc/net/dev 的对齐
 */
static bool adapter_read_counters(const char* name, uint64_t* rx_bytes, uint64_t* tx_bytes)
{
    char path[PATH_MAX];
    unsigned long long value;

    snprintf(path, sizeof(path), "/sys/class/net/%s/statistics/rx_bytes", name);
    FILE* file = fopen(path, "r");
    if (file == NULL) return false;
    const bool rx_ok = fscanf(file, "%llu", &value) == 1;
    if (rx_ok) *rx_bytes = (uint64_t)value;
    fclose(file);
    if (rx_ok == false) return false;

    snprintf(path, sizeof(path), "/sys/class/net/%s/statistics/tx_bytes", name);
    file = fopen(path, "r");
    if (file == NULL) return false;
    const bool tx_ok = fscanf(file, "%llu", &value) == 1;
    if (tx_ok) *tx_bytes = (uint64_t)value;
    fclose(file);
    return tx_ok;
}

/**
 * @brief 是不是回环口 (按接口类型判断, 名字不一定叫 lo)
 *
 * /sys/class/net/<网卡>/type 是 ARPHRD_*: 772 是回环, 1 是以太网
 */
static bool adapter_is_loopback(const char* name)
{
    char path[PATH_MAX];
    char type[16] = {0};

    snprintf(path, sizeof(path), "/sys/class/net/%s/type", name);
    FILE* file = fopen(path, "r");
    if (file == NULL) return false;
    const bool ok = fgets(type, sizeof(type), file) != NULL;
    fclose(file);
    if (ok == false) return false;
    return strtoul(type, NULL, 10) == 772; // ARPHRD_LOOPBACK
}

/**
 * @brief 网卡当前是否"通着"
 *
 * operstate 为 up 是正常情况; PPP/PPPoE 这类不报载波的接口显示 unknown,
 * 但它们确实在传数据, 不能算未连接
 */
static bool adapter_is_up(const char* name)
{
    char path[PATH_MAX];
    char state[16] = {0};

    snprintf(path, sizeof(path), "/sys/class/net/%s/operstate", name);
    FILE* file = fopen(path, "r");
    if (file == NULL) return false;
    const bool ok = fgets(state, sizeof(state), file) != NULL;
    fclose(file);
    if (ok == false) return false;
    return strncmp(state, "up", 2) == 0 || strncmp(state, "unknown", 7) == 0;
}

/**
 * @brief 找默认出口接口 (/proc/net/route 里目标与掩码都为 0 的那条, 取 metric 最小的)
 *
 * 路由表里目标是 0.0.0.0/0 的就是默认路由; 没有 (比如纯 IPv6 上联) 时留空,
 * 网页会退化成选第一块网卡
 */
static void adapter_default_route(char* out, const size_t out_len)
{
    if (out == NULL || out_len == 0) return;
    out[0] = '\0';

    FILE* file = fopen("/proc/net/route", "r");
    if (file == NULL) return;

    char line[256];
    if (fgets(line, sizeof(line), file) == NULL)
    {
        fclose(file);
        return;
    }

    bool found = false;
    unsigned long best_metric = 0;
    while (fgets(line, sizeof(line), file) != NULL)
    {
        char iface[64] = {0};
        unsigned long dest = 0, gateway = 0, flags = 0, metric = 0, mask = 0;
        // Iface Destination Gateway Flags RefCnt Use Metric Mask ...
        if (sscanf(line, "%63s %lx %lx %lx %*u %*u %lu %lx", iface, &dest, &gateway, &flags, &metric, &mask) != 6)
        {
            continue;
        }
        if (dest != 0 || mask != 0) continue;
        if (found == false || metric < best_metric)
        {
            snprintf(out, out_len, "%s", iface);
            best_metric = metric;
            found = true;
        }
    }
    fclose(file);
}

uint8_t get_adapters_stats(adapter_stats_t* out, const uint8_t max, char* default_id, const size_t default_id_len)
{
    if (out == NULL || max == 0) return 0;
    if (default_id != NULL && default_id_len > 0) adapter_default_route(default_id, default_id_len);

    struct ifaddrs* list = NULL;
    if (getifaddrs(&list) != 0) return 0;

    uint8_t count = 0;
    for (struct ifaddrs* ifa = list; ifa != NULL && count < max; ifa = ifa->ifa_next)
    {
        if (ifa->ifa_addr == NULL) continue;
        if (ifa->ifa_addr->sa_family != AF_INET && ifa->ifa_addr->sa_family != AF_INET6) continue;
        if (adapter_is_loopback(ifa->ifa_name)) continue;

        // 同一块网卡会有 IPv4/IPv6 多条记录, 只取第一次见到的那条
        bool seen = false;
        for (uint8_t i = 0; i < count; i++)
        {
            if (strcmp(out[i].id, ifa->ifa_name) == 0)
            {
                seen = true;
                break;
            }
        }
        if (seen) continue;

        uint64_t rx_bytes = 0, tx_bytes = 0;
        if (adapter_read_counters(ifa->ifa_name, &rx_bytes, &tx_bytes) == false) continue;

        adapter_stats_t* item = &out[count];
        memset(item, 0, sizeof(*item));
        snprintf(item->id, sizeof(item->id), "%s", ifa->ifa_name);
        snprintf(item->name, sizeof(item->name), "%s", ifa->ifa_name);
        item->connected = adapter_is_up(ifa->ifa_name);
        item->rx_bytes = rx_bytes;
        item->tx_bytes = tx_bytes;
        count++;
    }

    freeifaddrs(list);
    return count;
}

#elif defined(__APPLE__)

#include <ifaddrs.h>
#include <net/if.h>
#include <net/route.h>
#include <sys/sysctl.h>

/**
 * @brief 系统自带的虚拟接口 (AirDrop / 无线直连 / VPN / 隧道), 不是用户想看的网卡
 */
static bool adapter_is_virtual_name(const char* name)
{
    static const char* prefixes[] = {"awdl", "llw", "utun", "gif", "stf", "ap", "anpi", "ipsec"};
    for (size_t i = 0; i < sizeof(prefixes) / sizeof(prefixes[0]); i++)
    {
        if (strncmp(name, prefixes[i], strlen(prefixes[i])) == 0) return true;
    }
    return false;
}

/**
 * @brief 默认出口接口 (路由表里目标前缀长度为 0 的那条)
 *
 * 走 sysctl(NET_RT_DUMP), 不起外部进程
 */
static void adapter_default_route(char* out, const size_t out_len)
{
    if (out == NULL || out_len == 0) return;
    out[0] = '\0';

    int mib[6] = {CTL_NET, PF_ROUTE, 0, 0, NET_RT_DUMP, 0};
    size_t needed = 0;
    if (sysctl(mib, 6, NULL, &needed, NULL, 0) != 0 || needed == 0) return;

    char* buffer = malloc(needed);
    if (buffer == NULL) return;
    if (sysctl(mib, 6, buffer, &needed, NULL, 0) != 0)
    {
        free(buffer);
        return;
    }

    for (char* cursor = buffer; cursor < buffer + needed;)
    {
        const struct rt_msghdr* route = (const struct rt_msghdr*)cursor;
        if (route->rtm_msglen <= 0) break;
        // 默认路由 = 目标地址是全零的那条 (macOS 的 rt_msghdr 没有"前缀长度"字段,
        // 前缀长度藏在 RTA_DST 那个 sockaddr 里; 默认路由的地址本身就是 0)
        if ((route->rtm_flags & RTF_UP) != 0 && route->rtm_index != 0 && (route->rtm_addrs & RTA_DST) != 0)
        {
            const struct sockaddr* dst = (const struct sockaddr*)(route + 1);
            const bool is_default =
                (dst->sa_family == AF_INET && ((const struct sockaddr_in*)dst)->sin_addr.s_addr == 0) ||
                (dst->sa_family == AF_INET6 && IN6_IS_ADDR_UNSPECIFIED(&((const struct sockaddr_in6*)dst)->sin6_addr));
            if (is_default)
            {
                char name[IFNAMSIZ] = {0};
                if (if_indextoname(route->rtm_index, name) != NULL)
                {
                    snprintf(out, out_len, "%s", name);
                    break;
                }
            }
        }
        cursor += route->rtm_msglen;
    }
    free(buffer);
}

uint8_t get_adapters_stats(adapter_stats_t* out, const uint8_t max, char* default_id, const size_t default_id_len)
{
    if (out == NULL || max == 0) return 0;

    if (default_id != NULL && default_id_len > 0) adapter_default_route(default_id, default_id_len);

    struct ifaddrs* list = NULL;
    if (getifaddrs(&list) != 0) return 0;

    /**
     * 累计字节数在 AF_LINK 那条记录上 (ifa_data 指向 if_data)。
     * 注意它是 32 位计数器, 4GB 会回绕: 上层按"计数器变小就重设基线"处理,
     * 所以回绕的那一帧会显示一次"正在采样" —— 实验性功能, 先这样。
     */
    uint8_t count = 0;
    for (struct ifaddrs* ifa = list; ifa != NULL && count < max; ifa = ifa->ifa_next)
    {
        if (ifa->ifa_addr == NULL || ifa->ifa_addr->sa_family != AF_LINK) continue;
        if ((ifa->ifa_flags & IFF_LOOPBACK) != 0) continue;
        if ((ifa->ifa_flags & IFF_UP) == 0) continue;
        if (adapter_is_virtual_name(ifa->ifa_name)) continue;
        if (ifa->ifa_data == NULL) continue;

        bool seen = false;
        for (uint8_t i = 0; i < count; i++)
        {
            if (strcmp(out[i].id, ifa->ifa_name) == 0)
            {
                seen = true;
                break;
            }
        }
        if (seen) continue;

        const struct if_data* data = (const struct if_data*)ifa->ifa_data;
        adapter_stats_t* item = &out[count];
        memset(item, 0, sizeof(*item));
        snprintf(item->id, sizeof(item->id), "%s", ifa->ifa_name);
        snprintf(item->name, sizeof(item->name), "%s", ifa->ifa_name);
        item->connected = (ifa->ifa_flags & IFF_RUNNING) != 0;
        item->rx_bytes = data->ifi_ibytes;
        item->tx_bytes = data->ifi_obytes;
        count++;
    }

    freeifaddrs(list);
    return count;
}

#else

#ifndef __OPENWRT__
uint8_t get_adapters_stats(adapter_stats_t* out, const uint8_t max, char* default_id, const size_t default_id_len)
{
    (void)out;
    (void)max;
    if (default_id != NULL && default_id_len > 0) default_id[0] = '\0';
    return 0;
}
#endif

#endif

/**
 * @brief 取默认网关的 IPv4 地址
 *
 * 认证报文里的 <gwip> 要填网关, 不是 AC 的地址。
 * 拿不到时返回 false, 调用方自行决定兜底。
 */
bool get_default_gateway_ip(char* out, const size_t out_len)
{
    if (out == NULL || out_len == 0) return false;
    out[0] = '\0';

#ifdef _WIN32

    ULONG size = 16 * 1024;
    PIP_ADAPTER_ADDRESSES addrs = malloc(size);
    if (addrs == NULL) return false;

    const ULONG flags = GAA_FLAG_SKIP_ANYCAST | GAA_FLAG_SKIP_MULTICAST | GAA_FLAG_SKIP_DNS_SERVER |
                        GAA_FLAG_INCLUDE_GATEWAYS;
    ULONG rc = GetAdaptersAddresses(AF_INET, flags, NULL, addrs, &size);
    if (rc == ERROR_BUFFER_OVERFLOW)
    {
        free(addrs);
        addrs = malloc(size);
        if (addrs == NULL) return false;
        rc = GetAdaptersAddresses(AF_INET, flags, NULL, addrs, &size);
    }
    if (rc != NO_ERROR)
    {
        free(addrs);
        return false;
    }

    // 先认"默认出口"那块网卡; 认不出来就退回第一个可用网关
    DWORD best_index = 0;
    if (GetBestInterface(htonl(0x08080808), &best_index) != NO_ERROR) best_index = 0;

    bool found = false;
    char fallback[64] = "";

    for (PIP_ADAPTER_ADDRESSES addr = addrs; addr != NULL && found == false; addr = addr->Next)
    {
        if (addr->OperStatus != IfOperStatusUp) continue;

        for (PIP_ADAPTER_GATEWAY_ADDRESS_LH gw = addr->FirstGatewayAddress; gw != NULL; gw = gw->Next)
        {
            if (gw->Address.lpSockaddr == NULL || gw->Address.lpSockaddr->sa_family != AF_INET) continue;

            const struct sockaddr_in* sin = (const struct sockaddr_in*)gw->Address.lpSockaddr;
            char buf[64];
            if (inet_ntop(AF_INET, &sin->sin_addr, buf, sizeof(buf)) == NULL) continue;

            if (fallback[0] == '\0') snprintf(fallback, sizeof(fallback), "%s", buf);

            if (best_index != 0 && addr->IfIndex == best_index)
            {
                snprintf(out, out_len, "%s", buf);
                found = true;
                break;
            }
        }
    }

    if (found == false && fallback[0] != '\0') snprintf(out, out_len, "%s", fallback);

    free(addrs);
    return out[0] != '\0';

#elif defined(__linux__)

    FILE* file = fopen("/proc/net/route", "r");
    if (file == NULL) return false;

    char line[256];
    if (fgets(line, sizeof(line), file) == NULL) // 先吃掉表头
    {
        fclose(file);
        return false;
    }

    bool found = false;
    unsigned long best_metric = 0;

    while (fgets(line, sizeof(line), file) != NULL)
    {
        char iface[64] = {0};
        unsigned long dest = 0, gateway = 0, flags = 0, metric = 0, mask = 0;
        if (sscanf(line, "%63s %lx %lx %lx %*u %*u %lu %lx", iface, &dest, &gateway, &flags, &metric, &mask) != 6)
        {
            continue;
        }

        // 默认路由: 目标 0.0.0.0/0 且带网关标志 (RTF_GATEWAY = 0x0002)
        if (dest != 0 || mask != 0 || gateway == 0 || (flags & 0x0002UL) == 0) continue;

        if (found == false || metric < best_metric)
        {
            struct in_addr addr;
            addr.s_addr = (uint32_t)gateway; // /proc/net/route 里已经是网络字节序
            if (inet_ntop(AF_INET, &addr, out, out_len) != NULL)
            {
                best_metric = metric;
                found = true;
            }
        }
    }

    fclose(file);
    return found;

#else

    return false;

#endif
}

