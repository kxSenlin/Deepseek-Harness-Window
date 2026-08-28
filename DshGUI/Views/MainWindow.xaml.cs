using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using DshGUI.Infrastructure;
using DshGUI.Services;
using DshGUI.ViewModels;

namespace DshGUI.Views
{
    public partial class MainWindow : Window
    {
        private const int WM_GETMINMAXINFO = 0x0024;
        private const int WM_HOTKEY = 0x0312;
        private const int MONITOR_DEFAULTTONEAREST = 0x00000002;
        private const int HotkeyId = 0x0D5A;

        // Windows 11 DWM 窗口样式：圆角 + 灰色外轮廓 + 原生阴影。
        private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
        private const int DWMWA_BORDER_COLOR = 34;
        private const int DWMWCP_ROUND = 2;

        private const string MaximizeIconData = "M 1.5,1.5 H 8.5 V 8.5 H 1.5 Z";
        private const string RestoreIconData = "M 3,1 H 9 V 7 H 3 Z M 1,3 H 7 V 9 H 1 Z";

        // 注入到 dsh 页面：监听「运行中→空闲」「深色主题」「当前会话标题」三个状态。
        private const string BridgeScript = @"(function () {
    const post = (m) => window.chrome.webview.postMessage(m);
    let wasRunning = false;
    let idleTimer = null;
    let lastDark = null;
    let lastTitle = null;
    let wasApproval = false;

    function currentTitle() {
        const el = document.querySelector('nav button[disabled]');
        return el ? el.textContent.trim() : null;
    }

    setInterval(() => {
        const approval = document.querySelector('[data-approval-key]') != null;
        const running = document.querySelectorAll('[data-state=ongoing], [data-running]').length > 0 || approval;
        if (!wasRunning && running) {
            post({ type: 'running' });
        }
        if (wasRunning && !running && !idleTimer) {
            idleTimer = setTimeout(() => {
                idleTimer = null;
                if (document.querySelectorAll('[data-state=ongoing], [data-running]').length === 0
                    && document.querySelector('[data-approval-key]') == null) {
                    post({ type: 'idle' });
                }
            }, 800);
        }
        wasRunning = running;

        const body = document.body;
        const dark = body ? body.hasAttribute('data-ds-dark-theme') : false;
        if (dark !== lastDark) {
            lastDark = dark;
            post({ type: 'theme', dark });
        }

        const title = currentTitle();
        if (title !== lastTitle) {
            lastTitle = title;
            post({ type: 'title', text: title || '' });
        }

        if (approval && !wasApproval) {
            post({ type: 'approval' });
        } else if (!approval && wasApproval) {
            post({ type: 'approval-resolved' });
        }
        wasApproval = approval;
    }, 400);
})();";

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MINMAXINFO
        {
            public POINT PtReserved;
            public POINT PtMaxSize;
            public POINT PtMaxPosition;
            public POINT PtMinTrackSize;
            public POINT PtMaxTrackSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int CbSize;
            public RECT RcMonitor;
            public RECT RcWork;
            public int DwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

        // dsh 的工作目录 = agent 的 workspace 根目录（壳不提供选择器，固定用主目录）。
        private static readonly string WorkspaceDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

        private readonly MainViewModel _viewModel;
        private readonly SettingsService _settings;
        private readonly DshService _dsh;
        private readonly ThemeService _theme;
        private readonly TrayService _tray;
        private readonly NotificationService _notification = new();

        private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromSeconds(1) };
        private readonly DispatcherTimer _healthTimer = new() { Interval = TimeSpan.FromSeconds(3) };
        private readonly DispatcherTimer _pageLoadTimer = new() { Interval = TimeSpan.FromSeconds(20) };
        private bool _healthChecking;
        private bool _pendingPageLoad;
        private bool _navigateWhenShown;
        private bool _navigationRetried;
        private DateTime _elapsedStart;
        private Action? _primaryAction;
        private Action? _secondaryAction;
        private SettingsViewModel? _settingsViewModel;
        private PluginManagerService? _pluginManager;
        private PluginPackageService? _pluginPackage;
        private PluginManagerWindow? _pluginWindow;
        private ToastWindow? _approvalToast;
        private ToastWindow? _disconnectToast;

        private IntPtr _hwnd;
        private bool _updateInProgress;
        private CoreWebView2? _wiredCore;

        public MainWindow(MainViewModel viewModel, SettingsService settings, DshService dsh, ThemeService theme, TrayService tray)
        {
            InitializeComponent();

            // 把 WebView2 用户数据目录固定到 %LOCALAPPDATA%\DshGUI\WebView2，
            // 避免默认在 exe 旁生成「DshGUI.exe.WebView2」文件夹（如 exe 在桌面则会出现在桌面）。
            WebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshGUI", "WebView2"),
            };

            _viewModel = viewModel;
            _settings = settings;
            _dsh = dsh;
            _theme = theme;
            _tray = tray;
            DataContext = viewModel;

            TitleIcon.Source = IconHelper.GetAppIcon(16);
            _elapsedTimer.Tick += (_, _) => UpdateElapsed();
            _healthTimer.Tick += OnHealthTick;
            _pageLoadTimer.Tick += (_, _) => OnPageLoadTimeout();
            WebView.NavigationCompleted += OnNavigationCompleted;
            ContentRendered += OnContentRendered;
            TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

            _theme.ThemeChanged += () => _theme.ApplyToWebView(WebView.CoreWebView2);

            var profiles = new List<string>(DshPaths.GetProfileNames());
            if (!profiles.Contains(_settings.Settings.Profile, StringComparer.OrdinalIgnoreCase))
                profiles.Insert(0, _settings.Settings.Profile);
            ProfileSelector.ItemsSource = profiles;
            ProfileSelector.SelectedItem = _settings.Settings.Profile;

            RestoreWindowState();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            ApplyDwmWindowStyle();

            if (_settings.Settings.HotkeyEnabled)
                RegisterHotKey(_hwnd, HotkeyId, _settings.Settings.HotkeyModifiers, _settings.Settings.HotkeyKey);
        }

        // 圆角 + 灰色外轮廓 + 原生阴影（Windows 11 起由 DWM 提供；旧系统忽略失败）。
        private void ApplyDwmWindowStyle()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            var corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(_hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, Marshal.SizeOf(typeof(int)));

            // COLORREF = 0x00BBGGRR；0x808080 为灰色外轮廓。
            var borderColor = 0x00808080;
            _ = DwmSetWindowAttribute(_hwnd, DWMWA_BORDER_COLOR, ref borderColor, Marshal.SizeOf(typeof(int)));
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            switch (msg)
            {
                case WM_GETMINMAXINFO:
                    ConstrainMaximizeToWorkArea(hwnd, lParam);
                    handled = true;
                    break;

                case WM_HOTKEY:
                    if (wParam.ToInt32() == HotkeyId)
                        ToggleVisibility();
                    handled = true;
                    break;
            }

            return IntPtr.Zero;
        }

        // 无边框窗口最大化时会铺满整个屏幕（盖住任务栏），这里把最大化范围约束到工作区。
        private static void ConstrainMaximizeToWorkArea(IntPtr hwnd, IntPtr lParam)
        {
            var mmi = (MINMAXINFO)Marshal.PtrToStructure(lParam, typeof(MINMAXINFO))!;

            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { CbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(monitor, ref info))
                {
                    mmi.PtMaxPosition.X = info.RcWork.Left - info.RcMonitor.Left;
                    mmi.PtMaxPosition.Y = info.RcWork.Top - info.RcMonitor.Top;
                    mmi.PtMaxSize.X = info.RcWork.Right - info.RcWork.Left;
                    mmi.PtMaxSize.Y = info.RcWork.Bottom - info.RcWork.Top;
                }
            }

            Marshal.StructureToPtr(mmi, lParam, true);
        }

        private void SizeToWorkArea()
        {
            var area = SystemParameters.WorkArea;
            Width = Math.Min(1440, area.Width * 0.8);
            Height = Math.Min(920, area.Height * 0.8);
            Left = area.Left + (area.Width - Width) / 2;
            Top = area.Top + (area.Height - Height) / 2;
        }

        private void RestoreWindowState()
        {
            var s = _settings.Settings;
            var vsLeft = SystemParameters.VirtualScreenLeft;
            var vsTop = SystemParameters.VirtualScreenTop;
            var vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            var vsBottom = vsTop + SystemParameters.VirtualScreenHeight;
            var onScreen = s.HasWindowBounds
                && s.WindowLeft < vsRight
                && s.WindowTop < vsBottom
                && s.WindowLeft + s.WindowWidth > vsLeft
                && s.WindowTop + s.WindowHeight > vsTop;

            if (onScreen)
            {
                Left = s.WindowLeft;
                Top = s.WindowTop;
                Width = s.WindowWidth;
                Height = s.WindowHeight;
                if (s.WindowMaximized)
                    WindowState = WindowState.Maximized;
            }
            else
            {
                SizeToWorkArea();
            }
        }

        private void SaveWindowState()
        {
            var s = _settings.Settings;
            if (WindowState == WindowState.Maximized)
            {
                var rb = RestoreBounds;
                s.WindowLeft = rb.Left;
                s.WindowTop = rb.Top;
                s.WindowWidth = rb.Width;
                s.WindowHeight = rb.Height;
                s.WindowMaximized = true;
            }
            else if (WindowState == WindowState.Normal)
            {
                s.WindowLeft = Left;
                s.WindowTop = Top;
                s.WindowWidth = Width;
                s.WindowHeight = Height;
                s.WindowMaximized = false;
            }
            _settings.Save();
        }

        private async void OnLoaded(object sender, RoutedEventArgs e) => await StartupAsync();

        /// <summary>启动流程：加载面板 → 检查/启动 dsh → 跳转页面。</summary>
        private async Task StartupAsync()
        {
            ShowLoading("正在初始化…", showLog: false);
            // WebView2 推迟到 Navigate() 时才创建：启动/等待阶段内容区保持纯 WPF 加载面板，
            // 避免 WebView2 的 HWND 遮挡控制台，也避免打开窗口时看到空 WebView2。
            await RunStartupFlowAsync();
        }

        /// <summary>静默自启入口：窗口从不显示（无 Show/Hide、无开机闪现），后台直接跑启动流程。</summary>
        public void RunStartupInBackground()
        {
            // 创建隐藏 HWND（触发 OnSourceInitialized：全局热键、DWM 样式），但不显示窗口；
            // WebView2 与页面加载推迟到窗口首次显示（见 Navigate 的 IsVisible 守卫）时再进行。
            _ = new WindowInteropHelper(this).EnsureHandle();
            _ = StartupAsync();
        }

        // 初始化 / 重建 WebView2 控制器，并注入桥接脚本；可被重连流程复用。
        private async Task<bool> EnsureWebViewReadyAsync()
        {
            try
            {
                WebView.DefaultBackgroundColor = _theme.WebViewBackgroundColor;
                await WebView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                LogWebView($"EnsureCoreWebView2Async 失败: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            _theme.ApplyToWebView(WebView.CoreWebView2);
            _viewModel.RefreshTheme();

            var core = WebView.CoreWebView2;
            if (core == null)
                LogWebView("EnsureCoreWebView2Async 完成但 CoreWebView2 为 null");
            else if (!ReferenceEquals(core, _wiredCore))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
                core.WebMessageReceived += OnWebMessageReceived;
                _wiredCore = core;
            }

            return true;
        }

        // 检测 dsh 是否已安装/已启动，并按需安装、启动。可被「重试」再次调用。
        private async Task RunStartupFlowAsync()
        {
            ShowLoading("正在检查 DeepSeek Harness…", showLog: false);

            if (await _dsh.IsServerUpAsync())
            {
                Navigate();
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

            Navigate();
        }

        /// <summary>
        /// 确保已有访问令牌（0.1.2+ 需要）：DshGUI 自己启动的 dsh 从 stdout 解析；
        /// 解析不到或外部启动的 dsh 则弹出粘贴框。0.1.1 无需令牌直接 true。
        /// 只在 Navigate() 内调用——所有导航入口（启动/重连/切 profile/粘贴令牌后）统一走这里。
        /// </summary>
        private async Task<bool> EnsureAuthTokenAsync()
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

        private void ShowTokenInput()
        {
            StopElapsed();
            StatusText.Text = "此版本 dsh 需要访问令牌";
            InstallProgress.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            MirrorSelector.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
            TokenUrlBox.Text = "";
            TokenInputPanel.Visibility = Visibility.Visible;
        }

        private void OnTokenConnectClick(object sender, RoutedEventArgs e)
        {
            var match = Regex.Match(TokenUrlBox.Text?.Trim() ?? "", @"[?&]token=([A-Za-z0-9_-]{8,})");
            if (!match.Success)
            {
                System.Windows.MessageBox.Show(this, "地址里没有找到令牌，请粘贴 dsh 启动时打印的完整地址（含 ?token=…）。",
                    "无法连接", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            _dsh.SetExternalToken(match.Groups[1].Value);
            TokenInputPanel.Visibility = Visibility.Collapsed;
            Navigate();
        }

        private bool _profileSwitching;

        private async void OnProfileSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_profileSwitching || ProfileSelector.SelectedItem is not string profile)
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
            _healthTimer.Stop();
            CancelPendingPageLoad();
            _dsh.Stop();
            _dsh.ClearAccessToken();
            WebView.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            await RunStartupFlowAsync();
        }

        private void ShowLoading(string message, bool showLog, bool clearLog = true)
        {
            LoadingPanel.Visibility = Visibility.Visible;
            StatusText.Text = message;
            InstallProgress.Visibility = Visibility.Visible;
            LogContainer.Visibility = showLog ? Visibility.Visible : Visibility.Collapsed;
            if (showLog && clearLog)
                InstallLog.Clear();
            MirrorSelector.Visibility = Visibility.Collapsed;
            ActionButtons.Visibility = Visibility.Collapsed;
            SecondaryActionButton.Visibility = Visibility.Collapsed;
            StartElapsed();
        }

        private void ShowNoNode()
        {
            StopElapsed();
            StatusText.Text = "未检测到 Node.js（npm）。\ndsh 依赖 Node.js 才能安装和运行。";
            InstallProgress.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            SetActions("下载 Node.js", OpenNodeDownload, "重新检测", RetryAsync);
        }

        private void ShowNotInstalled()
        {
            StopElapsed();
            StatusText.Text = "未检测到 DeepSeek Harness（dsh）。\n需要安装后才能使用。";
            InstallProgress.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;

            var useMirror = _settings.Settings.NpmRegistry == SettingsViewModel.MirrorRegistry;
            MirrorOfficial.IsChecked = !useMirror;
            MirrorNpmmirror.IsChecked = useMirror;
            MirrorSelector.Visibility = Visibility.Visible;

            SetActions("立即安装", InstallDshAsync, "取消", CancelInstall);
        }

        private void ShowFailed(
            string message,
            string primaryText = "重试",
            Action? primary = null,
            string? secondaryText = null,
            Action? secondary = null)
        {
            StopElapsed();
            StatusText.Text = message;
            InstallProgress.Visibility = Visibility.Collapsed;
            // 保留日志便于排查。
            SetActions(primaryText, primary ?? RetryAsync, secondaryText, secondary);
        }

        private void ShowFatal(string message)
        {
            StopElapsed();
            StatusText.Text = message;
            InstallProgress.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            SetActions("退出", ExitApp);
        }

        private void SetActions(string primaryText, Action primary, string? secondaryText = null, Action? secondary = null)
        {
            PrimaryActionButton.Content = primaryText;
            _primaryAction = primary;
            if (secondaryText != null && secondary != null)
            {
                SecondaryActionButton.Content = secondaryText;
                _secondaryAction = secondary;
                SecondaryActionButton.Visibility = Visibility.Visible;
            }
            else
            {
                SecondaryActionButton.Visibility = Visibility.Collapsed;
            }
            ActionButtons.Visibility = Visibility.Visible;
        }

        private async void RetryAsync() => await RunStartupFlowAsync();

        private string GetSelectedRegistry() =>
            MirrorNpmmirror.IsChecked == true
                ? SettingsViewModel.MirrorRegistry
                : SettingsViewModel.OfficialRegistry;

        private void ExitApp()
        {
            _viewModel.AllowRealClose = true;
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

            StatusText.Text = "请在浏览器中安装 Node.js（LTS）。\n安装完成后点击「重新检测」。";
        }

        private void OnPrimaryActionClick(object sender, RoutedEventArgs e) => _primaryAction?.Invoke();

        private void OnSecondaryActionClick(object sender, RoutedEventArgs e) => _secondaryAction?.Invoke();

        private void CancelInstall()
        {
            StopElapsed();
            StatusText.Text = "已取消安装。\n可点击「重试」再次安装。";
            InstallProgress.Visibility = Visibility.Collapsed;
            LogContainer.Visibility = Visibility.Collapsed;
            MirrorSelector.Visibility = Visibility.Collapsed;
            SetActions("重试", RetryAsync);
        }

        private void StartElapsed()
        {
            _elapsedStart = DateTime.UtcNow;
            ElapsedText.Text = "已用 0 秒";
            _elapsedTimer.Start();
            SetTaskbarProgressIndeterminate();
        }

        private void StopElapsed()
        {
            _elapsedTimer.Stop();
            ClearTaskbarProgress();
        }

        private void SetTaskbarProgressIndeterminate()
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        }

        private void ClearTaskbarProgress()
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }

        private void UpdateElapsed()
        {
            ElapsedText.Text = $"已用 {(int)(DateTime.UtcNow - _elapsedStart).TotalSeconds} 秒";
        }

        private void AppendLogLine(string line)
        {
            if (string.IsNullOrEmpty(line))
                return;

            InstallLog.AppendText(line + Environment.NewLine);
            InstallLog.CaretIndex = InstallLog.Text.Length;
            InstallLog.ScrollToEnd();
        }

        private async void Navigate()
        {
            // 窗口未显示（如静默自启）时推迟加载页面：等窗口首次显示（IsVisibleChanged）再执行，
            // 避免在隐藏/未布局状态下创建 WebView2，也保证打开窗口时看到的是加载面板而非空白。
            if (!IsVisible)
            {
                _navigateWhenShown = true;
                return;
            }

            // 已有页面在加载：直接忽略，避免二次导航打断首次导航（WebView2 报 ConnectionAborted）。
            if (_pendingPageLoad)
                return;

            // 鉴权统一入口：0.1.2+ 需要一次性令牌（自启解析 / 外部粘贴），0.1.1 直接通过。
            if (!await EnsureAuthTokenAsync())
                return;

            _healthTimer.Start();
            _pendingPageLoad = true;
            _navigationRetried = false;
            _pageLoadTimer.Stop();
            _pageLoadTimer.Start();

            // dsh 端口刚响应不代表页面已渲染：创建 WebView2、保持加载面板，
            // 等 NavigationCompleted 再切到 WebView，避免出现「空 WebView2 / 白屏」。
            LogWebView($"Navigate 开始 IsVisible={IsVisible} IsLoaded={IsLoaded} WebViewVisible={WebView.Visibility}");
            ShowLoading("正在加载 DeepSeek Harness 界面…", showLog: false);
            if (!await EnsureWebViewReadyAsync())
            {
                _pendingPageLoad = false;
                _pageLoadTimer.Stop();
                ShowFatal("WebView2 运行时不可用，请先安装 Microsoft Edge WebView2 Runtime。");
                return;
            }

            LogWebView($"开始导航 {_dsh.NavigateUrl} CoreWebView2={(WebView.CoreWebView2 != null ? "ok" : "null")}");
            WebView.CoreWebView2?.Navigate(_dsh.NavigateUrl);
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            // 窗口首次渲染完成（含静默自启后首次托盘打开）：若有待执行的导航则执行。
            // 单个持久订阅 + 一次性标记：无论 IsVisibleChanged / ContentRendered 触发多少次，
            // 每次待导航只会执行一次，从根本上杜绝二次导航打断首次导航（ConnectionAborted）。
            if (_navigateWhenShown)
            {
                _navigateWhenShown = false;
                Navigate();
            }
        }

        private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (!_pendingPageLoad)
                return;

            _pendingPageLoad = false;
            _pageLoadTimer.Stop();

            if (e.IsSuccess)
            {
                StopElapsed();
                LoadingPanel.Visibility = Visibility.Collapsed;
                WebView.Visibility = Visibility.Visible;
            }
            else if (!_navigationRetried)
            {
                // 首次加载失败多为 WebView2 冷启动/窗口刚渲染的竞态：自动重试一次，避免直接弹失败提示。
                _navigationRetried = true;
                LogWebView($"页面加载失败(首次，将重试) WebErrorStatus={e.WebErrorStatus} URL={_dsh.Url}");
                ShowLoading("页面加载失败，正在重试…", showLog: false);
                _pendingPageLoad = true;
                WebView.CoreWebView2?.Navigate(_dsh.NavigateUrl);
                _pageLoadTimer.Stop();
                _pageLoadTimer.Start();
            }
            else
            {
                LogWebView($"页面加载失败(重试后仍失败) WebErrorStatus={e.WebErrorStatus} URL={_dsh.Url}");
                // 鉴权被拒且没有令牌：引导用户粘贴启动地址，而不是停在「页面加载失败」。
                if (_dsh.AccessToken == null && await _dsh.IsAuthRequiredAsync())
                {
                    ShowTokenInput();
                    return;
                }
                ShowFailed("页面加载失败，请检查 dsh 服务后重试。", "重试", RetryAsync);
            }
        }

        /// <summary>把 WebView2 关键节点写入 %LOCALAPPDATA%\DshGUI\webview.log，便于定位加载失败。</summary>
        private void LogWebView(string message)
        {
            try
            {
                var path = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshGUI", "webview.log");
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // 日志写失败不影响运行。
            }
        }

        private void OnPageLoadTimeout()
        {
            if (!_pendingPageLoad)
                return;

            // 页面迟迟未完成导航（如服务挂起）：不再让用户干等，直接显示 WebView，健康检查会接管。
            _pendingPageLoad = false;
            _pageLoadTimer.Stop();
            StopElapsed();
            LoadingPanel.Visibility = Visibility.Collapsed;
            WebView.Visibility = Visibility.Visible;
        }

        private void CancelPendingPageLoad()
        {
            _pendingPageLoad = false;
            _pageLoadTimer.Stop();
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
            // 更新前先停掉 dsh，再复用启动/安装面板显示 npm install 的完整日志和进度。
            _healthTimer.Stop();
            CancelPendingPageLoad();
            _viewModel.ShowMainWindow();
            WebView.Visibility = Visibility.Collapsed;
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
                ShowFailed("更新前需要停止 dsh，但当前 dsh 不是由 DshGUI 启动的（外部实例）或端口仍被占用。\n请手动停止后重试。", "重试更新", () => RunUpdateAsync(distTag), "启动 dsh", RetryAsync);
                return;
            }

            AppendLogLine("—— 开始更新 ——");
            AppendLogLine($"npm install -g @deepseek-ai/dsh@{distTag} --registry {_settings.Settings.NpmRegistry} --no-fund --no-audit --loglevel=http");
            var ok = await _dsh.InstallAsync(_settings.Settings.NpmRegistry, new Progress<string>(AppendLogLine), distTag);
            if (!ok)
            {
                ShowFailed($"更新失败。请查看上方日志，或手动执行 npm install -g @deepseek-ai/dsh@{distTag}。", "重试更新", () => RunUpdateAsync(distTag), "启动 dsh", RetryAsync);
                return;
            }

            AppendLogLine("—— 更新完成，正在重新启动 ——");
            await StartDshAndWaitAsync();
        }


        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using var doc = JsonDocument.Parse(e.WebMessageAsJson);
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
                        _viewModel.RefreshTheme();
                        break;

                    case "title" when doc.RootElement.TryGetProperty("text", out var text):
                        var title = text.GetString();
                        _viewModel.WindowTitle = string.IsNullOrWhiteSpace(title)
                            ? "DeepSeek Harness"
                            : title;
                        break;
                }
            }
            catch
            {
                // 忽略无法解析的消息。
            }
        }

        private void HandleAgentIdle()
        {
            if (!_settings.Settings.NotifyOnComplete)
                return;

            // 仅当窗口最小化或缩到托盘时提醒。
            if (WindowState == WindowState.Minimized || !IsVisible)
            {
                var title = _viewModel.WindowTitle;
                var message = string.IsNullOrWhiteSpace(title) || title == "DeepSeek Harness"
                    ? "回到窗口查看结果"
                    : title;
                _notification.Show("任务已完成", message, _viewModel.ShowMainWindow);
            }
        }

        private void HandleApprovalRequested()
        {
            // 仅当窗口不在前台时提醒，避免打扰正在看页面的人。
            if (WindowState == WindowState.Minimized || !IsVisible || !IsActive)
            {
                // 持久显示，直到用户点击或审批被处理。
                _approvalToast = _notification.Show(
                    "需要你的批准",
                    "有权限请求等待处理，点击返回窗口",
                    _viewModel.ShowMainWindow,
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
                        ReconnectDsh,
                        persistent: true);
                }
            }
            finally
            {
                _healthChecking = false;
            }
        }

        private async void ReconnectDsh()
        {
            _disconnectToast = null;
            _healthTimer.Stop();
            CancelPendingPageLoad();
            WebView.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;

            // WebView2 控制器由 Navigate() 在需要时创建/确保。
            await RunStartupFlowAsync();
        }

        private void ToggleVisibility()
        {
            if (IsVisible && WindowState != WindowState.Minimized)
                Hide();
            else
                _viewModel.ShowMainWindow();
        }

        private void ApplyHotkeyRegistration()
        {
            if (_hwnd == IntPtr.Zero)
                return;

            UnregisterHotKey(_hwnd, HotkeyId);
            if (_settings.Settings.HotkeyEnabled)
                RegisterHotKey(_hwnd, HotkeyId, _settings.Settings.HotkeyModifiers, _settings.Settings.HotkeyKey);
        }

        private void OnMinimizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void OnMaximizeClick(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
        }

        private void OnStateChanged(object? sender, EventArgs e)
        {
            MaximizeIconPath.Data = Geometry.Parse(
                WindowState == WindowState.Maximized ? RestoreIconData : MaximizeIconData);
        }

        private void OnPinClick(object sender, RoutedEventArgs e)
        {
            Topmost = !Topmost;
            _viewModel.IsTopmost = Topmost;
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            SaveWindowState();
            Hide();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            // 再点一次齿轮：已打开则关闭，未打开则打开。
            if (SettingsViewControl.Visibility == Visibility.Visible)
                CloseSettings();
            else
                OpenSettings();
        }

        private void OnPluginManagerClick(object sender, RoutedEventArgs e) => OpenPluginManager();

        /// <summary>打开插件操作台；dsh 崩溃/卡死时也可用。</summary>
        public void OpenPluginManager()
        {
            try
            {
                if (_pluginWindow is { IsLoaded: true })
                {
                    _pluginWindow.Show();
                    _pluginWindow.Activate();
                    return;
                }

                _pluginManager ??= new PluginManagerService(_dsh, RestartManagedDshAsync);
                _pluginPackage ??= new PluginPackageService(_pluginManager);
                _pluginWindow = new PluginManagerWindow(
                    _pluginManager, _pluginPackage, _dsh, RestartManagedDshAsync, this, _theme);
                _pluginWindow.Closed += (_, _) => _pluginWindow = null;
                _pluginWindow.Show();
            }
            catch (Exception ex)
            {
                var errorPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DshGUI", "plugin-manager-error.log");
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(errorPath)!);
                    File.WriteAllText(errorPath, DateTime.Now + Environment.NewLine + ex);
                }
                catch
                {
                    // 写诊断文件失败时忽略。
                }

                System.Windows.MessageBox.Show(this, "插件管理窗口打开失败：\n" + ex.Message, "DshGUI",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>供插件操作台在屏蔽/卸载/撤销后重启 dsh。</summary>
        public Task RestartManagedDshAsync() => RunStartupFlowAsync();

        private void OpenSettings()
        {
            CloseSettings();

            _settingsViewModel = new SettingsViewModel(_settings, _dsh)
            {
                NoticeCallback = (title, message) =>
                {
                    System.Windows.MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
                    return true;
                },
                PortOccupiedCallback = port =>
                    System.Windows.MessageBox.Show(
                        this,
                        $"端口 {port} 已被占用。\n\n若继续保存，DshGUI 会连接该端口上的现有服务，"
                        + "不再自行启动新的 dsh 实例。\n\n仍要保存吗？",
                        "端口已被占用",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning) == MessageBoxResult.Yes,
            };
            _settingsViewModel.RequestClose += CloseSettings;
            _settingsViewModel.SettingsChanged += OnSettingsChanged;
            _settingsViewModel.UpdateRequested += RunUpdateAsync;
            _settingsViewModel.RestartRequested += OnRestartDshRequested;
            SettingsViewControl.DataContext = _settingsViewModel;
            SettingsViewControl.Visibility = Visibility.Visible;
        }

        private async void OnRestartDshRequested()
        {
            CloseSettings();
            _healthTimer.Stop();
            CancelPendingPageLoad();
            _dsh.SetPort(_settings.Settings.DshPort);   // 应用设置面板里修改的端口
            _dsh.Stop();                                // 只停 DshGUI 自己启动的实例
            _dsh.ClearAccessToken();
            WebView.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            await RunStartupFlowAsync();
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

            SettingsViewControl.DataContext = null;
            SettingsViewControl.Visibility = Visibility.Collapsed;
        }

        private async void OnSettingsChanged()
        {
            _theme.SetPageDark(null);
            _theme.ApplyToWebView(WebView.CoreWebView2);
            _viewModel.RefreshTheme();
            ApplyHotkeyRegistration();

            var portChanged = _settings.Settings.DshPort != _dsh.Port;
            if (!portChanged)
            {
                WebView.CoreWebView2?.Reload();
                return;
            }

            // 端口变更：更新 DshService，停止旧端口上的 dsh，并按新端口重启连接流程。
            _dsh.SetPort(_settings.Settings.DshPort);
            _dsh.Stop();
            CancelPendingPageLoad();
            WebView.Visibility = Visibility.Collapsed;
            LoadingPanel.Visibility = Visibility.Visible;
            await RunStartupFlowAsync();
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_viewModel.AllowRealClose)
            {
                SaveWindowState();
                if (_hwnd != IntPtr.Zero)
                    UnregisterHotKey(_hwnd, HotkeyId);
                _pluginManager?.Dispose();
                _pluginManager = null;
                return;
            }

            e.Cancel = true;
            Hide();
        }
    }
}
