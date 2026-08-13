using System.Windows;
using System.Windows.Input;
using DshGUI.ViewModels;

namespace DshGUI.Views
{
    public partial class SettingsWindow : Window
    {
        private bool _recording;

        public SettingsWindow(SettingsViewModel viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            viewModel.RequestClose += () => Close();
        }

        private void OnRecordHotkeyClick(object sender, RoutedEventArgs e)
        {
            _recording = true;
            RecordButton.Content = "按下组合键…";
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (!_recording)
                return;

            if (e.Key == Key.Escape)
            {
                _recording = false;
                RecordButton.Content = "录制";
                e.Handled = true;
                return;
            }

            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (IsModifierKey(key))
            {
                e.Handled = true;
                return;
            }

            var modifiers = (int)(Keyboard.Modifiers
                & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift | ModifierKeys.Windows));
            if (modifiers == 0)
            {
                e.Handled = true;
                return;
            }

            ((SettingsViewModel)DataContext).SetHotkey(modifiers, KeyInterop.VirtualKeyFromKey(key));

            _recording = false;
            RecordButton.Content = "录制";
            e.Handled = true;
        }

        private static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin;
    }
}
