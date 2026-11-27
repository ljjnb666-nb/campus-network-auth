#if CHEZHI_LICENSE_UI_TEST
using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Win32;
using Chezhi.Licensing;

// Test-only trust values from a disposable loopback fixture. No production
// config override, campus account, core, SCM or actual campus network.
internal static class CandidateTrust
{
    internal static string Origin;
    internal static byte[] DllHash,Scope,SigningKey,CA;
}
internal static class DesktopLicenseOnlineSmoke
{
    private const string FixtureCard="ABCDE-FGHJK-MNPQR-STVWX-YZ012";
    private static byte[] Hex(string s)
    {
        byte[] b=new byte[s.Length/2];
        for(int i=0;i<b.Length;i++)b[i]=Convert.ToByte(s.Substring(i*2,2),16);
        return b;
    }
    private static void Check(bool value,string label){if(!value)throw new Exception(label);}
    private static void CheckNoIdentity(DeviceEnrollment enrollment,string label)
    {
        using(RegistryKey record=Registry.CurrentUser.OpenSubKey(enrollment.FixturePath))
            Check(record==null,label);
    }
    private static async Task Run(Form owner,Guid id)
    {
        var enrollment=DeviceEnrollment.Fixture(id);
        var store=LicenseCardStore.Fixture(id);
        byte[] fp;
        // Start with no enrollment record: the actual dialog, not the
        // test setup, must initiate the isolated CNG/DPAPI enrollment.
        CheckNoIdentity(enrollment,"fixture identity already exists");
        using(var gate=new DesktopLicenseGate(id))
        {
            Check(!gate.Allowed,"initial grant");
            Check(!await gate.VerifyAsync(owner,false),"missing card admitted");
            CheckNoIdentity(enrollment,"silent enrollment");
            gate.FixtureCard=FixtureCard;gate.FixtureRemember=true;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed,"implicit enrollment admitted");
            CheckNoIdentity(enrollment,"manual card created identity without consent");
            gate.FixtureFirst=true;gate.FixtureCancel=true;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed,"cancelled enrollment admitted");
            CheckNoIdentity(enrollment,"cancelled enrollment created identity");
            gate.FixtureCancel=false;gate.FixtureConfirmFirst=false;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed&&gate.FixtureConfirmationCount==1,"declined confirmation admitted");
            CheckNoIdentity(enrollment,"declined enrollment created identity");
            gate.FixtureConfirmFirst=true;
            Check(await gate.VerifyAsync(owner,true)&&gate.Allowed&&gate.FixtureDialogCount==4&&gate.FixtureConfirmationCount==2,"explicit first enrollment and activation");
            using(WindowsDeviceKey key=enrollment.OpenExisting())fp=key.Fingerprint;
            gate.FixtureFirst=false;
            byte[] saved=store.Load(fp,CandidateTrust.Scope);
            Check(saved!=null&&System.Text.Encoding.ASCII.GetString(saved)==FixtureCard,"verified save");
            Array.Clear(saved,0,saved.Length);
            gate.FixtureCancel=true;
            Check(!await gate.VerifyAsync(owner,true,true)&&gate.Allowed,"cancel replacement lost grant");
            gate.FixtureCancel=false;
            gate.Invalidate();Check(!gate.Allowed,"network invalidation");
            Check(await gate.VerifyAsync(owner,false)&&gate.Allowed,"fresh validation");
            gate.FixtureRenewDue();
            Check(await gate.RenewIfDueAsync()&&gate.Allowed,"real renewal");
            gate.FixtureCard="AAAAA-AAAAA-AAAAA-AAAAA-AAAAA";
            Check(!await gate.VerifyAsync(owner,true,true)&&!gate.Allowed,"denied replacement");
            saved=store.Load(fp,CandidateTrust.Scope);
            Check(saved!=null&&System.Text.Encoding.ASCII.GetString(saved)==FixtureCard,"denial replaced saved card");
            Array.Clear(saved,0,saved.Length);
        }
        // Different gate instance: actual DPAPI reopen and online validation,
        // not a cached grant. Same process, not a Windows reboot.
        using(var gate=new DesktopLicenseGate(id))
        {
            Check(!gate.Allowed,"reopen cached grant");
            Check(await gate.VerifyAsync(owner,false)&&gate.Allowed&&gate.FixtureDialogCount==0,"silent saved-card validation");
            Check(await gate.ForgetAsync()&&!gate.Allowed,"forget grant");
            Check(store.Load(fp,CandidateTrust.Scope)==null,"forget record");
            Check(!await gate.VerifyAsync(owner,false),"forgotten card restored");
            Guid originalSlot=enrollment.FixtureSlot();
            gate.FixtureCard=FixtureCard;gate.FixtureFirst=true;gate.FixtureRemember=true;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed&&gate.FixtureConfirmationCount==1,"duplicate enrollment admitted");
            Check(enrollment.FixtureSlot()==originalSlot,"duplicate enrollment replaced slot");
            Check(store.Load(fp,CandidateTrust.Scope)==null,"failed enrollment saved card");
            gate.FixtureFirst=false;
            Check(await gate.VerifyAsync(owner,true)&&gate.Allowed,"existing identity could not recover through manual activation");
        }
        using(WindowsDeviceKey key=enrollment.OpenExisting())
            Check(Convert.ToBase64String(key.Fingerprint)==Convert.ToBase64String(fp),"identity changed");
    }
    // Independent random profiles; never mutate the primary fixture or
    // production registry/key names. These are exception injections, not
    // claims of process-kill or power-loss testing.
    private sealed class InjectedEnrollmentFault : Exception {}
    private static byte[] RecordBytes(DeviceEnrollment e)
    {
        using(RegistryKey k=Registry.CurrentUser.OpenSubKey(e.FixturePath))
            return (byte[])k.GetValue("Enrollment");
    }
    private static void SetRecord(DeviceEnrollment e,byte[] bytes)
    {
        using(RegistryKey k=Registry.CurrentUser.OpenSubKey(e.FixturePath,true))
        {
            k.SetValue("Enrollment",bytes,RegistryValueKind.Binary);
            k.Flush();
        }
    }
    private static string KeyName(Guid slot){return "CheZhiTech.License.v1."+slot.ToString("N");}
    private static bool KeyExists(Guid slot)
    {return CngKey.Exists(KeyName(slot),CngProvider.MicrosoftSoftwareKeyStorageProvider);}
    private static void DeleteKey(Guid slot)
    {
        if(KeyExists(slot))
            using(CngKey key=CngKey.Open(KeyName(slot),CngProvider.MicrosoftSoftwareKeyStorageProvider))key.Delete();
    }
    private static void RejectRecovery(Func<WindowsDeviceKey> attempt,string label,bool allowCrypto)
    {
        try{using(WindowsDeviceKey key=attempt()){}}
        catch(EnrollmentRecoveryRequired){return;}
        catch(CryptographicException){if(allowCrypto)return;throw;}
        throw new Exception(label);
    }
    private static async Task CheckBlockedProfile(Form owner,Guid id,DeviceEnrollment e,string label)
    {
        byte[] before=RecordBytes(e);
        using(var gate=new DesktopLicenseGate(id))
        {
            Check(!await gate.VerifyAsync(owner,false)&&!gate.Allowed&&!gate.Busy,label+" silent");
            gate.FixtureCard=FixtureCard;gate.FixtureRemember=true;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed&&!gate.Busy,label+" reopen");
            gate.FixtureFirst=true;
            Check(!await gate.VerifyAsync(owner,true)&&!gate.Allowed&&!gate.Busy,label+" reenroll");
        }
        Check(Convert.ToBase64String(before)==Convert.ToBase64String(RecordBytes(e)),label+" changed record");
        using(RegistryKey card=Registry.CurrentUser.OpenSubKey(LicenseCardStore.Fixture(id).FixturePath))
            Check(card==null,label+" created card record");
    }
    private static async Task RunEnrollmentFaults(Form owner)
    {
        foreach(int stage in new[]{1,2})
        {
            Guid id=Guid.NewGuid();
            var e=DeviceEnrollment.Fixture(id);
            try
            {
                bool injected=false;
                e.FaultHook=delegate(int point){if(point==stage)throw new InjectedEnrollmentFault();};
                try{using(WindowsDeviceKey key=e.EnrollFirst(DeviceKeyPolicy.Software)){}}
                catch(InjectedEnrollmentFault){injected=true;}
                Check(injected,"enrollment fault hook not reached");
                Guid slot=e.FixtureSlot();
                Check(KeyExists(slot)==(stage==2),"unexpected pending key state");
                RejectRecovery(e.OpenExisting,"pending identity opened",false);
                RejectRecovery(delegate{return e.EnrollFirst(DeviceKeyPolicy.Software);},"pending identity replaced",false);
                await CheckBlockedProfile(owner,id,e,"pending stage "+stage);
                Check(e.FixtureSlot()==slot&&KeyExists(slot)==(stage==2),"pending key or slot changed");
            }
            finally{Cleanup(id);}
        }
        Guid corruptId=Guid.NewGuid();
        var corrupt=DeviceEnrollment.Fixture(corruptId);
        byte[] original=null;
        try
        {
            using(WindowsDeviceKey key=corrupt.EnrollFirst(DeviceKeyPolicy.Software)){}
            Guid slot=corrupt.FixtureSlot();
            original=RecordBytes(corrupt);
            SetRecord(corrupt,new byte[]{1,2,3,4});
            RejectRecovery(corrupt.OpenExisting,"corrupt identity opened",false);
            await CheckBlockedProfile(owner,corruptId,corrupt,"corrupt record");
            Check(KeyExists(slot),"corruption removed original key");
        }
        finally
        {
            if(original!=null)SetRecord(corrupt,original);
            Cleanup(corruptId);
        }
        Guid lostId=Guid.NewGuid();
        var lost=DeviceEnrollment.Fixture(lostId);
        try
        {
            using(WindowsDeviceKey key=lost.EnrollFirst(DeviceKeyPolicy.Software)){}
            Guid slot=lost.FixtureSlot();
            DeleteKey(slot);
            Check(!KeyExists(slot),"fixture key deletion failed");
            RejectRecovery(lost.OpenExisting,"missing key reopened",true);
            await CheckBlockedProfile(owner,lostId,lost,"lost key");
            Check(lost.FixtureSlot()==slot&&!KeyExists(slot),"missing key recreated");
            // Even a policy-compatible key in the original slot must not
            // be accepted when its fingerprint differs from the record.
            using(WindowsDeviceKey replacement=WindowsDeviceKey.Enroll(slot,DeviceKeyPolicy.Software)){}
            RejectRecovery(lost.OpenExisting,"replacement fingerprint admitted",true);
            await CheckBlockedProfile(owner,lostId,lost,"replaced key");
            Check(lost.FixtureSlot()==slot&&KeyExists(slot),"replacement key mutated");
        }
        finally{Cleanup(lostId);}
        Guid raceId=Guid.NewGuid();
        try
        {
            Func<bool> attempt=delegate{
                try{using(WindowsDeviceKey key=DeviceEnrollment.Fixture(raceId).EnrollFirst(DeviceKeyPolicy.Software)){}return true;}
                catch(EnrollmentRecoveryRequired){return false;}
            };
            bool[] result=await Task.WhenAll(Task.Run(attempt),Task.Run(attempt));
            Check(result[0]!=result[1],"concurrent enrollment did not produce exactly one winner");
            using(WindowsDeviceKey key=DeviceEnrollment.Fixture(raceId).OpenExisting())
                Check(key.Fingerprint.Length==32,"concurrent winner not reopenable");
        }
        finally{Cleanup(raceId);}
        Console.WriteLine("PASS enrollment fault stages 1/2, corrupt DPAPI record, missing/replaced key, concurrent enrollment; blocked desktop recovery paths");
    }
    private static void Cleanup(Guid id)
    {
        var e=DeviceEnrollment.Fixture(id);
        try
        {
            Guid slot=e.FixtureSlot();
            string name="CheZhiTech.License.v1."+slot.ToString("N");
            var provider=CngProvider.MicrosoftSoftwareKeyStorageProvider;
            if(CngKey.Exists(name,provider))
                using(CngKey key=CngKey.Open(name,provider))key.Delete();
        }
        catch(EnrollmentRecoveryRequired){}
        Registry.CurrentUser.DeleteSubKeyTree(e.FixturePath,false);
        Registry.CurrentUser.DeleteSubKeyTree(LicenseCardStore.Fixture(id).FixturePath,false);
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if(args.Length!=4)return 2;
        Guid id=Guid.NewGuid();
        try
        {
            string[] cfg=File.ReadAllLines(args[0]);
            var uri=new Uri(cfg[0]);
            if(uri.Scheme!="https"||uri.Host!="127.0.0.1")return 2;
            if(!String.Equals(Path.GetFullPath(args[1]),Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"chezhi_license_sdk.dll"),StringComparison.OrdinalIgnoreCase))return 2;
            CandidateTrust.Origin=cfg[0];CandidateTrust.Scope=Hex(cfg[1]);CandidateTrust.SigningKey=Hex(cfg[2]);
            CandidateTrust.DllHash=Hex(args[2]);CandidateTrust.CA=File.ReadAllBytes(args[3]);
            Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
            int result=1;
            using(var owner=new Form{Text="Disposable licensing integration fixture",Width=500,Height=340})
            {
                owner.Shown+=async delegate{
                    try{await Run(owner,id);await RunEnrollmentFaults(owner);result=0;}
                    catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);}
                    finally{owner.Close();}
                };
                Application.Run(owner);
            }
            if(result==0)Console.WriteLine("PASS actual desktop modal first enrollment, explicit confirmation/cancel/decline, duplicate enrollment rejection, real DLL/CNG/TLS, verified DPAPI save, invalidate, renew, denial, reload, forget and manual recovery");
            return result;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}
        finally{Cleanup(id);}
    }
}
#endif
