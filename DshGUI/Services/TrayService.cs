using System.Drawing;
using System.Windows.Forms;

namespace DshGUI.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayService()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 DeepSeek Harness", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("检查更新", null, (_, _) => CheckUpdateRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = ExtractAppIcon(),
            Text = "DeepSeek Harness",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
    }

    public event Action? OpenRequested;

    public event Action? CheckUpdateRequested;

    public event Action? ExitRequested;

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }

    private static Icon ExtractAppIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe != null)
            {
                var icon = Icon.ExtractAssociatedIcon(exe);
                if (icon != null)
                    return icon;
            }
        }
        catch
        {
            // 提取失败则回退到系统图标。
        }

        return SystemIcons.Application;
    }
}
