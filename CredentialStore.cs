using System;
using System.Collections.Generic;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Web.Script.Serialization;

internal sealed class SavedCredential
{
    public string Username { get; set; }
    public string Password { get; set; }
    public int Channel { get; set; }
}

// The record is encrypted for the current Windows user with DPAPI. No plaintext
// credential file, command-line argument, environment variable, or log is used.
internal sealed class CredentialStore
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CZT1");
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("CheZhiTech.CampusNetwork.Credentials.v1");
    private static readonly UTF8Encoding Utf8 = new UTF8Encoding(false, true);
    private readonly string directory;
    private readonly string filePath;

    internal CredentialStore(string directoryPath)
    {
        directory = Path.GetFullPath(directoryPath);
        filePath = Path.Combine(directory, "credentials.v1.dat");
    }

    internal static string DefaultDirectory()
    {
        string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (String.IsNullOrEmpty(root)) throw new IOException("Windows user profile is unavailable.");
        return Path.Combine(root, "CheZhiTech", "CampusNetwork");
    }

    internal string FilePath { get { return filePath; } }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Unexpected reparse point.");
    }

    private void EnsureDirectory()
    {
        if (!Directory.Exists(directory))
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null) throw new IOException("Windows user identity is unavailable.");
            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            Directory.CreateDirectory(directory, acl);
        }
        RejectReparsePoint(directory);
    }

    private static void Validate(SavedCredential value)
    {
        if (value == null || String.IsNullOrEmpty(value.Username) || String.IsNullOrEmpty(value.Password) ||
            Utf8.GetByteCount(value.Username) >= 16 || Utf8.GetByteCount(value.Password) >= 128 ||
            value.Channel < 1 || value.Channel > 5)
            throw new InvalidDataException("Credential values are invalid.");
    }

    private static byte[] ReadCipher(string path)
    {
        RejectReparsePoint(path);
        var info = new FileInfo(path);
        if (info.Length < 20 || info.Length > 65536) throw new InvalidDataException("Credential record size is invalid.");
        byte[] record = File.ReadAllBytes(path);
        if (record.Length < Magic.Length) throw new InvalidDataException("Credential record is invalid.");
        for (int i = 0; i < Magic.Length; i++)
            if (record[i] != Magic[i]) throw new InvalidDataException("Credential record is not recognized.");
        var cipher = new byte[record.Length - Magic.Length];
        Buffer.BlockCopy(record, Magic.Length, cipher, 0, cipher.Length);
        Array.Clear(record, 0, record.Length);
        return cipher;
    }

    internal SavedCredential Load()
    {
        if (!File.Exists(filePath)) return null;
        if (!Directory.Exists(directory)) throw new IOException("Credential directory is unavailable.");
        RejectReparsePoint(directory);
        byte[] cipher = null, plain = null;
        try
        {
            cipher = ReadCipher(filePath);
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
        EnsureDirectory();
        // Never replace an unknown, corrupted, or undecryptable existing record.
        if (File.Exists(filePath)) Load();
        byte[] plain = null, cipher = null;
        string temp = Path.Combine(directory, ".credential-" + Guid.NewGuid().ToString("N") + ".tmp");
        bool created = false;
        try
        {
            plain = Utf8.GetBytes(new JavaScriptSerializer().Serialize(value));
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null) throw new IOException("Windows user identity is unavailable.");
            var acl = new FileSecurity();
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, AccessControlType.Allow));
            using (var output = new FileStream(temp, FileMode.CreateNew, FileSystemRights.Write, FileShare.None, 4096, FileOptions.WriteThrough, acl))
            {
                created = true;
                output.Write(Magic, 0, Magic.Length);
                output.Write(cipher, 0, cipher.Length);
                output.Flush(true);
            }
            if (File.Exists(filePath)) { RejectReparsePoint(filePath); File.Replace(temp, filePath, null); }
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
        if (!File.Exists(filePath)) return;
        RejectReparsePoint(directory);
        RejectReparsePoint(filePath);
        // Explicit user action can remove a damaged record, but never an unknown file.
        using (var input = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var header = new byte[Magic.Length];
            if (input.Read(header, 0, header.Length) != header.Length) throw new InvalidDataException("Credential record is not recognized.");
            for (int i = 0; i < Magic.Length; i++)
                if (header[i] != Magic[i]) throw new InvalidDataException("Credential record is not recognized.");
        }
        File.Delete(filePath);
    }
}
