#if CHEZHI_CARD_TEST
using System;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;
using Chezhi.Licensing;
internal static class LicenseCardStoreSmoke
{
    private static void Check(bool b,string label){if(!b)throw new Exception(label);}
    private static void Reject(Action a)
    {bool denied=false;try{a();}catch(CryptographicException){denied=true;}catch(InvalidDataException){denied=true;}Check(denied,"invalid record accepted");}
    private static int Main()
    {
        var store=LicenseCardStore.Fixture(Guid.NewGuid());
        byte[] fp=new byte[32],scope=new byte[32],card=System.Text.Encoding.ASCII.GetBytes("CI-FIXTURE-NOT-A-REAL-CARD");
        fp[0]=1;scope[0]=2;
        try
        {
            Check(store.Load(fp,scope)==null,"missing record");
            store.Save(card,fp,scope);
            Check(Convert.ToBase64String(store.Load(fp,scope))==Convert.ToBase64String(card),"DPAPI round trip");
            byte[] other=(byte[])fp.Clone();other[0]^=1;
            Reject(delegate{store.Load(other,scope);});
            other=(byte[])scope.Clone();other[0]^=1;
            Reject(delegate{store.Load(fp,other);});
            using(RegistryKey k=Registry.CurrentUser.OpenSubKey(store.FixturePath,true))
            {
                byte[] cipher=(byte[])k.GetValue("Card");
                Check(cipher.Length>card.Length,"plaintext record");
                cipher[cipher.Length/2]^=1;k.SetValue("Card",cipher,RegistryValueKind.Binary);k.Flush();
            }
            Reject(delegate{store.Load(fp,scope);});
            store.Forget();Check(store.Load(fp,scope)==null,"forget failed");
            store.Forget();
            Console.WriteLine("PASS DPAPI card store: roundtrip, identity/scope mismatch, tamper, explicit forget");
            return 0;
        }
        finally{Registry.CurrentUser.DeleteSubKeyTree(store.FixturePath,false);Array.Clear(card,0,card.Length);}
    }
}
#endif
