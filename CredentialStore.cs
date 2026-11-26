using System;
using System.ComponentModel;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using Microsoft.Win32.SafeHandles;

internal sealed class SavedCredential
{
    public string Username { get; set; }
    public string Password { get; set; }
    public int Channel { get; set; }
}

internal sealed class CredentialStoreBusyException : IOException
{
    internal CredentialStoreBusyException(IOException cause)
        : base("The saved credential record is temporarily busy.", cause) { }
}

// The record is encrypted for the current Windows user with DPAPI. No plaintext
// credential file, command-line argument, environment variable, or log is used.
internal sealed class CredentialStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CZT1");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CheZhiTech.CampusNetwork.Credentials.v1");
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private static readonly SecurityIdentifier SystemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier AdministratorsSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
    private readonly string directory;
    private readonly string filePath;
    private readonly string leasePath;

    internal CredentialStore(string directoryPath)
    {
        directory = Path.GetFullPath(directoryPath);
        filePath = Path.Combine(directory, "credentials.v1.dat");
        leasePath = Path.Combine(directory, "credentials.v1.lock");
    }

    internal static string DefaultDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (String.IsNullOrEmpty(root)) throw new IOException("Windows user profile is unavailable.");
        return Path.Combine(root, "CheZhiTech", "CampusNetwork");
    }

    internal string FilePath { get { return filePath; } }
    internal string LeasePath { get { return leasePath; } }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unexpected reparse point.");
    }

    private sealed class PinnedDirectories : IDisposable
    {
        internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();

        public void Dispose()
        {
            for (int i = Handles.Count - 1; i >= 0; i--) Handles[i].Dispose();
        }
    }

    private PinnedDirectories PinDirectories(bool create)
    {
        const uint readAttributes = 0x80, directoryAccess = 0x02000000, noFollow = 0x00200000;
        string root = Path.GetPathRoot(directory);
        if (directory.Length <= root.Length) throw new IOException("The credential store requires a private subdirectory.");
        var pinned = new PinnedDirectories();
        bool complete = false;
        try
        {
            string path = root;
            string[] parts = directory.Substring(root.Length).Split(
                new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries);
            for (int i = -1; i < parts.Length; i++)
            {
                if (i >= 0) path = Path.Combine(path, parts[i]);
                SafeFileHandle handle = TryOpen(path, readAttributes, FileShare.Read | FileShare.Write, directoryAccess | noFollow);
                if (handle == null)
                {
                    if (!create || i < 0) return null;
                    // The parent is already pinned. Without it, recursive
                    // directory creation could follow a substituted ancestor.
                    Directory.CreateDirectory(path, PrivateDirectorySecurity());
                    handle = TryOpen(path, readAttributes, FileShare.Read | FileShare.Write, directoryAccess | noFollow);
                    if (handle == null) throw new IOException("Created credential directory is unavailable.");
                }
                pinned.Handles.Add(handle);
                CheckObjectType(handle, true);
            }
            CheckPrivateAcl(Directory.GetAccessControl(directory, AccessControlSections.Access | AccessControlSections.Owner));
            complete = true;
            return pinned;
        }
        finally { if (!complete) pinned.Dispose(); }
    }

    private static DirectorySecurity PrivateDirectorySecurity()
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        acl.AddAccessRule(new FileSystemAccessRule(CurrentUserSid(), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        return acl;
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using (var identity = WindowsIdentity.GetCurrent())
        {
            SecurityIdentifier user = identity.User;
            if (user == null) throw new IOException("Windows user identity is unavailable.");
            return user;
        }
    }

    private static FileSecurity PrivateFileSecurity()
    {
        SecurityIdentifier user = CurrentUserSid();
        var acl = new FileSecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(SystemSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return acl;
    }

    private static void CheckPrivateAcl(FileSystemSecurity acl)
    {
        SecurityIdentifier user = CurrentUserSid();
        var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl == null || !TrustedSid(descriptor.Owner, user))
            throw new IOException("Unexpected credential object owner or access control.");
        foreach (GenericAce entry in descriptor.DiscretionaryAcl)
        {
            var rule = entry as QualifiedAce;
            if (rule == null || (rule.AceQualifier != AceQualifier.AccessDenied &&
                (rule.AceQualifier != AceQualifier.AccessAllowed || !TrustedSid(rule.SecurityIdentifier, user))))
                throw new IOException("Unexpected credential object access rule.");
        }
    }

    private static bool TrustedSid(IdentityReference identity, SecurityIdentifier user)
    {
        return user.Equals(identity) || SystemSid.Equals(identity) || AdministratorsSid.Equals(identity);
    }

    // Keep the same lease file across replacements and Forget. Closing its
    // handle, including on process termination, releases the exclusive lease.
    private FileStream AcquireLease()
    {
        const int waitMilliseconds = 750;
        var acl = PrivateFileSecurity();
        var wait = Stopwatch.StartNew();
        for (;;)
        {
            try
            {
                const uint genericReadWrite = 0xc0000000, noFollow = 0x00200000;
                SafeFileHandle handle = TryOpen(leasePath, genericReadWrite, FileShare.None, noFollow);
                if (handle == null)
                    return new FileStream(leasePath, FileMode.CreateNew, FileSystemRights.ReadData | FileSystemRights.WriteData,
                        FileShare.None, 4096, FileOptions.None, acl);
                FileStream lease = null;
                try
                {
                    CheckObjectType(handle, false);
                    lease = new FileStream(handle, FileAccess.ReadWrite);
                    CheckPrivateAcl(lease.GetAccessControl());
                    return lease;
                }
                catch
                {
                    if (lease != null) lease.Dispose();
                    else handle.Dispose();
                    throw;
                }
            }
            catch (IOException e)
            {
                var native = e.InnerException as Win32Exception;
                int code = native == null ? e.HResult & 0xffff : native.NativeErrorCode;
                if (code != 32 && code != 33 && code != 80 && code != 183) throw;
                long remaining = waitMilliseconds - wait.ElapsedMilliseconds;
                if (remaining <= 0) throw new CredentialStoreBusyException(e);
                Thread.Sleep((int)Math.Min(10, remaining));
            }
        }
    }

    private static void Validate(SavedCredential value)
    {
        if (value == null || String.IsNullOrEmpty(value.Username) || String.IsNullOrEmpty(value.Password) ||
            Utf8.GetByteCount(value.Username) >= 16 || Utf8.GetByteCount(value.Password) >= 128 ||
            value.Username.IndexOf('\0') >= 0 || value.Password.IndexOf('\0') >= 0 ||
            value.Channel < 1 || value.Channel > 5)
            throw new InvalidDataException("Credential values are invalid.");
    }

    // File.Exists suppresses access failures. Distinguish a missing record
    // from one that exists but cannot be read; never treat the latter as empty.
    // Bound the read on the same open handle, not a separate FileInfo check.
    internal static byte[] ReadBoundedRecord(string path, int maximum)
    {
        string parent = Path.GetDirectoryName(Path.GetFullPath(path));
        try { RejectReparsePoint(parent); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        try { RejectReparsePoint(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        // Allow a cooperating atomic replacement, but not in-place writers.
        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            return ReadBoundedStream(input, maximum);
    }

    private static byte[] ReadBoundedStream(FileStream input, int maximum)
    {
        using (var reader = new BinaryReader(input, Encoding.UTF8, true))
        {
            if (input.Length > maximum) throw new InvalidDataException("Record exceeds size limit.");
            byte[] record = reader.ReadBytes(maximum + 1);
            if (record.Length > maximum) throw new InvalidDataException("Record exceeds size limit.");
            return record;
        }
    }

    private byte[] ReadCipher()
    {
        using (var handle = OpenCredential(false))
        {
            if (handle == null) return null;
            using (var input = new FileStream(handle, FileAccess.Read))
            {
                CheckPrivateAcl(input.GetAccessControl());
                byte[] record = ReadBoundedStream(input, 65536);
                try
                {
                    if (record.Length < 20) throw new InvalidDataException("Credential record size is invalid.");
                    for (int i = 0; i < Magic.Length; i++)
                        if (record[i] != Magic[i]) throw new InvalidDataException("Credential record is not recognized.");
                    var cipher = new byte[record.Length - Magic.Length];
                    Buffer.BlockCopy(record, Magic.Length, cipher, 0, cipher.Length);
                    return cipher;
                }
                finally { Array.Clear(record, 0, record.Length); }
            }
        }
    }

    internal SavedCredential Load()
    {
        using (var pinned = PinDirectories(false))
        {
            if (pinned == null) return null;
            using (AcquireLease()) return LoadRecord();
        }
    }

    private SavedCredential LoadRecord()
    {
        byte[] cipher = null, plain = null;
        try
        {
            cipher = ReadCipher();
            if (cipher == null) return null;
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
            string json = Utf8.GetString(plain);
            var value = new JavaScriptSerializer().Deserialize<SavedCredential>(json);
            Validate(value);
            return value;
        }
        finally
        {
            if (cipher != null) Array.Clear(cipher, 0, cipher.Length);
            if (plain != null) Array.Clear(plain, 0, plain.Length);
        }
    }

    internal void Save(SavedCredential value)
    {
        Validate(value);
        using (PinDirectories(true))
        using (AcquireLease()) SaveRecord(value);
    }

    private void SaveRecord(SavedCredential value)
    {
        // Never replace an unknown, corrupted, or undecryptable existing record.
        bool replacing = LoadRecord() != null;
        byte[] plain = null, cipher = null;
        string temp = Path.Combine(directory, ".credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            plain = Utf8.GetBytes(new JavaScriptSerializer().Serialize(value));
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            var acl = PrivateFileSecurity();
            using (var output = new FileStream(temp, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, acl))
            {
                created = true;
                output.Write(Magic, 0, Magic.Length);
                output.Write(cipher, 0, cipher.Length);
                output.Flush(true);
            }
            if (replacing) { RejectReparsePoint(filePath); File.Replace(temp, filePath, null); }
            else File.Move(temp, filePath);
            created = false;
        }
        finally
        {
            if (plain != null) Array.Clear(plain, 0, plain.Length);
            if (cipher != null) Array.Clear(cipher, 0, cipher.Length);
            if (created) { try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
    }

    internal void Forget()
    {
        using (var pinned = PinDirectories(false))
        {
            if (pinned == null) return;
            using (AcquireLease()) ForgetRecord();
        }
    }

    private void ForgetRecord()
    {
        // Verify and delete the same open file. Refuse a direct reparse
        // point, and prevent other opens from modifying it during the check.
        using (var handle = OpenCredential(true))
        {
            if (handle == null) return;
            using (var input = new FileStream(handle, FileAccess.Read))
            {
                CheckPrivateAcl(input.GetAccessControl());
                // Explicit user action can remove a damaged record, but never an unknown file.
                foreach (byte expected in Magic)
                    if (input.ReadByte() != expected) throw new InvalidDataException("Credential record is not recognized.");
                const int fileDispositionInfo = 4;
                byte delete = 1; // FILE_DISPOSITION_INFO.DeleteFile is a Win32 BOOLEAN.
                if (!SetFileInformationByHandle(handle, fileDispositionInfo, ref delete, 1))
                    throw new IOException("Unable to delete the credential record.", new Win32Exception(Marshal.GetLastWin32Error()));
            }
        }
    }

    private SafeFileHandle OpenCredential(bool deleting)
    {
        const uint genericRead = 0x80000000, deleteAccess = 0x00010000;
        const uint openReparsePoint = 0x00200000;
        uint access = deleting ? genericRead | deleteAccess : genericRead;
        FileShare share = deleting ? FileShare.None : FileShare.Read | FileShare.Delete;
        SafeFileHandle handle = TryOpen(filePath, access, share, openReparsePoint);
        if (handle == null) return null;
        try
        {
            CheckObjectType(handle, false);
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    private static SafeFileHandle TryOpen(string path, uint access, FileShare share, uint flags)
    {
        const uint openExisting = 3;
        SafeFileHandle handle = CreateFileW(path, access, share, IntPtr.Zero, openExisting, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error == 2) return null; // ERROR_FILE_NOT_FOUND
        throw new IOException("Unable to open a credential object.", new Win32Exception(error));
    }

    private static void CheckObjectType(SafeFileHandle handle, bool directory)
    {
        const int fileAttributeTagInfo = 9;
        FileAttributeTag attributes;
        if (!GetFileInformationByHandleEx(handle, fileAttributeTagInfo, out attributes, 8))
            throw new IOException("Unable to inspect a credential object.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((attributes.Attributes & FileAttributes.ReparsePoint) != 0 ||
            ((attributes.Attributes & FileAttributes.Directory) != 0) != directory)
            throw new IOException("Unexpected credential object type.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileAttributeTag
    {
        internal FileAttributes Attributes;
        internal uint ReparseTag;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string fileName, uint desiredAccess, FileShare shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int informationClass,
        out FileAttributeTag information, uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle file, int informationClass,
        ref byte information, uint bufferSize);
}
