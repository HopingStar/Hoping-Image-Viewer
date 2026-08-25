using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Windows;
using ImageViewer.App.Wpf;
using ImageViewer.Gallery;

namespace ImageViewer.App;

public partial class App : System.Windows.Application
{
    private const string SingleInstanceMutexName = @"Local\HopingImageViewer_SingleInstance";
    private const string PipeName = @"HopingImageViewer_Pipe";
    private const string FastPrefix = "FAST|";
    private static Mutex? _singleMutex;

    private SettingsStore _settings = null!;
    private bool _mainStarted;               // 主界面（WebView2 + Kestrel）是否已初始化
    private FastViewerWindow? _fastWindow;   // 当前极速查看器窗口（若已打开）

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 未处理异常写日志（定位崩溃用，托盘菜单等 UI 线程异常会走到这）
        DispatcherUnhandledException += (_, ev) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] Dispatcher: {ev.Exception}\n");
            }
            catch { }
        };
        AppDomain.CurrentDomain.UnhandledException += (_, ev) =>
        {
            try
            {
                File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] UNHANDLED: {ev.ExceptionObject}\n");
            }
            catch { }
        };

        _settings = new SettingsStore();
        // 极速模式：设置中开启「极速查看器」且启动参数带图片路径（来自文件关联双击）→ 秒开原生极速窗口，不加载主界面
        var imageArg = e.Args.FirstOrDefault(a => !string.IsNullOrWhiteSpace(a) && ImageService.ContentTypeFor(a) is not null);
        bool fastMode = _settings.GetFastViewer() && imageArg is not null;

        // 单实例复用：已有实例在运行 → 把命令行里的图片路径转发给它，本实例直接退出
        _singleMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            ForwardToRunningInstance(e.Args, fastMode);
            Shutdown();
            return;
        }
        StartPipeServer();   // 接收后续实例转发的路径（极速打开 / 普通查看）

        if (fastMode)
        {
            // 极速模式：不开主界面（不初始化 Kestrel/WebView2），原生极速窗口直接显示图片
            ShowFastViewer(Path.GetFullPath(imageArg!));
        }
        else
        {
            StartMainWindow(e.Args);
        }
    }

    /// <summary>启动主程序界面（WebView2 主窗口）。普通启动、极速窗口「回到相册」、或极速窗口收到普通打开请求时调用。</summary>
    private void StartMainWindow(string[] args)
    {
        _mainStarted = true;
        // 立即弹出窗口（秒开）：Kestrel 与 WebView2 在 MainWindow 内后台并行初始化，不阻塞窗口显示
        var window = new MainWindow(args);
        window.Show();
        window.Activate();
    }

    /// <summary>打开（或切换图片的）极速查看器窗口。极速窗口「回到相册」→ 关闭本窗口 + 主界面未初始化时加载主界面（相册首页）。
    /// path 为空 = 空画布（托盘「Flash 查看器」打开，可拖入图片）。</summary>
    private void ShowFastViewer(string? path)
    {
        if (_fastWindow is { } fw)
        {
            if (!string.IsNullOrWhiteSpace(path)) fw.LoadImage(path);
            fw.BringToFront();   // 换图/恢复窗口置前 + 闪烁任务栏
            return;
        }

        var fast = new FastViewerWindow(path);
        fast.Closed += (_, _) => _fastWindow = null;
        fast.ReturnToGalleryRequested += () =>
        {
            fast.Close();
            // 极速启动场景（主界面未加载）：点「回到相册」才加载主界面；主界面已在运行时仅关闭极速窗口
            if (!_mainStarted) StartMainWindow(Array.Empty<string>());
        };
        _fastWindow = fast;
        fast.Show();
        fast.BringToFront();   // 打开图片：窗口置前 + 闪烁任务栏提示
    }

    /// <summary>托盘菜单「Flash 查看器」：打开极速查看器（无图片，可拖入）。</summary>
    public void OpenFlashViewer() => ShowFastViewer(null);

    /// <summary>把命令行里的图片路径通过命名管道转发给正在运行的主实例（单实例复用）。
    /// 极速模式（fast=true）时加 FAST| 前缀，让主实例弹极速窗口而非 WebView2 查看器。</summary>
    private static void ForwardToRunningInstance(string[] args, bool fast)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || ImageService.ContentTypeFor(arg) is null) continue;
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client);
                writer.WriteLine(fast ? FastPrefix + Path.GetFullPath(arg) : Path.GetFullPath(arg));
                writer.Flush();
            }
            catch { }
            return;
        }
    }

    /// <summary>后台线程监听命名管道：收到 FAST|路径 → 打开/切换极速窗口；
    /// 收到普通路径 → 打开 WebView2 查看器（若主界面尚未初始化——极速模式下——先加载主界面再打开）。</summary>
    private void StartPipeServer()
    {
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1);
                    server.WaitForConnection();
                    using var reader = new StreamReader(server);
                    var line = reader.ReadLine();
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    var fast = line.StartsWith(FastPrefix, StringComparison.Ordinal);
                    var path = fast ? line[FastPrefix.Length..] : line;
                    if (string.IsNullOrWhiteSpace(path) || ImageService.ContentTypeFor(path) is null) continue;
                    var full = Path.GetFullPath(path);
                    System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (fast)
                        {
                            ShowFastViewer(full);
                            return;
                        }
                        // 普通打开：主界面未初始化时先加载主界面
                        if (!_mainStarted) StartMainWindow(Array.Empty<string>());
                        AppHost.PendingOpenPath = full;
                        // 主动推送主窗口弹窗并打开图片（不依赖前端轮询——窗口隐藏时 Chromium 会节流 JS 定时器）
                        try
                        {
                            foreach (Window w in System.Windows.Application.Current.Windows)
                            {
                                if (w is MainWindow mw) { mw.OpenImage(full); break; }
                            }
                        }
                        catch { }
                    });
                }
                catch { /* 单次监听失败不影响后续 */ }
            }
        })
        { IsBackground = true };
        thread.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _singleMutex?.ReleaseMutex(); } catch { }
        base.OnExit(e);
    }
}
