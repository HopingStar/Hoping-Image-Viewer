using System.Runtime.InteropServices;
using System.Windows;
using ImageViewer.App.Native;

namespace ImageViewer.App.Bridge;

/// <summary>
/// WebView2 Host Object（ComVisible），暴露给前端 window.chromeHost 的自绘标题栏窗口控制：
/// start_drag / minimize / toggle_maximize / is_maximized / close。
/// </summary>
[ComVisible(true)]
[ClassInterface(ClassInterfaceType.AutoDual)]
public sealed class WindowControlBridge
{
    private readonly Window _window;

    public WindowControlBridge(Window window) => _window = window;

    public void start_drag()
        => _window.Dispatcher.BeginInvoke(() => WindowChromeService.StartDrag(_window));

    public void minimize()
        => _window.Dispatcher.BeginInvoke(() => _window.WindowState = WindowState.Minimized);

    public int toggle_maximize()
    {
        var r = 0;
        _window.Dispatcher.Invoke(() => r = WindowChromeService.ToggleWorkAreaMaximize(_window) ? 1 : 0);
        return r;
    }

    public int is_maximized()
    {
        var r = 0;
        _window.Dispatcher.Invoke(() => r = WindowChromeService.IsMaximized(_window) ? 1 : 0);
        return r;
    }

    public void close()
        => _window.Dispatcher.BeginInvoke(() => _window.Close());

    /// <summary>弹系统文件夹选择框，返回选中的文件夹路径（取消返回 null）。</summary>
    public string? pick_folder()
    {
        string? result = null;
        _window.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择相册文件夹（直接链接，不复制文件）",
                Multiselect = false,
            };
            if (dlg.ShowDialog(_window) == true)
                result = dlg.FolderName;
        });
        return result;
    }
}
