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
    private ServiceJournal(string path) { filePath = path; }

    internal string Action { get; private set; }
    internal string StateBefore { get; private set; }
    internal string Result { get; private set; }
    internal string TimeUtc { get; private set; }
    internal string OperationId { get; private set; }
    internal bool NeedsRecovery { get; private set; }
    internal bool Uncertain { get; private set; }
    internal bool Invalid { get; private set; }
    private bool PriorUncertain { get; set; }
    internal bool HasPending { get { return Result == "pending"; } }

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
            if (!File.Exists(path)) return j;
            RejectReparsePoint(path);
            if (new FileInfo(path).Length > 4096) throw new InvalidDataException();
            var obj = new JavaScriptSerializer { MaxJsonLength = 4096, RecursionLimit = 4 }
                .DeserializeObject(File.ReadAllText(path, Encoding.UTF8)) as Dictionary<string, object>;
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
                if (!(obj["v"] is int) || (int)obj["v"] != 2) throw new InvalidDataException();
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
                j.OperationId = Guid.NewGuid().ToString("N");
                j.NeedsRecovery = j.Action == "prepare" && j.StateBefore == "Running" && j.Result == "ok";
                j.Uncertain = j.Result == "pending" || (j.Action == "prepare" && j.Result.StartsWith("failed:", StringComparison.Ordinal));
            }
        }
        catch
        {
            j.Invalid = true; j.Uncertain = true;
            j.Action = null; j.StateBefore = null; j.Result = null; j.TimeUtc = null;
        }
        return j;
    }

    private void Write()
    {
        if (Invalid) throw new InvalidDataException("Existing journal requires review.");
        string dir = Path.GetDirectoryName(filePath);
        Directory.CreateDirectory(dir);
        RejectReparsePoint(dir);
        if (File.Exists(filePath)) RejectReparsePoint(filePath);
        TimeUtc = DateTime.UtcNow.ToString("o");
        var json = new JavaScriptSerializer().Serialize(new
        {
            v = 2, action = Action, state = StateBefore, result = Result, time = TimeUtc,
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
    }

    // The caller records explicit user consent before invoking this method.
    internal void Begin(string action, string stateBefore)
    {
        if (Invalid || (action != "prepare" && action != "restore") || !ValidState(stateBefore)) throw new InvalidDataException();
        PriorUncertain = Uncertain;
        Action = action; StateBefore = stateBefore; Result = "pending";
        OperationId = Guid.NewGuid().ToString("N");
        // Once written, interruption is an uncertain outcome, not a no-op.
        Uncertain = true;
        Write();
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
            Result = "ok";
            if (Action == "prepare" && code == 10) NeedsRecovery = true; // elevated child actually stopped a running service
            if (actualState == "Running") { NeedsRecovery = false; Uncertain = false; }
            else Uncertain = Action == "restore" || (code == 0 && PriorUncertain) || (actualState != "Stopped" && actualState != "NotInstalled");
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

    internal void ReconcileRunning()
    {
        if (Invalid) return;
        if (!NeedsRecovery && !Uncertain && !HasPending) return;
        NeedsRecovery = false; Uncertain = false; Result = "ok"; Write();
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
            File.WriteAllText(path, "{broken");
            return Load(path).Invalid;
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
