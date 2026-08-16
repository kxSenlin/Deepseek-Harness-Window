using System.Windows;
using DshGUI.Models;
using DshGUI.Services;

namespace DshGUI.Views;

public partial class PluginImportPreviewDialog : Window
{
    public PluginImportPreviewDialog(PluginImportPreview preview)
    {
        InitializeComponent();
        Title = $"导入插件包：{preview.ProfileName}";
        DuplicatesList.ItemsSource = preview.Duplicates.Count > 0
            ? preview.Duplicates
            : ["（无重名插件）"];
        AdditionsItemsControl.ItemsSource = preview.Items
            .Where(item => !item.IsDuplicate)
            .ToList();
    }

    public IReadOnlyList<string> SelectedNames =>
        AdditionsItemsControl.ItemsSource
            .Cast<PluginImportPreviewItem>()
            .Where(item => item.IsSelected)
            .Select(item => item.Name)
            .ToList();

    private void OnContinueClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
