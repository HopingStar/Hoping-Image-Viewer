using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ImageViewer.App.Native;
using ImageViewer.Gallery;
using Point = System.Windows.Point;   // UseWindowsForms 引入了 System.Drawing/Forms，避免以下类型歧义
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using DragEventArgs = System.Windows.DragEventArgs;
using DragDropEffects = System.Windows.DragDropEffects;
using DataFormats = System.Windows.DataFormats;

namespace ImageViewer.App;

/// <summary>极速查看器：不依赖 WebView2/前端的原生轻量窗口，双击关联图片秒开。
/// 图片用 Stretch=Uniform 整图适配画布（保证完整居中显示，无裁剪），
/// 在此基础上用 ScaleTransform 缩放、RotateTransform 旋转、TranslateTransform 平移。
/// 点「回到相册」触发 ReturnToGalleryRequested（App 订阅：关闭本窗口 + 加载主界面）。</summary>
public partial class FastViewerWindow : Window
{
    private const double MinScale = 0.02;
    private const double MaxScale = 32;
    private const double Margin = 24;   // 图片四周留白（不贴画布边缘）
    private double _uniform = 1;      // Stretch=Uniform 的基准比例（图片适配画布）
    private double _fitScale = 1;     // 当前「适应窗口」的缩放值（用于双击切换）
    private double _angle;            // 当前旋转角度（0/90/180/270）
    private bool _dragging;
    private Point _dragStart;
    private bool _bgWhite;            // 画布背景：false=灰（原样） true=白
    private string _lang = "";        // 当前界面语言代码（本地化用）
    // ---- GIF 动画：WPF 内置 BitmapImage 对 GIF 动画不可靠；GifBitmapDecoder 的帧是未合成的局部帧，需按规范手工合成（偏移 + disposal），再定时切换 ----
    private DispatcherTimer? _gifTimer;
    private BitmapSource[]? _gifSources;   // 预合成的完整帧（全逻辑尺寸，切换时大小恒定、无解码延迟）
    private int[]? _gifDelays;             // 每帧延迟（毫秒）
    private int _gifIndex;

    public FastViewerWindow(string? path)
    {
        InitializeComponent();
        // 界面语言：本地化工具栏提示 / 空状态 / 回相册按钮
        _lang = new SettingsStore().GetLang();
        ApplyUi();
        // 画布背景：读取上次选择（灰原样 / 白）并应用
        _bgWhite = new SettingsStore().GetFlashBg();
        ApplyBg();
        Loaded += (_, _) =>
        {
            // path 为空（托盘「Flash 查看器」打开）→ 空画布提示拖入图片
            if (string.IsNullOrWhiteSpace(path)) ShowEmpty();
            else LoadImage(path);
        };
        // 兜底：若首帧布局未就绪导致未适配，渲染完成后补一次
        ContentRendered += (_, _) => { if (scale.ScaleX <= 0) FitToWindow(); };
        Closed += (_, _) => StopGifTimer();   // 窗口关闭时停掉 GIF 帧定时器
    }

    /// <summary>空状态（无图片）：画布留空，提示用户拖入图片查看。</summary>
    private void ShowEmpty()
    {
        img.Visibility = Visibility.Hidden;
        fileNameText.Text = Loc.T("拖入图片以快速查看", _lang);
        Title = "Hoping Image Flash v" + AppHost.AppVersion;
    }

    /// <summary>加载图片（初次打开或换图）。极速优先：直接解码显示；GIF 走手动帧动画，其余格式同步解码。</summary>
    public void LoadImage(string path)
    {
        try
        {
            StopGifTimer();
            bool isGif = Path.GetExtension(path).Equals(".gif", StringComparison.OrdinalIgnoreCase);
            if (isGif) LoadAnimatedGif(path);
            else
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(Path.GetFullPath(path));
                bmp.CacheOption = BitmapCacheOption.OnLoad;   // 静态图：OnLoad 立即解码（尺寸立即可用），Freeze 释放文件句柄
                bmp.EndInit();
                bmp.Freeze();
                img.Source = bmp;
            }
            img.Visibility = Visibility.Visible;
            var name = Path.GetFileName(path);
            fileNameText.Text = name;
            Title = "Hoping Image Flash v" + AppHost.AppVersion + " · " + name;
            _angle = 0;
            rotate.Angle = 0;
            _uniform = ComputeUniform();
            scale.ScaleX = scale.ScaleY = 1;   // Stretch=Uniform 即整图适配
            translate.X = translate.Y = 0;
            FitToWindow();
            // 兜底：画布尺寸可能此刻尚未就绪，等布局空闲后再适配一次
            Dispatcher.BeginInvoke(new Action(FitToWindow), DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            StopGifTimer();
            img.Visibility = Visibility.Collapsed;
            fileNameText.Text = Loc.T("无法打开图片", _lang) + ": " + ex.Message;
        }
    }

    /// <summary>加载动画 GIF：按 GIF 规范把各帧（多为局部帧 + 不同 disposal）合成到逻辑屏幕尺寸的完整帧，
    /// 再交给 DispatcherTimer 按每帧延迟播放。GifBitmapDecoder 的帧是未合成的原始局部帧，直接显示会
    /// 尺寸跳动 / 残影闪烁，故必须手工合成（帧偏移写入画布 + 逐帧 disposal 应用）。</summary>
    private void LoadAnimatedGif(string path)
    {
        _gifSources = null;
        _gifDelays = null;
        _gifIndex = 0;
        StopGifTimer();
        var decoder = new GifBitmapDecoder(
            new Uri(Path.GetFullPath(path)),
            BitmapCreateOptions.None,
            BitmapCacheOption.OnDemand);
        var frames = decoder.Frames;
        if (frames.Count < 2)   // 单帧 = 静态 GIF，按普通图片显示
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
            return;
        }

        var gif = ParseGifFrames(path);   // 帧信息（偏移 / disposal / 延迟）从文件二进制解析（WIC 的 DisposalMethod 查询不可靠）
        if (gif.Frames.Count < 2)
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(Path.GetFullPath(path));
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            img.Source = bmp;
            return;
        }
        int count = Math.Min(frames.Count, gif.Frames.Count);

        // 逻辑屏幕尺寸（GIF 全局画布，局部帧的偏移基准）；异常时退回首帧尺寸（多数 GIF 首帧为全屏背景帧）
        int W = gif.Width, H = gif.Height;
        if (W <= 0 || H <= 0) { W = frames[0].PixelWidth; H = frames[0].PixelHeight; }

        var pf = PixelFormats.Bgra32;
        int cStride = W * 4;
        var canvas = new byte[cStride * H];     // 合成画布（初始全透明）
        byte[]? prevBackup = null;               // 上一帧 disposal=3 时备份的画布
        int prevDisposal = 1;

        var sources = new BitmapSource[count];
        var delays = new int[count];

        for (int i = 0; i < count; i++)
        {
            var f = frames[i];
            var fi = gif.Frames[i];
            // 上一帧 disposal=3（恢复前一帧）：先把画布还原为显示上一帧前的状态
            if (prevDisposal == 3 && prevBackup is not null)
                Buffer.BlockCopy(prevBackup, 0, canvas, 0, canvas.Length);
            // 本帧 disposal=3：绘制前备份画布（本帧显示后需恢复到这个状态）
            if (fi.Disposal == 3)
            {
                prevBackup = new byte[canvas.Length];
                Buffer.BlockCopy(canvas, 0, prevBackup, 0, canvas.Length);
            }

            int fw = f.PixelWidth, fh = f.PixelHeight;

            // 帧像素统一转 Bgra32 后写入画布偏移处
            if (fw > 0 && fh > 0)
            {
                var conv = new FormatConvertedBitmap(f, pf, null, 0);
                int fStride = fw * 4;
                var px = new byte[fStride * fh];
                conv.CopyPixels(px, fStride, 0);
                CopyIntoCanvas(canvas, W, H, px, fw, fh, fi.Left, fi.Top, cStride, fStride);
            }

            // 输出本帧完整画面（独立冻结位图；画布后续还会被修改）
            var snap = new byte[canvas.Length];
            Buffer.BlockCopy(canvas, 0, snap, 0, canvas.Length);
            var src = BitmapSource.Create(W, H, 96, 96, pf, null, snap, cStride);
            src.Freeze();
            sources[i] = src;
            delays[i] = fi.DelayMs;

            // 应用本帧 disposal，为下一帧做准备
            if (fi.Disposal == 2)
                ClearRegion(canvas, W, H, fi.Left, fi.Top, fw, fh, cStride);
            prevDisposal = fi.Disposal;
        }

        _gifSources = sources;
        _gifDelays = delays;
        img.Source = sources[0];
        _gifTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delays[0]) };
        _gifTimer.Tick += (_, _) => GifTick();
        _gifTimer.Start();
    }

    /// <summary>把帧像素写入合成画布 (left, top) 处，处理画布边缘越界裁剪。
    /// GIF 帧为索引色，透明索引像素转换后 alpha=0；此类像素应跳过（露出画布已有内容），
    /// 否则会被当作不透明黑色覆盖到画布上，形成「故障像素」。故逐像素 alpha 感知拷贝。</summary>
    private static void CopyIntoCanvas(byte[] canvas, int W, int H, byte[] px, int fw, int fh,
                                       int left, int top, int cStride, int fStride)
    {
        for (int y = 0; y < fh; y++)
        {
            int cy = top + y;
            if (cy < 0 || cy >= H) continue;
            int copyW = fw, srcX = 0, dstX = left;
            if (dstX < 0) { srcX = -dstX; copyW -= srcX; dstX = 0; }
            if (dstX + copyW > W) copyW = W - dstX;
            if (copyW <= 0) continue;

            int sBase = y * fStride + srcX * 4;
            int dBase = cy * cStride + dstX * 4;
            for (int x = 0; x < copyW; x++)
            {
                int si = sBase + x * 4;
                if (px[si + 3] == 0) continue;          // 透明像素：跳过，保留画布已有内容
                canvas[dBase + x * 4] = px[si];
                canvas[dBase + x * 4 + 1] = px[si + 1];
                canvas[dBase + x * 4 + 2] = px[si + 2];
                canvas[dBase + x * 4 + 3] = 255;        // 不透明：设 alpha=255
            }
        }
    }

    /// <summary>把合成画布 (left, top, fw, fh) 区域清为透明（GIF disposal=2 恢复背景）。</summary>
    private static void ClearRegion(byte[] canvas, int W, int H, int left, int top, int fw, int fh, int cStride)
    {
        int x0 = Math.Max(left, 0), x1 = Math.Min(left + fw, W);
        int y0 = Math.Max(top, 0), y1 = Math.Min(top + fh, H);
        for (int y = y0; y < y1; y++)
            Array.Clear(canvas, y * cStride + x0 * 4, (x1 - x0) * 4);
    }

    /// <summary>单个 GIF 帧的图像信息（文件二进制解析得出，替代不可靠的 WIC metadata）。</summary>
    private readonly record struct GifFrameInfo(int Left, int Top, int Disposal, int DelayMs);

    /// <summary>GIF 解析结果：逻辑屏幕尺寸 + 每帧信息。</summary>
    private readonly record struct GifInfo(int Width, int Height, List<GifFrameInfo> Frames);

    /// <summary>从 GIF 文件二进制解析逻辑屏幕尺寸与每帧 偏移 / disposal / 延迟。
    /// WIC metadata 的 /grctlext/DisposalMethod 查询不可用（抛异常），故直接读 GIF 块结构。</summary>
    private static GifInfo ParseGifFrames(string path)
    {
        var frames = new List<GifFrameInfo>();
        var bytes = File.ReadAllBytes(path);
        int W = 0, H = 0;
        if (bytes.Length < 13) return new GifInfo(W, H, frames);
        W = bytes[6] | (bytes[7] << 8);       // 逻辑屏幕宽（LSD）
        H = bytes[8] | (bytes[9] << 8);       // 逻辑屏幕高
        int packed = bytes[10];
        int i = 13;
        if ((packed & 0x80) != 0) i += (2 << (packed & 7)) * 3;   // 跳过全局色表
        int disposal = 1, delay = 100;
        while (i < bytes.Length - 1)
        {
            byte b = bytes[i];
            if (b == 0x3B) break;             // 尾部结束标记
            if (b == 0x21)                    // 扩展块
            {
                byte label = bytes[i + 1];
                i += 2;
                if (label == 0xF9)            // 图形控制扩展（GCE）
                {
                    int sz = bytes[i]; i += 1;
                    if (sz >= 4)
                    {
                        disposal = (bytes[i] >> 2) & 7;                       // disposal 方法（bits 2-4）
                        delay = (bytes[i + 1] | (bytes[i + 2] << 8)) * 10;    // 1/100 秒 → 毫秒
                        if (delay <= 0) delay = 100;
                    }
                    i += sz + 1;              // 数据 + 终止符
                }
                else                          // 注释 / 应用扩展等：子块链
                {
                    while (i < bytes.Length && bytes[i] != 0)
                    {
                        int sz = bytes[i];
                        i += 1 + sz;
                    }
                    if (i < bytes.Length) i += 1;
                }
            }
            else if (b == 0x2C)               // 图像描述符
            {
                int left = bytes[i + 1] | (bytes[i + 2] << 8);
                int top = bytes[i + 3] | (bytes[i + 4] << 8);
                int w = bytes[i + 5] | (bytes[i + 6] << 8);
                int h = bytes[i + 7] | (bytes[i + 8] << 8);
                int imgPacked = bytes[i + 9];
                frames.Add(new GifFrameInfo(left, top, disposal, delay));
                i += 10;
                if ((imgPacked & 0x80) != 0) i += (2 << (imgPacked & 7)) * 3;   // 跳过局部色表
                i += 1;                       // LZW 最小码长
                while (i < bytes.Length && bytes[i] != 0)
                {
                    int sz = bytes[i];
                    i += 1 + sz;
                }
                if (i < bytes.Length) i += 1; // 数据子块终止符
                disposal = 1;                 // GCE 只作用于紧随的图像
                delay = 100;
            }
            else break;
        }
        return new GifInfo(W, H, frames);
    }

    /// <summary>定时器 tick：切到下一帧并按该帧延迟设置下一次间隔（帧已预合成，切换无解码延迟）。</summary>
    private void GifTick()
    {
        if (_gifSources is null || _gifDelays is null || _gifSources.Length == 0) return;
        _gifIndex = (_gifIndex + 1) % _gifSources.Length;
        img.Source = _gifSources[_gifIndex];
        if (_gifTimer is not null)
            _gifTimer.Interval = TimeSpan.FromMilliseconds(_gifDelays[_gifIndex]);
    }

    private void StopGifTimer()
    {
        _gifTimer?.Stop();
        _gifTimer = null;
    }

    /// <summary>Stretch=Uniform 的基准比例：图片（DIP 尺寸）适配「画布减去四周留白」的缩放，四周留 Margin 空白。</summary>
    private double ComputeUniform()
    {
        if (img.Source is not BitmapSource bs) return 1;
        double cw = canvas.ActualWidth - 2 * Margin, ch = canvas.ActualHeight - 2 * Margin;
        if (cw <= 0 || ch <= 0) return 1;
        double dpiX = bs.DpiX > 0 ? bs.DpiX : 96;
        double dpiY = bs.DpiY > 0 ? bs.DpiY : 96;
        double w = bs.PixelWidth * 96.0 / dpiX;
        double h = bs.PixelHeight * 96.0 / dpiY;
        if (w <= 0 || h <= 0) return 1;   // 异步解码中（GIF 首帧未就绪），尺寸未知时保持默认
        return Math.Clamp(Math.Min(cw / w, ch / h), MinScale, MaxScale);
    }

    /// <summary>适应窗口：未旋转时 Stretch=Uniform 即整图适配（scale=1）；
    /// 旋转 90/270 后宽高互换，需再乘一个缩放使旋转后的图完整显示。translate 归零居中。</summary>
    private void FitToWindow()
    {
        if (canvas.ActualWidth <= 0 || canvas.ActualHeight <= 0) return;
        _uniform = ComputeUniform();
        double s = _angle % 180 != 0 ? ComputeRotatedFit() : 1;
        scale.ScaleX = scale.ScaleY = s;
        translate.X = translate.Y = 0;
        _fitScale = s;
    }

    /// <summary>旋转 90/270 后：Uniform 内容（w*u, h*u）旋转后视觉为（h*u, w*u），
    /// 计算使其适配「画布减去四周留白」的缩放。</summary>
    private double ComputeRotatedFit()
    {
        if (img.Source is not BitmapSource bs) return 1;
        double cw = canvas.ActualWidth - 2 * Margin, ch = canvas.ActualHeight - 2 * Margin;
        if (cw <= 0 || ch <= 0) return 1;
        double dpiX = bs.DpiX > 0 ? bs.DpiX : 96;
        double dpiY = bs.DpiY > 0 ? bs.DpiY : 96;
        double w = bs.PixelWidth * 96.0 / dpiX;
        double h = bs.PixelHeight * 96.0 / dpiY;
        if (w <= 0 || h <= 0) return 1;   // 异步解码中（GIF 首帧未就绪）
        double vw = h * _uniform, vh = w * _uniform;
        return Math.Clamp(Math.Min(cw / vw, ch / vh), MinScale, MaxScale);
    }

    // ---- 缩放 ----

    /// <summary>以画布坐标 p 为中心缩放（鼠标中心缩放）：保持鼠标下的图片点位置不动。
    /// ScaleTransform 围绕画布中心（RenderTransformOrigin=0.5）缩放，translate 补偿使鼠标点固定。</summary>
    private void ZoomAt(Point p, double factor)
    {
        double newScale = Math.Clamp(scale.ScaleX * factor, MinScale, MaxScale);
        factor = newScale / scale.ScaleX;
        if (Math.Abs(factor - 1) < 1e-6) return;
        double cx = canvas.ActualWidth / 2;
        double cy = canvas.ActualHeight / 2;
        translate.X = (p.X - cx) * (1 - factor) + translate.X * factor;
        translate.Y = (p.Y - cy) * (1 - factor) + translate.Y * factor;
        scale.ScaleX *= factor;
        scale.ScaleY *= factor;
    }

    private void canvas_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        ZoomAt(e.GetPosition(canvas), e.Delta > 0 ? 1.15 : 1 / 1.15);
        e.Handled = true;
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 1.3);

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => ZoomAt(new Point(canvas.ActualWidth / 2, canvas.ActualHeight / 2), 1 / 1.3);

    private void Fit_Click(object sender, RoutedEventArgs e) => FitToWindow();

    private void canvas_SizeChanged(object sender, SizeChangedEventArgs e) => FitToWindow();

    /// <summary>双击：1:1 原尺寸 ↔ 适应窗口。</summary>
    private void ToggleActualSize()
    {
        if (Math.Abs(scale.ScaleX - _fitScale) < 0.01)
        {
            scale.ScaleX = scale.ScaleY = 1 / _uniform;   // 还原原尺寸（Stretch=Uniform 已缩小 _uniform 倍）
            translate.X = translate.Y = 0;
        }
        else
        {
            FitToWindow();
        }
    }

    // ---- 旋转 ----

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(int deg)
    {
        _angle = (_angle + deg + 360) % 360;
        rotate.Angle = _angle;
        FitToWindow();   // 旋转后宽高互换，重新适配
    }

    // ---- 平移（放大后拖拽） ----

    private void canvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount >= 2)
        {
            ToggleActualSize();
            e.Handled = true;
            return;
        }
        _dragging = true;
        _dragStart = e.GetPosition(canvas);
        canvas.CaptureMouse();
        e.Handled = true;
    }

    private void canvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetPosition(canvas);
        translate.X += p.X - _dragStart.X;
        translate.Y += p.Y - _dragStart.Y;
        _dragStart = p;
    }

    private void canvas_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        canvas.ReleaseMouseCapture();
    }

    /// <summary>把 Flash 窗口恢复到前台显示（最小化/后台时）并立即置前，不延迟。
    /// 置前成功则无需提示；只有置前失败（窗口未能到前台）时才闪烁任务栏黄闪提示有图片打开。</summary>
    public void BringToFront()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (!IsVisible) Show();
        Activate();
        bool ok = false;
        try
        {
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
            NativeMethods.SetForegroundWindow(hwnd);
            // 置前成功判定：窗口现在是前台窗口
            ok = NativeMethods.GetForegroundWindow() == hwnd;
        }
        catch { }
        if (!ok)
            WindowChromeService.Flash(this);
    }

    // ---- 拖入图片 ----

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedImagePath(e) is null ? DragDropEffects.None : DragDropEffects.Copy;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        var path = GetDroppedImagePath(e);
        if (path is not null) LoadImage(path);
        e.Handled = true;
    }

    /// <summary>从拖放数据中取出第一个图片文件的完整路径（非图片返回 null）。</summary>
    private static string? GetDroppedImagePath(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return null;
        var files = e.Data.GetData(DataFormats.FileDrop) as string[];
        if (files is null) return null;
        foreach (var f in files)
            if (ImageService.ContentTypeFor(f) is not null) return Path.GetFullPath(f);
        return null;
    }

    // ---- 回到相册 ----

    /// <summary>点「回到相册」时触发（App 订阅：关闭本窗口 + 加载主界面）。</summary>
    public event Action? ReturnToGalleryRequested;

    private void ReturnToGallery_Click(object sender, RoutedEventArgs e)
        => ReturnToGalleryRequested?.Invoke();

    /// <summary>按当前 _bgWhite 应用画布背景：灰（原样 #1b1e24）↔ 白。</summary>
    private void ApplyBg()
    {
        canvas.Background = new SolidColorBrush(_bgWhite
            ? System.Windows.Media.Colors.White
            : System.Windows.Media.Color.FromRgb(0x1b, 0x1e, 0x24));
    }

    /// <summary>切换画布背景并持久化（下次打开 Flash 保持）。</summary>
    private void Bg_Click(object sender, RoutedEventArgs e)
    {
        _bgWhite = !_bgWhite;
        ApplyBg();
        new SettingsStore().SetFlashBg(_bgWhite);
    }

    /// <summary>按当前语言更新工具栏按钮提示文本 / 回相册按钮文字（本地化）。</summary>
    private void ApplyUi()
    {
        fitBtn.ToolTip = Loc.T("适应窗口", _lang);
        zoomOutBtn.ToolTip = Loc.T("缩小", _lang);
        zoomInBtn.ToolTip = Loc.T("放大", _lang);
        rotLBtn.ToolTip = Loc.T("左旋 90°", _lang);
        rotRBtn.ToolTip = Loc.T("右旋 90°", _lang);
        bgBtn.ToolTip = Loc.T("切换背景（灰 / 白）", _lang);
        btnBack.Content = "🏠 " + Loc.T("回到相册", _lang);
    }
}
