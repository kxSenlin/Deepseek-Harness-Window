using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;

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

    /// <summary>启动的 dsh profile（dsh --profile &lt;名字&gt;，默认 web）。</summary>
    public string Profile { get; set; } = "web";

    /// <summary>0.1.2+ 的访问令牌（从 dsh stdout 的 URL 行解析，或外部 dsh 由用户粘贴）。0.1.1 无令牌为 null。</summary>
    public string? AccessToken { get; private set; }

    /// <summary>页面导航地址：带令牌（0.1.2+）或裸根路径（0.1.1）。</summary>
    public string NavigateUrl => AccessToken == null ? Url + "/" : Url + "/?token=" + AccessToken;

    /// <summary>外部 dsh 场景：由用户粘贴启动地址后注入令牌。</summary>
    public void SetExternalToken(string token) => AccessToken = token;

    /// <summary>切换 profile / 重连时清除缓存的令牌。</summary>
    public void ClearAccessToken() => AccessToken = null;

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
    /// 只停止 DshGUI 自己启动的 dsh 实例；外部实例（用户手动启动）不干预。
    /// </summary>
    public async Task<bool> StopRunningDshAsync(IProgress<string>? log = null)
    {
        if (!IsManagedProcessRunning)
        {
            log?.Report("当前 dsh 不是由 DshGUI 启动的（外部实例），DshGUI 不停止它。");
            return false;
        }

        log?.Report("停止 DshGUI 启动的 dsh 进程树…");
        Stop();

        // 等端口真正释放（最多 5 秒），避免旧进程未退净导致后续连接旧实例。
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < deadline && await IsPortListeningOnPortAsync(Port))
            await Task.Delay(200);

        return !await IsPortListeningOnPortAsync(Port);
    }

    public async Task<bool> InstallAsync(string registry, IProgress<string>? progress = null, string? distTag = null)
    {
        var package = string.IsNullOrWhiteSpace(distTag) ? "@deepseek-ai/dsh" : $"@deepseek-ai/dsh@{distTag}";
        if (distTag != null && !distTag.All(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' or '+'))
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
            ? $"\"{dsh}\" --profile {Profile} --port {Port} --no-open"
            : $"npx --registry {registry} @deepseek-ai/dsh --profile {Profile} --port {Port} --no-open";

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
            {
                progress?.Report(line);
                TryCaptureToken(line);
            }
        }
    }

    // 0.1.2+ 启动时打印：dsh web: http://127.0.0.1:3080/?token=XXX (LAN: ...)
    private static readonly Regex TokenPattern = new(@"[?&]token=([A-Za-z0-9_-]{8,})", RegexOptions.Compiled);

    private void TryCaptureToken(string line)
    {
        if (AccessToken != null)
            return;
        var match = TokenPattern.Match(line);
        if (match.Success)
            AccessToken = match.Groups[1].Value;
    }

    /// <summary>新版本 dsh（0.1.2+）对无令牌的 GET / 返回 401，0.1.1 直接 200。</summary>
    public async Task<bool> IsAuthRequiredAsync()
    {
        try
        {
            using var response = await Http.GetAsync(_dshUri);
            return response.StatusCode == HttpStatusCode.Unauthorized;
        }
        catch
        {
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
