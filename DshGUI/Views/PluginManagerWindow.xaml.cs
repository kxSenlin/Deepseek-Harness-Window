using System.Windows;
using DshGUI.Models;
using DshGUI.Services;
using DshGUI.ViewModels;

namespace DshGUI.Views;

public partial class PluginManagerWindow : Window
{
    private readonly PluginManagerViewModel _viewModel;
    private readonly ThemeService _theme;

    public PluginManagerWindow(
        PluginManagerService service,
        DshService dsh,
        Func<Task> restartDsh,
        Window owner,
        ThemeService theme)
    {
        InitializeComponent();
        Owner = owner;
        _theme = theme;
        _viewModel = new PluginManagerViewModel(service, dsh, restartDsh)
        {
            ConfirmCallback = Confirm,
            NoticeCallback = Notice,
            StopDshRequestedCallback = RequestStopDsh,
        };
        DataContext = _viewModel;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.ApplyWindowTitleBar(this);
    }

    private void OnThemeChanged() => _theme.ApplyWindowTitleBar(this);

    private bool Confirm(PluginConfirmPrompt prompt)
    {
        var dialog = new PluginConfirmDialog(prompt) { Owner = this };
        _theme.ApplyWindowTitleBar(dialog);
        return dialog.ShowDialog() == true && dialog.Confirmed;
    }

    private bool Notice(string title, string message)
    {
        var dialog = new PluginNoticeDialog(title, message) { Owner = this };
        _theme.ApplyWindowTitleBar(dialog);
        dialog.ShowDialog();
        return true;
    }

    private bool RequestStopDsh(string message)
    {
        var dialog = new PluginStopDshDialog(message) { Owner = this };
        _theme.ApplyWindowTitleBar(dialog);
        return dialog.ShowDialog() == true;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        await _viewModel.InitializeAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _theme.ThemeChanged -= OnThemeChanged;
        DataContext = null;
    }
}
