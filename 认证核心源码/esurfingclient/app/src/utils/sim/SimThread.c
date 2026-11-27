#include "utils/sim/SimThread.h"

#include "utils/log/Logger.h"
#include <stdlib.h>
#include <stdbool.h>

typedef struct
{
    sim_thread_func func;
    void* arg;
} thread_start_t;

#ifdef _WIN32

#include <windows.h>

struct SimThread
{
    HANDLE handle;
    DWORD id;
};

static DWORD WINAPI win32_thread_func(LPVOID arg)
{
    thread_start_t* start = arg;
    sim_thread_func func = start->func;
    void* func_arg = start->arg;
    free(start);
    return (DWORD)func(func_arg);
}

sim_thread_t* sim_thread_create(sim_thread_func func, void* arg)
{
    if (func == NULL) return NULL;
    sim_thread_t* thread = malloc(sizeof(*thread));
    if (!thread) return NULL;

    thread_start_t* start = malloc(sizeof(*start));
    if (start == NULL)
    {
        free(thread);
        return NULL;
    }
    start->func = func;
    start->arg = arg;

    thread->handle = CreateThread(NULL, 0, win32_thread_func, start, 0, &thread->id);
    if (!thread->handle)
    {
        free(start);
        free(thread);
        return NULL;
    }
    return thread;
}

/* Only the shutdown owner arms this deadline. Ordinary callers retain
 * the existing blocking-join contract; workers do not inherit the budget. */
static _Thread_local bool s_join_budget_active = false;
static _Thread_local ULONGLONG s_join_deadline = 0;

void sim_thread_shutdown_budget_begin(uint32_t timeout_ms)
{
    const ULONGLONG now = GetTickCount64();
    const ULONGLONG next = now + timeout_ms;
    /* Nested requests cannot extend an already established budget. */
    if (!s_join_budget_active || next < s_join_deadline) s_join_deadline = next;
    s_join_budget_active = true;
}

void sim_thread_shutdown_budget_end(void)
{
    s_join_budget_active = false;
    s_join_deadline = 0;
}

static DWORD join_budget_remaining(void)
{
    if (!s_join_budget_active) return INFINITE;
    const ULONGLONG now = GetTickCount64();
    if (now >= s_join_deadline) return 0;
    const ULONGLONG left = s_join_deadline - now;
    return left >= INFINITE ? INFINITE - 1 : (DWORD)left;
}

static int join_wait(sim_thread_t* thread, int* exit_code, DWORD timeout_ms)
{
    if (!thread || !thread->handle || thread->id == GetCurrentThreadId()) return -1;
    const DWORD result = WaitForSingleObject(thread->handle, timeout_ms);
    if (result == WAIT_TIMEOUT) return 1; // preserve ownership and output
    if (result != WAIT_OBJECT_0) return -1;
    DWORD code = 0;
    if (exit_code && !GetExitCodeThread(thread->handle, &code)) return -1;
    if (!CloseHandle(thread->handle)) return -1;
    if (exit_code) *exit_code = (int)code;
    thread->handle = NULL;
    return 0;
}

int sim_thread_join_timeout(sim_thread_t* thread, int* exit_code, uint32_t timeout_ms)
{
    if (timeout_ms == INFINITE) return -1;
    const DWORD remaining = join_budget_remaining();
    return join_wait(thread, exit_code, timeout_ms < remaining ? timeout_ms : remaining);
}

int sim_thread_join(sim_thread_t* thread, int* exit_code)
{
    return join_wait(thread, exit_code, join_budget_remaining());
}

/* Event-gated workers cannot complete before the test releases them.
 * No sleep-based assumption about scheduling, no network or credentials. */
static int budget_test_worker(void* arg)
{
    return WaitForSingleObject((HANDLE)arg, INFINITE) == WAIT_OBJECT_0 ? 37 : -1;
}

bool sim_thread_budget_self_test(void)
{
    HANDLE gate = CreateEventW(NULL, TRUE, FALSE, NULL);
    if (gate == NULL) return false;
    sim_thread_t* one = sim_thread_create(budget_test_worker, gate);
    sim_thread_t* two = sim_thread_create(budget_test_worker, gate);
    int rc1 = -77, rc2 = -88;
    bool ok = one != NULL && two != NULL;
    if (ok)
    {
        ok = sim_thread_join_timeout(one, &rc1, 10) == 1 && rc1 == -77 &&
            sim_thread_join_timeout(NULL, NULL, 0) == -1 &&
            sim_thread_join_timeout(one, NULL, INFINITE) == -1;
        sim_thread_shutdown_budget_begin(0);
        sim_thread_shutdown_budget_begin(5000);
        ok = ok && join_budget_remaining() == 0 &&
            sim_thread_join(one, &rc1) == 1 && rc1 == -77 &&
            sim_thread_join(two, &rc2) == 1 && rc2 == -88;
        sim_thread_shutdown_budget_end();
    }
    if (!SetEvent(gate)) abort();
    if (one != NULL)
    {
        if (sim_thread_join_timeout(one, &rc1, 5000) != 0) abort();
        ok = ok && rc1 == 37 && sim_thread_join_timeout(one, NULL, 0) == -1;
        sim_thread_destroy(one);
    }
    if (two != NULL)
    {
        if (sim_thread_join_timeout(two, &rc2, 5000) != 0) abort();
        ok = ok && rc2 == 37;
        sim_thread_destroy(two);
    }
    CloseHandle(gate);
    return ok;
}

int sim_thread_detach(sim_thread_t* thread)
{
    if (!thread || !thread->handle) return -1;
    if (!CloseHandle(thread->handle)) return -1;
    thread->handle = NULL;
    return 0;
}

uint64_t sim_thread_cur_id(void)
{
    return (uint64_t)GetCurrentThreadId();
}

void sim_thread_destroy(sim_thread_t* thread)
{
    if (thread) {
        if (thread->handle) CloseHandle(thread->handle);
        free(thread);
    }
}
    
#else

#include <pthread.h>
#include <stdlib.h>

struct SimThread
{
    pthread_t thread;
    int consumed;
};

static void* pthread_thread_func(void* arg)
{
    thread_start_t* start = arg;
    sim_thread_func func = start->func;
    void* func_arg = start->arg;
    free(start);
    return (void*)(intptr_t)func(func_arg);
}

sim_thread_t* sim_thread_create(sim_thread_func func, void* arg)
{
    if (func == NULL) return NULL;
    sim_thread_t* thread = malloc(sizeof(*thread));
    if (!thread) return NULL;

    thread_start_t* start = malloc(sizeof(*start));
    if (start == NULL)
    {
        free(thread);
        return NULL;
    }
    start->func = func;
    start->arg = arg;

    thread->consumed = 0;
    if (pthread_create(&thread->thread, NULL, pthread_thread_func, start) != 0)
    {
        free(start);
        free(thread);
        return NULL;
    }
    return thread;
}

int sim_thread_join(sim_thread_t* thread, int* exit_code)
{
    if (!thread) return -1;
    if (thread->consumed || pthread_equal(thread->thread, pthread_self())) return -1;

    void* ret;
    if (pthread_join(thread->thread, &ret) != 0) return -1;
    thread->consumed = 1;
    if (exit_code) *exit_code = (int)(intptr_t)ret;
    return 0;
}

int sim_thread_detach(sim_thread_t* thread)
{
    if (!thread) return -1;
    if (thread->consumed) return -1;

    if (pthread_detach(thread->thread) != 0) return -1;
    thread->consumed = 1;
    return 0;
}

uint64_t sim_thread_cur_id(void)
{
    pthread_t tid = pthread_self();
    return (uint64_t)((uintptr_t)tid);
}

void sim_thread_destroy(sim_thread_t* thread)
{
    if (thread) free(thread);
}

#endif

/* 两个平台共用: 所以写在平台分支之外 */
sim_thread_t* sim_thread_create_retry(sim_thread_func func, void* arg, const int retries)
{
    sim_thread_t* thread = sim_thread_create(func, arg);

    for (int i = 0; thread == NULL && i < retries; i++)
    {
        LOG_ERROR("线程创建失败, 重试第 %d 次 (最多 %d 次)", i + 1, retries);
        thread = sim_thread_create(func, arg);
    }

    return thread;
}
