using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
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

        private readonly MainViewModel _viewModel;
        private readonly SettingsService _settings;
        private readonly ThemeService _theme;
        private readonly DispatcherTimer _pageLoadTimer = new() { Interval = TimeSpan.FromSeconds(20) };

        private bool _pendingPageLoad;
        private bool _navigateWhenShown;
        private bool _navigationRetried;
        private bool _navigating;

        private IntPtr _hwnd;
        private CoreWebView2? _wiredCore;
        private PluginManagerService? _pluginManager;
        private PluginPackageService? _pluginPackage;
        private PluginManagerWindow? _pluginWindow;

        public MainWindow(MainViewModel viewModel, SettingsService settings, ThemeService theme)
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
            _theme = theme;
            DataContext = viewModel;

            TitleIcon.Source = IconHelper.GetAppIcon(16);
            _pageLoadTimer.Tick += (_, _) => OnPageLoadTimeout();
            WebView.NavigationCompleted += OnNavigationCompleted;
            ContentRendered += OnContentRendered;
            TaskbarItemInfo = new System.Windows.Shell.TaskbarItemInfo();

            _theme.ThemeChanged += () => _theme.ApplyToWebView(WebView.CoreWebView2);
            _viewModel.NavigateRequested += OnNavigateRequested;
            _viewModel.NavigationResetRequested += CancelPendingPageLoad;
            _viewModel.PluginManagerRequested += OpenPluginManager;
            _viewModel.SettingsApplied += ApplyHotkeyRegistration;
            _viewModel.ReloadRequested += ReloadWebView;
            _viewModel.ElapsedStarted += SetTaskbarProgressIndeterminate;
            _viewModel.ElapsedStopped += ClearTaskbarProgress;

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

        private async void OnLoaded(object sender, RoutedEventArgs e) => await _viewModel.StartAsync();

        /// <summary>静默自启入口：窗口从不显示（无 Show/Hide、无开机闪现），后台直接跑启动流程。</summary>
        public void RunStartupInBackground()
        {
            // 创建隐藏 HWND（触发 OnSourceInitialized：全局热键、DWM 样式），但不显示窗口；
            // WebView2 与页面加载推迟到窗口首次显示（见 Navigate 的 IsVisible 守卫）时再进行。
            _ = new WindowInteropHelper(this).EnsureHandle();
            _ = _viewModel.StartAsync();
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
            {
                LogWebView("EnsureCoreWebView2Async 完成但 CoreWebView2 为 null");
            }
            else if (!ReferenceEquals(core, _wiredCore))
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
                core.WebMessageReceived += OnWebMessageReceived;
                _wiredCore = core;
            }

            return true;
        }

        private async void OnNavigateRequested() => await NavigateAsync();

        private async Task NavigateAsync()
        {
            // 窗口未显示（如静默自启）时推迟加载页面：等窗口首次显示（ContentRendered）再执行，
            // 避免在隐藏/未布局状态下创建 WebView2，也保证打开窗口时看到的是加载面板而非空白。
            if (!IsVisible)
            {
                _navigateWhenShown = true;
                return;
            }

            // 防重入：窗口显示时 OnContentRendered 与 OnLoaded 的启动流程会并发触发导航，第一次可能
            // 仍在 await 初始化 WebView2（此时 _pendingPageLoad 尚未置位）。锁必须在任何 await 之前置位，
            // 否则会发出两个 Navigate 竞争 → ConnectionAborted（表现为“首次必失败，重试才进”）。
            if (_navigating)
                return;
            _navigating = true;
            try
            {
                // 新导航请求优先于旧导航：取消尚未完成的页面加载，避免旧 NavigationCompleted 干扰。
                if (_pendingPageLoad)
                    CancelPendingPageLoad();

                // 鉴权统一入口：0.1.2+ 需要一次性令牌（自启解析 / 外部粘贴），0.1.1 直接通过。
                if (!await _viewModel.EnsureAuthTokenAsync())
                    return;

                _viewModel.StartHealthChecking();
                _pendingPageLoad = true;
                _navigationRetried = false;
                _pageLoadTimer.Stop();
                _pageLoadTimer.Start();

                // dsh 端口刚响应不代表页面已渲染：创建 WebView2、保持加载面板，
                // 等 NavigationCompleted 再切到 WebView，避免出现「空 WebView2 / 白屏」。
                LogWebView($"Navigate 开始 IsVisible={IsVisible} IsLoaded={IsLoaded} WebViewVisible={WebView.Visibility}");
                _viewModel.ShowLoading("正在加载 DeepSeek Harness 界面…", showLog: false);
                if (!await EnsureWebViewReadyAsync())
                {
                    _pendingPageLoad = false;
                    _pageLoadTimer.Stop();
                    _viewModel.ShowFatal("WebView2 运行时不可用，请先安装 Microsoft Edge WebView2 Runtime。");
                    return;
                }

                LogWebView($"开始导航 {_viewModel.NavigateUrl} CoreWebView2={(WebView.CoreWebView2 != null ? "ok" : "null")}");
                WebView.CoreWebView2?.Navigate(_viewModel.NavigateUrl);
            }
            finally
            {
                _navigating = false;
            }
        }

        private void OnContentRendered(object? sender, EventArgs e)
        {
            // 窗口首次渲染完成（含静默自启后首次托盘打开）：若有待执行的导航则执行。
            if (_navigateWhenShown)
            {
                _navigateWhenShown = false;
                _ = NavigateAsync();
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
                _viewModel.ShowWebView();
            }
            else if (!_navigationRetried)
            {
                // 首次加载失败多为 WebView2 冷启动/窗口刚渲染的竞态：自动重试一次，避免直接弹失败提示。
                _navigationRetried = true;
                LogWebView($"页面加载失败(首次，将重试) WebErrorStatus={e.WebErrorStatus} URL={_viewModel.NavigateUrl}");
                _viewModel.ShowLoading("页面加载失败，正在重试…", showLog: false);
                _pendingPageLoad = true;
                WebView.CoreWebView2?.Navigate(_viewModel.NavigateUrl);
                _pageLoadTimer.Stop();
                _pageLoadTimer.Start();
            }
            else
            {
                LogWebView($"页面加载失败(重试后仍失败) WebErrorStatus={e.WebErrorStatus} URL={_viewModel.NavigateUrl}");
                await _viewModel.HandlePageLoadFailedAsync();
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
            _viewModel.ShowWebView();
        }

        private void CancelPendingPageLoad()
        {
            _pendingPageLoad = false;
            _pageLoadTimer.Stop();
        }

        private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                _viewModel.HandleWebMessageJson(e.WebMessageAsJson);
            }
            catch
            {
                // 忽略无法解析的消息。
            }
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

        private void ReloadWebView() => WebView.CoreWebView2?.Reload();

        private void SetTaskbarProgressIndeterminate()
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.Indeterminate;
        }

        private void ClearTaskbarProgress()
        {
            TaskbarItemInfo.ProgressState = System.Windows.Shell.TaskbarItemProgressState.None;
        }

        private void OnInstallLogTextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            InstallLog.CaretIndex = InstallLog.Text.Length;
            InstallLog.ScrollToEnd();
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

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            SaveWindowState();
            Hide();
        }

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

                var dsh = _viewModel.DshService;
                _pluginManager ??= new PluginManagerService(dsh, _viewModel.RestartManagedDshAsync);
                _pluginPackage ??= new PluginPackageService(_pluginManager);
                _pluginWindow = new PluginManagerWindow(
                    _pluginManager, _pluginPackage, dsh, _viewModel.RestartManagedDshAsync, this, _theme);
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
