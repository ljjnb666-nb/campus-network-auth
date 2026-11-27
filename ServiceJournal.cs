using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;

// Persist recovery responsibility separately from the outcome of the last action.
// A pending operation is uncertain, never proof that a service was changed.
internal sealed class ServiceJournal
{
    private readonly string filePath;
    private string expectedSnapshot;
    private ServiceJournal(string path) { filePath = path; }

    internal string Action { get; private set; }
    internal string StateBefore { get; private set; }
    internal string Result { get; private set; }
    internal string TimeUtc { get; private set; }
    internal string OperationId { get; private set; }
    internal long BootTicks { get; private set; }
    internal long BootSequence { get; private set; }
    internal bool NeedsRecovery { get; private set; }
    internal bool Uncertain { get; private set; }
    internal bool Invalid { get; private set; }
    private bool PriorUncertain { get; set; }
    internal bool HasPending { get { return Result == "pending"; } }
    internal bool HasConfirmedOutcome { get { return !Invalid && Result == "ok" && !Uncertain; } }

    internal static string DefaultPath()
    {
        return Path.Combine(CredentialStore.DefaultDirectory(), "service-journal.v1.json");
    }

    private static bool ValidState(string value)
    {
        return value == "Running" || value == "Stopped" || value == "NotInstalled" ||
            value == "StartPending" || value == "StopPending" || value == "ContinuePending" ||
            value == "PausePending" || value == "Paused" || value == "unknown";
    }
    private static bool ValidFailure(string value)
    {
        if (value == "failed:unknown") return true;
        int code;
        return value.StartsWith("failed:", StringComparison.Ordinal) && Int32.TryParse(value.Substring(7), out code);
    }
    private static string StringField(Dictionary<string, object> obj, string key)
    {
        object value;
        return obj.TryGetValue(key, out value) ? value as string : null;
    }
    private static bool BoolField(Dictionary<string, object> obj, string key)
    {
        object value;
        if (!obj.TryGetValue(key, out value) || !(value is bool)) throw new InvalidDataException();
        return (bool)value;
    }
    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Unexpected reparse point.");
    }
    internal static ServiceJournal Load(string path)
    {
        var j = new ServiceJournal(path);
        try
        {
            byte[] bytes = CredentialStore.ReadBoundedRecord(path, 4096);
            if (bytes == null) { j.expectedSnapshot = j.Snapshot(); return j; }
            // Preserve compatibility with legacy UTF-8 BOM files; reject invalid UTF-8.
            string json = new UTF8Encoding(false, true).GetString(bytes);
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            var obj = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 4 }
                .DeserializeObject(json) as Dictionary<string, object>;
            if (obj == null) throw new InvalidDataException();
            j.Action = StringField(obj, "action");
            j.StateBefore = StringField(obj, "state");
            j.Result = StringField(obj, "result");
            j.TimeUtc = StringField(obj, "time");
            if ((j.Action != "prepare" && j.Action != "restore") || !ValidState(j.StateBefore) ||
                String.IsNullOrEmpty(j.Result) || !(j.Result == "pending" || j.Result == "ok" ||
                j.Result == "cancelled" || ValidFailure(j.Result))) throw new InvalidDataException();
            if (obj.ContainsKey("v"))
            {
                if (!(obj["v"] is int) || ((int)obj["v"] != 2 && (int)obj["v"] != 3 && (int)obj["v"] != 4)) throw new InvalidDataException();
                if ((int)obj["v"] >= 3)
                {
                    long boot;
                    if (!Int64.TryParse(StringField(obj, "bootTicks"), out boot) || boot < 0 || boot > DateTime.MaxValue.Ticks) throw new InvalidDataException();
                    j.BootTicks = boot;
                }
                if ((int)obj["v"] == 4)
                {
                    long sequence;
                    if (!Int64.TryParse(StringField(obj, "bootSequence"), out sequence) ||
                        sequence < 0 || sequence > UInt32.MaxValue) throw new InvalidDataException();
                    j.BootSequence = sequence;
                }
                j.OperationId = StringField(obj, "operationId");
                Guid id;
                if (!Guid.TryParse(j.OperationId, out id)) throw new InvalidDataException();
                j.NeedsRecovery = BoolField(obj, "needsRecovery");
                j.Uncertain = BoolField(obj, "uncertain");
                j.PriorUncertain = obj.ContainsKey("priorUncertain") && BoolField(obj, "priorUncertain");
            }
            else
            {
                // Migrate legacy journals conservatively: successful prepare with a
                // previously running service is a possible outstanding restoration.
                j.OperationId = null; // legacy snapshot must be stable across independent readers
                j.NeedsRecovery = j.Action == "prepare" && j.StateBefore == "Running" && j.Result == "ok";
                j.Uncertain = j.Result == "pending" || (j.Action == "prepare" && j.Result.StartsWith("failed:", StringComparison.Ordinal));
            }
        }
        catch
        {
            j.Invalid = true; j.Uncertain = true;
            j.Action = null; j.StateBefore = null; j.Result = null; j.TimeUtc = null;
        }
        j.expectedSnapshot = j.Snapshot();
        return j;
    }

    private string Snapshot()
    {
        return new JavaScriptSerializer().Serialize(new { Action, StateBefore, Result, TimeUtc, OperationId, BootTicks, BootSequence, NeedsRecovery, Uncertain, PriorUncertain });
    }

    private void Write()
    {
        if (Invalid) throw new InvalidDataException("Existing journal requires review.");
        string dir = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(dir);
        RejectReparsePoint(dir);
        string lockPath = filePath + ".lock";
        if (File.Exists(lockPath)) RejectReparsePoint(lockPath);
        // All cooperating writers serialize the compare-and-replace operation.
        using (var writerLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
        var latest = Load(filePath);
        if (latest.Invalid || !String.Equals(latest.expectedSnapshot, expectedSnapshot, StringComparison.Ordinal))
            throw new IOException("Service journal changed since it was read.");
        if (File.Exists(filePath)) RejectReparsePoint(filePath);
        if (OperationId == null) OperationId = Guid.NewGuid().ToString("N");
        TimeUtc = DateTime.UtcNow.ToString("o");
        var json = new JavaScriptSerializer().Serialize(new
        {
            v = 4, action = Action, state = StateBefore, result = Result, time = TimeUtc,
            bootTicks = BootTicks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            bootSequence = BootSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            operationId = OperationId, needsRecovery = NeedsRecovery, uncertain = Uncertain, priorUncertain = PriorUncertain
        });
        string temp = filePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes, 0, bytes.Length); output.Flush(true);
            }
            if (File.Exists(filePath)) File.Replace(temp, filePath, null);
            else File.Move(temp, filePath);
        }
        finally { if (File.Exists(temp)) try { File.Delete(temp); } catch { } }
        expectedSnapshot = Snapshot();
        }
    }

    // The caller records explicit user consent before invoking this method.
    internal void Begin(string action, string stateBefore, long bootTicks = 0, long bootSequence = 0)
    {
        if (bootSequence < 0 || bootSequence > UInt32.MaxValue || bootTicks < 0 || bootTicks > DateTime.MaxValue.Ticks || Invalid || HasPending || (action != "prepare" && action != "restore") || !ValidState(stateBefore)) throw new InvalidDataException();
        string oldAction = Action, oldState = StateBefore, oldResult = Result, oldId = OperationId, oldTime = TimeUtc;
        bool oldPrior = PriorUncertain, oldUncertain = Uncertain;
        long oldBoot = BootTicks, oldSequence = BootSequence;
        try
        {
        PriorUncertain = Uncertain;
        Action = action; StateBefore = stateBefore; Result = "pending"; BootTicks = bootTicks; BootSequence = bootSequence;
        OperationId = Guid.NewGuid().ToString("N");
        // Once written, interruption is an uncertain outcome, not a no-op.
        Uncertain = true;
        Write();
        }
        catch
        {
            Action = oldAction; StateBefore = oldState; Result = oldResult; OperationId = oldId; TimeUtc = oldTime;
            PriorUncertain = oldPrior; Uncertain = oldUncertain; BootTicks = oldBoot; BootSequence = oldSequence; throw;
        }
    }

    internal void Complete(int? code, string actualState, bool cancelled)
    {
        if (Invalid || !HasPending || !ValidState(actualState)) throw new InvalidDataException();
        string oldResult = Result, oldTime = TimeUtc;
        bool oldRecovery = NeedsRecovery, oldUncertain = Uncertain;
        try
        {
        if (cancelled)
        {
            Result = "cancelled";
            // No elevated child was started. Preserve previous confirmed responsibility;
            // a prior uncertain outcome must not be silently discarded.
            Uncertain = actualState != "Running" && PriorUncertain;
        }
        else if (code == 0 || code == 10)
        {
            bool targetReached = Action == "prepare" ? actualState == "Stopped" || actualState == "NotInstalled" :
                actualState == "Running";
            // Code 10 describes an actual service change, not removal.
            // A no-op cannot establish who stopped a previously running service.
            bool contradictory = !targetReached || (code == 10 && actualState == "NotInstalled") ||
                (Action == "prepare" && code == 0 && StateBefore == "Running");
            if (contradictory)
            {
                Result = "failed:unknown";
                Uncertain = true;
                if (actualState == "Running") NeedsRecovery = false;
                else if (Action == "prepare" && (StateBefore == "Running" || code == 10)) NeedsRecovery = true;
            }
            else
            {
                Result = "ok";
                if (Action == "prepare" && code == 10) NeedsRecovery = true;
                if (actualState == "Running") { NeedsRecovery = false; Uncertain = false; }
                else Uncertain = code == 0 && PriorUncertain;
            }
        }
        else
        {
            Result = "failed:" + (code.HasValue ? code.Value.ToString() : "unknown");
            Uncertain = actualState != "Running";
            if (actualState == "Running") NeedsRecovery = false;
        }
        Write();
        }
        catch
        {
            Result = oldResult; TimeUtc = oldTime; NeedsRecovery = oldRecovery; Uncertain = oldUncertain;
            throw;
        }
    }

    internal bool CanResolveAfterRestart(long currentBoot, long currentSequence, string actualState)
    {
        // A wall-clock change alone is never evidence of a new boot. A sequence
        // reset or wrap is ambiguous and is deliberately not accepted.
        return !Invalid && HasPending && BootTicks > 0 && currentBoot > BootTicks &&
            currentBoot <= DateTime.MaxValue.Ticks && BootSequence > 0 &&
            currentSequence > BootSequence && currentSequence <= UInt32.MaxValue &&
            (actualState == "Running" || actualState == "Stopped");
    }

    // Explicitly reconcile an interrupted attempt, not a successful operation.
    internal void ResolveAfterRestart(long currentBoot, long currentSequence, string actualState)
    {
        if (!CanResolveAfterRestart(currentBoot, currentSequence, actualState)) throw new InvalidDataException();
        string oldResult = Result, oldTime = TimeUtc;
        bool oldRecovery = NeedsRecovery, oldUncertain = Uncertain;
        try
        {
            Result = "failed:unknown";
            NeedsRecovery = actualState == "Stopped" && (NeedsRecovery || StateBefore == "Running" || PriorUncertain);
            Uncertain = true; // outcome of original helper is still unknown
            Write();
        }
        catch { Result = oldResult; TimeUtc = oldTime; NeedsRecovery = oldRecovery; Uncertain = oldUncertain; throw; }
    }

    internal void ReconcileRunning()
    {
        if (Invalid || HasPending) return; // observation alone cannot resolve an unfinished helper
        if (!NeedsRecovery && !Uncertain) return;
        bool oldRecovery = NeedsRecovery, oldUncertain = Uncertain;
        string oldResult = Result, oldTime = TimeUtc, oldId = OperationId;
        // Current running state settles restoration responsibility, not the
        // historical helper outcome. Never rewrite a failed/cancelled result.
        try { NeedsRecovery = false; Uncertain = false; Write(); }
        catch { NeedsRecovery = oldRecovery; Uncertain = oldUncertain; Result = oldResult; TimeUtc = oldTime; OperationId = oldId; throw; }
    }

    private static bool OutcomeSelfTest(string dir)
    {
        // A separate file for each case prevents earlier recovery flags from
        // masking a false-positive result.
        int cases = 0;
        foreach (string action in new[] { "prepare", "restore" })
        foreach (string before in new[] { "Running", "Stopped" })
        foreach (int code in new[] { 0, 10 })
        foreach (string actual in new[] { "Running", "Stopped", "NotInstalled", "unknown" })
        {
            string path = Path.Combine(dir, "outcome-" + cases + ".json");
            var record = Load(path);
            record.Begin(action, before);
            record.Complete(code, actual, false);
            record = Load(path);
            bool expected = action == "restore" ? actual == "Running" :
                (actual == "Stopped" || (actual == "NotInstalled" && code == 0)) &&
                !(before == "Running" && code == 0);
            if (record.HasPending || record.Invalid || record.HasConfirmedOutcome != expected ||
                (expected ? record.Result != "ok" : record.Result != "failed:unknown") ||
                (!expected && !record.Uncertain))
            {
                Console.WriteLine("JOURNAL_OUTCOME_FAIL action=" + action + " before=" + before +
                    " code=" + code + " actual=" + actual + " result=" + record.Result);
                return false;
            }
            if (!expected)
            {
                record.ReconcileRunning();
                record = Load(path);
                if (record.Result != "failed:unknown" || record.HasConfirmedOutcome ||
                    record.NeedsRecovery || record.Uncertain) return false;
            }
            cases++;
        }
        string failurePath = Path.Combine(dir, "failed-outcome.json");
        var failure = Load(failurePath);
        failure.Begin("prepare", "Running");
        failure.Complete(25, "Stopped", false);
        failure.ReconcileRunning();
        if (Load(failurePath).Result != "failed:25" || Load(failurePath).HasConfirmedOutcome) return false;
        Console.WriteLine("JOURNAL_OUTCOME_PASS cases=" + cases + " failure_history=1");
        return true;
    }

    internal static bool SelfTest()
    {
        string dir = Path.Combine(Path.GetTempPath(), "ChezhiJournalTest-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(dir, "journal.json");
        try
        {
            var j = Load(path); j.Begin("prepare", "Running");
            j = Load(path); if (!j.HasPending || !j.Uncertain) return false;
            // Hold the destination without delete sharing: atomic replacement must fail.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool failed = false;
                try { j.Complete(10, "Stopped", false); } catch (IOException) { failed = true; } catch (UnauthorizedAccessException) { failed = true; }
                if (!failed || !j.HasPending || !j.Uncertain || j.NeedsRecovery) return false;
            }
            if (!Load(path).HasPending) return false;
            j.Complete(10, "Stopped", false);
            Console.WriteLine("JOURNAL_RETRY_PASS");
            j = Load(path); if (!j.NeedsRecovery || j.HasPending) return false;
            j.Begin("restore", "Stopped"); j.Complete(null, "Stopped", true);
            j = Load(path); if (!j.NeedsRecovery) return false;
            j.Begin("restore", "Stopped"); j.Complete(10, "Running", false);
            j = Load(path); if (j.NeedsRecovery || j.Uncertain) return false;
            j.Begin("prepare", "Stopped"); j.Complete(0, "Stopped", false);
            j = Load(path); if (j.NeedsRecovery || j.Uncertain) return false;
            var stale = Load(path);
            j.Begin("prepare", "Running");
            bool rejected = false;
            try { stale.Begin("restore", "Stopped"); } catch (IOException) { rejected = true; }
            if (!rejected || stale.HasPending || !Load(path).HasPending) return false;
            var pending = Load(path); pending.ReconcileRunning();
            if (!pending.HasPending || !Load(path).HasPending) return false;
            rejected = false;
            try { pending.Begin("restore", "Stopped"); } catch (InvalidDataException) { rejected = true; }
            if (!rejected) return false;
            j.Complete(10, "Stopped", false);
            rejected = false;
            try { pending.Complete(0, "Running", false); } catch (IOException) { rejected = true; }
            if (!rejected || !pending.HasPending || !Load(path).NeedsRecovery) return false;
            Console.WriteLine("JOURNAL_CONCURRENCY_PASS");
            j.Begin("restore", "Stopped", 1000, 10);
            j = Load(path);
            if (j.BootTicks != 1000 || j.BootSequence != 10 ||
                j.CanResolveAfterRestart(1000, 11, "Running") || j.CanResolveAfterRestart(999, 11, "Stopped") ||
                j.CanResolveAfterRestart(2000, 10, "Running") || j.CanResolveAfterRestart(2000, 9, "Stopped") ||
                j.CanResolveAfterRestart(2000, 0, "Stopped") || j.CanResolveAfterRestart(2000, 4294967296L, "Stopped") ||
                j.CanResolveAfterRestart(2000, 11, "StopPending") || j.CanResolveAfterRestart(2000, 11, "unknown")) return false;
            var oldPending = Load(path);
            j.ResolveAfterRestart(2000, 11, "Stopped");
            j = Load(path);
            if (j.HasPending || !j.NeedsRecovery || !j.Uncertain || j.Result != "failed:unknown") return false;
            rejected = false;
            try { oldPending.ResolveAfterRestart(2000, 11, "Running"); } catch (IOException) { rejected = true; }
            if (!rejected || !Load(path).NeedsRecovery) return false;
            j.Begin("restore", "Stopped", 1000);
            if (Load(path).CanResolveAfterRestart(2000, 11, "Running")) return false; // missing sequence
            // Simulate an actual old v3 pending record, not just a v4 zero.
            var legacy = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            legacy["v"] = 3; legacy.Remove("bootSequence");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(legacy));
            var old = Load(path);
            if (old.Invalid || !old.HasPending || old.BootTicks != 1000 || old.BootSequence != 0 ||
                old.CanResolveAfterRestart(2000, 11, "Running")) return false;
            Console.WriteLine("JOURNAL_BOOT_SEQUENCE_PASS");
            Console.WriteLine("JOURNAL_RESTART_PASS");
            File.WriteAllText(path, "{broken");
            if (!Load(path).Invalid) return false;
            File.WriteAllBytes(path, new byte[4097]);
            if (!Load(path).Invalid) return false;
            File.WriteAllBytes(path, new byte[] { 0xFF, 0xFE, 0xFF });
            if (!Load(path).Invalid) return false;
            // Existing but unreadable must never become a missing/clean journal.
            using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                if (!Load(path).Invalid) return false;
            File.Delete(path);
            if (Load(path).Invalid || Load(path).HasPending) return false;
            var missing = Load(path);
            missing.Begin("prepare", "Running");
            if (!Load(path).HasPending) return false;
            Console.WriteLine("JOURNAL_BOUNDED_READ_PASS");
            return OutcomeSelfTest(dir);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
