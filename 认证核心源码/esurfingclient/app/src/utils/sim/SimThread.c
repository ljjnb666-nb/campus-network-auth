#include "utils/sim/SimThread.h"

#include "utils/log/Logger.h"
#include <stdlib.h>

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

int sim_thread_join(sim_thread_t* thread, int* exit_code)
{
    if (!thread || !thread->handle || thread->id == GetCurrentThreadId()) return -1;
    if (WaitForSingleObject(thread->handle, INFINITE) != WAIT_OBJECT_0)
    {
        return -1;
    }
    DWORD code = 0;
    if (exit_code && !GetExitCodeThread(thread->handle, &code)) return -1;
    if (!CloseHandle(thread->handle)) return -1;
    if (exit_code) *exit_code = (int)code;
    thread->handle = NULL;
    return 0;
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
