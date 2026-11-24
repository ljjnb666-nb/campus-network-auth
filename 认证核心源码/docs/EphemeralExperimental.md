# 临时认证实验模式

这是未经真实认证或安全审计的实验代码，不可用于生产账号。

Windows 桌面构建接受 `--ephemeral-auth`；必须通过受限的匿名管道向标准输入传入一行 UTF-8 JSON，字段为 `username`、`password` 和整数 `channel`。该模式仅检查输入句柄是管道，无法确认它是否私有；启动器负责保护管道句柄。不要在命令行参数或普通文件中放口令。

启动器保持写端打开，发送第二行 `STOP` 或关闭写端会请求退出。`EPHEMERAL_STOP_ACCEPTED` 只表示请求已接收，不证明服务器已登出；请求可能受网络超时延迟。

状态令牌一览：`EPHEMERAL_WAIT_NETWORK`（尚未接入需要认证的校园网）；`EPHEMERAL_ONLINE`（检测到已能上互联网——通常是先前会话仍在有效期，此时不发认证请求，持续监测，会话失效后自动转入认证）；`EPHEMERAL_AUTH_OK` / `EPHEMERAL_AUTH_FAILED`（本次认证结果）；`EPHEMERAL_STOP_ACCEPTED` / `EPHEMERAL_STOP_CHANNEL_FAILED`（停止流程进展）。状态令牌只在状态变化时发送。

## 结构化事件（协议 v1，V1.2.0 起并行输出）

在上述旧令牌之外，核心以同一 stderr 通道并行输出一行结构化事件，供启动器归档诊断与前向兼容：

```
EPHEMERAL_EVENT {"v":1,"pid":<进程号>,"event":"<事件名>","code":<整数>,"error":"unknown"}
```

- 事件名：`wait_network` / `online` / `auth_ok` / `auth_failed` / `stop_accepted` / `stop_channel_failed` / `exit`。
- `code`：`auth_failed` 时为 `dialer_app` 返回值（粗粒度：0 成功 / 1 失败 / 2 超时重试）；`exit` 时为进程退出码（非 0 = 异常退出）；其余事件为 0。
- `error`：错误分类。上游认证协议无法可靠区分凭据错误/账号限制/服务器异常/超时，按审计规则一律如实填 `unknown`，不得编造分类。
- 旧令牌与结构化事件同时输出、语义一致；启动器可只消费其中一种。新增字段只追加，`v` 升级前不改变既有字段含义。


模式不启动 Web 或认证控制监听器，不写配置、日志及登出会话文件。运行中账号与会话数据仍在内存中，操作系统换页文件、崩溃转储和其他有权限的进程仍可能访问它们。非正常退出后不能从磁盘恢复登出现场，服务器可能暂时保留会话。该实现不保证对具体学校认证或共享策略有效。

`--ephemeral-check` 仅检查假数据的配置解析及内存重载；`--ephemeral-probe` 在网络探测前短暂运行初始化供检查监听和文件写入。两者均不得使用真实账号。当前测试未覆盖真实登录、心跳和登出。
