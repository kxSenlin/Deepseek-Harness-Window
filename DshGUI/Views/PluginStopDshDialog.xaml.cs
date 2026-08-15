using System.Windows;

namespace DshGUI.Views;

public partial class PluginStopDshDialog : Window
{
    public PluginStopDshDialog(string message)
    {
        InitializeComponent();
        MessageText.Text = message;
    }

    private void OnStopClick(object sender, RoutedEventArgs e) => DialogResult = true;
}
