#include "net/NetTraffic.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <stdatomic.h>

#include "utils/log/Logger.h"
#include "utils/platform/PlatformUtils.h"
#include "utils/sim/SimThread.h"
#include "utils/time/TimeUtils.h"

#include <cJSON/cJSON.h>

#ifdef _WIN32
#include <windows.h>
/** 1601-01-01 到 1970-01-01 的 100 纳秒数 (FILETIME 转 Unix 时间用) */
#define NET_TRAFFIC_EPOCH_DIFF 116444736000000000ULL
typedef SRWLOCK traffic_lock_t;
#define TRAFFIC_LOCK_INIT SRWLOCK_INIT
#else
#include <pthread.h>
#include <time.h>
typedef pthread_mutex_t traffic_lock_t;
#define TRAFFIC_LOCK_INIT PTHREAD_MUTEX_INITIALIZER
#endif

/** 最多同时跟踪多少块网卡 (家用/办公/路由都远到不了这个数) */
#define NET_TRAFFIC_MAX 32
/** 采样间隔 */
#define NET_TRAFFIC_INTERVAL_MS 1000
/** 睡眠切片: 停下时要能在一秒内响应 */
#define NET_TRAFFIC_SLICE_MS 100

/** 上一帧的计数器, 用来做差 */
typedef struct
{
    char id[ADAPTER_ID_LEN];
    uint64_t rx_bytes;
    uint64_t tx_bytes;
} traffic_prev_t;

/** 一帧对外的采样结果 */
typedef struct
{
    char id[ADAPTER_ID_LEN];
    char name[NAME_LENGTH];
    bool connected;
    bool has_rate;
    double down;
    double up;
} traffic_row_t;

static traffic_lock_t s_lock = TRAFFIC_LOCK_INIT;
static traffic_lock_t s_lifecycle = TRAFFIC_LOCK_INIT;
static atomic_bool s_running = false;
static sim_thread_t* s_thread;

static traffic_row_t s_rows[NET_TRAFFIC_MAX];
static uint8_t s_row_count;
static char s_default_id[ADAPTER_ID_LEN];
static uint64_t s_timestamp_ms;

static traffic_prev_t s_prev[NET_TRAFFIC_MAX];
static uint8_t s_prev_count;
static uint64_t s_prev_at_ms;

/**
 * 平台相关的锁 / 时钟 / 睡眠收在这几个小函数里, 上面的采样逻辑就与平台无关
 */
static void acquire(traffic_lock_t* lock)
{
#ifdef _WIN32
    AcquireSRWLockExclusive(lock);
#else
    pthread_mutex_lock(lock);
#endif
}

static void release(traffic_lock_t* lock)
{
#ifdef _WIN32
    ReleaseSRWLockExclusive(lock);
#else
    pthread_mutex_unlock(lock);
#endif
}

static void traffic_lock(void) { acquire(&s_lock); }
static void traffic_unlock(void) { release(&s_lock); }

/* Called only by the lifecycle owner, with no active sampling worker. */
static void clear_samples(void)
{
    traffic_lock();
    s_row_count = 0;
    s_prev_count = 0;
    s_prev_at_ms = 0;
    s_timestamp_ms = 0;
    s_default_id[0] = '\0';
    traffic_unlock();
}

static bool calculate_rate(uint64_t previous_at, uint64_t now,
    uint64_t previous_rx, uint64_t previous_tx, uint64_t rx, uint64_t tx,
    double* down, double* up)
{
    if (previous_at == 0 || now <= previous_at || rx < previous_rx || tx < previous_tx)
        return false;
    const uint64_t elapsed = now - previous_at;
    *down = (double)(rx - previous_rx) * 1000.0 / (double)elapsed;
    *up = (double)(tx - previous_tx) * 1000.0 / (double)elapsed;
    return true;
}

/**
 * @brief 当前时间 (Unix 毫秒)
 *
 * 网页用它判断数据新不新鲜, 必须是墙上时钟而不是开机以来的毫秒数
 */
static uint64_t traffic_now_ms(void)
{
#ifdef _WIN32
    FILETIME file_time;
    GetSystemTimeAsFileTime(&file_time);
    ULARGE_INTEGER value;
    value.LowPart = file_time.dwLowDateTime;
    value.HighPart = file_time.dwHighDateTime;
    if (value.QuadPart < NET_TRAFFIC_EPOCH_DIFF) return 0;
    return (uint64_t)((value.QuadPart - NET_TRAFFIC_EPOCH_DIFF) / 10000ULL);
#else
    struct timespec now;
    if (clock_gettime(CLOCK_REALTIME, &now) != 0 || now.tv_sec < 0) return 0;
    return (uint64_t)now.tv_sec * 1000ULL + (uint64_t)now.tv_nsec / 1000000ULL;
#endif
}

static void traffic_sleep_ms(const int ms)
{
#ifdef _WIN32
    Sleep((DWORD)ms);
#else
    struct timespec delay;
    delay.tv_sec = ms / 1000;
    delay.tv_nsec = (long)(ms % 1000) * 1000000L;
    nanosleep(&delay, NULL);
#endif
}

/**
 * @brief 采一帧: 向系统要计数器, 与上一帧做差得到 B/s
 *
 * 三种情况不给速率 (界面上显示"正在采样"):
 * 第一次见到这块网卡、与上一帧间隔为零、计数器变小 (网卡重插或清零)
 */
static void traffic_sample(void)
{
    adapter_stats_t frames[NET_TRAFFIC_MAX];
    char default_id[ADAPTER_ID_LEN] = {0};
    const uint8_t reported = get_adapters_stats(frames, NET_TRAFFIC_MAX, default_id, sizeof(default_id));
    const uint8_t count = reported > NET_TRAFFIC_MAX ? NET_TRAFFIC_MAX : reported;
    const uint64_t now = get_steady_tm_ms();
    const uint64_t wall_now = traffic_now_ms();

    traffic_row_t rows[NET_TRAFFIC_MAX];
    traffic_prev_t prev[NET_TRAFFIC_MAX];
    for (uint8_t i = 0; i < count; i++)
    {
        memset(&rows[i], 0, sizeof(rows[i]));
        // 两边都是定长数组, 直接按长度拷贝再补 NUL, 免得 snprintf 报"可能截断"
        memcpy(rows[i].id, frames[i].id, sizeof(rows[i].id));
        rows[i].id[sizeof(rows[i].id) - 1] = '\0';
        memcpy(rows[i].name, frames[i].name, sizeof(rows[i].name));
        rows[i].name[sizeof(rows[i].name) - 1] = '\0';
        rows[i].connected = frames[i].connected;
        memcpy(prev[i].id, frames[i].id, sizeof(prev[i].id));
        prev[i].id[sizeof(prev[i].id) - 1] = '\0';
        prev[i].rx_bytes = frames[i].rx_bytes;
        prev[i].tx_bytes = frames[i].tx_bytes;

        for (uint8_t j = 0; j < s_prev_count; j++)
        {
            if (strcmp(s_prev[j].id, rows[i].id) != 0) continue;
            rows[i].has_rate = calculate_rate(s_prev_at_ms, now,
                s_prev[j].rx_bytes, s_prev[j].tx_bytes, frames[i].rx_bytes,
                frames[i].tx_bytes, &rows[i].down, &rows[i].up);
            break;
        }
    }

    traffic_lock();
    memcpy(s_rows, rows, sizeof(traffic_row_t) * count);
    s_row_count = count;
    snprintf(s_default_id, sizeof(s_default_id), "%s", default_id);
    s_timestamp_ms = wall_now;
    memcpy(s_prev, prev, sizeof(traffic_prev_t) * count);
    s_prev_count = count;
    s_prev_at_ms = now;
    traffic_unlock();
}

static int traffic_thread(void* arg)
{
    (void)arg;
    while (atomic_load(&s_running))
    {
        traffic_sample();
        for (int i = 0; i < NET_TRAFFIC_INTERVAL_MS / NET_TRAFFIC_SLICE_MS; i++)
        {
            if (!atomic_load(&s_running)) return 0;
            traffic_sleep_ms(NET_TRAFFIC_SLICE_MS);
        }
    }
    return 0;
}

static bool start_worker(sim_thread_func worker)
{
    acquire(&s_lifecycle);
    if (s_thread != NULL)
    {
        const bool running = atomic_load(&s_running);
        release(&s_lifecycle);
        return running;
    }
    clear_samples();
    atomic_store(&s_running, true);
    s_thread = sim_thread_create(worker, NULL);
    const bool started = s_thread != NULL;
    if (!started) atomic_store(&s_running, false);
    release(&s_lifecycle);
    return started;
}

bool net_traffic_start(void)
{
    if (!start_worker(traffic_thread))
    {
        LOG_WARN("网速采样线程创建失败或旧线程尚未回收");
        return false;
    }
    return true;
}

static bool stop_worker(void)
{
    acquire(&s_lifecycle);
    atomic_store(&s_running, false);
    if (s_thread != NULL)
    {
        if (sim_thread_join(s_thread, NULL) != 0)
        {
            release(&s_lifecycle);
            return false;
        }
        sim_thread_destroy(s_thread);
        s_thread = NULL;
    }
    clear_samples();
    release(&s_lifecycle);
    return true;
}

bool net_traffic_stop_checked(void)
{
    return stop_worker();
}

void net_traffic_stop(void)
{
    if (!net_traffic_stop_checked()) LOG_ERROR("网速采样线程等待失败, 保留线程对象");
}

static int test_worker(void* arg)
{
    (void)arg;
    while (atomic_load(&s_running)) traffic_sleep_ms(1);
    return 0;
}

static int test_stop(void* arg)
{
    (void)arg;
    return stop_worker() ? 0 : 1;
}

static bool traffic_json_self_test(void);

bool traffic_resource_self_test(void)
{
    if (!traffic_json_self_test()) return false;
    double down = 0, up = 0;
    if (!calculate_rate(1000, 2000, 10, 20, 1010, 2020, &down, &up) ||
        down != 1000 || up != 2000 ||
        calculate_rate(0, 1000, 0, 0, 10, 10, &down, &up) ||
        calculate_rate(1000, 1000, 0, 0, 10, 10, &down, &up) ||
        calculate_rate(2000, 1000, 0, 0, 10, 10, &down, &up) ||
        calculate_rate(1000, 2000, 10, 10, 9, 20, &down, &up) ||
        calculate_rate(1000, 2000, 10, 10, 20, 9, &down, &up))
        return false;

    for (int i = 0; i < 16; ++i)
    {
        if (!start_worker(test_worker) || !start_worker(test_worker))
        {
            stop_worker();
            return false;
        }
        traffic_lock();
        s_row_count = 1;
        s_prev_count = 1;
        s_prev_at_ms = 1000;
        traffic_unlock();
        sim_thread_t* one = sim_thread_create(test_stop, NULL);
        sim_thread_t* two = sim_thread_create(test_stop, NULL);
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
            atomic_load(&s_running) || s_row_count != 0 || s_prev_count != 0 ||
            s_prev_at_ms != 0) return false;
    }
    return true;
}

/* Snapshot ownership is independent of the sampling lock. JSON allocation
 * and rendering must not stall the producer or return a partial document.
 * fail_at is a per-call synthetic failure gate, never a global allocator hook. */
typedef struct
{
    traffic_row_t rows[NET_TRAFFIC_MAX];
    uint8_t count;
    char default_id[ADAPTER_ID_LEN];
    uint64_t timestamp;
} traffic_snapshot_t;

static bool json_step(unsigned* step, unsigned fail_at)
{
    return ++(*step) != fail_at;
}

static char* render_snapshot(const traffic_snapshot_t* snapshot, unsigned fail_at)
{
    if (snapshot == NULL || snapshot->count == 0 || snapshot->count > NET_TRAFFIC_MAX) return NULL;
    unsigned step = 0;
    cJSON* root = NULL;
    cJSON* adapters = NULL;
    cJSON* item = NULL;
    char* json = NULL;

    if (!json_step(&step, fail_at) || !(root = cJSON_CreateObject())) goto cleanup;
    if (!json_step(&step, fail_at) || !cJSON_AddNumberToObject(root, "timestamp", (double)snapshot->timestamp)) goto cleanup;
    if (!json_step(&step, fail_at) || !cJSON_AddStringToObject(root, "defaultId", snapshot->default_id)) goto cleanup;
    if (!json_step(&step, fail_at) || !(adapters = cJSON_CreateArray())) goto cleanup;
    for (uint8_t i = 0; i < snapshot->count; i++)
    {
        const traffic_row_t* row = &snapshot->rows[i];
        if (!json_step(&step, fail_at) || !(item = cJSON_CreateObject())) goto cleanup;
        if (!json_step(&step, fail_at) || !cJSON_AddStringToObject(item, "id", row->id)) goto cleanup;
        if (!json_step(&step, fail_at) || !cJSON_AddStringToObject(item, "name", row->name)) goto cleanup;
        if (!json_step(&step, fail_at) || !cJSON_AddBoolToObject(item, "connected", row->connected)) goto cleanup;
        if (row->has_rate)
        {
            if (!json_step(&step, fail_at) || !cJSON_AddNumberToObject(item, "down", row->down)) goto cleanup;
            if (!json_step(&step, fail_at) || !cJSON_AddNumberToObject(item, "up", row->up)) goto cleanup;
        }
        else
        {
            if (!json_step(&step, fail_at) || !cJSON_AddNullToObject(item, "down")) goto cleanup;
            if (!json_step(&step, fail_at) || !cJSON_AddNullToObject(item, "up")) goto cleanup;
        }
        if (!json_step(&step, fail_at) || !cJSON_AddItemToArray(adapters, item)) goto cleanup;
        item = NULL; /* ownership transferred */
    }
    if (!json_step(&step, fail_at) || !cJSON_AddItemToObject(root, "adapters", adapters)) goto cleanup;
    adapters = NULL; /* ownership transferred */
#ifdef __APPLE__
    if (!json_step(&step, fail_at) || !cJSON_AddBoolToObject(root, "experimental", true)) goto cleanup;
#endif
    if (json_step(&step, fail_at)) json = cJSON_PrintUnformatted(root);

cleanup:
    cJSON_Delete(item);
    cJSON_Delete(adapters);
    cJSON_Delete(root);
    return json;
}

static bool traffic_json_self_test(void)
{
    traffic_snapshot_t sample = {0};
    sample.count = 2;
    sample.timestamp = 1234;
    snprintf(sample.default_id, sizeof(sample.default_id), "test0");
    snprintf(sample.rows[0].id, sizeof(sample.rows[0].id), "test0");
    snprintf(sample.rows[0].name, sizeof(sample.rows[0].name), "synthetic");
    sample.rows[0].connected = true;
    sample.rows[0].has_rate = true;
    sample.rows[0].down = 12;
    sample.rows[0].up = 34;
    snprintf(sample.rows[1].id, sizeof(sample.rows[1].id), "test1");

    char* json = render_snapshot(&sample, 0);
    if (json == NULL) return false;
    cJSON* root = cJSON_Parse(json);
    cJSON_free(json);
    const cJSON* rows = cJSON_GetObjectItemCaseSensitive(root, "adapters");
    const cJSON* first = cJSON_GetArrayItem(rows, 0);
    const cJSON* second = cJSON_GetArrayItem(rows, 1);
    const cJSON* down = cJSON_GetObjectItemCaseSensitive(first, "down");
    const bool ok = cJSON_IsArray(rows) && cJSON_GetArraySize(rows) == 2 &&
        cJSON_IsNumber(down) && down->valuedouble == 12 &&
        cJSON_IsNull(cJSON_GetObjectItemCaseSensitive(second, "down")) &&
        cJSON_IsNull(cJSON_GetObjectItemCaseSensitive(second, "up"));
    cJSON_Delete(root);
    if (!ok) return false;

    /* Every construction/attachment/render gate must fail without
     * returning partial JSON, including both row variants. */
#ifdef __APPLE__
    const unsigned last_step = 21;
#else
    const unsigned last_step = 20;
#endif
    for (unsigned step = 1; step <= last_step; ++step)
    {
        json = render_snapshot(&sample, step);
        if (json != NULL) { cJSON_free(json); return false; }
    }
    sample.count = 0;
    json = render_snapshot(&sample, 0);
    if (json != NULL) { cJSON_free(json); return false; }
    return true;
}

char* net_traffic_json(void)
{
    if (!atomic_load(&s_running)) return NULL;
    traffic_snapshot_t snapshot;
    traffic_lock();
    if (!atomic_load(&s_running) || s_row_count == 0 || s_row_count > NET_TRAFFIC_MAX)
    {
        traffic_unlock();
        return NULL;
    }
    snapshot.count = s_row_count;
    snapshot.timestamp = s_timestamp_ms;
    memcpy(snapshot.default_id, s_default_id, sizeof(snapshot.default_id));
    memcpy(snapshot.rows, s_rows, sizeof(traffic_row_t) * snapshot.count);
    traffic_unlock();

    return render_snapshot(&snapshot, 0);
}
