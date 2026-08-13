using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
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

        private const string MaximizeIconData = "M 1.5,1.5 H 8.5 V 8.5 H 1.5 Z";
        private const string RestoreIconData = "M 3,1 H 9 V 7 H 3 Z M 1,3 H 7 V 9 H 1 Z";

        private static readonly Brush PinActiveBrush = new SolidColorBrush(Color.FromRgb(0x4D, 0x6B, 0xFE));

        // 注入到 dsh 页面：监听「运行中→空闲」「深色主题」「当前会话标题」三个状态。
        private const string BridgeScript = @"(function () {
    const post = (m) => window.chrome.webview.postMessage(m);
    let wasRunning = false;
    let idleTimer = null;
    let lastDark = null;
    let lastTitle = null;

    function currentTitle() {
        const el = document.querySelector('nav button[disabled]');
        return el ? el.textContent.trim() : null;
    }

    setInterval(() => {
        const running = document.querySelectorAll('[data-state=ongoing], [data-running]').length > 0;
        if (wasRunning && !running && !idleTimer) {
            idleTimer = setTimeout(() => {
                idleTimer = null;
                if (document.querySelectorAll('[data-state=ongoing], [data-running]').length === 0) {
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

        // dsh 的工作目录 = agent 的 workspace 根目录（壳不提供选择器，固定用主目录）。
        private static readonly string WorkspaceDirectory =
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(60);

        private readonly MainViewModel _viewModel;
        private readonly SettingsService _settings;
        private readonly DshService _dsh;
        private readonly ThemeService _theme;
        private readonly NotificationService _notification = new();
        private readonly UpdateService _update = new();

        private IntPtr _hwnd;
        private bool _updateChecked;

        public MainWindow(MainViewModel viewModel, SettingsService settings, DshService dsh, ThemeService theme)
        {
            InitializeComponent();
            _viewModel = viewModel;
            _settings = settings;
            _dsh = dsh;
            _theme = theme;
            DataContext = viewModel;

            TitleIcon.Source = IconHelper.GetAppIcon(16);
            RestoreWindowState();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            _hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

            if (_settings.Settings.HotkeyEnabled)
                RegisterHotKey(_hwnd, HotkeyId, _settings.Settings.HotkeyModifiers, _settings.Settings.HotkeyKey);
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

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                WebView.DefaultBackgroundColor = _theme.WebViewBackgroundColor;
                await WebView.EnsureCoreWebView2Async();
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "WebView2 运行时不可用，请先安装 Microsoft Edge WebView2 Runtime。\n\n" + ex.Message,
                    "DshGUI",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                _viewModel.AllowRealClose = true;
                Application.Current.Shutdown();
                return;
            }

            _theme.ApplyToWebView(WebView.CoreWebView2);
            _viewModel.RefreshTheme();

            var core = WebView.CoreWebView2;
            if (core != null)
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);
                core.WebMessageReceived += OnWebMessageReceived;
            }

            if (await _dsh.IsServerUpAsync())
            {
                Navigate();
                return;
            }

            if (!DshService.IsInstalled())
            {
                var answer = MessageBox.Show(
                    "未检测到 DeepSeek Harness（dsh）。\n是否现在自动安装？",
                    "DshGUI",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Question);

                if (answer == MessageBoxResult.No)
                {
                    _viewModel.StatusText = "已取消安装。请手动执行 npm install -g @deepseek-ai/dsh 后重开。";
                    return;
                }

                _viewModel.StatusText = "正在安装 DeepSeek Harness（npm install -g @deepseek-ai/dsh）…";
                var installed = await _dsh.InstallAsync();
                if (!installed)
                {
                    _viewModel.StatusText = "安装失败。请手动执行 npm install -g @deepseek-ai/dsh。";
                    return;
                }
            }

            _viewModel.StatusText = "正在启动 DeepSeek Harness…";

            if (!_dsh.Start(WorkspaceDirectory))
            {
                _viewModel.StatusText = "无法启动 dsh。请确认已安装：npm install -g @deepseek-ai/dsh";
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
                _viewModel.StatusText = _dsh.HasExited
                    ? "dsh 启动失败（可能是端口被占用或配置错误）。\n请查看日志：" + DshService.LogPath
                    : "DeepSeek Harness 启动超时（60 秒）。\n请查看日志：" + DshService.LogPath;
                return;
            }

            Navigate();
        }

        private void Navigate()
        {
            WebView.NavigationCompleted += OnNavigationCompleted;
            WebView.CoreWebView2?.Navigate(DshService.Url + "/");
        }

        private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            StatusText.Visibility = Visibility.Collapsed;
            _ = CheckForUpdatesAsync();
        }

        private async Task CheckForUpdatesAsync()
        {
            if (_updateChecked)
                return;
            _updateChecked = true;

            var latest = await _update.GetLatestVersionAsync();
            var installed = UpdateService.GetInstalledVersion();
            if (!UpdateService.IsNewer(latest, installed))
                return;

            _notification.Show("更新可用", $"DeepSeek Harness {latest}（当前 {installed}）", UpdateNow);
        }

        public void TriggerUpdateCheck()
        {
            _updateChecked = false;
            _ = CheckForUpdatesAsync();
        }

        private async void UpdateNow()
        {
            _notification.Show("正在更新", "正在更新 DeepSeek Harness…");
            var ok = await _dsh.InstallAsync();
            _notification.Show(
                ok ? "更新完成" : "更新失败",
                ok ? "请重启应用以生效。" : "请手动执行 npm install -g @deepseek-ai/dsh");
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
                        HandleAgentIdle();
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
            UpdatePinIcon();
        }

        private void UpdatePinIcon()
        {
            if (Topmost)
                PinIcon.Fill = PinActiveBrush;
            else
                PinIcon.ClearValue(System.Windows.Shapes.Shape.FillProperty);
        }

        private void OnCloseClick(object sender, RoutedEventArgs e)
        {
            SaveWindowState();
            Hide();
        }

        private void OnSettingsClick(object sender, RoutedEventArgs e)
        {
            var settingsViewModel = new SettingsViewModel(_settings);
            settingsViewModel.SettingsChanged += () =>
            {
                _theme.SetPageDark(null);
                _theme.ApplyToWebView(WebView.CoreWebView2);
                _viewModel.RefreshTheme();
                WebView.CoreWebView2?.Reload();
                ApplyHotkeyRegistration();
            };

            var dialog = new SettingsWindow(settingsViewModel)
            {
                Owner = this,
            };
            dialog.ShowDialog();
        }

        private void OnClosing(object? sender, CancelEventArgs e)
        {
            if (_viewModel.AllowRealClose)
            {
                SaveWindowState();
                if (_hwnd != IntPtr.Zero)
                    UnregisterHotKey(_hwnd, HotkeyId);
                return;
            }

            e.Cancel = true;
            Hide();
        }
    }
}
