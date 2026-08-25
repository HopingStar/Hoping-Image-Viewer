using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Shell;
using ImageViewer.App.Native;
using ImageViewer.App.Wpf;

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


    /// <summary>关闭按钮：走 MainWindow.OnClosing 统一逻辑——按设置「最小化到托盘」或「退出程序」。</summary>
    public void close()
        => _window.Dispatcher.BeginInvoke(() => _window.Close());

    /// <summary>打开图片时把窗口带到最前（托盘隐藏/最小化均恢复显示）并闪烁任务栏提示用户。</summary>
    public void bring_to_front()
        => _window.Dispatcher.BeginInvoke(() =>
        {
            // 先恢复最小化（WPF 状态）
            if (_window.WindowState == WindowState.Minimized)
                _window.WindowState = WindowState.Normal;
            // 托盘隐藏：WPF Show 恢复显示
            if (!_window.IsVisible) _window.Show();
            // Win32 层强制恢复显示 + 置前（WPF Show/Activate 在托盘隐藏场景可能不生效）
            var hwnd = new WindowInteropHelper(_window).Handle;
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
            NativeMethods.SetForegroundWindow(hwnd);
            _window.Activate();
            WindowChromeService.Flash(_window);
        });

    /// <summary>设置任务栏进度条（一键识别等批量任务）。value 0..1 为进度比例；
    /// state：0=无进度、1=绿色进度、2=不确定(动画)、3=红色错误、4=黄色暂停。</summary>
    public void set_taskbar_progress(double value, int state)
        => _window.Dispatcher.BeginInvoke(() =>
        {
            if (_window is not MainWindow mw) return;
            var info = mw.TaskbarItemInfo ?? new TaskbarItemInfo();
            info.ProgressValue = Math.Clamp(value, 0, 1);
            info.ProgressState = state switch
            {
                1 => TaskbarItemProgressState.Normal,
                2 => TaskbarItemProgressState.Indeterminate,
                3 => TaskbarItemProgressState.Error,
                4 => TaskbarItemProgressState.Paused,
                _ => TaskbarItemProgressState.None,
            };
            mw.TaskbarItemInfo = info;
        });

    /// <summary>弹系统文件夹选择框，返回选中的文件夹路径（取消返回 null）。title 为前端本地化后的标题。</summary>
    public string? pick_folder(string? title = null)
    {
        string? result = null;
        _window.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "选择相册文件夹（直接链接，不复制文件）" : title,
                Multiselect = false,
            };
            if (dlg.ShowDialog(_window) == true)
                result = dlg.FolderName;
        });
        return result;
    }

    /// <summary>弹系统文件选择框选一个图片编辑器 exe，返回完整路径（取消返回 null）。title 为前端本地化后的标题。</summary>
    public string? pick_editor_exe(string? title = null)
    {
        string? result = null;
        _window.Dispatcher.Invoke(() =>
        {
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Title = string.IsNullOrWhiteSpace(title) ? "选择图片编辑器（exe）" : title,
                Filter = "可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dlg.ShowDialog(_window) == true)
                result = dlg.FileName;
        });
        return result;
    }
}
