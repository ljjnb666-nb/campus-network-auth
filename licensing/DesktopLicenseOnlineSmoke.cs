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
    private static async Task Run(Form owner,Guid id)
    {
        var enrollment=DeviceEnrollment.Fixture(id);
        var store=LicenseCardStore.Fixture(id);
        byte[] fp;
        // First enrollment is a real CNG/DPAPI operation in an isolated
        // profile; the actual modal dialog then reopens the enrolled key.
        using(WindowsDeviceKey key=enrollment.EnrollFirst(DeviceKeyPolicy.Software))fp=key.Fingerprint;
        using(var gate=new DesktopLicenseGate(id))
        {
            Check(!gate.Allowed,"initial grant");
            Check(!await gate.VerifyAsync(owner,false),"missing card admitted");
            gate.FixtureCard=FixtureCard;gate.FixtureRemember=true;
            Check(await gate.VerifyAsync(owner,true)&&gate.Allowed&&gate.FixtureDialogCount==1,"dialog activation");
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
        }
        using(WindowsDeviceKey key=enrollment.OpenExisting())
            Check(Convert.ToBase64String(key.Fingerprint)==Convert.ToBase64String(fp),"identity changed");
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
                    try{await Run(owner,id);result=0;}
                    catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);}
                    finally{owner.Close();}
                };
                Application.Run(owner);
            }
            if(result==0)Console.WriteLine("PASS actual desktop modal activation, real DLL/CNG/TLS, verified DPAPI save, cancel, invalidate, renew, denial, reload and forget");
            return result;
        }
        catch(Exception ex){Console.Error.WriteLine(ex.GetType().Name+": "+ex.Message);return 1;}
        finally{Cleanup(id);}
    }
}
#endif
