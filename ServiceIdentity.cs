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
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);

    private const uint WTD_UI_NONE = 2;
    private const uint WTD_REVOKE_NONE = 0;
    private const uint WTD_CHOICE_FILE = 1;
    private const uint WTD_STATE_ACTION_VERIFY = 1;
    private const uint WTD_STATE_ACTION_CLOSE = 2;
    private const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x00001000;
    private const uint WTD_REVOCATION_CHECK_NONE = 0x00000010;
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
            dwStateAction = WTD_STATE_ACTION_VERIFY,
            dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
        };
        bool marshalled = false;
        Guid action = GenericVerifyV2;
        try
        {
            Marshal.StructureToPtr(fileInfo, data.pFile, false);
            marshalled = true;
            return WinVerifyTrust(new IntPtr(-1), ref action, ref data) == 0;
        }
        catch { return false; }
        finally
        {
            if (marshalled)
            {
                data.dwStateAction = WTD_STATE_ACTION_CLOSE;
                try { WinVerifyTrust(new IntPtr(-1), ref action, ref data); } catch { }
                Marshal.DestroyStructure(data.pFile, typeof(WINTRUST_FILE_INFO));
            }
            Marshal.FreeHGlobal(data.pFile);
        }
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
            using (var cert = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                System.Security.Cryptography.X509Certificates.X509Certificate.CreateFromSignedFile(exePath)))
            {
                // Exact known publisher identity; unknown publishers require explicit review.
                string name = cert.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false);
                return IsKnownPublisher(name, cert.Subject);
            }
        }
        catch { return false; }
    }

    internal static bool IsKnownPublisher(string name, string subject)
    {
        // Do not interpret arbitrary substrings of a rendered distinguished name as an identity.
        return String.Equals(name, "中数通信息有限公司", StringComparison.Ordinal) ||
            String.Equals(name, "中数通信息有限公司·电信信息化部", StringComparison.Ordinal);
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
