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

            _theme = new ThemeService(_settings);
            _theme.ApplyTheme();
            _theme.StartSystemThemeWatcher();
            _dsh = new DshService();
            _viewModel = new MainViewModel(_theme);

            _tray = new TrayService();

            var silent = e.Args.Contains("--autostart") && _settings.Settings.AutoStartSilent;
            _mainWindow = new MainWindowView(_viewModel, _settings, _dsh, _theme, _tray)
            {
                StartSilent = silent,
            };
            MainWindow = _mainWindow;
            _tray.OpenRequested += () => _mainWindow.Dispatcher.Invoke(() => _viewModel.ShowMainWindow());
            _tray.CheckUpdateRequested += () => _mainWindow.Dispatcher.Invoke(() => _mainWindow.TriggerUpdateCheck());
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
