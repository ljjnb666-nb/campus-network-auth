#if CHEZHI_JOURNAL_PROCESS_TEST
using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

// Separate executable. Only disposable journal files, no Program, SCM,
// network, elevation, production paths or credential data.
internal static class JournalProcessSmoke
{
    private static void Check(bool ok,string label)
    {if(!ok)throw new InvalidOperationException(label);}
    private static string DirectoryFor(Guid id)
    {return Path.Combine(Path.GetTempPath(),"ChezhiJournalProcess-"+id.ToString("N"));}
    private static string Journal(Guid id)
    {return Path.Combine(DirectoryFor(id),"journal.json");}
    private static string EventName(Guid id,string suffix)
    {return @"Local\ChezhiJournalProcess-"+id.ToString("N")+"-"+suffix;}
    private static Process Child(Guid id,string mode)
    {
        var p=Process.Start(new ProcessStartInfo{
            FileName=typeof(JournalProcessSmoke).Assembly.Location,
            Arguments=mode+" "+id.ToString("N"),
            UseShellExecute=false,CreateNoWindow=true});
        if(p==null)throw new IOException("Fixture child unavailable");
        return p;
    }
    private static void Stop(Process p)
    {
        if(p==null)return;
        try{
            if(!p.HasExited){p.Kill();Check(p.WaitForExit(5000),"child termination");}
        }finally{p.Dispose();}
    }
    private static int RunChild(Guid id,string mode)
    {
        string path=Journal(id);
        if(!Directory.Exists(DirectoryFor(id)))return 2;
        if(mode=="pending")
        {
            using(var ready=EventWaitHandle.OpenExisting(EventName(id,"ready")))
            {
                var j=ServiceJournal.Load(path);
                j.Begin("restore","Stopped",1000,10);
                ready.Set();
                Thread.Sleep(20000);
                return 3;
            }
        }
        if(mode=="stale")
        {
            using(var ready=EventWaitHandle.OpenExisting(EventName(id,"ready")))
            using(var proceed=EventWaitHandle.OpenExisting(EventName(id,"proceed")))
            {
                var j=ServiceJournal.Load(path);
                if(!j.HasPending||j.Invalid)return 4;
                ready.Set();
                if(!proceed.WaitOne(10000))return 5;
                try{j.Complete(10,"Running",false);}
                catch(IOException){return 0;}
                return 6;
            }
        }
        return 2;
    }
    private static void Run(Guid id)
    {
        string dir=DirectoryFor(id),path=Journal(id);
        Check(!Directory.Exists(dir),"fixture already exists");
        Directory.CreateDirectory(dir);
        Process p=null;
        try
        {
            // Simulated existing restoration responsibility; no service
            // operation is performed by any fixture process.
            var j=ServiceJournal.Load(path);
            j.Begin("prepare","Running");
            j.Complete(10,"Stopped",false);
            using(var ready=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(id,"ready")))
            using(var proceed=new EventWaitHandle(false,EventResetMode.ManualReset,EventName(id,"proceed")))
            {
                p=Child(id,"pending");
                Check(ready.WaitOne(5000),"pending commit not acknowledged");
                j=ServiceJournal.Load(path);
                Check(j.HasPending&&j.NeedsRecovery&&j.Uncertain&&!j.HasConfirmedOutcome,"pending state");
                string operation=j.OperationId;
                Stop(p);p=null;
                j=ServiceJournal.Load(path);
                Check(j.HasPending&&j.OperationId==operation&&j.NeedsRecovery&&j.Uncertain&&!j.HasConfirmedOutcome,"termination lost responsibility");
                Check(!j.CanResolveAfterRestart(2000,10,"Stopped"),"same boot accepted");
                ready.Reset();
                p=Child(id,"stale");
                Check(ready.WaitOne(5000),"stale reader not acknowledged");
                // Inject boot evidence only in this temporary file. No
                // actual reboot or live SCM observation is asserted.
                j.ResolveAfterRestart(2000,11,"Stopped");
                j.Begin("restore","Stopped",2000,11);
                string next=j.OperationId;
                Check(next!=operation,"transaction identity reused");
                proceed.Set();
                Check(p.WaitForExit(10000)&&p.ExitCode==0,"stale completion was not rejected");
                j=ServiceJournal.Load(path);
                Check(j.HasPending&&j.OperationId==next&&j.NeedsRecovery&&j.Uncertain&&!j.HasConfirmedOutcome,"stale writer changed newer transaction");
                j.Complete(null,"Stopped",false);
                j=ServiceJournal.Load(path);
                Check(j.Result=="failed:unknown"&&j.NeedsRecovery&&!j.HasConfirmedOutcome,"failure history");
            }
            Console.WriteLine("PASS journal process termination, durable pending responsibility, injected boot guard and stale cross-process completion");
        }
        finally
        {
            try{Stop(p);}finally{Directory.Delete(dir,true);}
        }
    }
    private static int Main(string[] args)
    {
        try
        {
            if(args.Length==0){Run(Guid.NewGuid());return 0;}
            Guid id;
            if(args.Length!=2||!Guid.TryParseExact(args[1],"N",out id)||id==Guid.Empty)return 2;
            return RunChild(id,args[0]);
        }
        catch(Exception e){Console.Error.WriteLine(e.GetType().Name+": "+e.Message);return 1;}
    }
}
#endif
