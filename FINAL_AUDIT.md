# 五大专项统一验收记录

## 结论及范围

验收基线：d1036da436eed309c790f67849138af389bbba26，CI [#322](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45629516066)，Windows job 139258606018、Linux job 139258607031 均成功。

本记录是基于已读取源码片段、历史整改记录与指定 CI 实际输出的阶段复审，不是对全部源码逐行完成的独立第三方安全认证。不得将历史基线视为后续提交的验收，也不得以离线结果推断真实网络访问、认证或校园网络管理策略的验证结果。

| 专项 | 已验证范围 | 仍未关闭 |
| --- | --- | --- |
| 原生网络内存与资源管理 | NATIVE_NET_RESOURCE_PASS；网速两行快照 36 个实际 cJSON 分配失败位置全部释放、恢复成功；ASan/UBSan/LSan 指定测试通过 | 其他序列化/分配路径全覆盖、长时间资源趋势、实际网卡系统调用和平台异常 |
| 原生并发网络探测 | 既有离线网络资源门禁及合成线程资源测试 | 真实系统调用延迟、在途取消、并发压力和平台差异；不测试未获授权的网络或访问控制 |
| 原生线程退出及看门狗 | 既有离线停止、快照旧代次拒绝、100轮应用生命周期、100轮UI压力 | Windows系统调用阻塞、全局关闭预算与控制台/SCM时限的相容性，强制退出时的资源回收与敏感缓冲清理；POSIX未有完整实机证据 |
| 加密中间缓冲清理 | Linux 指定算法合成向量、优化擦除探针、ASan/UBSan/LSan；Windows 离线门禁 | 第三方内部副本、托管字符串、寄存器、系统换页与崩溃转储、所有异常退出；不声称完全擦除 |
| 构建供应链安全 | 版本锁、仓库声明摘要20项、工具摘要8项、PE直接导入基线、源码SBOM及官方Schema、证据索引；原生核心同runner重复构建一致 | 安装包独立字节/签名核验、全依赖公告覆盖、动态/传递组成、GUI重复构建、独立环境复现、签名来源证明 |

## 已读取的实际日志证据

- TRAFFIC_ALLOCATOR_FAILURE_PASS cases=36
- TRAFFIC_SNAPSHOT_LIFECYCLE_PASS
- NATIVE_NET_RESOURCE_PASS
- APP_LIFECYCLE_PASS cycles=100 seed=20261125 ready_ack_drain=100
- UI_STRESS_PASS warmup=10 measured=100 tray_cycles=22 gdi_warm=51 gdi_final=51 gdi_high=51 user_warm=78 user_final=78 user_high=78 handles_warm=346 handles_final=347 handles_high=347
- CRYPTO_OFFLINE_AUDIT_PASS（优化、sanitizer）
- CRYPTO_OPTIMIZED_WIPE_CODEGEN_PASS
- CYCLONEDX_SCHEMA_VERIFY_PASS
- BUILD_EVIDENCE_INDEX_PASS

重复构建证据（SHA-256 前缀，完整值见 CI）：

| 产物 | 第一次 | 第二次 | 结果 |
| --- | --- | --- | --- |
| 原生核心 | FBCEFE12 | FBCEFE12 | 一致，COFF 时间戳 0/0，节区一致 |
| GUI | 679098B8 | 87A872F2 | 不一致，COFF 时间戳与 .text 节区不同 |
| 配置 | D2454CE8 | D2454CE8 | 一致 |

#314、#318、#322 在各自提交内的两次原生构建都一致；不代表不同提交之间的产物摘要相同，也不代表独立 runner 复现。

## 发布阻断与关闭条件

1. 当前 HEAD 全部离线测试成功，证据索引覆盖该提交；任何新整改须重新验证。
2. GUI 的字节差异有明确原因及受控的修复验证；独立环境重复构建有证据，不将归一化摘要当作原始字节一致。
3. 独立验证第三方归档字节、签名、公告和实际链接/运行时依赖；代码签名与构建来源证明真实可核查。
4. 用户授权、学校允许的环境内进行本地 GUI、退出、官方客户端恢复及长跑验收；未授权时仅限离线合成环境。不得强制结束第三方进程或关闭系统防护。不能把模拟结果写成实机结果。
5. 实机 DPI、升级/卸载和许可缺失等事项依 RELEASE_CHECKLIST.md 逐项关闭。

当前结论：DEFINED_OFFLINE_GATES_VERIFIED（仅指定基线）；FULL_RELEASE_ACCEPTANCE_BLOCKED。


## 专项一集中复审补充：本地网卡枚举与流量资源

历史验收基线 e28d0c4c4bd707548a276db484cc1f3b9db10f26：[CI #338](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45632351246)，Linux 139268618601、Windows 139268619027 均成功。

实际日志 ADAPTER_RESOURCE_AUDIT_PASS growthFailures=6 jsonFailures=22 maxRows=255；TRAFFIC_ALLOCATOR_FAILURE_PASS cases=36；TRAFFIC_SNAPSHOT_LIFECYCLE_PASS；NATIVE_NET_RESOURCE_PASS。合计64个实际分配故障位置，限于定义的合成列表及快照路径。此基线原生核心同runner两次摘要 59BB59E8BEBFC316E7F4197E905717A73FC58F0B4DFBEB494E03215661D0A443 一致，GUI 不一致。

| 范围 | 验收状态 |
| --- | --- |
| 本地列表每次调用独占堆缓冲、255项边界、失败清理 | #338 PASS |
| 网速快照生命周期、JSON失败清理 | #338 PASS |
| Windows本地枚举与流量采样 API 合成故障、内存分配故障 | 770002a / 1b1c71a，22个定义用例，等待最新CI |
| macOS 本地采样 sysctl 缓冲容量、变长记录与地址边界检查 | 49656c2，源码已修改，未取得macOS编译/运行证据 |
| 实际系统API阻塞、真实网卡热插拔、长期资源趋势 | BLOCKED：需授权实机验证 |
| 其余原生网络资源路径全覆盖 | 不以本地列表和采样覆盖代替，仍有未验收范围 |

4MiB 枚举缓冲上限、3次分配尝试和255行上限是工程保护初值，不宣称适合全部拓扑。Windows测试替换API及分配函数，核对返回状态、活跃分配和输出边界；不调用真实网卡查询，不代表Windows系统API自身资源释放证明。未修改认证、网关获取、网络访问控制、服务切换或协议。

专项结论：LOCAL_ADAPTER_OFFLINE_REGRESSION_VERIFIED（#338已覆盖部分）；LATEST_RESOURCE_GATE_PENDING；FULL_NATIVE_NETWORK_RESOURCE_ACCEPTANCE_BLOCKED。最新提交的CI尚未确认前不关闭专项，不合并、不发布。


### 专项一验收更新：CI #348

基线 53c2c8edc6106155b04d941e7e15845e1eaca6d5，[CI #348](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45634777812)：Windows 139277191818、Linux 139277192498 均成功。

实际输出：
- WINDOWS_LOCAL_ADAPTER_RESOURCE_PASS cases=22
- ADAPTER_RESOURCE_AUDIT_PASS growthFailures=6 jsonFailures=22 maxRows=255
- TRAFFIC_ALLOCATOR_FAILURE_PASS cases=36
- TRAFFIC_SNAPSHOT_LIFECYCLE_PASS
- NATIVE_NET_RESOURCE_PASS
- APP_LIFECYCLE_PASS cycles=100；UI_STRESS_PASS measured=100

Windows/Linux 已定义的本地枚举和采样资源离线验收通过，共64个 Linux 分配失败位置及22个 Windows 合成场景，两类计数不得合并声称为86个内存分配失败位置。Windows 独立编译的PATH及诊断修正后门禁通过，不能据此证明原先唯一根因。

f81ba6c / dbc4d3b：补充 macOS 独立合成缓冲测试12例及 clang ASan/UBSan 门禁，覆盖零/超限容量、分配失败、读取失败、返回长度大于容量、截断记录、非法记录长度、非法地址长度及正常恢复。测试不访问实际路由表或网卡；显式关闭 macOS LSan，使用测试分配计数校验释放。新平台门禁尚未验证。

剩余阻断：macOS新门禁、其余原生资源路径未全面审计、真实系统API/热插拔/长期运行需授权环境。结论：WINDOWS_LINUX_LOCAL_RESOURCE_OFFLINE_VERIFIED（#348基线）；FULL_NATIVE_NETWORK_RESOURCE_ACCEPTANCE_BLOCKED。不得宣称全专项完成或正式发布。


### 专项一阶段验收：CI #354 三平台通过

验收对象 b8d3189360845f7e015c16de25ea5f47020d199b，[CI #354](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45635959300)，Windows 139281325475、Linux 139281325845、macOS 139281325997 均成功。

| 验收范围 | 实际日志 | 结论 |
| --- | --- | --- |
| Windows 本地网卡枚举及流量资源 | WINDOWS_LOCAL_ADAPTER_RESOURCE_PASS cases=22 | PASS |
| Linux 网卡列表扩容及 JSON 分配 | ADAPTER_RESOURCE_AUDIT_PASS growthFailures=6 jsonFailures=22 maxRows=255 | PASS |
| Linux 流量快照分配及生命周期 | TRAFFIC_ALLOCATOR_FAILURE_PASS cases=36；TRAFFIC_SNAPSHOT_LIFECYCLE_PASS | PASS |
| macOS 本地采样缓冲 | MACOS_LOCAL_ADAPTER_RESOURCE_PASS cases=12 | PASS |
| Windows 既有原生资源门禁 | NATIVE_NET_RESOURCE_PASS | PASS |
| 应用生命周期与界面压力 | APP_LIFECYCLE_PASS cycles=100；UI_STRESS_PASS measured=100 | PASS |

64个实际分配失败位置（Linux）与34个合成系统/缓冲场景（Windows 22、macOS 12）分别计量。不是98个实际OOM位置。Linux指定范围使用ASan/UBSan/LSan，macOS指定范围使用ASan/UBSan和测试分配计数；Windows合成场景使用API替身、分配计数及输出检查，不宣称三个平台均有相同sanitizer覆盖。

**阶段关闭：LOCAL_ADAPTER_AND_TRAFFIC_OFFLINE_VERIFIED。** 定义的本地网卡枚举、流量快照和合成故障门禁已通过。冻结该范围，除发现可证实的回归外不再重复扩展。后续文档提交不改变历史验收对象，最新HEAD的CI仍需独立确认。

**整个专项仍未完成：FULL_NATIVE_NETWORK_RESOURCE_ACCEPTANCE_BLOCKED。** 未全面审计的其他网络资源路径、真实系统API阻塞、网卡热插拔、长期实机趋势分别保留为独立开放项；不将未审计项一概称为外部阻断。真实环境验证只限用户授权及学校允许范围，禁止借验收改动认证协议或访问控制。项目整体仍有GUI字节复现性、独立供应链证据、签名、实机验收等阻断。不得合并或正式发布。

原生核心两次完整SHA-256均为59BB59E8BEBFC316E7F4197E905717A73FC58F0B4DFBEB494E03215661D0A443；GUI摘要38E7C85C / 739828A0不同；配置一致。UI GDI 51→51、USER 78→78、句柄346→347，属于短期测试数据，不证明长期无泄漏。


### 开发门禁与真实环境验收分离（2026-11-25）

用户要求先跳过必须真实运行的测试，继续离线工程整改。实际校园网运行、客户端交接、UAC、真实网卡热插拔、多DPI、升级/卸载及24/72小时长跑标记为 DEFERRED_REAL_ENV；这些项目不阻断离线开发，但不等于通过，不移除 RELEASE_CHECKLIST.md 的发布阻断。未全面审计的源码与自动化CI不能借此标记为实机延期。

61f0e7d / [CI #358](https://github.com/ljjnb666-nb/campus-network-auth/actions/runs/45643076105)：已取得 Windows 与 Linux 实际成功日志，Windows 原生资源门禁包含只读诊断状态 JSON 10个合成失败步骤，macOS 当前记录仍排队。#354/#356 三平台基线已通过。局部离线范围保持冻结；整个原生网络资源专项仍有未全面审计范围，不将其冒称完成。

转入可独立处理的供应链/发布安全子阶段：
- e14de26：敏感信息扫描不再静默略过读取失败；允许名单从子串改为精确字面量（PowerShell默认不区分大小写比较），失败报告仅文件、行号、规则。独立四项回归：精确允许、子串拒绝、命中值不输出、合成读取失败。
- edb7da1：CI 强制执行上述离线回归及正式扫描。新提交尚未取得CI证据；现有规则为启发式，文件排除、重解析点、Git历史及全类型凭据覆盖仍未关闭；若出现真实误报按具体来源复审，不放宽规则以求绿。
- 本轮独立读取 CERT Polska CVE-2026-16554 与 Debian Security Tracker CVE-2026-67215/67216，确认公告编号及风险类别；此前记录不能代替此次原始来源核验。当前本地修订适用性、公告穷尽性、32位及实际调用路径仍需复核，不宣称漏洞全部已修复。

保持Draft；不合并、不发布。
