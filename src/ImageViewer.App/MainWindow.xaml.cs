using System.ComponentModel;
using System.IO;
using System.Net;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using ImageViewer.App;
using ImageViewer.App.Bridge;
using ImageViewer.App.Hosting;
using ImageViewer.App.Native;
using ImageViewer.Gallery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Web.WebView2.Core;

namespace ImageViewer.App.Wpf;

/// <summary>
/// 主窗口：Show 后立即显示（秒开体验），Kestrel 与 WebView2 在后台并行初始化，
/// 完成后加载前端页面；启动参数里的图片路径会交给前端打开查看器。
/// </summary>
public partial class MainWindow : Window
{
    private WebHostHandle? _host;
    private readonly string[] _startArgs;
    private readonly WindowControlBridge _bridge;
    private TrayIcon? _tray;
    private bool _forceExit;   // 托盘「退出」：真正关闭窗口并结束进程（绕过关闭模式拦截）

    public MainWindow(string[] startArgs)
    {
        InitializeComponent();
        _startArgs = startArgs;
        _bridge = new WindowControlBridge(this);
        SetWindowIcon();
        // 系统托盘：关闭主界面时按设置最小化到托盘（进程驻留）或退出程序；托盘「退出」才真正结束进程
        _tray = new TrayIcon(this);
        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            try { _host?.Stop(); } catch { }
            // 走到这里窗口必然真正关闭（驻留托盘模式下关闭被 OnClosing 拦截隐藏，不会触发 Closed）→ 结束进程
            System.Windows.Application.Current.Shutdown();
        };
        Loaded += async (_, _) =>
        {
            // 全屏时隐藏边缘缩放热区并禁用缩放；退出全屏时恢复（含拖动标题栏自动退出全屏）
            WindowChromeService.FullscreenChanged += ApplyFullscreenState;
            // 任务栏进度：前端通过 HTTP（/api/taskbar-progress）异步上报，这里注册回调设置到任务栏进度条
            // （不用 WebView2 bridge 高频调用——跨进程调用会阻塞 JS 主线程导致一键识别循环卡死）
            TaskbarProgress.Register((value, state) => Dispatcher.BeginInvoke(() =>
            {
                try
                {
                    TaskbarItemInfo.ProgressValue = value;
                    TaskbarItemInfo.ProgressState = state switch
                    {
                        1 => TaskbarItemProgressState.Normal,
                        2 => TaskbarItemProgressState.Indeterminate,
                        3 => TaskbarItemProgressState.Error,
                        4 => TaskbarItemProgressState.Paused,
                        _ => TaskbarItemProgressState.None,
                    };
                }
                catch { }
            }));
            await StartAsync();
        };
    }

    /// <summary>关闭主界面：按设置决定——最小化到托盘（进程驻留，托盘可恢复）或真正关闭退出程序。
    /// 前端标题栏关闭按钮（chromeHost.close → Close）与系统关闭都走这里。</summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_forceExit) return;   // 托盘「退出」：不拦截，真正关闭并退出
        if (new SettingsStore().GetCloseToTray())
        {
            e.Cancel = true;
            Hide();               // 最小化到系统托盘（进程驻留，WebView2/Kestrel 不销毁，托盘可恢复）
        }
        // 关闭模式=退出程序：允许关闭，Closed 事件里 Shutdown 结束进程
    }

    /// <summary>托盘「退出」：设置强制退出标志后关闭窗口，绕过关闭模式拦截，真正结束进程。</summary>
    public void RequestExit()
    {
        _forceExit = true;
        Close();
    }

    /// <summary>外部（单实例转发）请求打开图片：恢复窗口（托盘隐藏/最小化）置前并闪烁，主动通知前端显示图片。
    /// 不依赖前端轮询——窗口隐藏时 Chromium 会节流 JS 定时器，须由主进程主动推送。</summary>
    public void OpenImage(string path)
    {
        // 恢复窗口显示并置前闪烁
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOW);
        NativeMethods.SetForegroundWindow(hwnd);
        Activate();
        WindowChromeService.Flash(this);
        // 主动通知前端打开图片（WebView2 就绪时）
        try
        {
            if (webView.CoreWebView2 is not null)
                _ = webView.CoreWebView2.ExecuteScriptAsync(
                    "window.hivOpenPendingPhoto(" + System.Text.Json.JsonSerializer.Serialize(path) + ");");
        }
        catch { }
    }

    /// <summary>全屏（工作区最大化，不遮任务栏）时：隐藏窗口边缘 6px 缩放热区（WebView2 铺满整个窗口）
    /// 并禁止缩放窗口；退出全屏时恢复 6px 边缘与可缩放状态。拖动标题栏退出全屏同样会触发。
    /// 用 WindowChrome 的 ResizeBorderThickness 归零 + ResizeMode=NoResize 双保险确保全屏不可缩放。</summary>
    private void ApplyFullscreenState(bool fullscreen)
    {
        // WindowChrome 缩放热区：全屏归零（页面铺满），普通状态 3px
        if (WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.ResizeBorderThickness = fullscreen ? new Thickness(0) : new Thickness(3);
        // 全屏禁止缩放（阻止边缘缩放热区生效）
        ResizeMode = fullscreen ? ResizeMode.NoResize : ResizeMode.CanResize;
        // WebView2 边距：全屏铺满整个窗口（隐藏边缘），普通状态左右/下内缩 3px 露出缩放热区
        webView.Margin = fullscreen ? new Thickness(0) : new Thickness(0, 3, 3, 3);
    }

    /// <summary>从程序集内嵌 Resource 加载窗口/任务栏图标（exe 旁不留 .ico 文件）。
    /// 不能用 XAML Icon 属性——那会触发 Baml2006.TypeConverterMarkupExtension 启动异常。</summary>
    private void SetWindowIcon()
    {
        try
        {
            var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/HopingImageViewer.ico"))?.Stream;
            if (stream is null) return;
            using (stream)
            {
                var icon = new BitmapImage();
                icon.BeginInit();
                icon.StreamSource = stream;
                icon.CacheOption = BitmapCacheOption.OnLoad;   // 立即解码，随后可关闭流
                icon.EndInit();
                Icon = icon;
            }
        }
        catch { /* 图标加载失败不影响主界面 */ }
    }

    /// <summary>窗口已显示后异步初始化：内嵌 Kestrel → WebView2 → 页面（互不阻塞窗口显示）。</summary>
    private async Task StartAsync()
    {
        try
        {
            // 双击图片用本程序打开：启动参数里是图片路径 → 记为待打开图片（前端启动后打开查看器）
            foreach (var arg in _startArgs)
            {
                if (!string.IsNullOrWhiteSpace(arg) && ImageService.ContentTypeFor(arg) is not null)
                {
                    AppHost.PendingOpenPath = Path.GetFullPath(arg);
                    break;
                }
            }

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

            // 端口写日志，便于排查
            try
            {
                File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "startup.log"),
                    $"port={_host.Port}\nwebroot={webRoot}\n");
            }
            catch { }

            await InitWebViewAsync(_host.Port);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("启动失败: " + ex.Message, "图片查看器",
                MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private async Task InitWebViewAsync(int port)
    {
        try
        {
            // userDataFolder 必须显式指定（默认用户目录可能无写权限）
            var userDataFolder = Path.Combine(AppContext.BaseDirectory, ".webview2");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await webView.EnsureCoreWebView2Async(env);
            // 隐藏网页特征：禁用 DevTools、右键菜单、浏览器缩放、状态栏、浏览器快捷键（Ctrl+F 等）
            webView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            webView.CoreWebView2.Settings.IsZoomControlEnabled = false;
            webView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            webView.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            // 自绘标题栏的窗口控制桥（前端 window.chromeHost 调用：拖动/最小化/最大化/关闭）
            webView.CoreWebView2.AddHostObjectToScript("chromeHost", _bridge);
            await webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(WindowGlue.Script);
            webView.CoreWebView2.Navigate($"http://127.0.0.1:{port}/");
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show("WebView2 初始化失败（需要 WebView2 运行时，Windows 10/11 一般已内置）:\n" + ex.Message,
                "图片查看器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
