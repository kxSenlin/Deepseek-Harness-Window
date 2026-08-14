using System.Windows;
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

    /// <summary>把当前主题的颜色字典合并进应用资源并替换上一套；所有 DynamicResource 自动刷新。</summary>
    public void ApplyTheme()
    {
        var dark = IsDark;
        if (_appliedDark == dark && _current != null)
            return;

        var resources = Application.Current.Resources;
        if (_current != null)
            resources.MergedDictionaries.Remove(_current);

        _current = new ResourceDictionary { Source = dark ? DarkSource : LightSource };
        resources.MergedDictionaries.Add(_current);
        _appliedDark = dark;
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
