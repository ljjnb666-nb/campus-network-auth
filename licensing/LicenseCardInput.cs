using System;
using System.Text;

// Same 25-character Crockford format as the issuer. Canonicalize before
// any enrollment or online exchange; reject malformed input locally.
internal static class LicenseCardInput
{
    private const string Alphabet="0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    internal static bool TryNormalize(string input,out byte[] card)
    {
        card=null;
        if(input==null||input.Length>100)return false;
        string raw=input.Trim();
        var result=new StringBuilder(29);
        int count=0;
        foreach(char original in raw)
        {
            if(original=='-')continue;
            char ch=original;
            if(ch>='a'&&ch<='z')ch=(char)(ch-32);
            if(ch=='I'||ch=='L')ch='1';
            else if(ch=='O')ch='0';
            if(Alphabet.IndexOf(ch)<0||count==25)return false;
            if(count>0&&count%5==0)result.Append('-');
            result.Append(ch);count++;
        }
        if(count!=25)return false;
        card=Encoding.ASCII.GetBytes(result.ToString());
        return true;
    }
}
