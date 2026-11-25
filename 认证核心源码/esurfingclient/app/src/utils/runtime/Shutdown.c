#include "utils/runtime/Shutdown.h"

#include "core/States.h"

#include "utils/time/TimeControl.h"
#include "utils/runtime/Watchdog.h"
#include "utils/log/Logger.h"

#include <stdatomic.h>
#include <stdlib.h>
#include <stdio.h>

#ifdef _WIN32
#include <windows.h>
extern bool get_service_mode(void);

/* Provisional native join budget, not the GUI's complete stop timeout.
 * All Windows worker joins consume the same 60-second window. */
#define SHUTDOWN_JOIN_BUDGET_MS 60000u
#else
#include <stdlib.h>
#endif

#ifndef __OPENWRT__
extern void restart_process(void);
#include "control/Control.h"

#ifndef __OPENWRT__
#include "net/NetClient.h"
#include "net/NetTraffic.h"
#endif
#endif

/* Checked variants are internal shutdown contracts. Legacy void wrappers
 * remain available for existing callers. */
extern bool watchdog_stop_checked(void);
extern bool time_control_stop_checked(void);
#ifndef __OPENWRT__
extern bool net_traffic_stop_checked(void);
extern bool net_probe_stop_checked(void);
extern bool control_server_stop_checked(void);
#endif

typedef struct
{
    const char* name;
    bool (*stop)(void);
} shutdown_step_t;

/* Return the first unconfirmed worker. Do not proceed to shared cleanup. */
static const char* stop_sequence(const shutdown_step_t* steps, size_t count)
{
    for (size_t i = 0; i < count; ++i)
        if (!steps[i].stop()) return steps[i].name;
    return NULL;
}

static _Noreturn void shutdown_incomplete(const char* worker)
{
    fprintf(stderr, "[ERROR] worker stop unconfirmed: %s; shutdown incomplete\n", worker);
    fflush(stderr);
    _Exit(EXIT_FAILURE);
}

/* Ownership is transferred only after a confirmed successful join. */
static bool reap_auth_thread(sim_thread_t** owner, int* code)
{
    if (*owner == NULL) return true;
    if (sim_thread_join(*owner, code) != 0) return false;
    sim_thread_destroy(*owner);
    *owner = NULL;
    return true;
}

static int shutdown_test_worker(void* arg)
{
    (void)arg;
    return 23;
}

static int s_stop_test_calls;
static bool shutdown_test_pass(void) { ++s_stop_test_calls; return true; }
static bool shutdown_test_fail(void) { ++s_stop_test_calls; return false; }

bool shutdown_resource_self_test(void)
{
    const shutdown_step_t pass[] = {
        {"first", shutdown_test_pass}, {"second", shutdown_test_pass}
    };
    s_stop_test_calls = 0;
    if (stop_sequence(pass, 2) != NULL || s_stop_test_calls != 2) return false;
    const shutdown_step_t fail[] = {
        {"first", shutdown_test_pass}, {"failed", shutdown_test_fail},
        {"unreachable", shutdown_test_pass}
    };
    s_stop_test_calls = 0;
    const char* rejected_step = stop_sequence(fail, 3);
    if (rejected_step != fail[1].name || s_stop_test_calls != 2) return false;
    int code = -1;
    sim_thread_t* owner = NULL;
    if (!reap_auth_thread(&owner, &code) || code != -1) return false;
    owner = sim_thread_create(shutdown_test_worker, NULL);
    if (owner == NULL) return false;
    if (!reap_auth_thread(&owner, &code)) abort();
    if (owner != NULL || code != 23 || !reap_auth_thread(&owner, NULL)) return false;
    owner = sim_thread_create(shutdown_test_worker, NULL);
    if (owner == NULL) return false;
    if (sim_thread_join(owner, NULL) != 0) abort();
    sim_thread_t* saved = owner;
    const bool rejected = !reap_auth_thread(&owner, NULL) && owner == saved;
    sim_thread_destroy(saved);
    return rejected;
}

void shut(const int8_t exit_code)
{
    /**
     * 关闭流程只允许进入一次 (多个线程可能同时触发关闭)。
     * 原实现是 volatile bool 的 check-then-set —— 两个线程可同时通过判断,
     * 双双进入关闭流程 (N01)。改用 C11 原子交换: 第一个抢到的执行收尾,
     * 其余线程在此处直接返回。
     */
    static atomic_bool shutting_down = false;
    if (atomic_exchange(&shutting_down, true))
    {
        return; // logger may already have been finalized by the owner
    }

    /* Publish shutdown before any join or potentially blocking event output. */
    atomic_store(&g_need_exit, true);
#ifdef _WIN32
    sim_thread_shutdown_budget_begin(SHUTDOWN_JOIN_BUDGET_MS);
#endif

    /**
     * 关闭流程一开始就停掉看门狗: 接下来主循环不再打卡,
     * 不停的话它会把正常退出误判成卡死, 还会盖掉真正的退出原因
     */
    if (!watchdog_stop_checked()) shutdown_incomplete("watchdog");

    LOG_DEBUG("主程序正在关闭");

    /**
     * 监管进程的收尾是"先停子进程再退出", 而且它自己装了信号处理,
     * 所以这里只置退出标志, 具体的关闭顺序交给 work_supervisor() 处理。
     * (Windows 服务收到 SCM 的停止请求时也是走这里)
     */
    if (g_prog_role == ROLE_SUPERVISOR)
    {
#ifdef _WIN32
        sim_thread_shutdown_budget_end();
#endif
        ephemeral_emit_event("exit", exit_code);
        return;
    }

    atomic_store(&g_thread_keep_alive, false);
    /* Request all account workers to stop before waiting for any of them. */
    if (g_prog_status != NULL)
    {
        for (int i = 0; i < g_prog_cnt; ++i)
            g_prog_status[i].runtime_status.is_running = false;
    }

    const shutdown_step_t workers[] = {
#ifndef __OPENWRT__
        {"traffic", net_traffic_stop_checked},
        {"probe", net_probe_stop_checked},
        {"control", control_server_stop_checked},
#endif
        {"scheduler", time_control_stop_checked}
    };
    const char* failed_worker = stop_sequence(workers, sizeof(workers) / sizeof(workers[0]));
    if (failed_worker != NULL) shutdown_incomplete(failed_worker);

    LOG_DEBUG("清理资源中");
    LOG_DEBUG("关闭线程");
    if (g_prog_status != NULL)
    {
        for (int i = 0; i < g_prog_cnt; ++i)
        {
            if (g_prog_status[i].thread == NULL) continue;
            int result_code = 0;
            if (!reap_auth_thread(&g_prog_status[i].thread, &result_code))
            {
                /* The worker may still access credentials/logging. Never wipe
                 * shared state, restart, or report graceful completion here. */
                shutdown_incomplete("authentication");
            }
            LOG_DEBUG("认证线程 %d 退出, 退出码: %d", i, result_code);
        }
    }

#ifdef _WIN32
    sim_thread_shutdown_budget_end();
#endif
    ephemeral_emit_event("exit", exit_code);
    LOG_INFO("退出程序, 退出码: %" PRIu8, exit_code);
    clean_logger();
    // 退出前擦除内存里的凭据 (账号/密码/票据), 不留给崩溃转储与换页文件
    if (g_ephemeral_auth) clear_ephemeral_secrets();
    else clear_all_account_secrets();
    g_shutdown_done = 1; // 通知控制台处理线程: 收尾已经做完了

#ifdef _WIN32
    if (get_service_mode())
    {
        longjmp(g_exit_jmp, 1);
    }
#endif

#ifndef __OPENWRT__
    if (g_need_restart)
    {
        restart_process();
    }
#endif

    exit(exit_code);
}

#ifdef _WIN32
/**
 * Windows 控制台事件处理
 *
 * 只置标志: 这个处理函数跑在系统另起的线程上, 而且随时可能被强杀,
 * 在这里做 join / 打日志 / rename / exit 都不安全。
 * 真正的关闭动作由各角色的主循环看到标志后执行
 */
static BOOL WINAPI console_handler(const DWORD ctrlType)
{
    if (ctrlType == CTRL_CLOSE_EVENT || ctrlType == CTRL_LOGOFF_EVENT || ctrlType == CTRL_SHUTDOWN_EVENT)
        g_console_closed = 1;
    g_stop_requested = 1;

    /**
     * 关窗口 / 注销 / 关机: 系统只给几秒, 而且处理函数一返回进程就会被结束。
     *
     * 这里先等主线程把收尾跑完 (登出、停线程、日志归档), 最多约 4.5 秒;
     * 然后必须 return FALSE 交回默认处理 —— 返回 TRUE 等于告诉系统"我自己处理了",
     * 系统就不结束进程, 进程会带着"没有控制台"的状态留在后台 (关终端关不掉就是这个原因)
     */
    if (ctrlType == CTRL_CLOSE_EVENT || ctrlType == CTRL_LOGOFF_EVENT || ctrlType == CTRL_SHUTDOWN_EVENT)
    {
        for (int i = 0; i < 450 && g_shutdown_done == 0; i++)
        {
            Sleep(10);
        }

        // 收尾跑完之后主线程还要 exit(): 再让出几百毫秒, 别抢在它前面被系统杀掉
        if (g_shutdown_done != 0) Sleep(300);

        return FALSE;
    }

    // Ctrl+C / Ctrl+Break: 只置标志, 由主循环走优雅关闭
    return TRUE;
}

#else
/**
 * Linux/Unix 信号处理
 *
 * 只置标志: 信号处理函数里不能做 join 线程、打日志、rename、exit
 * 这些不是 async-signal-safe 的事 (原来这里直接调 shut(), 有死锁风险)。
 * 真正的关闭动作由各角色的主循环看到标志后执行
 */
static void signal_handler(const int sig)
{
    (void)sig;
    g_stop_requested = 1;
}

#endif

void init_shutdown_hook(void)
{
#ifdef _WIN32
    if (SetConsoleCtrlHandler(console_handler, TRUE) == 0)
    {
        fprintf(stderr, "[ERROR] 设置控制台事件处理失败\n");
        exit(1);
    }
#else
    if (signal(SIGINT, signal_handler) == SIG_ERR)
    {
        fprintf(stderr, "[ERROR] 信号 SIGINT 设置失败\n");
        exit(1);
    }
    if (signal(SIGTERM, signal_handler) == SIG_ERR)
    {
        fprintf(stderr, "[ERROR] 信号 SIGTERM 设置失败\n");
        exit(1);
    }
    if (signal(SIGHUP, signal_handler) == SIG_ERR)
    {
        fprintf(stderr, "[ERROR] 信号 SIGHUP 设置失败\n");
        exit(1);
    }
    if (signal(SIGQUIT, signal_handler) == SIG_ERR)
    {
        fprintf(stderr, "[ERROR] 信号 SIGQUIT 设置失败\n");
        exit(1);
    }
#endif
}
