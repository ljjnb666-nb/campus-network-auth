# 架构说明（ARCHITECTURE.md）

## 总体结构

```
┌──────────────────────────── 澈知科技.exe（桌面程序，C#/WinForms） ───────────────────────────┐
│  Program.cs          UI + 应用编排（单实例/托盘/窗体/流程编排/自测入口）                        │
│  ConnectionStates.cs 领域状态机（网络/认证/核心/服务四类状态 + 迁移校验 + 展示映射）            │
│  CredentialStore.cs  DPAPI 凭据存储（ACL/原子替换/拒重解析点/损坏拒绝覆盖）                     │
│  ServiceIdentity.cs  服务身份校验（WinVerifyTrust + 厂商核对）                                 │
│  ServiceJournal.cs   服务操作事务记录（pending/完成/重启识别）                                  │
│  内嵌资源             Chezhi.AuthCore（认证核心 exe，SHA-256 常量校验） / Chezhi.Licenses      │
└───────────────┬──────────────────────────────────────────────────────────────────────────────┘
                │ 启动: --ephemeral-auth；凭据经匿名管道 stdin（一行 UTF-8 JSON）
                │ 停止: stdin "STOP\n"；状态: stderr 令牌 + EPHEMERAL_EVENT v1 结构化事件
┌───────────────▼──────────────────────────────────────────────────────────────────────────────┐
│  认证核心 ESurfingClient.exe（C，静态链接 libcurl 8.23.0/Schannel + cJSON）                     │
│  等待接入→认证→心跳保活→（会话失效自动重认证）→优雅登出；释放到 %TEMP%\Chezhi-<GUID>（ACL 隔离） │
└──────────────────────────────────────────────────────────────────────────────────────────────┘
```

## 关键设计决策

1. **双进程 + 管道协议**：凭据不进命令行/磁盘/环境变量；核心每次从资源释放并做哈希校验（防篡改）；退出后核心内存凭据被擦除（Kill 除外，已如实记录）。
2. **领域状态机唯一驱动 UI 状态**：`SetStatus` 仅做展示；连接 pill 由 `ConnectionState` 快照驱动，操作成功（保存/删除/服务切换）不可能改变连接状态（有自动化不变量断言）。
3. **有界自动恢复**：GUI 层 2 次退避重试 + 核心层内置心跳/退避，两层不叠加风暴（GUI 重试仅限"曾见过网络的崩溃"，疑似凭据错误不重试）。
4. **最小权限**：普通认证零提权；服务切换走 UAC 子进程，二次校验（签名/进程/热点状态）后操作。

## 模块边界与演进计划（A16 后续）

当前已按职责拆出 4 个可独立测试的模块（ConnectionStates/CredentialStore/ServiceIdentity/ServiceJournal，均为无 UI 依赖的纯逻辑）。Program.cs 仍含 UI、应用编排与自测代码（规模以当前源码为准），完整拆分建议按下列顺序渐进执行（每步跑全量自测）：

1. `Tests.cs`（partial class 搬移自测套件）→ 2. `src/Application`（连接编排/自动恢复）→ 3. `src/Windows`（服务/网络查询适配）→ 4. `src/Desktop`（窗体与控件）。
抽象建议（防过度设计，仅在出现第二实现时才引入接口）：`ICredentialStore` / `IAuthCoreProcess` / `IOfficialServiceManager` / `INetworkStateProvider`。

## A25 预留（多设备网络管理，仅接口设计）

预留能力边界（**当前不实现，不引入任何网络监听服务**）：
- `IDeviceSessionRegistry`：本地只读查询接口（枚举本程序保活的会话/设备通道）。
- `IRemoteControlChannel`：预留基于现有控制通道（127.0.0.1 回环 + 令牌）的扩展点；启用任何远程管理前必须新增：默认关闭的开关、双向认证、明示的监听地址与端口说明。
- 现状：控制通道（ControlServer）仅在核心非 ephemeral 模式启动，桌面 ephemeral 路径不监听任何端口。

## 数据流与安全边界

- 凭据明文仅存在于：GUI 内存（TextBox/序列化缓冲，发送后擦除字节数组）→ 管道 → 核心输入缓冲（读取后擦除）→ 认证请求（协议加密，密钥上游内嵌）。
- 落盘仅一处：DPAPI 加密的 credentials.v1.dat（用户勾选且认证成功后）。
- 日志/诊断：核心 ephemeral 模式日志级别 FATAL（无文件输出）；GUI 诊断导出为脱敏快照。

## 2026-11-26 冻结基线与边界补充

当前内部 RC 基线为 `716ab3193bf26c2d2ad34fad5e07e8220be4b121`。桌面 GUI 仍是 WinForms/.NET Framework 4.x；Roslyn 仅作为隔离候选完成同 runner 双编译与 11 项离线自测，未替换默认构建工具链。

认证核心 Windows 产物为 x86-64；CMake 配置启用 libcurl 与 GCC/MinGW 运行库静态链接；实际进入最终产物的归档成员尚待验证。PE 直接导入报告只能描述 PE 导入表，不能揭示全部静态链接归档成员。最终依赖组成须通过链接 map、归档成员和工具链来源记录补足。

认证协议仍使用明文 HTTP。DPAPI、匿名管道、核心哈希和本地状态机不改变上游协议的传输安全性质。任何远程控制接口、监听服务或额外网络权限均不在当前版本范围内。
