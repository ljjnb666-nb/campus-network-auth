using System;
using System.IO;
using System.Text;

// 服务操作事务记录（A09）：每次服务切换（准备连接=停用天翼服务 / 恢复天翼）由
// GUI 在提权子进程启动前写入 pending 记录，子进程退出后回写结果。若程序中途
// 异常退出，记录保持 pending——下次启动 GUI 识别并提示用户"经授权的恢复选项"。
// 记录仅含服务状态与结果码，不含任何凭据。
internal sealed class ServiceJournal
{
    private readonly string filePath;

    private ServiceJournal(string path) { filePath = path; }

    internal string Action { get; private set; }        // "prepare" / "restore"
    internal string StateBefore { get; private set; }   // 操作前服务状态
    internal string Result { get; private set; }        // "pending" / "ok" / "failed:<code>"
    internal string TimeUtc { get; private set; }

    internal static string DefaultPath()
    {
        return Path.Combine(CredentialStore.DefaultDirectory(), "service-journal.v1.json");
    }

    internal static ServiceJournal Load(string path)
    {
        var j = new ServiceJournal(path);
        try
        {
            if (File.Exists(path))
            {
                string raw = File.ReadAllText(path, Encoding.UTF8);
                j.Action = Field(raw, "action");
                j.StateBefore = Field(raw, "state");
                j.Result = Field(raw, "result");
                j.TimeUtc = Field(raw, "time");
            }
        }
        catch { j.Result = null; } // 读取失败按"无记录"处理（fail-open 提示层面，操作层面仍 fail-closed）
        return j;
    }

    // 字段值提取：格式固定为 {"action":"prepare",...}，值不含引号与转义（枚举/时间戳）
    private static string Field(string json, string name)
    {
        string marker = "\"" + name + "\":\"";
        int start = json.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;
        start += marker.Length;
        int end = json.IndexOf('"', start);
        if (end < 0) return null;
        return json.Substring(start, end - start);
    }

    private void Write(string action, string state, string result)
    {
        Action = action; StateBefore = state; Result = result;
        TimeUtc = DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'");
        string dir = Path.GetDirectoryName(filePath);
        if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        string json = "{\"action\":\"" + action + "\",\"state\":\"" + state +
            "\",\"result\":\"" + result + "\",\"time\":\"" + TimeUtc + "\"}";
        string temp = filePath + ".tmp";
        File.WriteAllText(temp, json, Encoding.UTF8);
        if (File.Exists(filePath)) File.Replace(temp, filePath, null);
        else File.Move(temp, filePath);
    }

    internal bool HasPending { get { return Result == "pending" && (Action == "prepare" || Action == "restore"); } }

    /// <summary>提权子进程启动前：写入 pending 记录。</summary>
    internal void Begin(string action, string stateBefore) { Write(action, stateBefore, "pending"); }

    /// <summary>子进程退出后：回写结果。code=0 成功；非 0 为子进程退出码；null=未知。</summary>
    internal void Complete(int? code) { Write(Action ?? "?", StateBefore ?? "?", code == null ? "failed:unknown" : (code == 0 ? "ok" : "failed:" + code)); }

    /// <summary>清除记录（用户明确放弃恢复，或已恢复完成）。</summary>
    internal void Clear()
    {
        try { if (File.Exists(filePath)) File.Delete(filePath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        Result = null; Action = null;
    }
}
