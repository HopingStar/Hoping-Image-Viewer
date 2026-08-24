using System.IO;
using System.Net;
using System.Windows;
using ImageViewer.App.Hosting;
using ImageViewer.App.Wpf;
using ImageViewer.Gallery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;

namespace ImageViewer.App;

public partial class App : Application
{
    private WebHostHandle? _host;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        try
        {
            // 0) 双击图片文件用本程序打开：命令行参数里是图片路径 → 记为待打开图片（前端启动后打开查看器）
            foreach (var arg in e.Args)
            {
                if (!string.IsNullOrWhiteSpace(arg) && ImageService.ContentTypeFor(arg) is not null)
                {
                    AppHost.PendingOpenPath = Path.GetFullPath(arg);
                    break;
                }
            }

            // 1) 组装内嵌 Kestrel：127.0.0.1 随机空闲端口（ListenLocalhost(0) 不支持动态端口，改用显式 Loopback）
            var webRoot = ResolveWebRoot();
            var webApp = AppHost.Build(
                options: new WebApplicationOptions
                {
                    ApplicationName = "HopingImageViewer",
                    ContentRootPath = AppContext.BaseDirectory,
                    WebRootPath = webRoot,
                },
                configureWebHost: webHost => webHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0)),
                appExePath: Environment.ProcessPath);

            await webApp.StartAsync();
            _host = new WebHostHandle(webApp);

            // 2) 端口写日志，便于排查
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                    $"port={_host.Port}\nwebroot={webRoot}\n");
            }
            catch { }

            // 3) 主窗口（WebView2 渲染前端）—— Show 即自动弹出，Activate 置前抢焦点
            var window = new MainWindow(_host.Port);
            window.Show();
            window.Activate();
        }
        catch (Exception ex)
        {
            MessageBox.Show("启动失败: " + ex.Message, "图片查看器",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _host?.Stop(); } catch { }
        base.OnExit(e);
    }

    /// <summary>定位前端 wwwroot：优先 exe 目录下 wwwroot（发布版自包含）；否则向上找仓库根 src/ImageViewer/wwwroot（开发期）。</summary>
    private static string ResolveWebRoot()
    {
        var publishWwwRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(publishWwwRoot)) return publishWwwRoot;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                var dev = Path.Combine(dir.FullName, "src", "ImageViewer", "wwwroot");
                if (Directory.Exists(dev)) return dev;
                break;
            }
            dir = dir.Parent;
        }
        return publishWwwRoot;
    }
}
