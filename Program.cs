using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using Microsoft.Win32;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.ServiceProcess;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;

[assembly: AssemblyTitle("澈知科技")]
[assembly: AssemblyProduct("澈知科技校园网连接")]
[assembly: AssemblyDescription("校园网认证客户端（含 Windows 移动热点设置入口）")]
[assembly: AssemblyCompany("澈知科技")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

internal static class Program
{
    private const string ServiceName = "ESurfingSvr";
    private const string OfficialExe = @"C:\Program Files (x86)\Chinatelecom_GDPortal\ESurfingClient.exe";
    private const string CoreResource = "Chezhi.AuthCore";
    private const string LicenseResource = "Chezhi.Licenses";
    private const string CoreHash = "7AA893C569CB9C714539F1430352CB8404C2900D9C7F87734D384DD24B57397F";
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private const string CredentialFileName = "credentials.v1.dat";
    private static string tempDir;
    private static string corePath;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--admin-prepare") return ChangeService(true);
        if (args.Length == 1 && args[0] == "--admin-restore") return ChangeService(false);
        if (args.Length == 1 && args[0] == "--self-test-logic") return LogicTests();
        if (args.Length == 1 && args[0] == "--self-test-core") return CoreTest(false);
        if (args.Length == 1 && args[0] == "--self-test-stop") return CoreTest(true);
        if (args.Length == 1 && args[0] == "--self-test-observe") return ObserveTests();
        if (args.Length == 1 && args[0] == "--self-test-credentials") return CredentialTests();
        if (args.Length == 1 && args[0] == "--self-test-ui") return UiTests();
        if (args.Length == 1 && args[0] == "--self-test-stub") return StubTestChild();
        if (args.Length == 2 && args[0] == "--self-test-credential-read") return CredentialReadTest(args[1]);
        if (args.Length != 0) return 2;
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        string identity = WindowsIdentity.GetCurrent().User.Value;
        string mutexName = @"Local\CheZhiCampus-" + identity + "-instance";
        string eventName = @"Local\CheZhiCampus-" + identity + "-show";
        bool created;
        using (var mutex = new System.Threading.Mutex(false, mutexName, out created))
        {
            if (!created)
            {
                if (!SignalExistingWindow(eventName))
                    MessageBox.Show("澈知科技已在运行，请到系统托盘双击图标打开。", "澈知科技", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return 0;
            }
            using (var showEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, eventName))
            using (var shutdown = new System.Threading.ManualResetEvent(false))
            {
        try
        {
            ExtractCore();
            using (var window = new MainWindow())
            {
                var handle = window.Handle;
                var listener = new System.Threading.Thread(delegate() {
                    var handles = new System.Threading.WaitHandle[] { showEvent, shutdown };
                    while (System.Threading.WaitHandle.WaitAny(handles) == 0)
                    {
                        try { if (!window.IsDisposed) window.BeginInvoke((Action)window.ShowFromTray); }
                        catch (ObjectDisposedException) { }
                        catch (InvalidOperationException) { }
                    }
                });
                listener.IsBackground = true;
                listener.Start();
                try { Application.Run(window); }
                finally { shutdown.Set(); listener.Join(2000); }
            }
            return 0;
        }
        catch (Exception)
        {
            MessageBox.Show("程序初始化失败，请确认临时文件目录可用后重试。", "澈知科技", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
        finally { CleanupCore(); }
            }
        }
    }

    private static bool SignalExistingWindow(string eventName)
    {
        for (int i = 0; i < 40; i++)
        {
            try { using (var existing = System.Threading.EventWaitHandle.OpenExisting(eventName)) { existing.Set(); return true; } }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { System.Threading.Thread.Sleep(50); }
            catch (UnauthorizedAccessException) { return false; }
        }
        return false;
    }

    private static void ExtractCore()
    {
        if (corePath != null) return;
        string root = Path.GetFullPath(Path.GetTempPath());
        string dir = Path.Combine(root, "Chezhi-" + Guid.NewGuid().ToString("N"));
        var current = WindowsIdentity.GetCurrent().User;
        if (current == null) throw new InvalidOperationException("No user identity.");
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(current, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        Directory.CreateDirectory(dir, acl);
        tempDir = dir;
        corePath = Path.Combine(dir, "ESurfingClient.exe");
        using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(CoreResource))
        {
            if (input == null) throw new InvalidDataException("Missing core.");
            using (var output = new FileStream(corePath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) input.CopyTo(output);
        }
        VerifyCore();
        CleanupStaleCoreDirs(dir); // 启动即清扫上次异常退出留下的残留（A18）
    }

    private static void VerifyCore()
    {
        using (var stream = new FileStream(corePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var sha = SHA256.Create())
        {
            string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            if (!String.Equals(actual, CoreHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Core integrity check failed.");
        }
    }

    private static void CleanupCore()
    {
        try { if (corePath != null && File.Exists(corePath)) File.Delete(corePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        try { if (tempDir != null && Directory.Exists(tempDir)) Directory.Delete(tempDir, false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        CleanupStaleCoreDirs(tempDir);
    }

    // A18：程序崩溃/被强杀后 %TEMP%\Chezhi-* 会残留（含核心 exe）。单实例互斥保证
    // 不存在第二个 GUI，因此非当前目录的 Chezhi-* 均为历史残留；正被运行中核心
    // 占用的目录删除会失败（文件锁），静默跳过留给下次，绝不影响活动进程。
    private static void CleanupStaleCoreDirs(string keep)
    {
        try
        {
            string root = Path.GetFullPath(Path.GetTempPath());
            foreach (string dir in Directory.GetDirectories(root, "Chezhi-*"))
            {
                string full = Path.GetFullPath(dir);
                if (String.Equals(full, keep, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(full, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch { }
    }

    private static int ClientCount()
    {
        // 优先 WMI: 能按可执行文件路径排除本程序释放到临时目录的核心 (含上次异常退出的残留),
        // 只统计官方客户端; WMI 不可用时退回按进程名计数 (保守, 宁可多算)
        try
        {
            string tempRoot = Path.GetFullPath(Path.GetTempPath());
            int count = 0;
            using (var query = new ManagementObjectSearcher("SELECT ExecutablePath FROM Win32_Process WHERE Name='ESurfingClient.exe'"))
            {
                query.Options.Timeout = TimeSpan.FromSeconds(5);
                using (ManagementObjectCollection rows = query.Get())
                {
                    foreach (ManagementObject row in rows)
                    {
                        string path = Convert.ToString(row["ExecutablePath"]);
                        if (!String.IsNullOrEmpty(path) &&
                            path.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) &&
                            path.IndexOf("\\Chezhi-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                        count++;
                    }
                }
            }
            return count;
        }
        catch
        {
            Process[] matches = Process.GetProcessesByName("ESurfingClient");
            try { return matches.Length; }
            finally { foreach (Process p in matches) p.Dispose(); }
        }
    }

    private static bool CanConnect(ServiceControllerStatus state, int count)
    {
        return state == ServiceControllerStatus.Stopped && count == 0;
    }

    private static string CheckBeforeConnect()
    {
        try
        {
            int count = ClientCount();
            if (!ServiceInstalled())
            {
                // 没装天翼客户端 = 没有后台服务, 无需"准备连接"; 仍要求没有官方客户端进程在跑
                return count == 0 ? null
                    : "检测到天翼客户端仍在运行，请先在客户端里断开并完全退出，再回来连接。";
            }
            using (var service = new ServiceController(ServiceName))
            {
                service.Refresh();
                if (CanConnect(service.Status, count)) return null;
                if (count != 0) return "检测到天翼客户端仍在运行，请先在客户端里断开并完全退出，再回来连接。";
                return "天翼后台服务正在运行，请先点击“准备连接”完成切换。";
            }
        }
        catch { return "暂时读不到天翼服务状态，请稍后再试。"; }
    }

    private static void QueryService(out int count, out string path)
    {
        count = 0;
        path = null;
        using (var query = new ManagementObjectSearcher("SELECT PathName FROM Win32_Service WHERE Name='ESurfingSvr'"))
        {
            query.Options.Timeout = TimeSpan.FromSeconds(5); // WMI 冷启动可达数秒, 不设限会卡 UI 线程
            using (ManagementObjectCollection rows = query.Get())
            {
                foreach (ManagementObject row in rows) { count++; path = Convert.ToString(row["PathName"]); }
            }
        }
    }

    private static bool ServiceInstalled()
    {
        int count;
        string path;
        QueryService(out count, out path);
        return count > 0;
    }

    // 服务事务记录用：当前服务状态快照（读不到=unknown）
    private static string CurrentServiceState()
    {
        try
        {
            using (var service = new ServiceController(ServiceName))
            {
                service.Refresh();
                return service.Status.ToString();
            }
        }
        catch { return "unknown"; }
    }

    // Any install location is accepted; the service must be unique and its executable
    // must still be the expected ESurfingSvr.exe so the elevated stop never targets a look-alike.
    private static string ServiceExePath()
    {
        int count;
        string path;
        QueryService(out count, out path);
        if (count != 1 || String.IsNullOrEmpty(path)) return null;
        path = path.Trim();
        if (path.Length > 0 && path[0] == '"')
        {
            int end = path.IndexOf('"', 1);
            if (end > 1) path = path.Substring(1, end - 1); // PathName 常带引号, 也可能跟参数
        }
        else
        {
            int space = path.IndexOf(' ');
            if (space > 0) path = path.Substring(0, space); // 未加引号但带参数的写法
        }
        return String.Equals(Path.GetFileName(path), "ESurfingSvr.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    private static bool IsExpectedService()
    {
        // A14：文件名匹配 + Authenticode 签名链验证 + 签名者厂商核对（电信系）。
        // 任一不满足即身份不符，提权子进程 fail-closed（guard 21 拒绝操作）。
        string path = ServiceExePath();
        return path != null && ServiceIdentity.VerifyExpectedService(path);
    }

    // 优先从天翼服务所在目录找原客户端 (用户可能装在任意位置), 再兜底官方默认路径
    private static string FindOfficialClient()
    {
        try
        {
            string serviceExe = ServiceExePath();
            if (serviceExe != null)
            {
                string candidate = Path.Combine(Path.GetDirectoryName(serviceExe), "ESurfingClient.exe");
                if (File.Exists(candidate)) return candidate;
            }
        }
        catch { }
        return File.Exists(OfficialExe) ? OfficialExe : null;
    }

    private static bool DirectAdaptersIdle()
    {
        using (var query = new ManagementObjectSearcher("SELECT Name, NetConnectionStatus FROM Win32_NetworkAdapter WHERE Name LIKE '%Wi-Fi Direct%'"))
        {
            query.Options.Timeout = TimeSpan.FromSeconds(5);
            using (ManagementObjectCollection rows = query.Get())
            {
                foreach (ManagementObject row in rows)
                {
                    object status = row["NetConnectionStatus"];
                    // 保守处理（A09/P6）：适配器存在但状态未知(null)时视为"非空闲"，
                    // 只有明确断开(0)/媒体断开(7)/硬件不存在(4/5/6)才允许服务切换。
                    if (status == null) return false;
                    int s = Convert.ToInt32(status);
                    if (s == 0 || s == 7 || s == 4 || s == 5 || s == 6) continue;
                    return false; // 1/2/3/8/9/10/11/12 一律视为可能在使用
                }
            }
        }
        // 没有任何 Wi-Fi Direct 适配器 (如纯有线台式机) = 不可能开着热点, 视为空闲
        return true;
    }

    private static int ServiceGuard(bool identityOk, int clients, bool directIdle, ServiceControllerStatus state, bool canStop, bool prepare)
    {
        if (!identityOk) return 21;
        if (clients != 0) return 22;
        if (!directIdle) return 23;
        if (prepare && state == ServiceControllerStatus.Stopped) return 0;
        if (!prepare && state == ServiceControllerStatus.Running) return 0;
        if (prepare && (state != ServiceControllerStatus.Running || !canStop)) return 24;
        if (!prepare && state != ServiceControllerStatus.Stopped) return 24;
        return 0;
    }

    // Only invoked in an explicitly elevated child after the user clicks a service action.
    // Fail closed if the original service identity, other client processes, or Wi-Fi Direct state cannot be checked.
    private static int ChangeService(bool prepare)
    {
        try
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return 20;
            if (!ServiceInstalled())
            {
                // 没装天翼客户端就没有后台服务: “准备连接”无事可做直接成功, “恢复天翼”无从恢复
                return prepare ? 0 : 21;
            }
            bool identityOk = IsExpectedService();
            int clients = ClientCount();
            bool directIdle = DirectAdaptersIdle();
            using (var service = new ServiceController(ServiceName))
            {
                service.Refresh();
                ServiceControllerStatus current = service.Status;
                bool canStop = current == ServiceControllerStatus.Running && service.CanStop;
                int guard = ServiceGuard(identityOk, clients, directIdle, current, canStop, prepare);
                if (guard != 0) return guard;
                if (prepare)
                {
                    if (current == ServiceControllerStatus.Stopped) return 0;
                    service.Stop();
                    service.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
                }
                else
                {
                    if (current == ServiceControllerStatus.Running) return 0;
                    service.Start();
                    service.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(20));
                }
            }
            return 0;
        }
        catch { return 25; }
    }

        private static string ServiceError(int code)
        {
            switch (code)
            {
                case 20: return "未获得管理员权限，操作已取消。";
                case 21: return "未找到天翼后台服务，或无法确认其身份，操作已取消。";
                case 22: return "仍有天翼客户端或认证进程在运行，请先断开并完全退出。";
                case 23: return "移动热点或 Wi-Fi Direct 仍在使用（或状态未知），请先关闭热点。";
                case 24: return "天翼服务正在切换中，请稍后再试。";
                default: return "操作未完成，请检查服务状态后重试。";
            }
        }

    private static Process LaunchCore(string mode, string username, string password, int channel, Action<Process, string> status)
    {
        if (Utf8.GetByteCount(username) >= 16 || Utf8.GetByteCount(password) >= 128)
            throw new ArgumentException("账号需少于 16 字节、密码需少于 128 字节（一个汉字按 3 字节计）。");
        string json = new JavaScriptSerializer().Serialize(new { username = username, password = password, channel = channel });
        if (Utf8.GetByteCount(json) + 1 >= 4096) throw new ArgumentException("输入过长，请检查账号和密码。");
        VerifyCore();
        var si = new ProcessStartInfo {
            FileName = corePath, Arguments = mode, WorkingDirectory = tempDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var p = new Process { StartInfo = si, EnableRaisingEvents = true };
        DataReceivedEventHandler filter = delegate(object sender, DataReceivedEventArgs e) {
            string s = e.Data;
            if (s == null) return;
            // 结构化事件（协议 v1，与旧令牌并行）：整行转发，由 OnCoreStatus 归档
            if (s.StartsWith("EPHEMERAL_EVENT ", StringComparison.Ordinal) && status != null)
            {
                status((Process)sender, s);
                return;
            }
            if (status != null && (s == "EPHEMERAL_AUTH_OK" || s == "EPHEMERAL_AUTH_FAILED" ||
                s == "EPHEMERAL_WAIT_NETWORK" || s == "EPHEMERAL_ONLINE" ||
                s == "EPHEMERAL_STOP_ACCEPTED" || s == "EPHEMERAL_STOP_CHANNEL_FAILED"))
                status((Process)sender, s);
        };
        p.OutputDataReceived += filter; p.ErrorDataReceived += filter;
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        try
        {
            byte[] bytes = Utf8.GetBytes(json + "\n");
            try { p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.BaseStream.Flush(); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        catch { try { p.StandardInput.Close(); } catch { } p.Dispose(); throw; }
        return p;
    }

    private static bool RequestStop(Process p)
    {
        if (p == null || p.HasExited) return false;
        try
        {
            byte[] bytes = Encoding.ASCII.GetBytes("STOP\n");
            p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length);
            p.StandardInput.BaseStream.Flush();
            return true;
        }
        catch (Exception e)
        {
            if (!(e is IOException) && !(e is ObjectDisposedException) && !(e is InvalidOperationException)) throw;
            try { p.StandardInput.Close(); } catch { }
            return false;
        }
    }

    private static int LogicTests()
    {
        bool ok = CanConnect(ServiceControllerStatus.Stopped, 0)
            && !CanConnect(ServiceControllerStatus.Stopped, 1)
            && !CanConnect(ServiceControllerStatus.Running, 0)
            && !CanConnect(ServiceControllerStatus.StopPending, 0)
            && ServiceError(22).Contains("认证进程")
            && ServiceError(23).Contains("热点")
            && ServiceGuard(false, 0, true, ServiceControllerStatus.Running, true, true) == 21
            && ServiceGuard(true, 1, true, ServiceControllerStatus.Running, true, true) == 22
            && ServiceGuard(true, 0, false, ServiceControllerStatus.Stopped, false, false) == 23
            && ServiceGuard(true, 0, true, ServiceControllerStatus.StopPending, false, true) == 24
            && ServiceGuard(true, 0, true, ServiceControllerStatus.Running, false, true) == 24
            && ServiceGuard(true, 0, true, ServiceControllerStatus.Running, true, true) == 0
            && ServiceGuard(true, 0, true, ServiceControllerStatus.Stopped, false, false) == 0
            && StateMachineInvariants();
        Console.WriteLine(ok ? "LOGIC_PASS" : "LOGIC_FAIL");
        return ok ? 0 : 1;
    }

    // 状态机不变量（7.2）：合法迁移被接受、非法迁移被拒绝；操作结果不改变连接状态；
    // 任何路径都不能"跳过认证直接变成已认证"；停止/登出后回到 Idle 而非保持已认证。
    private static bool StateMachineInvariants()
    {
        var m = new ConnectionState();
        bool ok = true;

        // 正常链路：Idle → Authenticating → Authenticated → LoggingOut → Idle
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticating, "connect");
        ok &= m.Auth == ConnectionStates.AuthState.Authenticating;
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticated, "auth_ok");
        ok &= m.PillText == "已认证";
        ok &= m.TrySetAuth(ConnectionStates.AuthState.LoggingOut, "stop_accepted");
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Idle, "exited");
        ok &= m.PillText == "未连接";

        // 非法迁移：Idle 不能直接跳到"已认证"（未认证不得显示已连接）
        ok &= !m.TrySetAuth(ConnectionStates.AuthState.Authenticated, "short-circuit");
        ok &= m.Auth == ConnectionStates.AuthState.Idle;
        // 已认证态不能"不经停止直接回等待网络"（须先停止/失败）
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticating, "retry") && m.TrySetAuth(ConnectionStates.AuthState.Authenticated, "ok");
        ok &= !m.TrySetAuth(ConnectionStates.AuthState.WaitingForNetwork, "invalid-from-authed");

        // 失败链路：Authenticating → Failed →（重新认证）Authenticating
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Failed, "auth_failed");
        ok &= m.PillText == "认证失败";
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticating, "manual-retry");
        ok &= m.TrySetAuth(ConnectionStates.AuthState.AuthenticatedBySession, "online");
        ok &= m.PillText == "已联网·会话";

        // 操作结果不变量：保存/删除/服务切换成功不得改变连接状态
        ConnectionStates.AuthState before = m.Auth;
        m.RecordOperation(ConnectionStates.OpResult.Success, "save-credentials");
        m.RecordOperation(ConnectionStates.OpResult.Success, "forget-credentials");
        m.RecordOperation(ConnectionStates.OpResult.Warning, "service-transition");
        ok &= m.Auth == before;

        // 非法迁移必须被记录（供诊断）
        ok &= m.RejectedTransitions.Count >= 2;
        return ok;
    }

    private static int ObserveTests()
    {
        try
        {
            Console.WriteLine("ServiceInstalled=" + ServiceInstalled());
            Console.WriteLine("ServiceIdentity=" + IsExpectedService());
            Console.WriteLine("OtherClients=" + ClientCount());
            Console.WriteLine("DirectIdle=" + DirectAdaptersIdle());
            Console.WriteLine("ConnectAllowed=" + (CheckBeforeConnect() == null));
            return 0;
        }
        catch (Exception e) { Console.WriteLine("OBSERVE_ERROR " + e.GetType().Name); return 1; }
    }

    private static SavedCredential DummyCredential()
    {
        return new SavedCredential { Username = "测试12", Password = "dummy-Secret-\"\\-中文-987", Channel = 3 };
    }

    private static bool SameCredential(SavedCredential a, SavedCredential b)
    {
        return a != null && b != null && a.Username == b.Username && a.Password == b.Password && a.Channel == b.Channel;
    }

    private static int CredentialReadTest(string dir)
    {
        try { return SameCredential(new CredentialStore(dir).Load(), DummyCredential()) ? 0 : 1; }
        catch { return 1; }
    }

    private static bool ContainsBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return false;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            int j = 0;
            while (j < needle.Length && haystack[i + j] == needle[j]) j++;
            if (j == needle.Length) return true;
        }
        return false;
    }

    private static int CredentialTests()
    {
        string dir = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ChezhiCredTest-" + Guid.NewGuid().ToString("N"));
        var store = new CredentialStore(dir);
        try
        {
            SavedCredential value = DummyCredential();
            store.Save(value);
            byte[] bytes = File.ReadAllBytes(store.FilePath);
            if (ContainsBytes(bytes, Encoding.UTF8.GetBytes(value.Username)) ||
                ContainsBytes(bytes, Encoding.UTF8.GetBytes(value.Password)) ||
                ContainsBytes(bytes, Encoding.Unicode.GetBytes(value.Username)) ||
                ContainsBytes(bytes, Encoding.Unicode.GetBytes(value.Password)) ||
                !SameCredential(store.Load(), value)) return 1;
            var start = new ProcessStartInfo {
                FileName = Application.ExecutablePath,
                Arguments = "--self-test-credential-read \"" + dir + "\"",
                UseShellExecute = false, CreateNoWindow = true
            };
            using (var child = Process.Start(start))
            {
                if (child == null || !child.WaitForExit(10000) || child.ExitCode != 0) return 1;
            }
            value.Password = "another-dummy-password";
            store.Save(value);
            if (!SameCredential(store.Load(), value)) return 1;
            byte[] invalid = Encoding.ASCII.GetBytes("UNKNOWN-RECORD");
            File.WriteAllBytes(store.FilePath, invalid);
            bool refused = false;
            try { store.Save(value); } catch { refused = true; }
            if (!refused || !File.ReadAllBytes(store.FilePath).SequenceEqual(invalid)) return 1;
            Console.WriteLine("CREDENTIALS_PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("CREDENTIALS_ERROR " + e.GetType().Name); return 1; }
        finally
        {
            try { if (File.Exists(store.FilePath)) File.Delete(store.FilePath); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { }
        }
    }

    private static int UiTests()
    {
        string dir = Path.Combine(Path.GetFullPath(Path.GetTempPath()), "ChezhiUiTest-" + Guid.NewGuid().ToString("N"));
        var store = new CredentialStore(dir);
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            using (var first = new MainWindow(dir, true))
            {
                first.Show(); Application.DoEvents();
                if (!first.CheckTrayAndSaveForTest()) return 1;
            }
            using (var second = new MainWindow(dir, true))
            {
                second.Show(); Application.DoEvents();
                if (!second.CheckCredentialReloadForTest()) return 1;
            }
            // P10/A06：崩溃注入——核心崩溃钩子模式下验证"有界自动恢复"被正确调度
            using (var third = new MainWindow(dir, true))
            {
                third.Show(); Application.DoEvents();
                if (!third.CheckAutoRetryForTest()) return 1;
            }
            Console.WriteLine("UI_TRAY_PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("UI_TEST_ERROR " + e.GetType().Name); return 1; }
        finally
        {
            CleanupCore(); // 崩溃注入测试释放过核心，统一清理
            try { if (File.Exists(store.FilePath)) File.Delete(store.FilePath); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { }
        }
    }

    private static int StubTestChild()
    {
        string line;
        while ((line = Console.ReadLine()) != null) if (line == "STOP") return 0;
        return 0;
    }

    private static int CoreTest(bool stop)
    {
        Process p = null;
        try
        {
            ExtractCore();
            var accepted = new System.Threading.ManualResetEvent(false);
            // 结构化事件（协议 v1）端到端断言：probe 模式必须至少收到一行 EPHEMERAL_EVENT
            var sawEvent = new System.Threading.ManualResetEvent(false);
            p = LaunchCore(stop ? "--ephemeral-probe" : "--ephemeral-check", "dummy-user", "dummy-pass", 1,
                delegate(Process sender, string value)
                {
                    if (value == "EPHEMERAL_STOP_ACCEPTED") accepted.Set();
                    if (value != null && value.StartsWith("EPHEMERAL_EVENT ", StringComparison.Ordinal)) sawEvent.Set();
                });
            bool ok;
            if (stop)
            {
                System.Threading.Thread.Sleep(500);
                ok = !p.HasExited && RequestStop(p) && accepted.WaitOne(5000) && sawEvent.WaitOne(5000) && p.WaitForExit(5000) && p.ExitCode == 0;
            }
            else ok = p.WaitForExit(10000) && p.ExitCode == 0;
            Console.WriteLine(ok ? (stop ? "STOP_PASS" : "CORE_PASS") : "CORE_FAIL");
            return ok ? 0 : 1;
        }
        catch (Exception e) { Console.WriteLine("CORE_ERROR " + e.GetType().Name); return 1; }
        finally
        {
            if (p != null) { try { if (!p.HasExited) { p.StandardInput.Close(); p.WaitForExit(10000); } } catch { } p.Dispose(); }
            CleanupCore();
        }
    }

    private sealed class MainWindow : Form
    {
        private static readonly Color FormBgColor = Color.FromArgb(246, 248, 250);
        private static readonly Color CardBgColor = Color.White;

        private readonly TextBox account = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly ComboBox channel = new ComboBox();
        private readonly CheckBox remember = new CheckBox();
        private readonly ModernButton connect = new ModernButton();
        private readonly ModernButton stop = new ModernButton();
        private readonly ModernButton save = new ModernButton();
        private readonly ModernButton reveal = new ModernButton();
        private readonly ModernButton prepare = new ModernButton();
        private readonly ModernButton restore = new ModernButton();
        private readonly ModernButton hotspot = new ModernButton();
        private readonly Label status = new Label();
        private readonly Timer timer = new Timer();
        private readonly NotifyIcon tray = new NotifyIcon();
        private readonly ContextMenuStrip trayMenu = new ContextMenuStrip();
        private readonly CredentialStore credentials;
        private readonly ConnectionState state = new ConnectionState();
        private readonly bool isSelfTest;
        // 核心结构化事件归档（EPHEMERAL_EVENT v1，协议见 docs/EphemeralExperimental.md）：
        // 环形上限 200 条，供诊断导出；畸形/超长行截断后原样保留，永不影响 UI 流程。
        private readonly List<string> coreEvents = new List<string>();
        private string credentialLoadError;
        private bool loadedCredential;
        private Process running;
        private bool stopping, closingAfterStop, serviceBusy, exitRequested;
        // —— 有界自动恢复（A06，不变量：用户主动停止/退出后绝不自动重启）——
        private bool userStopped;      // 用户主动停止或退出：取消一切待执行重试
        private bool runSawProgress;   // 本轮核心是否到达过"等待网络/认证成功/已联网"（未到达即失败=疑似凭据问题，不自动重试）
        private bool authFailureSeen;  // 本轮收到过 AUTH_FAILED（受控失败，退出不算崩溃，也不自动重试）
        private int autoRetryCount;   // 自动重试计数（每次用户发起的连接最多 2 次：10s、40s 指数退避）
        private readonly Timer retryTimer = new Timer();
        private Stopwatch grace;
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer animTimer = new Timer();
        private readonly StatusDot dot = new StatusDot();
        private readonly StatusPill statusPill = new StatusPill();
        private readonly StatusCard statusCard = new StatusCard();
        private readonly TableLayoutPanel statusRow = new TableLayoutPanel();

        // 状态指示配色: 灰=空闲/提示, 蓝=进行中, 绿=成功, 橙=需注意, 红=失败
        private static readonly Color ToneInfo = Color.FromArgb(100, 116, 139);
        private static readonly Color ToneBusy = Color.FromArgb(30, 96, 213);
        private static readonly Color ToneOk = Color.FromArgb(22, 163, 74);
        private static readonly Color ToneWarn = Color.FromArgb(217, 119, 6);
        private static readonly Color ToneFail = Color.FromArgb(220, 38, 38);

        internal MainWindow(string credentialDirectory = null, bool selfTest = false)
        {
            isSelfTest = selfTest;
            Text = "澈知科技 - 校园网连接";
            Icon appIcon = null;
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (appIcon != null) Icon = appIcon;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(540, 532);
            MinimumSize = new Size(520, 532);
            BackColor = FormBgColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;

            var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 16, 20, 14), BackColor = FormBgColor };
            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = FormBgColor };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); // Header
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // Spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 246)); // Auth Card
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // Spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 88)); // Tool Card
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // Spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); // Status Card
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 10)); // Spacer
            layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // Footer

            // Header
            var header = new HeaderPanel { Dock = DockStyle.Fill, BackColor = FormBgColor };
            statusPill.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            statusPill.Location = new Point(540 - 20 * 2 - statusPill.Width - 2, (52 - statusPill.Height) / 2);
            header.Controls.Add(statusPill);
            header.Resize += delegate { statusPill.Location = new Point(header.Width - statusPill.Width - 2, (header.Height - statusPill.Height) / 2); };
            layout.Controls.Add(header, 0, 0);

            // Auth Card
            var authCard = new ModernCard { Dock = DockStyle.Fill, BackColor = CardBgColor };
            var authLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16, 12, 16, 12), BackColor = CardBgColor, Margin = Padding.Empty };
            authLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 22)); // Card Title
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // Account
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // Password
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 36)); // Device
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); // Remember
            authLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 46)); // Actions

            var authHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, BackColor = CardBgColor };
            authHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            authHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
            var authTitle = new Label { Text = "账号与网络认证", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft, BackColor = CardBgColor };
            var authSubtitle = new Label { Text = "DPAPI 加密保护", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8F), ForeColor = Color.FromArgb(148, 163, 184), TextAlign = ContentAlignment.MiddleRight, BackColor = CardBgColor };
            authHeader.Controls.Add(authTitle, 0, 0); authHeader.Controls.Add(authSubtitle, 1, 0);
            authLayout.Controls.Add(authHeader, 0, 0);

            // Account field
            account.BorderStyle = BorderStyle.None; account.Font = new Font("Microsoft YaHei UI", 10F); account.Dock = DockStyle.Fill; account.BackColor = CardBgColor;
            var accountBox = new InputContainer(account) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(10, 7, 10, 4), BackColor = CardBgColor };
            AddField(authLayout, "账号", accountBox, 1);

            // Password field
            password.UseSystemPasswordChar = true; password.BorderStyle = BorderStyle.None; password.Font = new Font("Microsoft YaHei UI", 10F); password.Dock = DockStyle.Fill; password.Margin = new Padding(0, 5, 4, 0); password.BackColor = CardBgColor;
            reveal.Text = "显示"; reveal.Kind = ButtonKind.Normal; reveal.CornerRadius = 4; reveal.Font = new Font("Microsoft YaHei UI", 8.5F); reveal.Dock = DockStyle.Fill; reveal.Margin = new Padding(2, 2, 4, 2);
            var passInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, BackColor = CardBgColor };
            passInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            passInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            passInner.Controls.Add(password, 0, 0); passInner.Controls.Add(reveal, 1, 0);
            var passwordBox = new InputContainer(passInner) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(10, 2, 2, 2), BackColor = CardBgColor };
            AddField(authLayout, "密码", passwordBox, 2);

            // Channel field
            channel.DropDownStyle = ComboBoxStyle.DropDownList; channel.FlatStyle = FlatStyle.Flat; channel.Font = new Font("Microsoft YaHei UI", 9.5F); channel.Dock = DockStyle.Fill;
            channel.Items.AddRange(new object[] { "Windows", "Linux", "Android", "iOS", "macOS" }); channel.SelectedIndex = 0;
            var channelBox = new InputContainer(channel) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(6, 4, 6, 4), BackColor = CardBgColor };
            AddField(authLayout, "设备类型", channelBox, 3);

            // 记住账号密码：仅用户明确勾选后才持久化（连接成功后写入）；默认不勾选。
            // 勾选状态与本地已有记录保持一致；取消勾选不会删除已有记录（用托盘菜单清除）。
            remember.Text = "记住账号密码（DPAPI 加密，仅当前 Windows 用户可读）";
            remember.AutoSize = false; remember.Size = new Size(480, 20); remember.Dock = DockStyle.Fill;
            remember.ForeColor = Color.FromArgb(71, 85, 105); remember.BackColor = CardBgColor;
            remember.TextAlign = ContentAlignment.MiddleLeft; remember.Cursor = Cursors.Hand;
            tips.SetToolTip(remember, "不勾选时凭据只在内存中使用，连接成功也不会写入磁盘");
            authLayout.Controls.Add(remember, 0, 4);

            // Connection action buttons row
            var actionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 6, 0, 0), BackColor = CardBgColor };
            actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 44));
            actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            actionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 28));
            ConfigureButton(connect, "立即连接", ButtonKind.Primary);
            connect.CornerRadius = 6; connect.Font = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold); connect.Dock = DockStyle.Fill; connect.Margin = new Padding(0, 0, 8, 0);
            ConfigureButton(stop, "停止认证", ButtonKind.Danger);
            stop.CornerRadius = 6; stop.Dock = DockStyle.Fill; stop.Margin = new Padding(0, 0, 8, 0); stop.Enabled = false;
            ConfigureButton(save, "保存账号", ButtonKind.Normal);
            save.CornerRadius = 6; save.Dock = DockStyle.Fill; save.Margin = Padding.Empty;
            actionGrid.Controls.Add(connect, 0, 0); actionGrid.Controls.Add(stop, 1, 0); actionGrid.Controls.Add(save, 2, 0);
            authLayout.Controls.Add(actionGrid, 0, 5);
            authCard.Controls.Add(authLayout);
            layout.Controls.Add(authCard, 0, 2);

            // Tool Card
            var toolCard = new ModernCard { Dock = DockStyle.Fill, BackColor = CardBgColor };
            var toolLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(16, 10, 16, 10), BackColor = CardBgColor, Margin = Padding.Empty };
            toolLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 20)); // Title
            toolLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38)); // Buttons

            var toolHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Margin = Padding.Empty, BackColor = CardBgColor };
            toolHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            var toolTitle = new Label { Text = "系统服务与移动热点", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft, BackColor = CardBgColor };
            var toolSubtitle = new Label { Text = "切换天翼后台或设置网络共享", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8F), ForeColor = Color.FromArgb(148, 163, 184), TextAlign = ContentAlignment.MiddleRight, BackColor = CardBgColor };
            toolHeader.Controls.Add(toolTitle, 0, 0); toolHeader.Controls.Add(toolSubtitle, 1, 0);
            toolLayout.Controls.Add(toolHeader, 0, 0);

            var toolGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 0), BackColor = CardBgColor };
            toolGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            toolGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.33f));
            toolGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.34f));
            ConfigureButton(prepare, "准备连接", ButtonKind.Normal); prepare.CornerRadius = 6; prepare.Dock = DockStyle.Fill; prepare.Margin = new Padding(0, 0, 8, 0);
            ConfigureButton(restore, "恢复天翼", ButtonKind.Normal); restore.CornerRadius = 6; restore.Dock = DockStyle.Fill; restore.Margin = new Padding(0, 0, 8, 0);
            ConfigureButton(hotspot, "热点设置", ButtonKind.Normal); hotspot.CornerRadius = 6; hotspot.Dock = DockStyle.Fill; hotspot.Margin = Padding.Empty;
            toolGrid.Controls.Add(prepare, 0, 0); toolGrid.Controls.Add(restore, 1, 0); toolGrid.Controls.Add(hotspot, 2, 0);
            toolLayout.Controls.Add(toolGrid, 0, 1);
            toolCard.Controls.Add(toolLayout);
            layout.Controls.Add(toolCard, 0, 4);

            // Status Card
            statusCard.Dock = DockStyle.Fill;
            status.Text = "输入校园网账号和密码，点击“立即连接”即可。";
            status.Dock = DockStyle.Fill; status.ForeColor = Color.FromArgb(51, 65, 85); status.AutoEllipsis = true; status.TextAlign = ContentAlignment.MiddleLeft; status.Font = new Font("Microsoft YaHei UI", 9F);
            statusRow.Dock = DockStyle.Fill; statusRow.ColumnCount = 2; statusRow.RowCount = 1; statusRow.Margin = Padding.Empty; statusRow.Padding = new Padding(10, 0, 10, 0);
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dot.Size = new Size(18, 18); dot.Margin = new Padding(2, 13, 0, 0);
            statusRow.Controls.Add(dot, 0, 0); statusRow.Controls.Add(status, 1, 0);
            statusCard.Controls.Add(statusRow);
            statusCard.SetTone(ToneInfo);
            layout.Controls.Add(statusCard, 0, 6);

            // Footer
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(4, 0, 4, 0), BackColor = FormBgColor };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            var about = new LinkLabel { Text = "使用说明与开源许可", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, LinkColor = Color.FromArgb(37, 99, 235), ActiveLinkColor = Color.FromArgb(29, 78, 216), LinkBehavior = LinkBehavior.HoverUnderline, BackColor = FormBgColor };
            var exit = new LinkLabel { Text = "退出程序", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, LinkColor = Color.FromArgb(100, 116, 139), ActiveLinkColor = Color.FromArgb(220, 38, 38), LinkBehavior = LinkBehavior.HoverUnderline, BackColor = FormBgColor };
            footer.Controls.Add(about, 0, 0); footer.Controls.Add(exit, 1, 0);
            layout.Controls.Add(footer, 0, 8);

            root.Controls.Add(layout);
            Controls.Add(root);
            AcceptButton = connect;

            try
            {
                credentials = new CredentialStore(credentialDirectory ?? CredentialStore.DefaultDirectory());
                SavedCredential saved = credentials.Load();
                if (saved != null)
                {
                    account.Text = saved.Username; password.Text = saved.Password;
                    channel.SelectedIndex = saved.Channel - 1; loadedCredential = true;
                    remember.Checked = true; // 与本地已有记录保持一致
                }
            }
            catch { credentialLoadError = "读取已保存的账号失败，原有文件未改动。请确认与保存时使用同一个 Windows 用户。"; }

            tray.Icon = appIcon != null ? appIcon : SystemIcons.Application; // 复用同一个 Icon, 关闭时统一释放
            tray.Text = "澈知科技 - 校园网";
            trayMenu.Items.Add("打开主窗口", null, delegate { ShowFromTray(); });
            trayMenu.Items.Add("停止认证", null, delegate { ShowFromTray(); StopClicked(stop, EventArgs.Empty); });
            trayMenu.Items.Add("清除已保存账号", null, delegate { ShowFromTray(); ForgetCredentials(); });
            trayMenu.Items.Add("导出诊断信息", null, delegate { ShowFromTray(); ExportDiagnostics(); });
            trayMenu.Items.Add(new ToolStripSeparator());
            trayMenu.Items.Add("退出程序", null, delegate { ShowFromTray(); exitRequested = true; Close(); });
            tray.ContextMenuStrip = trayMenu;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.Visible = true;

            tips.AutoPopDelay = 6000; tips.InitialDelay = 400; tips.ReshowDelay = 150;
            tips.SetToolTip(account, "校园网账号，通常是学号或手机号");
            tips.SetToolTip(password, "校园网密码；保存时用 Windows 账户加密，仅本人可读");
            tips.SetToolTip(reveal, "显示或隐藏密码");
            tips.SetToolTip(channel, "选择正在认证的设备类型，电脑选 Windows 即可");
            tips.SetToolTip(connect, "连接校园网（按回车同样有效）");
            tips.SetToolTip(stop, "让认证程序登出并退出");
            tips.SetToolTip(save, "加密保存账号和密码");
            tips.SetToolTip(prepare, "停用天翼后台服务，切换为本程序认证（需管理员确认）");
            tips.SetToolTip(restore, "重新启用天翼后台服务并打开官方客户端");
            tips.SetToolTip(hotspot, "打开 Windows 移动热点设置");

            animTimer.Interval = 50;
            animTimer.Tick += delegate
            {
                if (dot.IsDisposed) { animTimer.Stop(); return; }
                double phase = (Environment.TickCount % 1400) / 1400.0 * 2.0 * Math.PI;
                dot.SetPulse((float)(0.5 + 0.5 * Math.Sin(phase)));
            };

            connect.Click += ConnectClicked;
            stop.Click += StopClicked;
            retryTimer.Tick += RetryTimerTick;
            // 系统睡眠/休眠唤醒：不自动重启认证（核心自身的心跳/退避循环会重新确认），
            // 仅提示状态待确认；用户停止过的会话保持停止
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            // 锁屏即自动遮罩密码（A19/6.2：失焦/锁屏后的密码暴露面收敛）
            SystemEvents.SessionSwitch += OnSessionSwitch;
            AcceptButton = connect; // 回车=立即连接（连接中自动失效，因按钮禁用）
            save.Click += delegate { SaveCredentials(true); };
            reveal.Click += delegate {
                password.UseSystemPasswordChar = !password.UseSystemPasswordChar;
                reveal.Text = password.UseSystemPasswordChar ? "显示" : "隐藏";
            };
            prepare.Click += async delegate { await RunServiceAction(true); };
            restore.Click += async delegate { await RunServiceAction(false); };
            hotspot.Click += delegate {
                try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:network-mobilehotspot", UseShellExecute = true }); }
                catch { SetStatus("打不开热点设置，请手动前往：Windows 设置 → 网络和 Internet → 移动热点。", ToneWarn); }
            };
            about.LinkClicked += delegate { ShowAbout(); };
            exit.LinkClicked += delegate { exitRequested = true; Close(); };
            Shown += delegate {
                // A11：WMI 检查（冷启动可达数秒）移出 UI 线程，先给即时提示
                if (credentialLoadError != null) SetStatus(credentialLoadError, ToneWarn);
                else if (loadedCredential) SetStatus("已载入保存的账号，正在检查天翼服务状态……", ToneOk);
                else SetStatus("正在检查天翼服务状态……", ToneInfo);
                RunPreConnectCheckAsync(false);
                if (!isSelfTest) PromptPendingServiceRecovery(); // A09：识别未完成的服务切换
            };
            timer.Interval = 1000;
            timer.Tick += delegate {
                if (stopping && running != null && !running.HasExited && grace.Elapsed.TotalSeconds >= 100)
                {
                    timer.Stop(); stop.Enabled = true; stop.Text = "强制结束"; StyleStopArmed(true);
                    SetStatus("等待停止已超过 100 秒。可点“强制结束”，服务器上的会话可能稍后才释放。", ToneFail);
                }
            };
            FormClosing += OnClosing;
            FormClosed += delegate {
                animTimer.Stop(); retryTimer.Stop();
                password.Clear(); tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); tips.Dispose();
                timer.Dispose(); animTimer.Dispose(); retryTimer.Dispose(); if (running != null) running.Dispose();
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                SystemEvents.SessionSwitch -= OnSessionSwitch;
                if (appIcon != null) appIcon.Dispose(); // ExtractAssociatedIcon 的实例由本窗口统一释放
            };
        }

        internal void ShowFromTray()
        {
            if (IsDisposed) return;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            BringToFront(); Activate();
        }

        private void HideToTray()
        {
            password.UseSystemPasswordChar = true; reveal.Text = "显示";
            Hide();
            if (!isSelfTest) try { tray.ShowBalloonTip(1500, "澈知科技", "已最小化到托盘，程序仍在运行；双击图标可重新打开。", ToolTipIcon.Info); } catch { }
        }

        internal bool CheckTrayAndSaveForTest()
        {
            SavedCredential dummy = DummyCredential();
            if (!password.UseSystemPasswordChar || reveal.Text != "显示") return false;
            account.Text = dummy.Username; password.Text = dummy.Password; channel.SelectedIndex = dummy.Channel - 1;
            reveal.PerformClick();
            if (password.UseSystemPasswordChar || reveal.Text != "隐藏") return false;
            if (!SaveCredentials(false)) return false;
            var info = new ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = "--self-test-stub", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true };
            Process stub = null;
            try
            {
                stub = Process.Start(info);
                if (stub == null) return false;
                running = stub;
                Close(); Application.DoEvents();
                if (Visible || IsDisposed || !tray.Visible || stub.HasExited || !password.UseSystemPasswordChar || reveal.Text != "显示") return false;
                ShowFromTray(); Application.DoEvents();
                if (!Visible || !tray.Visible || stub.HasExited) return false;
                if (!RequestStop(stub) || !stub.WaitForExit(5000) || stub.ExitCode != 0) return false;
                running = null;
                trayMenu.Items[trayMenu.Items.Count - 1].PerformClick(); Application.DoEvents();
                return IsDisposed;
            }
            finally
            {
                running = null;
                if (stub != null)
                {
                    try { if (!stub.HasExited) { RequestStop(stub); if (!stub.WaitForExit(5000)) stub.Kill(); } } catch { }
                    stub.Dispose();
                }
            }
        }

        internal bool CheckCredentialReloadForTest()
        {
            SavedCredential dummy = DummyCredential();
            bool ok = loadedCredential && account.Text == dummy.Username && password.Text == dummy.Password &&
                channel.SelectedIndex + 1 == dummy.Channel && password.UseSystemPasswordChar && reveal.Text == "显示";
            exitRequested = true; Close(); Application.DoEvents();
            return ok && IsDisposed;
        }

        // P10/A06 崩溃注入验证：以 --ephemeral-crash 钩子启动（发 WAIT_NETWORK 后非零退出），
        // 断言 OnCoreExit 将其判定为崩溃并调度了有界自动重试（第 1/2 次、10 秒档）。
        // 全程离线，无任何网络探测与真实认证。
        internal bool CheckAutoRetryForTest()
        {
            ExtractCore(); // 本测试要真实启动核心（--self-test-ui 主入口不预释放核心）
            account.Text = "retry-user"; password.Text = "retry-pass";
            userStopped = false; runSawProgress = false; authFailureSeen = false; autoRetryCount = 0;
            StartAuthProcess("--ephemeral-crash");
            if (running == null) return false;
            for (int i = 0; i < 80 && !running.HasExited; i++) { System.Threading.Thread.Sleep(100); Application.DoEvents(); }
            if (!running.HasExited) { userStopped = true; retryTimer.Stop(); return false; }
            // 泵消息让 Exited 回调（BeginInvoke）落地并执行 OnCoreExit → ScheduleAutoRetry
            for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            bool ok = autoRetryCount == 1 && retryTimer.Enabled;
            // 清理：取消重试，模拟用户停止（不变量验证：停止后重试被取消）
            userStopped = true; retryTimer.Stop();
            try { if (running != null) { running.Dispose(); running = null; } } catch { }
            exitRequested = true; Close(); Application.DoEvents();
            return ok && IsDisposed && !retryTimer.Enabled;
        }

        private bool SaveCredentials(bool announce)
        {
            if (account.Text.Length == 0 || password.Text.Length == 0)
            {
                SetStatus("请输入校园网账号和密码。", ToneWarn);
                if (announce && !isSelfTest) MessageBox.Show(this, status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return false;
            }
            if (credentials == null)
            {
                SetStatus("无法访问 Windows 用户配置目录，未保存账号。", ToneFail);
                if (announce && !isSelfTest) MessageBox.Show(this, status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            try
            {
                credentials.Save(new SavedCredential { Username = account.Text, Password = password.Text, Channel = channel.SelectedIndex + 1 });
                credentialLoadError = null; loadedCredential = true;
                if (announce) SetStatus("已加密保存，仅当前 Windows 用户可读取。", ToneOk);
                return true;
            }
            catch (InvalidDataException)
            {
                SetStatus("保存失败：账号过长，或旧记录无法读取（可先用托盘菜单“清除已保存账号”处理）。", ToneFail);
            }
            catch { SetStatus("加密保存失败，原有记录未改动。", ToneFail); }
            if (announce && !isSelfTest) MessageBox.Show(this, status.Text, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }

        private void ForgetCredentials()
        {
            if (credentials == null) { SetStatus("无法访问已保存的账号。", ToneWarn); return; }
            if (MessageBox.Show(this, "确定删除已保存的账号和密码？（不影响当前连接）", "清除已保存账号", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            try
            {
                credentials.Forget(); account.Clear(); password.Clear();
                password.UseSystemPasswordChar = true; reveal.Text = "显示";
                credentialLoadError = null; loadedCredential = false;
                SetStatus("已删除保存的账号密码。", ToneOk);
            }
            catch { SetStatus("删除失败：文件内容异常，为安全起见未做改动。", ToneFail); }
        }

        private static void AddField(TableLayoutPanel layout, string text, Control field, int row)
        {
            var rowTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = CardBgColor };
            rowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            rowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = new Label { Text = text, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Microsoft YaHei UI", 9.5F), BackColor = CardBgColor };
            field.Dock = DockStyle.Fill;
            rowTable.Controls.Add(label, 0, 0);
            rowTable.Controls.Add(field, 1, 0);
            layout.Controls.Add(rowTable, 0, row);
        }

        private enum ButtonKind { Primary, Normal, Danger, DangerFilled }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (radius <= 0)
            {
                path.AddRectangle(rect);
                return path;
            }
            int d = radius * 2;
            if (d > rect.Width) d = rect.Width;
            if (d > rect.Height) d = rect.Height;
            var arc = new Rectangle(rect.X, rect.Y, d, d);
            path.AddArc(arc, 180, 90);
            arc.X = rect.Right - d;
            path.AddArc(arc, 270, 90);
            arc.Y = rect.Bottom - d;
            path.AddArc(arc, 0, 90);
            arc.X = rect.X;
            path.AddArc(arc, 90, 90);
            path.CloseFigure();
            return path;
        }

        private sealed class ModernButton : Button
        {
            private bool isHovered;
            private bool isPressed;
            private int cornerRadius = 6;
            private ButtonKind kind = ButtonKind.Normal;

            public ModernButton()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Cursor = Cursors.Hand;
                Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Regular);
            }

            public ButtonKind Kind
            {
                get { return kind; }
                set { kind = value; Invalidate(); }
            }

            public int CornerRadius
            {
                get { return cornerRadius; }
                set { cornerRadius = value; Invalidate(); }
            }

            protected override void OnMouseEnter(EventArgs e) { isHovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { isHovered = false; isPressed = false; Invalidate(); base.OnMouseLeave(e); }
            protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { isPressed = true; Invalidate(); } base.OnMouseDown(e); }
            protected override void OnMouseUp(MouseEventArgs e) { isPressed = false; Invalidate(); base.OnMouseUp(e); }
            protected override void OnEnabledChanged(EventArgs e) { Cursor = Enabled ? Cursors.Hand : Cursors.Default; Invalidate(); base.OnEnabledChanged(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : CardBgColor;
                e.Graphics.Clear(parentColor);

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                Color back, border, text;
                bool drawBorder = true;

                if (!Enabled)
                {
                    back = Color.FromArgb(243, 244, 246);
                    border = Color.FromArgb(229, 231, 235);
                    text = Color.FromArgb(156, 163, 175);
                }
                else
                {
                    switch (kind)
                    {
                        case ButtonKind.Primary:
                            back = isPressed ? Color.FromArgb(23, 78, 174) : (isHovered ? Color.FromArgb(43, 114, 238) : Color.FromArgb(30, 96, 213));
                            border = back;
                            drawBorder = false;
                            text = Color.White;
                            break;
                        case ButtonKind.Danger:
                            back = isPressed ? Color.FromArgb(254, 202, 202) : (isHovered ? Color.FromArgb(254, 226, 226) : Color.FromArgb(255, 245, 245));
                            border = isHovered ? Color.FromArgb(248, 113, 113) : Color.FromArgb(252, 165, 165);
                            text = Color.FromArgb(220, 38, 38);
                            break;
                        case ButtonKind.DangerFilled:
                            back = isPressed ? Color.FromArgb(185, 28, 28) : (isHovered ? Color.FromArgb(239, 68, 68) : Color.FromArgb(220, 38, 38));
                            border = back;
                            drawBorder = false;
                            text = Color.White;
                            break;
                        default: // Normal
                            back = isPressed ? Color.FromArgb(229, 231, 235) : (isHovered ? Color.FromArgb(243, 244, 246) : Color.White);
                            border = isHovered ? Color.FromArgb(148, 163, 184) : Color.FromArgb(209, 213, 219);
                            text = Color.FromArgb(51, 65, 85);
                            break;
                    }
                }

                using (var path = CreateRoundedPath(rect, cornerRadius))
                {
                    using (var brush = new SolidBrush(back)) e.Graphics.FillPath(brush, path);
                    if (drawBorder)
                    {
                        using (var pen = new Pen(border, 1f)) e.Graphics.DrawPath(pen, path);
                    }
                }

                TextRenderer.DrawText(e.Graphics, Text, Font, rect, text,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private sealed class ModernCard : Panel
        {
            public ModernCard()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = CardBgColor;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : FormBgColor;
                e.Graphics.Clear(parentColor);

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = CreateRoundedPath(rect, 10))
                {
                    using (var brush = new SolidBrush(CardBgColor)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(226, 232, 240), 1f)) e.Graphics.DrawPath(pen, path);
                }
            }
        }

        private sealed class InputContainer : Panel
        {
            private bool isFocused;
            private bool isHovered;

            public InputContainer(Control child)
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = CardBgColor;
                Controls.Add(child);
                AttachEvents(child);
            }

            private void AttachEvents(Control c)
            {
                c.MouseEnter += delegate { isHovered = true; Invalidate(); };
                c.MouseLeave += delegate { isHovered = false; Invalidate(); };
                c.Enter += delegate { isFocused = true; Invalidate(); };
                c.Leave += delegate { isFocused = false; Invalidate(); };
                foreach (Control sub in c.Controls) AttachEvents(sub);
            }

            protected override void OnMouseEnter(EventArgs e) { isHovered = true; Invalidate(); base.OnMouseEnter(e); }
            protected override void OnMouseLeave(EventArgs e) { isHovered = false; Invalidate(); base.OnMouseLeave(e); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : CardBgColor;
                e.Graphics.Clear(parentColor);

                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = CreateRoundedPath(rect, 6))
                {
                    using (var brush = new SolidBrush(CardBgColor)) e.Graphics.FillPath(brush, path);

                    Color border;
                    float penWidth = 1f;
                    if (isFocused) { border = Color.FromArgb(30, 96, 213); penWidth = 1.5f; }
                    else if (isHovered) { border = Color.FromArgb(148, 163, 184); }
                    else { border = Color.FromArgb(209, 213, 219); }

                    using (var pen = new Pen(border, penWidth)) e.Graphics.DrawPath(pen, path);
                }
            }
        }

        private sealed class StatusCard : Panel
        {
            private Color currentTone = ToneInfo;

            public StatusCard()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                BackColor = FormBgColor;
            }

            public Color GetBgColor()
            {
                if (currentTone == ToneOk) return Color.FromArgb(240, 253, 244);
                if (currentTone == ToneBusy) return Color.FromArgb(239, 246, 255);
                if (currentTone == ToneWarn) return Color.FromArgb(255, 251, 235);
                if (currentTone == ToneFail) return Color.FromArgb(254, 242, 242);
                return Color.FromArgb(248, 250, 252);
            }

            public Color GetBorderColor()
            {
                if (currentTone == ToneOk) return Color.FromArgb(187, 247, 208);
                if (currentTone == ToneBusy) return Color.FromArgb(191, 219, 254);
                if (currentTone == ToneWarn) return Color.FromArgb(253, 230, 138);
                if (currentTone == ToneFail) return Color.FromArgb(254, 202, 202);
                return Color.FromArgb(226, 232, 240);
            }

            public void SetTone(Color tone)
            {
                currentTone = tone;
                Color bg = GetBgColor();
                foreach (Control c in Controls)
                {
                    c.BackColor = bg;
                    foreach (Control sub in c.Controls) sub.BackColor = bg;
                }
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : FormBgColor;
                e.Graphics.Clear(parentColor);
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);

                using (var path = CreateRoundedPath(rect, 8))
                {
                    using (var brush = new SolidBrush(GetBgColor())) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(GetBorderColor(), 1f)) e.Graphics.DrawPath(pen, path);
                }
            }
        }

        private sealed class StatusPill : Control
        {
            private Color color = ToneInfo;
            private string text = "未连接";

            public StatusPill()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Size = new Size(96, 28);
                BackColor = FormBgColor;
            }

            public void UpdateStatus(string pillText, Color pillColor)
            {
                text = pillText;
                color = pillColor;
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : FormBgColor;
                e.Graphics.Clear(parentColor);
                var rect = new Rectangle(0, 0, Width - 1, Height - 1);
                using (var path = CreateRoundedPath(rect, Height / 2))
                {
                    Color bg = Color.FromArgb(24, color.R, color.G, color.B);
                    using (var brush = new SolidBrush(bg)) e.Graphics.FillPath(brush, path);
                    using (var pen = new Pen(Color.FromArgb(90, color.R, color.G, color.B), 1.2f)) e.Graphics.DrawPath(pen, path);
                }

                int dotSize = 6;
                int dotX = 12;
                int dotY = (Height - dotSize) / 2;
                using (var dotBrush = new SolidBrush(color))
                {
                    e.Graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                }

                var textRect = new Rectangle(dotX + dotSize + 6, 0, Width - (dotX + dotSize + 10), Height);
                using (var font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, text, font, textRect, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                }
            }
        }

        private sealed class HeaderPanel : Panel
        {
            public HeaderPanel()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Height = 52;
                Dock = DockStyle.Fill;
                BackColor = FormBgColor;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : FormBgColor;
                e.Graphics.Clear(parentColor);

                int badgeSize = 42;
                int badgeY = (Height - badgeSize) / 2;
                var badgeRect = new Rectangle(0, badgeY, badgeSize, badgeSize);
                using (var path = CreateRoundedPath(badgeRect, 10))
                using (var brush = new LinearGradientBrush(badgeRect, Color.FromArgb(30, 96, 213), Color.FromArgb(20, 70, 180), 45f))
                {
                    e.Graphics.FillPath(brush, path);
                }

                using (var pen = new Pen(Color.White, 2.2f))
                {
                    pen.StartCap = LineCap.Round;
                    pen.EndCap = LineCap.Round;
                    int cx = badgeRect.X + badgeSize / 2;
                    int cy = badgeRect.Y + badgeSize / 2 + 7;
                    using (var dotBrush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(dotBrush, cx - 2, cy - 2, 4, 4);
                    e.Graphics.DrawArc(pen, cx - 7, cy - 7, 14, 14, 215, 110);
                    e.Graphics.DrawArc(pen, cx - 13, cy - 13, 26, 26, 215, 110);
                }

                using (var titleFont = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold))
                {
                    TextRenderer.DrawText(e.Graphics, "澈知科技", titleFont, new Point(52, badgeY - 1), Color.FromArgb(17, 24, 39));
                }

                using (var subFont = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Regular))
                {
                    TextRenderer.DrawText(e.Graphics, "校园网便捷认证 · 共享移动热点", subFont, new Point(53, badgeY + 22), Color.FromArgb(100, 116, 139));
                }
            }
        }

        private static void ApplyButtonLook(Button button, ButtonKind kind)
        {
            var modern = button as ModernButton;
            if (modern != null) { modern.Kind = kind; return; }
            bool enabled = button.Enabled;
            button.FlatStyle = FlatStyle.Flat;
            button.Cursor = enabled ? Cursors.Hand : Cursors.Default;
            button.FlatAppearance.BorderSize = kind == ButtonKind.Primary || kind == ButtonKind.DangerFilled ? 0 : 1;
            if (!enabled)
            {
                button.BackColor = Color.FromArgb(243, 245, 248);
                button.ForeColor = Color.FromArgb(168, 175, 184);
                button.FlatAppearance.BorderColor = Color.FromArgb(226, 232, 240);
                return;
            }
            switch (kind)
            {
                case ButtonKind.Primary:
                    button.BackColor = Color.FromArgb(30, 96, 213);
                    button.ForeColor = Color.White;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(43, 114, 238);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(23, 78, 174);
                    break;
                case ButtonKind.Danger:
                    button.BackColor = Color.FromArgb(255, 245, 245);
                    button.ForeColor = Color.FromArgb(220, 38, 38);
                    button.FlatAppearance.BorderColor = Color.FromArgb(252, 165, 165);
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(254, 226, 226);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(254, 202, 202);
                    break;
                case ButtonKind.DangerFilled:
                    button.BackColor = Color.FromArgb(220, 38, 38);
                    button.ForeColor = Color.White;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(239, 68, 68);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(185, 28, 28);
                    break;
                default:
                    button.BackColor = Color.White;
                    button.ForeColor = Color.FromArgb(51, 65, 85);
                    button.FlatAppearance.BorderColor = Color.FromArgb(209, 213, 219);
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(243, 244, 246);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(229, 231, 235);
                    break;
            }
        }

        private static void ConfigureButton(Button button, string text, ButtonKind kind)
        {
            button.Text = text; button.Height = 36;
            button.Tag = kind;
            var modern = button as ModernButton;
            if (modern != null) modern.Kind = kind;
            else ApplyButtonLook(button, kind);
            button.EnabledChanged += delegate {
                if (modern != null) modern.Kind = (ButtonKind)button.Tag;
                else ApplyButtonLook(button, (ButtonKind)button.Tag);
            };
        }

        // 状态指示点: 底色圆点 + 进行中的呼吸脉冲外环
        private sealed class StatusDot : Control
        {
            private Color color = ToneInfo;
            private float pulse;

            public StatusDot()
            {
                SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
                Size = new Size(18, 18);
            }

            public void Set(Color value) { color = value; Invalidate(); }
            public void SetPulse(float value) { pulse = value; Invalidate(); }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                Color parentColor = Parent != null && Parent.BackColor != Color.Transparent ? Parent.BackColor : FormBgColor;
                e.Graphics.Clear(parentColor);
                int cx = Width / 2;
                int cy = Height / 2;
                if (pulse > 0f)
                {
                    float radius = 4f + (7f * pulse);
                    using (var ring = new Pen(Color.FromArgb((int)(140 * (1f - pulse)), color), 1.8f))
                        e.Graphics.DrawEllipse(ring, cx - radius, cy - radius, radius * 2, radius * 2);
                }
                using (var brush = new SolidBrush(color))
                    e.Graphics.FillEllipse(brush, cx - 4, cy - 4, 8, 8);
            }
        }

        private void SetStatus(string text, Color tone, bool pulse = false)
        {
            // 展示层：只更新文案/指示点/卡片配色。顶部连接状态 pill 由领域状态驱动
            // （UpdateConnectionPill），操作成功不再被误显示为"已连接"（A03/A13 根因修复）。
            status.Text = text;
            if (!dot.IsDisposed) dot.Set(tone);
            if (statusCard != null && !statusCard.IsDisposed) statusCard.SetTone(tone);
            if (pulse) animTimer.Start();
            else
            {
                animTimer.Stop();
                if (!dot.IsDisposed) dot.SetPulse(0);
            }
        }

        // 顶部连接状态 pill 的唯一驱动来源：领域状态快照（禁止从提示颜色推断）
        private void UpdateConnectionPill()
        {
            if (statusPill == null || statusPill.IsDisposed) return;
            ConnectionStates.AuthState a = state.Auth;
            Color pillColor = ToneInfo;
            if (state.PillBusy) pillColor = ToneBusy;
            else if (a == ConnectionStates.AuthState.Authenticated || a == ConnectionStates.AuthState.AuthenticatedBySession) pillColor = ToneOk;
            else if (a == ConnectionStates.AuthState.Failed) pillColor = ToneFail;
            statusPill.UpdateStatus(state.PillText, pillColor);
        }

        // 连接状态迁移唯一入口：经迁移表校验，非法迁移拒绝并记录（供诊断/测试）
        private void SetAuthState(ConnectionStates.AuthState next, string text, Color tone, bool pulse = false)
        {
            state.TrySetAuth(next, text);
            SetStatus(text, tone, pulse);
            UpdateConnectionPill();
        }

        private void StyleStopArmed(bool armed)
        {
            stop.Tag = armed ? ButtonKind.DangerFilled : ButtonKind.Danger;
            var modern = stop as ModernButton;
            if (modern != null) modern.Kind = (ButtonKind)stop.Tag;
            else ApplyButtonLook(stop, (ButtonKind)stop.Tag);
        }

        // A11：环境检查（WMI，冷启动可达数秒）异步化——事件处理与启动检查共用
        private bool checkingBlocked;
        private async void RunPreConnectCheckAsync(bool showMessage)
        {
            if (checkingBlocked) return;
            checkingBlocked = true;
            try
            {
                string blocked = await Task.Run((Func<string>)CheckBeforeConnect);
                if (IsDisposed) return;
                if (running != null && !running.HasExited) return; // 检查期间用户已连接
                if (blocked != null)
                {
                    SetStatus(blocked, ToneWarn);
                    if (showMessage) MessageBox.Show(this, blocked, "暂不能连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (!showMessage)
                {
                    if (loadedCredential) SetStatus("已载入保存的账号，点击“立即连接”即可。", ToneOk);
                    else SetStatus("输入校园网账号和密码，点击“立即连接”即可。", ToneInfo);
                }
            }
            finally { checkingBlocked = false; }
        }

        private async void ConnectClicked(object sender, EventArgs e)
        {
            if (serviceBusy || checkingBlocked || (running != null && !running.HasExited)) return;
            // 输入校验、认证启动、凭据保存三者分离：连接前不再强制写盘（A04/A05）。
            // 仅当用户勾选"记住账号密码"且认证成功后，才在 SaveAfterAuth() 中持久化。
            if (account.Text.Length == 0 || password.Text.Length == 0)
            {
                SetStatus("请输入校园网账号和密码。", ToneWarn);
                return;
            }
            // A11：阻塞式 WMI 检查移出 UI 线程；检查期间按钮禁用防重复点击
            connect.Enabled = false;
            SetStatus("正在检查天翼服务状态……", ToneInfo, true);
            string blocked = await Task.Run((Func<string>)CheckBeforeConnect);
            if (IsDisposed) return;
            if (blocked != null)
            {
                SetStatus(blocked, ToneWarn); MessageBox.Show(this, blocked, "暂不能连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                connect.Enabled = true;
                return;
            }
            // 用户手动发起新一轮连接：重置自动恢复预算与停止标志
            userStopped = false; runSawProgress = false; authFailureSeen = false; autoRetryCount = 0; retryTimer.Stop();
            StartAuthProcess();
        }

        // 认证进程启动（连接与自动重试共用；测试可用 mode 注入崩溃钩子）。
        private void StartAuthProcess(string mode = "--ephemeral-auth")
        {
            connect.Enabled = false;
            try
            {
                if (running != null) { running.Dispose(); running = null; }
                stopping = false; closingAfterStop = false; savedAfterAuth = false; stop.Text = "停止认证"; StyleStopArmed(false); timer.Stop();
                running = LaunchCore(mode, account.Text, password.Text, channel.SelectedIndex + 1, OnCoreStatus);
                password.UseSystemPasswordChar = true; reveal.Text = "显示";
                connect.Text = "连接中…";
                stop.Enabled = true;
                state.SetCore(ConnectionStates.CoreState.Running);
                SetAuthState(ConnectionStates.AuthState.Authenticating, "正在连接校园网……", ToneBusy, true);
                Process started = running;
                started.Exited += delegate { if (!IsDisposed && IsHandleCreated) try { BeginInvoke((Action)delegate { OnCoreExit(started); }); } catch (InvalidOperationException) { } };
                if (started.HasExited) OnCoreExit(started);
            }
            catch (Exception ex)
            {
                SetStatus(ex is ArgumentException ? ex.Message : "启动认证失败，请重试。", ToneFail);
                connect.Text = "立即连接";
                connect.Enabled = true;
            }
        }

        // —— 有界自动恢复（A06）——
        // 触发条件（全部满足）：核心异常退出（非用户停止/退出、非受控认证失败）、
        // 本轮曾经到达过"等待网络/认证成功/已联网"（瞬时环境问题，排除疑似凭据错误）、
        // 重试次数 < 2。退避 10s → 40s。用户停止/退出/窗口关闭即刻取消；
        // 不绕过服务器频率限制：凭据疑似错误（未见过网络）一律不自动重试。
        private void ScheduleAutoRetry(int exitCode)
        {
            if (userStopped || exitRequested || stopping || runSawProgress == false || authFailureSeen || autoRetryCount >= 2) return;
            autoRetryCount++;
            int delay = autoRetryCount == 1 ? 10000 : 40000;
            retryTimer.Stop(); retryTimer.Interval = delay; retryTimer.Start();
            SetStatus("认证进程异常退出（退出码 " + exitCode + "），" + (delay / 1000) + " 秒后自动重试（第 " + autoRetryCount + "/2 次）。", ToneWarn, true);
        }

        private void RetryTimerTick(object sender, EventArgs e)
        {
            retryTimer.Stop();
            if (userStopped || exitRequested || serviceBusy || stopping || (running != null && !running.HasExited)) return;
            if (account.Text.Length == 0 || password.Text.Length == 0) return;
            try { StartAuthProcess(); }
            catch { SetAuthState(ConnectionStates.AuthState.Failed, "自动重试启动失败，请手动重连。", ToneFail); }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode != PowerModes.Resume || IsDisposed) return;
            try { BeginInvoke((Action)delegate {
                // 唤醒后不自动重启：运行中的核心由自身心跳/退避循环重新确认；
                // 已停止的会话保持停止。仅提示状态待确认。
                if (running != null && !running.HasExited) SetStatus("系统已唤醒，正在确认连接状态……", ToneInfo, true);
            }); } catch (InvalidOperationException) { }
        }

        private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason != SessionSwitchReason.SessionLock || IsDisposed) return;
            try { BeginInvoke((Action)delegate {
                password.UseSystemPasswordChar = true; reveal.Text = "显示";
            }); } catch (InvalidOperationException) { }
        }

        // A19：脱敏诊断导出（不含账号/密码/票据；仅状态快照、事件归档与环境摘要）
        private void ExportDiagnostics()
        {
            try
            {
                string dir = Path.Combine(Path.GetTempPath(), "CheZhiDiag");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "chezhi-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt");
                var sb = new StringBuilder();
                sb.AppendLine("澈知科技诊断信息（脱敏，不含凭据）");
                sb.AppendLine("生成时间(UTC): " + DateTime.UtcNow.ToString("o"));
                Version v = Assembly.GetExecutingAssembly().GetName().Version;
                sb.AppendLine("程序版本: " + (v == null ? "?" : v.ToString()));
                sb.AppendLine("认证状态: " + state.Auth + "  核心进程: " + state.Core + "  网络: " + state.Network + "  服务: " + state.Service);
                sb.AppendLine("自动恢复: 重试 " + autoRetryCount + "/2, 用户已停止=" + userStopped + ", 本轮曾见网络=" + runSawProgress);
                sb.AppendLine();
                sb.AppendLine("== 非法状态迁移记录（应恒为空） ==");
                foreach (string r in state.RejectedTransitions) sb.AppendLine(r);
                sb.AppendLine();
                sb.AppendLine("== 核心事件归档（最近 " + coreEvents.Count + " 条） ==");
                foreach (string ev in coreEvents) sb.AppendLine(ev);
                sb.AppendLine();
                try
                {
                    var j = ServiceJournal.Load(ServiceJournal.DefaultPath());
                    sb.AppendLine("== 服务事务记录 ==");
                    sb.AppendLine("action=" + (j.Action ?? "无") + " state=" + (j.StateBefore ?? "?") + " result=" + (j.Result ?? "无"));
                }
                catch { sb.AppendLine("== 服务事务记录：读取失败 =="); }
                sb.AppendLine();
                sb.AppendLine("== 环境 ==");
                sb.AppendLine("OS: " + Environment.OSVersion.VersionString);
                using (var g = CreateGraphics()) { sb.AppendLine("DPI: " + g.DpiX); }
                File.WriteAllText(file, sb.ToString(), Encoding.UTF8);
                SetStatus("诊断信息已导出：" + file, ToneOk);
                MessageBox.Show(this, "诊断信息已导出（不含账号密码）：\r\n" + file, "导出诊断", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex) { SetStatus("诊断导出失败：" + ex.GetType().Name, ToneFail); }
        }

        // 连接成功后的按需保存：未勾选"记住账号密码"时不写任何持久化凭据；
        // 保存失败只提示，不影响已经成功的连接。
        private bool savedAfterAuth;
        private void SaveAfterAuth()
        {
            if (savedAfterAuth) return;
            savedAfterAuth = true;
            if (!remember.Checked || credentials == null) return;
            if (!SaveCredentials(false))
                MessageBox.Show(this, "连接成功，但账号密码未能加密保存（可能是本地记录损坏或目录不可写）。\r\n本次连接不受影响；如需保存请先用托盘菜单“清除已保存账号”。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // 归档核心结构化事件（含畸形行防御：截断到 512 字符、环形上限 200 条）
        private void ArchiveCoreEvent(string line)
        {
            if (String.IsNullOrEmpty(line)) return;
            if (line.Length > 512) line = line.Substring(0, 512);
            coreEvents.Add(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + line);
            if (coreEvents.Count > 200) coreEvents.RemoveRange(0, coreEvents.Count - 200);
        }

        private void OnCoreStatus(Process sender, string token)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try { BeginInvoke((Action)delegate {
                if (!Object.ReferenceEquals(running, sender)) return;
                // 结构化事件（v1）：仅归档，不驱动 UI 文案（旧令牌仍承担状态显示，兼容并行）
                if (token.StartsWith("EPHEMERAL_EVENT ", StringComparison.Ordinal)) { ArchiveCoreEvent(token); return; }
                if (stopping && token != "EPHEMERAL_STOP_ACCEPTED" && token != "EPHEMERAL_STOP_CHANNEL_FAILED") return;
                if (token == "EPHEMERAL_AUTH_OK")
                {
                    connect.Text = "运行中…"; // 已连上, 按钮反映"保持运行"而非"连接中"
                    runSawProgress = true;
                    state.SetNetwork(ConnectionStates.NetworkState.AuthenticationRequired); // 认证前校园网待认证；成功后视为完成
                    SetAuthState(ConnectionStates.AuthState.Authenticated, "连接成功！现在可到 Windows 设置打开移动热点共享网络。", ToneOk);
                    SaveAfterAuth();
                }
                else if (token == "EPHEMERAL_AUTH_FAILED")
                {
                    runSawProgress = true; authFailureSeen = true; // 受控失败：退出不算崩溃、不自动重试
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.Failed, "连接失败：请核对账号密码，并确认已连上校园网。", ToneFail);
                }
                else if (token == "EPHEMERAL_WAIT_NETWORK")
                {
                    runSawProgress = true;
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.WaitingForNetwork, "正在等待接入校园网……请确认电脑已连上校园网的 Wi-Fi 或网线。", ToneBusy, true);
                }
                else if (token == "EPHEMERAL_ONLINE")
                {
                    runSawProgress = true;
                    connect.Text = "运行中…";
                    state.SetNetwork(ConnectionStates.NetworkState.InternetAvailable); // 有互联网≠本程序完成认证（沿用会话）
                    SetAuthState(ConnectionStates.AuthState.AuthenticatedBySession, "网络已连通（可能沿用了先前的登录会话）。程序保持运行，会话失效后会自动重新认证。", ToneOk);
                    SaveAfterAuth();
                }
                else if (token == "EPHEMERAL_STOP_ACCEPTED") SetAuthState(ConnectionStates.AuthState.LoggingOut, "正在退出认证并通知服务器登出……", ToneWarn, true);
                else if (token == "EPHEMERAL_STOP_CHANNEL_FAILED") SetStatus("停止指令发送失败，可稍后点“强制结束”。", ToneFail);
            }); } catch (InvalidOperationException) { }
        }

        private void OnCoreExit(Process started)
        {
            if (!Object.ReferenceEquals(running, started)) return;
            timer.Stop(); stopping = false; stop.Enabled = false; stop.Text = "停止认证"; StyleStopArmed(false); connect.Enabled = true; connect.Text = "立即连接";
            int code = 0; bool exitedCleanly = false;
            try { code = started.ExitCode; exitedCleanly = code == 0; } catch { }
            bool crashed = !exitedCleanly && !authFailureSeen && !userStopped;
            state.SetCore(crashed ? ConnectionStates.CoreState.Crashed : ConnectionStates.CoreState.Exited);
            // 收到退出 ≠ 服务器已登出（A06/A15：退出码区分崩溃/正常，登出结果未知）
            if (crashed)
            {
                SetAuthState(ConnectionStates.AuthState.Failed, "认证进程异常退出（退出码 " + code + "），服务器上的会话可能仍在。", ToneFail);
                ScheduleAutoRetry(code); // 有界自动恢复：满足条件才生效（见 ScheduleAutoRetry 注释）
            }
            else
            {
                SetAuthState(ConnectionStates.AuthState.Idle, authFailureSeen
                    ? "连接失败已结束。服务器上的会话状态未知。"
                    : "认证已停止。服务器上的会话可能稍后才释放。", ToneWarn);
            }
            if (closingAfterStop) { closingAfterStop = false; Close(); }
        }

        private void BeginStop()
        {
            if (running == null || running.HasExited || stopping) return;
            userStopped = true; retryTimer.Stop(); // 用户主动停止：取消一切待执行的自动重试（不变量）
            stopping = true; stop.Enabled = false; stop.Text = "正在停止…"; StyleStopArmed(false);
            grace = Stopwatch.StartNew(); timer.Start();
            bool sent = RequestStop(running);
            SetStatus(sent ? "正在停止认证，等待程序清理退出……" : "停止指令未收到确认，继续等待退出……", ToneWarn, true);
        }

        private void StopClicked(object sender, EventArgs e)
        {
            if (!stopping) { try { BeginStop(); } catch { SetStatus("停止请求发送失败，窗口将保持开启。", ToneFail); } return; }
            if (grace == null || grace.Elapsed.TotalSeconds < 100) return;
            if (MessageBox.Show(this, "强制结束可能让服务器会话多保留一会儿，确定立即结束认证进程？", "强制结束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { if (running != null && !running.HasExited) running.Kill(); }
            catch { SetStatus("结束失败，请保持窗口开启稍后再试。", ToneFail); }
        }

        private void OnClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing && !exitRequested)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            if (e.CloseReason == CloseReason.WindowsShutDown || e.CloseReason == CloseReason.TaskManagerClosing)
            {
                try { if (running != null && !running.HasExited) RequestStop(running); } catch { }
                return;
            }
            if (serviceBusy) { e.Cancel = true; exitRequested = false; SetStatus("正在处理服务操作，请稍候……", ToneWarn, true); return; }
            if (running == null || running.HasExited) return;
            e.Cancel = true;
            if (!stopping && MessageBox.Show(this, "退出将断开校园网认证，共享的热点也会断网。确定退出？", "退出澈知科技", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { exitRequested = false; return; }
            // 核心可能在确认框期间已自行退出 (确认框会泵消息): 此时无需再等待, 直接放行关闭
            if (running.HasExited) { stopping = false; e.Cancel = false; return; }
            userStopped = true; retryTimer.Stop(); // 确认退出=停止意图：取消待执行的自动重试
            closingAfterStop = true;
            ShowFromTray();
            try { BeginStop(); } catch { exitRequested = false; closingAfterStop = false; SetStatus("停止请求发送失败，窗口将保持开启。", ToneFail); }
        }

        // A09：程序重启后识别未完成的服务切换，向用户提供"经授权的恢复选项"（不擅自操作服务）
        private void PromptPendingServiceRecovery()
        {
            try
            {
                var journal = ServiceJournal.Load(ServiceJournal.DefaultPath());
                if (!journal.HasPending) return;
                if (journal.Action == "prepare")
                {
                    SetStatus("检测到上次“准备连接”未完成收尾，天翼后台服务可能仍处于停用状态。", ToneWarn);
                    if (MessageBox.Show(this, "上次停用天翼后台服务后，程序没有正常完成收尾。\r\n\r\n是否现在恢复天翼后台服务？（会请求管理员确认）", "恢复天翼服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
#pragma warning disable 4014 // 有意 fire-and-forget：方法内部 await 捕获 UI 上下文并自带完整异常处理
                        RunServiceAction(false);
#pragma warning restore 4014
                }
                else
                {
                    SetStatus("检测到上次“恢复天翼”未完成，服务当前状态未知，可用“恢复天翼”重试。", ToneWarn);
                    journal.Clear(); // restore 未完成无可自动建议的恢复动作，提示后清除
                }
            }
            catch { }
        }

        private async Task RunServiceAction(bool forPrepare)
        {
            if (serviceBusy) return;
            if (running != null && !running.HasExited) { MessageBox.Show(this, "请先停止本程序的认证，再执行切换。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information); return; }
            string instructions = forPrepare
                ? "继续前请确认：\r\n\r\n1. 已在天翼客户端中断开并完全退出；\r\n2. 移动热点已关闭。\r\n\r\n接下来将请求管理员权限，仅停用天翼后台服务，不会动其它程序或开机启动项。"
                : "继续前请确认：\r\n\r\n1. 本程序的认证已停止；\r\n2. 移动热点已关闭。\r\n\r\n接下来将请求管理员权限，重新启用天翼后台服务并打开官方客户端。";
            if (MessageBox.Show(this, instructions, forPrepare ? "准备连接" : "恢复天翼", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
            serviceBusy = true; prepare.Enabled = false; restore.Enabled = false; connect.Enabled = false;
            SetStatus("请在弹出的管理员确认窗口中操作……", ToneBusy, true);
            // A09 事务记录：提权子进程启动前写入 pending；程序此时异常退出则记录保留，
            // 下次启动提示恢复。子进程退出后（无论成败）回写结果。
            var journal = isSelfTest ? null : ServiceJournal.Load(ServiceJournal.DefaultPath());
            try { if (journal != null) journal.Begin(forPrepare ? "prepare" : "restore", CurrentServiceState()); } catch { journal = null; }
            try
            {
                var info = new ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = forPrepare ? "--admin-prepare" : "--admin-restore", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                using (Process p = Process.Start(info))
                {
                    if (p == null) throw new InvalidOperationException();
                    await Task.Run(() => p.WaitForExit());
                    int code = p.ExitCode;
                    try { if (journal != null) journal.Complete(code); } catch { }
                    if (code == 0)
                    {
                        if (forPrepare) SetStatus("准备完成！输入账号密码即可连接。", ToneOk);
                        else
                        {
                            SetStatus("天翼已恢复，请在官方客户端重新登录。", ToneOk);
                            string client = FindOfficialClient();
                            if (client != null) Process.Start(new ProcessStartInfo { FileName = client, WorkingDirectory = Path.GetDirectoryName(client), UseShellExecute = true });
                        }
                    }
                    else { string message = ServiceError(code); SetStatus(message, ToneWarn); MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning); }
                }
            }
            catch (Win32Exception e) { SetStatus(e.NativeErrorCode == 1223 ? "已取消管理员授权。" : "无法启动管理员操作，请重试。", ToneWarn); }
            catch { SetStatus("操作未完成，请检查天翼服务状态后重试。", ToneWarn); }
            finally { serviceBusy = false; prepare.Enabled = true; restore.Enabled = true; connect.Enabled = true; }
        }

        private void ShowAbout()
        {
            string text = "使用指南\r\n\r\n【首次连接】\r\n1. 在天翼客户端里断开并完全退出，关闭移动热点；\r\n2. 点击“准备连接”，在管理员确认后停用天翼后台服务；\r\n3. 输入账号和密码，点击“立即连接”；\r\n4. 连接成功后，到 Windows 设置开启移动热点即可共享。\r\n\r\n【状态说明】\r\n· “正在等待接入校园网”：还没检测到校园网，请检查网线/Wi-Fi；\r\n· “网络已连通（沿用先前会话）”：上次登录的会话仍在有效期，可直接上网；\r\n  程序会保持运行，该会话失效后自动用你保存的账号重新认证；\r\n· “连接成功”：本程序已完成认证并持续保持在线。\r\n\r\n【日常使用】\r\n· 关闭窗口＝最小化到托盘继续运行，双击托盘图标可重新打开；\r\n· 退出请用托盘菜单的“退出程序”；\r\n· 想换回官方客户端：先关热点、停止认证，再点“恢复天翼”；\r\n· 电脑重启后天翼服务可能自动恢复，需要重新“准备连接”。\r\n\r\n【账号安全】\r\n· 账号密码用当前 Windows 用户加密保存，仅本人可读取；\r\n· 输入框右侧“显示”可查看密码，托盘菜单可删除保存记录；\r\n· 运行期间凭据仍会短暂存在于内存中。\r\n\r\n【风险须知】\r\n认证协议为明文 HTTP，密码的加密密钥内嵌于所有客户端中——同一校园网内的窃听者抓到登录报文即可还原密码。请勿在其它场合复用本密码。\r\n\r\n———— 以下为第三方组件许可 ————\r\n\r\n";
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(LicenseResource))
            using (var reader = s == null ? null : new StreamReader(s, Encoding.UTF8))
            {
                if (reader != null) text += reader.ReadToEnd(); // 资源缺失时仅显示使用指南
            }
            using (var dialog = new Form {
                Text = "使用说明与开源许可",
                Size = new Size(720, 580),
                StartPosition = FormStartPosition.CenterParent,
                BackColor = FormBgColor,
                Font = new Font("Microsoft YaHei UI", 9F),
                ShowIcon = false,
                MinimizeBox = false,
                MaximizeBox = false
            })
            {
                var card = new ModernCard { Dock = DockStyle.Fill, Padding = new Padding(16), Margin = new Padding(16), BackColor = CardBgColor };
                var root = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16), BackColor = FormBgColor };
                var closeBtn = new ModernButton { Text = "关闭", Kind = ButtonKind.Primary, Size = new Size(100, 36), Dock = DockStyle.Right, CornerRadius = 6 };
                closeBtn.Click += delegate { dialog.Close(); };
                var bottom = new Panel { Dock = DockStyle.Bottom, Height = 44, Padding = new Padding(0, 8, 0, 0), BackColor = FormBgColor };
                bottom.Controls.Add(closeBtn);

                var box = new TextBox {
                    Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
                    WordWrap = true, Dock = DockStyle.Fill, Text = text,
                    Font = new Font("Microsoft YaHei UI", 9F),
                    BackColor = CardBgColor, BorderStyle = BorderStyle.None
                };
                card.Controls.Add(box);
                root.Controls.Add(card);
                root.Controls.Add(bottom);
                dialog.Controls.Add(root);
                dialog.Shown += delegate { box.SelectionStart = 0; box.SelectionLength = 0; };
                dialog.ShowDialog(this);
            }
        }
    }
}
