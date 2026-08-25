using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Media;
using ImageViewer.App.Wpf;
using ImageViewer.Gallery;

namespace ImageViewer.App;

/// <summary>
/// 系统托盘图标：程序常驻托盘（关闭窗口按设置最小化到托盘，进程驻留）。
/// 右键弹自定义炫酷菜单（WPF 无边框圆角窗口：打开主界面 / 退出）；双击托盘图标恢复主界面。
/// 菜单窗口 ShowInTaskbar=false，不占用任务栏（修复右键托盘时任务栏出现无名字窗口的问题）。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly MainWindow _window;
    private TrayMenuWindow? _menu;

    public TrayIcon(MainWindow window)
    {
        _window = window;
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

        // 右键托盘 → 弹自定义炫酷菜单（替代系统 ContextMenuStrip）。
        // 用 Cursor.Position 取鼠标屏幕坐标——NotifyIcon 事件的 e.X/e.Y 经常为 (0,0)，会把菜单弹到屏幕左上角。
        _icon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Right)
            {
                var pos = Cursor.Position;
                ShowMenu(pos.X, pos.Y);
            }
        };
        _icon.DoubleClick += (_, _) => ShowWindow(_window);
    }

    /// <summary>在鼠标位置弹自定义菜单窗口，并限制在工作区内（托盘一般在右下角，菜单出现在鼠标上方/左侧）。</summary>
    private void ShowMenu(int screenX, int screenY)
    {
        _window.Dispatcher.BeginInvoke(() =>
        {
            _menu?.Close();
            _menu = new TrayMenuWindow(
                onOpen: () => ShowWindow(_window),
                onFlash: () => ((App)System.Windows.Application.Current).OpenFlashViewer(),
                onExit: () => _window.RequestExit(),
                flashEnabled: new SettingsStore().GetFastViewer());
            _menu.Show();
            _menu.UpdateLayout();
            // Cursor.Position 是物理像素，转 DIP 再定位；菜单出现在鼠标右上方（贴近托盘习惯）
            var dpi = VisualTreeHelper.GetDpi(_menu);
            double wDip = _menu.ActualWidth / dpi.DpiScaleX;
            double hDip = _menu.ActualHeight / dpi.DpiScaleY;
            var work = SystemParameters.WorkArea;
            double left = screenX / dpi.DpiScaleX + 6;
            double top = screenY / dpi.DpiScaleY - hDip - 6;
            left = Math.Clamp(left, work.Left + 4, Math.Max(work.Left + 4, work.Right - wDip - 4));
            top = Math.Clamp(top, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - hDip - 4));
            _menu.Left = left;
            _menu.Top = top;
        });
    }

    private void ShowWindow(MainWindow window)
        => window.Dispatcher.BeginInvoke(() =>
        {
            try
            {
                if (!window.IsVisible) window.Show();
                if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
                window.Activate();
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"),
                        $"[{DateTime.Now:HH:mm:ss}] ShowWindow: {ex}\n");
                }
                catch { }
            }
        });

    public void Dispose()
    {
        _menu?.Close();
        _menu = null;
        _icon.Dispose();
    }
}
