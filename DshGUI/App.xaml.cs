using System.Windows;
using DshGUI.Services;
using DshGUI.ViewModels;
using MainWindowView = DshGUI.Views.MainWindow;

namespace DshGUI
{
    public partial class App : Application
    {
        private SettingsService? _settings;
        private ThemeService? _theme;
        private DshService? _dsh;
        private MainViewModel? _viewModel;
        private MainWindowView? _mainWindow;
        private TrayService? _tray;
        private SingleInstanceService? _singleInstance;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            _singleInstance = new SingleInstanceService();
            if (!_singleInstance.IsFirstInstance)
            {
                _singleInstance.SignalExistingInstance();
                Shutdown();
                return;
            }

            _settings = new SettingsService();
            _settings.Load();

            // 旧版本把开机自启写在 HKCU\...\Run；升级后迁移到 Startup 文件夹快捷方式，避免旧注册表项继续生效。
            AutoStartService.MigrateLegacyRunKey(_settings.Settings.AutoStart);

            // 旧配置或手改 settings.json 可能保存了不安全的端口；启动时回退到 3080。
            if (DshService.GetPortError(_settings.Settings.DshPort) != null)
            {
                _settings.Settings.DshPort = 3080;
                _settings.Save();
            }

            _theme = new ThemeService(_settings);
            _theme.ApplyTheme();
            _theme.StartSystemThemeWatcher();
            _dsh = new DshService(_settings.Settings.DshPort);
            _viewModel = new MainViewModel(_theme);

            _tray = new TrayService();

            var silent = e.Args.Contains("--autostart") && _settings.Settings.AutoStartSilent;
            _mainWindow = new MainWindowView(_viewModel, _settings, _dsh, _theme, _tray)
            {
                StartSilent = silent,
            };
            MainWindow = _mainWindow;
            _tray.OpenRequested += () => _mainWindow.Dispatcher.Invoke(() => _viewModel.ShowMainWindow());
            _tray.PluginManagerRequested += () => _mainWindow.Dispatcher.Invoke(() => _mainWindow.OpenPluginManager());
            _tray.ExitRequested += () => _mainWindow.Dispatcher.Invoke(() =>
            {
                _viewModel.AllowRealClose = true;
                Shutdown();
            });

            _singleInstance.Listen(() => _mainWindow.Dispatcher.Invoke(() => _viewModel.ShowMainWindow()));

            _mainWindow.Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            _dsh?.Stop();
            _tray?.Dispose();
            _singleInstance?.Dispose();
            base.OnExit(e);
        }
    }
}
