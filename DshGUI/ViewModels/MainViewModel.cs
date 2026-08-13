using System.Windows;
using System.Windows.Media;
using DshGUI.Services;

namespace DshGUI.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ThemeService _theme;

    private string _statusText = "正在启动 DeepSeek Harness…";
    private bool _allowRealClose;

    public MainViewModel(ThemeService theme)
    {
        _theme = theme;
    }

    public string StatusText
    {
        get => _statusText;
        set => SetProperty(ref _statusText, value);
    }

    private string _windowTitle = "DeepSeek Harness";

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

    public Brush TitleBarBackground => _theme.TitleBarBackground;

    public Brush TitleBarForeground => _theme.TitleBarForeground;

    public Brush ContentBackground => _theme.ContentBackground;

    public void ShowMainWindow()
    {
        if (Application.Current.MainWindow is not { } window)
            return;

        window.Show();
        if (window.WindowState == WindowState.Minimized)
            window.WindowState = WindowState.Normal;
        window.Activate();
    }

    public void RefreshTheme()
    {
        OnPropertyChanged(nameof(TitleBarBackground));
        OnPropertyChanged(nameof(TitleBarForeground));
        OnPropertyChanged(nameof(ContentBackground));
    }
}
