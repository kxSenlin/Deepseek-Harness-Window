using System.Diagnostics;
using System.Text;

namespace DshGUI.Services;

/// <summary>运行 dsh CLI 只读/管理命令，带超时和输出收集。</summary>
public sealed class DshCliResult
{
    public int ExitCode { get; init; }

    public string Output { get; init; } = "";

    public bool TimedOut { get; init; }
}

public static class DshCliService
{
    public static bool IsInstalled => DshPaths.FindDshCommand() != null;

    /// <summary>执行 dsh 命令；profile/package 参数都经过白名单校验，禁止 shell 注入。</summary>
    public static async Task<DshCliResult> RunAsync(
        IEnumerable<string> arguments, int timeoutMs, IProgress<string>? progress = null)
    {
        var command = DshPaths.FindDshCommand();
        if (command == null)
        {
            return new DshCliResult { ExitCode = 127, Output = "dsh 未安装（PATH 中找不到 dsh）" };
        }

        foreach (var argument in arguments)
        {
            if (!IsSafeArgument(argument))
            {
                return new DshCliResult { ExitCode = 2, Output = $"拒绝非法参数：{argument}" };
            }
        }

        var psi = new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
            // 参数已通过白名单校验（不含空格/引号），直接拼接；命令路径本身加引号。
            Arguments = "/c \"" + command + "\" " + string.Join(" ", arguments),
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        using var process = new Process { StartInfo = psi };
        var output = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            lock (output)
            {
                output.AppendLine(e.Data);
            }

            progress?.Report(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data == null)
                return;
            lock (output)
            {
                output.AppendLine(e.Data);
            }

            progress?.Report(e.Data);
        };

        try
        {
            if (!process.Start())
                return new DshCliResult { ExitCode = 1, Output = "无法启动 dsh 进程" };
        }
        catch (Exception ex)
        {
            return new DshCliResult { ExitCode = 1, Output = "启动 dsh 失败：" + ex.Message };
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = new CancellationTokenSource(timeoutMs);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能已退出。
            }

            lock (output)
            {
                return new DshCliResult
                {
                    ExitCode = -1,
                    TimedOut = true,
                    Output = output + "\n命令超时（" + timeoutMs + "ms），已终止。",
                };
            }
        }

        await Task.Delay(50); // 让最后一批异步读行落地。
        lock (output)
        {
            return new DshCliResult { ExitCode = process.ExitCode, Output = output.ToString() };
        }
    }

    public static Task<DshCliResult> DumpConfigAsync(string profile) =>
        RunAsync(["--profile", profile, "--dump-config"], 45_000);

    public static Task<DshCliResult> RemovePluginAsync(string profile, string packageName, IProgress<string>? progress = null) =>
        RunAsync(["plugin", "--profile", profile, "remove", packageName], 300_000, progress);

    public static Task<DshCliResult> InstallProfileAsync(string profile, IProgress<string>? progress = null) =>
        RunAsync(["plugin", "--profile", profile, "install"], 300_000, progress);

    private static bool IsSafeArgument(string argument)
    {
        if (string.IsNullOrWhiteSpace(argument))
            return false;
        return argument.All(c =>
            char.IsLetterOrDigit(c)
            || c is '-' or '_' or '.' or '@' or '/' or ':' or '#' or '~' or '^');
    }
}
