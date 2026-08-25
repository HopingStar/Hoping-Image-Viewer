using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace ImageViewer.App.Native;

/// <summary>自绘标题栏的窗口行为：原生拖动 / 工作区最大化（全屏，不遮任务栏）切换 / 全屏时拖动自动退出全屏 / 是否最大化判定。</summary>
internal static class WindowChromeService
{
    // 进入工作区最大化前的窗口位置大小；null 表示当前未最大化
    private static NativeMethods.RECT? _saved;

    /// <summary>全屏状态变化回调：true=进入全屏（隐藏边缘缩放热区、禁缩放），false=退出全屏（恢复）。
    /// 触发点：toggle 切换、拖动标题栏自动退出全屏。MainWindow 据此切换 WebView2 边距 / ResizeMode / ResizeBorderThickness。</summary>
    public static event Action<bool>? FullscreenChanged;

    // ---- 全屏时拖动追踪（低级鼠标钩子：等光标真正移动超过阈值才还原为窗口拖拽，单击不还原） ----
    private static IntPtr _mouseHook;
    private static NativeMethods.LowLevelMouseProc? _hookProc;   // 保持委托存活，防止被 GC
    private static NativeMethods.POINT _dragDown;
    private static IntPtr _dragHwnd;
    private static NativeMethods.RECT? _dragSaved;


    /// <summary>发起原生拖动（WM_NCLBUTTONDOWN + HTCAPTION）。
    /// 全屏（工作区最大化）时：先用低级鼠标钩子等待真实拖动，超过阈值才还原为窗口并拖拽——
    /// 单纯点击标题栏不退出全屏，双击仍可切换，拖动则自动取消全屏。</summary>
    public static void StartDrag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (_saved is { } saved)
        {
            NativeMethods.GetCursorPos(out _dragDown);
            _dragHwnd = hwnd;
            _dragSaved = saved;
            _hookProc = MouseHookProc;
            _mouseHook = NativeMethods.SetWindowsHookEx(
                NativeMethods.WH_MOUSE_LL, _hookProc, NativeMethods.GetModuleHandle(null), 0);
            if (_mouseHook == IntPtr.Zero)
            {
                // 钩子安装失败（极少见）：退化为立即还原并拖拽
                EndFullscreenDrag();
                RestoreAndDrag(hwnd, saved, _dragDown);
            }
            return;
        }

        NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    private static IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _mouseHook != IntPtr.Zero)
        {
            var msg = unchecked((int)wParam.ToInt64());
            if (msg == NativeMethods.WM_LBUTTONUP)
            {
                // 只是点击标题栏（未拖动）：退出追踪，保持全屏
                EndFullscreenDrag();
            }
            else if (msg == NativeMethods.WM_MOUSEMOVE)
            {
                var data = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
                var dx = data.Pt.X - _dragDown.X;
                var dy = data.Pt.Y - _dragDown.Y;
                var threshold = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXDRAG);
                if (Math.Abs(dx) > threshold || Math.Abs(dy) > threshold)
                {
                    var hwnd = _dragHwnd;
                    var saved = _dragSaved;
                    EndFullscreenDrag();
                    if (hwnd != IntPtr.Zero && saved is { } s)
                        RestoreAndDrag(hwnd, s, data.Pt);
                }
            }
        }
        return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    private static void EndFullscreenDrag()
    {
        if (_mouseHook != IntPtr.Zero)
        {
            _ = NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
        _dragHwnd = IntPtr.Zero;
        _dragSaved = null;
    }

    /// <summary>把窗口还原为全屏前的大小，并将窗口放到光标下方（标题栏对准光标），随后开始拖拽。
    /// 位置严格约束在工作区内，避免还原到屏幕外导致边缘（缩放热区）不可见。</summary>
    private static void RestoreAndDrag(IntPtr hwnd, NativeMethods.RECT saved, NativeMethods.POINT cursor)
    {
        var w = saved.Right - saved.Left;
        var h = saved.Bottom - saved.Top;
        var left = cursor.X - w / 2;
        var top = cursor.Y - 30;   // 30 ≈ 标题栏中心到窗口顶部的距离，拖拽时光标停在标题栏上
        ClampToWorkArea(ref left, ref top, w, h);
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        _saved = null;
        FullscreenChanged?.Invoke(false);
        NativeMethods.ReleaseCapture();
        _ = NativeMethods.SendMessage(hwnd, NativeMethods.WM_NCLBUTTONDOWN, (IntPtr)NativeMethods.HTCAPTION, IntPtr.Zero);
    }

    /// <summary>把窗口位置/尺寸约束到工作区内：完全在屏幕内，边缘热区才可见、可缩放。</summary>
    public static void EnsureOnScreen(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!NativeMethods.GetWindowRect(hwnd, out var r)) return;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) return;   // 窗口尚未布局完成时跳过
        var wa = GetWorkArea();
        if (w > wa.Right - wa.Left) w = wa.Right - wa.Left;   // 窗口比屏幕大 → 缩到屏幕大小
        if (h > wa.Bottom - wa.Top) h = wa.Bottom - wa.Top;
        int left = r.Left, top = r.Top;
        ClampToWorkArea(ref left, ref top, w, h);
        if (left != r.Left || top != r.Top || w != r.Right - r.Left || h != r.Bottom - r.Top)
        {
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, left, top, w, h,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private static void ClampToWorkArea(ref int left, ref int top, int w, int h)
    {
        var wa = GetWorkArea();
        if (left < wa.Left) left = wa.Left;
        if (top < wa.Top) top = wa.Top;
        if (left + w > wa.Right) left = wa.Right - w;
        if (top + h > wa.Bottom) top = wa.Bottom - h;
        if (left < wa.Left) left = wa.Left;   // 窗口比工作区还大时兜底
        if (top < wa.Top) top = wa.Top;
    }

    public static bool IsMaximized(Window window) => _saved is not null;

    /// <summary>切换工作区最大化（全屏，不遮挡任务栏）：保存原 rect → 铺满工作区 / 还原。返回切换后是否全屏。</summary>
    public static bool ToggleWorkAreaMaximize(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;

        if (_saved is { } saved)
        {
            _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, saved.Left, saved.Top,
                saved.Right - saved.Left, saved.Bottom - saved.Top,
                NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
            _saved = null;
            FullscreenChanged?.Invoke(false);
            return false;
        }

        NativeMethods.GetWindowRect(hwnd, out var r);
        _saved = r;
        var wa = GetWorkArea();
        _ = NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, wa.Left, wa.Top,
            wa.Right - wa.Left, wa.Bottom - wa.Top,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
        FullscreenChanged?.Invoke(true);
        return true;
    }

    private static NativeMethods.RECT GetWorkArea()
    {
        var rect = new NativeMethods.RECT();
        _ = NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref rect, 0);
        return rect;
    }

    /// <summary>闪烁窗口标题栏 + 任务栏图标，提示用户图片已打开。</summary>
    public static void Flash(Window window)
    {
        try
        {
            var info = new NativeMethods.FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.FLASHWINFO>(),
                hwnd = new WindowInteropHelper(window).Handle,
                dwFlags = NativeMethods.FLASHW_ALL | NativeMethods.FLASHW_TIMERNOFG,
                uCount = 0,
                dwTimeout = 0,
            };
            _ = NativeMethods.FlashWindowEx(ref info);
        }
        catch { }
    }
}
