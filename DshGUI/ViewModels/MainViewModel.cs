using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using DshGUI.Services;

namespace DshGUI.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly ThemeService _theme;

    private bool _allowRealClose;

    public MainViewModel(ThemeService theme)
    {
        _theme = theme;
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
}
