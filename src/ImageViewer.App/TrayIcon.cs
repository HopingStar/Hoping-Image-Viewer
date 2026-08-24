using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace ImageViewer.App;

/// <summary>
/// 系统托盘图标：程序常驻托盘（关闭窗口仅隐藏，不退出进程）。
/// 右键菜单：「打开主界面」恢复窗口 /「退出」真正结束进程；双击托盘图标恢复窗口。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIcon(Window window)
    {
        _icon = new NotifyIcon { Visible = true, Text = "Hoping Image Viewer" };
        try
        {
            // 用程序集内嵌的 ico 作为托盘图标
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/HopingImageViewer.ico"))?.Stream;
            if (stream is not null)
            {
                using (stream) _icon.Icon = new Icon(stream);
            }
        }
        catch { }

        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主界面", null, (_, _) => ShowWindow(window));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());
        // 右键手动在鼠标位置弹出菜单（菜单左上角落在鼠标点 → 显示在鼠标右侧，符合习惯）
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
                menu.Show(Cursor.Position);
        };
        _icon.DoubleClick += (_, _) => ShowWindow(window);
    }

    private static void ShowWindow(Window window)
        => window.Dispatcher.BeginInvoke(() =>
        {
            if (!window.IsVisible) window.Show();
            if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
            window.Activate();
        });

    public void Dispose() => _icon.Dispose();
}
