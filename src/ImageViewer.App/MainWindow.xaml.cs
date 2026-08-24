using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using ImageViewer.App.Bridge;
using Microsoft.Web.WebView2.Core;

namespace ImageViewer.App.Wpf;

public partial class MainWindow : Window
{
    private readonly int _port;
    private readonly WindowControlBridge _bridge;

    public MainWindow(int port)
    {
        InitializeComponent();
        _port = port;
        _bridge = new WindowControlBridge(this);
        SetWindowIcon();
        Loaded += async (_, _) => await InitWebViewAsync();
    }

    /// <summary>从程序集内嵌 Resource 加载窗口/任务栏图标（exe 旁不留 .ico 文件）。
    /// 不能用 XAML Icon 属性——那会触发 Baml2006.TypeConverterMarkupExtension 启动异常。</summary>
    private void SetWindowIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(
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

    private async Task InitWebViewAsync()
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
            webView.CoreWebView2.Navigate($"http://127.0.0.1:{_port}/");
        }
        catch (Exception ex)
        {
            MessageBox.Show("WebView2 初始化失败（需要 WebView2 运行时，Windows 10/11 一般已内置）:\n" + ex.Message,
                "图片查看器", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
