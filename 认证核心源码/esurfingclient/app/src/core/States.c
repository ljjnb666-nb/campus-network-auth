#include "control/Control.h"

#include "core/States.h"

#include "utils/text/TextUtils.h"

#include <stdio.h>
#include <stdatomic.h>

#ifdef _WIN32
jmp_buf g_exit_jmp;
#else
#include <stddef.h>
#endif

uint64_t g_start_run_tm = 0;

prog_role_t g_prog_role = ROLE_STANDALONE;

bool g_ephemeral_auth = false;
bool g_ephemeral_probe = false;
char g_ephemeral_input[4096] = {0};
static _Atomic bool s_ephemeral_stop_requested = false;

void request_ephemeral_stop(void)
{
    atomic_store_explicit(&s_ephemeral_stop_requested, true, memory_order_relaxed);
}

bool stop_requested(void)
{
    return g_stop_requested != 0 ||
        atomic_load_explicit(&s_ephemeral_stop_requested, memory_order_relaxed);
}

void clear_ephemeral_secrets(void)
{
    volatile unsigned char* p = (volatile unsigned char*)g_ephemeral_input;
    for (size_t i = 0; i < sizeof(g_ephemeral_input); ++i) p[i] = 0;
    if (g_prog_status != NULL)
    {
        p = (volatile unsigned char*)&g_prog_status[0].cfg;
        for (size_t i = 0; i < sizeof(g_prog_status[0].cfg); ++i) p[i] = 0;
        p = (volatile unsigned char*)&g_prog_status[0].auth_cfg;
        for (size_t i = 0; i < sizeof(g_prog_status[0].auth_cfg); ++i) p[i] = 0;
    }
}

/** 常规模式的退出清理: 把所有已加载账号的配置与认证状态 (含密码/票据) 擦干净 */
void clear_all_account_secrets(void)
{
    if (g_prog_status == NULL) return;

    for (int8_t i = 0; i < g_prog_cnt; i++)
    {
        volatile unsigned char* p = (volatile unsigned char*)&g_prog_status[i].cfg;
        for (size_t k = 0; k < sizeof(g_prog_status[i].cfg); k++) p[k] = 0;
        p = (volatile unsigned char*)&g_prog_status[i].auth_cfg;
        for (size_t k = 0; k < sizeof(g_prog_status[i].auth_cfg); k++) p[k] = 0;
    }
}

uint8_t g_prog_account = 0;

uint16_t g_control_port = CONTROL_DEFAULT_PORT;

uint16_t g_web_port = DEFAULT_WEB_PORT;

bool g_web_external_acc = DEFAULT_WEB_EXTERNAL_ACC;

char g_control_token[CONTROL_TOKEN_LEN] = {0};

int g_main_argc = 0;

char** g_main_argv = NULL;

volatile sig_atomic_t g_stop_requested = 0;

volatile sig_atomic_t g_shutdown_done = 0;

volatile sig_atomic_t g_console_closed = 0;

static char s_boot_id[BOOT_ID_LEN] = "";

const char* boot_id_get(void)
{
    if (s_boot_id[0] != '\0') return s_boot_id;

    uint8_t bytes[8];
    if (get_rand_bytes(bytes, sizeof(bytes)) == false)
    {
        LOG_WARN("熵源不可用, boot id 将失去随机性");
        memset(bytes, 0, sizeof(bytes));
    }
    for (size_t i = 0; i < sizeof(bytes); i++)
    {
        snprintf(s_boot_id + i * 2, 3, "%02x", bytes[i]);
    }
    return s_boot_id;
}

int8_t g_prog_cnt = 0;

_Thread_local int8_t tl_thread_idx = -1;

_Thread_local const char* tl_thread_name = NULL;

prog_status_t* g_prog_status;

char g_school_network_symbol[SCHOOL_NETWORK_SYMBOL] = {0};

/* 门户页面响应头里拿到的校园网标志 (由 NetClientCurl 的 header_cb 填) */
char g_school_id[SCHOOL_ID_LENGTH] = {0};

char g_school_domain[DOMAIN_LENGTH] = {0};

char g_area[AREA_LENGTH] = {0};

const char g_log_file_name[] = "run.log";

char g_cfg_log_dir[LOG_PATH_MAX] = "";

bool g_logger_inited = false;

log_cfg_t g_logger_cfg = {
    .lv = LOG_LEVEL_INFO,
    .log_dir = "",
    .log_file = "",
    .file_handle = NULL,
    .max_lines = 10000,
    .cur_lines = 0,
    .file_dev = 0,
    .file_ino = 0,
    .lines_since_check = 0
};

bool g_console_enabled = true;

bool g_query_mode = false;

bool g_list_only = false;

int g_child_count = 0;

child_t g_children[SUPERVISOR_MAX_CHILDREN];

stop_flag_t g_supervisor_stop = 0;

bool g_thread_keep_alive = false;

bool g_is_webserver_running = false;

bool g_need_exit = false;

bool g_prog_enabled = false;

bool g_need_restart = false;

bool g_cfg_loaded = false;

bool g_need_reload = false;

bool g_waiting_cfg = false;

long g_conn_timeout = DEFAULT_CONN_TIMEOUT;

long g_op_timeout = DEFAULT_OP_TIMEOUT;
