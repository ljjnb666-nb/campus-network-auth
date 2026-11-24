using System;
using System.IO;
using System.Runtime.InteropServices;

// 服务身份校验（A14）：文件名匹配仅是最低门槛。本模块对服务可执行文件做
// Authenticode 签名验证（WinVerifyTrust，验证签名链完整性）+ 厂商元数据核对。
// 验证失败一律按"身份不符"处理（fail-closed）：提权子进程将拒绝操作该服务。
internal static class ServiceIdentity
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public uint pPolicyCallbackData;
        public uint pSIPCallbackData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPWStr)] string pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool LookupPrivilegeValue(string lpSystemName, string lpName, out long lpLuid);

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATE_ACTION_VERIFY = 1;
    private const uint WTD_STATE_ACTION_CLOSE = 2;
    private static readonly Guid GenericVerifyV2 = new Guid("{00AAC56B-CD44-11d0-8CC2-00C04FC295EE}");

    /// <summary>验证文件具有有效的 Authenticode 签名（不吊销检查：离线/内网环境可用）。</summary>
    internal static bool HasValidSignature(string filePath)
    {
        if (String.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return false;
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_FILE_INFO)),
            pcwszFilePath = Path.GetFullPath(filePath)
        };
        var data = new WINTRUST_DATA
        {
            cbStruct = (uint)Marshal.SizeOf(typeof(WINTRUST_DATA)),
            dwUIChoice = WTD_UI_NONE,
            fdwRevocationChecks = WTD_REVOKE_NONE,
            dwUnionChoice = WTD_CHOICE_FILE,
            pFile = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(WINTRUST_FILE_INFO))),
            dwStateAction = WTD_STATE_ACTION_VERIFY
        };
        try
        {
            Marshal.StructureToPtr(fileInfo, data.pFile, false);
            int result = WinVerifyTrust(IntPtr.Zero, GenericVerifyV2.ToString("B"), ref data);
            // 无论如何都要关闭状态句柄
            WINTRUST_DATA close = data;
            close.dwStateAction = WTD_STATE_ACTION_CLOSE;
            try { WinVerifyTrust(IntPtr.Zero, GenericVerifyV2.ToString("B"), ref close); } catch { }
            return result == 0;
        }
        catch { return false; }
        finally { Marshal.FreeHGlobal(data.pFile); }
    }

    /// <summary>
    /// 身份综合判定：Authenticode 签名链有效（WinVerifyTrust，免吊销检查以适配离线/内网）
    /// 且签名者证书主题包含电信相关标识（实测官方 ESurfingSvr.exe 由
    /// "中数通信息有限公司·电信信息化部"签名，但文件不带版本资源——元数据不可用作依据）。
    /// </summary>
    internal static bool VerifyExpectedService(string exePath)
    {
        try
        {
            if (!HasValidSignature(exePath)) return false;
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exePath);
            string subject = cert == null ? "" : cert.Subject ?? "";
            return subject.IndexOf("电信", StringComparison.Ordinal) >= 0 ||
                   subject.IndexOf("telecom", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch { return false; }
    }

    internal static string Describe(string exePath)
    {
        try
        {
            var cert = System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exePath);
            return cert == null ? "?" : cert.Subject;
        }
        catch { return "?"; }
    }
}
