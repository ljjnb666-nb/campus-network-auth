#include "control/Control.h"

#include "core/States.h"

#include "utils/sim/SimThread.h"

#include "utils/text/TextUtils.h"
#include "utils/log/Logger.h"

#include <cJSON/cJSON.h>

#include "ControlInternal.h"
#include "utils/time/TimeUtils.h"
#include <stdatomic.h>
#include <stdlib.h>
#ifndef _WIN32
#include <pthread.h>
#endif

#define TL_IDX_CONTROL (-3)

#define CTL_TIMEOUT_MS 1000

static atomic_bool s_server_running = false;

#ifdef _WIN32
static SRWLOCK s_lifecycle = SRWLOCK_INIT;
static SRWLOCK s_wsa_lock = SRWLOCK_INIT;
static void lifecycle_lock(void) { AcquireSRWLockExclusive(&s_lifecycle); }
static void lifecycle_unlock(void) { ReleaseSRWLockExclusive(&s_lifecycle); }
#else
static pthread_mutex_t s_lifecycle = PTHREAD_MUTEX_INITIALIZER;
static void lifecycle_lock(void) { pthread_mutex_lock(&s_lifecycle); }
static void lifecycle_unlock(void) { pthread_mutex_unlock(&s_lifecycle); }
#endif

/* An absolute deadline must not be extended by a peer sending more bytes.
 * A failed/backwards clock observation conservatively ends the wait. */
static long remaining_ms(uint64_t start, uint64_t now, long timeout_ms)
{
    if (timeout_ms <= 0 || start == 0 || now < start) return 0;
    const uint64_t elapsed = now - start;
    return elapsed >= (uint64_t)timeout_ms ? 0 : timeout_ms - (long)elapsed;
}

static ctl_sock_t s_listen_sock = CTL_INVALID_SOCK;

static uint16_t s_server_port = CONTROL_DEFAULT_PORT;

static sim_thread_t* s_server_thread = NULL;

uint16_t g_client_port = CONTROL_DEFAULT_PORT;

/* ------------------------------------------------------------------
 * 通用
 * ------------------------------------------------------------------ */

#ifdef _WIN32
bool ctl_net_init(void)
{
    /**
     * WSAStartup 是带引用计数的, 每调一次都要有对应的 WSACleanup。
     * 每次请求都调它的话计数只增不减 (Web 进程每刷一次页面就调一次),
     * 属于记账式泄漏。初始化一次就够, 进程退出时由系统统一回收。
     */
    static bool s_wsa_ready = false;
    AcquireSRWLockExclusive(&s_wsa_lock);
    if (!s_wsa_ready)
    {
        WSADATA wsa;
        s_wsa_ready = WSAStartup(MAKEWORD(2, 2), &wsa) == 0;
    }
    const bool ready = s_wsa_ready;
    ReleaseSRWLockExclusive(&s_wsa_lock);
    return ready;
}
#else
bool ctl_net_init(void)
{
    return true;
}
#endif

/**
 * @brief 等待套接字可读
 * @param sock 套接字
 * @param timeout_ms 超时毫秒数
 * @return 1 可读, 0 超时, -1 出错
 */
static int ctl_wait_readable(const ctl_sock_t sock, const long timeout_ms)
{
    if (sock == CTL_INVALID_SOCK || timeout_ms < 0) return -1;
#ifndef _WIN32
    if (sock < 0 || sock >= FD_SETSIZE) return -1;
#endif
    fd_set read_set;
    FD_ZERO(&read_set);
    FD_SET(sock, &read_set);

    struct timeval tv;
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;

#ifdef _WIN32
    const int ready = select(0, &read_set, NULL, NULL, &tv);
#else
    const int ready = select(sock + 1, &read_set, NULL, NULL, &tv);
#endif
    if (ready <= 0) return ready;

    return FD_ISSET(sock, &read_set) ? 1 : 0;
}

int ctl_wait_writable(const ctl_sock_t sock, const long timeout_ms)
{
    if (sock == CTL_INVALID_SOCK || timeout_ms < 0) return -1;
#ifndef _WIN32
    if (sock < 0 || sock >= FD_SETSIZE) return -1;
#endif
    fd_set write_set;
    FD_ZERO(&write_set);
    FD_SET(sock, &write_set);

    struct timeval tv;
    tv.tv_sec = timeout_ms / 1000;
    tv.tv_usec = (timeout_ms % 1000) * 1000;

#ifdef _WIN32
    const int ready = select(0, NULL, &write_set, NULL, &tv);
#else
    const int ready = select(sock + 1, NULL, &write_set, NULL, &tv);
#endif
    if (ready <= 0) return ready;

    return FD_ISSET(sock, &write_set) ? 1 : 0;
}

bool ctl_port_available(const uint16_t port)
{
    if (ctl_net_init() == false) return false;

    const ctl_sock_t sock = socket(AF_INET, SOCK_STREAM, 0);
    if (sock == CTL_INVALID_SOCK) return false;

    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    const bool available = bind(sock, (struct sockaddr*)&addr, sizeof(addr)) == 0;

    ctl_close(sock);
    return available;
}

bool control_port_available(const uint16_t port)
{
    return ctl_port_available(port);
}

/**
 * @brief 读取一行报文
 * @param sock 套接字
 * @param buf 缓冲
 * @param buf_len 缓冲长度
 * @return 读到的长度, 0 表示对端关闭或超时
 */
size_t ctl_read_line(const ctl_sock_t sock, char* buf, const size_t buf_len)
{
    if (buf == NULL || buf_len == 0) return 0;
    size_t used = 0;
    const uint64_t start = get_steady_tm_ms();

    while (used + 1 < buf_len)
    {
        const long left = remaining_ms(start, get_steady_tm_ms(), CTL_TIMEOUT_MS);
        if (left == 0 || ctl_wait_readable(sock, left) != 1) break;

        const int n = recv(sock, buf + used, (int)(buf_len - 1 - used), 0);
        if (n <= 0) break;

        used += (size_t)n;
        if (memchr(buf, '\n', used) != NULL)
        {
            buf[used] = '\0';
            return used;
        }
    }

    /* Do not dispatch or parse a truncated frame. */
    buf[0] = '\0';
    return 0;
}

/**
 * @brief 等对端把连接关掉 (最多 timeout_ms)
 *
 * 读到的数据直接丢弃: 客户端发完一行请求就不会再发东西, 这里能读到的只可能是
 * 对端关闭 (FIN), 或者干脆读到错误 (RST)。
 */
void ctl_wait_close(const ctl_sock_t sock, const long timeout_ms)
{
    char sink[64];
    const uint64_t start = get_steady_tm_ms();

    while (true)
    {
        const long left = remaining_ms(start, get_steady_tm_ms(), timeout_ms);
        if (left == 0) return;
        const int ready = ctl_wait_readable(sock, left > 100 ? 100 : left);
        if (ready < 0) return;
        if (ready == 0) continue;

        const int n = recv(sock, sink, (int)sizeof(sink), 0);
        if (n <= 0) return; // 0 = FIN, -1 = RST / 错误
    }
}

/* ------------------------------------------------------------------
 * 认证进程侧: 控制服务
 * ------------------------------------------------------------------ */

/**
 * @brief 组装状态应答
 * @param reply 输出缓冲
 * @param reply_len 缓冲长度
 */
static void ctl_reply_status(char* reply, const size_t reply_len)
{
    cJSON* root = cJSON_CreateObject();
    cJSON* data = cJSON_CreateObject();

    /**
     * 这里读的是认证线程在写的运行时状态。
     * 沿用项目现有的跨线程裸 bool 风格 (与 TimeControl 一致),
     * 都是单字长标量, 且只有认证线程与这里的读取方
     */
    uint8_t account = 0;
    bool is_authed = false;
    bool is_running = false;
    bool is_time_disabled = false;

    if (g_prog_status != NULL && g_prog_cnt > 0)
    {
        account = g_prog_status[0].cfg.idx;
        is_authed = g_prog_status[0].runtime_status.is_authed;
        is_running = g_prog_status[0].runtime_status.is_running;
        is_time_disabled = g_prog_status[0].runtime_status.is_time_disabled;
    }

    cJSON_AddNumberToObject(data, "account", account);
    cJSON_AddBoolToObject(data, "is_authed", is_authed);
    cJSON_AddBoolToObject(data, "is_running", is_running);
    cJSON_AddBoolToObject(data, "is_time_disabled", is_time_disabled);
    cJSON_AddStringToObject(data, "bootId", boot_id_get());

    cJSON_AddBoolToObject(root, "ok", true);
    cJSON_AddItemToObject(root, "data", data);

    char* text = cJSON_PrintUnformatted(root);
    snprintf(reply, reply_len, "%s\n", safe_str(text));
    free(text);
    cJSON_Delete(root);
}

/**
 * @brief 组装简单应答
 * @param reply 输出缓冲
 * @param reply_len 缓冲长度
 * @param ok 是否成功
 * @param error 失败原因 (成功时可为 NULL)
 */
static void ctl_reply_result(char* reply, const size_t reply_len, const bool ok, const char* error)
{
    cJSON* root = cJSON_CreateObject();
    cJSON_AddBoolToObject(root, "ok", ok);
    if (ok == false) cJSON_AddStringToObject(root, "error", safe_str(error));

    char* text = cJSON_PrintUnformatted(root);
    snprintf(reply, reply_len, "%s\n", safe_str(text));
    free(text);
    cJSON_Delete(root);
}

/**
 * @brief 校验请求令牌
 *
 * 控制通道只监听回环, 但本机其它进程同样连得上;
 * 没配令牌时不做校验 (手工单独起进程的场景)
 * @param req 请求对象
 * @return 令牌是否正确
 */
static bool ctl_check_token(const cJSON* req)
{
    if (g_control_token[0] == '\0') return true;

    const cJSON* item = cJSON_GetObjectItem(req, "token");
    if (item == NULL || cJSON_IsString(item) == false) return false;

    // 常数时间比较: 不按第一个不匹配字节提前返回
    const char* given = item->valuestring;
    const char* expect = g_control_token;
    const size_t given_len = strlen(given);
    const size_t expect_len = strlen(expect);
    if (given_len != expect_len) return false;

    unsigned char diff = 0;
    for (size_t i = 0; i < expect_len; i++)
    {
        diff |= (unsigned char)given[i] ^ (unsigned char)expect[i];
    }
    return diff == 0;
}

/**
 * @brief 处理一条请求并生成应答
 * @param request 请求报文
 * @param reply 输出缓冲
 * @param reply_len 缓冲长度
 */
static void ctl_dispatch(const char* request, char* reply, const size_t reply_len)
{
    cJSON* req = cJSON_Parse(request);
    if (req == NULL)
    {
        ctl_reply_result(reply, reply_len, false, "bad json");
        return;
    }

    if (ctl_check_token(req) == false)
    {
        LOG_WARN("控制通道收到令牌不匹配的请求, 已拒绝");
        ctl_reply_result(reply, reply_len, false, "bad token");
        cJSON_Delete(req);
        return;
    }

    const cJSON* cmd_item = cJSON_GetObjectItem(req, "cmd");
    const char* cmd = (cmd_item != NULL && cJSON_IsString(cmd_item)) ? cmd_item->valuestring : "";

    if (strcmp(cmd, "status") == 0)
    {
        ctl_reply_status(reply, reply_len);
    }
    else if (strcmp(cmd, "restart_auth") == 0 || strcmp(cmd, "apply_config") == 0)
    {
        /**
         * "重新认证"与"应用新配置"走同一条路: 都不再做进程内热重载, 本进程 (认证)
         * 直接退出, 由监管者用当前配置重新拉起 —— 能收到这条命令就说明有监管者在
         * (Web 子进程只由它启动)。
         *
         * 只置标志不在这里调 shut(): 本函数跑在控制服务线程上, 而 shut() 会去
         * join 这条线程, 自己等自己。主循环看到标志后退出, 由 dialer_app 跑完
         * clean() (含登出) 再结束进程。
         */
        const bool applying = (strcmp(cmd, "apply_config") == 0);
        LOG_DEBUG("控制通道收到%s请求, 本进程将退出并由监管者重新拉起",
                 applying ? "应用新配置" : "重新认证");
        g_stop_requested = 1;
        ctl_reply_result(reply, reply_len, true, NULL);
    }
    else if (strcmp(cmd, "shutdown") == 0)
    {
        /**
         * 只置标志, 不在这里调 shut(): 本函数就跑在控制服务线程上,
         * 而 shut() 会 control_server_stop() 去 join 这条线程 —— 自己等自己。
         * 主循环看到 g_stop_requested 后退出, 由 dialer_app 跑完 clean() (含登出)
         */
        LOG_DEBUG("控制通道收到退出请求, 本进程将正常关闭");
        g_stop_requested = 1;
        ctl_reply_result(reply, reply_len, true, NULL);
    }
    else
    {
        ctl_reply_result(reply, reply_len, false, "unknown cmd");
    }

    cJSON_Delete(req);
}

/**
 * @brief 处理一个客户端连接
 * @param client 客户端套接字
 */
static void ctl_handle_client(const ctl_sock_t client)
{
    char request[CONTROL_MSG_MAX];
    char reply[CONTROL_MSG_MAX];

    if (ctl_read_line(client, request, sizeof(request)) == 0) return;

    ctl_dispatch(request, reply, sizeof(reply));

    if (send(client, reply, (int)strlen(reply), 0) <= 0)
    {
        LOG_WARN("控制通道应答发送失败");
        return;
    }

    /**
     * 让客户端先关, 本端再关。
     *
     * 谁先 close 谁留 TIME_WAIT, 而这个端点是控制端口: 落在服务端的话, 认证进程
     * 重启后重新 bind 会被上一个实例留下的 TIME_WAIT 挡住 (Windows 默认约 2 分钟,
     * SO_REUSEADDR 对"被接受连接留下的 TIME_WAIT"并不总能绕开), 表现就是网页
     * 显示"认证进程不可达"、监管者也请求不到优雅退出。
     *
     * 做法: 先关自己的写方向 (客户端随即读到 EOF 并 close), 再等它的 FIN。
     */
    ctl_shutdown_write(client);
    ctl_wait_close(client, 500);
}

/**
 * @brief 控制服务线程主循环
 */
static int ctl_server_app(void* arg)
{
    (void)arg;
    tl_thread_idx = TL_IDX_CONTROL;
    tl_thread_name = "control"; // 日志里标成本线程, 不要把标签交给魔术数字去猜

    LOG_DEBUG("控制通道服务线程已启动");

    while (atomic_load(&s_server_running))
    {
        if (ctl_wait_readable(s_listen_sock, 1000) != 1) continue;
        if (!atomic_load(&s_server_running)) break;

        const ctl_sock_t client = accept(s_listen_sock, NULL, NULL);
        if (client == CTL_INVALID_SOCK) continue;

        ctl_handle_client(client);
        ctl_close(client);
    }

    LOG_DEBUG("控制通道已停止");
    return 0;
}

static bool start_locked(const uint16_t port)
{
    if (s_server_thread != NULL)
        return atomic_load(&s_server_running) && s_server_port == port;
    if (ctl_net_init() == false)
    {
        LOG_ERROR("网络库初始化失败, 控制通道无法启动");
        return false;
    }

    s_listen_sock = socket(AF_INET, SOCK_STREAM, 0);
    if (s_listen_sock == CTL_INVALID_SOCK)
    {
        LOG_ERROR("创建控制通道套接字失败");
        return false;
    }

    /**
     * 允许立即重新绑定同一个端口
     *
     * 认证进程每次重启 (控制通道的"重新认证"/"应用新配置"、崩溃后被监管者重拉)
     * 都要重新 bind。连接由客户端先关闭, TIME_WAIT 落在临时端口上, 不影响本端口重绑。
     *
     * Windows 上 SO_REUSEADDR 的语义与 POSIX 不同: 它允许其它进程在端口被活跃
     * 监听时再次 bind 成功 (端口劫持, 可截获带令牌的控制报文) —— 改用独占绑定。
     */
#ifdef _WIN32
    int exclusive = 1;
    if (setsockopt(s_listen_sock, SOL_SOCKET, SO_EXCLUSIVEADDRUSE, (const char*)&exclusive, sizeof(exclusive)) != 0)
    {
        LOG_ERROR("控制通道设置 SO_EXCLUSIVEADDRUSE 失败, 拒绝监听");
        ctl_close(s_listen_sock);
        s_listen_sock = CTL_INVALID_SOCK;
        return false;
    }
#else
    int reuse = 1;
    if (setsockopt(s_listen_sock, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse, sizeof(reuse)) != 0)
    {
        LOG_WARN("控制通道设置 SO_REUSEADDR 失败 (重启后可能短暂无法绑定端口)");
    }
#endif

    // 只监听回环: 控制通道不对外开放
    struct sockaddr_in addr;
    memset(&addr, 0, sizeof(addr));
    addr.sin_family = AF_INET;
    addr.sin_port = htons(port);
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);

    if (bind(s_listen_sock, (struct sockaddr*)&addr, sizeof(addr)) != 0)
    {
        LOG_ERROR("控制通道绑定 127.0.0.1:%" PRIu16 " 失败 (端口可能已被占用)", port);
        ctl_close(s_listen_sock);
        s_listen_sock = CTL_INVALID_SOCK;
        return false;
    }

    if (listen(s_listen_sock, 4) != 0)
    {
        LOG_ERROR("控制通道监听失败");
        ctl_close(s_listen_sock);
        s_listen_sock = CTL_INVALID_SOCK;
        return false;
    }

    atomic_store(&s_server_running, true);
    s_server_port = port;
    s_server_thread = sim_thread_create(ctl_server_app, NULL);
    if (s_server_thread == NULL)
    {
        LOG_ERROR("控制通道线程创建失败");
        atomic_store(&s_server_running, false);
        ctl_close(s_listen_sock);
        s_listen_sock = CTL_INVALID_SOCK;
        return false;
    }

    LOG_DEBUG("控制通道已启动, 监听 127.0.0.1:%" PRIu16 "%s", s_server_port,
        g_control_token[0] != '\0' ? " (需要令牌)" : " (未设令牌, 本机任意进程都可下发动作)");
    return true;
}

bool control_server_start(const uint16_t port)
{
    lifecycle_lock();
    const bool ok = start_locked(port);
    lifecycle_unlock();
    return ok;
}

static bool stop_locked(void)
{
    atomic_store(&s_server_running, false);
    if (s_server_thread != NULL)
    {
        if (sim_thread_join(s_server_thread, NULL) != 0) return false;
        sim_thread_destroy(s_server_thread);
        s_server_thread = NULL;
    }

    if (s_listen_sock != CTL_INVALID_SOCK)
    {
        ctl_close(s_listen_sock);
        s_listen_sock = CTL_INVALID_SOCK;
    }
    return true;
}

void control_server_stop(void)
{
    lifecycle_lock();
    const bool ok = stop_locked();
    lifecycle_unlock();
    if (!ok) LOG_ERROR("控制通道线程等待失败, 保留线程与套接字");
}

/* A kernel-assigned loopback port, no control command dispatch, no external
 * network or account state. Exercise the production frame/close readers. */
static bool loopback_reader_self_test(void)
{
    if (!ctl_net_init()) return false;
    ctl_sock_t listener = CTL_INVALID_SOCK, peer = CTL_INVALID_SOCK, accepted = CTL_INVALID_SOCK;
    bool ok = false;
    struct sockaddr_in addr = {0};
#ifdef _WIN32
    int len = sizeof(addr);
#else
    socklen_t len = sizeof(addr);
#endif
    addr.sin_family = AF_INET;
    addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    listener = socket(AF_INET, SOCK_STREAM, 0);
    if (listener == CTL_INVALID_SOCK ||
        bind(listener, (struct sockaddr*)&addr, sizeof(addr)) != 0 ||
        listen(listener, 1) != 0 ||
        getsockname(listener, (struct sockaddr*)&addr, &len) != 0) goto done;
    peer = socket(AF_INET, SOCK_STREAM, 0);
    if (peer == CTL_INVALID_SOCK ||
        connect(peer, (struct sockaddr*)&addr, sizeof(addr)) != 0) goto done;
    accepted = accept(listener, NULL, NULL);
    if (accepted == CTL_INVALID_SOCK) goto done;

    char frame[16] = {0};
    if (send(peer, "x", 1, 0) != 1 ||
        ctl_read_line(accepted, frame, sizeof(frame)) != 0 || frame[0] != '\0') goto done;
    if (send(peer, "{}\n", 3, 0) != 3 ||
        ctl_read_line(accepted, frame, sizeof(frame)) != 3 ||
        strcmp(frame, "{}\n") != 0) goto done;
    if (send(peer, "data", 4, 0) != 4) goto done;
    ctl_wait_close(accepted, 50);
    /* The peer is still open. Expiring the wait is not a socket close. */
    if (send(peer, "{}\n", 3, 0) != 3 ||
        ctl_read_line(accepted, frame, sizeof(frame)) != 3 ||
        strcmp(frame, "{}\n") != 0) goto done;
    ok = true;

done:
    if (accepted != CTL_INVALID_SOCK) ctl_close(accepted);
    if (peer != CTL_INVALID_SOCK) ctl_close(peer);
    if (listener != CTL_INVALID_SOCK) ctl_close(listener);
    return ok;
}

bool control_resource_self_test(void)
{
    char byte = 'x';
    return remaining_ms(100, 100, 1000) == 1000 &&
        remaining_ms(100, 1099, 1000) == 1 &&
        remaining_ms(100, 1100, 1000) == 0 &&
        remaining_ms(100, 1101, 1000) == 0 &&
        remaining_ms(100, 99, 1000) == 0 &&
        remaining_ms(0, 100, 1000) == 0 &&
        remaining_ms(100, 100, 0) == 0 &&
        ctl_read_line(CTL_INVALID_SOCK, NULL, 0) == 0 &&
        ctl_read_line(CTL_INVALID_SOCK, &byte, 0) == 0 && byte == 'x' &&
        ctl_read_line(CTL_INVALID_SOCK, &byte, 1) == 0 && byte == '\0' &&
        loopback_reader_self_test();
}

void control_set_port(const uint16_t port)
{
    g_client_port = port;
}
