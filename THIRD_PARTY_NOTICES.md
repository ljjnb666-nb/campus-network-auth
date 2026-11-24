# 第三方组件说明（SBOM）

本文件是本项目的软件物料清单（SBOM）与第三方许可说明。构建产物中的每个第三方组件都列明：真实使用版本、来源、许可、在本项目中的用途。运行时的完整许可文本见 `Licenses.txt`（作为 `Chezhi.Licenses` 资源嵌入桌面程序）。

## 组件清单

| 组件 | 版本 | 获取方式与校验 | 许可 | 用途与链接方式 |
|---|---|---|---|---|
| ESurfingClient-CVersion（认证核心） | 上游基线 `ffdfaf13965661ff1c5e298c2c9d9cc6f9194687` 的修改版 | 随仓库携带（`认证核心源码/`），已移除 Web 界面/mongoose/OpenWrt 支持 | Apache-2.0 | 整个认证核心；静态编译进独立 `认证核心.exe`，由桌面程序按 SHA-256 校验后释放执行 |
| libcurl | 8.23.0 | `scripts/Fetch-Curl.ps1` 从 `https://curl.se/download/curl-8.23.0.tar.gz` 下载，SHA-256 `E3DE77CFA6367083C776A26647FC9F633B538C06C3BAA73D931A252AC8F72B49` 固定校验；缓存于 `third-party/`（不入库） | curl license（MIT 系） | HTTP 客户端；静态链接进认证核心（Schannel 后端，关闭 OpenSSL/zlib/PSL/SSH2/Brotli/zstd 与 curl 命令行工具） |
| cJSON | 1.7.19 | 随仓库携带（`认证核心源码/esurfingclient/app/third_party/cJSON/`） | MIT | JSON 解析；静态编译进认证核心 |
| .NET Framework | 4.x（Windows 系统自带） | 系统 | Microsoft Windows 许可 | 桌面程序（WinForms）运行时；系统自带，不随本程序分发 |
| Windows 系统 API | DPAPI / WMI / ServiceController / Win32 | 系统 | Microsoft Windows 许可 | 凭据加密、服务与网络状态查询 |

已移除组件（历史版本曾使用）：mongoose（GPL-2.0-only/商业双许可，Web 界面服务器，已于 V1.2.0 整改中整体移除，不再链接，许可负担随之解除）。

## 漏洞核查记录

- 核查工具：`scripts/Test-CurlVulns.ps1`（OSV 区间精确判定，对照官方 `https://curl.se/docs/vuln.json`）。
- libcurl 8.20.0（历史固定版本）：受影响 **84** 条（含 1 条 High：HTTP/2 upload Use-After-Free，见 CURL-CVE-2026-92392）。
- 处置：升级至 8.23.0（2026-10-14 发布的最新版）。升级后受影响 **42** 条——均为截至最新版尚无修复版本的公开披露项（绝大多数为 Low），其中真正落入本项目使用面（纯 HTTP、Schannel、静态链接、无 HTTP/2/WS/SSH/FTP/代理）的子集需人工复核，已列为残余风险记录于 `AUDIT_REPORT.md`（A21）。
- 升级验证：完整源码重建 + 全部离线自测（logic/credentials/core/ui）通过后才采纳；认证协议本身（ESurfing HTTP 表单协议）不受传输库升级影响。
- 每次构建时 CI 均执行依赖版本漏洞核查步骤（结果不阻断构建，作为人工复核证据）。

## 分发注意

- 认证核心为 Apache-2.0 衍生作品：再分发时须保留 `Licenses.txt` 中的 Apache-2.0 声明与版权条款（§4）。
- libcurl 与 cJSON 许可宽松，随附声明即可。
- 桌面界面源码（Program.cs/CredentialStore.cs）当前未附许可声明：**对外分发前必须补充**，在此之前仅限作者自用。
