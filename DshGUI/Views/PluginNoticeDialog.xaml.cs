using System.Windows;

namespace DshGUI.Views;

public partial class PluginNoticeDialog : Window
{
    public PluginNoticeDialog(string title, string message)
    {
        InitializeComponent();
        Title = title;
        MessageText.Text = message;
    }

    private void OnOkClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
