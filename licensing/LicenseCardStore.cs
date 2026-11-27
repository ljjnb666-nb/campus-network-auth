// Separate, user-consented DPAPI record. Never stores an authorization grant.
using System;
using System.IO;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32;

namespace Chezhi.Licensing
{
    internal sealed class LicenseCardStore
    {
        private const string DefaultPath=@"Software\CheZhiTech\CampusLicenseCard\v1";
        private const string Value="Card";
        private readonly string path,mutexName;
        private readonly SecurityIdentifier user;
        private static readonly SecurityIdentifier SystemSid=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        private readonly byte[] context;
        internal LicenseCardStore():this(DefaultPath){}
        private LicenseCardStore(string p)
        {
            path=p;
            using(WindowsIdentity id=WindowsIdentity.GetCurrent()) user=id.User;
            if(user==null)throw new SecurityException("Missing user identity.");
            context=Encoding.UTF8.GetBytes("CheZhi.Campus.Card.v1|"+user.Value+"|"+path);
            using(SHA256 hash=SHA256.Create())mutexName=@"Global\CheZhi.Card."+BitConverter.ToString(hash.ComputeHash(context)).Replace("-","");
        }
#if CHEZHI_CARD_TEST
        internal static LicenseCardStore Fixture(Guid id)
        {if(id==Guid.Empty)throw new ArgumentException();return new LicenseCardStore(DefaultPath+@"-Fixtures\"+id.ToString("N"));}
        internal string FixturePath {get{return path;}}
#endif
        private bool Trusted(IdentityReference sid){return user.Equals(sid)||SystemSid.Equals(sid);}
        private void Verify(CommonObjectSecurity acl)
        {
            if(!user.Equals(acl.GetOwner(typeof(SecurityIdentifier)))||!acl.AreAccessRulesProtected)
                throw new SecurityException("Card record ACL mismatch.");
            foreach(AuthorizationRule r in acl.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            {
                AccessRule access=r as AccessRule;
                if(access==null||access.IsInherited||!Trusted(access.IdentityReference))
                    throw new SecurityException("Unexpected card record access.");
            }
        }
        private RegistrySecurity RegistryAcl()
        {
            var acl=new RegistrySecurity();acl.SetOwner(user);acl.SetAccessRuleProtection(true,false);
            foreach(SecurityIdentifier sid in new[]{user,SystemSid})
                acl.AddAccessRule(new RegistryAccessRule(sid,RegistryRights.FullControl,InheritanceFlags.ContainerInherit,PropagationFlags.None,AccessControlType.Allow));
            return acl;
        }
        private T Lease<T>(Func<T> action)
        {
            var acl=new MutexSecurity();acl.SetOwner(user);acl.SetAccessRuleProtection(true,false);
            foreach(SecurityIdentifier sid in new[]{user,SystemSid})acl.AddAccessRule(new MutexAccessRule(sid,MutexRights.FullControl,AccessControlType.Allow));
            bool created,held=false;
            using(var mutex=new Mutex(false,mutexName,out created,acl))
            {
                Verify(mutex.GetAccessControl());
                try
                {
                    try{held=mutex.WaitOne(TimeSpan.FromSeconds(5));}
                    catch(AbandonedMutexException){held=true;}
                    if(!held)throw new IOException("Card store busy.");
                    return action();
                }
                finally{if(held)mutex.ReleaseMutex();}
            }
        }
        private byte[] Entropy(byte[] fingerprint,byte[] scope)
        {
            if(fingerprint==null||fingerprint.Length!=32||scope==null||scope.Length!=32)throw new ArgumentException("Invalid card context.");
            byte[] b=new byte[context.Length+64];
            Buffer.BlockCopy(context,0,b,0,context.Length);
            Buffer.BlockCopy(fingerprint,0,b,context.Length,32);
            Buffer.BlockCopy(scope,0,b,context.Length+32,32);
            return b;
        }
        private static void Validate(byte[] card)
        {
            if(card==null||card.Length<1||card.Length>100)throw new InvalidDataException("Invalid card record.");
            foreach(byte b in card)if(b<0x21||b>0x7e)throw new InvalidDataException("Invalid card character.");
        }
        internal byte[] Load(byte[] fingerprint,byte[] scope)
        {
            byte[] entropy=Entropy(fingerprint,scope);
            return Lease(delegate{
                using(RegistryKey k=Registry.CurrentUser.OpenSubKey(path))
                {
                    if(k==null)return null;
                    Verify(k.GetAccessControl());
                    object value=k.GetValue(Value,null,RegistryValueOptions.DoNotExpandEnvironmentNames);
                    if(value==null)return null;
                    byte[] cipher=value as byte[];
                    if(cipher==null||cipher.Length==0||cipher.Length>4096||k.GetValueKind(Value)!=RegistryValueKind.Binary)
                        throw new InvalidDataException("Invalid card record.");
                    byte[] raw=ProtectedData.Unprotect(cipher,entropy,DataProtectionScope.CurrentUser);
                    try{Validate(raw);return raw;}
                    catch{Array.Clear(raw,0,raw.Length);throw;}
                }
            });
        }
        // Only caller-confirmed replacement after verified online activation.
        // No write on network failure; no automatic change of enrollment.
        internal void Save(byte[] card,byte[] fingerprint,byte[] scope)
        {
            Validate(card);
            byte[] cipher=ProtectedData.Protect(card,Entropy(fingerprint,scope),DataProtectionScope.CurrentUser);
            Lease(delegate{
                using(RegistryKey k=Registry.CurrentUser.CreateSubKey(path,RegistryKeyPermissionCheck.ReadWriteSubTree,RegistryAcl()))
                {
                    if(k==null)throw new IOException("Card store unavailable.");
                    Verify(k.GetAccessControl());
                    k.SetValue(Value,cipher,RegistryValueKind.Binary);k.Flush();
                    byte[] actual=k.GetValue(Value) as byte[];
                    if(actual==null||actual.Length!=cipher.Length)throw new IOException("Card write verification failed.");
                    int diff=0;for(int i=0;i<cipher.Length;i++)diff|=actual[i]^cipher[i];
                    if(diff!=0)throw new IOException("Card write verification failed.");
                }
                return true;
            });
        }
        internal void Forget()
        {
            Lease(delegate{
                using(RegistryKey k=Registry.CurrentUser.OpenSubKey(path,true))
                {
                    if(k!=null){Verify(k.GetAccessControl());k.DeleteValue(Value,false);k.Flush();}
                }
                return true;
            });
        }
    }
}
