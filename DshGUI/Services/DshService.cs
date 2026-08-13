using System.Diagnostics;
using System.Net.Http;

namespace DshGUI.Services;

public sealed class DshService : IDisposable
{
    public const string Url = "http://127.0.0.1:3080";

    private static readonly Uri DshUri = new(Url + "/");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshGUI");

    public static string LogPath => Path.Combine(LogDirectory, "dsh.log");

    private Process? _process;

    public bool HasExited => _process is { HasExited: true };

    public static bool IsInstalled() => FindOnPath("dsh") != null;

    public async Task<bool> IsServerUpAsync()
    {
        try
        {
            using var _ = await Http.GetAsync(DshUri);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> InstallAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c npm install -g @deepseek-ai/dsh",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, a) => AppendLog(a.Data);
        process.ErrorDataReceived += (_, a) => AppendLog(a.Data);

        try
        {
            process.Start();
        }
        catch
        {
            return false;
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync();
        return process.ExitCode == 0 && IsInstalled();
    }

    public bool Start(string workspaceDirectory)
    {
        var dsh = FindOnPath("dsh");
        var commandLine = dsh != null ? $"\"{dsh}\" web" : "npx @deepseek-ai/dsh web";

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c " + commandLine,
            WorkingDirectory = workspaceDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            var process = new Process { StartInfo = psi };
            process.OutputDataReceived += (_, a) => AppendLog(a.Data);
            process.ErrorDataReceived += (_, a) => AppendLog(a.Data);
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            return true;
        }
        catch (Exception ex)
        {
            AppendLog("启动失败: " + ex);
            return false;
        }
    }

    public void Stop()
    {
        if (_process is { HasExited: false })
        {
            try
            {
                _process.Kill(entireProcessTree: true);
            }
            catch
            {
                // 进程可能刚好退出，忽略。
            }
        }

        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Stop();

    private static string? FindOnPath(string name)
    {
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        var extensions = new[] { ".cmd", ".bat", ".exe" };
        foreach (var dir in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(dir, name + extension);
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }

    private static void AppendLog(string? line)
    {
        if (line == null)
            return;

        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.AppendAllText(
                LogPath,
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
        }
        catch
        {
            // 日志写失败不影响运行。
        }
    }
}
