using System.Windows;
using Microsoft.Win32;
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
        PluginPackageService packageService,
        DshService dsh,
        Func<Task> restartDsh,
        Window owner,
        ThemeService theme)
    {
        InitializeComponent();
        Owner = owner;
        _theme = theme;
        _viewModel = new PluginManagerViewModel(service, packageService, dsh, restartDsh)
        {
            ConfirmCallback = Confirm,
            NoticeCallback = Notice,
            StopDshRequestedCallback = RequestStopDsh,
            ExportPackagePathCallback = ChooseExportPath,
            ImportPackagePathCallback = ChooseImportPath,
            ImportPreviewCallback = ShowImportPreview,
        };
        DataContext = _viewModel;
        _theme.ThemeChanged += OnThemeChanged;
        _theme.ApplyWindowTitleBar(this);
    }

    private static string? ChooseExportPath()
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "DshGUI 插件包 (*.dshpkg)|*.dshpkg",
            FileName = "dsh-plugins.dshpkg",
            DefaultExt = ".dshpkg",
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private static string? ChooseImportPath()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "DshGUI 插件包 (*.dshpkg)|*.dshpkg",
            CheckFileExists = true,
        };
        return dialog.ShowDialog() == true ? dialog.FileName : null;
    }

    private IReadOnlyList<string>? ShowImportPreview(PluginImportPreview preview)
    {
        var dialog = new PluginImportPreviewDialog(preview) { Owner = this };
        _theme.ApplyWindowTitleBar(dialog);
        return dialog.ShowDialog() == true ? dialog.SelectedNames : null;
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
