using System.Windows;
using System.Windows.Interop;

namespace ImageViewer.App.Native;

/// <summary>自绘标题栏的窗口行为：原生拖动 / 工作区最大化切换 / 是否最大化判定。</summary>
internal static class WindowChromeService
{
    // 进入工作区最大化前的窗口位置大小；null 表示当前未最大化
    private static NativeMethods.RECT? _saved;

    /// <summary>发起原生拖动（WM_NCLBUTTONDOWN + HTCAPTION）。</summary>
    public static void StartDrag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    public static bool IsMaximized(Window window) => _saved is not null;

    /// <summary>切换工作区最大化（保存原 rect → 铺满工作区）/ 还原。返回切换后是否最大化。</summary>
    public static bool ToggleWorkAreaMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        if (_saved is { } saved)
        {
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            _saved = null;
            return false;
        }

        NativeMethods.GetWindowRect(hwnd, out var r);
        _saved = r;
        var wa = GetWorkArea();
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, wa.Left, wa.Top,
            wa.Right - wa.Left, wa.Bottom - wa.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        return true;
    }

    private static NativeMethods.RECT GetWorkArea()
    {
        var rect = new NativeMethods.RECT();
        _ = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }
}
