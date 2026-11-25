#include "utils/runtime/Watchdog.h"

#include "core/States.h"

#ifndef _WIN32
#include <unistd.h>
#include <pthread.h>
#include <time.h>
#endif

#include "utils/sim/SimThread.h"

#include "utils/time/TimeUtils.h"
#include "utils/log/Logger.h"

#include <stdatomic.h>
#include <stdlib.h>

#ifdef _WIN32
#include <stdlib.h>
#endif

#define WATCHDOG_TICK_MS 500

#define WATCHDOG_MARGIN_MS 5000

#define WATCHDOG_MIN_BUDGET_MS 1000

#define WATCHDOG_NET_MARGIN_MS 10000

#define WATCHDOG_START_GRACE_MS 30000

/* 墙钟与单调钟的进度差超过这个值, 就认定是墙钟被调整, 不是主循环卡住 */
#define WATCHDOG_CLOCK_JUMP_MS 3000

#define WATCHDOG_MS_PER_DAY 86400000ULL

#define WATCHDOG_MS_PER_HOUR 3600000ULL

#define WATCHDOG_MS_PER_MIN 60000ULL

/* A single snapshot makes pet, stop and timeout claim mutually exclusive.
 * Bits 63..62: mode; bits 61..32: generation; bits 31..0: budget. */
#define WD_MODE_MASK (UINT64_C(3) << 62)
#define WD_RUNNING (UINT64_C(1) << 62)
#define WD_FIRING (UINT64_C(2) << 62)
#define WD_SEQ_MASK (UINT64_C(0x3fffffff) << 32)
static _Atomic uint64_t s_snapshot = 0;
static sim_thread_t* s_thread = NULL;
static _Thread_local bool tl_in_watchdog = false;

/* Only lifecycle callers acquire this lock. The watchdog and pet callers
 * never acquire it, so holding it across join cannot block worker exit. */
#ifdef _WIN32
static SRWLOCK s_lifecycle = SRWLOCK_INIT;
static void lifecycle_lock(void) { AcquireSRWLockExclusive(&s_lifecycle); }
static void lifecycle_unlock(void) { ReleaseSRWLockExclusive(&s_lifecycle); }
#else
static pthread_mutex_t s_lifecycle = PTHREAD_MUTEX_INITIALIZER;
static void lifecycle_lock(void) { pthread_mutex_lock(&s_lifecycle); }
static void lifecycle_unlock(void) { pthread_mutex_unlock(&s_lifecycle); }
#endif

static bool snapshot_running(uint64_t value)
{
    return (value & WD_MODE_MASK) == WD_RUNNING;
}

static uint64_t next_pet(uint64_t old, uint32_t budget)
{
    return WD_RUNNING | (((old & WD_SEQ_MASK) + (UINT64_C(1) << 32)) & WD_SEQ_MASK) | budget;
}

static bool claim_timeout(_Atomic uint64_t* state, uint64_t observed)
{
    if (!snapshot_running(observed)) return false;
    const uint64_t firing = (observed & ~WD_MODE_MASK) | WD_FIRING;
    return atomic_compare_exchange_strong(state, &observed, firing);
}

static void publish_pet(_Atomic uint64_t* state, uint32_t budget)
{
    uint64_t old = atomic_load(state);
    while (snapshot_running(old))
    {
        const uint64_t next = next_pet(old, budget);
        if (atomic_compare_exchange_weak(state, &old, next)) return;
    }
}

static void request_watchdog_stop(_Atomic uint64_t* state)
{
    uint64_t old = atomic_load(state);
    while (snapshot_running(old))
    {
        const uint64_t stopped = old & ~WD_MODE_MASK;
        if (atomic_compare_exchange_weak(state, &old, stopped)) return;
    }
}

/* 把毫秒折成可读时长: 墙钟一跳几十小时时, 原始毫秒数根本看不出量级 */
static void format_duration(const uint64_t ms, char* buf, const size_t buf_size)
{
    const uint64_t days = ms / WATCHDOG_MS_PER_DAY;
    const uint64_t hours = ms % WATCHDOG_MS_PER_DAY / WATCHDOG_MS_PER_HOUR;
    const uint64_t mins = ms % WATCHDOG_MS_PER_HOUR / WATCHDOG_MS_PER_MIN;
    const uint64_t secs = ms % WATCHDOG_MS_PER_MIN / 1000;
    const uint64_t millis = ms % 1000;

    if (days > 0) snprintf(buf, buf_size, "%" PRIu64 " 天 %" PRIu64 " 小时", days, hours);
    else if (hours > 0) snprintf(buf, buf_size, "%" PRIu64 " 小时 %" PRIu64 " 分", hours, mins);
    else if (mins > 0) snprintf(buf, buf_size, "%" PRIu64 " 分 %" PRIu64 " 秒", mins, secs);
    else snprintf(buf, buf_size, "%" PRIu64 ".%03" PRIu64 " 秒", secs, millis);
}

/**
 * @brief 判定卡死后的动作
 *
 * 不能调 shut(): 它会 join 各个线程, 可能正好卡在同一个死锁上, 那就一起卡住了。
 * 也不能指望常规的 LOG_* —— 日志虽然是无锁的, 但这里要尽量少做事。
 * 所以只做一次直达 fd 的 write, 然后立刻退出, 交给外部监管者重新拉起。
 * @param budget_ms 超时时声明的预算
 * @param overdue_ms 超出预算多久
 */
static void watchdog_fire(const uint32_t budget_ms, const uint64_t overdue_ms)
{
    char msg[256];
    snprintf(msg, sizeof(msg),
        WATCHDOG_KILL_MARK ": 主循环声明 %u 毫秒内会再来打卡, 已超时 %llu 毫秒没有动静. "
        "本进程将退出, 由外部监管者 (procd / systemd / SCM) 重新拉起",
        budget_ms, (unsigned long long)overdue_ms);

    log_raw_line(msg);

    /**
     * 用 _exit 而不是 exit: exit 会跑 atexit 回调、刷 stdio 缓冲, 那些都可能
     * 卡在同一个问题上。这里要的是"立刻死掉让监管者重启"
     */
    _exit(1);
}

/**
 * @brief 看门狗线程主循环
 * @param arg 未使用
 * @return 恒为 0 (判定卡死时走 _exit, 不会返回)
 */
static int watchdog_app(void* arg)
{
    (void)arg;
    tl_in_watchdog = true; // 本线程的睡眠不算"主循环在动", 见 tl_in_watchdog 的说明
    tl_thread_idx = -1;
    tl_thread_name = "watchdog";

    uint64_t last_snapshot = atomic_load(&s_snapshot);
    uint32_t budget_ms = (uint32_t)last_snapshot;

    /**
     * 计时必须用单调钟: OpenWrt 没有 RTC, NTP 一同步墙钟就会前跳几十小时,
     * 用墙钟会把正常的心跳等待判成卡死 (曾出现过"声明 1000 毫秒, 已超时 1.7 亿毫秒").
     */
    uint64_t expected_by = get_steady_tm_ms() + budget_ms;
    uint64_t deadline = expected_by + WATCHDOG_MARGIN_MS;
    uint64_t last_steady = get_steady_tm_ms();
    uint64_t last_wall = get_cur_tm_ms();

    /**
     * 第一轮必定重新取一次预算。
     *
     * 少了这一步会有个竞态: 主循环可能在【本线程读到初始序号之前】就已经打过卡了,
     * 那次打卡会被当成"序号没变"而丢掉, 预算于是永远是初始值 —— 表现就是
     * 明明有打卡, 判定时却报"声明 0 毫秒", 而且按启动宽限而不是按声明的预算计时。
     * (这不是想出来的, 是测试日志里那句"声明 0 毫秒"暴露出来的)
     */
    bool first_tick = true;

    while (snapshot_running(atomic_load(&s_snapshot)))
    {
        sleep_ms(WATCHDOG_TICK_MS, true);

        /**
         * 关闭流程中主循环本来就不再打卡了, 那不是卡死。
         * 这里必须放行, 否则正常退出会被误判, 还会盖掉真正的退出原因
         */
        if (g_need_exit || stop_requested()) return 0;

        // 打卡分支也要走单调钟: 这里算出来的 expected_by 直接参与后面的判定
        const uint64_t pet_now = get_steady_tm_ms();

        const uint64_t observed = atomic_load(&s_snapshot);
        if (!snapshot_running(observed)) return 0;
        if (first_tick || observed != last_snapshot)
        {
            budget_ms = (uint32_t)observed;
            last_snapshot = observed;
            expected_by = pet_now + (uint64_t)budget_ms;
            deadline = expected_by + WATCHDOG_MARGIN_MS;
            first_tick = false;
            last_steady = pet_now;
            last_wall = get_cur_tm_ms();
            continue;
        }

        const uint64_t now = pet_now;
        const uint64_t step = now - last_steady;
        const uint64_t wall_now = get_cur_tm_ms();
        const uint64_t wall_step = wall_now - last_wall;

        /**
         * 判定开枪之前先看两个钟对不对得上。
         *
         * 判据是【两个钟的进度互相矛盾】(比如单调钟只走了几毫秒, 墙钟却跳了 47 小时),
         * 而不是"超过了某个绝对时长" —— 后者会把真卡死一起放过。
         * 真卡死时两个钟是一起走的, 这里不会成立, 该开枪还是开枪。
         * 顺带把墙钟为什么对不上记进日志, 否则下次还得靠人工拿两条时间戳相减去猜。
         */
        if (step + WATCHDOG_CLOCK_JUMP_MS < wall_step || wall_step + WATCHDOG_CLOCK_JUMP_MS < step)
        {
            char moved[64], elapsed[64];
            format_duration(wall_step > step ? wall_step - step : step - wall_step, moved, sizeof(moved));
            format_duration(step, elapsed, sizeof(elapsed));
            LOG_WARN("检测到墙钟被调整 (跳变约 %s, 单调钟同期只走了 %s), 已重新计时; "
                "NTP 校时不算主循环卡死", moved, elapsed);
        }

        last_steady = now;
        last_wall = wall_now;

        if (now > deadline)
        {
            /* An intervening pet or stop invalidates this exact snapshot.
             * Once the CAS wins, timeout owns the terminal decision. */
            if (g_need_exit || stop_requested()) return 0;
            if (claim_timeout(&s_snapshot, observed))
            {
                if (g_need_exit || stop_requested())
                {
                    atomic_store(&s_snapshot, observed & ~WD_MODE_MASK);
                    return 0;
                }
                watchdog_fire(budget_ms, now - expected_by);
            }
        }
    }

    return 0;
}

static bool start_worker(sim_thread_func worker)
{
    lifecycle_lock();
    if (s_thread != NULL)
    {
        const bool running = snapshot_running(atomic_load(&s_snapshot));
        lifecycle_unlock();
        return running;
    }

    atomic_store(&s_snapshot, next_pet(atomic_load(&s_snapshot), WATCHDOG_START_GRACE_MS));
    s_thread = sim_thread_create(worker, NULL);
    const bool started = s_thread != NULL;
    if (!started) request_watchdog_stop(&s_snapshot);
    lifecycle_unlock();
    return started;
}

bool watchdog_start(void)
{
    if (!start_worker(watchdog_app))
    {
        LOG_ERROR("看门狗线程创建失败或上一线程尚未回收");
        return false;
    }
    return true;
}

static bool stop_worker(void)
{
    if (tl_in_watchdog) return false;
    lifecycle_lock();
    request_watchdog_stop(&s_snapshot);
    if (s_thread != NULL)
    {
        if (sim_thread_join(s_thread, NULL) != 0)
        {
            lifecycle_unlock();
            return false; // retain ownership; never start a second worker
        }
        sim_thread_destroy(s_thread);
        s_thread = NULL;
    }
    atomic_store(&s_snapshot, atomic_load(&s_snapshot) & ~WD_MODE_MASK);
    lifecycle_unlock();
    return true;
}

void watchdog_stop(void)
{
    if (!stop_worker()) LOG_ERROR("看门狗线程等待失败, 保留线程对象");
}

void watchdog_pet(uint32_t budget_ms)
{
    if (tl_in_watchdog) return;
    if (budget_ms < WATCHDOG_MIN_BUDGET_MS) budget_ms = WATCHDOG_MIN_BUDGET_MS;
    publish_pet(&s_snapshot, budget_ms);
}

/* Offline worker: same ownership paths, no clock, network or fatal exit. */
static int watchdog_test_worker(void* arg)
{
    (void)arg;
    while (snapshot_running(atomic_load(&s_snapshot)))
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

static int watchdog_test_stop(void* arg)
{
    (void)arg;
    return stop_worker() ? 0 : 1;
}

bool watchdog_resource_self_test(void)
{
    _Atomic uint64_t local = WD_RUNNING | 1000;
    uint64_t stale = atomic_load(&local);
    publish_pet(&local, 2000);
    if (claim_timeout(&local, stale) || (uint32_t)atomic_load(&local) != 2000) return false;
    stale = atomic_load(&local);
    request_watchdog_stop(&local);
    if (claim_timeout(&local, stale)) return false;
    publish_pet(&local, 3000);
    if (snapshot_running(atomic_load(&local))) return false;
    atomic_store(&local, next_pet(atomic_load(&local), 1000));
    if (claim_timeout(&local, stale)) return false;
    if (!claim_timeout(&local, atomic_load(&local))) return false;
    publish_pet(&local, 3000);
    request_watchdog_stop(&local);
    if ((atomic_load(&local) & WD_MODE_MASK) != WD_FIRING) return false;

    for (int i = 0; i < 16; ++i)
    {
        if (!start_worker(watchdog_test_worker) || !start_worker(watchdog_test_worker))
        {
            stop_worker();
            return false;
        }
        watchdog_pet(2500);
        if ((uint32_t)atomic_load(&s_snapshot) != 2500) { stop_worker(); return false; }
        sim_thread_t* one = sim_thread_create(watchdog_test_stop, NULL);
        sim_thread_t* two = sim_thread_create(watchdog_test_stop, NULL);
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
        if (!stop_worker() || rc1 != 0 || rc2 != 0 || s_thread != NULL ||
            (atomic_load(&s_snapshot) & WD_MODE_MASK) != 0) return false;
    }
    return true;
}

void watchdog_pet_network(void)
{
    if (!snapshot_running(atomic_load(&s_snapshot)) || tl_in_watchdog) return;

    /**
     * 一次网络请求最长 = 连接超时 + 操作超时 + 余量。逐个请求覆盖, 不去估
     * "一轮循环最多几次请求" —— 估小一次就会把慢请求误判成卡死。
     */
    const uint64_t budget = (uint64_t)(g_conn_timeout + g_op_timeout) * 1000ULL + WATCHDOG_NET_MARGIN_MS;

    watchdog_pet((uint32_t)(budget > UINT32_MAX ? UINT32_MAX : budget));
}
