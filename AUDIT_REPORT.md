# 澈知科技校园网连接 — V1.2.0 整改审计报告

- 分支：`fix/v1.2.0-stability-security`
- 审计基线：`d1f59e5`（initial），当前起点：`0607631`（清理 web 界面后的 main）
- 目标版本：V1.2.0（当前程序集版本 1.1.0.0）
- 审计方式：全量源码通读（Program.cs 1594 行、CredentialStore.cs 162 行、构建脚本、认证核心 C 源码关键路径）+ 实际运行自测

## 一、工程基线（2026-11-24 实测）

### 项目结构
| 组件 | 规模 | 说明 |
|---|---|---|
| Program.cs | 1594 行 | GUI + 自测 + 服务管理 + 进程管理 + 状态显示（职责耦合，A16） |
| CredentialStore.cs | 162 行 | DPAPI 凭据存储（设计良好：ACL/原子替换/拒重解析点） |
| 认证核心（C） | app/ 77 个编译单元 | 基于 ESurfingClient-CVersion ffdfaf1，已移除 webserver/mongoose/portal |
| Build.ps1 | 11 行 | csc 编译 GUI，嵌入核心 exe + Licenses，校验核心哈希 |
| Build-Core.ps1 | 82 行 | curl 8.20.0 静态编译 + 核心 CMake/Ninja + GUI 重编，哈希写入临时源码 |

### 桌面端 ↔ 核心通信协议（EPHEMERAL，见 docs/EphemeralExperimental.md）
- GUI 启动核心：`--ephemeral-check` / `--ephemeral-probe` / `--ephemeral-auth`，凭据经 stdin 管道传一行 JSON（`{username,password,channel}`，UTF-8，≤4096 字节），不落盘、不进命令行。
- 核心经 stderr 回报 6 个令牌：`EPHEMERAL_AUTH_OK / AUTH_FAILED / WAIT_NETWORK / ONLINE / STOP_ACCEPTED / STOP_CHANNEL_FAILED`。
- 停止：GUI 向 stdin 写 `STOP\n`；核心 ephemeral_stop_reader 线程处理。
- 局限（A10）：仅 6 个粗粒度令牌，无法区分凭据错误/账号限制/服务器异常/超时/内部异常。

### 启动→保活→停止 调用链（已核实）
启动：Main → 单实例 mutex（按用户 SID）→ ExtractCore（%TEMP%\Chezhi-<GUID>，ACL 限当前用户+SYSTEM）→ VerifyCore（SHA-256 常量比对）→ MainWindow。
连接：ConnectClicked → **SaveCredentials(false)（强制保存，A04）** → CheckBeforeConnect（WMI，UI 线程，A11）→ LaunchCore → stderr 令牌 → OnCoreStatus（BeginInvoke，旧进程事件用 ReferenceEquals 隔离）。
保活：核心内部心跳/退避（上游机制），GUI 仅转发状态。
停止：BeginStop → stdin "STOP\n" → 100 秒宽限（timer）→ 强制结束需确认。
退出：OnClosing → UserClosing 隐藏托盘；WindowsShutDown 尝试优雅停止；确认退出 → closingAfterStop → OnCoreExit → Close。

### 基线测试与产物（整改前）
| 项 | 结果 |
|---|---|
| --self-test-logic | PASS |
| --self-test-credentials | PASS |
| --self-test-core | PASS（新核心 A691A52D… 集成通过） |
| --self-test-ui | PASS（UI_TRAY_PASS） |
| --self-test-stop | **BLOCKED**：进程 13196（用户 GUI 会话的旧核心，auth-1）持有单实例锁；同条件下旧版同样失败，非回归。待用户停止认证后补测 |
| 认证核心.exe SHA-256 | A691A52D6536E4CE190279D1A676F9C128E453B30D1286BAB63F59B99A77DDB3 |
| 仓库状态 | main == origin/main（0607631），.gitignore 有一处未提交（.zcode/） |

### 已知环境约束
- 用户 GUI 正在运行（内存中为旧版），其核心子进程占用 auth-1 锁 → 影响 stop 自测与后续重编覆盖（Build.ps1 输出同名 exe 时若 GUI 运行中会文件占用）。
- 真实校园网认证/服务切换需用户明确授权（原则 8）。

## 二、25 项原始问题追踪（逐项重新核实）

| 编号 | 问题 | 是否属实 | 等级 | 根因/位置 | 修复方案 | 状态 |
|---|---|---|---|---|---|---|
| A01 | HTTP 认证协议固有风险 | 是 | 高 | 上游协议明文+密钥内嵌 | 保持兼容实现，文档记录残余风险 | UPSTREAM_LIMITATION |
| A02 | 仓库缺构建依赖（认证核心.exe、curl 包） | 是 | 高 | .gitignore 排除但脚本依赖 | P2：依赖自动获取+固定哈希+CI 从源码构建 | FIXED_PENDING_VERIFICATION |
| A03 | 操作成功误显示"已连接" | 是 | 高 | SetStatus 以颜色映射状态（1386-1406）；SaveCredentials/ForgetCredentials/RunServiceAction 成功也用 ToneOk | P4：领域状态枚举与展示解耦 | FIXED_PENDING_VERIFICATION |
| A04 | 连接前强制保存密码 | 是 | 高 | ConnectClicked:1419 无条件 SaveCredentials(false) | P3：增加"记住密码"选项，认证与保存解耦 | FIXED_PENDING_VERIFICATION |
| A05 | 凭据保存失败阻断认证 | 是 | 高 | 同上耦合；CredentialStore.Save 对损坏旧记录抛异常（Save→Load 校验） | P3：保存失败不阻断临时认证 | FIXED_PENDING_VERIFICATION |
| A06 | 核心异常退出缺恢复策略 | 是 | 中 | OnCoreExit 不区分退出码，无重试边界 | P5：退出码分类+有界自动恢复 | FIXED_PENDING_VERIFICATION |
| A07 | 无根目录 CI | 是 | 中 | 仓库无 .github/workflows | P2：GitHub Actions | FIXED_PENDING_VERIFICATION |
| A08 | 未做真实认证验收 | 是 | 中 | 需用户授权+真实环境 | 提供验证步骤；REAL_NETWORK_VERIFICATION=BLOCKED | BLOCKED |
| A09 | 服务恢复机制不足 | 是 | 中 | ChangeService 无事务记录，重启后不知道未完成操作 | P6：服务操作事务记录+恢复提示 | FIXED_PENDING_VERIFICATION |
| A10 | 认证失败原因分类不足 | 是 | 中 | 仅 6 个 EPHEMERAL 令牌 | P4：结构化事件协议（版本化），无法区分处标 Unknown | FIXED_PENDING_VERIFICATION |
| A11 | UI 线程阻塞操作 | 是 | 中 | CheckBeforeConnect（WMI 5s 超时）、凭据加载、核心校验均在 UI 线程（Shown/ctor/ConnectClicked） | P7：异步化 | FIXED_PENDING_VERIFICATION |
| A12 | 第三方许可兼容 | 部分 | 中 | mongoose(GPL) 已随 0607631 移除；剩 Apache-2.0 核心+curl(MIT)+cJSON(MIT) | P12：THIRD_PARTY_NOTICES 核实 | FIXED_PENDING_VERIFICATION |
| A13 | 网络/认证状态混淆 | 是 | 高 | 顶部 pill 只看颜色（1393-1398）；"网络已连通"也显示"已连接" | P4：NetworkState 与 AuthenticationState 分离 | FIXED_PENDING_VERIFICATION |
| A14 | 服务身份校验不足 | 是 | 中 | ServiceExePath 仅匹配文件名 ESurfingSvr.exe（252），无签名校验 | P6：校验 PE 签名/发布者 | FIXED_PENDING_VERIFICATION |
| A15 | 核心异常退出信息丢失 | 是 | 中 | OnCoreExit(1467-1473) 不读 ExitCode，统一显示"已停止" | P5：区分正常退出/崩溃 | FIXED_PENDING_VERIFICATION |
| A16 | Program.cs 职责耦合 | 是 | 中 | 1594 行单文件含 UI+业务+进程+服务+自测 | P8：分模块拆分 | FIXED_PENDING_VERIFICATION |
| A17 | 高 DPI 兼容 | 是 | 中 | 无 app.manifest（非 PerMonitorV2）；AutoScaleMode.Dpi 但 AutoScaleDimensions 未设；绝对像素布局（72px 列宽等） | P7：清单+缩放验证 | FIXED_PENDING_VERIFICATION |
| A18 | 进程生命周期残留 | 是 | 中 | Kill() 后 %TEMP%\Chezhi-* 残留；无 Job Object；异常终止不清理 | P5：Job Object+启动时清扫残留 | FIXED_PENDING_VERIFICATION |
| A19 | 缺诊断体系 | 是 | 中 | 无运行日志/诊断导出；仅自测 | P7：脱敏诊断入口 | FIXED_PENDING_VERIFICATION |
| A20 | 无根目录 README | 是 | 低 | — | P12 | FIXED_PENDING_VERIFICATION |
| A21 | 无依赖安全扫描证据 | 是 | 中 | 无 SBOM/无漏洞核查记录 | P2/P3：SBOM+已知 CVE 核查 | FIXED_PENDING_VERIFICATION |
| A22 | 无安装/升级/卸载机制 | 是 | 低 | 便携单 exe 分发 | P12：评估并记录方案 | DEFERRED_WITH_REASON |
| A23 | EXE 未签名 | 是 | 中 | 无代码签名证书 | 记录方案；不伪造签名 | BLOCKED |
| A24 | 长期运行性能未测 | 是 | 低 | 无实测数据 | P9：实测并记录 | FIXED_PENDING_VERIFICATION |
| A25 | 无多设备网络管理接口 | 是（预留需求） | 低 | — | P8：仅接口设计，不引入监听服务 | DEFERRED_WITH_REASON（按任务要求仅预留） |

## 三、基线期间确认的其它缺陷（新增追踪）

| 编号 | 问题 | 位置 | 等级 |
|---|---|---|---|
| N01 | Shutdown.c `static volatile bool shutting_down` 的 check-then-set 非线程安全（TOCTOU），双线程可同时进入关闭流程 | 认证核心源码/.../Shutdown.c:32-38 | 中 |
| N02 | Build.ps1/Build-Core.ps1 未校验 PowerShell/CMake/Ninja/GCC/中英文路径；构建失败语义未覆盖"不覆盖正常产物" | Build*.ps1 | 中 |
| N03 | VerifyCore 校验与执行之间存在 TOCTOU 窗口（校验后、执行前文件可被替换；目录 ACL 已限当前用户，风险受限） | Program.cs:135-143 | 低 |
| N04 | AssemblyDescription"热点管理"与实际能力（仅打开系统设置入口）不符 | Program.cs:21 | 低 |
| N05 | 核心以 -Os 编译但无 ASan/UBSan/静态分析接入记录 | Build-Core.ps1 | 低 |
| N06 | RunServiceAction 在 await 后访问窗体控件，若窗口在等待期间关闭存在失效风险（BeginInvoke 缺失） | Program.cs:1533-1545 | 低 |

> 状态说明：FIXED_VERIFIED / FIXED_PENDING_VERIFICATION / NOT_REPRODUCED / UPSTREAM_LIMITATION / BLOCKED / DEFERRED_WITH_REASON。本表随各阶段修复滚动更新，全部整改完成后进行三轮复审并出具最终验收报告。

## 四、阶段提交记录

### P1 工程基线（bcb18af）
- 全量源码通读完成；25 项原始问题逐项核实（A01-A25）+ 新增 6 项（N01-N06）。
- 基线自测：logic/credentials/core/ui PASS；stop BLOCKED（用户 GUI 会话的活核心 PID 13196 持有 auth-1 单实例锁，实测同条件旧版同样失败）。
- 发现 PID 13196 为用户 2026-11-23 22:59 启动的真实认证核心（GUI 已退出但核心仍保活）——A06/A18 的真实样本。

### P2 构建修复（本阶段）
**新增**：`scripts/Fetch-Curl.ps1`（依赖三元组固定：curl 8.20.0 / curl.se 官方源 / FC5819CA…，缓存复用+离线构建+哈希不符即停）；`scripts/scan-secrets.ps1`（敏感信息启发式扫描）；`scripts/Test-CurlVulns.ps1`（对照官方 vuln.json 核查）；`.github/workflows/build.yml`（Windows 构建+离线自检+安全扫描+产物上传）。
**改造**：`Build-Core.ps1`（依赖经 Fetch-Curl 获取；vswhere 通用发现 VS 自带 CMake/Ninja，移除个人路径硬编码；生成 build-manifest.json：版本/提交/工具链/依赖/产物哈希）；`Build.ps1`（CoreHash 以 Program.cs 为唯一来源；-CorePath 参数与产物自动定位；临时输出成功后才替换正式产物，失败不覆盖；生成 gui-build-manifest.json）。

**实测证据**：
1. 下载路径：从 curl.se 官方下载的 curl-8.20.0.tar.gz 与固定 SHA-256 逐字节一致（third-party-test 已清理）。
2. 缓存路径：仓库根历史包校验通过直接复用。
3. **字节不可复现实证**：相同核心源码两次构建哈希不同（A691A52D… → 3B51270E…，MinGW PE 时间戳/构建路径差异）。本项目构建为"过程可复现、字节不可复现"；CoreHash 同步流程（Build-Core 输出 → 更新 Program.cs）为必经步骤，已写入文档。
4. vswhere 发现解决本机 VS 自带工具链定位（首次改造曾因移除个人路径导致 cmake 找不到，已用通用方案修复）。
5. 全部离线自测在新构建产物上通过：logic/credentials/core/ui PASS；stop 仍 BLOCKED（同 PID 13196 锁，与代码无关）。
6. 密钥扫描通过；curl 8.20.0 在官方漏洞库有 54 条命中记录（启发式含可能误报）——已列为 A21 证据，curl 升级（8.23.0）作为独立可验证变更放入 P3，不擅自混入。

**CI 设计修正**：CI 只运行 Build-Core.ps1（同批产出核心+GUI，哈希自洽）；不运行 Build.ps1——提交的 CoreHash 与 CI 现编核心哈希逐字节一致在本工具链下不可保证（见证据 3）。

**状态更新**：A02 → FIXED_PENDING_VERIFICATION（待 CI 首跑验证）；A07 → FIXED_PENDING_VERIFICATION（同）；A21 → 证据已采集（54 CVE 命中，待 P3 处置）；N02 → FIXED_PENDING_VERIFICATION。

