#ifdef _WIN32
#include <winsock2.h>
#endif

#include "net/NetClient.h"

#include "NetClientInternal.h"
#include "core/States.h"

#include "utils/log/Logger.h"
#include "utils/sim/SimThread.h"
#include "utils/text/TextUtils.h"
#include "utils/time/TimeUtils.h"

#include <ctype.h>
#include <cJSON/cJSON.h>
#include <curl/curl.h>
#include <inttypes.h>
#include <stdio.h>
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>
#ifndef _WIN32
#include <pthread.h>
#include <time.h>
#endif

#define GENERATE_URL "http://connectivitycheck.platform.hicloud.com/generate_204"
#define GENERATE_BAK_URL "http://connect.rom.miui.com/generate_204"
#define AUTH_URL "http://14.146.227.141:7001"
#define AUTH_BAK_URL "http://14.146.227.141:7001"

/** 一次并发探测的 handle 上限 (每个目标可能同时开多路固定 IP) */
#define PROBE_MAX 16

/**
 * 单个探测的超时 (毫秒)
 *
 * 探测只关心"连不连得上、状态码是多少", 正常都在几十毫秒内完成。
 * 原来沿用的是配置里的 conn_timeout (常见 7~10 秒) 串行试 2~4 个地址: 一旦某个
 * 地址被网关/防火墙静默丢弃 (SYN 发出去没有回应), 一次检测就要白等十几到几十秒。
 * 这类超时与服务器响应快慢无关, 调大超时只会等更久 —— 所以这里固定用短超时。
 */
#define PROBE_CONNECT_TIMEOUT_MS 3000L
#define PROBE_TOTAL_TIMEOUT_MS 4000L

/** 后台探测的间隔 (仅 Web 进程用) */
#define PROBE_INTERVAL_MS 2000

/** 失败汇总的最小间隔: 探测 2 秒一轮, 网络长时间不通时不能把日志刷满 */
#define PROBE_FAIL_REPORT_MS 30000

/** 丢弃响应体: 只需要状态码, 不把门户页面整个拉下来 */
static size_t probe_sink(const char* ptr, size_t size, size_t nmemb, void* userdata)
{
    (void)ptr;
    (void)userdata;

    if (size != 0 && nmemb > SIZE_MAX / size) return 0;
    return size * nmemb;
}

/** 一个探测目标 */
typedef struct
{
    const char* url;
    const char* label;
} probe_target_t;

/** @brief 判断字符串是不是合法的 IPv4 (只认 a.b.c.d 四段 0~255, 不查 DNS) */
static bool probe_is_ipv4(const char* s)
{
    if (s == NULL || *s == '\0') return false;

    uint8_t parts = 0;
    while (*s != '\0')
    {
        if (isdigit((unsigned char)*s) == 0) return false;

        int value = 0;
        while (isdigit((unsigned char)*s) != 0)
        {
            value = value * 10 + (*s - '0');
            if (value > 255) return false;
            s++;
        }

        parts++;
        if (*s == '\0') break;
        if (*s != '.') return false;
        s++;
    }
    return parts == 4;
}

/**
 * 探测目标放在全局配置里 (g_prog_status[0].cfg.probe_hosts), 这里用两个短名字
 * 指向它, 省得到处写长前缀。内容由 probe_targets_defaults / probe_targets_parse 填。
 */
#define s_known_hosts (g_prog_status[0].cfg.probe_hosts)
#define s_known_host_count (g_prog_status[0].cfg.probe_host_count)

/** @brief 找 host 对应的槽位, 没有就新建一个 (新建时清空) */
static probe_host_t* known_host_slot(const char* host)
{
    if (host == NULL) return NULL;

    for (uint8_t i = 0; i < s_known_host_count; i++)
    {
        if (str_ieq(s_known_hosts[i].host, host)) return &s_known_hosts[i];
    }

    if (s_known_host_count >= PROBE_HOST_MAX) return NULL;

    probe_host_t* slot = &s_known_hosts[s_known_host_count];
    memset(slot, 0, sizeof(*slot));
    snprintf(slot->host, sizeof(slot->host), "%s", host);
    s_known_host_count++;
    return slot;
}

static void known_host_add(const char* host, const char* ip)
{
    if (ip == NULL) return;

    probe_host_t* slot = known_host_slot(host);
    if (slot == NULL) return;
    if (slot->ip_count >= PROBE_IP_MAX) return;

    snprintf(slot->ips[slot->ip_count], sizeof(slot->ips[0]), "%s", ip);
    slot->ip_count++;
}

void probe_targets_defaults(void)
{
    memset(s_known_hosts, 0, sizeof(s_known_hosts));
    s_known_host_count = 0;

    (void)known_host_slot("connectivitycheck.platform.hicloud.com");
    known_host_add("connectivitycheck.platform.hicloud.com", "183.61.185.93");
    known_host_add("connectivitycheck.platform.hicloud.com", "125.88.255.53");

    (void)known_host_slot("connect.rom.miui.com");
    known_host_add("connect.rom.miui.com", "202.69.4.32");
    known_host_add("connect.rom.miui.com", "220.181.104.183");
}

/**
 * @brief 解析配置里的 probe_hosts
 *
 * 语义是"替换": 配置里写了就用配置的这一套（写全写少由用户决定）;
 * 完全没写时由调用方补默认值并写回配置文件。格式不对返回 false, 保留默认。
 */
bool probe_targets_parse(const cJSON* arr)
{
    if (arr == NULL) return false;

    if (cJSON_IsArray(arr) == false)
    {
        LOG_WARN("probe_hosts 不是数组, 使用内置默认探测目标");
        return false;
    }

    probe_host_t parsed[PROBE_HOST_MAX];
    uint8_t parsed_count = 0;

    const int item_count = cJSON_GetArraySize(arr);
    for (int i = 0; i < item_count && parsed_count < PROBE_HOST_MAX; i++)
    {
        const cJSON* item = cJSON_GetArrayItem(arr, i);
        const cJSON* host = cJSON_GetObjectItem(item, "host");
        const cJSON* ips = cJSON_GetObjectItem(item, "ips");

        if (cJSON_IsString(host) == false || host->valuestring[0] == '\0') continue;
        if (cJSON_IsArray(ips) == false) continue;

        probe_host_t t = {0};
        snprintf(t.host, sizeof(t.host), "%s", host->valuestring);

        const int ip_count = cJSON_GetArraySize(ips);
        for (int k = 0; k < ip_count && t.ip_count < PROBE_IP_MAX; k++)
        {
            const cJSON* ip = cJSON_GetArrayItem(ips, k);
            if (cJSON_IsString(ip) == false || ip->valuestring[0] == '\0') continue;
            if (probe_is_ipv4(ip->valuestring) == false) continue;

            snprintf(t.ips[t.ip_count], sizeof(t.ips[0]), "%s", ip->valuestring);
            t.ip_count++;
        }

        // 每个域名最多留 PROBE_IP_MAX 个 IP: 超出的不静默丢, 提示一下
        if (ip_count > PROBE_IP_MAX)
        {
            LOG_WARN("探测目标 %s 配了 %d 个 IP, 超过每域名 %d 个上限, 多余的会被忽略",
                t.host, ip_count, PROBE_IP_MAX);
        }

        if (t.ip_count == 0) continue;
        parsed[parsed_count] = t;
        parsed_count++;
    }

    if (parsed_count == 0) return false;

    memcpy(s_known_hosts, parsed, sizeof(parsed[0]) * parsed_count);
    s_known_host_count = parsed_count;

    /**
     * 总路数超过 14 只提示, 不做处理 (探测并发 PROBE_MAX = 16, 其中两台认证
     * 服务器固定占 2 路); 超出的部分会由 probe_group 的 probe_count 截断
     */
    uint16_t total_ips = 0;
    for (uint8_t i = 0; i < s_known_host_count; i++)
    {
        total_ips += s_known_hosts[i].ip_count;
    }
    if (total_ips > PROBE_MAX - 2)
    {
        LOG_WARN("探测目标共 %" PRIu16 " 路, 超过 %d 路上限 (认证服务器已占 2 路), 超出部分不会被探测",
            total_ips, PROBE_MAX - 2);
    }

    for (uint8_t i = 0; i < s_known_host_count; i++)
    {
        LOG_DEBUG("探测目标: %s (%" PRIu8 " 个固定 IP)", s_known_hosts[i].host, s_known_hosts[i].ip_count);
    }
    return true;
}

/** @brief 把当前探测目标导出成 probe_hosts 数组 (调用方负责 cJSON_Delete) */
cJSON* probe_targets_to_json(void)
{
    cJSON* arr = cJSON_CreateArray();
    if (arr == NULL) return NULL;

    for (uint8_t i = 0; i < s_known_host_count; i++)
    {
        cJSON* item = cJSON_CreateObject();
        if (item == NULL) break;

        cJSON_AddStringToObject(item, "host", s_known_hosts[i].host);

        cJSON* ips = cJSON_CreateArray();
        if (ips != NULL)
        {
            for (uint8_t k = 0; k < s_known_hosts[i].ip_count; k++)
            {
                cJSON_AddItemToArray(ips, cJSON_CreateString(s_known_hosts[i].ips[k]));
            }
            cJSON_AddItemToObject(item, "ips", ips);
        }

        cJSON_AddItemToArray(arr, item);
    }
    return arr;
}

/** 一个在飞的探测 */
typedef struct
{
    CURL* handle;
    struct curl_slist* resolve;
    const char* label;
    CURLcode code;
} probe_t;

/**
 * @brief 从 URL 里取出 host (http://host[:port]/...)
 * @return 是否取到
 */
static bool url_host(const char* url, char* out, size_t out_len)
{
    if (url == NULL || out == NULL || out_len == 0) return false;

    const char* start = strstr(url, "://");
    start = (start != NULL) ? start + 3 : url;

    const size_t len = strcspn(start, ":/");
    if (len == 0 || len >= out_len) return false;

    memcpy(out, start, len);
    out[len] = '\0';
    return true;
}

/** @brief 找某个 host 的兜底 IP 表, 没有就返回 NULL */
static const probe_host_t* known_host_for(const char* host)
{
    if (host == NULL) return NULL;

    for (uint8_t i = 0; i < s_known_host_count; i++)
    {
        if (str_ieq(s_known_hosts[i].host, host)) return &s_known_hosts[i];
    }
    return NULL;
}

/**
 * @brief 往 multi 里加一个探测
 * @param ip 非空时用固定解析 (CURLOPT_RESOLVE); 为空表示地址本身是 IP, 直接请求
 */
static bool probe_add(CURLM* multi, const char* url, const char* host, const char* ip,
                      const char* label, probe_t* probes, size_t* count)
{
    if (*count >= PROBE_MAX) return false;

    CURL* handle = curl_easy_init();
    if (handle == NULL) return false;

    struct curl_slist* resolve = NULL;

    if (curl_easy_setopt(handle, CURLOPT_URL, url) != CURLE_OK) goto setup_failed;
    if (curl_easy_setopt(handle, CURLOPT_CONNECTTIMEOUT_MS, PROBE_CONNECT_TIMEOUT_MS) != CURLE_OK) goto setup_failed;
    if (curl_easy_setopt(handle, CURLOPT_TIMEOUT_MS, PROBE_TOTAL_TIMEOUT_MS) != CURLE_OK) goto setup_failed;
    if (curl_easy_setopt(handle, CURLOPT_FOLLOWLOCATION, 0L) != CURLE_OK) goto setup_failed;
    if (curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, probe_sink) != CURLE_OK) goto setup_failed;
    if (curl_easy_setopt(handle, CURLOPT_NOSIGNAL, 1L) != CURLE_OK) goto setup_failed;
    /**
     * 只要 IPv4
     *
     * 这些检测域名的 AAAA 记录很常见 (实测 hicloud 的 DNS 只回 IPv6), 而校园网/
     * 路由器上 IPv6 往往根本不通。让 curl 先试 IPv6, 每次都要白等一次连接超时
     * 才回落到 IPv4 —— 表现就是"检测经常超时"。
     */
    if (curl_easy_setopt(handle, CURLOPT_IPRESOLVE, CURL_IPRESOLVE_V4) != CURLE_OK) goto setup_failed;

#ifdef __OPENWRT__
    /**
     * 探测也要绑路由标记 (SO_MARK)
     *
     * 多 WAN 分流的环境里, 每个账号的流量靠 mark 走各自的线路; 探测如果不绑 mark,
     * 全部走主路由表 —— 主线路的会话一死, 所有账号的探测都看到 302, 于是所有账号
     * 同时退登重登。探测必须和本账号的认证/心跳走同一条线路。
     */
    if (curl_easy_setopt(handle, CURLOPT_OPENSOCKETFUNCTION, open_socket_callback) != CURLE_OK) goto setup_failed;
#endif

    if (ip != NULL && host != NULL)
    {
        char entry[256];
        const int len = snprintf(entry, sizeof(entry), "%s:80:%s", host, ip);
        if (len < 0 || (size_t)len >= sizeof(entry)) goto setup_failed;

        resolve = curl_slist_append(NULL, entry);
        if (resolve == NULL) goto setup_failed;
        if (curl_easy_setopt(handle, CURLOPT_RESOLVE, resolve) != CURLE_OK) goto setup_failed;
    }

    if (curl_multi_add_handle(multi, handle) != CURLM_OK) goto setup_failed;

    probes[*count].handle = handle;
    probes[*count].resolve = resolve;
    probes[*count].label = label;
    probes[*count].code = CURLE_AGAIN; // not completed yet
    (*count)++;

    return true;

setup_failed:
    curl_easy_cleanup(handle);
    curl_slist_free_all(resolve);
    return false;
}

static network_status_t probe_classify(CURLcode transfer, CURLcode info, long http_code)
{
    if (transfer != CURLE_OK || info != CURLE_OK) return STATUS_ERROR;
    if (http_code == HTTP_NO_CONTENT || http_code == HTTP_MOVED_PERMANENTLY) return STATUS_OK;
    if (http_code == HTTP_FOUND) return STATUS_NEED_AUTH;
    return STATUS_ERROR;
}

/* Drain after EVERY perform, including the initial one and the final one
 * with running == 0. Only completed, successful transfers can classify. */
static network_status_t probe_drain(CURLM* multi, probe_t* probes, size_t count)
{
    network_status_t result = STATUS_ERROR;
    int left = 0;
    CURLMsg* msg;
    while ((msg = curl_multi_info_read(multi, &left)) != NULL)
    {
        if (msg->msg != CURLMSG_DONE) continue;
        for (size_t i = 0; i < count; ++i)
        {
            if (probes[i].handle != msg->easy_handle) continue;
            probes[i].code = msg->data.result;
            long http_code = 0;
            const CURLcode info = curl_easy_getinfo(msg->easy_handle, CURLINFO_RESPONSE_CODE, &http_code);
            const network_status_t observed = probe_classify(msg->data.result, info, http_code);
            if (result == STATUS_ERROR) result = observed;
            break;
        }
    }
    return result;
}

/* Cancellation is opt-in: the background worker owns its running flag;
 * synchronous callers retain the existing check_network_status contract. */
static bool probe_cancelled(const atomic_bool* running)
{
    return running != NULL && !atomic_load(running);
}

static network_status_t probe_drive(CURLM* multi, probe_t* probes, size_t count,
                                    const atomic_bool* keep_running)
{
    const uint64_t start = get_steady_tm_ms();
    if (start == 0) return STATUS_ERROR;
    int running = 0;
    while (!probe_cancelled(keep_running))
    {
        if (curl_multi_perform(multi, &running) != CURLM_OK) break;
        const network_status_t result = probe_drain(multi, probes, count);
        if (probe_cancelled(keep_running)) break;
        if (result != STATUS_ERROR || running == 0) return result;
        const uint64_t now = get_steady_tm_ms();
        if (now < start || now - start >= PROBE_TOTAL_TIMEOUT_MS + 1000) break;
        const uint64_t left = PROBE_TOTAL_TIMEOUT_MS + 1000 - (now - start);
        const int slice = keep_running != NULL ? 100 : 200;
        int wait_fds = 0;
        /* poll waits even when libcurl has no descriptors, unlike multi_wait. */
        if (curl_multi_poll(multi, NULL, 0, left < (uint64_t)slice ? (int)left : slice,
                            &wait_fds) != CURLM_OK) break;
    }
    return STATUS_ERROR;
}

/* No external requests. Exercise immediate completion with an invalid URL,
 * plus the exact classification boundary used in production. */
static int probe_test_thread(void* arg)
{
    int* value = arg;
    *value = 42;
    return 17;
}

static bool probe_thread_resource_self_test(void)
{
    if (sim_thread_create(NULL, NULL) != NULL || sim_thread_join(NULL, NULL) == 0 ||
        sim_thread_detach(NULL) == 0) return false;
    for (int i = 0; i < 64; ++i)
    {
        int value = 0;
        int code = -1;
        sim_thread_t* thread = sim_thread_create(probe_test_thread, &value);
        if (thread == NULL) return false;
        if (sim_thread_join(thread, &code) != 0)
        {
            /* A failed wait does not establish that the stack argument is safe
             * to reclaim. This offline executable must stop immediately. */
            abort();
        }
        const bool ok = value == 42 && code == 17 &&
            sim_thread_join(thread, NULL) != 0 && sim_thread_detach(thread) != 0;
        sim_thread_destroy(thread);
        if (!ok) return false;
    }
    return true;
}

#ifdef _WIN32
/* A loopback listener with no response producer keeps the HTTP request
 * pending. Cancel only after a real curl_multi_perform has started it. */
static bool probe_pending_cancel_self_test(void)
{
    WSADATA wsa;
    if (WSAStartup(MAKEWORD(2, 2), &wsa) != 0) return false;
    SOCKET listener = INVALID_SOCKET;
    CURLM* multi = NULL;
    CURL* easy = NULL;
    bool added = false, ok = false;
    struct sockaddr_in addr = {0};
    int addr_len = sizeof(addr);
    char url[96];
    int running = 0;
    atomic_bool keep_running = true;

    listener = socket(AF_INET, SOCK_STREAM, 0);
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    if (listener == INVALID_SOCKET ||
        bind(listener, (struct sockaddr*)&addr, sizeof(addr)) != 0 ||
        listen(listener, 1) != 0 ||
        getsockname(listener, (struct sockaddr*)&addr, &addr_len) != 0) goto done;

    snprintf(url, sizeof(url), "http://127.0.0.1:%u/", (unsigned)ntohs(addr.sin_port));
    multi = curl_multi_init();
    easy = curl_easy_init();
    if (multi == NULL || easy == NULL ||
        curl_easy_setopt(easy, CURLOPT_URL, url) != CURLE_OK ||
        curl_easy_setopt(easy, CURLOPT_PROXY, "") != CURLE_OK ||
        curl_easy_setopt(easy, CURLOPT_CONNECTTIMEOUT_MS, 2000L) != CURLE_OK ||
        curl_easy_setopt(easy, CURLOPT_TIMEOUT_MS, 3000L) != CURLE_OK ||
        curl_easy_setopt(easy, CURLOPT_WRITEFUNCTION, probe_sink) != CURLE_OK ||
        curl_multi_add_handle(multi, easy) != CURLM_OK) goto done;

    added = true;
    if (curl_multi_perform(multi, &running) != CURLM_OK || running != 1) goto done;
    probe_t probe = { .handle = easy, .code = CURLE_AGAIN };
    atomic_store(&keep_running, false);
    ok = probe_drive(multi, &probe, 1, &keep_running) == STATUS_ERROR &&
         probe.code == CURLE_AGAIN;

done:
    if (added && curl_multi_remove_handle(multi, easy) != CURLM_OK) ok = false;
    if (easy != NULL) curl_easy_cleanup(easy);
    if (multi != NULL && curl_multi_cleanup(multi) != CURLM_OK) ok = false;
    if (listener != INVALID_SOCKET && closesocket(listener) != 0) ok = false;
    WSACleanup();
    return ok;
}
#endif

bool probe_resource_self_test(void)
{
    if (!probe_thread_resource_self_test()) return false;
#ifdef _WIN32
    if (!probe_pending_cancel_self_test()) return false;
#endif
    if (probe_sink(NULL, SIZE_MAX, 2, NULL) != 0 ||
        probe_sink(NULL, 4, 8, NULL) != 32 ||
        probe_classify(CURLE_OK, CURLE_OK, 204) != STATUS_OK ||
        probe_classify(CURLE_OK, CURLE_OK, 301) != STATUS_OK ||
        probe_classify(CURLE_OK, CURLE_OK, 302) != STATUS_NEED_AUTH ||
        probe_classify(CURLE_OK, CURLE_OK, 500) != STATUS_ERROR ||
        probe_classify(CURLE_PARTIAL_FILE, CURLE_OK, 204) != STATUS_ERROR ||
        probe_classify(CURLE_OPERATION_TIMEDOUT, CURLE_OK, 302) != STATUS_ERROR ||
        probe_classify(CURLE_OK, CURLE_BAD_FUNCTION_ARGUMENT, 204) != STATUS_ERROR)
        return false;

    CURLM* multi = curl_multi_init();
    if (multi == NULL) return false;
    CURL* handle = curl_easy_init();
    if (handle == NULL) { curl_multi_cleanup(multi); return false; }
    bool added = false;
    bool ok = false;
    if (curl_easy_setopt(handle, CURLOPT_URL, "://") == CURLE_OK &&
        curl_easy_setopt(handle, CURLOPT_WRITEFUNCTION, probe_sink) == CURLE_OK &&
        curl_multi_add_handle(multi, handle) == CURLM_OK)
    {
        added = true;
        probe_t probe = { .handle = handle, .code = CURLE_AGAIN };
        atomic_bool keep_running = false;
        ok = probe_drive(multi, &probe, 1, &keep_running) == STATUS_ERROR &&
            probe.code == CURLE_AGAIN; // no perform after cancellation
        atomic_store(&keep_running, true);
        ok = ok && probe_drive(multi, &probe, 1, &keep_running) == STATUS_ERROR &&
            probe.code == CURLE_URL_MALFORMAT;
    }
    if (added) curl_multi_remove_handle(multi, handle);
    curl_easy_cleanup(handle);
    curl_multi_cleanup(multi);
    return ok;
}

/**
 * @brief 汇总一次"全部失败"的原因 (带限流)
 *
 * 只打结果不说明是哪一路失败的话, 排查时还得猜是 DNS 超时还是连接超时。
 * @param probes 本次所有在飞的探测
 */
static void probe_report_failure(const probe_t* probes, const size_t count)
{
    static _Thread_local uint64_t s_last_report_ms = 0;

    const uint64_t now = get_steady_tm_ms();
    if (now - s_last_report_ms < PROBE_FAIL_REPORT_MS) return;
    s_last_report_ms = now;

    char detail[256];
    size_t used = 0;
    detail[0] = '\0';

    for (size_t i = 0; i < count && used + 24 < sizeof(detail); i++)
    {
        const int n = snprintf(detail + used, sizeof(detail) - used, "%s=%d ",
                               probes[i].label, (int)probes[i].code);
        if (n <= 0) break;
        used += (size_t)n;
    }

    LOG_WARN("联网检测全部失败: %s(6=域名解析失败, 28=连接超时, 7=连不上)", detail);
}

/**
 * @brief 并发探测一组目标, 谁先给出可用结果就采用谁
 *
 * 并发的原因:
 * - 这些 generate_204 地址在不同线路上的可达性差别很大 (某路由器一天里主地址连接
 *   超时 183 次、备用地址只失败 4 次), 串行试的话一个不可达地址就要吃满一次超时;
 * - 每个固定 IP 各占一路并发 —— 写成一条 `CURLOPT_RESOLVE` 多地址的话 curl 会
 *   串行试, 一样要等超时。
 *
 * **不走系统 DNS**: 目标地址都是固定的, 只发固定 IP 那几路 (IP 字面量的目标直接
 * 请求)。这样 DNS 挂了、被劫持都不影响检测结果。
 *
 * @return STATUS_OK 拿到 204/301, STATUS_NEED_AUTH 拿到 302, 其余 STATUS_ERROR
 */
static network_status_t probe_group(const probe_target_t* targets, const size_t count,
                                    const atomic_bool* keep_running)
{
    if (targets == NULL || count == 0 || probe_cancelled(keep_running)) return STATUS_ERROR;

    CURLM* multi = curl_multi_init();
    if (multi == NULL) return STATUS_ERROR;

    probe_t probes[PROBE_MAX];
    char labels[PROBE_MAX][64];
    size_t probe_count = 0;
    network_status_t result = STATUS_ERROR;

    for (size_t i = 0; i < count && probe_count < PROBE_MAX && !probe_cancelled(keep_running); i++)
    {
        char host[128] = "";
        const bool has_host = url_host(targets[i].url, host, sizeof(host));
        const probe_host_t* known = has_host ? known_host_for(host) : NULL;

        if (known != NULL && known->ip_count > 0)
        {
            // 域名: 只发固定 IP 的各路, 不走系统 DNS
            for (uint8_t k = 0; k < known->ip_count && k < PROBE_IP_MAX && probe_count < PROBE_MAX && !probe_cancelled(keep_running); k++)
            {
                snprintf(labels[probe_count], sizeof(labels[0]), "%s/ip%" PRIu8, targets[i].label, (uint8_t)(k + 1));
                probe_add(multi, targets[i].url, host, known->ips[k], labels[probe_count], probes, &probe_count);
            }
        }
        else
        {
            // IP 字面量 (认证服务器): 直接请求, curl 不会去查 DNS
            snprintf(labels[probe_count], sizeof(labels[0]), "%s", targets[i].label);
            probe_add(multi, targets[i].url, host, NULL, labels[probe_count], probes, &probe_count);
        }
    }

    if (probe_count > 0 && !probe_cancelled(keep_running))
    {
        result = probe_drive(multi, probes, probe_count, keep_running);
        if (result == STATUS_ERROR && !probe_cancelled(keep_running))
            probe_report_failure(probes, probe_count);
    }

    for (size_t i = 0; i < probe_count; i++)
    {
        curl_multi_remove_handle(multi, probes[i].handle);
        curl_easy_cleanup(probes[i].handle);
        curl_slist_free_all(probes[i].resolve);
    }
    curl_multi_cleanup(multi);

    return probe_cancelled(keep_running) ? STATUS_ERROR : result;
}

static network_status_t check_network_status_impl(const bool connect_only, const atomic_bool* keep_running)
{
    /**
     * connect_only 只为兼容调用点保留: 探测统一发完整 GET 再看状态码 ——
     * 只建连接不请求的话拿不到 204/302, 也就分不清"能上网"和"需要认证"。
     */
    (void)connect_only;
    if (probe_cancelled(keep_running) || g_prog_status == NULL || g_prog_cnt <= 0)
        return STATUS_ERROR;

    /**
     * 联网探测目标来自配置 (probe_hosts), 没配就用内置的 hicloud / miui 两组。
     * 配置里只写 host, 检测路径统一拼 /generate_204。
     */
    probe_target_t internet_targets[PROBE_HOST_MAX];
    char urls[PROBE_HOST_MAX][192];
    uint8_t internet_count = 0;

    for (uint8_t i = 0; i < s_known_host_count && internet_count < PROBE_HOST_MAX; i++)
    {
        snprintf(urls[internet_count], sizeof(urls[0]), "http://%s/generate_204", s_known_hosts[i].host);
        internet_targets[internet_count].url = urls[internet_count];
        internet_targets[internet_count].label = s_known_hosts[i].host;
        internet_count++;
    }

    static const probe_target_t auth_targets[] = {
        {AUTH_URL, "auth1"},
        {AUTH_BAK_URL, "auth2"},
    };

    const network_status_t status =
        (internet_count > 0) ? probe_group(internet_targets, internet_count, keep_running) : STATUS_ERROR;
    if (probe_cancelled(keep_running)) return STATUS_ERROR;
    if (status != STATUS_ERROR) return status;

    /**
     * 外网都不通: 再并发试认证服务器 —— 能连上 (返回 302 或其它状态码) 说明
     * 人还在校园网里, 只是没过认证。
     */
    return probe_group(auth_targets, sizeof(auth_targets) / sizeof(auth_targets[0]), keep_running);
}

network_status_t check_network_status(const bool connect_only)
{
    return check_network_status_impl(connect_only, NULL);
}

/**
 * @brief 网络状态变化时提示一次
 *
 * 这些状态是每秒/每几秒轮询一次的, 每次都打 INFO 会把日志刷满 —— 但完全不提示
 * 又不行: 用户至少要知道"现在已联网, 不需要认证"。所以只在状态真的变了时提示
 * 一次, 相同的状态重复检测到就什么都不打。
 *
 * 状态按线程记录: 认证进程里每个账号一个线程, 别让它们互相覆盖。
 *
 * @param status 本次检测到的网络状态
 */
void log_network_state_change(const network_status_t status)
{
    static _Thread_local network_status_t s_last_state = (network_status_t)-1;

    static _Thread_local uint64_t s_last_event_ms = 0;
    const uint64_t now = get_steady_tm_ms();
    if (g_ephemeral_auth && (status != s_last_state || now - s_last_event_ms >= 10000))
    {
        ephemeral_emit_event(status == STATUS_OK ? "network_online" :
            (status == STATUS_NEED_AUTH ? "network_required" : "network_unknown"), 0);
        s_last_event_ms = now;
    }
    if (status == s_last_state) return;

    s_last_state = status;

    switch (status)
    {
    case STATUS_OK:
        LOG_INFO("已连接至互联网");
        break;
    case STATUS_NEED_AUTH:
        LOG_INFO("需要认证");
        break;
    default:
        // 网络异常的具体原因由调用点的 WARN/ERROR 覆盖, 这里不重复
        break;
    }
}

/* ------------------------------------------------------------------
 * 后台联网探测 (仅 Web 进程用)
 * Web 线程是单线程的事件循环, 在这里现做一次探测 (最坏十几秒) 会把
 * /api/traffic、/api/hotspot/status 一起拖到前端超时, 所以改成后台线程定时探、
 * 接口只读缓存。
 * ------------------------------------------------------------------ */

static atomic_bool s_probe_running = false;
static _Atomic(network_status_t) s_probe_cached = STATUS_ERROR;
static sim_thread_t* s_probe_thread = NULL;
#ifdef _WIN32
static SRWLOCK s_probe_lifecycle = SRWLOCK_INIT;
static void probe_lock(void) { AcquireSRWLockExclusive(&s_probe_lifecycle); }
static void probe_unlock(void) { ReleaseSRWLockExclusive(&s_probe_lifecycle); }
#else
static pthread_mutex_t s_probe_lifecycle = PTHREAD_MUTEX_INITIALIZER;
static void probe_lock(void) { pthread_mutex_lock(&s_probe_lifecycle); }
static void probe_unlock(void) { pthread_mutex_unlock(&s_probe_lifecycle); }
#endif

static int probe_thread(void* arg)
{
    (void)arg;

    while (atomic_load(&s_probe_running))
    {
        const uint64_t started = get_steady_tm_ms();
        const network_status_t observed = check_network_status_impl(true, &s_probe_running);
        if (!atomic_load(&s_probe_running)) return 0;
        atomic_store(&s_probe_cached, observed);
        const uint64_t elapsed = get_steady_tm_ms() - started;

        /**
         * 正常都在几十毫秒内完成。超过 1 秒说明有检测地址不可达 (SYN 被静默丢弃,
         * 只能等满超时) —— 这正是"经常超时"的现场, 留一条 WARN 方便排查。
         */
        if (elapsed >= 1000)
        {
            LOG_WARN("联网状态探测耗时 %" PRIu64 " 毫秒 (结果 %d): 有检测地址不可达, 已按短超时放弃并换下一个",
                     elapsed, (int)observed);
        }

        for (int i = 0; i < PROBE_INTERVAL_MS / 100; i++)
        {
            if (!atomic_load(&s_probe_running)) return 0;
            sleep_ms(100, false);
        }
    }

    return 0;
}

static bool start_probe_worker(sim_thread_func worker)
{
    probe_lock();
    if (s_probe_thread != NULL)
    {
        const bool running = atomic_load(&s_probe_running);
        probe_unlock();
        return running;
    }

    atomic_store(&s_probe_cached, STATUS_ERROR);
    atomic_store(&s_probe_running, true);
    s_probe_thread = sim_thread_create(worker, NULL);
    const bool started = s_probe_thread != NULL;
    if (!started) atomic_store(&s_probe_running, false);
    probe_unlock();
    return started;
}

void net_probe_start(void)
{
    if (!start_probe_worker(probe_thread))
        LOG_WARN("联网状态探测线程创建失败或旧线程尚未回收");
}

bool net_probe_stop_checked(void)
{
    probe_lock();
    atomic_store(&s_probe_running, false);
    if (s_probe_thread != NULL)
    {
        if (sim_thread_join(s_probe_thread, NULL) != 0)
        {
            probe_unlock();
            return false;
        }
        sim_thread_destroy(s_probe_thread);
        s_probe_thread = NULL;
    }
    atomic_store(&s_probe_cached, STATUS_ERROR);
    probe_unlock();
    return true;
}

void net_probe_stop(void)
{
    if (!net_probe_stop_checked())
        LOG_ERROR("联网状态探测线程等待失败, 保留线程对象");
}

static int probe_lifecycle_test_worker(void* arg)
{
    (void)arg;
    while (atomic_load(&s_probe_running))
    {
#ifdef _WIN32
        Sleep(1);
#else
        const struct timespec delay = {0, 1000000L};
        nanosleep(&delay, NULL);
#endif
    }
    return 0;
}

static int probe_lifecycle_test_stop(void* arg)
{
    (void)arg;
    return net_probe_stop_checked() ? 0 : 1;
}

bool probe_lifecycle_self_test(void)
{
    atomic_bool cancelled_worker = false;
    /* Must return before consulting account configuration or opening sockets. */
    if (check_network_status_impl(true, &cancelled_worker) != STATUS_ERROR)
        return false;
    for (int i = 0; i < 16; ++i)
    {
        if (!start_probe_worker(probe_lifecycle_test_worker) ||
            !start_probe_worker(probe_lifecycle_test_worker))
        {
            net_probe_stop_checked();
            return false;
        }
        atomic_store(&s_probe_cached, STATUS_OK);
        sim_thread_t* one = sim_thread_create(probe_lifecycle_test_stop, NULL);
        sim_thread_t* two = sim_thread_create(probe_lifecycle_test_stop, NULL);
        int rc1 = -1, rc2 = -1;
        if (one != NULL)
        {
            if (sim_thread_join(one, &rc1) != 0) abort();
            sim_thread_destroy(one);
        }
        if (two != NULL)
        {
            if (sim_thread_join(two, &rc2) != 0) abort();
            sim_thread_destroy(two);
        }
        if (!net_probe_stop_checked() || rc1 != 0 || rc2 != 0 ||
            s_probe_thread != NULL || atomic_load(&s_probe_running) ||
            atomic_load(&s_probe_cached) != STATUS_ERROR) return false;
    }
    return true;
}

network_status_t net_probe_status(void)
{
    if (!atomic_load(&s_probe_running)) return STATUS_ERROR;
    return atomic_load(&s_probe_cached);
}
