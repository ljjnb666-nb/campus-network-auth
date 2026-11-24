# 澈知科技校园网连接（campus-network-auth）

Windows 桌面校园网认证客户端：以官方天翼认证协议替代官方客户端完成登录并保持在线，凭据 DPAPI 加密存储，最小权限设计（普通认证不需要管理员权限）。

> ⚠️ 安全须知（务必阅读）：认证协议为**明文 HTTP**（上游协议固有限制），同一校园网内的窃听者可还原密码。请勿在其它场合复用校园网密码。详见 [SECURITY.md](SECURITY.md)。

## 主要功能

- 校园网认证：输入账号密码即连接，支持已保存会话自动续期、掉线自动重连（核心内置心跳与退避）。
- 凭据管理：可选"记住账号密码"（默认关闭，仅勾选且认证成功后才落盘），DPAPI CurrentUser 加密 + ACL 隔离，托盘菜单一键清除。
- 天翼服务切换：准备连接（停用天翼后台服务，走 UAC + 服务签名身份校验）/ 恢复天翼（含未完成操作的重启识别与恢复提示）。
- 状态与诊断：领域状态机驱动的连接状态显示（已认证/已联网·会话/认证中/失败明确区分），脱敏诊断信息导出。
- 移动热点：提供 Windows 移动热点设置入口（认证后即可共享网络；本程序不实现独立热点控制）。

## 技术架构

- 桌面端：C# / WinForms / .NET Framework 4.x（系统自带，无需安装运行时）。
- 认证核心：C（基于开源 [ESurfingClient-CVersion](https://github.com/BadGhost520/ESurfingClient-CVersion) 修改，已移除 Web 界面与 OpenWrt 支持），静态链接 libcurl(Schannel)。
- 桌面程序以 SHA-256 校验后释放核心 exe，凭据经匿名管道传递（不进命令行/磁盘/日志）。
- 详见 [ARCHITECTURE.md](ARCHITECTURE.md)。

## 运行要求

- Windows 10/11 x64。
- 已安装天翼校园网客户端（用于服务切换；未安装也可用部分功能）。

## 构建

```powershell
# 完整源码重建（全新克隆即可用；自动下载并校验 libcurl 8.23.0）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build-Core.ps1

# 或仅重建桌面界面（需已有认证核心 exe）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\Build.ps1
```

要求：CMake、Ninja、MinGW-w64 GCC（脚本自动发现 PATH/标准位置/VS 自带工具链）。详见 [从源码构建.md](从源码构建.md)。CI 见 `.github/workflows/build.yml`。

## 使用说明

1. 在天翼客户端中断开并完全退出，关闭移动热点。
2. 点"准备连接"（UAC 确认后停用天翼后台服务）。
3. 输入账号密码，点"立即连接"（回车同样有效）；勾选"记住账号密码"可在认证成功后加密保存。
4. 连接成功后到 Windows 设置开启移动热点即可共享。
5. 退出用托盘菜单"退出程序"（会先通知服务器登出）；换回官方客户端用"恢复天翼"。

## 常见问题 / 安全限制 / 数据隐私

见 [SECURITY.md](SECURITY.md)（威胁模型与已知限制）、[TESTING.md](TESTING.md)（自测入口）、[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)（组件与许可）。

- 数据隐私：凭据仅存于本机（DPAPI 加密，仅当前 Windows 用户可读）；诊断导出为脱敏内容；程序不收集/上传任何数据。

## 卸载与官方服务恢复

本程序为绿色软件：删除程序文件与 `%LOCALAPPDATA%\CheZhiTech` 即完成卸载。若此前停用了天翼服务，先用"恢复天翼"或以管理员运行 `sc start ESurfingSvr` 恢复。

## 许可

- 认证核心：Apache-2.0（衍生自 ESurfingClient-CVersion，见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)）。
- 桌面界面源码：暂未附许可声明，**对外分发前需补充**（当前仅限自用）。
