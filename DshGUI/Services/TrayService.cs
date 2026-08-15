using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace DshGUI.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _idleIcon;
    private readonly Icon _runningIcon;
    private readonly System.Windows.Forms.Timer _blinkTimer;
    private bool _running;
    private bool _blinkOn;

    public TrayService()
    {
        _idleIcon = ExtractAppIcon();
        _runningIcon = CreateRunningIcon(_idleIcon);

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开 DeepSeek Harness", null, (_, _) => OpenRequested?.Invoke());
        menu.Items.Add("插件管理", null, (_, _) => PluginManagerRequested?.Invoke());
        menu.Items.Add("检查更新", null, (_, _) => CheckUpdateRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "DeepSeek Harness",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();

        _blinkTimer = new System.Windows.Forms.Timer { Interval = 500 };
        _blinkTimer.Tick += (_, _) =>
        {
            if (!_running)
                return;
            _blinkOn = !_blinkOn;
            _icon.Icon = _blinkOn ? _runningIcon : _idleIcon;
        };
    }

    public event Action? OpenRequested;

    public event Action? PluginManagerRequested;

    public event Action? CheckUpdateRequested;

    public event Action? ExitRequested;

    /// <summary>运行中：托盘图标闪烁绿点；空闲恢复原图标。</summary>
    public void SetRunning(bool running)
    {
        _running = running;
        if (running)
        {
            _blinkOn = true;
            _icon.Icon = _runningIcon;
            _icon.Text = "DeepSeek Harness（运行中）";
            _blinkTimer.Start();
        }
        else
        {
            _blinkTimer.Stop();
            _icon.Icon = _idleIcon;
            _icon.Text = "DeepSeek Harness";
        }
    }

    public void Dispose()
    {
        _blinkTimer.Stop();
        _blinkTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _idleIcon.Dispose();
        _runningIcon.Dispose();
    }

    private static Icon CreateRunningIcon(Icon baseIcon)
    {
        try
        {
            using var bitmap = baseIcon.ToBitmap();
            using var g = Graphics.FromImage(bitmap);
            var dot = Math.Max(4, bitmap.Width / 4);
            using var brush = new SolidBrush(System.Drawing.Color.FromArgb(0xFF, 0x2E, 0xCC, 0x71));
            g.FillEllipse(brush, bitmap.Width - dot, bitmap.Height - dot, dot, dot);

            var handle = bitmap.GetHicon();
            try
            {
                return (Icon)Icon.FromHandle(handle).Clone();
            }
            finally
            {
                DestroyIcon(handle);
            }
        }
        catch
        {
            return (Icon)baseIcon.Clone();
        }
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

        return (Icon)SystemIcons.Application.Clone();
    }

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);
}
