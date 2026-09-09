using System.Text;

namespace DshGUI.Services;

/// <summary>
/// DshGUI 落盘日志的统一入口（dsh.log / app.log）。
///
/// 截断规则（每个文件、每次运行独立计算）：
/// - 会话内**首次写入**该文件时，若它已有内容超过 2 MB，则清空再写；本次运行从没写过就不动它。
/// - 同一会话内累计写入超过 10 MB 时清空覆盖，计数归零后继续追加。
///
/// 所有文件 IO 都吞掉异常：写日志失败绝不影响运行。写入用一把锁串行化（dsh 的输出来自后台回调线程）。
/// </summary>
public static class DiagnosticLog
{
    /// <summary>会话首次写入时的清空阈值：已有文件超过该大小才清空。</summary>
    private const long SessionStartThresholdBytes = 2L * 1024 * 1024;

    /// <summary>同一会话内累计写入上限：超过则清空覆盖。</summary>
    private const long SessionMaxBytes = 10L * 1024 * 1024;

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshGUI");

    private static readonly object Gate = new();

    /// <summary>每个日志文件的会话状态（是否已做首次基线检查、本会话已写入字节数）。</summary>
    private static readonly Dictionary<string, LogFileState> States = new(StringComparer.OrdinalIgnoreCase);

    public static string DshLogPath => Path.Combine(LogDirectory, "dsh.log");

    public static string AppLogPath => Path.Combine(LogDirectory, "app.log");

    /// <summary>写 dsh 子进程的原始输出（stdout/stderr）到 dsh.log。</summary>
    public static void Dsh(string? line) => Write(DshLogPath, line);

    /// <summary>写 DshGUI 自身诊断信息到 app.log，用分类前缀区分来源（如 webview / plugin）。</summary>
    public static void App(string category, string? message) =>
        Write(AppLogPath, string.IsNullOrEmpty(message) ? message : $"[{category}] {message}");

    private static void Write(string path, string? message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}";
            var bytes = Encoding.UTF8.GetByteCount(line);

            lock (Gate)
            {
                var state = GetState(path);

                // 会话内累计超限：清空覆盖，计数归零。
                if (state.SessionBytes + bytes > SessionMaxBytes)
                {
                    Truncate(path);
                    state.SessionBytes = 0;
                }

                File.AppendAllText(path, line);
                state.SessionBytes += bytes;
            }
        }
        catch
        {
            // 写日志失败不影响运行。
        }
    }

    /// <summary>取文件的会话状态；首次调用时做一次「已有文件是否超过 2 MB」的基线检查。</summary>
    private static LogFileState GetState(string path)
    {
        if (States.TryGetValue(path, out var existing))
            return existing;

        // 目录只需在每个文件的会话首次写入时确保一次。
        Directory.CreateDirectory(LogDirectory);

        // 惰性触发：只有真正要写这个文件时才检查，且超过阈值才清空；不写就不动它。
        if (File.Exists(path) && new FileInfo(path).Length > SessionStartThresholdBytes)
            Truncate(path);

        var state = new LogFileState();
        States[path] = state;
        return state;
    }

    /// <summary>把日志文件截断为 0（不存在则创建），不长时间持有句柄。</summary>
    private static void Truncate(string path)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        // FileMode.Create 会截断已有内容；不写任何字节。
    }

    private sealed class LogFileState
    {
        public long SessionBytes;
    }
}
