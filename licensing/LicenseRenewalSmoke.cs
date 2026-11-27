#if CHEZHI_RENEWAL_TEST
using System;
using Chezhi.Licensing;
internal static class LicenseRenewalSmoke
{
    private static void Check(bool b,string message){if(!b)throw new Exception(message);}
    private static int Main()
    {
        var schedule=new LicenseRenewalSchedule();
        Check(!schedule.Due(1000000),"startup auto renewal");
        schedule.Record(AuthorizationPhase.Authorized,1000);
        Check(!schedule.Due(120999)&&schedule.Due(121000),"success interval");
        schedule.Record(AuthorizationPhase.NetworkUnavailable,121000);
        schedule.Invalidate(122000);
        Check(!schedule.Due(181999)&&schedule.Due(182000),"network backoff");
        schedule.Record(AuthorizationPhase.RetryLater,182000);
        Check(!schedule.Due(241999)&&schedule.Due(242000),"rate backoff");
        foreach(var phase in new[]{AuthorizationPhase.Denied,AuthorizationPhase.SecurityError,AuthorizationPhase.LocalError,AuthorizationPhase.RecoveryRequired,AuthorizationPhase.Stopped})
        {
            schedule.Record(AuthorizationPhase.Authorized,1000);
            schedule.Record(phase,2000);schedule.Invalidate(3000);
            Check(!schedule.Due(Int64.MaxValue),"terminal automatic retry");
        }
        schedule.Record(AuthorizationPhase.Authorized,1000);schedule.Reset();
        Check(!schedule.Due(Int64.MaxValue),"reset");
        Console.WriteLine("PASS renewal schedule: bounded intervals, backoff, terminal states and reset");
        return 0;
    }
}
#endif
