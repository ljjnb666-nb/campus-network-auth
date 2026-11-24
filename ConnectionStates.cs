using System;
using System.Collections.Generic;

// 领域状态模型（V1.2.0）：连接状态、网络状态、核心进程状态、官方服务状态
// 与"操作结果"彻底分离——操作成功（保存/删除/服务切换）不再被显示为"已连接"。
// 状态迁移集中在 ConnectionState 内校验，非法迁移被拒绝并记录，供 UI 与测试共用。
internal static class ConnectionStates
{
    // 网络可达性（与是否完成本客户端认证无关）
    internal enum NetworkState { Unknown, Offline, InternetAvailable, AuthenticationRequired }

    // 本客户端的认证任务状态
    internal enum AuthState { Idle, WaitingForNetwork, Authenticating, Authenticated, AuthenticatedBySession, Reauthenticating, Failed, LoggingOut }

    // 认证核心子进程状态
    internal enum CoreState { NotStarted, Starting, Running, Stopping, Exited, Crashed }

    // 天翼官方后台服务状态
    internal enum ServiceState { Unknown, NotInstalled, Running, Stopped, Transitioning, Failed }

    // 一次性操作结果（仅影响提示文案与配色，不影响连接状态）
    internal enum OpResult { Information, Success, Warning, Error }
}

// 迁移规则与当前状态快照。线程模型：仅 UI 线程访问（与 WinForms 控件一致）；
// 纯托管逻辑无 I/O，LogicTests 直接实例化断言。
internal sealed class ConnectionState
{
    private ConnectionStates.AuthState auth = ConnectionStates.AuthState.Idle;
    private ConnectionStates.CoreState core = ConnectionStates.CoreState.NotStarted;
    private ConnectionStates.NetworkState network = ConnectionStates.NetworkState.Unknown;
    private ConnectionStates.ServiceState service = ConnectionStates.ServiceState.Unknown;

    private static readonly Dictionary<ConnectionStates.AuthState, ConnectionStates.AuthState[]> AllowedFrom = new Dictionary<ConnectionStates.AuthState, ConnectionStates.AuthState[]>
    {
        // 静止态可进入任意活动态；活动态之间允许核心驱动的正常流转；
        // 关键不变量：用户停止/退出后不允许"自动回到已认证"（只能经 Authenticating 重新获得）。
        { ConnectionStates.AuthState.Idle, new[] { ConnectionStates.AuthState.WaitingForNetwork, ConnectionStates.AuthState.Authenticating } },
        { ConnectionStates.AuthState.WaitingForNetwork, new[] { ConnectionStates.AuthState.Authenticating, ConnectionStates.AuthState.Idle, ConnectionStates.AuthState.Failed, ConnectionStates.AuthState.LoggingOut } },
        { ConnectionStates.AuthState.Authenticating, new[] { ConnectionStates.AuthState.Authenticated, ConnectionStates.AuthState.AuthenticatedBySession, ConnectionStates.AuthState.Failed, ConnectionStates.AuthState.WaitingForNetwork, ConnectionStates.AuthState.LoggingOut, ConnectionStates.AuthState.Idle } },
        { ConnectionStates.AuthState.Authenticated, new[] { ConnectionStates.AuthState.Reauthenticating, ConnectionStates.AuthState.LoggingOut, ConnectionStates.AuthState.Failed, ConnectionStates.AuthState.Idle } },
        { ConnectionStates.AuthState.AuthenticatedBySession, new[] { ConnectionStates.AuthState.Reauthenticating, ConnectionStates.AuthState.LoggingOut, ConnectionStates.AuthState.Failed, ConnectionStates.AuthState.Idle } },
        { ConnectionStates.AuthState.Reauthenticating, new[] { ConnectionStates.AuthState.Authenticated, ConnectionStates.AuthState.AuthenticatedBySession, ConnectionStates.AuthState.Failed, ConnectionStates.AuthState.WaitingForNetwork, ConnectionStates.AuthState.LoggingOut, ConnectionStates.AuthState.Idle } },
        { ConnectionStates.AuthState.Failed, new[] { ConnectionStates.AuthState.Authenticating, ConnectionStates.AuthState.Idle, ConnectionStates.AuthState.WaitingForNetwork, ConnectionStates.AuthState.LoggingOut } },
        { ConnectionStates.AuthState.LoggingOut, new[] { ConnectionStates.AuthState.Idle, ConnectionStates.AuthState.Failed } }
    };

    internal ConnectionStates.AuthState Auth { get { return auth; } }
    internal ConnectionStates.CoreState Core { get { return core; } }
    internal ConnectionStates.NetworkState Network { get { return network; } }
    internal ConnectionStates.ServiceState Service { get { return service; } }
    internal List<string> RejectedTransitions { get; private set; }

    internal ConnectionState()
    {
        RejectedTransitions = new List<string>();
    }

    /// <summary>尝试迁移认证状态；非法迁移拒绝并记录，返回是否接受。</summary>
    internal bool TrySetAuth(ConnectionStates.AuthState next, string reason)
    {
        if (next == auth) return true;
        ConnectionStates.AuthState[] allowed;
        if (!AllowedFrom.TryGetValue(auth, out allowed) || Array.IndexOf(allowed, next) < 0)
        {
            RejectedTransitions.Add(auth + "->" + next + " (" + (reason ?? "?") + ")");
            return false;
        }
        auth = next;
        return true;
    }

    internal void SetCore(ConnectionStates.CoreState next) { core = next; }
    internal void SetNetwork(ConnectionStates.NetworkState next) { network = next; }
    internal void SetService(ConnectionStates.ServiceState next) { service = next; }

    /// <summary>操作结果永远不改领域状态（不变量：保存/删除/服务切换 ≠ 连接状态变化）。</summary>
    internal void RecordOperation(ConnectionStates.OpResult result, string description) { /* 仅作语义锚点，无副作用 */ }

    // —— 展示层映射（唯一来源；UI 不再从颜色推断状态）——
    internal string PillText
    {
        get
        {
            switch (auth)
            {
                case ConnectionStates.AuthState.Authenticating:
                case ConnectionStates.AuthState.Reauthenticating: return "认证中";
                case ConnectionStates.AuthState.WaitingForNetwork: return "待入网";
                case ConnectionStates.AuthState.Authenticated: return "已认证";
                case ConnectionStates.AuthState.AuthenticatedBySession: return "已联网·会话";
                case ConnectionStates.AuthState.LoggingOut: return "登出中";
                case ConnectionStates.AuthState.Failed: return "认证失败";
                default: return "未连接";
            }
        }
    }

    internal bool PillBusy
    {
        get
        {
            return auth == ConnectionStates.AuthState.Authenticating ||
                   auth == ConnectionStates.AuthState.Reauthenticating ||
                   auth == ConnectionStates.AuthState.WaitingForNetwork ||
                   auth == ConnectionStates.AuthState.LoggingOut;
        }
    }
}
