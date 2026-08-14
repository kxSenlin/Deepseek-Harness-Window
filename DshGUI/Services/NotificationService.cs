using System;
using System.Collections.Generic;
using System.Windows;
using DshGUI.Views;

namespace DshGUI.Services;

public sealed class NotificationService
{
    private const double Gap = 8;

    private readonly List<ToastWindow> _toasts = new();

    public ToastWindow Show(string title, string message, Action? onClick = null, bool persistent = false)
    {
        var toast = new ToastWindow(title, message, onClick, persistent);
        _toasts.Add(toast);
        toast.Loaded += (_, _) => Reposition();
        toast.Closed += (_, _) =>
        {
            _toasts.Remove(toast);
            Reposition();
        };

        // 先放到屏幕外，避免在 Loaded 重定位前闪现。
        var area = SystemParameters.WorkArea;
        toast.Left = area.Left + area.Width;
        toast.Top = area.Top + area.Height;

        toast.Show();
        return toast;
    }

    private void Reposition()
    {
        var area = SystemParameters.WorkArea;
        double bottom = area.Top + area.Height - Gap;

        // 最新的一条在最下，依次往上错开。
        for (int i = _toasts.Count - 1; i >= 0; i--)
        {
            var toast = _toasts[i];
            double height = toast.ActualHeight > 0 ? toast.ActualHeight : 0;
            toast.Left = area.Left + area.Width - toast.ActualWidth - Gap;
            toast.Top = bottom - height;
            bottom -= height + Gap;
        }
    }
}
