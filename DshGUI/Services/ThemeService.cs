using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using DshGUI.Models;

namespace DshGUI.Services;

public sealed class ThemeService
{
    private readonly SettingsService _settings;

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

    public Brush TitleBarBackground => new SolidColorBrush(IsDark
        ? Color.FromRgb(0x1E, 0x1E, 0x1E)
        : Color.FromRgb(0xF3, 0xF3, 0xF3));

    public Brush TitleBarForeground => new SolidColorBrush(IsDark
        ? Colors.White
        : Color.FromRgb(0x1E, 0x1E, 0x1E));

    public Brush ContentBackground => new SolidColorBrush(IsDark
        ? Color.FromRgb(0x1E, 0x1E, 0x1E)
        : Colors.White);

    public System.Drawing.Color WebViewBackgroundColor => IsDark
        ? System.Drawing.Color.FromArgb(0x1E, 0x1E, 0x1E)
        : System.Drawing.Color.White;

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
