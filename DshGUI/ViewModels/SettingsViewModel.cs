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

    private readonly SettingsService _settings;

    private int _themeIndex;
    private bool _notifyOnComplete;
    private bool _autoStart;
    private bool _hotkeyEnabled;
    private int _hotkeyModifiers;
    private int _hotkeyKey;

    public SettingsViewModel(SettingsService settings)
    {
        _settings = settings;
        _themeIndex = (int)settings.Settings.Theme;
        _notifyOnComplete = settings.Settings.NotifyOnComplete;
        _autoStart = settings.Settings.AutoStart;
        _hotkeyEnabled = settings.Settings.HotkeyEnabled;
        _hotkeyModifiers = settings.Settings.HotkeyModifiers;
        _hotkeyKey = settings.Settings.HotkeyKey;

        SaveCommand = new RelayCommand(_ => Save());
        CancelCommand = new RelayCommand(_ => RequestClose?.Invoke());
    }

    public int ThemeIndex
    {
        get => _themeIndex;
        set => SetProperty(ref _themeIndex, value);
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

    public bool HotkeyEnabled
    {
        get => _hotkeyEnabled;
        set => SetProperty(ref _hotkeyEnabled, value);
    }

    public string HotkeyDisplay => FormatHotkey(_hotkeyModifiers, _hotkeyKey);

    public ICommand SaveCommand { get; }

    public ICommand CancelCommand { get; }

    public event Action? RequestClose;

    public event Action? SettingsChanged;

    public void SetHotkey(int modifiers, int key)
    {
        _hotkeyModifiers = modifiers;
        _hotkeyKey = key;
        OnPropertyChanged(nameof(HotkeyDisplay));
    }

    private void Save()
    {
        _settings.Settings.Theme = (ThemePreference)_themeIndex;
        _settings.Settings.NotifyOnComplete = _notifyOnComplete;
        _settings.Settings.AutoStart = _autoStart;
        _settings.Settings.HotkeyEnabled = _hotkeyEnabled;
        _settings.Settings.HotkeyModifiers = _hotkeyModifiers;
        _settings.Settings.HotkeyKey = _hotkeyKey;
        _settings.Save();

        AutoStartService.SetEnabled(_autoStart);

        SettingsChanged?.Invoke();
        RequestClose?.Invoke();
    }

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
