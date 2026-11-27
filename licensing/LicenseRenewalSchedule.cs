using System;
using Chezhi.Licensing;

// UI-independent scheduling policy. Never grants authorization.
internal sealed class LicenseRenewalSchedule
{
    private long due;
    private bool armed;
    internal bool Due(long now){return armed && now>=due;}
    internal void Reset(){armed=false;due=0;}
    internal void Record(AuthorizationPhase phase,long now)
    {
        long delay;
        switch(phase)
        {
            case AuthorizationPhase.Authorized:delay=120000;break;
            case AuthorizationPhase.NetworkUnavailable:
            case AuthorizationPhase.RetryLater:
            case AuthorizationPhase.NeedsVerification:
            case AuthorizationPhase.Expired:delay=60000;break;
            default:Reset();return;
        }
        due=now>Int64.MaxValue-delay?Int64.MaxValue:now+delay;
        armed=true;
    }
    // Invalidation requires fresh proof, not necessarily an immediate network
    // request on every interface event. Preserve any longer retry backoff.
    internal void Invalidate(long now)
    {
        if(!armed)return;
        long earliest=now>Int64.MaxValue-60000?Int64.MaxValue:now+60000;
        if(due<earliest)due=earliest;
    }
}
