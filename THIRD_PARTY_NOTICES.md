# 第三方组件说明（SBOM）

本文件是本项目的软件物料清单（SBOM）与第三方许可说明。构建产物中的每个第三方组件都列明：真实使用版本、来源、许可、在本项目中的用途。运行时的完整许可文本见 `Licenses.txt`（作为 `Chezhi.Licenses` 资源嵌入桌面程序）。

## 组件清单

| 组件 | 版本 | 获取方式与校验 | 许可 | 用途与链接方式 |
|---|---|---|---|---|
| ESurfingClient-CVersion（认证核心） | 上游基线 `ffdfaf13965661ff1c5e298c2c9d9cc6f9194687` 的修改版 | 随仓库携带（`认证核心源码/`），已移除 Web 界面/mongoose/OpenWrt 支持 | Apache-2.0 | 整个认证核心；静态编译进独立 `认证核心.exe`，由桌面程序按 SHA-256 校验后释放执行 |
| libcurl | 8.23.0 | `scripts/Fetch-Curl.ps1` 从 `https://curl.se/download/curl-8.23.0.tar.gz` 下载，SHA-256 `E3DE77CFA6367083C776A26647FC9F633B538C06C3BAA73D931A252AC8F72B49` 固定校验；缓存于 `third-party/`（不入库） | curl license（MIT 系） | HTTP 客户端；静态链接进认证核心（Schannel 后端，关闭 OpenSSL/zlib/PSL/SSH2/Brotli/zstd 与 curl 命令行工具） |
| cJSON | 1.7.19（本地修订） | 随仓库携带（`认证核心源码/esurfingclient/app/third_party/cJSON/`） | MIT | JSON 解析；静态编译进认证核心 |
| .NET Framework | 4.x（Windows 系统自带） | 系统 | Microsoft Windows 许可 | 桌面程序（WinForms）运行时；系统自带，不随本程序分发 |
| Windows 系统 API | DPAPI / WMI / ServiceController / Win32 | 系统 | Microsoft Windows 许可 | 凭据加密、服务与网络状态查询 |

已移除组件（历史版本曾使用）：mongoose（GPL-2.0-only/商业双许可，Web 界面服务器，已于 V1.2.0 整改中整体移除，不再链接，许可负担随之解除）。

## 漏洞核查记录

- 核查工具：`scripts/Test-CurlVulns.ps1`（OSV 区间精确判定，对照官方 `https://curl.se/docs/vuln.json`）。
- libcurl 8.20.0（历史固定版本）：受影响 **84** 条（含 1 条 High：HTTP/2 upload Use-After-Free，见 CURL-CVE-2026-92392）。
- 处置：升级至 8.23.0。旧扫描器将 Git commit 范围当作版本范围，曾误报 42 条；已修正并加入官方版本专用公告交叉校验。基线 6ad2b93 的 CI 45574796280 两源一致，已知影响记录为 **0**。这是该次版本公告核查结果，不是全部依赖无漏洞证明；历史 8.20.0 数量亦未经新解析器复核。
- 升级验证：完整源码重建 + 全部离线自测（logic/credentials/core/ui）通过后才采纳；认证协议本身（ESurfing HTTP 表单协议）不受传输库升级影响。
- 每次构建时 CI 均执行依赖版本漏洞核查步骤（结果不阻断构建，作为人工复核证据）。

## 分发注意

- 认证核心为 Apache-2.0 衍生作品：再分发时须保留 `Licenses.txt` 中的 Apache-2.0 声明与版权条款（§4）。
- libcurl 与 cJSON 许可宽松，随附声明即可。
- 桌面界面源码（Program.cs/CredentialStore.cs）当前未附许可声明：**对外分发前必须补充**，在此之前仅限作者自用。

## 构建依赖锁与机器可读清单

- `scripts/build-dependencies.lock.json` 记录 CI #257（45618397006）安装的 20 个 UCRT64 软件包版本及 CMake 4.5.0；构建前检查安装结果，不匹配则失败。此为版本漂移门禁，不是归档字节级锁定或可复现安装；滚动仓库不保证旧版本一直可用。
- cJSON.h 与上游 v1.7.19 Git blob 一致；cJSON.c 不一致，经逐行比较，本地额外增加 SetNumberHelper 空指针检查、两个打印嵌套深度检查。锁定当前本地 blob，不声称与原版完全一致。
- `build-core/sbom.cdx.json` 由构建清单生成，CycloneDX 1.5 源码级物料清单，记录核心、curl、cJSON 及已检查构建包版本；不代表完整二进制链接/系统组件清单，不是签名或来源证明。

## cJSON 公开公告复审（发布阻断，2026-11-25）

- [CVE-2026-16554](https://cert.pl/en/posts/2026/07/CVE-2026-16554/)：1.7.19 的 32 位 print_string_ptr 整数溢出。Windows 当前核心构建目标为 x86_64，仍需核实实际二进制架构、输入大小约束及其他平台；不得泛化为已修复。
- [CVE-2026-67215](https://security-tracker.debian.org/tracker/CVE-2026-67215)：JSON Patch 递归资源消耗风险。当前 CMakeLists 仅编译 cJSON.c，未列入 cJSON_Utils.c；仍需核对调用与链接证据。
- [CVE-2026-67216](https://security-tracker.debian.org/tracker/CVE-2026-67216)：cJSON_Compare 的递归比较算法复杂度风险，不能描述为仅二次复杂度；需审查实际调用与输入边界。
- 2026-11-25 复核 CERT Polska 与 Debian Security Tracker 原始公告，确认上述编号及问题描述。此前引用未经充分核验，现以该公开来源为准。上述为已发现公告，不是穷尽扫描；必须完成当前公告清单、实际调用路径、修复/不适用证据和回归。curl 的零条版本公告不能代替 cJSON 或其他依赖核查。

### 后续整改与证据（2026-11-25）

- CI #268（45619253721）通过版本锁、manifest、源码级 SBOM 生成及 Linux crypto 审计。该 CI 不包含下列新门禁。
- 本地 cJSON 增加 ensure 与 print_string_ptr 的长度累加上限检查；已同步本地源码 blob 锁。新增合成 ASan/UBSan 边界回归，待最新 CI 验证；没有执行 32 位完整构建或大型分配测试。
- 复审发现旧 JSON Patch 扫描误用了 cJSON_ 前缀，实际工具 API 使用 cJSONUtils_。已修正为扫描 cJSON_Compare 和全部 cJSONUtils_ 标识符，新增正反例；旧 CI 不能证明该规则通过。CMake 组件检查保留。源码级门禁不是完整程序可达性证明；新 CI 尚待验证，不标记上游 CVE 已修复。
- 新增 PE 直接导入表及 x86-64 原生核心检查，输出 pe-imports.json；不覆盖传递依赖、运行时动态加载或所有静态链接组件。
- CycloneDX 1.5 官方三份 Schema 按 Git blob 校验，并局部化外部引用后使用 PowerShell Test-Json 校验，加入无效格式负向测试；待 CI 验证。
- 最新工作流修复了中间提交出现的重复步骤，检查最终文件仅有一组上传步骤及两个预期 job。
