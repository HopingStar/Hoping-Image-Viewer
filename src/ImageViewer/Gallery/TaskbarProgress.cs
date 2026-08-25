namespace ImageViewer.Gallery;

/// <summary>任务栏进度中转：前端通过 HTTP（/api/taskbar-progress）异步上报进度，
/// WPF 宿主注册回调后把进度设置到任务栏进度条。
/// 相比 WebView2 host object 高频调用，HTTP 上报不阻塞 JS 主线程（避免一键识别循环卡死）。</summary>
public static class TaskbarProgress
{
    private static Action<double, int>? _sink;

    /// <summary>WPF 宿主注册任务栏进度设置回调（线程安全；回调内自行切回 UI 线程）。</summary>
    public static void Register(Action<double, int> sink) => _sink = sink;

    /// <summary>前端进度上报（由 /api/taskbar-progress 端点调用）。
    /// value 0..1 为进度比例；state：0=无、1=绿色、2=不确定、3=红色错误、4=黄色暂停。</summary>
    public static void Update(double value, int state) => _sink?.Invoke(Math.Clamp(value, 0, 1), state);
}
