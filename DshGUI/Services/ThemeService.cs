using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using DshGUI.Models;

namespace DshGUI.Services;

public sealed class ThemeService
{
    private static readonly Uri LightSource = new("pack://application:,,,/DshGUI;component/Themes/Colors.Light.xaml");
    private static readonly Uri DarkSource = new("pack://application:,,,/DshGUI;component/Themes/Colors.Dark.xaml");

    private readonly SettingsService _settings;
    private ResourceDictionary? _current;
    private bool? _appliedDark;

    public ThemeService(SettingsService settings)
    {
        _settings = settings;
    }

    public bool IsDark => _pageDark ?? EffectiveScheme == "dark";

    private bool? _pageDark;

    /// <summary>网页内部切换主题时由 JS 回调设置；置 null 则回到壳自己的主题设置。</summary>
    public void SetPageDark(bool? dark) => _pageDark = dark;

    public string EffectiveScheme => _settings.Settings.Theme switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => SystemUsesDarkTheme() ? "dark" : "light",
    };

    public System.Drawing.Color WebViewBackgroundColor => IsDark
        ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
        : System.Drawing.Color.White;

    /// <summary>把当前主题的颜色字典合并进应用资源并替换上一套；所有 DynamicResource 自动刷新。返回是否真的发生了切换。</summary>
    public bool ApplyTheme()
    {
        var dark = IsDark;
        if (_appliedDark == dark && _current != null)
            return false;

        var resources = Application.Current.Resources;
        if (_current != null)
            resources.MergedDictionaries.Remove(_current);

        _current = new ResourceDictionary { Source = dark ? DarkSource : LightSource };
        resources.MergedDictionaries.Add(_current);
        _appliedDark = dark;
        return true;
    }

    /// <summary>主题真正切换后触发（用于同步 WebView2 等外部对象）。</summary>
    public event Action? ThemeChanged;

    /// <summary>监听系统深浅色切换，实时跟随（仅「跟随系统」模式生效）。</summary>
    public void StartSystemThemeWatcher()
    {
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category != UserPreferenceCategory.General)
                return;
            Application.Current?.Dispatcher.InvokeAsync(OnSystemThemeChanged);
        };
    }

    private void OnSystemThemeChanged()
    {
        if (_settings.Settings.Theme != ThemePreference.System)
            return;

        _pageDark = null;
        if (ApplyTheme())
            ThemeChanged?.Invoke();
    }

    public void ApplyToWebView(CoreWebView2? core)
    {
        if (core == null)
            return;

        core.Profile.PreferredColorScheme = _settings.Settings.Theme switch
        {
            ThemePreference.Light => CoreWebView2PreferredColorScheme.Light,
            ThemePreference.Dark => CoreWebView2PreferredColorScheme.Dark,
            _ => CoreWebView2PreferredColorScheme.Auto,
        };
    }

    /// <summary>把系统标题栏（插件管理/确认弹窗）同步为当前深色/浅色。</summary>
    public void ApplyWindowTitleBar(Window window)
    {
        void Apply()
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            var enabled = IsDark ? 1 : 0;
            // Windows 11/10 20H1 使用属性 20；旧版 10 使用属性 19。
            _ = DwmSetWindowAttribute(hwnd, 20, ref enabled, Marshal.SizeOf(typeof(int)));
            _ = DwmSetWindowAttribute(hwnd, 19, ref enabled, Marshal.SizeOf(typeof(int)));
        }

        window.SourceInitialized += (_, _) => Apply();
        Apply();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    public static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
