namespace DshGUI.Models;

public enum ThemePreference
{
    System,
    Light,
    Dark,
}

public sealed class AppSettings
{
    public ThemePreference Theme { get; set; } = ThemePreference.System;

    /// <summary>npm 镜像源（安装 / 更新 / 检查更新共用）。</summary>
    public string NpmRegistry { get; set; } = "https://registry.npmjs.org";

    /// <summary>dsh 监听端口，DshGUI 通过该端口连接并拉起 dsh。</summary>
    public int DshPort { get; set; } = 3080;

    /// <summary>agent 完成时弹通知（仅最小化/托盘时）。</summary>
    public bool NotifyOnComplete { get; set; } = true;

    /// <summary>开机自启。</summary>
    public bool AutoStart { get; set; }

    /// <summary>开机自启时静默到托盘（不弹窗口）。</summary>
    public bool AutoStartSilent { get; set; }

    /// <summary>全局快捷键是否启用（默认关）。</summary>
    public bool HotkeyEnabled { get; set; }

    /// <summary>快捷键修饰键位掩码（Win32 MOD_*，默认 Ctrl+Alt）。</summary>
    public int HotkeyModifiers { get; set; } = 0x0002 | 0x0001;

    /// <summary>快捷键主键（Win32 VK_*，默认 D）。</summary>
    public int HotkeyKey { get; set; } = 0x44;

    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public bool HasWindowBounds => WindowWidth > 0 && WindowHeight > 0;
}
