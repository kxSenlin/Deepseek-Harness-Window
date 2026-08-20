using System.Diagnostics;
using System.Net.Http;

namespace DshGUI.Services;

public sealed class DshService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DshGUI");

    public static string LogPath => Path.Combine(LogDirectory, "dsh.log");

    private Process? _process;
    private Uri _dshUri = new("http://127.0.0.1:3080/");

    public DshService(int port = 3080)
    {
        SetPort(port);
    }

    /// <summary>dsh 使用的端口（可在设置中修改）。</summary>
    public int Port { get; private set; }

    public string Url => $"http://127.0.0.1:{Port}";

    private static readonly HashSet<int> BlockedServicePorts =
    [
        1080,   // SOCKS 代理
        1433,   // SQL Server
        1521,   // Oracle
        3128,   // 常见 HTTP 代理
        3306,   // MySQL
        3389,   // RDP 远程桌面
        5432,   // PostgreSQL
        5900,   // VNC
        5985,   // WinRM HTTP
        5986,   // WinRM HTTPS
        6379,   // Redis
        27017,  // MongoDB
    ];

    /// <summary>校验端口是否允许：1-65535，且避开系统保留端口与常见服务/危险端口。</summary>
    public static string? GetPortError(int port)
    {
        if (port is < 1 or > 65535)
            return "端口必须是 1-65535 之间的数字。";
        if (port < 1024)
            return $"端口 {port} 是系统保留端口（1-1023），请使用 1024 以上的端口。";
        if (BlockedServicePorts.Contains(port))
            return $"端口 {port} 是系统常用服务端口，可能被其他服务占用或存在安全风险，请更换端口。";
        return null;
    }

    /// <summary>修改监听端口；仅影响后续检测与启动，不杀死现有进程。</summary>
    public void SetPort(int port)
    {
        var error = GetPortError(port);
        if (error != null)
            throw new ArgumentOutOfRangeException(nameof(port), error);
        Port = port;
        _dshUri = new Uri(Url + "/");
    }

    public bool HasExited => _process is { HasExited: true };

    /// <summary>当前进程是否由 DshGUI 启动且仍在运行。</summary>
    public bool IsManagedProcessRunning => _process is { HasExited: false };

    public static bool IsInstalled() => DshPaths.FindOnPath("dsh") != null;

    public static bool IsNodeInstalled() => DshPaths.FindOnPath("node") != null;

    public static bool IsNpmInstalled() => DshPaths.FindOnPath("npm") != null;

    public async Task<bool> IsServerUpAsync()
    {
        try
        {
            using var _ = await Http.GetAsync(_dshUri);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>只检测指定 TCP 端口是否在监听（不要求 HTTP 服务已就绪）。</summary>
    public static async Task<bool> IsPortListeningOnPortAsync(int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync("127.0.0.1", port);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>检测 DeepSeek Harness 是否在运行：DshGUI 子进程存活，或配置端口有 HTTP 服务/TCP 监听。</summary>
    public async Task<bool> IsRunningAsync() =>
        IsManagedProcessRunning || await IsPortListeningOnPortAsync(Port) || await IsServerUpAsync();

    /// <summary>
    /// 停止运行中的 DeepSeek Harness：先停 DshGUI 拉起的进程树；
    /// 若配置端口仍被外部 dsh 占用，则定位监听该端口的 node 进程并结束其进程树。
    /// </summary>
    public async Task<bool> StopRunningDshAsync(IProgress<string>? log = null)
    {
        if (IsManagedProcessRunning)
        {
            log?.Report("停止 DshGUI 启动的 dsh 进程树…");
            Stop();
        }
        else if (await IsRunningAsync())
        {
            log?.Report($"检测到外部 dsh 实例占用 {Port} 端口");
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline && await IsRunningAsync())
        {
            var pid = await FindProcessIdListeningOnPortAsync(Port);
            if (pid == null)
                break;

            try
            {
                using var process = Process.GetProcessById(pid.Value);
                var name = process.ProcessName;
                if (!name.StartsWith("node", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("dsh", StringComparison.OrdinalIgnoreCase))
                {
                    log?.Report($"端口 {Port} 由非 dsh 进程占用（{name}），拒绝结束：{pid}");
                    return false;
                }

                log?.Report($"结束占用 {Port} 端口的 dsh 进程：{name} ({pid})");
                process.Kill(entireProcessTree: true);
                await Task.Delay(500);
            }
            catch (Exception ex)
            {
                log?.Report("结束外部 dsh 进程失败：" + ex.Message);
                return false;
            }
        }

        return !await IsRunningAsync();
    }

    /// <summary>通过 netstat -ano 查找监听指定 TCP 端口的进程 id。</summary>
    public static async Task<int?> FindProcessIdListeningOnPortAsync(int port)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "netstat.exe",
            Arguments = "-ano -p tcp",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = new Process { StartInfo = psi };
            if (!process.Start())
                return null;

            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            var suffix = ":" + port + " ";
            foreach (var line in output.Split('\n'))
            {
                if (!line.Contains("LISTENING", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (!line.Contains(suffix, StringComparison.Ordinal))
                    continue;
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 5 && int.TryParse(parts[^1], out var pid))
                    return pid;
            }
        }
        catch
        {
            // netstat 不可用时无法定位外部进程。
        }

        return null;
    }

    public async Task<bool> InstallAsync(string registry, IProgress<string>? progress = null, string? distTag = null)
    {
        var package = string.IsNullOrWhiteSpace(distTag) ? "@deepseek-ai/dsh" : $"@deepseek-ai/dsh@{distTag}";
        if (distTag != null && !distTag.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_'))
            return false;

        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c npm install -g {package} --registry {registry} --no-fund --no-audit --loglevel=http",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = psi };
        process.OutputDataReceived += (_, a) => Report(a.Data);
        process.ErrorDataReceived += (_, a) => Report(a.Data);

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

        void Report(string? line)
        {
            AppendLog(line);
            if (line != null)
                progress?.Report(line);
        }
    }

    public bool Start(string workspaceDirectory, string registry, IProgress<string>? progress = null)
    {
        var dsh = DshPaths.FindOnPath("dsh");
        var commandLine = dsh != null
            ? $"\"{dsh}\" web --port {Port}"
            : $"npx --registry {registry} @deepseek-ai/dsh web --port {Port}";

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
            process.OutputDataReceived += (_, a) => Report(a.Data);
            process.ErrorDataReceived += (_, a) => Report(a.Data);
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

        void Report(string? line)
        {
            AppendLog(line);
            if (line != null)
                progress?.Report(line);
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
