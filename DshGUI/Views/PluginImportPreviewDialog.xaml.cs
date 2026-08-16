using System.Windows;
using DshGUI.Models;

namespace DshGUI.Views;

public partial class PluginImportPreviewDialog : Window
{
    public PluginImportPreviewDialog(PluginImportPreview preview)
    {
        InitializeComponent();
        Title = $"导入插件包：{preview.ProfileName}";
        ProfileCombo.ItemsSource = preview.AvailableProfiles.Count > 0
            ? preview.AvailableProfiles
            : [preview.ProfileName];
        ProfileCombo.SelectedItem = preview.ProfileName;
        DuplicatesList.ItemsSource = preview.Duplicates.Count > 0
            ? preview.Duplicates
            : ["（无重名插件）"];
        AdditionsItemsControl.ItemsSource = preview.Items
            .Where(item => !item.IsDuplicate)
            .ToList();
    }

    public PluginImportSelection Selection => new()
    {
        ProfileName = ProfileCombo.SelectedItem as string ?? "",
        SelectedNames = AdditionsItemsControl.ItemsSource
            .Cast<PluginImportPreviewItem>()
            .Where(item => item.IsSelected)
            .Select(item => item.Name)
            .ToList(),
    };

    private void OnContinueClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
