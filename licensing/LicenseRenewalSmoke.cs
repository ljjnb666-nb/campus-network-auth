#if CHEZHI_RENEWAL_TEST
using System;
using Chezhi.Licensing;
internal static class LicenseRenewalSmoke
{
    private static void Check(bool b,string message){if(!b)throw new Exception(message);}
    private sealed class TestClock : IAuthorizationClock
    {
        internal long Utc=1000, Mono=100;
        public long UnixSeconds {get{return Utc;}}
        public long MonotonicMilliseconds {get{return Mono;}}
    }
    private static void Grant(AuthorizationState state,TestClock clock,ulong revision)
    {
        var ticket=state.TryBegin(LicenseOperation.Validate);
        Check(ticket!=null,"verification not admitted");
        Check(state.Complete(ticket,LicenseStatus.Ok,new LicenseMetadata(revision,clock.Utc,clock.Utc+300,0)),
            "valid metadata rejected");
        Check(state.CanStartProtectedOperation,"valid grant not admitted");
    }
    private static void CheckCredentialLifecycle()
    {
        var clock=new TestClock();
        var state=new AuthorizationState(clock);
        Grant(state,clock,7);
        clock.Mono+=299999;
        Check(state.CanStartProtectedOperation,"premature monotonic expiry");
        clock.Mono++;
        Check(!state.CanStartProtectedOperation&&state.Phase==AuthorizationPhase.Expired,"monotonic expiry missed");
        Grant(state,clock,7);
        clock.Utc+=300;
        Check(!state.CanStartProtectedOperation&&state.Phase==AuthorizationPhase.Expired,"UTC expiry missed");
        Grant(state,clock,7);
        clock.Utc--;
        Check(!state.CanStartProtectedOperation&&state.Phase==AuthorizationPhase.Expired,"UTC rollback extended grant");
        Grant(state,clock,8);
        var stale=state.TryBegin(LicenseOperation.Renew);
        Check(stale!=null&&!state.CanStartProtectedOperation,"renew retained old grant");
        Check(!state.Complete(stale,LicenseStatus.Ok,new LicenseMetadata(7,clock.Utc,clock.Utc+300,0))&&
            state.Phase==AuthorizationPhase.SecurityError,"revision rollback admitted");
        Grant(state,clock,8);
        var denied=state.TryBegin(LicenseOperation.Renew);
        Check(!state.Complete(denied,LicenseStatus.Denied,null)&&!state.CanStartProtectedOperation&&
            state.Phase==AuthorizationPhase.Denied,"server denial retained grant");
        state.NetworkChanged(false);state.NetworkChanged(true);
        Check(state.Phase==AuthorizationPhase.Denied&&!state.CanStartProtectedOperation,"network reset denial");
        Grant(state,clock,8);
        var unavailable=state.TryBegin(LicenseOperation.Renew);
        Check(!state.Complete(unavailable,LicenseStatus.Unavailable,null)&&!state.CanStartProtectedOperation,
            "network failure retained grant");
        Check(state.TryBegin(LicenseOperation.Validate)==null,"network backoff skipped");
        clock.Mono+=5000;
        var throttled=state.TryBegin(LicenseOperation.Validate);
        Check(throttled!=null&&!state.Complete(throttled,LicenseStatus.RetryLater,null),"rate failure admitted");
        clock.Mono+=59999;
        Check(state.TryBegin(LicenseOperation.Validate)==null,"rate backoff skipped");
        clock.Mono++;
        Grant(state,clock,8);
        clock.Mono--;
        Check(!state.CanStartProtectedOperation&&state.Phase==AuthorizationPhase.SecurityError&&
            state.TryBegin(LicenseOperation.Validate)==null,"monotonic rollback not terminal");
        Console.WriteLine("PASS credential expiry, UTC/monotonic rollback, revision, denial, network and rate backoff");
    }
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
        schedule.Record(AuthorizationPhase.Authorized,1000);
        schedule.Invalidate(120000);
        for(long now=121000;now<=180000;now+=1000)schedule.Invalidate(now);
        Check(!schedule.Due(179999)&&schedule.Due(180000),"repeated invalidation postponed renewal");
        schedule.Record(AuthorizationPhase.Authorized,180000);
        schedule.Invalidate(299000);
        Check(!schedule.Due(358999)&&schedule.Due(359000),"next interval cannot defer");
        CheckCredentialLifecycle();
        schedule.Record(AuthorizationPhase.Authorized,1000);schedule.Reset();
        Check(!schedule.Due(Int64.MaxValue),"reset");
        Console.WriteLine("PASS renewal schedule: bounded intervals, backoff, terminal states and reset");
        return 0;
    }
}
#endif
