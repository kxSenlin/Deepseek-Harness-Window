using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace DshGUI.Infrastructure;

public static class IconHelper
{
    public static ImageSource? GetAppIcon(int size)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null)
                return null;

            using var icon = System.Drawing.Icon.ExtractAssociatedIcon(exe);
            if (icon == null)
                return null;

            using var bitmap = icon.ToBitmap();
            var hBitmap = bitmap.GetHbitmap();
            try
            {
                return Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, IntPtr.Zero, Int32Rect.Empty,
                    BitmapSizeOptions.FromWidthAndHeight(size, size));
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
