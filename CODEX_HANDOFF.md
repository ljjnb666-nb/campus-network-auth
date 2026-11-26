# Codex 最终集中交接任务书

- 项目：`ljjnb666-nb/campus-network-auth`
- 冻结基线：`716ab3193bf26c2d2ad34fad5e07e8220be4b121`
- 分支：`fix/v1.2.0-stability-security`
- 冻结源码基线的验收：CI #443，4/4 jobs 成功；后续文档提交及新源码提交须分别核查自身 CI
- PR #1：Draft；main 不得擅自修改
- 目标：完成本地 Windows/独立环境可做的最后验证；不得为了追求发布而降低门禁或改变认证、访问控制和服务管理语义。

## 0. 开始前必须确认

1. Fetch 最新远端分支与 main，确认工作树干净，核对冻结基线及 PR 状态。
2. 阅读 `README.md`、`SECURITY.md`、`ARCHITECTURE.md`、`FINAL_AUDIT.md`、`RELEASE_CHECKLIST.md`、`THIRD_PARTY_NOTICES.md`。
3. 不从 Phase 0 重做已通过范围；不为无变化状态重复测试。独立环境验证、工具链迁移或源码变化后的最终验收仍须按第 1、5 节执行完整既有门禁。
4. 不运行真实认证、不修改学校网络访问控制、不强制结束官方客户端或第三方进程，不自动修改服务启动类型。
5. 不合并 PR、不推送 main、不打 tag、不发布。所有变更留在开发分支并附实际 CI 证据。

## 1. 独立 Windows 构建与 Roslyn 正式迁移评估

**目标：** 评估是否能安全替换默认 Framework csc，而不是直接切换。

- 独立记录 Visual Studio/Roslyn 安装来源、版本、Authenticode 签名主体、签名状态、csc.exe 及全部 CodeAnalysis 依赖 DLL SHA-256。
- 当前 runner 观察到的 Roslyn 版本为 5.1200.26.50202；观察值不是可信来源锁，不得直接照抄为独立来源证明。
- 保持 .NET Framework 4.x 目标引用、C# 语言版本、WinForms 资源、manifest、图标、平台和嵌入资源行为。
- 在隔离输出目录做两次完整 GUI 构建，比较原始字节；禁止对成品 EXE 改时间戳、节区或其他字段制造一致。
- 分别运行现有 11 项离线入口，并保存完整 stdout/stderr、退出码、候选 EXE/配置摘要。
- 仅当独立来源、依赖闭包、双构建和全套回归都通过，才提出默认工具链迁移 PR；不自动下载不明编译器、不加静默回退。

## 2. 静态链接组成与许可证核验

**目标：** 建立“实际进入最终 EXE 的组件/归档成员 → 版本/来源 → 许可/NOTICE → 分发义务”可审计链。

- 在不改功能的构建中保存 GNU linker map（建议启用 map 与 cross-reference 输出）、链接命令、静态库归档清单及对象成员。
- 核实实际进入 Windows x64 原生核心的 libcurl、libgcc、libstdc++、libwinpthread、MinGW-w64 CRT/headers 等成员；区分静态链接、动态导入、仅安装但未链接的包。
- 对每个实际链接成员保存包名/版本、上游 URL/tag/commit、归档原始字节 SHA-256、可信签名验证结果、许可与 NOTICE 原文。
- 重点复核 GCC Runtime Library Exception、MinGW-w64、winpthreads 及任何静态链接例外条款；不能因 Licenses.txt 当前缺某段文本就直接断言违法，也不能默认无需补充。
- 不得将 PE 直接导入表或源码 SBOM 冒充完整静态/动态组成证明。更新 `THIRD_PARTY_NOTICES.md` 与 `Licenses.txt` 时保留来源和具体文件，不复制无关许可证。

## 3. cJSON 公告适用性最终证据

- 版本锁：cJSON v1.7.19；本地 cJSON.c blob `021b5c1fa7fe4871c03caf2568ce400f5ab7d01a`，cJSON.h 与上游一致。
- 7 处本地差异见 `THIRD_PARTY_NOTICES.md`。先逐处复核语义和整数边界，不覆盖本地修订。
- 重点补充 CVE-2026-16554 32 位打印长度边界的独立测试。若产品只支持 Windows x64，明确目标范围；若声称支持 32 位，必须提供 32 位编译与测试证据。
- 对 cJSON_Utils 相关公告，保存最终链接 map/归档成员及全量调用检索证据；源码树未见 cJSON_Utils.c 不自动等于二进制不可能包含相关实现。
- 生成逐条公告适用性记录：affected / not_affected / fixed / under_investigation，逐项附版本、配置、调用路径和证据；没有证据就保留 under_investigation，不编造 VEX 结论。

## 4. CS4014 与异步生命周期只读审查

- 定位 Program.cs 中系统唤醒分支的 `RunPreConnectCheckAsync(false)` 未等待调用。
- 先判断未等待调用的异常观察、任务代次及窗口释放风险，记录证据与可行的本地生命周期测试；本交接不授权修改认证、网络接入或第三方服务行为。
- 不得简单添加 await 导致系统事件线程阻塞；不得改变连接重试策略、真实网络请求、凭据或服务切换语义。
- 保留所有已有 256 状态组合、100 轮生命周期、UI 与 11 项候选离线回归。新增测试需针对实际风险，不堆砌重复测试。

## 5. 最终 RC 回归与归档

- 对最终提交运行完整 Windows/Linux/macOS/SBOM CI。
- 独立下载最终 artifact，重算 ZIP SHA-256、CRC、文件名/条目、安全路径、build-evidence 索引、build-manifest 产物摘要。
- 将 artifact、commit、tree、workflow run、每个 job 和日志成功标记绑定记录。
- 产物仍无 Authenticode 时明确标记 unsigned；不能将内部归档核验写成发布批准。

## 6. 需用户/权利人及获准设备的事项

这些不是 Codex 可以自行替用户批准的事项：
- 桌面源码权利人/著作权人确认及对外分发许可。
- 代码签名证书购买/持有与签名授权。
- 学校允许环境中的真实连接、官方客户端恢复、UAC/服务切换。
- 100%–200% 真实 DPI、多显示器、睡眠唤醒、网卡热插拔、升级/卸载。
- 24/72 小时长跑和实际资源趋势。

## 7. 最终交付要求

Codex 最后只提交：源码差异、独立构建原始日志、工具链与归档签名证据、链接 map/归档成员、许可证对照表、cJSON 公告适用性记录、CS4014 处理说明、最终 CI 与 artifact 摘要、未关闭风险清单。

验收人不得将“代码已改”“CI 通过”“PR 已合并”“主干集成”“真实功能已验证”混为一谈。任何未完成项目保留 BLOCKED/DEFERRED，不得用模拟数据冒充实测。
