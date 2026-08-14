using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace DshGUI.Views
{
    public partial class ToastWindow : Window
    {
        private readonly DispatcherTimer? _timer;

        public ToastWindow(string title, string message, Action? onClick = null, bool persistent = false)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
            if (onClick != null)
                Clicked += onClick;

            if (!persistent)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
                _timer.Tick += (_, _) => Close();
                _timer.Start();
            }
        }

        public event Action? Clicked;

        private void OnClick(object sender, MouseButtonEventArgs e)
        {
            Clicked?.Invoke();
            Close();
        }
    }
}
