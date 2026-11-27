using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
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
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

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
        if (args.Length == 1 && args[0] == "--admin-prepare")
        {
#if CHEZHI_LICENSED
            // A process-local online grant is not a cross-process proof.
            // Until the privileged handoff is independently validated,
            // the licensed candidate must not stop the official service.
            return 28;
#else
            return ChangeService(true);
#endif
        }
        if (args.Length == 1 && args[0] == "--admin-restore") return ChangeService(false);
        if (args.Length == 1 && args[0] == "--self-test-logic") return LogicTests();
        if (args.Length == 2 && args[0] == "--self-test-boot-sequence")
        {
            long expected;
            return Int64.TryParse(args[1], out expected) && expected > 0 && ReadBootSequence() == expected ? 0 : 1;
        }
        if (args.Length == 2 && args[0] == "--self-test-service-gate")
        {
            Guid id;
            if (!Guid.TryParse(args[1], out id)) return 2;
            return WithServiceMutationGate(@"Local\CheZhiTest-" + id.ToString("N"), delegate { return 0; });
        }
        if (args.Length == 2 && args[0] == "--self-test-signature") return SignatureTests(args[1]);
        if (args.Length == 1 && args[0] == "--self-test-core") return CoreTest(false);
        if (args.Length == 1 && args[0] == "--self-test-native-input") return NativeInputTests();
        if (args.Length == 1 && args[0] == "--self-test-native-net") return NativeNetTests();
        if (args.Length == 1 && args[0] == "--self-test-stop") return CoreTest(true);
        if (args.Length == 1 && args[0] == "--self-test-resources") return ResourceTests();
        if (args.Length == 1 && args[0] == "--self-test-observe") return ObserveTests();
        if (args.Length == 1 && args[0] == "--self-test-credentials") return CredentialTests();
        if (args.Length == 1 && args[0] == "--self-test-ui") return UiTests();
        if (args.Length == 1 && args[0] == "--self-test-app-lifecycle") return ApplicationLifecycleTests();
        if (args.Length == 1 && args[0] == "--self-test-ui-stress") return ApplicationLifecycleTests(true);
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
    }

    private static FileStream OpenVerifiedCore()
    {
        if ((File.GetAttributes(tempDir) & FileAttributes.ReparsePoint) != 0 ||
            (File.GetAttributes(corePath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unexpected core reparse point.");
        var stream = new FileStream(corePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            using (var sha = SHA256.Create())
            {
                string actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                if (!String.Equals(actual, CoreHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Core integrity check failed.");
            }
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static void VerifyCore()
    {
        using (OpenVerifiedCore()) { }
    }

    private static void CleanupCore()
    {
        // Only clean the directory allocated by this execution. Never recursively
        // delete arbitrary temp folders selected by a name prefix.
        if (tempDir != null) CleanupOwnedCoreDirectory(tempDir);
        corePath = null; tempDir = null;
    }

    private static void CleanupOwnedCoreDirectory(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            if ((File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0) return;
            string file = Path.Combine(dir, "ESurfingClient.exe");
            if (File.Exists(file))
            {
                if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) return;
                File.Delete(file);
            }
            // Unknown contents must be preserved. A locked executable is also left alone.
            Directory.Delete(dir, false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool CleanupScopeInvariants()
    {
        string root = Path.Combine(Path.GetTempPath(), "ChezhiCleanupTest-" + Guid.NewGuid().ToString("N"));
        string owned = Path.Combine(root, "Chezhi-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(root, "Chezhi-other");
        try
        {
            Directory.CreateDirectory(owned); Directory.CreateDirectory(other);
            string known = Path.Combine(owned, "ESurfingClient.exe");
            string unknown = Path.Combine(owned, "keep.txt");
            string unrelated = Path.Combine(other, "keep.txt");
            File.WriteAllText(known, "test"); File.WriteAllText(unknown, "preserve"); File.WriteAllText(unrelated, "preserve");
            CleanupOwnedCoreDirectory(owned);
            bool ok = !File.Exists(known) && File.Exists(unknown) && File.Exists(unrelated);
            File.Delete(unknown);
            CleanupOwnedCoreDirectory(owned);
            ok &= !Directory.Exists(owned) && File.Exists(unrelated);
            Console.WriteLine(ok ? "CLEANUP_SCOPE_PASS" : "CLEANUP_SCOPE_FAIL");
            return ok;
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    private static int ClientCount() { bool visible; return ReadClients(out visible); }

    private static int ReadClients(out bool visible)
    {
        visible = false;
        // At handoff no live owned core is permitted; count even stale extracted cores.
        Process[] matches = Process.GetProcessesByName("ESurfingClient");
        try
        {
            int count = 0;
            foreach (Process process in matches)
            {
                try { if (process.HasExited) continue; } catch { count++; continue; }
                count++;
                // The window alone is not proof of official identity.
                try
                {
                    if (process.MainWindowHandle != IntPtr.Zero &&
                        ServiceIdentity.VerifyExpectedService(process.MainModule.FileName)) visible = true;
                }
                catch { }
            }
            return count;
        }
        finally { foreach (Process process in matches) process.Dispose(); }
    }

    private static bool CanConnect(ServiceControllerStatus service, int count)
    { return service == ServiceControllerStatus.Stopped && count == 0; }

    private sealed class HandoffObservation
    {
        internal readonly ConnectionStates.ServiceState Service;
        internal readonly int Clients;
        internal readonly bool VisibleClient;
        internal HandoffObservation(ConnectionStates.ServiceState service, int clients, bool visible = false) { Service = service; Clients = clients; VisibleClient = visible; }
        internal ConnectionStates.HandoffDecision Decide(ConnectionStates.HandoffIntent intent)
        { return ConnectionStates.DecideHandoff(intent, Service, Clients, VisibleClient); }
    }
    private static ConnectionStates.ServiceState MapServiceState(string value)
    {
        switch (value)
        {
            case "Running": return ConnectionStates.ServiceState.Running;
            case "Stopped": return ConnectionStates.ServiceState.Stopped;
            case "NotInstalled": return ConnectionStates.ServiceState.NotInstalled;
            case "StartPending": case "StopPending": case "PausePending": case "ContinuePending":
                return ConnectionStates.ServiceState.Transitioning;
            default: return ConnectionStates.ServiceState.Unknown;
        }
    }
    private static HandoffObservation ObserveHandoff()
    {
        try { bool visible; int count = ReadClients(out visible); return new HandoffObservation(MapServiceState(CurrentServiceState()), count, visible); }
        catch { return new HandoffObservation(ConnectionStates.ServiceState.Unknown, -1); }
    }
    private static string HandoffMessage(ConnectionStates.HandoffDecision decision)
    {
        switch (decision)
        {
            case ConnectionStates.HandoffDecision.WaitForClient: return "仍检测到天翼或旧认证进程。请在客户端断开并退出；程序不会强制结束进程。";
            case ConnectionStates.HandoffDecision.WaitForService: return "天翼服务正在切换，请等待系统完成后重试。";
            case ConnectionStates.HandoffDecision.NeedStopService: return "天翼客户端已退出，后台服务仍运行。请授权准备连接。";
            case ConnectionStates.HandoffDecision.NeedStartService: return "天翼服务已停止，需要授权恢复。";
            case ConnectionStates.HandoffDecision.ExistingClient: return "检测到现有天翼或同名认证进程；未重复启动，请检查天翼窗口或托盘。";
            default: return "暂时无法确认天翼服务或进程状态，未执行切换。";
        }
    }
    private static string CheckBeforeConnect()
    {
        var decision = ObserveHandoff().Decide(ConnectionStates.HandoffIntent.Connect);
        return decision == ConnectionStates.HandoffDecision.Ready ? null : HandoffMessage(decision);
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
                foreach (ManagementObject row in rows)
                    using (row) { count++; path = Convert.ToString(row["PathName"]); }
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
        catch { try { if (!ServiceInstalled()) return "NotInstalled"; } catch { } return "unknown"; }
    }

    // Resolve the executable token without silently truncating a service path
    // at the first space. Privileged actions must reject ambiguous unquoted
    // paths rather than assume Windows would launch the intended executable.
    private static string ParseServiceImage(string command, out string reason)
    {
        reason = "path_unresolved";
        if (String.IsNullOrWhiteSpace(command)) return null;
        string raw = Environment.ExpandEnvironmentVariables(command.Trim());
        string path;
        if (raw[0] == '"')
        {
            int end = raw.IndexOf('"', 1);
            if (end <= 1 || (end + 1 < raw.Length && !Char.IsWhiteSpace(raw[end + 1]))) return null;
            path = raw.Substring(1, end - 1);
        }
        else
        {
            int end = raw.IndexOf("ESurfingSvr.exe", StringComparison.OrdinalIgnoreCase);
            if (end < 0) return null;
            end += "ESurfingSvr.exe".Length;
            if (end < raw.Length && !Char.IsWhiteSpace(raw[end])) return null;
            path = raw.Substring(0, end);
            if (path.Any(Char.IsWhiteSpace))
            {
                reason = "unquoted_service_path";
                return null;
            }
        }
        // Absolute local drive paths only: no relative, UNC or device paths.
        if (path.Length < 4 || !Char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' ||
            path.IndexOfAny(new[] { '"', '\r', '\n', '\0', '%' }) >= 0) return null;
        try
        {
            path = Path.GetFullPath(path);
            if (!String.Equals(Path.GetFileName(path), "ESurfingSvr.exe", StringComparison.OrdinalIgnoreCase)) return null;
        }
        catch { return null; }
        reason = "resolved";
        return path;
    }

    private static string ServiceExePath(out string reason)
    {
        int count;
        string path;
        QueryService(out count, out path);
        reason = count == 0 ? "service_missing" : "path_unresolved";
        return count == 1 ? ParseServiceImage(path, out reason) : null;
    }

    private static string ServiceExePath()
    {
        string reason;
        return ServiceExePath(out reason);
    }

    private static string ServiceVerificationReason()
    {
        try
        {
            string reason;
            string path = ServiceExePath(out reason);
            return path == null ? reason : ServiceIdentity.VerificationReason(path);
        }
        catch { return "path_unresolved"; }
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
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, UIntPtr size, out UIntPtr bytesRead);

    // KUSER_SHARED_DATA.BootId is a loader-incremented sequence, NOT the
    // BCD BootIdentifier GUID. The Windows 10+ user-mode mapping is read-only.
    // ReadProcessMemory avoids an unsafe pointer dereference if the layout is unavailable.
    // https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntddk/ns-ntddk-kuser_shared_data
    // https://geoffchappell.com/studies/windows/km/ntoskrnl/inc/api/ntexapi_x/kuser_shared_data/index.htm
    private static long ReadBootSequence()
    {
        try
        {
            if (Environment.OSVersion.Platform != PlatformID.Win32NT || Environment.OSVersion.Version.Major != 10) return 0;
            using (Process self = Process.GetCurrentProcess())
            {
                byte[] buffer = new byte[4];
                UIntPtr read;
                if (!ReadProcessMemory(self.Handle, new IntPtr(0x7ffe02c4), buffer, new UIntPtr(4U), out read) ||
                    read.ToUInt64() != 4) return 0;
                return BitConverter.ToUInt32(buffer, 0);
            }
        }
        catch { return 0; }
    }

    private sealed class BootEvidence
    {
        internal readonly long Ticks, Sequence;
        internal BootEvidence(long ticks, long sequence) { Ticks = ticks; Sequence = sequence; }
    }

    private static BootEvidence ReadBootEvidence()
    {
        long sequence = ReadBootSequence();
        long ticks = ReadBootTicks();
        return sequence > 0 && ticks > 0 && sequence == ReadBootSequence() ? new BootEvidence(ticks, sequence) : null;
    }

    private static bool BootSequenceInvariants()
    {
        long sequence = ReadBootSequence();
        if (sequence <= 0 || sequence != ReadBootSequence())
        { Console.WriteLine("BOOT_SEQUENCE_UNAVAILABLE"); return false; }
        var start = new ProcessStartInfo {
            FileName = Application.ExecutablePath,
            Arguments = "--self-test-boot-sequence " + sequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            UseShellExecute = false, CreateNoWindow = true
        };
        using (var child = Process.Start(start))
        {
            if (child == null || !child.WaitForExit(10000) || child.ExitCode != 0)
            { Console.WriteLine("BOOT_SEQUENCE_CROSS_PROCESS_FAIL"); return false; }
        }
        var evidence = ReadBootEvidence();
        bool ok = evidence != null && evidence.Sequence == sequence && evidence.Ticks > 0;
        Console.WriteLine(ok ? "BOOT_SEQUENCE_PASS" : "BOOT_SEQUENCE_EVIDENCE_FAIL");
        return ok;
    }

    private static long ReadBootTicks()
    {
        try
        {
            using (var query = new ManagementObjectSearcher("SELECT LastBootUpTime FROM Win32_OperatingSystem"))
            using (var results = query.Get())
            {
                long ticks = 0; int count = 0;
                foreach (ManagementObject item in results)
                using (item)
                {
                    count++;
                    var value = item["LastBootUpTime"] as string;
                    if (String.IsNullOrEmpty(value)) return 0;
                    ticks = ManagementDateTimeConverter.ToDateTime(value).ToUniversalTime().Ticks;
                }
                return count == 1 && ticks <= DateTime.UtcNow.Ticks ? ticks : 0;
            }
        }
        catch { return 0; }
    }

    private static int ChangeService(bool prepare)
    {
#if CHEZHI_LICENSED
        if (prepare) return 28;
#endif
        try
        {
            var principal = new WindowsPrincipal(WindowsIdentity.GetCurrent());
            if (!principal.IsInRole(WindowsBuiltInRole.Administrator)) return 20;
            return WithServiceMutationGate(@"Global\CheZhiCampus-ServiceMutation-v1", delegate { return ChangeServiceUnderGate(prepare); });
        }
        catch { return 25; }
    }

    private static int WithServiceMutationGate(string name, Func<int> operation)
    {
        try
        {
            using (var gate = new System.Threading.Mutex(false, name))
            {
                bool owned = false;
                try
                {
                    try { owned = gate.WaitOne(0); }
                    catch (System.Threading.AbandonedMutexException) { owned = true; return 27; }
                    if (!owned) return 27;
                    return operation();
                }
                finally { if (owned) gate.ReleaseMutex(); }
            }
        }
        catch { return 27; }
    }

    private static bool OperationPermissionInvariants()
    {
        bool ok = true;
        // Exhaust all combinations of the eight ownership inputs, including inconsistent snapshots.
        for (int mask = 0; mask < 256; mask++)
        {
            bool service = (mask & 1) != 0, check = (mask & 2) != 0, retry = (mask & 4) != 0,
                core = (mask & 8) != 0, stopping = (mask & 16) != 0, force = (mask & 32) != 0,
                exiting = (mask & 64) != 0, cancelled = (mask & 128) != 0;
            var rights = ConnectionStates.DecideOperations(service, check, retry, core, stopping, force, exiting, cancelled);
            bool idle = !service && !check && !retry && !core && !stopping && !exiting;
            ok &= rights.CanConnect == idle && rights.CanSwitch == idle && rights.CanRecover == idle;
            if (service) ok &= rights.Stop == ConnectionStates.StopIntent.None;
            if (rights.Stop == ConnectionStates.StopIntent.ForceCore) ok &= core && stopping && force && !service;
            if (rights.Stop == ConnectionStates.StopIntent.CancelConnection) ok &= check && !cancelled && !service && !core && !exiting;
            if (rights.CanPrepareForConnection) ok &= check && !cancelled && !service && !core && !exiting;
            if (rights.Stop == ConnectionStates.StopIntent.CancelRetry) ok &= retry && !check && !core && !service && !exiting;
        }
        Console.WriteLine(ok ? "OPERATION_MATRIX_PASS cases=256" : "OPERATION_MATRIX_FAIL");
        return ok;
    }

    private static bool ServiceGateInvariants()
    {
        Guid id = Guid.NewGuid();
        string name = @"Local\CheZhiTest-" + id.ToString("N");
        Func<int> launch = delegate {
            using (Process child = Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath,
                Arguments = "--self-test-service-gate " + id.ToString("N"), UseShellExecute = false, CreateNoWindow = true }))
            {
                if (child == null) return -1;
                if (!child.WaitForExit(5000)) { child.Kill(); child.WaitForExit(5000); return -1; }
                return child.ExitCode;
            }
        };
        bool ok = WithServiceMutationGate(name, launch) == 27 && launch() == 0;
        Console.WriteLine(ok ? "SERVICE_GATE_PASS" : "SERVICE_GATE_FAIL");
        return ok;
    }

    private static int ChangeServiceUnderGate(bool prepare)
    {
        try
        {
            if (!ServiceInstalled())
            {
                // 没装天翼客户端就没有后台服务: “准备连接”无事可做直接成功, “恢复天翼”无从恢复
                return prepare ? 0 : 26;
            }
            string verifiedPath = ServiceExePath();
            bool identityOk = verifiedPath != null && ServiceIdentity.VerifyExpectedService(verifiedPath);
            int clients = ClientCount();
            bool directIdle = DirectAdaptersIdle();
            using (var service = new ServiceController(ServiceName))
            {
                service.Refresh();
                ServiceControllerStatus current = service.Status;
                bool canStop = current == ServiceControllerStatus.Running && service.CanStop;
                int guard = ServiceGuard(identityOk, clients, directIdle, current, canStop, prepare);
                if (guard != 0) return guard;
                // Narrow the external race window without claiming atomic SCM identity binding.
                if (!String.Equals(verifiedPath, ServiceExePath(), StringComparison.OrdinalIgnoreCase) ||
                    !ServiceIdentity.VerifyExpectedService(verifiedPath)) return 21;
                if (ClientCount() != 0) return 22;
                if (!DirectAdaptersIdle()) return 23;
                service.Refresh();
                if (service.Status != current) return 24;
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
            return 10; // confirmed change, distinct from idempotent no-op
        }
        catch { return 25; }
    }

        private static string ServiceError(int code)
        {
            switch (code)
            {
                case 20: return "未获得管理员权限，操作已取消。";
                case 21: return "天翼服务文件路径或数字签名身份未通过验证；未执行切换。请核对官方客户端。";
                case 26: return "未安装天翼后台服务，请检查官方客户端安装。";
                case 27: return "另一个服务切换正在执行，或无法确认切换互斥状态；未执行新的服务修改。";
                case 22: return "仍有天翼客户端或认证进程在运行，请先断开并完全退出。";
                case 23: return "移动热点或 Wi-Fi Direct 仍在使用（或状态未知），请先关闭热点。";
                case 24: return "天翼服务正在切换中，请稍后再试。";
                default: return "操作未完成，请检查服务状态后重试。";
            }
        }

    // Parse only the authenticated child pipe's allowlisted protocol fields.
    // Never retain arbitrary JSON strings in diagnostics.
    private static string ParseCoreEvent(string line, int expectedPid)
    {
        const string prefix = "EPHEMERAL_EVENT ";
        if (String.IsNullOrEmpty(line) || line.Length > 512 || !line.StartsWith(prefix, StringComparison.Ordinal)) return null;
        try
        {
            var obj = new JavaScriptSerializer { MaxJsonLength = 512, RecursionLimit = 4 }
                .DeserializeObject(line.Substring(prefix.Length)) as System.Collections.Generic.Dictionary<string, object>;
            if (obj == null || obj.Count != 5 || !obj.ContainsKey("v") || !obj.ContainsKey("pid") ||
                !obj.ContainsKey("event") || !obj.ContainsKey("code") || !obj.ContainsKey("error") ||
                !(obj["v"] is int) || (int)obj["v"] != 1 || !(obj["pid"] is int) ||
                (int)obj["pid"] != expectedPid || !(obj["code"] is int) ||
                !String.Equals(obj["error"] as string, "unknown", StringComparison.Ordinal)) return null;
            string ev = obj["event"] as string;
            int code = (int)obj["code"];
            switch (ev)
            {
                case "auth_ok": case "wait_network": case "online": case "stop_accepted": case "stop_channel_failed":
                case "network_online": case "network_required": case "network_unknown": case "auth_start": case "probe_ready":
                    return code == 0 ? ev : null;
                case "auth_failed": case "core_failed": return code != 0 ? ev : null;
                case "auth_retry_wait": return code > 0 && code <= 35 ? ev + ":" + code.ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
                case "network_retry_exhausted": return code == 1 || code == 2 ? ev : null;
                case "test_crash": return code == 3 ? ev : null;
                case "exit": return ev;
                default: return null;
            }
        }
        catch { return null; }
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Process, Task> CoreReaders =
        new System.Runtime.CompilerServices.ConditionalWeakTable<Process, Task>();

    // A fixed-size reader enforces the bound before a complete line is allocated.
    private static void ReadBoundedEvents(TextReader reader, Action<string> receive)
    {
        char[] buffer = new char[256];
        var line = new StringBuilder(512);
        bool oversized = false;
        int count;
        while ((count = reader.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < count; i++)
            {
                char ch = buffer[i];
                if (ch == '\n')
                {
                    if (!oversized && receive != null) receive(line.ToString().TrimEnd('\r'));
                    line.Length = 0; oversized = false;
                }
                else if (!oversized)
                {
                    if (line.Length == 512) { line.Length = 0; oversized = true; }
                    else line.Append(ch);
                }
            }
        }
        // Incomplete final records are deliberately ignored.
    }

    private static string ValidateAuthInput(string username, string password, int channel)
    {
        if (String.IsNullOrEmpty(username) || String.IsNullOrEmpty(password)) return "请输入校园网账号和密码。";
        if (channel < 1 || channel > 5) return "请选择有效的设备类型。";
        if (username.IndexOf('\0') >= 0 || password.IndexOf('\0') >= 0) return "账号或密码含无效字符。";
        try
        {
            if (Utf8.GetByteCount(username) >= 16 || Utf8.GetByteCount(password) >= 128)
                return "账号需少于 16 字节、密码需少于 128 字节。";
            return null;
        }
        catch (EncoderFallbackException) { return "账号或密码含无效字符。"; }
    }

    private static Process LaunchCore(string mode, string username, string password, int channel, Action<Process, string> status,
        Action<Stream, Stream, Stream> captureStreams = null)
    {
        string invalid = ValidateAuthInput(username, password, channel);
        if (invalid != null) throw new ArgumentException(invalid);
        string json = new JavaScriptSerializer().Serialize(new { username = username, password = password, channel = channel });
        if (Utf8.GetByteCount(json) + 1 >= 4096) throw new ArgumentException("输入过长，请检查账号和密码。");
        var si = new ProcessStartInfo {
            FileName = corePath, Arguments = mode, WorkingDirectory = tempDir,
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8
        };
        var p = new Process { StartInfo = si, EnableRaisingEvents = true };
        bool started = false;
        try
        {
        // Hold the verified file open without write/delete sharing until
        // CreateProcess returns; do not release it between hashing and launch.
        using (OpenVerifiedCore())
        {
            if (!p.Start()) throw new InvalidOperationException("Core failed to start.");
            started = true;
        }
        int pid = p.Id;
        StreamReader errors = p.StandardError;
        StreamReader output = p.StandardOutput;
        if (captureStreams != null) captureStreams(p.StandardInput.BaseStream, output.BaseStream, errors.BaseStream);
        Task stderrTask = Task.Run(delegate {
            using (errors)
                ReadBoundedEvents(errors, delegate(string line) {
                    string ev = ParseCoreEvent(line, pid);
                    if (ev != null && status != null) status(p, ev);
                });
        });
        Task stdoutTask = Task.Run(delegate {
            using (output)
            {
                char[] buffer = new char[256];
                while (output.Read(buffer, 0, buffer.Length) > 0) { }
            }
        });
        CoreReaders.Add(p, Task.WhenAll(stderrTask, stdoutTask));

            byte[] bytes = Utf8.GetBytes(json + "\n");
            try { p.StandardInput.BaseStream.Write(bytes, 0, bytes.Length); p.StandardInput.BaseStream.Flush(); }
            finally { Array.Clear(bytes, 0, bytes.Length); }
        }
        catch
        {
            if (started) ReapOfflineOrFailedCore(p);
            else p.Dispose();
            throw;
        }
        return p;
    }

    private static bool DrainCoreReaders(Process p, int timeoutMs)
    {
        Task readers;
        if (!CoreReaders.TryGetValue(p, out readers)) return false;
        try { return readers.Wait(timeoutMs); }
        catch (AggregateException) { return false; }
        finally
        {
            if (readers.IsCompleted)
            {
                CoreReaders.Remove(p);
                try { if (p.HasExited) p.StandardInput.Dispose(); } catch { }
            }
        }
    }

    // For a failed launch or an explicitly offline test only; never use this
    // to terminate another client or a privileged service helper.
    private static void ReapOfflineOrFailedCore(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                try { p.StandardInput.Close(); } catch { }
                if (!p.WaitForExit(5000))
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            DrainCoreReaders(p, 5000);
        }
        catch { }
        finally { DisposeCoreProcess(p); }
    }

    private static void DisposeCoreProcess(Process p)
    {
        if (p == null) return;
        // The caller owns redirected streams once it obtains them. Closing
        // stdin also signals EOF if Windows is shutting down.
        try { p.StandardInput.Dispose(); } catch { }
        try { if (p.HasExited) p.StandardOutput.Dispose(); } catch { }
        try { if (p.HasExited) p.StandardError.Dispose(); } catch { }
        p.Dispose();
    }

    private static int SampleHandles(int cycle)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using (Process self = Process.GetCurrentProcess())
        {
            int count = self.HandleCount;
            Console.WriteLine("RESOURCE_SAMPLE cycle=" + cycle + " handles=" + count +
                " private_bytes=" + self.PrivateMemorySize64 + " threads=" + self.Threads.Count);
            return count;
        }
    }

    private static int ResourceTests()
    {
        try
        {
            ExtractCore();
            using (OpenVerifiedCore())
            {
                bool writeDenied = false, deleteDenied = false;
                try { using (var writer = new FileStream(corePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { } }
                catch (IOException) { writeDenied = true; }
                catch (UnauthorizedAccessException) { writeDenied = true; }
                try { File.Move(corePath, corePath + ".moved"); }
                catch (IOException) { deleteDenied = true; }
                catch (UnauthorizedAccessException) { deleteDenied = true; }
                if (!deleteDenied) File.Move(corePath + ".moved", corePath);
                if (!writeDenied || !deleteDenied) return 1;
            }
            Console.WriteLine("CORE_INTEGRITY_HANDLE_PASS");
            const int warmup = 10, measured = 100, budget = 32;
            int baseline = SampleHandles(-warmup), warm = 0, high = 0, finalCount = 0;
            int completed = 0;
            for (int cycle = 0; cycle < warmup + measured; cycle++)
            {
                Process child = null;
                try
                {
                    bool probe = cycle % 3 == 0;
                    using (var accepted = new System.Threading.ManualResetEvent(false))
                    {
                        Stream input = null, output = null, errors = null;
                        child = LaunchCore(probe ? "--ephemeral-probe" : "--ephemeral-check",
                            "dummy-user", "dummy-pass", 1, delegate(Process sender, string value) {
                                if (value == "stop_accepted") accepted.Set();
                            }, delegate(Stream i, Stream o, Stream e) { input = i; output = o; errors = e; });
                        // Capture before readers start: fast check mode can exit before LaunchCore returns.
                        if (probe && (!RequestStop(child) || !accepted.WaitOne(5000))) return 1;
                        if (!child.WaitForExit(10000) || child.ExitCode != 0 || !DrainCoreReaders(child, 5000)) return 1;
                        Task leftover;
                        if (CoreReaders.TryGetValue(child, out leftover) || input.CanWrite || output.CanRead || errors.CanRead)
                        {
                            Console.WriteLine("RESOURCE_STREAM_OPEN cycle=" + cycle);
                            return 1;
                        }
                    }
                    completed++;
                }
                finally { if (child != null) ReapOfflineOrFailedCore(child); }
                int progress = cycle + 1 - warmup;
                if (progress == 0) { warm = SampleHandles(0); high = warm; }
                else if (progress > 0 && progress % 25 == 0)
                {
                    finalCount = SampleHandles(progress);
                    high = Math.Max(high, finalCount);
                }
            }
            Console.WriteLine("RESOURCE_CYCLES=" + completed + " HANDLE_BASELINE=" + baseline +
                " HANDLE_WARM=" + warm + " HANDLE_FINAL=" + finalCount + " HANDLE_HIGH=" + high);
            // Bound post-warmup handle growth, not initial CLR allocations.
            if (high - warm > budget)
            {
                Console.WriteLine("CORE_RESOURCE_HANDLE_GROWTH_FAIL budget=" + budget);
                return 1;
            }
            Console.WriteLine("CORE_RESOURCE_PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("CORE_RESOURCE_FAIL " + e.GetType().Name); return 1; }
        finally { CleanupCore(); }
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
            && ValidateAuthInput("a", "b", 1) == null
            && ValidateAuthInput(new string('a', 16), "b", 1) != null
            && ValidateAuthInput("a", new string('b', 128), 1) != null
            && ValidateAuthInput("a", "b", 0) != null
            && ValidateAuthInput("a", "b\0", 1) != null
            && OperationPermissionInvariants() && ServiceGateInvariants() && CleanupScopeInvariants() && ServicePathInvariants() && HandoffInvariants() && StateMachineInvariants() && CoreEventInvariants() && ServiceJournal.SelfTest() && BootSequenceInvariants() && ServiceIdentity.NativeLayoutIsValid() && ServiceIdentity.IsKnownPublisher("中数通信息有限公司", "")
            && !ServiceIdentity.IsKnownPublisher("fake telecom", "CN=fake telecom");
        Console.WriteLine(ok ? "LOGIC_PASS" : "LOGIC_FAIL");
        return ok ? 0 : 1;
    }

    // 状态机不变量（7.2）：合法迁移被接受、非法迁移被拒绝；操作结果不改变连接状态；
    // 任何路径都不能"跳过认证直接变成已认证"；停止/登出后回到 Idle 而非保持已认证。
    private static bool ServicePathInvariants()
    {
        string reason;
        bool ok = ParseServiceImage("\"C:\\Program Files\\Vendor\\ESurfingSvr.exe\" -service", out reason)
            == @"C:\Program Files\Vendor\ESurfingSvr.exe" && reason == "resolved";
        ok &= ParseServiceImage(@"D:\Vendor\ESurfingSvr.exe -service", out reason)
            == @"D:\Vendor\ESurfingSvr.exe" && reason == "resolved";
        ok &= ParseServiceImage(@"C:\Program Files\Vendor\ESurfingSvr.exe -service", out reason) == null
            && reason == "unquoted_service_path";
        ok &= ParseServiceImage("\"C:\\Vendor\\ESurfingSvr.exe\"invalid", out reason) == null;
        ok &= ParseServiceImage(@"C:\Vendor\ESurfingSvr.exe.fake", out reason) == null;
        ok &= ParseServiceImage(@"ESurfingSvr.exe", out reason) == null;
        ok &= ParseServiceImage(@"\\server\share\ESurfingSvr.exe", out reason) == null;
        ok &= ParseServiceImage(@"C:\Vendor\Other.exe -arg ESurfingSvr.exe", out reason) == null;
        Console.WriteLine(ok ? "SERVICE_PATH_PASS" : "SERVICE_PATH_FAIL");
        return ok;
    }

    private static bool HandoffInvariants()
    {
        var c = ConnectionStates.HandoffIntent.Connect;
        var p = ConnectionStates.HandoffIntent.Prepare;
        var r = ConnectionStates.HandoffIntent.Restore;
        var running = ConnectionStates.ServiceState.Running;
        var stopped = ConnectionStates.ServiceState.Stopped;
        var absent = ConnectionStates.ServiceState.NotInstalled;
        var moving = ConnectionStates.ServiceState.Transitioning;
        var unknown = ConnectionStates.ServiceState.Unknown;
        bool ok = true;
        foreach (var intent in new[] { c, p, r })
        {
            ok &= ConnectionStates.DecideHandoff(intent, unknown, 0) == ConnectionStates.HandoffDecision.BlockUnknown;
            ok &= ConnectionStates.DecideHandoff(intent, running, -1) == ConnectionStates.HandoffDecision.BlockUnknown;
            ok &= ConnectionStates.DecideHandoff(intent, moving, 0) == ConnectionStates.HandoffDecision.WaitForService;
            ok &= ConnectionStates.DecideHandoff(intent, stopped, 1) == ConnectionStates.HandoffDecision.WaitForClient;
        }
        ok &= ConnectionStates.DecideHandoff(c, running, 1) == ConnectionStates.HandoffDecision.WaitForClient;
        ok &= ConnectionStates.DecideHandoff(c, running, 0) == ConnectionStates.HandoffDecision.NeedStopService;
        ok &= ConnectionStates.DecideHandoff(c, stopped, 0) == ConnectionStates.HandoffDecision.Ready;
        ok &= ConnectionStates.DecideHandoff(p, stopped, 0) == ConnectionStates.HandoffDecision.Ready;
        ok &= ConnectionStates.DecideHandoff(p, absent, 0) == ConnectionStates.HandoffDecision.Ready;
        ok &= ConnectionStates.DecideHandoff(r, running, 0) == ConnectionStates.HandoffDecision.LaunchClient;
        ok &= ConnectionStates.DecideHandoff(r, running, 1, true) == ConnectionStates.HandoffDecision.ExistingClient;
        ok &= ConnectionStates.DecideHandoff(r, running, 1, false) == ConnectionStates.HandoffDecision.WaitForClient;
        ok &= ConnectionStates.DecideHandoff(r, stopped, 0) == ConnectionStates.HandoffDecision.NeedStartService;
        var model = new ConnectionState();
        foreach (ConnectionStates.HandoffState phase in Enum.GetValues(typeof(ConnectionStates.HandoffState)))
        { model.SetHandoff(phase); ok &= model.Auth == ConnectionStates.AuthState.Idle && model.PillText != "认证失败"; }
        Console.WriteLine(ok ? "HANDOFF_PASS" : "HANDOFF_FAIL");
        return ok;
    }

    private static int SignatureTests(string signedPath)
    {
        // Read-only verification of an external signed fixture, never execute it.
        string dir = Path.Combine(Path.GetTempPath(), "ChezhiSignatureTest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            if (!ServiceIdentity.NativeLayoutIsValid()) { Console.WriteLine("SIGNATURE_LAYOUT_FAIL"); return 1; }
            if (!ServiceIdentity.HasValidSignature(signedPath)) { Console.WriteLine("SIGNATURE_VALID_FIXTURE_FAIL"); return 1; }
            string damaged = Path.Combine(dir, "damaged.exe");
            File.Copy(signedPath, damaged);
            using (var fs = new FileStream(damaged, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                if (fs.Length < 128) return 1;
                fs.Position = 64;
                int original = fs.ReadByte();
                fs.Position = 64; fs.WriteByte((byte)(original ^ 1));
            }
            string unsigned = Path.Combine(dir, "unsigned.txt");
            File.WriteAllText(unsigned, "unsigned fixture");
            if (ServiceIdentity.HasValidSignature(damaged)) { Console.WriteLine("SIGNATURE_TAMPERED_ACCEPTED"); return 1; }
            if (ServiceIdentity.HasValidSignature(unsigned)) { Console.WriteLine("SIGNATURE_UNSIGNED_ACCEPTED"); return 1; }
            if (!ServiceIdentity.HasValidSignature(signedPath)) { Console.WriteLine("SIGNATURE_REPEAT_FAIL"); return 1; }
            Console.WriteLine("SIGNATURE_PASS");
            return 0;
        }
        catch { return 1; }
        finally { try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { } }
    }

    private static bool CoreEventInvariants()
    {
        string valid = "EPHEMERAL_EVENT {\"v\":1,\"pid\":42,\"event\":\"auth_ok\",\"code\":0,\"error\":\"unknown\"}";
        return ParseCoreEvent(valid, 42) == "auth_ok" && ParseCoreEvent(valid, 43) == null
            && ParseCoreEvent(valid.Replace("\"v\":1", "\"v\":2"), 42) == null
            && ParseCoreEvent(valid.Replace("unknown", "secret"), 42) == null
            && ParseCoreEvent(valid.Replace("auth_ok", "secret"), 42) == null
            && ParseCoreEvent(valid.Replace("auth_ok", "auth_retry_wait"), 42) == null
            && ParseCoreEvent(valid.Replace("auth_ok", "auth_retry_wait").Replace("\"code\":0", "\"code\":3"), 42) == "auth_retry_wait:3"
            && ParseCoreEvent(valid.Replace("auth_ok", "auth_retry_wait").Replace("\"code\":0", "\"code\":36"), 42) == null
            && ParseCoreEvent(valid.Replace("\"code\":0", "\"code\":\"0\""), 42) == null
            && ParseCoreEvent(valid + new string('x', 512), 42) == null
            && ParseCoreEvent("EPHEMERAL_AUTH_OK", 42) == null && BoundedReaderInvariants();
    }

    private static bool BoundedReaderInvariants()
    {
        var lines = new List<string>();
        using (var reader = new StringReader(new string('x', 65536) + "\nvalid\r\npartial"))
            ReadBoundedEvents(reader, lines.Add);
        return lines.Count == 1 && lines[0] == "valid";
    }

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
        ok &= m.TrySetAuth(ConnectionStates.AuthState.WaitingForNetwork, "network-lost");
        ok &= !m.TrySetAuth(ConnectionStates.AuthState.RetryScheduled, "invalid-direct-retry");
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticated, "auth-after-wait");

        // 失败链路：Authenticating → Failed →（重新认证）Authenticating
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Failed, "auth_failed");
        ok &= m.PillText == "认证失败";
        m.Failure = ConnectionStates.FailureKind.Network;
        ok &= m.PillText == "网络异常";
        m.Failure = ConnectionStates.FailureKind.Runtime;
        ok &= m.PillText == "运行中断";
        m.Failure = ConnectionStates.FailureKind.Authentication;
        ok &= m.TrySetAuth(ConnectionStates.AuthState.Authenticating, "manual-retry");
        ok &= m.TrySetAuth(ConnectionStates.AuthState.WaitingForNetwork, "wait");
        ok &= m.TrySetAuth(ConnectionStates.AuthState.AuthenticatedBySession, "online");
        m.SetNetwork(ConnectionStates.NetworkState.InternetAvailable);
        ok &= m.PillText == "已联网·会话";
        m.SetNetwork(ConnectionStates.NetworkState.Unknown);
        ok &= m.PillText == "网络待确认";

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

    private static SavedCredential DummyCredential(string fixtureDirectory)
    {
        // Derive non-secret test data from this test's unique temporary directory.
        // Reproduce the same sample across the offline child process and UI reload.
        string sample = Path.GetFileName(Path.GetFullPath(fixtureDirectory));
        if (string.IsNullOrEmpty(sample)) throw new ArgumentException("Missing fixture directory");
        string sampleText = string.Format("synthetic:{0}:\"\\中文", sample);
        return new SavedCredential { Username = "测试12", Password = sampleText, Channel = 3 };
    }

    private static bool SameCredential(SavedCredential a, SavedCredential b)
    {
        return a != null && b != null && a.Username == b.Username && a.Password == b.Password && a.Channel == b.Channel;
    }

    private static int CredentialReadTest(string dir)
    {
        try { return SameCredential(new CredentialStore(dir).Load(), DummyCredential(dir)) ? 0 : 1; }
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
            SavedCredential value = DummyCredential(dir);
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
            using (var heldLease = new FileStream(store.LeasePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                bool busy = false;
                try { store.Load(); } catch (CredentialStoreBusyException) { busy = true; }
                if (!busy) return 1;
            }
            if (!SameCredential(store.Load(), value)) return 1;
            byte[] invalid = Encoding.ASCII.GetBytes("UNKNOWN-RECORD");
            File.WriteAllBytes(store.FilePath, invalid);
            bool refused = false;
            try { store.Save(value); } catch { refused = true; }
            if (!refused || !File.ReadAllBytes(store.FilePath).SequenceEqual(invalid)) return 1;
            refused = false;
            try { store.Forget(); } catch (InvalidDataException) { refused = true; }
            if (!refused || !File.ReadAllBytes(store.FilePath).SequenceEqual(invalid)) return 1;
            File.WriteAllBytes(store.FilePath, Encoding.ASCII.GetBytes("CZT1"));
            store.Forget();
            if (store.Load() != null) return 1;
            File.WriteAllBytes(store.FilePath, new byte[65537]);
            refused = false;
            try { store.Load(); } catch (InvalidDataException) { refused = true; }
            if (!refused || new FileInfo(store.FilePath).Length != 65537) return 1;
            using (var locked = new FileStream(store.FilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                refused = false;
                try { store.Load(); } catch (IOException) { refused = true; }
                if (!refused) return 1;
            }
            File.Delete(store.FilePath);
            if (store.Load() != null) return 1;
            // Derive the deliberately invalid value from the existing offline
            // fixture, never from a real credential.
            value.Password = DummyCredential(dir).Password + '\0';
            refused = false;
            try { store.Save(value); } catch (InvalidDataException) { refused = true; }
            if (!refused || File.Exists(store.FilePath)) return 1;
            Console.WriteLine("CREDENTIAL_BOUNDED_READ_PASS");
            Console.WriteLine("CREDENTIALS_PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("CREDENTIALS_ERROR " + e.GetType().Name); return 1; }
        finally
        {
            try { if (File.Exists(store.FilePath)) File.Delete(store.FilePath); } catch { }
            try { File.Delete(store.LeasePath); } catch { }
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
                if (!first.CheckAdaptiveLayoutForTest()) return 1;
                if (!first.CheckHandoffCancellationForTest()) return 1;
                if (!first.CheckOperationProjectionForTest()) return 1;
                if (!first.CheckCredentialSaveOrderingForTest()) return 1;
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
            using (var fourth = new MainWindow(dir, true))
            {
                fourth.Show(); Application.DoEvents();
                if (!fourth.CheckCoreEventLifecycleForTest()) return 1;
            }
            using (var fifth = new MainWindow(dir, true))
            {
                fifth.Show(); Application.DoEvents();
                if (!fifth.CheckServiceRecoveryForTest(dir)) return 1;
            }
            // Repeated direct Dispose must clean resources even without FormClosed.
            for (int i = 0; i < 5; i++)
            {
                var window = new MainWindow(dir, true);
                window.Show(); Application.DoEvents();
                window.Dispose();
                if (!window.CheckDisposedResourcesForTest()) return 1;
                window.Dispose();
            }
            Console.WriteLine("UI_DISPOSE_PASS");
            Console.WriteLine("UI_TRAY_PASS");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("UI_TEST_ERROR " + e.GetType().Name); return 1; }
        finally
        {
            CleanupCore(); // 崩溃注入测试释放过核心，统一清理
            try { if (File.Exists(store.FilePath)) File.Delete(store.FilePath); } catch { }
            try { File.Delete(store.LeasePath); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { }
        }
    }

    private static int ApplicationLifecycleTests(bool uiStress = false)
    {
        string dir = Path.Combine(Path.GetFullPath(Path.GetTempPath()), (uiStress ? "ChezhiUiStress-" : "ChezhiAppLifecycle-") + Guid.NewGuid().ToString("N"));
        int result = 1;
        try
        {
            Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
            Control.CheckForIllegalCrossThreadCalls = true;
            using (var window = new MainWindow(dir, true))
            {
                window.Shown += async delegate {
                    try
                    {
                        // A dedicated, real WinForms message loop owns all UI continuations.
                        if (!(System.Threading.SynchronizationContext.Current is WindowsFormsSynchronizationContext))
                            throw new InvalidOperationException("Missing WinForms synchronization context.");
                        bool ok = uiStress ? await window.CheckUiStressForTestAsync() : await window.CheckRepeatedApplicationLifecycleForTestAsync();
                        result = ok ? 0 : 1;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("APP_LIFECYCLE_ERROR " + ex.GetType().Name + "\n" + ex.StackTrace);
                    }
                    finally { Application.ExitThread(); }
                };
                Application.Run(window);
            }
            return result;
        }
        catch (Exception ex) { Console.WriteLine("APP_LIFECYCLE_HOST_ERROR " + ex.GetType().Name + "\n" + ex.StackTrace); return 1; }
        finally
        {
            CleanupCore();
            try { File.Delete(new CredentialStore(dir).LeasePath); } catch { }
            try { if (Directory.Exists(dir)) Directory.Delete(dir, false); } catch { }
        }
    }

    private static int StubTestChild()
    {
        string line;
        while ((line = Console.ReadLine()) != null) if (line == "STOP") return 0;
        return 0;
    }

    private static int NativeNetTests()
    {
        try
        {
            ExtractCore();
            using (var child = new Process { StartInfo = new ProcessStartInfo {
                FileName = corePath, Arguments = "--self-test-net-resources", WorkingDirectory = tempDir,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true } })
            {
                bool started = false;
                try
                {
                    using (OpenVerifiedCore()) { started = child.Start(); }
                    if (!started) return 1;
                    var output = child.StandardOutput.ReadToEndAsync();
                    var errors = child.StandardError.ReadToEndAsync();
                    if (!child.WaitForExit(10000) || !Task.WaitAll(new Task[] { output, errors }, 5000)) return 1;
                    bool ok = child.ExitCode == 0 && errors.Result.Contains("NATIVE_NET_RESOURCE_PASS");
                    Console.WriteLine(ok ? "NATIVE_NET_RESOURCE_PASS" : "NATIVE_NET_RESOURCE_FAIL exit=" + child.ExitCode);
                    return ok ? 0 : 1;
                }
                finally
                {
                    if (started && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                }
            }
        }
        catch (Exception ex) { Console.WriteLine("NATIVE_NET_RESOURCE_ERROR " + ex.GetType().Name); return 1; }
        finally { CleanupCore(); }
    }

    private static int NativeInputTests()
    {
        // Raw bytes exercise the native parser, bypassing GUI validation.
        // Every child is explicitly --ephemeral-check, never network auth.
        try
        {
            ExtractCore();
            string valid = new JavaScriptSerializer().Serialize(new { username = "dummy-user", password = "dummy-pass", channel = 1 });
            var cases = new List<KeyValuePair<byte[], bool>>();
            Action<string, bool> add = (value, ok) => cases.Add(new KeyValuePair<byte[], bool>(Utf8.GetBytes(value), ok));
            add(valid + "\n", true);
            add(valid + " \t\r\n", true);
            add(valid + " {}\n", false);
            add(valid.TrimEnd('}') + ",\"username\":\"duplicate\"}\n", false);
            add(valid.TrimEnd('}') + ",\"extra\":0}\n", false);
            add(valid.Replace("\"username\"", "\"Username\"") + "\n", false);
            add(valid.Replace("dummy-pass", "dummy-pass\\u0000suffix") + "\n", false);
            add(valid.Replace("dummy-pass", "dummy-pass\\\\u0000") + "\n", true);
            add(valid.Replace("dummy-pass", "dummy-pass\0suffix") + "\n", false);
            add(valid.Replace("dummy-pass", "dummy-pass\t") + "\n", false);
            add(valid.Replace("dummy-pass", "dummy-pass\\q") + "\n", false);
            add(valid.Replace("dummy-user", "测试12") + "\n", true);
            add(valid.Replace("dummy-user", new string('a', 16)) + "\n", false);
            add(valid.Replace("dummy-pass", new string('b', 128)) + "\n", false);
            add(valid.Replace("\"channel\":1", "\"channel\":1.5") + "\n", false);
            add(valid.Replace("\"channel\":1", "\"channel\":6") + "\n", false);
            add(valid, false);
            add(valid + new string(' ', 4096) + "\n", false);
            byte[] invalidUtf8 = Utf8.GetBytes(valid + "\n");
            int offset = Array.IndexOf(invalidUtf8, (byte)'d');
            invalidUtf8[offset] = 0xff;
            cases.Add(new KeyValuePair<byte[], bool>(invalidUtf8, false));
            int index = 0;
            foreach (var fixture in cases)
            {
                using (var child = new Process { StartInfo = new ProcessStartInfo {
                    FileName = corePath, Arguments = "--ephemeral-check", WorkingDirectory = tempDir,
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
                    RedirectStandardOutput = true, RedirectStandardError = true } })
                {
                    bool started = false;
                    try
                    {
                        using (OpenVerifiedCore()) { started = child.Start(); }
                        if (!started) return 1;
                        var output = child.StandardOutput.ReadToEndAsync();
                        var errors = child.StandardError.ReadToEndAsync();
                        try { child.StandardInput.BaseStream.Write(fixture.Key, 0, fixture.Key.Length); }
                        catch (IOException) { } // an invalid frame may be rejected before the write completes
                        try { child.StandardInput.Close(); } catch (IOException) { }
                        if (!child.WaitForExit(10000) || !Task.WaitAll(new Task[] { output, errors }, 5000)) return 1;
                        if (child.ExitCode != (fixture.Value ? 0 : 1) ||
                            (errors.Result.Contains("EPHEMERAL_CONFIG_OK") != fixture.Value) ||
                            output.Result.Contains("dummy-pass") || errors.Result.Contains("dummy-pass"))
                        {
                            Console.WriteLine("NATIVE_INPUT_FAIL case=" + index + " exit=" + child.ExitCode + " expected_valid=" + fixture.Value);
                            return 1;
                        }
                    }
                    finally
                    {
                        if (started && !child.HasExited) { child.Kill(); child.WaitForExit(5000); }
                    }
                }
                index++;
            }
            Console.WriteLine("NATIVE_INPUT_PASS cases=" + index);
            return 0;
        }
        catch (Exception ex) { Console.WriteLine("NATIVE_INPUT_ERROR " + ex.GetType().Name); return 1; }
        finally { CleanupCore(); }
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
                    if (value == "stop_accepted") { accepted.Set(); sawEvent.Set(); }
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
            if (p != null) ReapOfflineOrFailedCore(p);
            CleanupCore();
        }
    }

    private sealed class MainWindow : Form
    {
        private static readonly Color FormBgColor = Color.FromArgb(246, 248, 250);
        private static readonly Color CardBgColor = Color.White;

        private readonly TextBox account = new TextBox();
        private readonly TextBox password = new TextBox();
        private readonly AdaptiveDeviceCombo channel = new AdaptiveDeviceCombo();
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
        private ToolStripItem trayStop, trayRecover;
        private readonly CredentialStore credentials;
        private readonly ConnectionState state = new ConnectionState();
        private readonly bool isSelfTest;
#if CHEZHI_LICENSED
        private readonly DesktopLicenseGate licenseGate = new DesktopLicenseGate();
#endif
        private Icon appIcon;
        private bool resourcesDisposed;
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
        private bool runtimeFailureSeen;
        private bool authFailureSeen;  // 本轮收到过 AUTH_FAILED（受控失败，退出不算崩溃，也不自动重试）
        private int autoRetryCount;   // 自动重试计数（每次用户发起的连接最多 2 次：10s、40s 指数退避）
        private readonly Timer retryTimer = new Timer();
        private sealed class AuthTask
        {
            internal readonly string Username, Password;
            internal readonly int Channel;
            internal readonly bool Remember;
            internal AuthTask(string username, string password, int channel, bool remember)
            { Username = username; Password = password; Channel = channel; Remember = remember; }
        }
        private AuthTask activeTask;
        private bool connectCheckPending, retryEligible, rememberRevoked;
        private Func<HandoffObservation> observeHandoff = ObserveHandoff;
        private bool offlineLifecycleProbe;
        private bool powerSuspended;
        private int networkEvidenceEpoch, networkChangeQueued;
        private bool addressEventsSubscribed, availabilityEventsSubscribed;
        private const long NetworkEvidenceTtlMs = 45000;
        private bool offlineProbeReady, offlineProbeStopAccepted, lastCoreReadersDrained;
        private long lastNetworkEvidence;
        private readonly Stopwatch evidenceClock = Stopwatch.StartNew();
        private int taskGeneration;
        private Process completedProcess;
        private Stopwatch grace;
        private readonly ToolTip tips = new ToolTip();
        private readonly Timer animTimer = new Timer();
        private readonly Timer evidenceTimer = new Timer();
        private readonly StatusDot dot = new StatusDot();
        private readonly StatusPill statusPill = new StatusPill();
        private readonly StatusCard statusCard = new StatusCard();
        private readonly TableLayoutPanel statusRow = new TableLayoutPanel();
        private Action reflowLayout;
        private bool layoutBusy, layoutRefreshQueued;
        private Panel scrollRoot;
        private TableLayoutPanel mainLayout;

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
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
            if (appIcon != null) Icon = appIcon;
            StartPosition = FormStartPosition.CenterScreen;
            // All initial dimensions are authored at 96 DPI. Reflow also measures
            // actual font/control metrics, including Windows accessibility text scaling.
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(600, 626);
            MinimumSize = new Size(500, 430);
            BackColor = FormBgColor;
            Font = new Font("Microsoft YaHei UI", 9F);
            DoubleBuffered = true;

            var root = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(20, 16, 20, 14), BackColor = FormBgColor };
            scrollRoot = root;
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty, Padding = Padding.Empty, BackColor = FormBgColor, Height = 580 };
            mainLayout = layout;
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

            // Header: three independent columns. Real labels replace fixed-pixel
            // text drawing, so the title and subtitle cannot overlap.
            var header = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = Padding.Empty, BackColor = FormBgColor };
            header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 54));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            var badge = new HeaderPanel { Dock = DockStyle.Fill, Margin = Padding.Empty };
            var heading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = FormBgColor };
            heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
            heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
            var headingTitle = new Label { Text = "澈知科技", Font = new Font("Microsoft YaHei UI", 14.5F, FontStyle.Bold), ForeColor = Color.FromArgb(17, 24, 39), Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.BottomLeft };
            var headingSub = new Label { Text = "校园网认证 · 移动热点共享", Font = new Font("Microsoft YaHei UI", 8.5F), ForeColor = Color.FromArgb(100, 116, 139), Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft };
            heading.Controls.Add(headingTitle, 0, 0); heading.Controls.Add(headingSub, 0, 1);
            statusPill.Anchor = AnchorStyles.None;
            statusPill.Margin = Padding.Empty;
            statusPill.Font = new Font("Microsoft YaHei UI", 8.5F, FontStyle.Bold);
            header.Controls.Add(badge, 0, 0); header.Controls.Add(heading, 1, 0); header.Controls.Add(statusPill, 2, 0);
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

            var authHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = CardBgColor };
            authHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            authHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            authHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 140));
            var authTitle = new Label { Text = "账号与网络认证", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft, BackColor = CardBgColor };
            var authSubtitle = new Label { Text = "DPAPI 加密保护", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8F), ForeColor = Color.FromArgb(148, 163, 184), TextAlign = ContentAlignment.MiddleRight, BackColor = CardBgColor };
            authTitle.Margin = Padding.Empty; authSubtitle.Margin = new Padding(8, 0, 0, 0); authSubtitle.AutoSize = false;
            authHeader.Controls.Add(authTitle, 0, 0); authHeader.Controls.Add(authSubtitle, 1, 0);
            authLayout.Controls.Add(authHeader, 0, 0);

            // Account field
            account.BorderStyle = BorderStyle.None; account.Font = new Font("Microsoft YaHei UI", 10F); account.Dock = DockStyle.Fill; account.BackColor = CardBgColor;
            var accountBox = new InputContainer(account) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(10, 3, 10, 3), BackColor = CardBgColor };
            AddField(authLayout, "账号", accountBox, 1);

            // Password field
            password.UseSystemPasswordChar = true; password.BorderStyle = BorderStyle.None; password.Font = new Font("Microsoft YaHei UI", 10F); password.Anchor = AnchorStyles.Left | AnchorStyles.Right; password.Margin = new Padding(0, 0, 4, 0); password.BackColor = CardBgColor;
            reveal.Text = "显示"; reveal.Kind = ButtonKind.Normal; reveal.CornerRadius = 4; reveal.Font = new Font("Microsoft YaHei UI", 8.5F); reveal.Dock = DockStyle.Fill; reveal.Margin = new Padding(2, 2, 4, 2);
            var passInner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = CardBgColor };
            passInner.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            passInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            passInner.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56));
            passInner.Controls.Add(password, 0, 0); passInner.Controls.Add(reveal, 1, 0);
            var passwordBox = new InputContainer(passInner) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(10, 2, 2, 2), BackColor = CardBgColor };
            AddField(authLayout, "密码", passwordBox, 2);

            // Channel field
            // Keep native selection, keyboard and accessibility semantics. The fixed
            // owner-drawn items have a measured height instead of OS default metrics.
            channel.Font = new Font("Microsoft YaHei UI", 9.5F);
            channel.Items.AddRange(new object[] { "Windows", "Linux", "Android", "iOS", "macOS" }); channel.SelectedIndex = 0;
            var channelBox = new InputContainer(channel) { Dock = DockStyle.Fill, Height = 32, Margin = new Padding(0, 2, 0, 2), Padding = new Padding(6, 4, 6, 4), BackColor = CardBgColor };
            AddField(authLayout, "设备类型", channelBox, 3);

            // 记住账号密码：仅用户明确勾选后才持久化（连接成功后写入）；默认不勾选。
            // 勾选状态与本地已有记录保持一致；取消勾选不会删除已有记录（用托盘菜单清除）。
            remember.Text = "记住账号密码（仅当前 Windows 用户可读）";
            remember.AutoSize = false; remember.Dock = DockStyle.Fill; remember.Margin = Padding.Empty;
            remember.ForeColor = Color.FromArgb(71, 85, 105); remember.BackColor = CardBgColor;
            remember.TextAlign = ContentAlignment.MiddleLeft; remember.Cursor = Cursors.Hand;
            tips.SetToolTip(remember, "使用 DPAPI 加密。不勾选时凭据只在内存中使用，连接成功也不会写入磁盘。");
            authLayout.Controls.Add(remember, 0, 4);

            // Connection action buttons row
            var actionGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 6, 0, 0), BackColor = CardBgColor };
            actionGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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

            var toolHeader = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = CardBgColor };
            toolHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            toolHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            toolHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 156));
            var toolTitle = new Label { Text = "系统服务与移动热点", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 9.5F, FontStyle.Bold), ForeColor = Color.FromArgb(30, 41, 59), TextAlign = ContentAlignment.MiddleLeft, BackColor = CardBgColor };
            var toolSubtitle = new Label { Text = "服务切换 · 网络共享", Dock = DockStyle.Fill, Font = new Font("Microsoft YaHei UI", 8F), ForeColor = Color.FromArgb(148, 163, 184), TextAlign = ContentAlignment.MiddleRight, BackColor = CardBgColor };
            toolTitle.Margin = Padding.Empty; toolSubtitle.Margin = new Padding(8, 0, 0, 0); toolSubtitle.AutoSize = false;
            toolHeader.Controls.Add(toolTitle, 0, 0); toolHeader.Controls.Add(toolSubtitle, 1, 0);
            toolLayout.Controls.Add(toolHeader, 0, 0);

            var toolGrid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0, 4, 0, 0), BackColor = CardBgColor };
            toolGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
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
            status.Dock = DockStyle.Fill; status.ForeColor = Color.FromArgb(51, 65, 85); status.AutoEllipsis = false; status.Margin = Padding.Empty; status.TextAlign = ContentAlignment.MiddleLeft; status.Font = new Font("Microsoft YaHei UI", 9F);
            statusRow.Dock = DockStyle.Fill; statusRow.ColumnCount = 2; statusRow.RowCount = 1; statusRow.Margin = Padding.Empty; statusRow.Padding = new Padding(10, 0, 10, 0);
            statusRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 28));
            statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            dot.Size = new Size(18, 18); dot.Margin = Padding.Empty; dot.Anchor = AnchorStyles.None;
            statusRow.Controls.Add(dot, 0, 0); statusRow.Controls.Add(status, 1, 0);
            statusCard.Controls.Add(statusRow);
            statusCard.SetTone(ToneInfo);
            layout.Controls.Add(statusCard, 0, 6);

            // Footer
            var footer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = new Padding(4, 0, 4, 0), BackColor = FormBgColor };
            footer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 85));
            var about = new LinkLabel { Text = "使用说明与开源许可", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, LinkColor = Color.FromArgb(37, 99, 235), ActiveLinkColor = Color.FromArgb(29, 78, 216), LinkBehavior = LinkBehavior.HoverUnderline, BackColor = FormBgColor };
            var exit = new LinkLabel { Text = "退出程序", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, LinkColor = Color.FromArgb(100, 116, 139), ActiveLinkColor = Color.FromArgb(220, 38, 38), LinkBehavior = LinkBehavior.HoverUnderline, BackColor = FormBgColor };
            footer.Controls.Add(about, 0, 0); footer.Controls.Add(exit, 1, 0);
            layout.Controls.Add(footer, 0, 8);

            root.Controls.Add(layout);
            Controls.Add(root);
            // Measure the real text and native inputs after the handle exists. Row
            // heights include margins/padding, instead of assuming a fixed font.
            reflowLayout = delegate {
                if (layoutBusy || IsDisposed || !IsHandleCreated) return;
                layoutBusy = true;
                root.SuspendLayout(); layout.SuspendLayout(); authLayout.SuspendLayout(); toolLayout.SuspendLayout();
                try
                {
                    using (Graphics g = CreateGraphics())
                    {
                        Func<int, int> px = value => Math.Max(1, (int)Math.Ceiling(value * g.DpiY / 96F));
                        int available = Math.Max(1, root.ClientSize.Width - root.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth);
                        int innerWidth = Math.Max(px(120), available - authLayout.Padding.Horizontal - px(6));
                        int labelWidth = px(88);
                        foreach (Control row in authLayout.Controls)
                        {
                            var rowTable = row as TableLayoutPanel;
                            if (rowTable == null || !(rowTable.GetControlFromPosition(0, 0) is Label) ||
                                !(rowTable.GetControlFromPosition(1, 0) is InputContainer)) continue;
                            Label fieldLabel = (Label)rowTable.GetControlFromPosition(0, 0);
                            labelWidth = Math.Max(labelWidth, MeasureLine(g, fieldLabel.Text, fieldLabel.Font).Width + px(12));
                        }
                        foreach (Control row in authLayout.Controls)
                        {
                            var rowTable = row as TableLayoutPanel;
                            if (rowTable != null && rowTable.GetControlFromPosition(1, 0) is InputContainer)
                                rowTable.ColumnStyles[0].Width = labelWidth;
                        }

                        int pillWidth = Math.Max(px(116), Math.Max(MeasureLine(g, "已联网·会话", statusPill.Font).Width, MeasureLine(g, "等待天翼退出", statusPill.Font).Width) + px(36));
                        header.ColumnStyles[0].Width = px(56);
                        header.ColumnStyles[2].Width = pillWidth + px(8);
                        statusPill.Size = new Size(pillWidth, Math.Max(px(30), MeasureLine(g, "已联网·会话", statusPill.Font).Height + px(8)));
                        int headingWidth = Math.Max(px(80), available - px(56) - pillWidth - px(8));
                        int titleHeight = MeasureWrapped(g, headingTitle.Text, headingTitle.Font, headingWidth) + px(5);
                        int subHeight = MeasureWrapped(g, headingSub.Text, headingSub.Font, headingWidth) + px(5);
                        heading.RowStyles[0].Height = titleHeight; heading.RowStyles[1].Height = subHeight;
                        layout.RowStyles[0].Height = Math.Max(px(62), Math.Max(titleHeight + subHeight + px(4), statusPill.Height + px(8)));

                        int authSubtitleWidth = Math.Min(innerWidth / 2, MeasureLine(g, authSubtitle.Text, authSubtitle.Font).Width + px(12));
                        authHeader.ColumnStyles[1].Width = authSubtitleWidth;
                        int authTitleHeight = Math.Max(MeasureWrapped(g, authTitle.Text, authTitle.Font, Math.Max(px(60), innerWidth - authSubtitleWidth)),
                            MeasureWrapped(g, authSubtitle.Text, authSubtitle.Font, Math.Max(px(60), authSubtitleWidth - authSubtitle.Margin.Horizontal))) + px(12);
                        channel.ApplyItemMetrics(g);
                        int fieldHeight = Math.Max(px(44), Math.Max(account.PreferredHeight + accountBox.Padding.Vertical,
                            Math.Max(password.PreferredHeight + passwordBox.Padding.Vertical + px(8),
                            Math.Max(channel.PreferredHeight, channel.Height) + channelBox.Padding.Vertical)) + px(10));
                        passInner.ColumnStyles[1].Width = Math.Max(px(62), MeasureLine(g, "隐藏", reveal.Font).Width + px(20));
                        int rememberHeight = Math.Max(px(32), MeasureWrapped(g, remember.Text, remember.Font, Math.Max(px(60), innerWidth - px(28))) + px(12));
                        int actionButtonHeight = Math.Max(px(48), Math.Max(MeasureLine(g, "立即连接", connect.Font).Height,
                            Math.Max(MeasureLine(g, "正在停止…", stop.Font).Height, MeasureLine(g, "保存账号", save.Font).Height)) + px(20));
                        int actionHeight = ReflowButtonGroup(actionGrid, new[] { connect, stop, save },
                            new[] { "立即连接", "正在停止…", "保存账号" }, new[] { 44F, 28F, 28F },
                            g, innerWidth, actionButtonHeight, px(12)) + actionGrid.Margin.Vertical;
                        authLayout.RowStyles[0].Height = authTitleHeight;
                        for (int i = 1; i <= 3; i++) authLayout.RowStyles[i].Height = fieldHeight;
                        authLayout.RowStyles[4].Height = rememberHeight;
                        authLayout.RowStyles[5].Height = actionHeight;
                        layout.RowStyles[2].Height = authTitleHeight + fieldHeight * 3 + rememberHeight + actionHeight + authLayout.Padding.Vertical + authCard.Margin.Vertical + px(4);

                        int toolInnerWidth = Math.Max(px(120), available - toolLayout.Padding.Horizontal - toolCard.Margin.Horizontal);
                        int toolSubtitleWidth = Math.Min(toolInnerWidth / 2, MeasureLine(g, toolSubtitle.Text, toolSubtitle.Font).Width + px(12));
                        toolHeader.ColumnStyles[1].Width = toolSubtitleWidth;
                        int toolTitleHeight = Math.Max(MeasureWrapped(g, toolTitle.Text, toolTitle.Font, Math.Max(px(60), toolInnerWidth - toolSubtitleWidth)),
                            MeasureWrapped(g, toolSubtitle.Text, toolSubtitle.Font, Math.Max(px(60), toolSubtitleWidth - toolSubtitle.Margin.Horizontal))) + px(12);
                        int toolButtonHeight = ReflowButtonGroup(toolGrid, new[] { prepare, restore, hotspot },
                            new[] { "准备连接", "恢复天翼", "热点设置" }, new[] { 33.33F, 33.33F, 33.34F },
                            g, toolInnerWidth, Math.Max(px(42), Math.Max(MeasureLine(g, "准备连接", prepare.Font).Height,
                                Math.Max(MeasureLine(g, "恢复天翼", restore.Font).Height, MeasureLine(g, "热点设置", hotspot.Font).Height)) + px(16)),
                            px(12)) + toolGrid.Margin.Vertical;
                        toolLayout.RowStyles[0].Height = toolTitleHeight;
                        toolLayout.RowStyles[1].Height = toolButtonHeight;
                        layout.RowStyles[4].Height = toolTitleHeight + toolButtonHeight + toolLayout.Padding.Vertical + toolCard.Margin.Vertical + px(4);
                        statusRow.ColumnStyles[0].Width = px(28);
                        dot.Size = new Size(px(18), px(18));
                        layout.RowStyles[6].Height = Math.Max(px(52), MeasureWrapped(g, status.Text, status.Font,
                            Math.Max(px(80), available - statusRow.Padding.Horizontal - px(36))) + px(18) + statusCard.Margin.Vertical);
                        footer.ColumnStyles[1].Width = MeasureLine(g, exit.Text, exit.Font).Width + px(16);
                        layout.RowStyles[8].Height = Math.Max(px(30), Math.Max(
                            MeasureWrapped(g, about.Text, about.Font, Math.Max(1, available - footer.Padding.Horizontal -
                                (int)footer.ColumnStyles[1].Width - about.Margin.Horizontal)),
                            MeasureWrapped(g, exit.Text, exit.Font, Math.Max(1, (int)footer.ColumnStyles[1].Width - exit.Margin.Horizontal))) + px(12));
                        for (int i = 1; i < 8; i += 2) layout.RowStyles[i].Height = px(12);
                        int total = 0;
                        foreach (RowStyle style in layout.RowStyles) total += (int)Math.Ceiling(style.Height);
                        layout.Height = total;
                        root.AutoScrollMinSize = new Size(0, total + root.Padding.Vertical);
                    }
                }
                finally
                {
                    toolLayout.ResumeLayout(true); authLayout.ResumeLayout(true); layout.ResumeLayout(true); root.ResumeLayout(true); layoutBusy = false;
                }
            };
            root.SizeChanged += delegate { RefreshAdaptiveLayout(); };
            status.TextChanged += delegate { RefreshAdaptiveLayout(); };
            foreach (Control control in Descendants(layout))
                if (control is Label || control is Button || control is CheckBox || control is TextBox ||
                    control is ComboBox || control is StatusPill)
                    control.FontChanged += delegate { QueueAdaptiveLayout(); };
            connect.TextChanged += delegate { QueueAdaptiveLayout(); };
            stop.TextChanged += delegate { QueueAdaptiveLayout(); };
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
            catch (CredentialStoreBusyException)
            {
                credentialLoadError = "保存的账号正在被另一个操作使用，请稍后重新打开程序。";
            }
            catch { credentialLoadError = "读取已保存的账号失败，原有文件未改动。请确认与保存时使用同一个 Windows 用户。"; }

            tray.Icon = appIcon != null ? appIcon : SystemIcons.Application; // 复用同一个 Icon, 关闭时统一释放
            tray.Text = "澈知科技 - 校园网";
            trayMenu.Items.Add("打开主窗口", null, delegate { ShowFromTray(); });
            trayStop = trayMenu.Items.Add("停止认证", null, delegate { ShowFromTray(); StopClicked(stop, EventArgs.Empty); });
            trayMenu.Items.Add("清除已保存账号", null, delegate { ShowFromTray(); ForgetCredentials(); });
#if CHEZHI_LICENSED
            trayMenu.Items.Add("查看授权状态", null, delegate {
                ShowFromTray();
                MessageBox.Show(this, licenseGate.Message, "软件授权", MessageBoxButtons.OK, MessageBoxIcon.Information);
            });
            trayMenu.Items.Add("更换授权卡密", null, async delegate {
                ShowFromTray();
                if (serviceBusy || connectCheckPending || activeTask != null || exitRequested)
                { SetStatus("请先停止认证及待处理连接，再更换授权卡密。", ToneWarn); return; }
                if (MessageBox.Show(this, licenseGate.Message + "\n\n是否输入或更换授权卡密？", "软件授权",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    bool ok = await licenseGate.VerifyAsync(this, true, true);
                    if (!IsDisposed && !Disposing) SetStatus(licenseGate.Message, ok ? ToneOk : ToneWarn);
                }
            });
            trayMenu.Items.Add("清除已保存授权卡密", null, async delegate {
                ShowFromTray();
                if (serviceBusy || connectCheckPending || activeTask != null || exitRequested) return;
                if (MessageBox.Show(this, "仅清除本地卡密，不解绑服务器或删除设备密钥。确定？", "软件授权",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                bool ok = await licenseGate.ForgetAsync();
                if (!IsDisposed && !Disposing) SetStatus(licenseGate.Message, ok ? ToneInfo : ToneWarn);
            });
#endif
            trayMenu.Items.Add("导出诊断信息", null, delegate { ShowFromTray(); ExportDiagnostics(); });
            trayRecover = trayMenu.Items.Add("核对遗留服务事务", null, async delegate { ShowFromTray(); await RecoverInterruptedJournalAsync(); });
            trayMenu.Opening += delegate { RefreshOperationPermissions(); };
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
            remember.CheckedChanged += delegate { if (!remember.Checked && activeTask != null) rememberRevoked = true; };
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
                FitInitialWindow();
                RefreshAdaptiveLayout();
                // A11：WMI 检查（冷启动可达数秒）移出 UI 线程，先给即时提示
                if (credentialLoadError != null) SetStatus(credentialLoadError, ToneWarn);
                else if (loadedCredential) SetStatus("已载入保存的账号，正在检查天翼服务状态……", ToneOk);
                else SetStatus("正在检查天翼服务状态……", ToneInfo);
                if (!isSelfTest) { SubscribeNetworkChanges(); RunStartupChecksAsync(); }
            };
            evidenceTimer.Interval = 5000;
            evidenceTimer.Tick += async delegate {
                if (IsDisposed || Disposing) return;
                // Expiry must not wait behind a slow WMI/SCM observation.
                ExpireNetworkEvidence(evidenceClock.ElapsedMilliseconds);
#if CHEZHI_LICENSED
                // Existing session remains usable for safe recovery; no
                // fresh protected operation may bypass the online gate.
                if (!isSelfTest && !serviceBusy && !connectCheckPending && !stopping &&
                    !exitRequested && !powerSuspended && activeTask != null &&
                    running != null && !Object.ReferenceEquals(running, completedProcess))
                {
                    int licenseEpoch = taskGeneration;
                    Process licenseProcess = running;
                    bool attempted = await licenseGate.RenewIfDueAsync();
                    if (IsDisposed || Disposing || resourcesDisposed) return;
                    if (attempted && licenseEpoch == taskGeneration && Object.ReferenceEquals(running, licenseProcess) &&
                        !licenseGate.Allowed && !stopping && !userStopped && !exitRequested && !serviceBusy && !connectCheckPending)
                        SetStatus(licenseGate.Message + " 当前会话暂不强制中断；新连接需重新验证。", ToneWarn);
                }
#endif
                if (pendingServiceChild != null) await PollTimedOutServiceAsync();
                else if (!isSelfTest && !serviceBusy && !connectCheckPending && !retryTimer.Enabled && activeTask == null)
                    await RunPreConnectCheckAsync(false, true);
            };
            evidenceTimer.Start();
            timer.Interval = 1000;
            timer.Tick += delegate {
                if (stopping && running != null && !running.HasExited && grace.Elapsed.TotalSeconds >= 100)
                {
                    timer.Stop(); RefreshOperationPermissions();
                    SetStatus("等待停止已超过 100 秒。可点“强制结束”，服务器上的会话可能稍后才释放。", ToneFail);
                }
            };
            FormClosing += OnClosing;
            RefreshOperationPermissions();
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing && !resourcesDisposed)
                {
                    resourcesDisposed = true;
#if CHEZHI_LICENSED
                    licenseGate.Dispose();
#endif
                    userStopped = true; taskGeneration++; activeTask = null;
                    idleCheckLifetime.Cancel();
                    idleCheckLifetime.Dispose();
                    SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                    SystemEvents.SessionSwitch -= OnSessionSwitch;
                    UnsubscribeNetworkChanges();
                    timer.Stop(); animTimer.Stop(); retryTimer.Stop(); evidenceTimer.Stop();
                    timer.Dispose(); animTimer.Dispose(); retryTimer.Dispose(); evidenceTimer.Dispose();
                    password.Clear(); tray.Visible = false; tray.Dispose(); trayMenu.Dispose(); tips.Dispose();
                    if (running != null) DisposeCoreProcess(running);
                    if (pendingServiceChild != null) pendingServiceChild.Dispose();
                    if (appIcon != null) { appIcon.Dispose(); appIcon = null; }
                }
            }
            finally { base.Dispose(disposing); }
        }

        private static Size MeasureLine(Graphics graphics, string value, Font font)
        {
            return TextRenderer.MeasureText(graphics, value ?? "", font, new Size(32767, 32767),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }

        private static int MeasureWrapped(Graphics graphics, string value, Font font, int width)
        {
            return TextRenderer.MeasureText(graphics, value ?? "", font, new Size(Math.Max(1, width), 32767),
                TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix).Height;
        }

        // Use a full-width vertical group only when the regular columns cannot
        // fit the actual font metrics. No controls are recreated during reflow.
        private static int ReflowButtonGroup(TableLayoutPanel grid, ModernButton[] buttons,
            string[] longestLabels, float[] weights, Graphics graphics, int available, int height, int padding)
        {
            bool stacked = false;
            for (int i = 0; i < buttons.Length; i++)
            {
                int needed = Math.Max(MeasureLine(graphics, buttons[i].Text, buttons[i].Font).Width,
                    MeasureLine(graphics, longestLabels[i], buttons[i].Font).Width) + padding * 2 +
                    (i == buttons.Length - 1 ? 0 : 8); // use the target horizontal margin, not the previous mode
                if (available * weights[i] / 100F < needed) stacked = true;
            }
            int rows = stacked ? buttons.Length : 1, columns = stacked ? 1 : buttons.Length;
            if (grid.RowCount != rows || grid.ColumnCount != columns)
            {
                grid.SuspendLayout();
                try
                {
                    grid.Controls.Clear();
                    grid.RowStyles.Clear(); grid.ColumnStyles.Clear();
                    grid.RowCount = rows; grid.ColumnCount = columns;
                    for (int i = 0; i < rows; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100F / rows));
                    for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, stacked ? 100F : weights[i]));
                    for (int i = 0; i < buttons.Length; i++) grid.Controls.Add(buttons[i], stacked ? 0 : i, stacked ? i : 0);
                }
                finally { grid.ResumeLayout(true); }
            }
            // Include a vertical gap in stacked mode; retain a stable horizontal margin.
            for (int i = 0; i < buttons.Length; i++)
                buttons[i].Margin = stacked ? new Padding(0, 0, 0, i == buttons.Length - 1 ? 0 : padding / 2) :
                    new Padding(0, 0, i == buttons.Length - 1 ? 0 : 8, 0);
            return rows * (height + (stacked ? padding / 2 : 0));
        }

        private void QueueAdaptiveLayout()
        {
            if (layoutRefreshQueued || IsDisposed || Disposing || resourcesDisposed || !IsHandleCreated) return;
            layoutRefreshQueued = true;
            try { BeginInvoke((Action)delegate { layoutRefreshQueued = false; RefreshAdaptiveLayout(); }); }
            catch (InvalidOperationException) { layoutRefreshQueued = false; }
        }

        private void RefreshAdaptiveLayout()
        {
            if (!IsDisposed && !Disposing && !resourcesDisposed && reflowLayout != null) reflowLayout();
        }

        private void FitInitialWindow()
        {
            RefreshAdaptiveLayout();
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int wanted = mainLayout.Height + scrollRoot.Padding.Vertical + (Height - ClientSize.Height);
            int height = Math.Min(work.Height, Math.Max(Height, wanted + 4));
            int width = Math.Min(work.Width, Width);
            Size = new Size(width, height);
            Location = new Point(work.Left + Math.Max(0, (work.Width - Width) / 2),
                work.Top + Math.Max(0, (work.Height - Height) / 2));
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_DPICHANGED = 0x02E0;
            base.WndProc(ref m);
            if (m.Msg == WM_DPICHANGED && IsHandleCreated && !IsDisposed)
                try { BeginInvoke((Action)RefreshAdaptiveLayout); } catch (InvalidOperationException) { }
        }

        internal void ShowFromTray()
        {
            if (IsDisposed) return;
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            RefreshAdaptiveLayout();
            BringToFront(); Activate();
        }

        private void HideToTray()
        {
            password.UseSystemPasswordChar = true; reveal.Text = "显示";
            Hide();
            if (!isSelfTest) try { tray.ShowBalloonTip(1500, "澈知科技", "已最小化到托盘，程序仍在运行；双击图标可重新打开。", ToolTipIcon.Info); } catch { }
        }

        internal bool CheckAdaptiveLayoutForTest()
        {
            Size original = Size;
            var fonts = new Dictionary<Control, Font>();
            var enlarged = new List<Font>();
            string oldStatus = status.Text, oldConnect = connect.Text, oldStop = stop.Text;
            int oldChannel = channel.SelectedIndex;
            int cases = 0;
            try
            {
                RefreshAdaptiveLayout(); Application.DoEvents();
                if (!CheckLayoutBounds(mainLayout, "normal")) return false;
                foreach (Control c in Descendants(mainLayout))
                {
                    if (!(c is Label || c is Button || c is CheckBox || c is TextBox || c is ComboBox || c is StatusPill)) continue;
                    fonts[c] = c.Font;
                }
                float[] scales = { 1F, 1.25F, 1.5F, 2F };
                Size[] sizes = { MinimumSize, new Size(Math.Max(MinimumSize.Width, 620), Math.Max(MinimumSize.Height, 540)),
                    new Size(Math.Max(MinimumSize.Width, 820), Math.Max(MinimumSize.Height, 720)) };
                string[] messages = { "输入校园网账号和密码，点击“立即连接”即可。",
                    "正在等待接入校园网，请检查电脑的 Wi-Fi 或网线连接；如遇网络异常，可停止并重新连接。",
                    "系统服务正在切换。请不要关闭程序或重复连接，等待服务实际状态确认及事务记录保存完成后再重新选择操作。" };
                foreach (float scale in scales)
                {
                    foreach (var item in fonts)
                    {
                        var f = new Font(item.Value.FontFamily, item.Value.Size * scale, item.Value.Style);
                        enlarged.Add(f); item.Key.Font = f;
                    }
                    foreach (Size size in sizes)
                    {
                        Size = size;
                        for (int variant = 0; variant < messages.Length; variant++)
                        {
                            status.Text = messages[variant];
                            connect.Text = variant == 0 ? "立即连接" : "连接中…";
                            stop.Text = variant == 0 ? "停止认证" : variant == 1 ? "正在停止…" : "强制结束";
                            RefreshAdaptiveLayout(); Application.DoEvents();
                            string stage = "scale=" + scale.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                                " size=" + Size + " variant=" + variant;
                            if (!CheckLayoutBounds(mainLayout, stage) || !CheckTableOverlap(mainLayout, stage)) return false;
                            if (channel.Items.Count != 5 || channel.SelectedIndex != oldChannel)
                            { Console.WriteLine("UI_LAYOUT_CHANNEL_SELECTION_CHANGED " + stage); return false; }
                            cases++;
                        }
                    }
                }
                Console.WriteLine("UI_LAYOUT_MATRIX_PASS cases=" + cases + " font_scales=4 window_sizes=3 status_variants=3");
                Console.WriteLine("UI_LAYOUT_PASS");
                return true;
            }
            finally
            {
                foreach (var item in fonts) item.Key.Font = item.Value;
                foreach (Font font in enlarged) font.Dispose();
                status.Text = oldStatus; connect.Text = oldConnect; stop.Text = oldStop; Size = original;
                RefreshAdaptiveLayout(); Application.DoEvents();
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern uint GetGuiResources(IntPtr process, uint flags);

        private static int[] SampleUiResources(int cycle)
        {
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using (Process self = Process.GetCurrentProcess())
            {
                int gdi = checked((int)GetGuiResources(self.Handle, 0));
                int user = checked((int)GetGuiResources(self.Handle, 1));
                int handles = self.HandleCount;
                Console.WriteLine("UI_RESOURCE_SAMPLE cycle=" + cycle + " gdi=" + gdi +
                    " user=" + user + " handles=" + handles + " private_bytes=" + self.PrivateMemorySize64);
                return new[] { gdi, user, handles };
            }
        }

        private async Task<bool> CheckScrollReachabilityForTestAsync(string stage)
        {
            // Scroll only through the existing native WinForms scroll container.
            // No control is clicked and no real network or service action is executed.
            var targets = new List<Control> { account, password, reveal, channel, remember, connect, stop, save, prepare, restore, hotspot };
            targets.AddRange(Descendants(mainLayout).Where(c => c is LinkLabel));
            scrollRoot.AutoScrollPosition = Point.Empty;
            foreach (Control target in targets)
            {
                scrollRoot.ScrollControlIntoView(target);
                await FlushUiEventsForTest();
                Rectangle actual = scrollRoot.RectangleToClient(target.RectangleToScreen(target.ClientRectangle));
                Rectangle visible = scrollRoot.ClientRectangle;
                visible.Inflate(2, 2); // same rounding allowance as the existing layout checks
                if (!target.Visible || !visible.Contains(actual))
                {
                    Console.WriteLine("UI_SCROLL_UNREACHABLE " + stage + " control=" + target.GetType().Name +
                        " bounds=" + actual + " viewport=" + scrollRoot.ClientRectangle +
                        " scroll=" + scrollRoot.AutoScrollPosition);
                    return false;
                }
            }
            scrollRoot.AutoScrollPosition = Point.Empty;
            return targets.Count == 13;
        }

        internal async Task<bool> CheckUiStressForTestAsync()
        {
            if (!isSelfTest || InvokeRequired) return false;
            int thread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            const int warmup = 10, measured = 100;
            // Sample the same live window after warm-up; these budgets allow
            // bounded framework caching without accepting unbounded growth.
            int[] limits = { 32, 32, 64 };
            int[] warm = null, high = null, final = null;
            var original = new Dictionary<Control, Font>();
            var large = new Dictionary<Control, Font>();
            Size originalSize = Size;
            string originalStatus = status.Text;
            int originalChannel = channel.SelectedIndex;
            int scrollChecks = 0, trayCycles = 0;
            try
            {
                foreach (Control c in Descendants(mainLayout))
                {
                    if (!(c is Label || c is Button || c is CheckBox || c is TextBox || c is ComboBox || c is StatusPill)) continue;
                    original[c] = c.Font;
                    large[c] = new Font(c.Font.FontFamily, c.Font.Size * 2F, c.Font.Style);
                }
                account.Text = "offline-user"; password.Text = "offline-pass";
                remember.Checked = false;
                for (int cycle = 0; cycle < warmup + measured; cycle++)
                {
                    bool compact = cycle % 2 == 0;
                    foreach (var entry in original) entry.Key.Font = compact ? large[entry.Key] : entry.Value;
                    Size = compact ? MinimumSize : new Size(Math.Max(MinimumSize.Width, 820), Math.Max(MinimumSize.Height, 720));
                    channel.SelectedIndex = cycle % channel.Items.Count;
                    int selected = channel.SelectedIndex;
                    status.Text = compact ? "系统服务正在切换。请不要关闭程序或重复连接，等待服务实际状态确认及事务记录保存完成后再重新选择操作。" :
                        "输入校园网账号和密码，点击“立即连接”即可。";
                    if (cycle % 5 == 0)
                    {
                        password.UseSystemPasswordChar = false;
                        HideToTray();
                        await FlushUiEventsForTest();
                        if (Visible || !password.UseSystemPasswordChar) return false;
                        ShowFromTray();
                        trayCycles++;
                    }
                    RefreshAdaptiveLayout();
                    await FlushUiEventsForTest();
                    if (InvokeRequired || thread != System.Threading.Thread.CurrentThread.ManagedThreadId) return false;
                    string stage = "stress_cycle=" + cycle + " compact=" + compact;
                    if (!CheckLayoutBounds(mainLayout, stage) || !CheckTableOverlap(mainLayout, stage) ||
                        !Visible || channel.SelectedIndex != selected || channel.Items.Count != 5 ||
                        account.Text != "offline-user" || password.Text != "offline-pass" ||
                        state.Auth != ConnectionStates.AuthState.Idle || running != null || activeTask != null ||
                        !connect.Enabled || stop.Enabled || !prepare.Enabled || !restore.Enabled)
                    { Console.WriteLine("UI_STRESS_INVARIANT_FAIL " + stage); return false; }
                    if (compact)
                    {
                        if (!await CheckScrollReachabilityForTestAsync(stage)) return false;
                        scrollChecks += 13;
                    }
                    // Exercise actual drawing, not only geometry. The bitmap is owned
                    // by this iteration and disposed before sampling.
                    using (var bitmap = new Bitmap(Math.Max(1, ClientSize.Width), Math.Max(1, ClientSize.Height)))
                        DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                    int progress = cycle + 1 - warmup;
                    if (progress == 0)
                    {
                        warm = SampleUiResources(0);
                        if (warm[0] <= 0 || warm[1] <= 0 || warm[2] <= 0) return false;
                        high = (int[])warm.Clone();
                    }
                    else if (progress > 0 && progress % 10 == 0)
                    {
                        final = SampleUiResources(progress);
                        for (int i = 0; i < high.Length; i++)
                        {
                            if (final[i] <= 0) return false;
                            high[i] = Math.Max(high[i], final[i]);
                            if (high[i] - warm[i] > limits[i])
                            { Console.WriteLine("UI_RESOURCE_GROWTH_FAIL metric=" + i + " warm=" + warm[i] + " high=" + high[i] + " budget=" + limits[i]); return false; }
                        }
                    }
                }
                if (final == null || scrollChecks != 715 || trayCycles != 22) return false;
                Console.WriteLine("UI_SCROLL_PASS checks=" + scrollChecks + " compact_cycles=55");
                Console.WriteLine("UI_STRESS_PASS warmup=10 measured=100 tray_cycles=" + trayCycles +
                    " gdi_warm=" + warm[0] + " gdi_final=" + final[0] + " gdi_high=" + high[0] +
                    " user_warm=" + warm[1] + " user_final=" + final[1] + " user_high=" + high[1] +
                    " handles_warm=" + warm[2] + " handles_final=" + final[2] + " handles_high=" + high[2]);
                return true;
            }
            finally
            {
                foreach (var entry in original) entry.Key.Font = entry.Value;
                foreach (Font font in large.Values) font.Dispose();
                channel.SelectedIndex = originalChannel;
                account.Clear(); password.Clear();
                status.Text = originalStatus; Size = originalSize;
                scrollRoot.AutoScrollPosition = Point.Empty;
                RefreshAdaptiveLayout();
            }
        }

        private static bool CheckTableOverlap(Control parent, string stage)
        {
            foreach (Control c in Descendants(parent))
            {
                var table = c as TableLayoutPanel;
                if (table == null) continue;
                for (int i = 0; i < table.Controls.Count; i++)
                    for (int j = i + 1; j < table.Controls.Count; j++)
                    {
                        Control first = table.Controls[i], second = table.Controls[j];
                        if (!first.Visible || !second.Visible) continue;
                        Rectangle overlap = Rectangle.Intersect(first.Bounds, second.Bounds);
                        if (overlap.Width > 2 && overlap.Height > 2)
                        {
                            Console.WriteLine("UI_LAYOUT_OVERLAP " + stage + " " + first.GetType().Name + " " + second.GetType().Name);
                            return false;
                        }
                    }
            }
            return true;
        }

        private static IEnumerable<Control> Descendants(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                yield return child;
                foreach (Control descendant in Descendants(child)) yield return descendant;
            }
        }

        private bool CheckLayoutBounds(Control parent, string stage)
        {
            using (Graphics g = CreateGraphics())
            {
                foreach (Control c in Descendants(parent))
                {
                    if (c.Width <= 0 || c.Height <= 0) { Console.WriteLine("UI_LAYOUT_EMPTY " + c.GetType().Name); return false; }
                    var label = c as Label;
                    if (label != null && label.Text.Length > 0)
                    {
                        int h = MeasureWrapped(g, label.Text, label.Font, Math.Max(1, label.ClientSize.Width - label.Padding.Horizontal));
                        if (h > label.ClientSize.Height + 2)
                        { Console.WriteLine("UI_LAYOUT_LABEL_CLIPPED " + stage + " " + label.Text + " " + h + "/" + label.Height + " width=" + label.Width); return false; }
                    }
                    var check = c as CheckBox;
                    if (check != null && check.Height < MeasureWrapped(g, check.Text, check.Font,
                        Math.Max(1, check.ClientSize.Width - (int)(28 * g.DpiX / 96F))))
                    { Console.WriteLine("UI_LAYOUT_CHECKBOX_CLIPPED"); return false; }
                    var button = c as ModernButton;
                    if (button != null && (MeasureLine(g, button.Text, button.Font).Width > button.ClientSize.Width || MeasureLine(g, button.Text, button.Font).Height > button.ClientSize.Height))
                    { Console.WriteLine("UI_LAYOUT_BUTTON_CLIPPED " + stage + " " + button.Text + " " + button.Size); return false; }
                    var input = c as TextBox;
                    if (input != null && input.Height < input.PreferredHeight)
                    { Console.WriteLine("UI_LAYOUT_INPUT_CLIPPED"); return false; }
                    var combo = c as ComboBox;
                    if (combo != null)
                    {
                        int textHeight = 0;
                        foreach (object item in combo.Items)
                            textHeight = Math.Max(textHeight, MeasureLine(g, combo.GetItemText(item), combo.Font).Height);
                        var adaptive = combo as AdaptiveDeviceCombo;
                        int nativeSelection = adaptive == null ? -1 : adaptive.ReadNativeItemHeight(-1);
                        int nativeList = adaptive == null ? -1 : adaptive.ReadNativeItemHeight(0);
                        if (combo.Height < combo.PreferredHeight || combo.ClientSize.Height < textHeight ||
                            combo.ItemHeight < textHeight || nativeSelection < textHeight || nativeList < textHeight)
                        {
                            Console.WriteLine("UI_LAYOUT_COMBO_CLIPPED " + stage +
                                " actual=" + combo.Height + " preferred=" + combo.PreferredHeight +
                                " itemHeight=" + combo.ItemHeight + " textHeight=" + textHeight +
                                " nativeSelection=" + nativeSelection + " nativeList=" + nativeList +
                                " client=" + combo.ClientSize + " font=" + combo.Font.Size +
                                " formatting=" + combo.FormattingEnabled + " dpi=" + g.DpiY);
                            return false;
                        }
                    }
                    var pill = c as StatusPill;
                    if (pill != null && (MeasureLine(g, "等待天翼退出", pill.Font).Width + (int)Math.Ceiling(30 * g.DpiX / 96F) > pill.ClientSize.Width ||
                        MeasureLine(g, "等待天翼退出", pill.Font).Height > pill.ClientSize.Height))
                    { Console.WriteLine("UI_LAYOUT_PILL_CLIPPED " + stage); return false; }
                    if (c.Parent != null && c.Parent != scrollRoot &&
                        (c.Left < -2 || c.Top < -2 || c.Right > c.Parent.ClientSize.Width + 2 || c.Bottom > c.Parent.ClientSize.Height + 2))
                    { Console.WriteLine("UI_LAYOUT_OUTSIDE " + stage + " " + c.GetType().Name + " child=" + c.Bounds + " parent=" + c.Parent.GetType().Name + " size=" + c.Parent.ClientSize); return false; }
                }
            }
            return true;
        }

        internal bool CheckOperationProjectionForTest()
        {
            bool ok = true;
            // Idle -> pending connect -> cancellation -> service ownership -> idle.
            Action<bool, bool, bool, string> check = delegate(bool allow, bool canStop, bool recover, string label) {
                RefreshOperationPermissions();
                ok &= connect.Enabled == allow && prepare.Enabled == allow && restore.Enabled == allow &&
                    stop.Enabled == canStop && trayStop.Enabled == canStop && trayRecover.Enabled == recover &&
                    stop.Text == label && trayStop.Text == label;
            };
            userStopped = false;
            check(true, false, true, "停止认证");
            connectCheckPending = true;
            check(false, true, false, "取消连接");
            serviceBusy = true;
            check(false, false, false, "停止认证");
            serviceBusy = false; userStopped = true;
            check(false, false, false, "停止认证");
            connectCheckPending = false;
            check(true, false, true, "停止认证");
            retryTimer.Start();
            check(false, true, false, "取消重试");
            retryTimer.Stop();
            check(true, false, true, "停止认证");
            Console.WriteLine(ok ? "UI_OPERATION_PASS" : "UI_OPERATION_FAIL");
            return ok;
        }

        internal bool CheckHandoffCancellationForTest()
        {
            // Exercise the actual asynchronous preflight with injected observations.
            // Never invoke authentication, elevation, or a real network operation.
            account.Text = "test-user"; password.Text = "test-password";
            activeTask = new AuthTask(account.Text, password.Text, 1, false);
            userStopped = false;
            int generation = ++taskGeneration;
            observeHandoff = delegate { return new HandoffObservation(ConnectionStates.ServiceState.Running, 1); };
            Task work = CheckAndStartAsync(generation);
            var clock = Stopwatch.StartNew();
            while (state.Handoff != ConnectionStates.HandoffState.WaitingForClient && !work.IsCompleted && clock.ElapsedMilliseconds < 3000)
            { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            bool ok = state.Handoff == ConnectionStates.HandoffState.WaitingForClient && !connect.Enabled && stop.Enabled;
            BeginStop();
            observeHandoff = delegate { return new HandoffObservation(ConnectionStates.ServiceState.Stopped, 0); };
            while (!work.IsCompleted && clock.ElapsedMilliseconds < 6000)
            { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            ok &= work.Status == TaskStatus.RanToCompletion && running == null && activeTask == null &&
                state.Auth == ConnectionStates.AuthState.Idle && state.Handoff == ConnectionStates.HandoffState.None &&
                connect.Enabled && !stop.Enabled && prepare.Enabled && restore.Enabled;
            observeHandoff = ObserveHandoff;
            Console.WriteLine(ok ? "HANDOFF_CANCEL_PASS" : "HANDOFF_CANCEL_FAIL");
            return ok;
        }

        internal bool CheckCredentialSaveOrderingForTest()
        {
            try
            {
                account.Text = "old-user"; password.Text = "old-password"; channel.SelectedIndex = 0;
                remember.Checked = true; rememberRevoked = false; savedAfterAuth = false;
                activeTask = new AuthTask(account.Text, password.Text, 1, true);
                account.Text = "new-user"; password.Text = "new-password";
                if (!SaveCredentials(false)) return false;
                SaveAfterAuth();
                var saved = credentials.Load();
                bool ok = saved != null && saved.Username == "new-user" && saved.Password == "new-password";
                Console.WriteLine(ok ? "CREDENTIAL_ORDER_PASS" : "CREDENTIAL_ORDER_FAIL");
                return ok;
            }
            finally { activeTask = null; rememberRevoked = false; savedAfterAuth = false; }
        }

        private static Process StartOfflineStub()
        {
            return Process.Start(new ProcessStartInfo { FileName = Application.ExecutablePath,
                Arguments = "--self-test-stub", UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true });
        }

        private static bool PumpTaskForTest(Task task, int timeoutMs)
        {
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted && clock.ElapsedMilliseconds < timeoutMs)
            { Application.DoEvents(); System.Threading.Thread.Sleep(10); }
            Application.DoEvents();
            return task.Status == TaskStatus.RanToCompletion;
        }

        internal bool CheckCoreEventLifecycleForTest()
        {
            Process stub = StartOfflineStub();
            if (stub == null) return false;
            try
            {
                running = stub; completedProcess = null;
                activeTask = new AuthTask("dummy-user", "dummy-pass", 1, false);
                userStopped = false; stopping = false; authFailureSeen = false; retryEligible = false;
                state.SetCore(ConnectionStates.CoreState.Running);
                SetAuthState(ConnectionStates.AuthState.Authenticating, "test", ToneInfo);
                OnCoreStatus(stub, "stop_accepted"); Application.DoEvents();
                bool ok = state.Auth == ConnectionStates.AuthState.Authenticating;
                OnCoreStatus(stub, "auth_ok"); Application.DoEvents();
                OnCoreStatus(stub, "network_online"); Application.DoEvents();
                OnCoreStatus(stub, "auth_ok"); Application.DoEvents();
                ok &= state.Auth == ConnectionStates.AuthState.Authenticated &&
                    state.Network == ConnectionStates.NetworkState.InternetAvailable;
                OnCoreStatus(stub, "auth_failed"); Application.DoEvents();
                OnCoreStatus(stub, "network_online"); Application.DoEvents();
                ok &= state.Auth == ConnectionStates.AuthState.Failed && state.Network == ConnectionStates.NetworkState.Unknown;
                BeginStop();
                ok &= userStopped && state.Auth == ConnectionStates.AuthState.LoggingOut;
                OnCoreStatus(stub, "auth_ok"); Application.DoEvents();
                ok &= state.Auth == ConnectionStates.AuthState.LoggingOut;
                if (!stub.WaitForExit(5000)) return false;
                OnCoreExit(stub);
                OnCoreExit(stub);
                OnCoreStatus(stub, "network_online"); Application.DoEvents();
                ok &= state.Auth == ConnectionStates.AuthState.Idle && state.Network == ConnectionStates.NetworkState.Unknown &&
                    activeTask == null && !retryTimer.Enabled && connect.Enabled && prepare.Enabled && restore.Enabled;
                Console.WriteLine(ok ? "CORE_LIFECYCLE_PASS" : "CORE_LIFECYCLE_FAIL");
                return ok;
            }
            finally
            {
                running = null; completedProcess = null;
                try { if (!stub.HasExited) { RequestStop(stub); if (!stub.WaitForExit(5000)) stub.Kill(); } } catch { }
                stub.Dispose();
            }
        }

        private Task FlushUiEventsForTest()
        {
            var done = new TaskCompletionSource<bool>();
            BeginInvoke((Action)delegate { done.SetResult(true); });
            return done.Task;
        }

        internal async Task<bool> CheckRepeatedApplicationLifecycleForTestAsync()
        {
            if (!isSelfTest || InvokeRequired) return false;
            int uiThread = System.Threading.Thread.CurrentThread.ManagedThreadId;
            ExtractCore();
            const int seed = 20261125, cycles = 100;
            var random = new Random(seed);
            var scenarios = new List<int>();
            // Exactly 20 of each scenario, shuffled deterministically.
            for (int i = 0; i < cycles; i++) scenarios.Add(i % 5);
            for (int i = scenarios.Count - 1; i > 0; i--)
            {
                int k = random.Next(i + 1), temp = scenarios[i]; scenarios[i] = scenarios[k]; scenarios[k] = temp;
            }
            Process previous = null;
            int completed = 0;
            offlineLifecycleProbe = true;
            try
            {
                remember.Checked = false;
                account.Text = "offline-user"; password.Text = "offline-pass"; channel.SelectedIndex = 0;
                for (int cycle = 0; cycle < cycles; cycle++)
                {
                    int scenario = scenarios[cycle];
                    observeHandoff = delegate { return new HandoffObservation(ConnectionStates.ServiceState.Stopped, 0); };
                    // Same awaited command as the actual click handler, not a second test-only implementation.
                    Task command = ConnectCommandAsync();
                    if (await Task.WhenAny(command, Task.Delay(5000)) != command)
                    { Console.WriteLine("APP_COMMAND_TIMEOUT cycle=" + cycle); return false; }
                    await command; // observe the original exception, if any
                    if (InvokeRequired || uiThread != System.Threading.Thread.CurrentThread.ManagedThreadId)
                        throw new InvalidOperationException("Lifecycle continuation left UI thread.");
                    var clock = Stopwatch.StartNew();
                    while (!offlineProbeReady && clock.ElapsedMilliseconds < 5000)
                    {
                        if (running == null || Object.ReferenceEquals(running, completedProcess) || running.HasExited) break;
                        await Task.Delay(10);
                    }
                    await FlushUiEventsForTest();
                    Process child = running;
                    if (connectCheckPending || !offlineProbeReady || child == null || Object.ReferenceEquals(child, completedProcess) ||
                        Object.ReferenceEquals(child, previous) || child.HasExited ||
                        state.Auth != ConnectionStates.AuthState.Authenticating || connect.Enabled || !stop.Enabled ||
                        prepare.Enabled || restore.Enabled || trayRecover.Enabled)
                    {
                        Console.WriteLine("APP_CYCLE_START_FAIL cycle=" + cycle + " scenario=" + scenario +
                            " pending=" + connectCheckPending + " ready=" + offlineProbeReady +
                            " missing=" + (child == null) + " completed=" + Object.ReferenceEquals(child, completedProcess) +
                            " previous=" + Object.ReferenceEquals(child, previous) + " exited=" + (child != null && child.HasExited) +
                            " code=" + (child != null && child.HasExited ? child.ExitCode : -999) +
                            " auth=" + state.Auth + " core=" + state.Core + " phase=" + CurrentPermissions().Phase +
                            " connect=" + connect.Enabled + " stop=" + stop.Enabled + " prepare=" + prepare.Enabled +
                            " restore=" + restore.Enabled + " recover=" + trayRecover.Enabled);
                        return false;
                    }
                    // The real probe never authenticates. The events below simulate the UI's
                    // validated protocol messages, not a successful network connection.
                    if (previous != null)
                    {
                        OnCoreStatus(previous, "auth_failed");
                        OnCoreStatus(previous, "network_online");
                        OnCoreExit(previous);
                        await FlushUiEventsForTest();
                        if (state.Auth != ConnectionStates.AuthState.Authenticating ||
                            state.Network != ConnectionStates.NetworkState.Unknown) return false;
                    }
                    string[] events;
                    ConnectionStates.AuthState expected;
                    ConnectionStates.NetworkState expectedNetwork;
                    switch (scenario)
                    {
                        case 0:
                            events = new[] { "auth_ok", "network_online", "auth_ok" };
                            expected = ConnectionStates.AuthState.Authenticated;
                            expectedNetwork = ConnectionStates.NetworkState.InternetAvailable;
                            break;
                        case 1:
                            events = new[] { "wait_network", "online", "network_required" };
                            expected = ConnectionStates.AuthState.WaitingForNetwork;
                            expectedNetwork = ConnectionStates.NetworkState.AuthenticationRequired;
                            break;
                        case 2:
                            events = new[] { "auth_failed", "network_online", "auth_ok" };
                            expected = ConnectionStates.AuthState.Failed;
                            expectedNetwork = ConnectionStates.NetworkState.Unknown;
                            break;
                        case 3:
                            events = new[] { "core_failed", "network_online", "auth_start", "network_retry_exhausted" };
                            expected = ConnectionStates.AuthState.Failed;
                            expectedNetwork = ConnectionStates.NetworkState.Unknown;
                            break;
                        default:
                            events = new[] { "auth_ok", "network_online", "network_retry_exhausted", "network_online" };
                            expected = ConnectionStates.AuthState.Failed;
                            expectedNetwork = ConnectionStates.NetworkState.Unknown;
                            break;
                    }
                    foreach (string token in events) OnCoreStatus(child, token);
                    await FlushUiEventsForTest();
                    if (state.Auth != expected || state.Network != expectedNetwork ||
                        (scenario == 2 && state.Failure != ConnectionStates.FailureKind.Authentication) ||
                        (scenario == 3 && (state.Failure != ConnectionStates.FailureKind.Runtime || retryEligible)) ||
                        (scenario == 4 && (state.Failure != ConnectionStates.FailureKind.Network || !retryEligible)))
                    { Console.WriteLine("APP_CYCLE_EVENT_FAIL cycle=" + cycle + " scenario=" + scenario + " auth=" + state.Auth + " network=" + state.Network); return false; }
                    if (scenario == 0)
                    {
                        // A power notification invalidates already queued positive network evidence.
                        OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Suspend));
                        OnCoreStatus(child, "network_online");
                        await FlushUiEventsForTest();
                        if (!powerSuspended || state.Auth != ConnectionStates.AuthState.Authenticated ||
                            state.Network != ConnectionStates.NetworkState.Unknown || !Object.ReferenceEquals(running, child) || retryTimer.Enabled)
                            return false;
                        OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));
                        await FlushUiEventsForTest();
                        if (powerSuspended || state.Network != ConnectionStates.NetworkState.Unknown || !Object.ReferenceEquals(running, child))
                            return false;
                        OnCoreStatus(child, "network_online");
                        await FlushUiEventsForTest();
                        if (state.Network != ConnectionStates.NetworkState.InternetAvailable) return false;

                        int generationBeforeNetwork = taskGeneration;
                        // Queue a positive event first, then invalidate it with actual callback entry points.
                        OnCoreStatus(child, "network_online");
                        for (int burst = 0; burst < 16; burst++)
                        {
                            OnNetworkAddressChanged(this, EventArgs.Empty);
                            OnNetworkAvailabilityChanged(this, null);
                        }
                        await FlushUiEventsForTest();
                        if (networkChangeQueued != 0 || state.Network != ConnectionStates.NetworkState.Unknown ||
                            state.Auth != ConnectionStates.AuthState.Authenticated || !Object.ReferenceEquals(child, running) ||
                            taskGeneration != generationBeforeNetwork || retryTimer.Enabled) return false;
                        OnCoreStatus(child, "network_online");
                        await FlushUiEventsForTest();
                        if (state.Network != ConnectionStates.NetworkState.InternetAvailable) return false;
                        long stamp = lastNetworkEvidence;
                        ExpireNetworkEvidence(stamp + NetworkEvidenceTtlMs - 1);
                        if (state.Network != ConnectionStates.NetworkState.InternetAvailable) return false;
                        ExpireNetworkEvidence(stamp + NetworkEvidenceTtlMs);
                        if (state.Network != ConnectionStates.NetworkState.Unknown ||
                            state.Auth != ConnectionStates.AuthState.Authenticated || retryTimer.Enabled) return false;
                    }
                    StopClicked(stop, EventArgs.Empty);
                    OnCoreStatus(child, "auth_ok"); OnCoreStatus(child, "network_online");
                    // Exercise the real process Exited callback and reader drain, not a synthetic exit.
                    clock.Restart();
                    while (!Object.ReferenceEquals(completedProcess, child) && clock.ElapsedMilliseconds < 10000)
                        await Task.Delay(10);
                    await FlushUiEventsForTest();
                    if (!Object.ReferenceEquals(completedProcess, child) || !child.HasExited || child.ExitCode != 0 ||
                        !lastCoreReadersDrained || !offlineProbeStopAccepted ||
                        state.Auth != ConnectionStates.AuthState.Idle || state.Network != ConnectionStates.NetworkState.Unknown ||
                        activeTask != null || retryTimer.Enabled || stopping || !connect.Enabled || !prepare.Enabled ||
                        !restore.Enabled || stop.Enabled || trayStop.Enabled || !trayRecover.Enabled)
                    {
                        Console.WriteLine("APP_CYCLE_STOP_FAIL cycle=" + cycle + " scenario=" + scenario +
                            " drain=" + lastCoreReadersDrained + " ack=" + offlineProbeStopAccepted +
                            " exited=" + child.HasExited + " auth=" + state.Auth + " network=" + state.Network +
                            " phase=" + CurrentPermissions().Phase + " retry=" + retryTimer.Enabled);
                        return false;
                    }
                    previous = child;
                    completed++;
                    if (completed % 20 == 0) Console.WriteLine("APP_LIFECYCLE_PROGRESS=" + completed);
                }
                Console.WriteLine("APP_LIFECYCLE_PASS cycles=" + completed + " seed=" + seed + " ready_ack_drain=100");
                if (!await CheckPowerCancellationForTestAsync()) return false;
                Console.WriteLine("POWER_TRANSITION_PASS active_cycles=20 pending_connect=1 pending_service=1 idle=1");
                if (!await CheckNetworkObservationRaceForTestAsync()) return false;
                Console.WriteLine("NETWORK_CHANGE_PASS active_cycles=20 burst_per_cycle=32 expiry_boundaries=40 service_wait=1 stale_idle=1");
                return true;
            }
            finally
            {
                offlineLifecycleProbe = false; observeHandoff = ObserveHandoff;
                userStopped = true; taskGeneration++; retryTimer.Stop(); activeTask = null;
                if (running != null)
                {
                    Process owned = running;
                    running = null; completedProcess = null;
                    ReapOfflineOrFailedCore(owned);
                }
            }
        }

        private async Task<bool> CheckPowerCancellationForTestAsync()
        {
            if (!isSelfTest) return false;
            // Both paths remain offline: the observation keeps the real service action
            // in its waiting phase and cannot reach an elevation prompt.
            observeHandoff = delegate { return new HandoffObservation(ConnectionStates.ServiceState.Running, 1); };
            Task connectWork = ConnectCommandAsync();
            var clock = Stopwatch.StartNew();
            while (state.Handoff != ConnectionStates.HandoffState.WaitingForClient && !connectWork.IsCompleted &&
                clock.ElapsedMilliseconds < 3000) await Task.Delay(10);
            if (!connectCheckPending || state.Handoff != ConnectionStates.HandoffState.WaitingForClient) return false;
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Suspend));
            await FlushUiEventsForTest();
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));
            await FlushUiEventsForTest();
            if (await Task.WhenAny(connectWork, Task.Delay(5000)) != connectWork) return false;
            await connectWork;
            if (connectCheckPending || activeTask != null || retryTimer.Enabled || !connect.Enabled ||
                state.Auth != ConnectionStates.AuthState.Idle) return false;

            Task<bool> serviceWork = RunServiceAction(true);
            clock.Restart();
            while (state.Handoff != ConnectionStates.HandoffState.WaitingForClient && !serviceWork.IsCompleted &&
                clock.ElapsedMilliseconds < 3000) await Task.Delay(10);
            if (!serviceBusy || state.Handoff != ConnectionStates.HandoffState.WaitingForClient) return false;
            int serviceGeneration = taskGeneration;
            OnNetworkAddressChanged(this, EventArgs.Empty);
            await FlushUiEventsForTest();
            if (!serviceBusy || taskGeneration != serviceGeneration || serviceWork.IsCompleted ||
                connect.Enabled || prepare.Enabled || restore.Enabled) return false;
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Suspend));
            await FlushUiEventsForTest();
            if (!serviceBusy || connect.Enabled || prepare.Enabled || restore.Enabled) return false;
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));
            await FlushUiEventsForTest();
            if (await Task.WhenAny(serviceWork, Task.Delay(5000)) != serviceWork || await serviceWork) return false;
            if (serviceBusy || activeTask != null || !connect.Enabled || pendingServiceChild != null) return false;
            Process previous = running;
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Suspend));
            await FlushUiEventsForTest();
            if (connect.Enabled || prepare.Enabled || restore.Enabled || stop.Enabled) return false;
            OnPowerModeChanged(this, new PowerModeChangedEventArgs(PowerModes.Resume));
            await FlushUiEventsForTest();
            return !powerSuspended && Object.ReferenceEquals(previous, running) && activeTask == null &&
                !retryTimer.Enabled && connect.Enabled && !stop.Enabled;
        }

        private async Task<bool> CheckNetworkObservationRaceForTestAsync()
        {
            // Deterministically delay the idle observation across an interface change.
            if (!isSelfTest || !CurrentPermissions().CanConnect) return false;
            // Task gates have no disposable handles that could be closed while
            // the observer is still using them. Keep cleanup C# 5 compatible.
            var entered = new TaskCompletionSource<bool>();
            var release = new TaskCompletionSource<bool>();
            state.SetService(ConnectionStates.ServiceState.Stopped);
            observeHandoff = delegate {
                entered.TrySetResult(true);
                if (!release.Task.Wait(5000)) throw new System.TimeoutException();
                return new HandoffObservation(ConnectionStates.ServiceState.Running, 1);
            };
            try
            {
                Task work = RunPreConnectCheckAsync(false, true);
                bool enteredInTime = false;
                try
                {
                    enteredInTime = await Task.WhenAny(entered.Task, Task.Delay(3000)) == entered.Task;
                    if (enteredInTime)
                    {
                        OnNetworkAddressChanged(this, EventArgs.Empty);
                        await FlushUiEventsForTest();
                    }
                }
                finally
                {
                    // Signal on every path. Awaiting in finally is unsupported
                    // by the .NET Framework C# 5 compiler used by this project.
                    release.TrySetResult(true);
                }
                if (await Task.WhenAny(work, Task.Delay(5000)) != work)
                {
                    Console.WriteLine("NETWORK_OBSERVATION_TIMEOUT");
                    return false;
                }
                await work;
                if (!enteredInTime || state.Service != ConnectionStates.ServiceState.Stopped ||
                    lastIdleObservation != null || checkingBlocked)
                {
                    Console.WriteLine("NETWORK_STALE_OBSERVATION_FAIL entered=" + enteredInTime +
                        " service=" + state.Service + " cached=" + (lastIdleObservation != null) + " busy=" + checkingBlocked);
                    return false;
                }
                observeHandoff = delegate { return new HandoffObservation(ConnectionStates.ServiceState.Running, 1); };
                await RunPreConnectCheckAsync(false, true);
                bool ok = state.Service == ConnectionStates.ServiceState.Running && lastIdleObservation != null &&
                    !checkingBlocked && CurrentPermissions().CanConnect;
                if (!ok) Console.WriteLine("NETWORK_FRESH_OBSERVATION_FAIL service=" + state.Service +
                    " cached=" + (lastIdleObservation != null) + " busy=" + checkingBlocked);
                return ok;
            }
            finally
            {
                release.TrySetResult(true);
                observeHandoff = ObserveHandoff;
            }
        }

        internal bool CheckServiceRecoveryForTest(string dir)
        {
            string path = Path.Combine(dir, "service-test-" + Guid.NewGuid().ToString("N") + ".json");
            Process stub = StartOfflineStub();
            if (stub == null) return false;
            try
            {
                var journal = ServiceJournal.Load(path);
                journal.Begin("prepare", "Running");
                // Inject the same ownership transfer state as a timed-out helper.
                pendingServiceChild = stub; pendingServiceJournal = journal; serviceBusy = true;
                RefreshOperationPermissions();
                readServiceState = delegate { return "StopPending"; };
                if (!PumpTaskForTest(PollTimedOutServiceAsync(), 3000) || !serviceBusy) return false;
                Task<bool> conflicting = RunServiceAction(false);
                if (!PumpTaskForTest(conflicting, 3000) || conflicting.Result) return false;
                if (!RequestStop(stub) || !stub.WaitForExit(5000)) return false;
                if (!PumpTaskForTest(PollTimedOutServiceAsync(), 3000) || !serviceBusy || !ServiceJournal.Load(path).HasPending) return false;
                readServiceState = delegate { return "Stopped"; };
                using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (!PumpTaskForTest(PollTimedOutServiceAsync(), 3000) || !serviceBusy || !journal.HasPending) return false;
                }
                if (!PumpTaskForTest(PollTimedOutServiceAsync(), 3000)) return false;
                bool ok = !serviceBusy && pendingServiceChild == null && !ServiceJournal.Load(path).HasPending &&
                    connect.Enabled && prepare.Enabled && restore.Enabled && running == null && activeTask == null &&
                    ServiceJournal.Load(path).Result == "failed:unknown" && !ServiceJournal.Load(path).HasConfirmedOutcome;
                Console.WriteLine(ok ? "SERVICE_RECOVERY_PASS" : "SERVICE_RECOVERY_FAIL");
                return ok;
            }
            finally
            {
                if (Object.ReferenceEquals(pendingServiceChild, stub))
                {
                    pendingServiceChild = null; pendingServiceJournal = null; serviceBusy = false;
                    try { if (!stub.HasExited) { RequestStop(stub); if (!stub.WaitForExit(5000)) stub.Kill(); } } catch { }
                    stub.Dispose();
                }
                readServiceState = CurrentServiceState;
                try { File.Delete(path); File.Delete(path + ".lock"); } catch { }
            }
        }

        internal bool CheckDisposedResourcesForTest()
        {
            return IsDisposed && resourcesDisposed && !timer.Enabled && !animTimer.Enabled &&
                !retryTimer.Enabled && !evidenceTimer.Enabled && !addressEventsSubscribed && !availabilityEventsSubscribed;
        }

        internal bool CheckTrayAndSaveForTest()
        {
            SavedCredential dummy = DummyCredential(Path.GetDirectoryName(credentials.FilePath));
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
            SavedCredential dummy = DummyCredential(Path.GetDirectoryName(credentials.FilePath));
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
            activeTask = new AuthTask(account.Text, password.Text, channel.SelectedIndex + 1, false);
            StartAuthProcess("--ephemeral-crash");
            if (running == null) return false;
            for (int i = 0; i < 80 && !running.HasExited; i++) { System.Threading.Thread.Sleep(100); Application.DoEvents(); }
            if (!running.HasExited) { userStopped = true; retryTimer.Stop(); return false; }
            // 泵消息让 Exited 回调（BeginInvoke）落地并执行 OnCoreExit → ScheduleAutoRetry
            for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
            bool ok = autoRetryCount == 1 && retryTimer.Enabled && state.Auth == ConnectionStates.AuthState.RetryScheduled &&
                !connect.Enabled && !prepare.Enabled && !restore.Enabled && stop.Enabled;
            account.Text = "edited-user";
            ok &= activeTask != null && activeTask.Username == "retry-user";
            // 清理：取消重试，模拟用户停止（不变量验证：停止后重试被取消）
            StopClicked(stop, EventArgs.Empty);
            ok &= userStopped && !retryTimer.Enabled && activeTask == null && state.Auth == ConnectionStates.AuthState.Idle;
            RetryTimerTick(null, EventArgs.Empty);
            ok &= running != null && running.HasExited;
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
                if (activeTask != null) rememberRevoked = true; // explicit later save wins over old task
                credentialLoadError = null; loadedCredential = true;
                if (announce) SetStatus("已加密保存，仅当前 Windows 用户可读取。", ToneOk);
                return true;
            }
            catch (InvalidDataException)
            {
                SetStatus("保存失败：账号过长，或旧记录无法读取（可先用托盘菜单“清除已保存账号”处理）。", ToneFail);
            }
            catch (CredentialStoreBusyException) { SetStatus("保存的账号正在被另一个操作使用，请稍后重试保存。", ToneWarn); }
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
                credentials.Forget(); rememberRevoked = true; remember.Checked = false; account.Clear(); password.Clear();
                password.UseSystemPasswordChar = true; reveal.Text = "显示";
                credentialLoadError = null; loadedCredential = false;
                SetStatus("已删除保存的账号密码。", ToneOk);
            }
            catch (CredentialStoreBusyException) { SetStatus("保存的账号正在被另一个操作使用，请稍后重试删除。", ToneWarn); }
            catch { SetStatus("删除失败：无法安全访问保存的账号。", ToneFail); }
        }

        private static void AddField(TableLayoutPanel layout, string text, Control field, int row)
        {
            var rowTable = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, BackColor = CardBgColor };
            rowTable.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            rowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 88));
            rowTable.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            var label = new Label { Text = text, Dock = DockStyle.Fill, Margin = Padding.Empty, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Color.FromArgb(71, 85, 105), Font = new Font("Microsoft YaHei UI", 9.5F), BackColor = CardBgColor };
            field.Dock = DockStyle.Fill;
            rowTable.Controls.Add(label, 0, 0);
            rowTable.Controls.Add(field, 1, 0);
            layout.Controls.Add(rowTable, 0, row);
        }

        private enum ButtonKind { Primary, Normal, Danger, DangerFilled }

        private static GraphicsPath CreateRoundedPath(Rectangle rect, int radius)
        {
            var path = new GraphicsPath();
            if (rect.Width <= 0 || rect.Height <= 0) return path;
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

        private sealed class AdaptiveDeviceCombo : ComboBox
        {
            private const int CB_GETITEMHEIGHT = 0x0154;

            [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
            private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

            internal AdaptiveDeviceCombo()
            {
                FormattingEnabled = true;
                DropDownStyle = ComboBoxStyle.DropDownList;
                FlatStyle = FlatStyle.Standard;
                DrawMode = DrawMode.OwnerDrawFixed;
                AccessibleName = "设备类型";
                MaxDropDownItems = 5;
            }

            internal void ApplyItemMetrics(Graphics graphics)
            {
                int textHeight = MeasureLine(graphics, "Windows", Font).Height;
                foreach (object item in Items)
                    textHeight = Math.Max(textHeight, MeasureLine(graphics, GetItemText(item), Font).Height);
                int padding = Math.Max(4, (int)Math.Ceiling(6 * graphics.DpiY / 96F));
                int needed = textHeight + padding;
                if (ItemHeight != needed) ItemHeight = needed;
                int dropHeight = needed * Math.Max(1, Math.Min(Items.Count, MaxDropDownItems)) + 4;
                if (DropDownHeight != dropHeight) DropDownHeight = dropHeight;
            }

            internal int ReadNativeItemHeight(int index)
            {
                if (!IsHandleCreated) return -1;
                return SendMessage(Handle, CB_GETITEMHEIGHT, new IntPtr(index), IntPtr.Zero).ToInt32();
            }

            protected override void OnDrawItem(DrawItemEventArgs e)
            {
                e.DrawBackground();
                if (e.Index >= 0 && e.Index < Items.Count)
                {
                    Rectangle area = e.Bounds;
                    int inset = Math.Max(2, (int)Math.Ceiling(4 * e.Graphics.DpiX / 96F));
                    area.Inflate(-inset, 0);
                    Color color = Enabled ? e.ForeColor : SystemColors.GrayText;
                    TextRenderer.DrawText(e.Graphics, GetItemText(Items[e.Index]), Font, area, color,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
                }
                e.DrawFocusRectangle();
                base.OnDrawItem(e);
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
                if (child is TextBox || child is ComboBox) { child.Dock = DockStyle.None; child.Anchor = AnchorStyles.Left | AnchorStyles.Right; }
                AttachEvents(child);
            }

            protected override void OnLayout(LayoutEventArgs e)
            {
                base.OnLayout(e);
                if (Controls.Count != 1) return;
                Control child = Controls[0];
                int height = child is TextBox ? ((TextBox)child).PreferredHeight :
                    child is ComboBox ? ((ComboBox)child).PreferredHeight : 0;
                if (height <= 0) return;
                Rectangle area = DisplayRectangle;
                Rectangle bounds = new Rectangle(area.X, area.Y + Math.Max(0, (area.Height - height) / 2),
                    Math.Max(1, area.Width), height);
                if (child.Bounds != bounds) child.Bounds = bounds;
                // Native single-line inputs can impose their own actual height.
                // Center the result after Windows applies that constraint.
                int top = area.Y + Math.Max(0, (area.Height - child.Height) / 2);
                if (child.Top != top) child.Top = top;
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

                int dotSize = Math.Max(4, (int)Math.Round(6 * e.Graphics.DpiY / 96F));
                int dotX = Math.Max(8, (int)Math.Round(12 * e.Graphics.DpiX / 96F));
                int dotY = (Height - dotSize) / 2;
                using (var dotBrush = new SolidBrush(color))
                {
                    e.Graphics.FillEllipse(dotBrush, dotX, dotY, dotSize, dotSize);
                }

                int gap = Math.Max(4, (int)Math.Round(6 * e.Graphics.DpiX / 96F));
                var textRect = new Rectangle(dotX + dotSize + gap, 0, Math.Max(1, Width - dotX - dotSize - gap * 2), Height);
                TextRenderer.DrawText(e.Graphics, text, Font, textRect, color, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
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

                int badgeSize = Math.Min(Math.Min(Width, Height) - 2, (int)Math.Round(42 * e.Graphics.DpiY / 96F));
                if (badgeSize < 12) return;
                float scale = badgeSize / 42F;
                int badgeY = (Height - badgeSize) / 2;
                var badgeRect = new Rectangle(0, badgeY, badgeSize, badgeSize);
                using (var path = CreateRoundedPath(badgeRect, Math.Max(1, (int)(10 * scale))))
                using (var brush = new LinearGradientBrush(badgeRect, Color.FromArgb(30, 96, 213), Color.FromArgb(20, 70, 180), 45f))
                    e.Graphics.FillPath(brush, path);
                using (var pen = new Pen(Color.White, 2.2f * scale))
                {
                    pen.StartCap = LineCap.Round; pen.EndCap = LineCap.Round;
                    float cx = badgeRect.X + badgeSize / 2F;
                    float cy = badgeRect.Y + badgeSize / 2F + 7 * scale;
                    using (var dotBrush = new SolidBrush(Color.White)) e.Graphics.FillEllipse(dotBrush, cx - 2 * scale, cy - 2 * scale, 4 * scale, 4 * scale);
                    e.Graphics.DrawArc(pen, cx - 7 * scale, cy - 7 * scale, 14 * scale, 14 * scale, 215, 110);
                    e.Graphics.DrawArc(pen, cx - 13 * scale, cy - 13 * scale, 26 * scale, 26 * scale, 215, 110);
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
            else if (a == ConnectionStates.AuthState.Authenticated || a == ConnectionStates.AuthState.AuthenticatedBySession)
                pillColor = state.Network == ConnectionStates.NetworkState.InternetAvailable ? ToneOk : ToneWarn;
            else if (a == ConnectionStates.AuthState.Failed) pillColor = ToneFail;
            else if (state.Handoff == ConnectionStates.HandoffState.Blocked || state.Handoff == ConnectionStates.HandoffState.PreparationRequired || state.Handoff == ConnectionStates.HandoffState.LaunchUnconfirmed) pillColor = ToneWarn;
            statusPill.UpdateStatus(state.PillText, pillColor);
        }

        private void SetHandoff(ConnectionStates.HandoffState phase, string message, Color tone, bool pulse = false)
        {
            state.SetHandoff(phase); SetStatus(message, tone, pulse); UpdateConnectionPill();
        }
        private bool HandoffCancelled(int generation, bool connectionTask)
        {
            return IsDisposed || Disposing || exitRequested || powerSuspended || generation != taskGeneration ||
                (connectionTask && (userStopped || activeTask == null));
        }
        private async Task<HandoffObservation> WaitForHandoffAsync(ConnectionStates.HandoffIntent intent, int generation, bool connectionTask)
        {
            var clock = Stopwatch.StartNew();
            while (!HandoffCancelled(generation, connectionTask))
            {
                var observation = await Task.Run(observeHandoff);
                if (HandoffCancelled(generation, connectionTask)) return null;
                state.SetService(observation.Service);
                var decision = observation.Decide(intent);
                if (decision != ConnectionStates.HandoffDecision.WaitForClient &&
                    decision != ConnectionStates.HandoffDecision.WaitForService) return observation;
                if (clock.Elapsed.TotalSeconds >= 25) return observation;
                int remaining = Math.Max(1, 25 - (int)clock.Elapsed.TotalSeconds);
                SetHandoff(decision == ConnectionStates.HandoffDecision.WaitForClient ?
                    ConnectionStates.HandoffState.WaitingForClient : ConnectionStates.HandoffState.WaitingForService,
                    (decision == ConnectionStates.HandoffDecision.WaitForClient ?
                        "等待天翼或旧认证进程正常退出（" + observation.Clients + " 个），" : "等待后台服务完成切换，") +
                    "剩余约 " + remaining + " 秒。", ToneBusy, true);
                await Task.Delay(1000);
            }
            return null;
        }

        // 连接状态迁移唯一入口：经迁移表校验，非法迁移拒绝并记录（供诊断/测试）
        private void SetAuthState(ConnectionStates.AuthState next, string text, Color tone, bool pulse = false)
        {
            if (!state.TrySetAuth(next, text)) return;
            SetStatus(text, tone, pulse);
            UpdateConnectionPill();
        }

        private ConnectionStates.OperationPermissions CurrentPermissions()
        {
            bool owned = running != null && !Object.ReferenceEquals(running, completedProcess);
            bool force = stopping && grace != null && grace.Elapsed.TotalSeconds >= 100;
            return ConnectionStates.DecideOperations(serviceBusy, connectCheckPending, retryTimer.Enabled,
                owned, stopping, force, exitRequested || powerSuspended, userStopped);
        }

        private void RefreshOperationPermissions()
        {
            if (IsDisposed || Disposing || resourcesDisposed) return;
            var rights = CurrentPermissions();
            connect.Enabled = rights.CanConnect;
            prepare.Enabled = restore.Enabled = rights.CanSwitch;
            stop.Enabled = rights.Stop != ConnectionStates.StopIntent.None;
            switch (rights.Stop)
            {
                case ConnectionStates.StopIntent.CancelConnection: stop.Text = "取消连接"; break;
                case ConnectionStates.StopIntent.CancelRetry: stop.Text = "取消重试"; break;
                case ConnectionStates.StopIntent.ForceCore: stop.Text = "强制结束"; break;
                default: stop.Text = rights.Phase == ConnectionStates.OperationPhase.Stopping ? "正在停止…" : "停止认证"; break;
            }
            StyleStopArmed(rights.Stop == ConnectionStates.StopIntent.ForceCore);
            if (trayStop != null) { trayStop.Enabled = stop.Enabled; trayStop.Text = stop.Text; }
            if (trayRecover != null) trayRecover.Enabled = rights.CanRecover;
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
        private readonly System.Threading.CancellationTokenSource idleCheckLifetime =
            new System.Threading.CancellationTokenSource();
        private Task<HandoffObservation> idleCheckWorker;
        private HandoffObservation lastIdleObservation;
        private Process pendingServiceChild;
        private ServiceJournal pendingServiceJournal;
        private bool pollingService;
        private Func<string> readServiceState = CurrentServiceState;
        private async void RunStartupChecksAsync()
        {
            int generation = taskGeneration;
            await RunPreConnectCheckAsync(false);
            if (!HandoffCancelled(generation, false) && !connectCheckPending && !serviceBusy && activeTask == null)
                await PromptPendingServiceRecovery();
        }
        private async Task RunPreConnectCheckAsync(bool showMessage, bool quiet = false)
        {
            if (resourcesDisposed || checkingBlocked || powerSuspended || serviceBusy || connectCheckPending ||
                retryTimer.Enabled || activeTask != null || (idleCheckWorker != null && !idleCheckWorker.IsCompleted)) return;
            var cancellation = idleCheckLifetime.Token;
            int generation = taskGeneration;
            int observationEpoch = System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0);
            checkingBlocked = true;
            try
            {
                var observer = observeHandoff;
                var worker = Task.Run(() => SampleIdleEnvironment(observer), cancellation);
                idleCheckWorker = worker;
                using (var deadline = System.Threading.CancellationTokenSource.CreateLinkedTokenSource(cancellation))
                {
                    var timeout = Task.Delay(5000, deadline.Token);
                    if (await Task.WhenAny(worker, timeout) != worker)
                    {
                        if (!cancellation.IsCancellationRequested &&
                            observationEpoch == System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0) &&
                            !HandoffCancelled(generation, false) && !serviceBusy && !connectCheckPending && activeTask == null)
                            SetStatus("环境状态检查超时，请稍后重试。", ToneWarn);
                        return;
                    }
                    deadline.Cancel();
                }
                var observation = await worker;
                if (cancellation.IsCancellationRequested ||
                    observationEpoch != System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0) ||
                    HandoffCancelled(generation, false) || serviceBusy || connectCheckPending ||
                    (running != null && !Object.ReferenceEquals(running, completedProcess))) return;
                if (observation == null)
                {
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "环境状态检查失败，请稍后重试。", ToneWarn);
                    return;
                }
                state.SetService(observation.Service);
                bool changed = lastIdleObservation == null || lastIdleObservation.Service != observation.Service ||
                    lastIdleObservation.Clients != observation.Clients || lastIdleObservation.VisibleClient != observation.VisibleClient;
                lastIdleObservation = observation;
                if (quiet && (!changed || state.Auth != ConnectionStates.AuthState.Idle)) return;
                if (!JournalAllowsNewAction()) return;
                var decision = observation.Decide(ConnectionStates.HandoffIntent.Connect);
                if (observation.VisibleClient && observation.Service == ConnectionStates.ServiceState.Running)
                {
                    SetHandoff(ConnectionStates.HandoffState.OfficialRunning, "检测到官方天翼窗口；当前认证状态未确认。", ToneInfo);
                    return;
                }
                if (decision != ConnectionStates.HandoffDecision.Ready)
                {
                    string blocked = HandoffMessage(decision);
                    SetHandoff(decision == ConnectionStates.HandoffDecision.NeedStopService ?
                        ConnectionStates.HandoffState.PreparationRequired : ConnectionStates.HandoffState.Blocked, blocked, ToneWarn);
                    if (showMessage) MessageBox.Show(this, blocked, "暂不能连接", MessageBoxButtons.OK, MessageBoxIcon.Information);
                }
                else if (!showMessage)
                {
                    string ready = loadedCredential ? "已载入保存的账号，点击“立即连接”即可。" :
                        "输入校园网账号和密码，点击“立即连接”即可。";
                    SetHandoff(ConnectionStates.HandoffState.Ready, credentialLoadError ?? ready,
                        credentialLoadError == null ? ToneInfo : ToneWarn);
                }
            }
            catch
            {
                if (!cancellation.IsCancellationRequested &&
                    observationEpoch == System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0) &&
                    !HandoffCancelled(generation, false) && !serviceBusy && !connectCheckPending && activeTask == null)
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "环境状态检查失败，请稍后重试。", ToneWarn);
            }
            finally { checkingBlocked = false; }
        }

        private static HandoffObservation SampleIdleEnvironment(Func<HandoffObservation> observer)
        {
            // The UI may stop waiting while the read-only query is still
            // running. Observe its exception here without posting to the UI.
            try { return observer(); }
            catch { return null; }
        }

        private async void ConnectClicked(object sender, EventArgs e)
        {
            await ConnectCommandAsync();
        }

        private async Task ConnectCommandAsync()
        {
            if (!CurrentPermissions().CanConnect) return;
#if CHEZHI_LICENSED
            if (licenseGate.Busy) { SetStatus("授权操作正在进行，请稍后。", ToneWarn); return; }
#endif
            string invalid = ValidateAuthInput(account.Text, password.Text, channel.SelectedIndex + 1);
            if (invalid != null) { SetStatus(invalid, ToneWarn); return; }
            retryTimer.Stop();
            taskGeneration++;
            activeTask = new AuthTask(account.Text, password.Text, channel.SelectedIndex + 1, remember.Checked);
            userStopped = false; rememberRevoked = false; autoRetryCount = 0;
            await CheckAndStartAsync(taskGeneration);
        }

        private async Task RecoverInterruptedJournalAsync()
        {
            if (isSelfTest || !CurrentPermissions().CanRecover || activeTask != null) return;
            int generation = ++taskGeneration;
            serviceBusy = true; RefreshOperationPermissions();
            try
            {
                var journal = ServiceJournal.Load(ServiceJournal.DefaultPath());
                if (journal.Invalid || !journal.HasPending)
                {
                    SetStatus(journal.Invalid ? "事务记录损坏，不能自动修复；请导出诊断。" : "没有未完成的服务事务。", ToneInfo);
                    return;
                }
                BootEvidence boot = await Task.Run((Func<BootEvidence>)ReadBootEvidence);
                string actual = await Task.Run(readServiceState);
                if (HandoffCancelled(generation, false)) return;
                if (boot == null || !journal.CanResolveAfterRestart(boot.Ticks, boot.Sequence, actual))
                {
                    SetHandoff(ConnectionStates.HandoffState.Blocked, journal.BootTicks == 0 || journal.BootSequence == 0 ?
                        "旧事务缺少启动代次证据，请导出诊断并人工核对；不能自动解除。" :
                        "尚未确认完整重启或服务仍未稳定。请先保存工作，通过 Windows 的重启功能重启后再核对。", ToneWarn);
                    return;
                }
                if (MessageBox.Show(this, "已检测到新的 Windows 启动代次，当前天翼服务状态为 " + actual +
                    "。是否将上次事务记录为结果未知的中断？不会启动/停止服务或自动连接；若服务原本运行，将保留恢复责任。",
                    "核对遗留事务", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
                BootEvidence latestBoot = await Task.Run((Func<BootEvidence>)ReadBootEvidence);
                string latestState = await Task.Run(readServiceState);
                if (HandoffCancelled(generation, false)) return;
                if (latestBoot == null || latestBoot.Ticks != boot.Ticks || latestBoot.Sequence != boot.Sequence || latestState != actual)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, "核对期间状态变化，未修改记录。", ToneWarn); return; }
                journal.ResolveAfterRestart(latestBoot.Ticks, latestBoot.Sequence, latestState);
                state.SetService(MapServiceState(latestState));
                SetHandoff(ConnectionStates.HandoffState.None, "遗留事务已记录为中断；请重新选择连接或恢复，未自动执行任何网络操作。", ToneInfo);
            }
            catch { if (!IsDisposed) SetHandoff(ConnectionStates.HandoffState.Blocked, "遗留事务核对失败，记录保持原状，请导出诊断。", ToneWarn); }
            finally
            {
                serviceBusy = false;
                RefreshOperationPermissions();
            }
        }

        private bool JournalAllowsNewAction()
        {
            if (isSelfTest) return true;
            var journal = ServiceJournal.Load(ServiceJournal.DefaultPath());
            if (!journal.Invalid && !journal.HasPending) return true;
            SetHandoff(ConnectionStates.HandoffState.Blocked,
                journal.Invalid ? "服务事务记录损坏，请先核对诊断。" :
                "存在上次尚未确认的服务操作；请使用托盘菜单“核对遗留服务事务”。", ToneWarn);
            return false;
        }

        private async Task CheckAndStartAsync(int generation)
        {
            if (activeTask == null || userStopped || serviceBusy || exitRequested || powerSuspended || connectCheckPending) return;
            if (!JournalAllowsNewAction())
            {
                activeTask = null; retryTimer.Stop();
                state.TrySetAuth(ConnectionStates.AuthState.Idle, "unresolved journal");
                UpdateConnectionPill();
                RefreshOperationPermissions();
                return;
            }
            bool automaticRetry = autoRetryCount > 0;
            connectCheckPending = true;
            RefreshOperationPermissions();
            state.TrySetAuth(ConnectionStates.AuthState.Idle, "new preflight");
            SetHandoff(ConnectionStates.HandoffState.Checking, "正在检查天翼服务和残留进程……", ToneInfo, true);
            bool ready = false;
            try
            {
#if CHEZHI_LICENSED
                if (!await licenseGate.VerifyAsync(this, !automaticRetry) || HandoffCancelled(generation, true))
                {
                    if (!HandoffCancelled(generation, true)) SetHandoff(ConnectionStates.HandoffState.Blocked, licenseGate.Message, ToneWarn);
                    return;
                }
#endif
                var observation = await WaitForHandoffAsync(ConnectionStates.HandoffIntent.Connect, generation, true);
                if (observation == null || HandoffCancelled(generation, true)) return;
                var decision = observation.Decide(ConnectionStates.HandoffIntent.Connect);
                if (decision == ConnectionStates.HandoffDecision.NeedStopService && !automaticRetry)
                {
                    SetHandoff(ConnectionStates.HandoffState.PreparationRequired, HandoffMessage(decision), ToneWarn);
                    if (!await RunServiceAction(true, true) || HandoffCancelled(generation, true)) return;
                    observation = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
                    if (HandoffCancelled(generation, true)) return;
                    state.SetService(observation.Service);
                    decision = observation.Decide(ConnectionStates.HandoffIntent.Connect);
                }
                if (decision != ConnectionStates.HandoffDecision.Ready)
                {
                    SetHandoff(decision == ConnectionStates.HandoffDecision.NeedStopService ?
                        ConnectionStates.HandoffState.PreparationRequired : ConnectionStates.HandoffState.Blocked, HandoffMessage(decision), ToneWarn);
                    return;
                }
                if (HandoffCancelled(generation, true)) return;
#if CHEZHI_LICENSED
                // Handoff may have interrupted connectivity. Revalidate
                // before launching; never treat a prior grant as offline permission.
                if (!await licenseGate.VerifyAsync(this, false) || HandoffCancelled(generation, true))
                {
                    if (!HandoffCancelled(generation, true)) SetHandoff(ConnectionStates.HandoffState.Blocked, licenseGate.Message + " 可使用恢复天翼功能。", ToneWarn);
                    return;
                }
#endif
#if CHEZHI_LICENSED
                // Authorization awaited network I/O after the previous
                // observation. Re-observe local handoff before launch.
                observation = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
                if (HandoffCancelled(generation, true)) return;
                if (observation.Decide(ConnectionStates.HandoffIntent.Connect) != ConnectionStates.HandoffDecision.Ready || !licenseGate.Allowed)
                {
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "授权或天翼环境已变化，请重新检查；可恢复天翼。", ToneWarn);
                    return;
                }
#endif
                // Keep preflight ownership until the synchronous launch has returned.
                state.SetHandoff(ConnectionStates.HandoffState.None);
                StartAuthProcess(isSelfTest && offlineLifecycleProbe ? "--ephemeral-probe" : "--ephemeral-auth");
                ready = true;
            }
            catch
            {
                if (isSelfTest) throw;
                if (!HandoffCancelled(generation, true)) SetHandoff(ConnectionStates.HandoffState.Blocked, "无法检查天翼环境，请稍后重试。", ToneWarn);
            }
            finally
            {
                connectCheckPending = false;
                if (!IsDisposed && !Disposing)
                {
                    if (!ready || HandoffCancelled(generation, true)) activeTask = null;
                    RefreshOperationPermissions();
                }
            }
        }

        // The actual process always uses the immutable, user-authorized task snapshot.
        private void StartAuthProcess(string mode = "--ephemeral-auth")
        {
            if (activeTask == null || userStopped || serviceBusy || exitRequested || powerSuspended) return;
            if (isSelfTest && mode == "--ephemeral-auth") throw new InvalidOperationException("Offline test cannot authenticate.");
#if CHEZHI_LICENSED
            if (mode == "--ephemeral-auth" && !licenseGate.Allowed) throw new InvalidOperationException("Online authorization required.");
#endif
            if (running != null && !Object.ReferenceEquals(running, completedProcess)) return;
            try
            {
                if (running != null) { DisposeCoreProcess(running); running = null; }
                completedProcess = null;
                stopping = false; closingAfterStop = false; savedAfterAuth = false;
                runSawProgress = false; authFailureSeen = false; runtimeFailureSeen = false; retryEligible = false;
                offlineProbeReady = false; offlineProbeStopAccepted = false; lastCoreReadersDrained = false;
                stop.Text = "停止认证"; StyleStopArmed(false); timer.Stop();
                running = LaunchCore(mode, activeTask.Username, activeTask.Password, activeTask.Channel, OnCoreStatus);
                password.UseSystemPasswordChar = true; reveal.Text = "显示";
                connect.Text = "连接中…"; RefreshOperationPermissions();
                lastNetworkEvidence = 0;
                state.SetCore(ConnectionStates.CoreState.Running);
                state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                SetAuthState(ConnectionStates.AuthState.Authenticating, "正在连接校园网……", ToneBusy, true);
                Process started = running;
                int completionQueued = 0;
                Action complete = delegate {
                    if (System.Threading.Interlocked.Exchange(ref completionQueued, 1) != 0) return;
                    Task.Run(delegate {
                        // WaitForExit() drains asynchronous output before queuing the terminal event.
                        bool drained = false;
                        try { started.WaitForExit(); drained = DrainCoreReaders(started, 5000); } catch { }
                        if (!IsDisposed && IsHandleCreated) try {
                            BeginInvoke((Action)delegate { if (!Object.ReferenceEquals(running, started) || Object.ReferenceEquals(completedProcess, started)) return; lastCoreReadersDrained = drained; if (!drained) retryEligible = false; OnCoreExit(started); });
                        } catch (InvalidOperationException) { }
                    });
                };
                started.Exited += delegate { complete(); };
                if (started.HasExited) complete();
            }
            catch (Exception ex)
            {
                activeTask = null; state.SetCore(ConnectionStates.CoreState.Exited);
                state.Failure = ConnectionStates.FailureKind.Runtime;
                SetAuthState(ConnectionStates.AuthState.Failed, ex is ArgumentException ? ex.Message : "启动认证失败，请重试。", ToneFail);
                SetStatus(ex is ArgumentException ? ex.Message : "启动认证失败，请重试。", ToneFail);
                connect.Text = "立即连接"; RefreshOperationPermissions();
            }
        }

        // Only an explicit network probe exhaustion event permits automatic recovery.
        // Unknown failures, rejected credentials and arbitrary crashes require manual action.
        private void ScheduleAutoRetry(int exitCode)
        {
            if (userStopped || exitRequested || powerSuspended || stopping || activeTask == null ||
                !retryEligible || authFailureSeen || autoRetryCount >= 2) { activeTask = null; return; }
            autoRetryCount++;
            int delay = autoRetryCount == 1 ? 10000 : 40000;
            retryTimer.Stop(); retryTimer.Interval = delay; retryTimer.Start();
            RefreshOperationPermissions();
            SetAuthState(ConnectionStates.AuthState.RetryScheduled, "网络探测失败（退出码 " + exitCode + "），" + (delay / 1000) + " 秒后重试（第 " + autoRetryCount + "/2 次）。", ToneWarn, true);
        }

        private async void RetryTimerTick(object sender, EventArgs e)
        {
            if (!retryTimer.Enabled) return;
            retryTimer.Stop();
            if (userStopped || exitRequested || powerSuspended || serviceBusy || stopping || activeTask == null ||
                state.Auth != ConnectionStates.AuthState.RetryScheduled || isSelfTest) { RefreshOperationPermissions(); return; }
            await CheckAndStartAsync(taskGeneration);
        }

        private static bool NetworkEvidenceIsFresh(long received, long now)
        {
            return received >= 0 && now >= received && now - received < NetworkEvidenceTtlMs;
        }

        private void ExpireNetworkEvidence(long now)
        {
            if (IsDisposed || Disposing || state.Network != ConnectionStates.NetworkState.InternetAvailable ||
                NetworkEvidenceIsFresh(lastNetworkEvidence, now)) return;
            InvalidateNetworkEvidence("网络探测证据已过期，当前可达性待确认。");
        }

        private void InvalidateNetworkEvidence(string message)
        {
            bool hadPositiveEvidence = state.Network == ConnectionStates.NetworkState.InternetAvailable;
            state.SetNetwork(ConnectionStates.NetworkState.Unknown);
            UpdateConnectionPill();
            if (hadPositiveEvidence && !serviceBusy && !connectCheckPending && !stopping &&
                !userStopped && !authFailureSeen && !runtimeFailureSeen && !retryEligible &&
                (state.Auth == ConnectionStates.AuthState.Authenticated || state.Auth == ConnectionStates.AuthState.AuthenticatedBySession))
                SetStatus(message, ToneWarn);
        }

        private void SubscribeNetworkChanges()
        {
            // Availability means an interface is available, not internet connectivity.
            // Either notification is only an invalidation hint.
            try
            {
                if (!addressEventsSubscribed)
                { NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged; addressEventsSubscribed = true; }
            }
            catch { } // evidence expiry remains the fallback if Windows subscription fails
            try
            {
                if (!availabilityEventsSubscribed)
                { NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged; availabilityEventsSubscribed = true; }
            }
            catch { }
        }

        private void UnsubscribeNetworkChanges()
        {
            if (addressEventsSubscribed)
            {
                try { NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged; } catch { }
                addressEventsSubscribed = false;
            }
            if (availabilityEventsSubscribed)
            {
                try { NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged; } catch { }
                availabilityEventsSubscribed = false;
            }
        }

        private void OnNetworkAddressChanged(object sender, EventArgs e) { QueueNetworkChange(); }
        private void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e) { QueueNetworkChange(); }

        private void QueueNetworkChange()
        {
            if (IsDisposed || Disposing || !IsHandleCreated) return;
            System.Threading.Interlocked.Increment(ref networkEvidenceEpoch);
            // Coalesce a burst of interface notifications into one pending UI callback.
            if (System.Threading.Interlocked.Exchange(ref networkChangeQueued, 1) != 0) return;
            try
            {
                BeginInvoke((Action)delegate {
                    System.Threading.Interlocked.Exchange(ref networkChangeQueued, 0);
                    if (IsDisposed || Disposing || resourcesDisposed) return;
                    lastIdleObservation = null;
#if CHEZHI_LICENSED
                    licenseGate.Invalidate();
#endif
                    InvalidateNetworkEvidence("检测到网络接口变化，当前可达性待重新确认。");
                    // Do not start/restart a core, change a service or revoke an authorized handoff.
                    // Existing retry rules and idle observation timer remain responsible.
                });
            }
            catch (InvalidOperationException) { System.Threading.Interlocked.Exchange(ref networkChangeQueued, 0); }
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if ((e.Mode != PowerModes.Suspend && e.Mode != PowerModes.Resume) || IsDisposed || Disposing) return;
            // Invalidate positive events already queued before the notification arrived.
            System.Threading.Interlocked.Increment(ref networkEvidenceEpoch);
            bool suspended = e.Mode == PowerModes.Suspend;
            try
            {
                BeginInvoke((Action)(async delegate {
                    try { await ApplyPowerTransitionAsync(suspended); }
                    catch (Exception error)
                    {
                        // This posted UI callback owns faults escaping the
                        // transition, including faults during form teardown.
                        System.Diagnostics.Trace.TraceError("Power transition failed: {0}", error.GetType().Name);
                    }
                }));
            }
            catch (InvalidOperationException) { }
        }

        private async Task ApplyPowerTransitionAsync(bool suspended)
        {
            if (IsDisposed || Disposing || resourcesDisposed) return;
            bool checkIdleEnvironment = false;
            powerSuspended = suspended;
#if CHEZHI_LICENSED
            licenseGate.Invalidate();
#endif
            taskGeneration++; // revoke stale environment checks and post-service continuation
            lastIdleObservation = null;
            state.SetNetwork(ConnectionStates.NetworkState.Unknown);
            UpdateConnectionPill();
            password.UseSystemPasswordChar = true; reveal.Text = "显示";
            if (serviceBusy)
            {
                // Already-started helpers remain owned and supervised; never clear their journal.
                // A generation change prevents automatically launching a client afterwards.
                if (connectCheckPending) { userStopped = true; activeTask = null; }
                RefreshOperationPermissions();
                return;
            }
            if (connectCheckPending || retryTimer.Enabled)
            {
                userStopped = true; activeTask = null; retryTimer.Stop();
                state.SetHandoff(ConnectionStates.HandoffState.None);
                SetAuthState(ConnectionStates.AuthState.Idle, "系统电源状态变化，已取消待执行连接或重试，请重新连接。", ToneWarn);
            }
            else if (running != null && !Object.ReferenceEquals(running, completedProcess) && !stopping &&
                !userStopped && !authFailureSeen && !runtimeFailureSeen && !retryEligible)
            {
                // Existing core may continue its own recovery; no new authentication is launched here.
                SetStatus(suspended ? "系统即将休眠，网络可达性待确认。" : "系统已唤醒，网络可达性待确认。", ToneWarn);
            }
            else if (!suspended && state.Auth == ConnectionStates.AuthState.Idle &&
                (running == null || Object.ReferenceEquals(running, completedProcess)))
            {
                SetHandoff(ConnectionStates.HandoffState.None, "系统已唤醒，请按需重新连接。", ToneInfo);
                checkIdleEnvironment = !isSelfTest;
            }
            RefreshOperationPermissions();
            if (checkIdleEnvironment) await RunPreConnectCheckAsync(false);
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
                sb.AppendLine("认证状态: " + state.Auth + "  核心进程: " + state.Core + "  网络: " + state.Network + "  服务: " + state.Service + "  切换: " + state.Handoff);
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
            if (activeTask == null || !activeTask.Remember || rememberRevoked || !remember.Checked || credentials == null) return;
            if (!String.Equals(activeTask.Username, account.Text, StringComparison.Ordinal) ||
                !String.Equals(activeTask.Password, password.Text, StringComparison.Ordinal) ||
                activeTask.Channel != channel.SelectedIndex + 1) return;
            try
            {
                credentials.Save(new SavedCredential { Username = activeTask.Username, Password = activeTask.Password, Channel = activeTask.Channel });
                credentialLoadError = null; loadedCredential = true;
            }
            catch (CredentialStoreBusyException)
            {
                MessageBox.Show(this, "连接成功。另一个操作正在使用保存的账号，请稍后手动保存。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch
            {
                MessageBox.Show(this, "连接成功，但账号密码未能加密保存（可能是本地记录损坏或目录不可写）。\r\n本次连接不受影响；如需保存请先用托盘菜单“清除已保存账号”。", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
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
            int observedEpoch = System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0);
            long receivedAt = evidenceClock.ElapsedMilliseconds;
            try { BeginInvoke((Action)delegate {
                if (!Object.ReferenceEquals(running, sender) || Object.ReferenceEquals(completedProcess, sender)) return;
                // ParseCoreEvent already validated the PID, schema, event and code; token is allowlisted.
                ArchiveCoreEvent(token);
                if (token == "probe_ready")
                {
                    if (isSelfTest && offlineLifecycleProbe) offlineProbeReady = true;
                    return;
                }
                if (isSelfTest && offlineLifecycleProbe && stopping && token == "stop_accepted") offlineProbeStopAccepted = true;
                bool stopEvent = token == "stop_accepted" || token == "stop_channel_failed";
                if (stopEvent && !stopping) return;
                if ((stopping || userStopped || authFailureSeen || runtimeFailureSeen || retryEligible) && !stopEvent) return;
                if ((token == "network_online" || token == "online") &&
                    (powerSuspended || observedEpoch != System.Threading.Interlocked.CompareExchange(ref networkEvidenceEpoch, 0, 0) ||
                        !NetworkEvidenceIsFresh(receivedAt, evidenceClock.ElapsedMilliseconds))) return;
                if (token == "core_failed")
                {
                    runtimeFailureSeen = true;
                    state.Failure = ConnectionStates.FailureKind.Runtime;
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.Failed, "认证核心运行中断，正在等待进程退出。", ToneFail);
                    return;
                }
                if (token == "network_retry_exhausted" || (token == "test_crash" && isSelfTest))
                {
                    retryEligible = true;
                    state.Failure = ConnectionStates.FailureKind.Network;
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.Failed, "网络探测已耗尽，等待核心退出。", ToneWarn);
                    return;
                }
                if (token == "network_online")
                {
                    lastNetworkEvidence = receivedAt;
                    state.SetNetwork(ConnectionStates.NetworkState.InternetAvailable);
                    UpdateConnectionPill();
                    if (state.Auth == ConnectionStates.AuthState.Authenticated)
                        SetStatus("认证成功，互联网可达。", ToneOk);
                    else if (state.Auth == ConnectionStates.AuthState.WaitingForNetwork)
                        SetAuthState(ConnectionStates.AuthState.AuthenticatedBySession, "网络已连通；尚未验证本次输入的凭据。", ToneOk);
                    return;
                }
                if (token == "network_required" || token == "network_unknown")
                {
                    state.SetNetwork(token == "network_required" ? ConnectionStates.NetworkState.AuthenticationRequired : ConnectionStates.NetworkState.Unknown);
                    if (state.Auth == ConnectionStates.AuthState.Authenticated || state.Auth == ConnectionStates.AuthState.AuthenticatedBySession)
                        SetAuthState(ConnectionStates.AuthState.WaitingForNetwork, "网络状态变化，等待重新确认。", ToneWarn, true);
                    UpdateConnectionPill();
                    return;
                }
                if (token == "auth_start")
                {
                    var next = state.Auth == ConnectionStates.AuthState.Authenticated || state.Auth == ConnectionStates.AuthState.AuthenticatedBySession
                        ? ConnectionStates.AuthState.Reauthenticating : ConnectionStates.AuthState.Authenticating;
                    SetAuthState(next, "正在认证……", ToneBusy, true);
                    return;
                }
                const string retryWaitPrefix = "auth_retry_wait:";
                if (token.StartsWith(retryWaitPrefix, StringComparison.Ordinal))
                {
                    SetStatus("暂未完成认证，预计 " + token.Substring(retryWaitPrefix.Length) + " 分钟后自动重试。", ToneWarn);
                    return;
                }
                if (token == "auth_ok")
                {
                    if (state.Auth != ConnectionStates.AuthState.Authenticating && state.Auth != ConnectionStates.AuthState.WaitingForNetwork &&
                        state.Auth != ConnectionStates.AuthState.Reauthenticating) return; // duplicate auth_ok cannot invalidate newer network evidence
                    connect.Text = "运行中…"; runSawProgress = true;
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown); // authentication is not a reachability probe
                    SetAuthState(ConnectionStates.AuthState.Authenticated, "认证成功，互联网可达性待确认。", ToneOk);
                    SaveAfterAuth();
                }
                else if (token == "auth_failed")
                {
                    runSawProgress = true; authFailureSeen = true; retryEligible = false;
                    state.Failure = ConnectionStates.FailureKind.Authentication;
                    state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.Failed, "认证失败（原因未知），请检查网络及账号状态。", ToneFail);
                }
                else if (token == "wait_network")
                {
                    runSawProgress = true; state.SetNetwork(ConnectionStates.NetworkState.Unknown);
                    SetAuthState(ConnectionStates.AuthState.WaitingForNetwork, "正在等待接入校园网……请确认电脑已连上校园网的 Wi-Fi 或网线。", ToneBusy, true);
                }
                else if (token == "online")
                {
                    lastNetworkEvidence = receivedAt;
                    runSawProgress = true; connect.Text = "运行中…";
                    state.SetNetwork(ConnectionStates.NetworkState.InternetAvailable);
                    UpdateConnectionPill();
                    if (state.Auth == ConnectionStates.AuthState.Authenticated)
                        SetStatus("网络已连通，本程序已完成认证。", ToneOk);
                    else
                        SetAuthState(ConnectionStates.AuthState.AuthenticatedBySession, "网络已连通；尚未验证本次输入的凭据。", ToneOk);
                    // An existing internet connection does not validate the submitted credentials.
                }
                else if (token == "stop_accepted") SetAuthState(ConnectionStates.AuthState.LoggingOut, "正在退出认证，服务器登出结果待确认。", ToneWarn, true);
                else if (token == "stop_channel_failed") SetStatus("停止指令发送失败，可稍后点“强制结束”。", ToneFail);
            }); } catch (InvalidOperationException) { }
        }

        private void OnCoreExit(Process started)
        {
            if (!Object.ReferenceEquals(running, started) || Object.ReferenceEquals(completedProcess, started)) return;
            completedProcess = started;
            timer.Stop(); stopping = false; connect.Text = "立即连接";
            int code = -1;
            try { code = started.ExitCode; } catch { }
            bool crashed = code != 0 && !authFailureSeen && !userStopped;
            state.SetCore(crashed ? ConnectionStates.CoreState.Crashed : ConnectionStates.CoreState.Exited);
            state.SetNetwork(ConnectionStates.NetworkState.Unknown);
            if (userStopped)
            {
                state.SetHandoff(ConnectionStates.HandoffState.None);
                SetAuthState(ConnectionStates.AuthState.Idle, "认证已停止。服务器会话状态待确认。", ToneInfo);
                activeTask = null;
            }
            else if (authFailureSeen)
            {
                SetAuthState(ConnectionStates.AuthState.Failed, "认证失败，进程已退出；服务器会话状态未知。", ToneFail);
                activeTask = null;
            }
            else if (crashed || (!userStopped && code == 0))
            {
                state.Failure = retryEligible ? ConnectionStates.FailureKind.Network : ConnectionStates.FailureKind.Runtime;
                SetAuthState(ConnectionStates.AuthState.Failed, (retryEligible ? "网络探测失败" : "认证核心运行中断") + "（退出码 " + code + "），服务器会话状态未知。", ToneFail);
                ScheduleAutoRetry(code);
            }
            else
            {
                SetAuthState(ConnectionStates.AuthState.Idle, "认证已停止。服务器上的会话可能稍后才释放。", ToneWarn);
                activeTask = null;
            }
            RefreshOperationPermissions();
            if (closingAfterStop) { closingAfterStop = false; Close(); }
        }

        private void BeginStop()
        {
            if (serviceBusy) { SetStatus("正在进行系统服务切换，完成后才能停止或退出。", ToneWarn); return; }
            // Cancellation intent is independent of process existence and invalidates outstanding checks.
#if CHEZHI_LICENSED
            licenseGate.Invalidate();
#endif
            userStopped = true; taskGeneration++; retryTimer.Stop(); activeTask = null;
            state.SetNetwork(ConnectionStates.NetworkState.Unknown); UpdateConnectionPill();
            if (running == null || running.HasExited)
            {
                RefreshOperationPermissions();
                state.SetHandoff(ConnectionStates.HandoffState.None);
                SetAuthState(ConnectionStates.AuthState.Idle, "已取消连接或重试。", ToneInfo);
                return;
            }
            if (stopping) return;
            stopping = true; grace = Stopwatch.StartNew(); RefreshOperationPermissions();
            state.SetCore(ConnectionStates.CoreState.Stopping);
            SetAuthState(ConnectionStates.AuthState.LoggingOut, "正在停止认证……", ToneWarn, true);
            timer.Start();
            bool sent = RequestStop(running);
            SetStatus(sent ? "正在停止认证，等待程序清理退出……" : "停止指令未收到确认，继续等待退出……", ToneWarn, true);
        }

        private void StopClicked(object sender, EventArgs e)
        {
            var intent = CurrentPermissions().Stop;
            if (intent == ConnectionStates.StopIntent.None) return;
            if (intent != ConnectionStates.StopIntent.ForceCore) { try { BeginStop(); } catch { SetStatus("停止请求发送失败，窗口将保持开启。", ToneFail); } return; }
            Process target = running;
            if (MessageBox.Show(this, "强制结束可能让服务器会话多保留一会儿，确定立即结束认证进程？", "强制结束", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
            try { if (stopping && target != null && Object.ReferenceEquals(running, target) && !target.HasExited) target.Kill(); }
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
                userStopped = true; taskGeneration++; retryTimer.Stop(); activeTask = null;
                try { if (running != null && !running.HasExited) RequestStop(running); } catch { }
                return;
            }
            if (serviceBusy) { e.Cancel = true; exitRequested = false; RefreshOperationPermissions(); SetStatus("正在处理服务操作，请稍候……", ToneWarn, true); return; }
            if (running == null || running.HasExited) { userStopped = true; taskGeneration++; retryTimer.Stop(); activeTask = null; return; }
            e.Cancel = true;
            if (!stopping && MessageBox.Show(this, "退出将断开校园网认证，共享的热点也会断网。确定退出？", "退出澈知科技", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) { exitRequested = false; RefreshOperationPermissions(); return; }
            // 核心可能在确认框期间已自行退出 (确认框会泵消息): 此时无需再等待, 直接放行关闭
            if (running.HasExited) { stopping = false; e.Cancel = false; return; }
            userStopped = true; retryTimer.Stop(); // 确认退出=停止意图：取消待执行的自动重试
            closingAfterStop = true;
            ShowFromTray();
            try { BeginStop(); } catch { exitRequested = false; closingAfterStop = false; RefreshOperationPermissions(); SetStatus("停止请求发送失败，窗口将保持开启。", ToneFail); }
        }

        // A09：程序重启后识别未完成的服务切换，向用户提供"经授权的恢复选项"（不擅自操作服务）
        private async Task PromptPendingServiceRecovery()
        {
            try
            {
                int generation = taskGeneration;
                var journal = ServiceJournal.Load(ServiceJournal.DefaultPath());
                if (journal.Invalid)
                {
                    SetStatus("服务事务记录损坏，请手动核对天翼服务状态。", ToneWarn);
                    return;
                }
                if (journal.HasPending)
                {
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "上次服务事务未完成；请使用托盘菜单“核对遗留服务事务”。", ToneWarn);
                    return;
                }
                if (!journal.NeedsRecovery && !journal.Uncertain) return;
                string actual = await Task.Run((Func<string>)CurrentServiceState);
                if (HandoffCancelled(generation, false) || serviceBusy || connectCheckPending || activeTask != null) return;
                if (actual == "Running") { journal.ReconcileRunning(); return; }
                if (actual != "Stopped" || (!journal.NeedsRecovery && journal.StateBefore != "Running"))
                {
                    SetStatus("上次服务切换结果不确定，请手动核对当前服务状态。", ToneWarn);
                    return;
                }
                SetStatus("天翼后台服务已停止，存在尚未确认的恢复责任。", ToneWarn);
                if (MessageBox.Show(this, "检测到上次操作可能停止了原本运行的天翼服务。是否恢复？（需要管理员确认）",
                    "恢复天翼服务", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    if (!HandoffCancelled(generation, false) && activeTask == null) await RunServiceAction(false);
            }
            catch { if (!IsDisposed) SetStatus("无法读取服务恢复记录，请手动核对。", ToneWarn); }
        }

        private static string IdentityReasonText(string reason)
        {
            switch (reason)
            {
                case "path_unresolved": return "无法解析官方程序路径";
                case "service_missing": return "未找到后台服务";
                case "unquoted_service_path": return "服务路径含空格但未加引号，存在启动歧义；请使用官方安装程序修复";
                case "file_missing": return "文件不存在";
                case "signature_untrusted": return "签名信任验证失败";
                case "publisher_not_allowlisted": return "发布者尚未确认";
                case "signature_unavailable": return "无法读取数字签名";
                default: return "身份检查异常";
            }
        }

        private async Task<bool> LaunchOfficialAsync(int generation)
        {
            if (HandoffCancelled(generation, false)) return false;
            SetHandoff(ConnectionStates.HandoffState.Launching, "正在定位并验证官方天翼客户端……", ToneBusy, true);
            string client = await Task.Run((Func<string>)FindOfficialClient);
            string reason = await Task.Run(() => ServiceIdentity.VerificationReason(client));
            if (HandoffCancelled(generation, false)) return false;
            if (reason != "verified")
            {
                SetHandoff(ConnectionStates.HandoffState.Blocked, "后台服务已检查，但无法自动启动天翼客户端（" + IdentityReasonText(reason) + "）。请从开始菜单手动启动。", ToneWarn);
                return false;
            }
            var observation = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
            if (HandoffCancelled(generation, false)) return false;
            state.SetService(observation.Service);
            var decision = observation.Decide(ConnectionStates.HandoffIntent.Restore);
            if (decision == ConnectionStates.HandoffDecision.ExistingClient)
            { SetHandoff(ConnectionStates.HandoffState.OfficialRunning, HandoffMessage(decision), ToneInfo); return true; }
            if (decision != ConnectionStates.HandoffDecision.LaunchClient)
            { SetHandoff(ConnectionStates.HandoffState.Blocked, HandoffMessage(decision), ToneWarn); return false; }
            using (Process child = Process.Start(new ProcessStartInfo { FileName = client, WorkingDirectory = Path.GetDirectoryName(client), UseShellExecute = true }))
            {
                if (child == null)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, "未能启动官方客户端，请从开始菜单打开。", ToneWarn); return false; }
            }
            // Process.Start returning a handle does not prove the UI appeared.
            var launchClock = Stopwatch.StartNew();
            while (launchClock.Elapsed.TotalSeconds < 8 && !HandoffCancelled(generation, false))
            {
                await Task.Delay(500);
                var current = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
                if (HandoffCancelled(generation, false)) return false;
                state.SetService(current.Service);
                if (current.VisibleClient)
                {
                    SetHandoff(ConnectionStates.HandoffState.OfficialRunning, "已检测到经过身份验证的天翼窗口，请在官方客户端登录。", ToneOk);
                    return true;
                }
            }
            if (!HandoffCancelled(generation, false))
                SetHandoff(ConnectionStates.HandoffState.LaunchUnconfirmed,
                    "已发送启动请求，但尚未确认天翼窗口出现；请检查任务栏、托盘或开始菜单。", ToneWarn);
            return false;
        }

        private async Task<bool> RunServiceAction(bool forPrepare, bool fromConnect = false)
        {
            var rights = CurrentPermissions();
            if (exitRequested || (fromConnect ? !rights.CanPrepareForConnection : !rights.CanSwitch)) return false;
#if CHEZHI_LICENSED
            if (forPrepare && licenseGate.Busy) return false;
#endif
            if (!fromConnect) { taskGeneration++; state.TrySetAuth(ConnectionStates.AuthState.Idle, "handoff request"); }
            if (!JournalAllowsNewAction()) return false;
            int generation = taskGeneration;
            serviceBusy = true; RefreshOperationPermissions();
            ServiceJournal journal = null;
            bool started = false;
            int? code = null;
            try
            {
#if CHEZHI_LICENSED
                if (forPrepare && (!await licenseGate.VerifyAsync(this, !fromConnect) || HandoffCancelled(generation, fromConnect)))
                {
                    if (!HandoffCancelled(generation, fromConnect)) SetHandoff(ConnectionStates.HandoffState.Blocked, licenseGate.Message, ToneWarn);
                    return false;
                }
#endif
                var intent = forPrepare ? ConnectionStates.HandoffIntent.Prepare : ConnectionStates.HandoffIntent.Restore;
                SetHandoff(ConnectionStates.HandoffState.Checking, "正在检查天翼服务与进程……", ToneBusy, true);
                var observation = await WaitForHandoffAsync(intent, generation, fromConnect);
                if (observation == null || HandoffCancelled(generation, fromConnect)) return false;
                var decision = observation.Decide(intent);
                if (decision == ConnectionStates.HandoffDecision.Ready)
                { SetHandoff(ConnectionStates.HandoffState.Ready, "天翼客户端已退出，后台服务无需停止，可以连接。", ToneOk); return true; }
                if (decision == ConnectionStates.HandoffDecision.ExistingClient)
                { SetHandoff(ConnectionStates.HandoffState.OfficialRunning, HandoffMessage(decision), ToneInfo); return true; }
                if (decision == ConnectionStates.HandoffDecision.LaunchClient)
                {
                    if (MessageBox.Show(this, "无需切换后台服务，是否打开官方天翼客户端？", "恢复天翼",
                        MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                    { SetHandoff(ConnectionStates.HandoffState.None, "已取消打开天翼客户端。", ToneInfo); return false; }
                    return await LaunchOfficialAsync(generation);
                }
                if (decision != ConnectionStates.HandoffDecision.NeedStopService && decision != ConnectionStates.HandoffDecision.NeedStartService)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, HandoffMessage(decision), ToneWarn); return false; }
#if CHEZHI_LICENSED
                if (forPrepare)
                {
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "授权候选版尚未验收管理员服务切换，暂不停止天翼后台服务。恢复天翼仍可用。", ToneWarn);
                    return false;
                }
#endif
                string instructions = forPrepare
                    ? "天翼客户端已退出，但后台服务仍运行。请确认已断开天翼并关闭移动热点。继续将请求管理员权限停止后台服务。" +
                        (fromConnect ? " 准备成功后将自动继续本次认证。" : "")
                    : "请确认已停止澈知认证并关闭移动热点。继续将请求管理员权限启动天翼后台服务，然后打开官方客户端。";
                if (MessageBox.Show(this, instructions, forPrepare ? "准备连接" : "恢复天翼",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK)
                { SetHandoff(ConnectionStates.HandoffState.None, "已取消服务切换。", ToneInfo); return false; }
                observation = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
                if (HandoffCancelled(generation, fromConnect)) return false;
                state.SetService(observation.Service);
                if (observation.Decide(intent) != decision)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, "确认期间天翼状态发生变化，未执行切换，请重试。", ToneWarn); return false; }
                string before = observation.Service == ConnectionStates.ServiceState.Running ? "Running" : "Stopped";
                journal = ServiceJournal.Load(ServiceJournal.DefaultPath());
                if (journal.Invalid)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, "服务事务记录损坏，需先人工核对，未执行切换。", ToneWarn); return false; }
                BootEvidence boot = await Task.Run((Func<BootEvidence>)ReadBootEvidence);
                if (HandoffCancelled(generation, fromConnect)) return false;
                if (boot == null)
                { SetHandoff(ConnectionStates.HandoffState.Blocked, "无法读取 Windows 启动代次，未执行服务切换。", ToneWarn); return false; }
#if CHEZHI_LICENSED
                if (forPrepare && (!await licenseGate.VerifyAsync(this, false) || HandoffCancelled(generation, fromConnect)))
                {
                    if (!HandoffCancelled(generation, fromConnect)) SetHandoff(ConnectionStates.HandoffState.Blocked, licenseGate.Message, ToneWarn);
                    return false;
                }
                // Re-observe after the asynchronous license exchange.
                if (forPrepare)
                {
                    observation = await Task.Run((Func<HandoffObservation>)ObserveHandoff);
                    if (HandoffCancelled(generation, fromConnect) || observation.Decide(intent) != decision || !licenseGate.Allowed)
                        return false;
                }
#endif
                journal.Begin(forPrepare ? "prepare" : "restore", before, boot.Ticks, boot.Sequence);
                state.SetService(ConnectionStates.ServiceState.Transitioning);
                SetHandoff(forPrepare ? ConnectionStates.HandoffState.Preparing : ConnectionStates.HandoffState.Restoring, "请在弹出的管理员确认窗口中操作……", ToneBusy, true);
                var info = new ProcessStartInfo { FileName = Application.ExecutablePath, Arguments = forPrepare ? "--admin-prepare" : "--admin-restore", UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
                Process child = Process.Start(info);
                if (child == null) throw new InvalidOperationException();
                started = true;
                bool transferred = false;
                string actual = null;
                try
                {
                    bool exited = await Task.Run(() => child.WaitForExit(60000));
                    if (!exited)
                    {
                        pendingServiceChild = child; pendingServiceJournal = journal; transferred = true;
                        if (!IsDisposed) SetHandoff(ConnectionStates.HandoffState.Blocked,
                            "服务操作超时，结果待确认。已取消后续自动连接或启动，程序将继续只读检查。", ToneWarn);
                        return false;
                    }
                    code = child.ExitCode;
                    string observed = await Task.Run(readServiceState);
                    if (MapServiceState(observed) == ConnectionStates.ServiceState.Unknown || MapServiceState(observed) == ConnectionStates.ServiceState.Transitioning)
                    {
                        pendingServiceChild = child; pendingServiceJournal = journal; transferred = true;
                        SetHandoff(ConnectionStates.HandoffState.Blocked, "服务操作已退出，但实际服务状态仍待确认；不会自动继续。", ToneWarn);
                        return false;
                    }
                    journal.Complete(code, observed, false);
                    actual = observed;
                }
                catch
                {
                    pendingServiceChild = child; pendingServiceJournal = journal; transferred = true;
                    throw;
                }
                finally { if (!transferred) child.Dispose(); }
                if (HandoffCancelled(generation, fromConnect)) return false;
                state.SetService(MapServiceState(actual));
                bool expected = forPrepare ? actual == "Stopped" || actual == "NotInstalled" : actual == "Running";
                if (journal.HasConfirmedOutcome && expected)
                {
                    if (forPrepare)
                    { SetHandoff(ConnectionStates.HandoffState.Ready, "准备完成，可以连接校园网。", ToneOk); return true; }
                    return await LaunchOfficialAsync(generation);
                }
                string failure = code == 0 || code == 10 ? "服务操作返回成功，但事务结果或实际状态未得到确认，请核对后重新选择操作。" : ServiceError(code ?? 25);
                if (code == 21)
                {
                    string reason = await Task.Run((Func<string>)ServiceVerificationReason);
                    if (HandoffCancelled(generation, fromConnect)) return false;
                    failure += " 当前只读检查：" + IdentityReasonText(reason) + "。";
                }
                SetHandoff(ConnectionStates.HandoffState.Blocked, failure, ToneWarn);
                return false;
            }
            catch (Win32Exception e)
            {
                if (!started && journal != null && journal.HasPending && e.NativeErrorCode == 1223)
                    try { journal.Complete(null, "unknown", true); } catch { }
                state.SetService(ConnectionStates.ServiceState.Unknown);
                if (!IsDisposed) SetHandoff(ConnectionStates.HandoffState.Blocked, e.NativeErrorCode == 1223 ? "已取消管理员授权。" : "无法启动管理员操作或官方客户端，请重试。", ToneWarn);
                return false;
            }
            catch
            {
                state.SetService(ConnectionStates.ServiceState.Unknown);
                if (!IsDisposed) SetHandoff(ConnectionStates.HandoffState.Blocked, "操作结果未知，请检查服务状态。", ToneWarn);
                return false;
            }
            finally
            {
                serviceBusy = pendingServiceChild != null;
                if (!serviceBusy && !IsDisposed && !Disposing && !exitRequested && generation != taskGeneration)
                    SetHandoff(ConnectionStates.HandoffState.Blocked, "系统状态已变化，服务操作后续步骤已取消；请重新检查。", ToneWarn);
                RefreshOperationPermissions();
            }
        }

        private async Task PollTimedOutServiceAsync()
        {
            if (pollingService || pendingServiceChild == null || IsDisposed || Disposing) return;
            Process child = pendingServiceChild;
            pollingService = true;
            try
            {
                if (!child.HasExited) return;
                int code = child.ExitCode;
                string actual = await Task.Run(readServiceState);
                if (IsDisposed || Disposing) return;
                // A terminated helper is not proof that an SCM transition has settled.
                var observed = MapServiceState(actual);
                if (observed == ConnectionStates.ServiceState.Unknown || observed == ConnectionStates.ServiceState.Transitioning) return;
                pendingServiceJournal.Complete(code, actual, false);
                state.SetService(MapServiceState(actual));
                SetHandoff(ConnectionStates.HandoffState.Blocked,
                    "超时服务操作已结束（" + actual + "），请重新选择准备、连接或恢复；不会自动继续。", ToneWarn);
            }
            catch
            {
                if (!IsDisposed) SetHandoff(ConnectionStates.HandoffState.Blocked, "服务操作结果仍无法确认；已保留事务记录。", ToneWarn);
                return;
            }
            finally { pollingService = false; }
            if (!IsDisposed && !Disposing && Object.ReferenceEquals(pendingServiceChild, child))
            {
                pendingServiceChild = null; pendingServiceJournal = null; child.Dispose();
                serviceBusy = false; RefreshOperationPermissions();
            }
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
