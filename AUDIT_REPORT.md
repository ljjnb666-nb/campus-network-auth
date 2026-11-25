# 澈知科技校园网连接 — V1.2.0 整改审计报告

- 分支：`fix/v1.2.0-stability-security`
- 审计基线：`d1f59e5`（initial），当前起点：`0607631`（清理 web 界面后的 main）
- 目标版本：V1.2.0（程序集版本已升至 1.2.0.0）
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
| A02 | 仓库缺构建依赖（认证核心.exe、curl 包） | 是 | 高 | .gitignore 排除但脚本依赖 | 依赖自动获取+固定哈希+CI 从源码构建；全新克隆实测通过 | FIXED_VERIFIED |
| A03 | 操作成功误显示"已连接" | 是 | 高 | SetStatus 以颜色映射状态（1386-1406）；SaveCredentials/ForgetCredentials/RunServiceAction 成功也用 ToneOk | P4：领域状态枚举与展示解耦 | FIXED_PENDING_VERIFICATION |
| A04 | 连接前强制保存密码 | 是 | 高 | ConnectClicked:1419 无条件 SaveCredentials(false) | P3：增加"记住密码"选项，认证与保存解耦 | FIXED_PENDING_VERIFICATION |
| A05 | 凭据保存失败阻断认证 | 是 | 高 | 同上耦合；CredentialStore.Save 对损坏旧记录抛异常（Save→Load 校验） | P3：保存失败不阻断临时认证 | FIXED_PENDING_VERIFICATION |
| A06 | 核心异常退出缺恢复策略 | 是 | 中 | OnCoreExit 不区分退出码，无重试边界 | P5：退出码分类+有界自动恢复 | FIXED_PENDING_VERIFICATION |
| A07 | 无根目录 CI | 是 | 中 | 仓库无 .github/workflows | GitHub Actions 全绿（run 45413577809，五项自测全 PASS） | FIXED_VERIFIED |
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

**状态更新（终验后更新）**：A02 → FIXED_VERIFIED（全新克隆实测 + CI 全绿）；A07 → FIXED_VERIFIED（run 45413577809：五项自测全 PASS，含本机受阻的 STOP_PASS 与结构化事件端到端断言）；A10 → FIXED_VERIFIED（CI 实证）；N02 → FIXED_VERIFIED。

### P12 文档、三轮最终审计与验收（本阶段）

**文档**：README.md（项目/构建/使用/卸载/隐私）、CHANGELOG.md（V1.2.0 全量条目）、ARCHITECTURE.md（结构图/设计决策/A16 拆分路线/A25 接口预留——不引入监听服务）、TESTING.md（自测矩阵/CI/真实环境 10 步清单）、RELEASE_CHECKLIST.md（发布门禁/签名 BLOCKED/发布限制）、SECURITY.md、THIRD_PARTY_NOTICES.md（P3）。程序集版本 1.1.0.0 → **1.2.0.0**。
**发布形态变化**：exe + exe.config（PerMonitorV2）两个文件；CI 产物清单同步。

**三轮最终审计记录**：

- **第一轮（功能与代码）**：全量自测 PASS（logic/credentials/core/ui，含状态机不变量、崩溃注入）；发现并修复 2 个整改过程引入的回归——① P4 曾把 AUTH_FAILED 后的受控退出显示为"异常退出"（P5 修复：authFailureSeen 区分）；② 崩溃注入测试初版在 --self-test-ui 模式未释放核心（P10 修复：ExtractCore/CleanupCore 对称化）。原功能对照：托盘/保存/显隐/服务切换/热点入口流程逐项复核代码路径，无功能删除。
- **第二轮（安全与异常）**：凭据——连接不再写盘、DPAPI+ACL+原子替换、明文不落盘测试；并发——N01 修复+volatile 清点；进程残留——清扫+5 次启停实测 0 残留；提权——服务签名门槛（真实二进制实测：中数通/电信信息化部签名）；恶意输入——凭据长度/编码边界校验、结构化事件畸形行截断归档、服务路径引号解析、重解析点拒绝。敏感信息扫描（本地+全新克隆）通过。
- **第三轮（交付与构建）**：**全新 git 克隆（github.com/ljjnb666-nb/campus-network-auth @ b495416）→ 依赖自动下载校验 → Build-Core.ps1 全量构建 → build-manifest.json（提交/工具链/依赖/产物哈希）→ 四项自测全部 PASS**。A02 验收达成。CI 工具链路径问题（setup-msys2 安装位置随 runner 变化导致 5 次失败）以动态解析修复（f24b708），随后验证。

### P8/P9/P10 架构·性能·测试（本阶段）
**P8（A16）**：模块化已完成 5 个独立文件（CredentialStore/ConnectionStates/ServiceIdentity/ServiceJournal/Program+计划中的接口边界见 ARCHITECTURE.md）。Program.cs 完整拆分（含 UI/编排层）标记 DEFERRED_WITH_REASON：文件级搬运在当前工具链下回归风险大于收益（partial 类迁移需要大段文本搬移并重验全部自测），留专门重构窗口执行；A25 多设备管理接口以文档化接口预留（不引入任何监听服务/攻击面）。
**P10（A06 验证）**：核心新增离线崩溃钩子 `--ephemeral-crash`（读管道凭据→发 WAIT_NETWORK→300ms→退出码 3，无网络探测/真实认证）；GUI `CheckAutoRetryForTest()` 注入式验证完整链路：事件→状态机→runSawProgress→退出分类（崩溃）→ScheduleAutoRetry（第 1/2 次、10s 档）→用户停止取消重试。并入 `--self-test-ui`（首轮失败暴露了自测模式未释放核心的问题，已修复：CheckAutoRetryForTest 内 ExtractCore + finally CleanupCore）。
**P9 实测数据（2026-11-24，本机 Win10 x64，.NET FX 4.8 运行时）**：
| 指标 | 实测 | 目标 | 结论 |
|---|---|---|---|
| 冷启动（进程初始化，logic 自测） | 29–42 ms | ≤3 s | ✅ |
| 核心释放+SHA-256+管道集成（core 自测） | 257–268 ms | — | ✅ |
| UI+崩溃注入+自动重试全链路（ui 自测） | 1387–1411 ms | — | ✅ |
| GUI 进程内存（活实例 PID 19768） | 56.3 MB WS / 39.3 MB Private | 双进程 ≤100 MB | ✅（合计 ~65 MB） |
| 核心进程内存（用户会话 15.2h） | 8.7 MB / 131 句柄 / 4 线程 | 无泄漏 | ✅ |
| 核心平均 CPU（15.2h/112.9s CPU 时间） | 0.206% | <1% | ✅ |
| 5 次启停后残留进程 | 0（排除用户会话） | 0 | ✅ |
| GDI 对象计数 | 未采样（需 GetGuiResources API，工具链未接入） | — | 记录为缺口 |
| 72 小时长跑 | 未执行 | — | BLOCKED（需真实环境时间） |
**单实例机制顺带验证**：活实例存在时第二实例 1.2s 内信号退出 ✓。
**状态更新**：A06 → FIXED_VERIFIED（注入测试实跑）；A16 → DEFERRED_WITH_REASON；A24 → 部分 FIXED_PENDING_VERIFICATION（上表实测，长跑项 BLOCKED）；A25 → DEFERRED_WITH_REASON（按要求仅预留）。

## 五、最终验收报告（V1.2.0 候选版）

### 交付与版本
| 项 | 值 |
|---|---|
| 最新提交 | `f24b708`（P12：版本+文档+CI 修复） |
| 分支 | `fix/v1.2.0-stability-security`（与 origin 同步，未动 main，未合并/未发布） |
| 提交数 | 11（1×前期清理 + 10×整改阶段，见上文清单） |
| 变更规模 | 111 个文件，+1,898 / −51,743 行（删除量主体为 Web 界面/OpenWrt 上游遗留） |
| 程序集版本 | 1.1.0.0 → **1.2.0.0** |
| 构建产物 | `D:\所有项目\00_正在开发\xiaoyuanwang\澈知科技.exe` + `澈知科技.exe.config`；`认证核心.exe`（哈希 7AA893C5…，见 build-core\gui-build-manifest.json / build-manifest.json） |
| 构建清单 | build-core\build-manifest.json（gitCommit/工具链/依赖/产物 SHA-256） |

### 25 项问题终态
FIXED_VERIFIED：A03 A04 A05 A06 A09 A11 A13 A14 A15 A18 A19 A20；FIXED_PENDING_VERIFICATION（机制落地、终验待真实环境/CI）：A02 A07 A08* A10 A12 A17 A21 A24；UPSTREAM_LIMITATION：A01；BLOCKED：A08 A23（真实验证/签名证书）；DEFERRED_WITH_REASON：A16 A22 A25。
（*A08 归入 BLOCKED：真实网络验证需用户授权，见 TESTING.md 十步清单。）
新增问题 N01–N06：N01 N02 N03（记录+缓解）N04 N06 均已处置；N05（ASan 未启用）维持记录状态。

### 验证层级（严格区分）
- **CODE_IMPLEMENTED** ✅ 全部整改代码落地并编译零警告。
- **BUILD_VERIFIED** ✅ 本机 + **全新克隆**两环境全量构建成功（含依赖自动获取与哈希校验）。
- **OFFLINE_TESTS_VERIFIED** ✅ 四项自测双环境全 PASS（含状态机不变量/崩溃注入/凭据安全/核心集成）；--self-test-stop 本机因用户活动会话持锁受限（环境性，非缺陷），CI 覆盖。
- **CI_VERIFIED** ✅（76ea70e 起）：CI 全绿。历经两次真实根因修复——① setup-msys2 安装位置随 runner 变化（硬编码 C:\msys64 失效）→ 动态解析（f24b708，该次运行已证明依赖获取+工具链+全量构建成功，仅自测步骤失败）；② pwsh 的 `&` 不等待 GUI 子系统进程（$LASTEXITCODE 未设置误判失败；应用本身 PASS 后正常退出，时间线已核实）→ Start-Process -Wait 显式等待（76ea70e，全绿）。
- **REAL_NETWORK_VERIFIED** ❌ BLOCKED——需用户授权与真实校园网（步骤清单见 TESTING.md）。
- **LONG_RUNNING_VERIFIED** ❌ 未执行（24/72h）；已观测 15.2h 用户会话稳定（8.7MB/0.2% CPU）。
- **RELEASE_READY** ❌ 否——真实环境验证、长跑、DPI 多档实机、签名、桌面源码许可补齐之前不满足发布条件（门禁清单见 RELEASE_CHECKLIST.md）。

### 静态/依赖/性能/UI 汇总
静态安全分析：Mimosa 钩子全程拦截提交（6 处经逐行复核为自测固定参数误报，经用户批准以 --no-verify 提交，如实记录）；敏感信息扫描双环境通过；curl 漏洞核查 8.23.0 剩 42 条披露项（上游未修复，人工复核待续）。性能实测见 P9 表。UI 离线断言通过，多档 DPI 待实机。

### 剩余风险（如实）
明文 HTTP 协议（A01）、libcurl 未修复披露项、EXE 未签名、C# 字符串不可彻底擦除、Kill 后核心内存不擦除、GDI 计数未采样、--self-test-stop 本机环境受限。

### 用户后续步骤
1. 托盘退出旧会话后补跑 `.\澈知科技.exe --self-test-stop`。
2. 按 TESTING.md 十步清单做真实环境验证（含 24/72h）。
3. 决定是否合并 fix 分支 → main / 打 v1.2.0 tag（RELEASE_CHECKLIST.md）。

### P7 UI 与交互优化
**A17 高 DPI**：新增 `app.manifest`（PerMonitorV2 声明 + Win10/11 supportedOS）与 `app.config`（System.Windows.Forms.ApplicationConfigurationSection DpiAwareness=PerMonitorV2）；两构建脚本接线（`/win32manifest:` 嵌入 + `app.config` 随产物复制为 `澈知科技.exe.config`）。.NET FX 4.7+ 生效，旧运行时自动回退系统级缩放；分发形态从单 exe 变为 exe+config 两个文件（已写入文档）。注：多档 DPI 实机渲染验证（100%–200%）待用户环境执行（P11 清单），当前以框架机制落地为准。
**A11 UI 线程阻塞**：`CheckBeforeConnect`（WMI 冷启动可达数秒）全面异步化——启动检查 `RunPreConnectCheckAsync`（先显示"正在检查"，完成后回填结论）、`ConnectClicked` 改 async（检查期间禁用连接按钮防重复点击）；检查期间窗口不再冻结。
**A19 诊断入口**：托盘菜单"导出诊断信息"→ 写出脱敏文本（程序版本/四类领域状态快照/自动恢复计数/非法状态迁移记录/核心事件归档 200 条/服务事务记录/OS 与 DPI），不含任何账号、密码、票据；导出到 %TEMP%\CheZhiDiag。
**其它**：锁屏（SessionSwitch SessionLock）即自动遮罩密码；`AcceptButton=connect`（回车连接，检查/连接中按钮禁用防误触）；AssemblyDescription 由"热点管理"修正为"认证客户端（含移动热点设置入口）"（如实描述能力）。
**验证**：logic/credentials/core/ui 全 PASS；编译零警告。
**状态更新**：A17 → FIXED_PENDING_VERIFICATION（机制落地，多档 DPI 实机验证待用户执行）；A11 → FIXED_VERIFIED（启动/连接路径均异步）；A19 → FIXED_VERIFIED（导出入口落地，脱敏内容明确）。

### P6 Windows 服务管理整改
**A14 身份校验强化**：新增 `ServiceIdentity.cs`——WinVerifyTrust（WINTRUST_ACTION_GENERIC_VERIFY_V2，免吊销检查以适配离线/内网）验证 Authenticode 签名链 + 签名者证书主题包含"电信/telecom"（实测本机官方 ESurfingSvr.exe 由"中数通信息有限公司·电信信息化部"签名、且**不带版本资源**——元数据不可用作身份依据，故以证书主题为准）。`IsExpectedService` 升级为文件名+签名+厂商三重校验，任一不符提权子进程 fail-closed（guard 21）。
**A09 事务记录**：新增 `ServiceJournal.cs`（%LOCALAPPDATA%\CheZhiTech\CampusNetwork\service-journal.v1.json，原子写入，无凭据）。GUI 在提权子进程启动前写 pending（含操作前服务状态快照 CurrentServiceState()），退出后回写 ok/failed:code；程序异常退出则 pending 留存——下次启动 `PromptPendingServiceRecovery()` 识别：prepare 未完成→提示+经授权的恢复选项（用户确认才触发，仍走完整 UAC+防护链）；restore 未完成→提示后清除。
**热点状态保守处理**：`DirectAdaptersIdle` 从"仅 status==2 视为忙"改为白名单放行（0 断开/7 媒体断开/4/5/6 硬件态才空闲，**null=未知一律视为忙**）——状态未知时禁止服务切换（保守方向）。
**已具备项复核**：WMI 5s 超时+异常回退 ✓、UAC 1223 拒绝恢复提示 ✓、未安装场景 ✓、切换后 WaitForStatus 状态复核（提权子进程内）✓、重复检测/执行竞争（子进程内二次校验）✓。
**验证**：logic/credentials/core/ui 全 PASS；编译零警告（CS4014 fire-and-forget 以 pragma 显式标注）。
**状态更新**：A14 → FIXED_VERIFIED（真实服务二进制实测通过校验逻辑）；A09 → FIXED_VERIFIED（事务记录+重启识别+经授权恢复）；A25 之外的 P6 需求全部落实。

### P5 稳定性与异常恢复
**N01 修复（C11 原子）**：`Shutdown.c` 的 `static volatile bool shutting_down` check-then-set 竞态改为 `atomic_exchange`（stdatomic.h）——首个触发的线程独占关闭流程，其余直接返回。
**A18 修复**：`CleanupStaleCoreDirs()` 在 GUI 启动（ExtractCore 后）与退出（CleanupCore）时清扫 `%TEMP%\Chezhi-*` 历史残留（单实例互斥保证非当前目录均为残留；被运行中核心占用的目录因文件锁删除失败而安全跳过）。
**A06 有界自动恢复**：
- 触发条件（全部满足）：核心异常退出（退出码非 0）且非用户停止/退出、非受控认证失败（authFailureSeen）、本轮曾到达"等待网络/认证成功/已联网"（runSawProgress——未见过网络即失败=疑似凭据错误，不自动重试，避免向服务器反复提交错误密码）。
- 退避 10s→40s，每轮用户发起的连接最多 2 次；用户停止/确认退出/窗口关闭即刻取消（userStopped 不变量）；重试前检查无并发核心；RetryTimerTick 全条件守卫。
- 唤醒（PowerModeChanged Resume）：不自动重启（核心自身心跳/退避循环重新确认；用户停止过的保持停止），仅状态提示。SystemEvents 订阅随窗口销毁注销。
- 修复 P4 引入的文案回归：AUTH_FAILED 后核心退出不再显示"异常退出"（受控失败与崩溃区分）；强杀（userStopped）也不算崩溃。
**8.4 并发审计（记录）**：
- volatile 用途清点：安全清除模式（Config.c/States.c 的凭据擦除）✓ 正确；`volatile sig_atomic_t`（信号标志 g_stop_requested/g_shutdown_done/g_console_closed）✓ 符合标准；ControlServer/CheckNetwork/NetTraffic/Watchdog 的 `static volatile bool` 为单写者简单标志（无 check-then-set 复合更新）——目标平台事实安全，形式上应为 C11 atomic，记为低风险残余（改进涉及 4 文件多线程路径，独立验证成本高，本轮不做）。
- ASan/UBSan：MinGW-w64 静态构建下未启用（与静态 curl/strip 组合稳定性未验证），记为 BLOCKED_WITH_REASON；CI 编译 -Wall -Wextra 零警告作为最低门槛建立。
- 100 秒停止预算评估：保留。依据：核心登出走 HTTP（op_timeout 15s×重试）+ 控制通道/线程收尾，100s 高于最坏路径；无真实环境数据前不做推测性调整。
**验证**：logic（含状态机不变量）/credentials/core/ui 全 PASS；新核心 1B3278A0… 已同步 CoreHash。
**状态更新**：N01 → FIXED_VERIFIED；A18 → FIXED_VERIFIED（残留清扫+文件锁安全）；A06 → FIXED_PENDING_VERIFICATION（逻辑与守卫完成，崩溃注入测试在 P10 补充）；A15 → FIXED_VERIFIED（退出码分类 + 受控失败区分）。

### P4 状态机与结构化事件
**A03/A13 根因修复**：新增 `ConnectionStates.cs`（领域状态模型：NetworkState/AuthState/CoreState/ServiceState/OpResult 五套枚举 + 迁移表校验 + pill 展示映射）。`SetStatus` 退化为纯展示层（文案/指示点/卡片配色）；顶部连接状态 pill 的唯一驱动来源改为 `UpdateConnectionPill()`（领域状态快照）——保存账号/删除账号/服务切换成功不再被显示为"已连接"。所有连接状态变化必经 `SetAuthState()`（非法迁移拒绝并记录）。
**7.2 不变量**：`StateMachineInvariants()` 并入 `--self-test-logic`（实跑断言）：正常链路/失败链路/非法迁移拒绝（Idle 不能跳到已认证；已认证不经停止不能回等待网络）；操作结果（保存/删除/服务切换）不改变连接状态；拒绝记录可查。
**7.3 结构化事件（协议 v1）**：核心侧 `ephemeral_emit_event()`（DialerAuth.c 定义、States.h 声明）在 7 个站点与旧令牌并行输出 `EPHEMERAL_EVENT {"v":1,"pid":...,"event":...,"code":...,"error":"unknown"}`；auth_failed 携带 dialer_app 返回码、exit 携带进程退出码；上游协议无法可靠分类失败原因，error 如实为 unknown（不编造）。GUI 侧管道过滤器转发结构化行 → `ArchiveCoreEvent`（截断 512 字符、环形 200 条，畸形行防御）。`CoreTest --self-test-stop` 新增端到端断言：probe 模式必须收到 EPHEMERAL_EVENT 行（本地被活进程锁阻塞，CI 覆盖）。
**OnCoreExit 增强**：按退出码区分 Exited/Crashed 并入 CoreState（A15 的基础）。
**验证**：logic（含新不变量）/credentials/core/ui 全 PASS；新核心 9644A4B2… 已同步 CoreHash；协议文档 EphemeralExperimental.md 增补 v1 章节。
**状态更新**：A03 → FIXED_VERIFIED（不变量测试 + 迁移校验实跑）；A13 → FIXED_VERIFIED（NetworkState 与 AuthState 分离，"已联网·会话"与"已认证"明确区分）；A10 → FIXED_PENDING_VERIFICATION（结构化协议落地，错误分类=unknown 如实，终态区分待 CI stop 测试验证）；A15 → FIXED_PENDING_VERIFICATION（退出码已区分崩溃/正常）。

### P3 安全整改
**A04/A05 已修复并验证（96301cc）**：连接前不再强制保存凭据；新增「记住账号密码」复选框（默认不勾选，已有记录自动勾选）；勾选且认证成功（AUTH_OK/ONLINE）后才写入磁盘；保存失败仅弹窗提示不阻断已成功的连接；损坏记录拒绝覆盖（沿用 CredentialStore 原有防御）并给出处理指引。全部离线自测通过。

**A21 依赖安全（本次提交）**：
- `scripts/Test-CurlVulns.ps1` 重写为 OSV 区间精确判定（introduced≤V<fixed + versions + last_affected 兜底，并报告"提及但不受影响"的条目）。
- 官方漏洞库精确核查：**8.20.0 受影响 84 条（含 High：CURL-CVE-2026-92392 HTTP/2 UAF）→ 升级 8.23.0 后 42 条（绝大多数 Low，均为截至最新版尚无修复版本的披露项）**。本项目使用面（纯 HTTP/Schannel/静态链接/无 HTTP2、WS、SSH、FTP、代理）相关子集已列明需人工复核，残余风险记录。
- 升级流程：下载官方包（SHA-256 E3DE77CFA6367083C776A26647FC9F633B538C06C3BAA73D931A252AC8F72B49）→ 全量重建（新核心 7B043843…）→ CoreHash 同步 → 全部离线自测通过后采纳。三元组同步更新于 Fetch-Curl/Build-Core/CI 缓存键/文档。

**新增文档**：`SECURITY.md`（威胁模型、已实施防护、UPSTREAM_LIMITATION 声明：HTTP 明文/密码可还原/无服务器身份验证、EXE 未签名、C# 字符串不可彻底擦除、Kill 后内存清理缺失等如实记录）；`THIRD_PARTY_NOTICES.md`（SBOM：ESurfingClient-CVersion 基线/libcurl 8.23.0/cJSON 1.7.19/.NET FX 4.x + 漏洞核查记录 + 分发注意）。

**状态更新**：A04 → FIXED_VERIFIED（离线自测+UI 测试通过）；A05 → FIXED_VERIFIED；A21 → FIXED_PENDING_VERIFICATION（精确数据已采集，最新版残余 42 条披露项为上游未修复，人工复核完成前不宣称清零）；A01 → UPSTREAM_LIMITATION（SECURITY.md 已如实记录）。





## 六、第二轮独立审计整改（新增记录，覆盖前文对应状态）

基线 3c42408；本轮保留旧报告作为历史记录，不将其 PASS 自动继承到新代码。

| 项目 | 本轮变更 | 当前状态 |
|---|---|---|
| B01 | WinVerifyTrust GUID 引用及结构体修复，已知发布者精确匹配，签名回归自测 | 待最新 CI 与真实官方文件验证 |
| B02/B07/B08 | 状态迁移、退出事件去重及输出排空、认证与网络证据分离 | 待最新 CI；真实事件序列需实机验证 |
| B03/B04/B05 | 任务快照、停止取消、重试前复查、代次隔离、仅明确网络探测失败重试 | 待最新 CI；交错与真实环境验证仍需补充 |
| B06 | 持久化恢复责任、区分实际改变与幂等、事务测试 | 待最新 CI；UAC/崩溃实机验证待完成 |
| B09 | 有界流读取、结构化白名单解析、诊断仅保存规范事件名 | 待最新 CI；协议完整乱序和模糊测试未穷尽 |
| B10 | UI 与签名测试强制退出码检查，漏洞库获取失败阻断 | 待最新 CI；残余漏洞适用性未关闭 |

CODE_IMPLEMENTED：上述机制已提交，尚不能认定全部 B 项 FIXED_VERIFIED。BUILD_VERIFIED/OFFLINE_TESTS_VERIFIED/CI_VERIFIED 必须针对最终提交重新核验。REAL_NETWORK_VERIFIED/LONG_RUNNING_VERIFIED/RELEASE_READY 仍未通过。未操作真实校园网或 Windows 服务。当前不合并 main、不打 tag。


### 第二轮 CI 兼容性修复

- 运行 45420876262、45421123500：原生 C 核心成功链接，旧版 .NET Framework C# 编译器报 CS1985（catch 中 await），后续测试未执行。
- 修复：UAC 取消时保守记录实际服务状态 unknown，保留已有恢复责任，不在 catch 中 await 或同步执行 WMI。失败路径恢复服务状态为 Unknown。
- 新提交必须重新运行完整 CI；前两次成功的 CI 不替代最终提交验收。


### 签名 ABI 回归修复

- Run 45422759183（4ce8ba3）：全量构建通过；logic/credentials/core/stop 均 PASS，强制 UI 自测退出码 0；新增 Authenticode 自测真实失败，WinVerifyTrust 抛 AccessViolationException（-1073741819）。后续扫描与上传未执行。
- 对照 Windows SDK WINTRUST_DATA 定义（https://learn.microsoft.com/windows/win32/api/wintrust/ns-wintrust-wintrust_data），修正两个 LPVOID 字段由 uint 为 IntPtr。增加 x86/x64 所有字段偏移和结构体大小校验，调用前 fail-closed；logic 自测覆盖布局，签名自测分阶段输出并重复验证可信文件。
- 保留签名强制门禁，不捕获或忽略原生内存访问异常；新提交 CI 待验证。真实官方天翼文件仍需只读实机核验。


### 用户实机 UI 回归修复

用户在 f906561 候选版反馈标题/副标题重叠、设备类型和记住密码文字裁切；前次 UI_TRAY_PASS 仅覆盖功能，不是视觉布局通过。保留既有界面风格与认证逻辑，改用实际文字度量/独立标签/自适应行高及垂直滚动；新增 UI_LAYOUT_PASS 门禁。真实多 DPI 待验证。未签名提示是既知 A23 发布限制，不能通过伪造签名或禁用安全设置解决。


### UI 自适应修复后 CI 回归

- 72c161c 的 Push 45426967443 / PR 45426983317 均全量构建与 logic/credentials/core/stop 成功；新布局门禁报 UI_LAYOUT_OUTSIDE HeaderPanel，后续签名/扫描/产物上传未执行。
- 本次约束所有单行表格的行高为父容器 100%，避免未声明 RowStyle 时首选高度撑出容器。两组卡片标题按实际分栏宽度测量换行；缩小测试真实使用最小窗口宽度，新增按钮文字裁切检查和越界尺寸诊断。
- 根因修复仍需新 CI 复核，不以旧绿色提交替代，不跳过检测。真实高 DPI 仍需实机验证。


### 用户实机反馈：天翼退出与交接状态

4826977 的 Push/PR CI 均通过（45427730917 / 45427748575），用户实机反馈有残留进程、前置阻塞误显示为认证失败、恢复未启动官方 UI。新增独立交接状态、纯决策 HANDOFF_PASS、有限等待、授权后准备并复查、已准备/已恢复幂等处理、可验证客户端启动、区分可见窗口和无窗口同名进程、只读签名失败分类。窗口可见不等于认证成功。原生签名和热点 guard 未放宽，不强杀进程。实际官方程序签名、路径、UAC 和启动仍需实机验证；最新 CI 待完成。


### 交接状态机 CI 复核与路径加固

197dc8c 的 push 45432305190 和 PR 45432322062 均全绿，日志实际 HANDOFF_PASS、LOGIC_PASS、CREDENTIALS_PASS、CORE_PASS、STOP_PASS、UI_LAYOUT_PASS、UI_TRAY_PASS、SIGNATURE_PASS；push 产物 14939776253。新增路径语义解析及 SERVICE_PATH_PASS，歧义路径明确拒绝并提示官方安装修复；现有窗口需要官方签名验证，启动请求与窗口确认分离；启动前置状态也使用交接观测。此后修改待新 CI，不继承旧绿。未真实启动或停止用户电脑的服务，未改变认证协议。


### 主动生命周期审计（基线 ea99365）

Push 45433080634、PR 45433094588 均 success。源码复核发现：C01 停止后失败终态优先级；C02 停止中状态滞后；C03 活动认证按钮；C04 前置输入校验过晚；C05 原生网络耗尽与认证失败混用；C06 探测证据过期；C07 提权子进程超时后无限等待；C08 启动检查/恢复竞争；C09 服务身份检查与操作间 TOCTOU；C10 外部状态刷新；C11 保存与快照一致性；C12 交接测试未覆盖真实异步取消；C13 多显示器 DPI；C14 依赖扫描报告 42 条待评估。此次修正 C01/02/03/04/06/08/12 的部分或全部机制，新增异步取消及输入校验回归；新 CI 尚未确认。C05/07/09/10/11/13/14 及其他实机交错仍 OPEN，不降低安全验证，不宣称整体完成。


### 主动审计第二批（基线 7bd9565）

Push 45434028192、PR 45434052362 success；push 日志 HANDOFF_CANCEL_PASS 等均通过。新增网络故障分类、服务超时后续动作取消及只读退出观察、服务修改前身份环境复查、空闲状态刷新、凭据保存顺序回归。C05/07/09/10/11 已实施机制，最新 CI 及实机验证待完成；C07 无法确认退出/持久化时保守阻塞，C09 外部竞争仍在。C12 扩展、C13 DPI、C14 依赖评估仍开放。不修改 main，不执行真实认证。


### 主动审计第三批（基线 3fdaa06）

Push 45434982617 / PR 45435005570 success，日志 CREDENTIAL_ORDER_PASS 等通过。新发现：事务写盘失败导致内存结果不可重试；超时 helper 退出时 SCM 仍可能切换；重复 auth_ok 覆盖新网络证据；强制结束确认框期间进程对象可能变化。整改事务回滚、保留 helper 直到稳定观测和持久化、迟到事件过滤与强制结束目标绑定。新增 JOURNAL_RETRY_PASS / CORE_LIFECYCLE_PASS / SERVICE_RECOVERY_PASS，测试真实离线子进程及注入观测，不进行真实认证/提权。最新 CI 待核实；C09 外部竞争、C13 DPI、C14 依赖适用性及实际服务场景仍开放。


### 主动审计第四批（基线 b2d4bc0）

Push 45547153523 / PR 45547161298 success，实际 JOURNAL_RETRY_PASS、CORE_LIFECYCLE_PASS、SERVICE_RECOVERY_PASS 及既有自测通过。新增发现：通配符递归清理可能删除非本实例目录；启动早期异常不在统一回收范围；输出读取任务登记未主动释放；迟到退出回调可影响新任务；服务完成后重复观测与持久化证据不一致。整改清理范围、失败启动回收、读取任务排空、退出回调身份及单一服务观察；新增 CLEANUP_SCOPE_PASS/CORE_RESOURCE_PASS 强制自测。新 CI 待验证。短期句柄计数为诊断，不宣称无泄漏。旧残留不自动清理；依赖评估 C14 仍开放（扫描报告 42 条，尚未验证适用性）。未修改 main 或实际校园网环境。


### 依赖扫描根因审计（基线 0887c79）

Push 45547797875 / PR 45547803893 success；CLEANUP_SCOPE_PASS / CORE_RESOURCE_PASS，30 次，句柄 243→326，未证明泄漏或无泄漏。旧 curl 扫描报告 42 条，源码未按 ranges.type 区分 GIT/SEMVER，将 Git 哈希与数字版本混比，且 last_affected 兜底无条件使用。2026-11-25 核对 https://curl.se/docs/vuln-8.23.0.html 和 https://curl.se/docs/vuln-8.23.0.json，官方版本页为 0，JSON []；https://curl.se/docs/CVE-2023-38545.json 与 https://curl.se/docs/CVE-2026-107929.json 均具有 SEMVER 与 GIT 两类范围，已分别在 8.4.0/8.23.0 修复。此次修复语义并新增离线样例、官方双源 ID 一致门禁；尚待新 CI，不能把旧 42 条直接认定为真实漏洞或整体安全。依赖版本和哈希不变，协议明文等独立风险仍开放。


### 主动审计第五批（基线 816a05f）

Push 45550140241 / PR 45550145561 success；CURL_RANGE_TEST_PASS、CURL_FEED_CROSSCHECK_PASS，官方双源 8.23.0 公告数 0。30 次资源测试仍为 243→326。源码发现重定向流主动获取后未统一显式关闭、FormClosed 清理遗漏直接 Dispose。新增读者 using、退出后 stdin 释放、统一 Process 和窗口 Dispose 入口、10 次预热+100 次分段采样及 32 句柄预算、逐轮流关闭断言、5 次直接窗口释放回归。新 CI 待核实，不推定 83 个句柄均为泄漏，不推定无泄漏。真实环境、72h、跨进程服务事务和签名发布仍未验收。


### 主动审计第六批（基线 ce047fd）

Push 45550721734 / PR 45550731789 success；110 次循环，预热后 handles 326→326，threads 18→18，private bytes 27803648→28823552；UI_DISPOSE_PASS，curl 双源公告 0。发现管理员子进程缺乏跨进程互斥、旧事务实例可覆盖新记录、启动时仅 Running 即清除 pending。新增机器范围 helper 互斥、事务独占写锁及快照版本检查、保留遗留 pending，并新增 SERVICE_GATE_PASS / JOURNAL_CONCURRENCY_PASS。新 CI 未验证。遗留 pending 需人工核对，自动解除尚未实现；Global 权限/跨会话实机未验，外部 SCM 竞争仍存在。未修改 main、未执行实际校园网或系统服务操作。


### 主动审计第七批（基线 ebd3825）

Push 45551297375 / PR 45551302877 success；SERVICE_GATE_PASS / JOURNAL_CONCURRENCY_PASS，110 次句柄 326→326。新增 v3 启动代次证据、托盘显式核对中断事务、二次观察、保守记录失败未知及恢复责任；JOURNAL_RESTART_PASS；禁止重试期间重复连接。未自动清除旧 pending 或损坏记录，缺少 WMI 启动证据时拒绝新服务切换。WMI 时间并非不可伪造的 boot ID，真实重启和跨会话仍待验证；未完全统一按钮管理，未修改 main，最新 CI 待验证。


### 主动审计第八批（基线 4432edb）

Push 45551781061 / PR 45551787864 success，JOURNAL_RESTART_PASS，110 次资源测试预热后 326→331，curl 双源 0。集中主要操作按钮及托盘权限，新增纯决策 256 组合 OPERATION_MATRIX_PASS 和 UI_OPERATION_PASS；取消进行中避免重复操作，服务结束不盲目开放按钮。底层操作标志仍保留，未宣称所有状态迁移完全统一。最新 CI 待验证；真实 UAC、校园网、DPI、72h、旧事务人工核对及正式签名仍开放。未修改 main。


### 主动审计第九批（基线 b287c4e）

Push 45552326254 / PR 45552334588 success；OPERATION_MATRIX_PASS cases=256、UI_OPERATION_PASS、110 次资源通过（326→331）、curl 双源 0。发现 core_failed 未锁存，后续网络事件可污染失败状态；网络耗尽等待退出时仍可能展示旧可达性；成功前置检查结束短暂刷新空闲权限。修复终态锁存、立即撤销网络证据、权限刷新时序；新增 APP_LIFECYCLE_PASS 100 次真实离线 probe UI 连接/停止，固定种子五类注入、真实进程退出回调。新 CI 待验证。未真实认证、未实际服务切换，未宣称100次全系统交接，真实 DPI/UAC/校园网、72h、签名、旧事务恢复仍开放，main 未修改。


### 第九批回归修复（74e9f5b）

Push 45552962416 / PR 45552969825 failure，APP_CYCLE_START_FAIL cycle=0/1。原日志无法区分失败条件，不宣称已确定唯一根因。离线 probe 的 sleep_ms(...,true) 依赖生产状态标志，改为显式 probe_ready 与有界 STOP/EOF 等待，超时非零。测试增加握手、真实 STOP ACK、实际排空证据及脱敏细粒度失败输出，不取消任何门禁。新 CI 待验证；未执行真实服务/校园网，main 未修改。


### 生命周期第二轮回归修复（基线 8b8bd74）

两次 CI 45553627347 / 45553634310 failure，详细日志 APP_CYCLE_START_FAIL cycle=1/0：pending=False ready=False missing=True phase=Idle 而 connect=False stop=True prepare=False restore=False recover=False，随后 UI_TEST_ERROR InvalidOperationException。提示异步中间状态/上下文问题，但未证明唯一根因。新独立 STA WinForms 消息循环门禁，等待共享 ConnectCommandAsync，显式 UI 线程检查及回调屏障，前置占用至启动结束、STOP ACK 排序；不减轮次或断言，新 CI 待证实。未操作实际服务/校园网，main 未变。


### 主动审计第十批（基线 aca4036）

Push 45554185087 / PR 45554192196 success；APP_LIFECYCLE_PASS cycles=100 seed=20261125 ready_ack_drain=100，110次资源/256权限/签名/curl双源均通过，artifact 14994274298。发现旧电源回调仅处理 Resume、可能覆盖终态提示，等待中的连接/重试可在唤醒后继续，服务操作后续启动缺乏电源变化失效标志。新增 Suspend/Resume 代次、正向证据失效、取消尚未执行的连接/重试、保留在途 helper 并阻断后续启动、UI事件注入组合测试 POWER_TRANSITION_PASS。新 CI 待验证；软件注入非真实休眠，旧事件接收代次非实际探测时间，真实 UAC/校园网/DPI/72h/签名仍未验收，main 未修改。


### 主动审计第十一批（基线 f41c292）

Push 45554742785 / PR 45554750745 success；POWER_TRANSITION_PASS active_cycles=20 pending_connect=1 pending_service=1 idle=1，100次应用、110次核心（326→326）、256种权限、签名及curl双源通过，artifact 14995331373。发现无主动网卡变化失效通知、TTL在异步查询后检查、正向事件以UI处理时间计时、空闲观测可跨网络变化。新增 Windows NetworkChange 订阅/释放、合并通知及共享证据代次、接收时间TTL、先行过期检查、空闲观测代次校验、停止意图立即撤销证据；NETWORK_CHANGE_PASS 组合注入。网络通知不证明联网、不自动认证、不取消已授权服务操作。新 CI 待验证；真实网络/休眠/DPI/UAC/72h、正式签名仍未验证，main 未修改。


### 第十一批编译兼容性修正（6741d9f）

Push 45555594052 / PR 45555600124 failure，实际日志 CS1984 (await in finally) / CS0104 (TimeoutException ambiguity)，原生构建后 GUI 未完成，全部测试及签名/漏洞扫描/上传跳过。修复新增网络观测测试的 C#5 兼容性：任务门控代替可提前释放的事件句柄，finally 同步释放，外部等待原任务，显式异常类型。保留原全部测试条件，细化超时/旧观测/新观测诊断。新 CI 待验证；不宣称网络测试通过。布局专项仅源码复核，尚未扩展测试；main 未改。


### 主动审计第十二批（基线 1b55b05）

Push 45556011612 / PR 45556017456 success，NETWORK_CHANGE_PASS active_cycles=20 burst_per_cycle=32 expiry_boundaries=40 service_wait=1 stale_idle=1；100次应用/110次核心（326→331）/256种权限/电源/签名回归/curl双源通过，artifact 14995069094。UI布局审计发现固定比例按钮列、工具按钮高度仅按单个按钮测量、字体变化不主动重排、布局测试只测单档放大。新增按度量切换纵排、最长文案度量、字体重排及36种布局矩阵（含重叠和状态胶囊断言）。新CI待验证；非真实多DPI/分辨率，滚动可达及UI长时间压力尚待实施，main未修改。


### 第十二批 UI 回归修正（基线 64f492a）

Push 45556478832 / PR 45556488266 failure，构建、基础离线、110次核心均通过，UI_LAYOUT_COMBO_CLIPPED；后续测试/签名/漏洞核查/上传跳过。原生 ComboBox 未设置 FormattingEnabled，存在 .NET Framework 旧高度兼容计算与实际高度差异；启用修正路径，按实际/首选高度布置、实际高度居中、保留原断言并增加 itemHeight、文字与选择不变量，输出精确尺寸诊断；按钮布局模式使用固定目标间距。未确认唯一根因，最新 CI 待验证。滚动、GDI 压力尚未扩展，main 未修改。


### 第十二批第二次 UI 回归修正（3ea40cd）

Push 45557447103 / PR 45557455447 failure；构建与基础离线通过；两次均 UI_LAYOUT_COMBO_CLIPPED scale=2 size=500×430 variant=0 actual=40 preferred=43 itemHeight=32 textHeight=35 font=19 formatting=True dpi=96。其余UI后续门禁跳过。FormattingEnabled 不能单独修复原生高度差；采用 OwnerDrawFixed 及实际文本度量设置选项高度与下拉高度，CB_GETITEMHEIGHT 验证实际原生列表与选择区，保留原断言、选择不变量及36组合。新增绘制仍待 CI 与实机视觉/键盘验证；滚动和GDI压力未实施，main 未修改。


### 主动审计第十三批（4969476）

Push 45573821415 / PR 45573829051 success，UI_LAYOUT_MATRIX_PASS 36，100次应用/110次核心（326→326）/256种权限/电源/网络/签名/curl双源通过，artifact 15003048447。新增独立实际 WinForms 消息循环 UI压力回归，10预热+100测量，715次滚动可达性、22次托盘隐藏恢复、实际绘制、GDI/USER/进程句柄采样峰值预算32/32/64，UI_SCROLL_PASS/UI_STRESS_PASS 强制验收。新CI待验证；采样有限、非实际72小时、真实多DPI和真实校园网/UAC/正式签名未验证。main 未修改。


### 发布安全复审第一批（6ad2b93）

Push 45574796280 / PR 45574804954 success；UI_SCROLL_PASS checks=715、UI_STRESS_PASS 100测量（GDI 51→51、USER 78→78、句柄345→346），核心326→326、100应用、256权限、36布局、网络/电源、签名回归与curl双源通过，artifact 15003617560。复审发现凭据/事务先长度检查再重开文件、File.Exists吞掉访问错误、核心哈希与启动间释放保护句柄、凭据持久化未拒绝NUL、发布清单及SBOM仍记旧42条误报及通配卸载风险。新增同句柄有界读取、缺失与不可读区分、严格UTF8事务读取、核心校验句柄跨启动、三类强制回归及文档修正。当前不是最终独立全量审计；同用户恶意竞态、旧事务启动时间证据、真实网络/UAC/DPI/72h/正式签名仍开放。新CI待验证，main未修改。


### 发布安全复审第一批回归修正（3806033）

Push 45575904810 / PR 45575919612 failure，实际日志显示新增三项文件/核心安全断言、原有全部功能/UI及签名回归通过，Secret scan 唯一命中 Program.cs:1160 的人工 NUL 测试字符串；后续依赖核查/上传未执行。改为从既有虚构凭据构造非法 NUL 输入，保持测试；扫描报告不再打印匹配值，只列位置和规则；不添加豁免、不改变匹配规则或阻断退出码。现有启发式扫描仍有覆盖限制。新CI待验证，启动代次证据复审尚未实施，main未修改。


### 发布安全复审第二批（57c2868）

Push 45576570837 / PR 45576580976 success，新增 bounded read / integrity handle、UI/应用/资源/签名回归、敏感信息扫描、curl 双源均通过，artifact 15004183600。复审 v3 以 WMI 启动时间单证据判定重启不足，未采用可能表示 BCD 启动项的 BootIdentifier GUID。v4 增加 Windows 内核 BootId（公开 WDK 定义为 OS loader 启动序列，Win10+共享页偏移0x2c4；ReadProcessMemory 安全读取），双证据严格递增、稳定服务、用户确认及二次核验；旧 pending 缺序号保持阻断，非 pending 兼容。新增 JOURNAL_BOOT_SEQUENCE_PASS/BOOT_SEQUENCE_PASS，当前仅同启动跨进程及模拟变化，真实重启/快速启动/休眠和兼容性待现场，不能证明无其他风险。新 CI 待验证，main 未修改。
