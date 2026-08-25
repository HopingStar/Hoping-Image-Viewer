using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using ImageViewer.Gallery;

namespace ImageViewer.App;

/// <summary>托盘右键弹出的炫酷菜单：无边框圆角深色面板 + 阴影 + 淡入动画。
/// 点击菜单外（失焦）/ Esc 关闭；ShowInTaskbar=false，不占任务栏。菜单项动作由 TrayIcon 注入回调。</summary>
public partial class TrayMenuWindow : Window
{
    private readonly Action _onOpen;
    private readonly Action _onFlash;
    private readonly Action _onExit;
    private bool _closing;   // 防 Deactivated 递归关闭

    public TrayMenuWindow(Action onOpen, Action onFlash, Action onExit, bool flashEnabled)
    {
        InitializeComponent();
        _onOpen = onOpen;
        _onFlash = onFlash;
        _onExit = onExit;
        // 配置中关闭了极速查看器 → Flash 菜单项变灰不可点
        FlashBtn.IsEnabled = flashEnabled;
        // 菜单文本本地化（读当前语言）
        var lang = new SettingsStore().GetLang();
        openText.Text = Loc.T("打开主界面", lang);
        flashText.Text = Loc.T("Flash 查看器", lang);
        exitText.Text = Loc.T("退出", lang);
        // 淡入动画
        Loaded += (_, _) =>
        {
            Opacity = 0;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        };
    }

    // 统一关闭入口：_closing 防重入——点菜单项时先置位再 Close，
    // 否则 Close 过程中窗口失活触发 Deactivated 会再次 Close，抛「窗口关闭期间无法 Close」异常导致进程退出
    private void CloseMenu()
    {
        if (_closing) return;
        _closing = true;
        try { Close(); } catch { }
    }

    // 关闭菜单后再回调动作（延迟到消息循环，避免在窗口关闭过程中同步触发回调）
    private void Open_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Dispatcher.BeginInvoke(() => { try { _onOpen(); } catch { } });
    }

    private void Flash_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Dispatcher.BeginInvoke(() => { try { _onFlash(); } catch { } });
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        CloseMenu();
        Dispatcher.BeginInvoke(() => { try { _onExit(); } catch { } });
    }

    // 点击菜单外（窗口失焦）→ 关闭
    private void Window_Deactivated(object sender, EventArgs e) => CloseMenu();

    // Esc → 关闭
    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape) CloseMenu();
    }
}
