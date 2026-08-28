using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DshGUI.Infrastructure;
using DshGUI.Services;
using DshGUI.Views;

namespace DshGUI.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private enum PrimaryActionKind
    {
        None,
        DownloadNode,
        Retry,
        Install,
        CancelInstall,
        RetryUpdate,
        Exit,
    }

    private enum SecondaryActionKind
    {
        None,
        Retry,
        CancelInstall,
    }

    private readonly ThemeService _theme;
    private readonly SettingsService _settings;
    private readonly DshService _dsh;
    private readonly TrayService _tray;
    private readonly NotificationService _notification;

    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private bool _allowRealClose;
    private bool _isTopmost;
    private string _windowTitle = "DeepSeek Harness";
    private bool _healthChecking;
    private bool _updateInProgress;
    private bool _profileSwitching;
    private DateTime _elapsedStart;
    private SettingsViewModel? _settingsViewModel;
    private ToastWindow? _approvalToast;
    private ToastWindow? _disconnectToast;
    private string? _selectedProfile;
    private string _retryUpdateDistTag = "latest";

    private string _statusText = "正在初始化…";
    private string _elapsedText = "已用 0 秒";
    private string _logText = "";
    private bool _isLoadingVisible = true;
    private bool _isWebViewVisible;
    private bool _isProgressVisible = true;
    private bool _isLogVisible;
    private bool _isMirrorSelectorVisible;
    private bool _isTokenInputVisible;
    private bool _areActionsVisible;
    private bool _isSecondaryActionVisible;
    private string _primaryActionText = "";
    private string _secondaryActionText = "";
    private string _tokenUrl = "";
    private bool _isOfficialMirrorChecked;
    private bool _isNpmmirrorChecked;
    private bool _isSettingsVisible;

    private PrimaryActionKind _primaryActionKind;
    private SecondaryActionKind _secondaryActionKind;

    public MainViewModel(
        ThemeService theme,
        SettingsService settings,
        DshService dsh,
        TrayService tray,
        NotificationService notification)
    {
        _theme = theme;
        _settings = settings;
        _dsh = dsh;
        _tray = tray;
        _notification = notification;

        _selectedProfile = settings.Settings.Profile;
        _isOfficialMirrorChecked = settings.Settings.NpmRegistry != SettingsViewModel.MirrorRegistry;
        _isNpmmirrorChecked = settings.Settings.NpmRegistry == SettingsViewModel.MirrorRegistry;

        PrimaryActionCommand = new RelayCommand(_ => ExecutePrimaryAction());
        SecondaryActionCommand = new RelayCommand(_ => ExecuteSecondaryAction());
        TokenConnectCommand = new RelayCommand(_ => ConnectToken());
        SettingsCommand = new RelayCommand(_ => ToggleSettings());
        PluginManagerCommand = new RelayCommand(_ => PluginManagerRequested?.Invoke());
        ToggleTopmostCommand = new RelayCommand(_ => IsTopmost = !IsTopmost);

        _elapsedTimer.Tick += (_, _) => UpdateElapsed();
        _healthTimer.Tick += OnHealthTick;

        LoadProfiles();
    }

    public string WindowTitle
    {
        get => _windowTitle;
        set => SetProperty(ref _windowTitle, value);
    }

    public bool AllowRealClose
    {
        get => _allowRealClose;
        set => _allowRealClose = value;
    }

    /// <summary>窗口置顶状态（标题栏图钉由 XAML 触发器按此状态换色）。</summary>
    public bool IsTopmost
    {
        get => _isTopmost;
        set => SetProperty(ref _isTopmost, value);
    }

    public string NavigateUrl => _dsh.NavigateUrl;

    /// <summary>供视图构造插件管理窗口等需要 DshService 的场景使用。</summary>
    public DshService DshService => _dsh;

    public ObservableCollection<string> Profiles { get; } = [];

    public string? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (SetProperty(ref _selectedProfile, value))
                _ = OnProfileChangedAsync();
        }
    }

    public SettingsViewModel? SettingsViewModel
    {
        get => _settingsViewModel;
        private set => SetProperty(ref _settingsViewModel, value);
    }

    public bool IsSettingsVisible
    {
        get => _isSettingsVisible;
        private set => SetProperty(ref _isSettingsVisible, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string LogText
    {
        get => _logText;
        private set => SetProperty(ref _logText, value);
    }

    public bool IsLoadingVisible
    {
        get => _isLoadingVisible;
        private set => SetProperty(ref _isLoadingVisible, value);
    }

    public bool IsWebViewVisible
    {
        get => _isWebViewVisible;
        private set => SetProperty(ref _isWebViewVisible, value);
    }

    public bool IsProgressVisible
    {
        get => _isProgressVisible;
        private set => SetProperty(ref _isProgressVisible, value);
    }

    public bool IsLogVisible
    {
        get => _isLogVisible;
        private set => SetProperty(ref _isLogVisible, value);
    }

    public bool IsMirrorSelectorVisible
    {
        get => _isMirrorSelectorVisible;
        private set => SetProperty(ref _isMirrorSelectorVisible, value);
    }

    public bool IsTokenInputVisible
    {
        get => _isTokenInputVisible;
        private set => SetProperty(ref _isTokenInputVisible, value);
    }

    public bool AreActionsVisible
    {
        get => _areActionsVisible;
        private set => SetProperty(ref _areActionsVisible, value);
    }

    public bool IsSecondaryActionVisible
    {
        get => _isSecondaryActionVisible;
        private set => SetProperty(ref _isSecondaryActionVisible, value);
    }

    public string PrimaryActionText
    {
        get => _primaryActionText;
        private set => SetProperty(ref _primaryActionText, value);
    }

    public string SecondaryActionText
    {
        get => _secondaryActionText;
        private set => SetProperty(ref _secondaryActionText, value);
    }

    public string TokenUrl
    {
        get => _tokenUrl;
        set => SetProperty(ref _tokenUrl, value);
    }

    public bool IsOfficialMirrorChecked
    {
        get => _isOfficialMirrorChecked;
        set
        {
            if (SetProperty(ref _isOfficialMirrorChecked, value) && value)
                IsNpmmirrorChecked = false;
        }
    }

    public bool IsNpmmirrorChecked
    {
        get => _isNpmmirrorChecked;
        set
        {
            if (SetProperty(ref _isNpmmirrorChecked, value) && value)
                IsOfficialMirrorChecked = false;
        }
    }

    public ICommand PrimaryActionCommand { get; }

    public ICommand SecondaryActionCommand { get; }

    public ICommand TokenConnectCommand { get; }

    public ICommand SettingsCommand { get; }

    public ICommand PluginManagerCommand { get; }

    public ICommand ToggleTopmostCommand { get; }

    /// <summary>dsh 已就绪，请求视图执行 WebView2 导航。</summary>
    public event Action? NavigateRequested;

    /// <summary>请求视图取消未完成的页面加载（切换 profile/更新/重连等流程开始时）。</summary>
    public event Action? NavigationResetRequested;

    /// <summary>请求视图打开插件管理窗口。</summary>
    public event Action? PluginManagerRequested;

    /// <summary>设置已保存，请求视图同步热键注册等纯视图状态。</summary>
    public event Action? SettingsApplied;

    /// <summary>设置变化但端口未变，请求视图重新加载 WebView 页面。</summary>
    public event Action? ReloadRequested;

    /// <summary>已用计时器启动，请求视图显示任务栏不确定进度。</summary>
    public event Action? ElapsedStarted;

    /// <summary>已用计时器停止，请求视图清除任务栏进度。</summary>
    public event Action? ElapsedStopped;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    public void ShowMainWindow()
    {
        if (Application.Current.MainWindow is not { } window)
            return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();

        // Activate() 在部分时机不可靠（比如从 toast 点击唤起），用 Win32 强制置前。
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero)
            SetForegroundWindow(hwnd);
    }

    public void RefreshTheme() => _theme.ApplyTheme();

    public Task RestartManagedDshAsync() => RunStartupFlowAsync();

    /// <summary>启动流程：加载面板 → 检查/启动 dsh → 请求导航。</summary>
    public async Task StartAsync()
    {
        ShowLoading("正在初始化…", showLog: false);
        await RunStartupFlowAsync();
    }

    public async Task<bool> EnsureAuthTokenAsync()
    {
        if (_dsh.AccessToken != null)
            return true;
        if (!await _dsh.IsAuthRequiredAsync())
            return true;

        if (_dsh.IsManagedProcessRunning)
        {
            var tokenDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
            while (_dsh.AccessToken == null && DateTime.UtcNow < tokenDeadline)
                await Task.Delay(100);
            if (_dsh.AccessToken != null)
                return true;
        }

        ShowTokenInput();
        return false;
    }

    /// <summary>页面加载两次仍失败：能判定是鉴权问题时引导粘贴令牌，否则提示重试。</summary>
    public async Task HandlePageLoadFailedAsync()
    {
        if (_dsh.AccessToken == null && await _dsh.IsAuthRequiredAsync())
        {
            ShowTokenInput();
            return;
        }

        ShowFailed("页面加载失败，请检查 dsh 服务后重试。", "重试", PrimaryActionKind.Retry);
    }

    public void ShowLoading(string message, bool showLog, bool clearLog = true)
    {
        IsLoadingVisible = true;
        IsWebViewVisible = false;
        StatusText = message;
        IsProgressVisible = true;
        IsLogVisible = showLog;
        if (showLog && clearLog)
            LogText = "";
        IsMirrorSelectorVisible = false;
        AreActionsVisible = false;
        IsSecondaryActionVisible = false;
        StartElapsed();
    }

    public void ShowWebView()
    {
        StopElapsed();
        IsLoadingVisible = false;
        IsWebViewVisible = true;
    }

    public void StartHealthChecking()
    {
        _healthTimer.Stop();
        _healthTimer.Start();
    }

    public void StopHealthChecking() => _healthTimer.Stop();

    /// <summary>解析并处理 WebView 桥接消息；解析失败由视图忽略。</summary>
    public void HandleWebMessageJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var type = doc.RootElement.GetProperty("type").GetString();

        switch (type)
        {
            case "idle":
                _tray.SetRunning(false);
                HandleAgentIdle();
                break;

            case "running":
                _tray.SetRunning(true);
                break;

            case "approval":
                HandleApprovalRequested();
                break;

            case "approval-resolved":
                HandleApprovalResolved();
                break;

            case "theme" when doc.RootElement.TryGetProperty("dark", out var dark):
                _theme.SetPageDark(dark.GetBoolean());
                RefreshTheme();
                break;

            case "title" when doc.RootElement.TryGetProperty("text", out var text):
                var title = text.GetString();
                WindowTitle = string.IsNullOrWhiteSpace(title)
                    ? "DeepSeek Harness"
                    : title;
                break;
        }
    }

    private void LoadProfiles()
    {
        var profiles = new List<string>(DshPaths.GetProfileNames());
        if (!profiles.Contains(_settings.Settings.Profile, StringComparer.OrdinalIgnoreCase))
            profiles.Insert(0, _settings.Settings.Profile);

        Profiles.Clear();
        foreach (var profile in profiles)
            Profiles.Add(profile);
    }

    private async Task OnProfileChangedAsync()
    {
        if (_profileSwitching || SelectedProfile is not string profile)
            return;
        if (string.Equals(profile, _settings.Settings.Profile, StringComparison.Ordinal))
            return;

        _profileSwitching = true;
        try
        {
            _settings.Settings.Profile = profile;
            _settings.Save();
            _dsh.Profile = profile;
            await SwitchProfileAsync();
        }
        finally
        {
            _profileSwitching = false;
        }
    }

    private async Task SwitchProfileAsync()
    {
        StopHealthChecking();
        _dsh.Stop();
        _dsh.ClearAccessToken();
        IsWebViewVisible = false;
        IsLoadingVisible = true;
        await RunStartupFlowAsync();
    }

    /// <summary>启动流程：检查/安装/启动 dsh，成功后请求视图导航。</summary>
    public async Task RunStartupFlowAsync()
    {
        StopHealthChecking();
        NavigationResetRequested?.Invoke();
        ShowLoading("正在检查 DeepSeek Harness…", showLog: false);

        if (await _dsh.IsServerUpAsync())
        {
            NavigateRequested?.Invoke();
            return;
        }

        if (!DshService.IsNodeInstalled() || !DshService.IsNpmInstalled())
        {
            ShowNoNode();
            return;
        }

        if (!DshService.IsInstalled())
        {
            ShowNotInstalled();
            return;
        }

        await StartDshAndWaitAsync();
    }

    private async void InstallDshAsync()
    {
        var registry = GetSelectedRegistry();
        _settings.Settings.NpmRegistry = registry;
        _settings.Save();

        ShowLoading("正在安装 DeepSeek Harness…", showLog: true);
        AppendLogLine($"镜像源：{registry}");
        AppendLogLine($"npm install -g @deepseek-ai/dsh --registry {registry} --no-fund --no-audit --loglevel=http");

        var progress = new Progress<string>(AppendLogLine);
        var ok = await _dsh.InstallAsync(registry, progress);

        if (ok)
        {
            AppendLogLine("—— 安装完成 ——");
            await StartDshAndWaitAsync();
        }
        else
        {
            AppendLogLine("—— 安装失败 ——");
            ShowFailed("安装失败。\n请确认网络可用，或切换到国内镜像后重试。");
        }
    }

    private async Task StartDshAndWaitAsync()
    {
        ShowLoading("正在启动 DeepSeek Harness…", showLog: true, clearLog: false);
        AppendLogLine("—— 开始启动 dsh web 服务 ——");
        var progress = new Progress<string>(AppendLogLine);

        if (!_dsh.Start(WorkspaceDirectory, _settings.Settings.NpmRegistry, progress))
        {
            ShowFailed("无法启动 dsh。\n请确认已安装：npm install -g @deepseek-ai/dsh");
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var up = false;
        while (stopwatch.Elapsed < StartupTimeout)
        {
            if (await _dsh.IsServerUpAsync())
            {
                up = true;
                break;
            }

            if (_dsh.HasExited)
                break;

            await Task.Delay(300);
        }

        if (!up)
        {
            ShowFailed(_dsh.HasExited
                ? "dsh 启动失败（可能是端口被占用或配置错误）。\n请查看日志：" + DshService.LogPath
                : "DeepSeek Harness 启动超时（60 秒）。\n请查看日志：" + DshService.LogPath);
            return;
        }

        NavigateRequested?.Invoke();
    }

    private void ShowTokenInput()
    {
        StopElapsed();
        StatusText = "此版本 dsh 需要访问令牌";
        IsProgressVisible = false;
        IsLogVisible = false;
        IsMirrorSelectorVisible = false;
        AreActionsVisible = false;
        TokenUrl = "";
        IsTokenInputVisible = true;
    }

    private void ConnectToken()
    {
        var match = Regex.Match(TokenUrl?.Trim() ?? "", @"[?&]token=([A-Za-z0-9_-]{8,})");
        if (!match.Success)
        {
            System.Windows.MessageBox.Show(
                Application.Current.MainWindow,
                "地址里没有找到令牌，请粘贴 dsh 启动时打印的完整地址（含 ?token=…）。",
                "无法连接",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return;
        }

        _dsh.SetExternalToken(match.Groups[1].Value);
        IsTokenInputVisible = false;
        NavigateRequested?.Invoke();
    }

    private void ShowNoNode()
    {
        StopElapsed();
        StatusText = "未检测到 Node.js（npm）。\ndsh 依赖 Node.js 才能安装和运行。";
        IsProgressVisible = false;
        IsLogVisible = false;
        SetActions("下载 Node.js", PrimaryActionKind.DownloadNode, "重新检测", SecondaryActionKind.Retry);
    }

    private void ShowNotInstalled()
    {
        StopElapsed();
        StatusText = "未检测到 DeepSeek Harness（dsh）。\n需要安装后才能使用。";
        IsProgressVisible = false;
        IsLogVisible = false;

        IsOfficialMirrorChecked = _settings.Settings.NpmRegistry != SettingsViewModel.MirrorRegistry;
        IsNpmmirrorChecked = _settings.Settings.NpmRegistry == SettingsViewModel.MirrorRegistry;
        IsMirrorSelectorVisible = true;

        SetActions("立即安装", PrimaryActionKind.Install, "取消", SecondaryActionKind.CancelInstall);
    }

    private void ShowFailed(
        string message,
        string primaryText = "重试",
        PrimaryActionKind primary = PrimaryActionKind.Retry,
        string? secondaryText = null,
        SecondaryActionKind secondary = SecondaryActionKind.None)
    {
        StopElapsed();
        StatusText = message;
        IsProgressVisible = false;
        // 保留日志便于排查。
        SetActions(primaryText, primary, secondaryText, secondary);
    }

    public void ShowFatal(string message)
    {
        StopElapsed();
        StatusText = message;
        IsProgressVisible = false;
        IsLogVisible = false;
        SetActions("退出", PrimaryActionKind.Exit);
    }

    private void SetActions(
        string primaryText,
        PrimaryActionKind primary,
        string? secondaryText = null,
        SecondaryActionKind secondary = SecondaryActionKind.None)
    {
        PrimaryActionText = primaryText;
        _primaryActionKind = primary;
        if (secondaryText != null)
        {
            SecondaryActionText = secondaryText;
            _secondaryActionKind = secondary;
            IsSecondaryActionVisible = true;
        }
        else
        {
            IsSecondaryActionVisible = false;
        }

        AreActionsVisible = true;
    }

    private void ExecutePrimaryAction()
    {
        switch (_primaryActionKind)
        {
            case PrimaryActionKind.DownloadNode:
                OpenNodeDownload();
                break;

            case PrimaryActionKind.Retry:
                _ = RunStartupFlowAsync();
                break;

            case PrimaryActionKind.Install:
                InstallDshAsync();
                break;

            case PrimaryActionKind.CancelInstall:
                CancelInstall();
                break;

            case PrimaryActionKind.RetryUpdate:
                RunUpdateAsync(_retryUpdateDistTag);
                break;

            case PrimaryActionKind.Exit:
                ExitApp();
                break;
        }
    }

    private void ExecuteSecondaryAction()
    {
        switch (_secondaryActionKind)
        {
            case SecondaryActionKind.Retry:
                _ = RunStartupFlowAsync();
                break;

            case SecondaryActionKind.CancelInstall:
                CancelInstall();
                break;
        }
    }

    private void CancelInstall()
    {
        StopElapsed();
        StatusText = "已取消安装。\n可点击「重试」再次安装。";
        IsProgressVisible = false;
        IsLogVisible = false;
        IsMirrorSelectorVisible = false;
        SetActions("重试", PrimaryActionKind.Retry);
    }

    private void ExitApp()
    {
        AllowRealClose = true;
        Application.Current.Shutdown();
    }

    private void OpenNodeDownload()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://nodejs.org/",
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打开浏览器失败时忽略。
        }

        StatusText = "请在浏览器中安装 Node.js（LTS）。\n安装完成后点击「重新检测」。";
    }

    private string GetSelectedRegistry() =>
        IsNpmmirrorChecked
            ? SettingsViewModel.MirrorRegistry
            : SettingsViewModel.OfficialRegistry;

    private void StartElapsed()
    {
        _elapsedStart = DateTime.UtcNow;
        ElapsedText = "已用 0 秒";
        _elapsedTimer.Start();
        ElapsedStarted?.Invoke();
    }

    private void StopElapsed()
    {
        _elapsedTimer.Stop();
        ElapsedStopped?.Invoke();
    }

    private void UpdateElapsed()
    {
        ElapsedText = $"已用 {(int)(DateTime.UtcNow - _elapsedStart).TotalSeconds} 秒";
    }

    private void AppendLogLine(string line)
    {
        if (string.IsNullOrEmpty(line))
            return;

        LogText += line + Environment.NewLine;
        if (LogText.Length > 40_000)
            LogText = LogText[^30_000..];
    }

    private async void RunUpdateAsync(string distTag)
    {
        if (_updateInProgress)
            return;

        _updateInProgress = true;
        try
        {
            CloseSettings();
            await UpdateDshAsync(distTag);
        }
        finally
        {
            _updateInProgress = false;
        }
    }

    private async Task UpdateDshAsync(string distTag)
    {
        _retryUpdateDistTag = distTag;
        StopHealthChecking();
        NavigationResetRequested?.Invoke();
        ShowMainWindow();
        IsWebViewVisible = false;
        var updateLabel = distTag switch
        {
            "latest" => "最新版",
            "next" => "预览版",
            _ => "版本 " + distTag,
        };
        ShowLoading($"正在更新 DeepSeek Harness（{updateLabel}）…", showLog: true);

        AppendLogLine("—— 停止正在运行的 dsh ——");
        var stopped = await _dsh.StopRunningDshAsync(new Progress<string>(AppendLogLine));
        if (!stopped)
        {
            ShowFailed(
                "更新前需要停止 dsh，但当前 dsh 不是由 DshGUI 启动的（外部实例）或端口仍被占用。\n请手动停止后重试。",
                "重试更新",
                PrimaryActionKind.RetryUpdate,
                "启动 dsh",
                SecondaryActionKind.Retry);
            return;
        }

        AppendLogLine("—— 开始更新 ——");
        AppendLogLine($"npm install -g @deepseek-ai/dsh@{distTag} --registry {_settings.Settings.NpmRegistry} --no-fund --no-audit --loglevel=http");
        var ok = await _dsh.InstallAsync(_settings.Settings.NpmRegistry, new Progress<string>(AppendLogLine), distTag);
        if (!ok)
        {
            ShowFailed(
                $"更新失败。请查看上方日志，或手动执行 npm install -g @deepseek-ai/dsh@{distTag}。",
                "重试更新",
                PrimaryActionKind.RetryUpdate,
                "启动 dsh",
                SecondaryActionKind.Retry);
            return;
        }

        AppendLogLine("—— 更新完成，正在重新启动 ——");
        await StartDshAndWaitAsync();
    }

    private void HandleAgentIdle()
    {
        if (!_settings.Settings.NotifyOnComplete)
            return;

        if (Application.Current.MainWindow is not { } window)
            return;

        // 仅当窗口最小化或缩到托盘时提醒。
        if (window.WindowState == WindowState.Minimized || !window.IsVisible)
        {
            var title = WindowTitle;
            var message = string.IsNullOrWhiteSpace(title) || title == "DeepSeek Harness"
                ? "回到窗口查看结果"
                : title;
            _notification.Show("任务已完成", message, ShowMainWindow);
        }
    }

    private void HandleApprovalRequested()
    {
        if (Application.Current.MainWindow is not { } window)
            return;

        // 仅当窗口不在前台时提醒，避免打扰正在看页面的人。
        if (window.WindowState == WindowState.Minimized || !window.IsVisible || !window.IsActive)
        {
            // 持久显示，直到用户点击或审批被处理。
            _approvalToast = _notification.Show(
                "需要你的批准",
                "有权限请求等待处理，点击返回窗口",
                ShowMainWindow,
                persistent: true);
        }
    }

    private void HandleApprovalResolved()
    {
        if (_approvalToast == null)
            return;

        try
        {
            _approvalToast.Close();
        }
        catch
        {
            // 已关闭则忽略。
        }

        _approvalToast = null;
    }

    private async void OnHealthTick(object? sender, EventArgs e)
    {
        if (_healthChecking)
            return;
        _healthChecking = true;
        try
        {
            var up = await _dsh.IsServerUpAsync();
            if (!up && _disconnectToast == null)
            {
                _disconnectToast = _notification.Show(
                    "dsh 已断开",
                    "点击重新连接",
                    Reconnect,
                    persistent: true);
            }
        }
        finally
        {
            _healthChecking = false;
        }
    }

    private async void Reconnect() => await ReconnectAsync();

    private async Task ReconnectAsync()
    {
        _disconnectToast = null;
        StopHealthChecking();
        IsWebViewVisible = false;
        IsLoadingVisible = true;

        // WebView2 控制器由视图在 NavigateRequested 时创建/确保。
        await RunStartupFlowAsync();
    }

    private void ToggleSettings()
    {
        if (IsSettingsVisible)
            CloseSettings();
        else
            OpenSettings();
    }

    private void OpenSettings()
    {
        CloseSettings();

        var viewModel = new SettingsViewModel(_settings, _dsh)
        {
            NoticeCallback = (title, message) =>
            {
                System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    message,
                    title,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return true;
            },
            PortOccupiedCallback = port =>
                System.Windows.MessageBox.Show(
                    Application.Current.MainWindow,
                    $"端口 {port} 已被占用。\n\n若继续保存，DshGUI 会连接该端口上的现有服务，"
                    + "不再自行启动新的 dsh 实例。\n\n仍要保存吗？",
                    "端口已被占用",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) == MessageBoxResult.Yes,
        };
        viewModel.RequestClose += CloseSettings;
        viewModel.SettingsChanged += OnSettingsChanged;
        viewModel.UpdateRequested += RunUpdateAsync;
        viewModel.RestartRequested += OnRestartDshRequested;

        SettingsViewModel = viewModel;
        IsSettingsVisible = true;
    }

    private void CloseSettings()
    {
        if (_settingsViewModel != null)
        {
            _settingsViewModel.RequestClose -= CloseSettings;
            _settingsViewModel.SettingsChanged -= OnSettingsChanged;
            _settingsViewModel.UpdateRequested -= RunUpdateAsync;
            _settingsViewModel.RestartRequested -= OnRestartDshRequested;
            _settingsViewModel = null;
        }

        SettingsViewModel = null;
        IsSettingsVisible = false;
    }

    private async void OnRestartDshRequested()
    {
        CloseSettings();
        StopHealthChecking();
        _dsh.SetPort(_settings.Settings.DshPort);   // 应用设置面板里修改的端口
        _dsh.Stop();                                // 只停 DshGUI 自己启动的实例
        _dsh.ClearAccessToken();
        IsWebViewVisible = false;
        IsLoadingVisible = true;
        await RunStartupFlowAsync();
    }

    private async void OnSettingsChanged()
    {
        _theme.SetPageDark(null);
        _theme.ApplyTheme();
        SettingsApplied?.Invoke();

        var portChanged = _settings.Settings.DshPort != _dsh.Port;
        if (!portChanged)
        {
            ReloadRequested?.Invoke();
            return;
        }

        // 端口变更：更新 DshService，停止旧端口上的 dsh，并按新端口重启连接流程。
        _dsh.SetPort(_settings.Settings.DshPort);
        _dsh.Stop();
        _dsh.ClearAccessToken();
        IsWebViewVisible = false;
        IsLoadingVisible = true;
        await RunStartupFlowAsync();
    }

    // dsh 的工作目录 = agent 的 workspace 根目录（壳不提供选择器，固定用主目录）。
    private static readonly string WorkspaceDirectory =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);
}
