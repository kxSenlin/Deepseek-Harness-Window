using System.Windows;
using DshGUI.Models;

namespace DshGUI.Views;

public partial class PluginConfirmDialog : Window
{
    private readonly string _nameToMatch;

    public PluginConfirmDialog(PluginConfirmPrompt prompt)
    {
        InitializeComponent();
        Title = prompt.Title;
        MessageText.Text = prompt.Message;
        _nameToMatch = prompt.NameToMatch;

        if (prompt.RequireNameInput)
        {
            InputLabel.Visibility = Visibility.Visible;
            NameInput.Visibility = Visibility.Visible;
            NameInput.Focus();
        }
    }

    public bool Confirmed { get; private set; }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (_nameToMatch.Length > 0
            && !string.Equals(NameInput.Text.Trim(), _nameToMatch, StringComparison.OrdinalIgnoreCase))
        {
            ErrorText.Text = "输入不匹配，请重新输入插件名。";
            ErrorText.Visibility = Visibility.Visible;
            NameInput.SelectAll();
            NameInput.Focus();
            return;
        }

        Confirmed = true;
        DialogResult = true;
    }
}
