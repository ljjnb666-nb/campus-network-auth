#if CHEZHI_CARD_INPUT_TEST
using System;
using System.Text;
internal static class LicenseCardInputSmoke
{
    private static void Check(bool ok,string label){if(!ok)throw new Exception(label);}
    private static int Main()
    {
        byte[] card;
        Check(LicenseCardInput.TryNormalize("abcde-fghjk-mnpqr-stvwx-yz012",out card),"valid lowercase");
        Check(Encoding.ASCII.GetString(card)=="ABCDE-FGHJK-MNPQR-STVWX-YZ012","canonical");
        Check(LicenseCardInput.TryNormalize("ABCDE-FGHJK-MNPQR-STVWX-YZOIL",out card),"ambiguous digits");
        Check(Encoding.ASCII.GetString(card)=="ABCDE-FGHJK-MNPQR-STVWX-YZ011","mapped digits");
        foreach(string s in new[]{null,"","SHORT","ABCDE-FGHJK-MNPQR-STVWX-YZ01U","ABCDE-FGHJK-MNPQR-STVWX-YZ01中","ABCDE FGHJK-MNPQR-STVWX-YZ012",new string('A',26),new string('-',101)})
            Check(!LicenseCardInput.TryNormalize(s,out card)&&card==null,"malformed accepted");
        Console.WriteLine("PASS card input canonicalization and malformed input rejection");
        return 0;
    }
}
#endif
