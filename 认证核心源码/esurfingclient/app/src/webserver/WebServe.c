#include "WebInternal.h"

#include <mongoose/mongoose.h>

#include "core/States.h"

#include "utils/platform/PlatformUtils.h"
#include "utils/text/TextUtils.h"
#include "utils/log/Logger.h"

/**
 * @brief 常数时间比较两个定长口令串 (长度不同直接不等, 不提前短路)
 */
static bool token_equal(const struct mg_str a, const char* b)
{
    const size_t blen = strlen(b);
    if (a.len != blen) return false;

    unsigned char diff = 0;
    for (size_t i = 0; i < blen; i++)
    {
        diff |= (unsigned char)a.buf[i] ^ (unsigned char)b[i];
    }
    return diff == 0;
}

/**
 * @brief 回环模式下的 Host 头校验
 *
 * 校验 Host 是阻断 DNS rebinding 的关键: 恶意域名解析到 127.0.0.1 后,
 * 浏览器会把请求的 Host 带成攻击者域名 —— 不在允许表里就拒掉,
 * 跨源脚本就读不到接口响应。外部访问模式 (0.0.0.0) 下无法限定 Host, 不校验。
 */
static bool host_allowed(struct mg_http_message* hm) // mg_http_get_header 的形参不是 const, 这里保持一致
{
    struct mg_str host = mg_str("");
    const struct mg_str* header = mg_http_get_header(hm, "Host");
    if (header != NULL) host = *header;

    static const char* names[3] = {"127.0.0.1", "localhost", "[::1]"};
    char expect[80];
    for (size_t i = 0; i < sizeof(names) / sizeof(names[0]); i++)
    {
        if (mg_strcmp(host, mg_str(names[i])) == 0) return true; // 不带端口的写法
        snprintf(expect, sizeof(expect), "%s:%u", names[i], (unsigned)g_web_port);
        if (mg_strcmp(host, mg_str(expect)) == 0) return true;
    }
    return false;
}

int str_case_cmp(const char* a, const char* b)
{
#ifdef _WIN32
    return _stricmp(a, b);
#else
    return strcasecmp(a, b);
#endif
}

#ifdef _WIN32
/**
 * @brief 通过文件句柄获取文件大小与修改时间
 * @note FindFirstFile 读取的是目录项, 正在被写入的日志文件 (run.log) 的大小与时间
 *       可能还没有同步到目录项, 这里用句柄再取一次真实值
 * @return 是否获取成功
 */
bool win_stat_file(const char* path, uint64_t* size, uint64_t* mtime)
{
    HANDLE handle = CreateFileA(path, GENERIC_READ,
        FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
        NULL, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (handle == INVALID_HANDLE_VALUE) return false;

    LARGE_INTEGER file_size;
    BY_HANDLE_FILE_INFORMATION info;
    const bool ok = GetFileSizeEx(handle, &file_size) != 0 &&
        GetFileInformationByHandle(handle, &info) != 0;
    CloseHandle(handle);
    if (ok == false) return false;

    if (size != NULL) *size = (uint64_t)file_size.QuadPart;
    if (mtime != NULL)
    {
        const uint64_t file_time = ((uint64_t)info.ftLastWriteTime.dwHighDateTime << 32) |
            (uint64_t)info.ftLastWriteTime.dwLowDateTime;
        *mtime = file_time / 10000000ULL - 11644473600ULL;
    }
    return true;
}
#endif

/**
 * @brief 网页文件所在目录
 *
 * 必须按【程序所在目录】拼绝对路径, 不能写成相对路径 "portal":
 * 那样它会相对于进程的当前工作目录, 而配置文件是按程序目录找的, 两者不一致。
 * 把程序放进 /usr/local/bin 再从别处启动 (教程里就这么建议的) 时,
 * 网页文件会找不到, 打开页面只有 404。
 * @return 目录路径
 */
static const char* web_root_dir(void)
{
    static char dir[PATH_MAX] = "";

    if (dir[0] != '\0') return dir;

    char exec_dir[PATH_MAX];
    if (get_exec_dir(exec_dir) == false)
    {
        LOG_WARN("无法获取程序所在目录, 网页文件将按当前目录下的 portal 查找");
        return "portal";
    }

    snprintf(dir, sizeof(dir), "%s%cportal", safe_str(exec_dir), SEP);
    return dir;
}

void fn(struct mg_connection *c, const int ev, void *ev_data)
{
    // 请求体预上限: 超大 Content-Length 在完整缓冲之前就掐断, 免得先吃满内存
    if (ev == MG_EV_READ && c->recv.len > 256u * 1024u)
    {
        LOG_WARN("请求缓冲超过 256 KiB, 提前断开");
        mg_http_reply(c, 413, HEADER_TEXT, "Payload too large\n");
        c->is_closing = 1;
        return;
    }

    if (ev != MG_EV_HTTP_MSG) return;

    struct mg_http_message* hm = ev_data;

    // 回环模式下先过 Host 校验, 阻断 DNS rebinding 读取接口响应
    if (g_web_external_acc == false && host_allowed(hm) == false)
    {
        LOG_WARN("拒绝 Host 异常的请求: %.*s", (int)hm->uri.len, hm->uri.buf);
        mg_http_reply(c, 403, HEADER_TEXT, "Forbidden: Host not allowed\n");
        return;
    }

    // GET 请求
    if (mg_strcmp(hm->method, mg_str("GET")) == 0)
    {
        // 命中 API 时直接返回, 避免静态文件处理重复发送响应
        if (handle_api_get(c, hm)) return;

            struct mg_http_serve_opts opts = { .root_dir = web_root_dir() };
        mg_http_serve_dir(c, hm, &opts);
        return;
    }

    // POST 请求: 必须带启动时下发的一次性令牌 —— 恶意网页跨站拿不到它
    if (mg_strcmp(hm->method, mg_str("POST")) == 0)
    {
        const struct mg_str* token = mg_http_get_header(hm, "X-Auth-Token");
        if (token == NULL || token_equal(*token, web_token()) == false)
        {
            LOG_WARN("拒绝缺少或错误 X-Auth-Token 的 POST: %.*s", (int)hm->uri.len, hm->uri.buf);
            mg_http_reply(c, 403, HEADER_TEXT, "Forbidden: bad token\n");
            return;
        }
        handle_api_post(c, hm);
        return;
    }

    // 其它方法 (HEAD/PUT/DELETE/OPTIONS...): 必须回包, 否则连接会一直挂着占资源
    mg_http_reply(c, 405, HEADER_TEXT, "Method Not Allowed\n");
}
