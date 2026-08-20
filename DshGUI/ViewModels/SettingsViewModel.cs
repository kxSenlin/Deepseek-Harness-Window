using System.Collections.Generic;
using System.Windows.Input;
using DshGUI.Infrastructure;
using DshGUI.Models;
using DshGUI.Services;

namespace DshGUI.ViewModels;

public sealed class SettingsViewModel : ViewModelBase
{
    private const int ModCtrl = 0x0002;
    private const int ModAlt = 0x0001;
    private const int ModShift = 0x0004;
    private const int ModWin = 0x0008;

    public const string OfficialRegistry = "https://registry.npmjs.org";
    public const string MirrorRegistry = "https://registry.npmmirror.com";

    private readonly SettingsService _settings;
    private readonly DshService _dsh;
    private readonly UpdateService _update = new();

    private int _themeIndex;
    private int _registryIndex;
    private string _dshPortText;
    private bool _notifyOnComplete;
    private bool _autoStart;
    private bool _autoStartSilent;
    private bool _hotkeyEnabled;
    private int _hotkeyModifiers;
    private int _hotkeyKey;
    private string _stopDshStatus = "";
    private string _installedVersionText = "";
    private string _updateStatusText = "";
    private bool _canUpdateLatest;
    private bool _canUpdatePreview;

    public SettingsViewModel(SettingsService settings, DshService dsh)
    {
        _settings = settings;
        _dsh = dsh;
        _themeIndex = (int)settings.Settings.Theme;
        _registryIndex = settings.Settings.NpmRegistry == MirrorRegistry ? 1 : 0;
        _dshPortText = settings.Settings.DshPort.ToString();
        _notifyOnComplete = settings.Settings.NotifyOnComplete;
        _autoStart = settings.Settings.AutoStart;
        _autoStartSilent = settings.Settings.AutoStartSilent;
        _hotkeyEnabled = settings.Settings.HotkeyEnabled;
        _hotkeyModifiers = settings.Settings.HotkeyModifiers;
        _hotkeyKey = settings.Settings.HotkeyKey;

        SaveCommand = new RelayCommand(_ => SaveAsync());
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
        StopDshCommand = new RelayCommand(_ => StopDshAsync());
        CheckUpdateCommand = new RelayCommand(_ => _ = CheckUpdateAsync());
        UpdateLatestCommand = new RelayCommand(_ => UpdateRequested?.Invoke("latest"));
        UpdatePreviewCommand = new RelayCommand(_ => UpdateRequested?.Invoke("next"));

        InstalledVersionText = $"当前版本：{UpdateService.GetInstalledVersion() ?? "未知"}";
        UpdateStatusText = "点击「检查更新」获取最新版本";
    }

    /// <summary>UI 注入：普通提示框。</summary>
    public Func<string, string, bool>? NoticeCallback { get; set; }

    /// <summary>UI 注入：保存时端口已被占用，返回 true 表示仍要保存。</summary>
    public Func<int, bool>? PortOccupiedCallback { get; set; }

    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetProperty(ref _themeIndex, value);
    }

    public int RegistryIndex
    {
        get => _registryIndex;
        set => SetProperty(ref _registryIndex, value);
    }

    public string DshPortText
    {
        get => _dshPortText;
        set => SetProperty(ref _dshPortText, value);
    }

    public bool NotifyOnComplete
    {
        get => _notifyOnComplete;
        set => SetProperty(ref _notifyOnComplete, value);
    }

    public bool AutoStart
    {
        get => _autoStart;
        set => SetProperty(ref _autoStart, value);
    }

    public bool AutoStartSilent
    {
        get => _autoStartSilent;
        set => SetProperty(ref _autoStartSilent, value);
    }

    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set => SetProperty(ref _hotkeyEnabled, value);
    }

    public string HotkeyDisplay => FormatHotkey(_hotkeyModifiers, _hotkeyKey);

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand StopDshCommand { get; }

    public string StopDshStatus
    {
        get => _stopDshStatus;
        private set => SetProperty(ref _stopDshStatus, value);
    }

    public string InstalledVersionText
    {
        get => _installedVersionText;
        private set => SetProperty(ref _installedVersionText, value);
    }

    public string UpdateStatusText
    {
        get => _updateStatusText;
        private set => SetProperty(ref _updateStatusText, value);
    }

    public bool CanUpdateLatest
    {
        get => _canUpdateLatest;
        private set => SetProperty(ref _canUpdateLatest, value);
    }

    public bool CanUpdatePreview
    {
        get => _canUpdatePreview;
        private set => SetProperty(ref _canUpdatePreview, value);
    }

    public ICommand CheckUpdateCommand { get; }

    public ICommand UpdateLatestCommand { get; }

    public ICommand UpdatePreviewCommand { get; }

    public event Action? RequestClose;

    /// <summary>请求 MainWindow 执行更新；参数为 npm dist-tag（latest/next）。</summary>
    public event Action<string>? UpdateRequested;

    public event Action? SettingsChanged;

    public void SetHotkey(int modifiers, int key)
    {
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        OnPropertyChanged(nameof(HotkeyDisplay));
    }

    private async void StopDshAsync()
    {
        StopDshStatus = "正在关闭 DeepSeek Harness…";
        try
        {
            if (!await _dsh.IsRunningAsync())
            {
                StopDshStatus = "当前没有运行中的 DeepSeek Harness。";
                return;
            }

            var stopped = await _dsh.StopRunningDshAsync();
            StopDshStatus = stopped
                ? "DeepSeek Harness 已关闭。"
                : "关闭失败：端口仍被其他程序占用，请检查后重试。";
        }
        catch (Exception ex)
        {
            StopDshStatus = "关闭失败：" + ex.Message;
        }
    }

    private async void SaveAsync()
    {
        if (!int.TryParse(_dshPortText.Trim(), out var port))
        {
            NoticeCallback?.Invoke("端口无效", "dsh 端口必须是 1-65535 之间的数字。");
            return;
        }

        var portError = DshService.GetPortError(port);
        if (portError != null)
        {
            NoticeCallback?.Invoke("端口不安全", portError);
            return;
        }

        // 只有端口发生变化且已被占用时才提醒，避免每次保存当前端口都弹窗。
        if (port != _settings.Settings.DshPort
            && await DshService.IsPortListeningOnPortAsync(port))
        {
            var confirmed = PortOccupiedCallback?.Invoke(port) ?? false;
            if (!confirmed)
                return;
        }

        _settings.Settings.Theme = (ThemePreference)_themeIndex;
        _settings.Settings.NpmRegistry = _registryIndex == 1 ? MirrorRegistry : OfficialRegistry;
        _settings.Settings.DshPort = port;
        _settings.Settings.NotifyOnComplete = _notifyOnComplete;
        _settings.Settings.AutoStart = _autoStart;
        _settings.Settings.AutoStartSilent = _autoStartSilent;
        _settings.Settings.HotkeyEnabled = _hotkeyEnabled;
        _settings.Settings.HotkeyModifiers = _hotkeyModifiers;
        _settings.Settings.HotkeyKey = _hotkeyKey;
        _settings.Save();

        _dshPortText = port.ToString();
        OnPropertyChanged(nameof(DshPortText));

        AutoStartService.SetEnabled(_autoStart);

        SettingsChanged?.Invoke();
        RequestClose?.Invoke();
    }

    private async Task CheckUpdateAsync()
    {
        UpdateStatusText = "正在检查更新…";
        CanUpdateLatest = false;
        CanUpdatePreview = false;

        try
        {
            var installed = UpdateService.GetInstalledVersion();
            InstalledVersionText = string.IsNullOrEmpty(installed)
                ? "当前版本：未知"
                : $"当前版本：{installed}";

            var (latest, preview) = await _update.GetAvailableVersionsAsync(_settings.Settings.NpmRegistry);
            var latestNewer = UpdateService.IsNewer(latest, installed);
            var previewNewer = UpdateService.IsNewer(preview, installed);

            CanUpdateLatest = latestNewer;
            CanUpdatePreview = previewNewer;

            if (latestNewer && previewNewer)
                UpdateStatusText = $"发现新版本 {latest}，另有预览版 {preview}";
            else if (latestNewer)
                UpdateStatusText = IsPrerelease(latest)
                    ? $"发现新版本 {latest}（预发布版）"
                    : $"发现新版本 {latest}";
            else if (previewNewer)
                UpdateStatusText = $"发现预览版 {preview}（预发布版），当前 {installed ?? "未知"}";
            else if (latest == null && preview == null)
                UpdateStatusText = "检查失败：无法连接 npm registry，请稍后重试。";
            else
                UpdateStatusText = $"已是最新版本（当前 {installed ?? "未知"}）";
        }
        catch
        {
            UpdateStatusText = "检查失败：无法连接 npm registry，请稍后重试。";
        }
    }

    private static bool IsPrerelease(string? version) => version?.Contains('-') == true;

    private static string FormatHotkey(int modifiers, int key)
    {
        var parts = new List<string>();
        if ((modifiers & ModCtrl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");

        parts.Add(KeyInterop.KeyFromVirtualKey(key).ToString());
        return string.Join(" + ", parts);
    }
}
