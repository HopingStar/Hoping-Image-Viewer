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
    private static Mutex? _singleMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 单实例复用：已有实例在运行 → 把命令行里的图片路径转发给它，本实例直接退出
        _singleMutex = new Mutex(true, SingleInstanceMutexName, out bool createdNew);
        if (!createdNew)
        {
            ForwardToRunningInstance(e.Args);
            Shutdown();
            return;
        }
        StartPipeServer();   // 接收后续实例转发的图片路径，交给前端打开

        // 立即弹出窗口（秒开）：Kestrel 与 WebView2 在 MainWindow 内后台并行初始化，不阻塞窗口显示
        var window = new MainWindow(e.Args);
        window.Show();
        window.Activate();
    }

    /// <summary>把命令行里的图片路径通过命名管道转发给正在运行的主实例（单实例复用）。</summary>
    private static void ForwardToRunningInstance(string[] args)
    {
        foreach (var arg in args)
        {
            if (string.IsNullOrWhiteSpace(arg) || ImageService.ContentTypeFor(arg) is null) continue;
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
                client.Connect(3000);
                using var writer = new StreamWriter(client);
                writer.WriteLine(Path.GetFullPath(arg));
                writer.Flush();
            }
            catch { }
            return;
        }
    }

    /// <summary>后台线程监听命名管道：收到图片路径 → 记为待打开图片，前端轮询到后打开查看器。</summary>
    private static void StartPipeServer()
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
                    if (!string.IsNullOrWhiteSpace(line) && ImageService.ContentTypeFor(line) is not null)
                    {
                        var path = Path.GetFullPath(line);
                        AppHost.PendingOpenPath = path;
                        // 主动推送主窗口弹窗并打开图片（不依赖前端轮询——窗口隐藏时 Chromium 会节流 JS 定时器）
                        try
                        {
                            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                            {
                                foreach (Window w in System.Windows.Application.Current.Windows)
                                {
                                    if (w is MainWindow mw) { mw.OpenImage(path); break; }
                                }
                            });
                        }
                        catch { }
                    }
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
