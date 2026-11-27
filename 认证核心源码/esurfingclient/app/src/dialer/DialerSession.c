#include "cipher/CipherUtils.h"
#include "DialerInternal.h"

#include "net/NetClient.h"

#include "cipher/CipherInterface.h"

#include "core/States.h"

#include "dialer/ErrorCode.h"


#include "utils/platform/PlatformUtils.h"
#include "utils/log/Logger.h"

#include <string.h>

static void clean_session(void)
{
    LOG_DEBUG("清除会话初始化状态");
    destroy_cipher_factory();
    g_prog_status[tl_thread_idx].runtime_status.is_initialized = 0;
}

bool init_session(void)
{
    LOG_DEBUG("init_session 函数入口检查, 使用配置: %" PRIu8 ", 下标: %" PRIu8, g_prog_status[tl_thread_idx].cfg.idx, tl_thread_idx);

    /**
     * 向 ticket_url POST 获取 ZSM.
     * iOS/macOS 没有本地模块时 Algo-ID 为零 UUID, 首次用空 POST.
     * Android/Linux 仍 POST 全 0 UUID.
     */
    const char* ticket_body = g_prog_status[tl_thread_idx].auth_cfg.algo_id;
    if (g_prog_status[tl_thread_idx].cfg.chn == 4 || g_prog_status[tl_thread_idx].cfg.chn == 5)
    {
        ticket_body = "";
        LOG_DEBUG("iOS/macOS 通道首次拉取 ZSM 使用空 POST");
    }

    // 未知 Algo-ID 的重取必须有上限: 异常服务器反复下发不可用 ZSM 时不能无限循环
    unsigned attempts = 0;

    for (;;)
    {
        if (stop_requested() || g_need_exit)
        {
            LOG_DEBUG("初始化会话收到退出请求, 停止重取");
            return false;
        }

        if (++attempts > 10)
        {
            LOG_ERROR("初始化会话重取超过 10 次, 放弃并按失败处理");
            return false;
        }

        curl_resp_t resp = post(g_prog_status[tl_thread_idx].auth_cfg.ticket_url, ticket_body);
        if (resp.http_code != HTTP_OK || resp.body_size == 0 || resp.body_data == NULL)
        {
            LOG_ERROR("初始化会话失败");
            error_code_record(&resp);
            curl_resp_free(&resp);
            return false;
        }
        LOG_DEBUG("会话响应长度: %zu", resp.body_size);

        const bytes_t zsm = {
            .data = (uint8_t*)resp.body_data,
            .len = resp.body_size
        };

        LOG_DEBUG("开始初始化会话");
        if (load_cipher(zsm) == false)
        {
            /* 黑名单/未知 Algo-ID: 重置全部 POST 身份字段后立即重取 */
            refresh_states();
            LOG_DEBUG("ZSM 中的 Algo-ID 不可用, 已重置 POST 数据, 立即重新获取");
            curl_resp_free(&resp);
            continue;
        }

        curl_resp_free(&resp);
        LOG_DEBUG("初始化会话成功");
        g_prog_status[tl_thread_idx].runtime_status.is_initialized = 1;
        return true;
    }
}

/* Session-owned state only. Never load and restore external request flags:
 * a scheduler request arriving during logout must survive cleanup. */
static void clear_session_status(runtime_status_t* status)
{
    atomic_store(&status->is_initialized, false);
    atomic_store(&status->is_authed, false);
    status->last_error_kind = 0;
    status->last_error_at = 0;
    memset(status->last_error_code, 0, sizeof(status->last_error_code));
    memset(status->last_sub_error, 0, sizeof(status->last_sub_error));
    memset(status->last_error_msg, 0, sizeof(status->last_error_msg));
    atomic_store(&status->is_running, false);
}

bool session_status_self_test(void)
{
    runtime_status_t status = {0};
    runtime_status_init(&status);
    for (unsigned int flags = 0; flags < 4; ++flags)
    {
        atomic_store(&status.is_initialized, true);
        atomic_store(&status.is_running, true);
        atomic_store(&status.is_authed, true);
        atomic_store(&status.is_need_reauth, (flags & 1) != 0);
        atomic_store(&status.is_time_disabled, (flags & 2) != 0);
        status.last_error_kind = 1;
        status.last_error_at = 123;
        memset(status.last_error_msg, 'x', sizeof(status.last_error_msg));
        memset(status.last_error_code, 'x', sizeof(status.last_error_code));
        memset(status.last_sub_error, 'x', sizeof(status.last_sub_error));
        clear_session_status(&status);
        if (status.is_initialized || status.is_running || status.is_authed ||
            status.is_need_reauth != ((flags & 1) != 0) ||
            status.is_time_disabled != ((flags & 2) != 0) ||
            status.last_error_kind != 0 || status.last_error_at != 0 ||
            status.last_error_msg[0] || status.last_error_code[0] ||
            status.last_sub_error[0]) return false;
    }
    return true;
}

void clean(void)
{
    if (g_prog_status[tl_thread_idx].runtime_status.is_initialized == true) // 如果已经初始化会话, 则进入
    {
        if (g_prog_status[tl_thread_idx].runtime_status.is_authed == true) // 如果已经认证, 则进入
        {
            LOG_INFO("配置 %" PRIu8 " 登出, 下标: %" PRId8,
                g_prog_status[tl_thread_idx].cfg.idx,
                tl_thread_idx);
            term(); // 登出
        }
        clean_session(); // 清理会话
    }
    /* A partially initialized session may also own a cipher. */
    if (g_prog_status[tl_thread_idx].auth_cfg.cipher) destroy_cipher_factory();
    cipher_secure_zero(&g_prog_status[tl_thread_idx].auth_cfg, sizeof(auth_cfg_t));
    clear_session_status(&g_prog_status[tl_thread_idx].runtime_status);
}

void reset(void)
{
    clean(); // 清理数据
    refresh_states(); // 重置指定数据
}

/**
 * @brief 监管进程是否已经不在了
 *
 * 只在被监管的角色下判断: 单实例模式 (OpenWrt 上手工运行) 的父进程是终端或 procd,
 * 它们退出并不意味着本程序该退出。
 * Linux 上子进程登记了 PR_SET_PDEATHSIG, 内核会直接通知, 这里恒为 false
 */
bool supervisor_gone(void)
{
    if (g_prog_role != ROLE_AUTH && g_prog_role != ROLE_WEB) return false;
    return parent_process_alive() == false;
}
