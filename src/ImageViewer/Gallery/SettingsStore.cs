using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>应用设置存储（极速查看器 / 关闭模式 / 识别功能开关等），持久化到程序同目录 data/settings.json（随程序走）。
/// 桌面宿主启动时直接读取，前端通过 /api/fastviewer、/api/prefs 读写同一文件。</summary>
public sealed class SettingsStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private bool _fastViewer;
    private bool _closeToTray = true;   // 关闭主界面 → 最小化到托盘（默认，进程驻留）
    private bool _aiEnabled;            // 启用角色识别功能（默认关，普通用户用不上；开启后前端显示识别入口）
    private int _viewerBg;              // 主查看器背景：0=默认灰 1=白 2=黑（默认灰）
    private bool _flashBg;              // Flash 极速查看器背景：false=灰（默认） true=白
    private string _editorPath = "";    // 用户指定的图片编辑器 exe 路径（空 = 自动探测画图 / Paint.NET）
    private bool _rightClickClose = true;   // 查看器内右键直接关闭（默认开；关闭后右键可拖拽）
    private bool _updateCheck = true;   // 启动时自动检查更新（默认开；关闭后不检查也不显示标题栏 chip）
    private string _lang = "";          // 界面语言代码：空 = 跟随系统语言（前端检测）；否则为显式语言，如 en / ja / zh-TW / ko
    private bool _showDetailError;      // 错误提示附带底层异常详情（默认关；开启便于排查，详情为英文）
    private string _theme = "light";    // 主界面主题：light=浅色（默认） dark=深色（Flash 原生窗口不参与）

    public SettingsStore()
    {
        // 配置存到程序同目录 data/ 下（便携式，随程序走）
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<SettingsData>(File.ReadAllText(_file));
            // 属性为 bool?：旧配置文件缺失字段时为 null → 取默认值（不能用非 nullable，否则缺字段退化为 false）
            _fastViewer = doc?.FastViewer ?? false;
            _closeToTray = doc?.CloseToTray ?? true;
            _aiEnabled = doc?.AiEnabled ?? false;
            _viewerBg = doc?.ViewerBg ?? 0;
            _flashBg = doc?.FlashBg ?? false;
            _editorPath = doc?.EditorPath ?? "";
            _rightClickClose = doc?.RightClickCloseViewer ?? true;
            _updateCheck = doc?.UpdateCheckEnabled ?? true;
            _lang = doc?.Lang ?? "";
            _showDetailError = doc?.ShowDetailError ?? false;
            _theme = string.IsNullOrEmpty(doc?.Theme) ? "light" : doc.Theme!;
        }
        catch { /* 配置损坏/不可读时忽略，按默认值处理 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(new SettingsData
            {
                FastViewer = _fastViewer,
                CloseToTray = _closeToTray,
                AiEnabled = _aiEnabled,
                ViewerBg = _viewerBg,
                FlashBg = _flashBg,
                EditorPath = _editorPath,
                RightClickCloseViewer = _rightClickClose,
                UpdateCheckEnabled = _updateCheck,
                Lang = _lang,
                ShowDetailError = _showDetailError,
                Theme = _theme,
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>是否开启「极速查看器」：双击被关联的图片格式直接打开原生极速窗口（不加载 WebView2 主界面，秒开）。</summary>
    public bool GetFastViewer()
    {
        lock (_lock) return _fastViewer;
    }

    /// <summary>保存「极速查看器」开关状态。</summary>
    public void SetFastViewer(bool value)
    {
        lock (_lock) _fastViewer = value;
        Save();
    }

    /// <summary>是否「关闭主界面时最小化到系统托盘」（false = 关闭即退出程序）。</summary>
    public bool GetCloseToTray()
    {
        lock (_lock) return _closeToTray;
    }

    /// <summary>保存「关闭主界面」模式。</summary>
    public void SetCloseToTray(bool value)
    {
        lock (_lock) _closeToTray = value;
        Save();
    }

    /// <summary>是否启用「角色识别」功能（关闭时前端隐藏右键「识别角色」/ 工具栏「一键识别」等识别入口）。</summary>
    public bool GetAiEnabled()
    {
        lock (_lock) return _aiEnabled;
    }

    /// <summary>保存「角色识别」功能开关。</summary>
    public void SetAiEnabled(bool value)
    {
        lock (_lock) _aiEnabled = value;
        Save();
    }

    /// <summary>主查看器背景：0=默认灰 1=白 2=黑。</summary>
    public int GetViewerBg()
    {
        lock (_lock) return _viewerBg;
    }

    /// <summary>保存主查看器背景。</summary>
    public void SetViewerBg(int value)
    {
        lock (_lock) _viewerBg = Math.Clamp(value, 0, 2);
        Save();
    }

    /// <summary>Flash 极速查看器背景：false=灰 true=白。</summary>
    public bool GetFlashBg()
    {
        lock (_lock) return _flashBg;
    }

    /// <summary>保存 Flash 极速查看器背景。</summary>
    public void SetFlashBg(bool value)
    {
        lock (_lock) _flashBg = value;
        Save();
    }

    /// <summary>用户指定的图片编辑器 exe 路径（空字符串 = 自动探测画图 / Paint.NET）。</summary>
    public string GetEditorPath()
    {
        lock (_lock) return _editorPath;
    }

    /// <summary>保存图片编辑器路径（空字符串 = 恢复自动探测）。</summary>
    public void SetEditorPath(string value)
    {
        lock (_lock) _editorPath = (value ?? "").Trim();
        Save();
    }

    /// <summary>查看器内右键是否直接关闭（false = 右键可拖拽平移）。</summary>
    public bool GetRightClickCloseViewer()
    {
        lock (_lock) return _rightClickClose;
    }

    /// <summary>保存「右键关闭查看器」开关。</summary>
    public void SetRightClickCloseViewer(bool value)
    {
        lock (_lock) _rightClickClose = value;
        Save();
    }

    /// <summary>是否启动时自动检查更新。</summary>
    public bool GetUpdateCheck()
    {
        lock (_lock) return _updateCheck;
    }

    /// <summary>保存「启动时检查更新」开关。</summary>
    public void SetUpdateCheck(bool value)
    {
        lock (_lock) _updateCheck = value;
        Save();
    }

    /// <summary>界面语言代码（空 = 跟随系统；其他为显式语言，如 en / ja / zh-TW / ko）。</summary>
    public string GetLang()
    {
        lock (_lock) return _lang;
    }

    /// <summary>保存界面语言代码（空字符串 = 恢复跟随系统语言）。</summary>
    public void SetLang(string? value)
    {
        lock (_lock) _lang = (value ?? "").Trim();
        Save();
    }

    /// <summary>错误提示是否附带底层异常详情（默认关，开启便于排查）。</summary>
    public bool GetShowDetailError()
    {
        lock (_lock) return _showDetailError;
    }

    /// <summary>保存「显示详细错误信息」开关。</summary>
    public void SetShowDetailError(bool value)
    {
        lock (_lock) _showDetailError = value;
        Save();
    }

    /// <summary>主界面主题：dark / light。</summary>
    public string GetTheme()
    {
        lock (_lock) return _theme;
    }

    /// <summary>保存主界面主题。</summary>
    public void SetTheme(string? value)
    {
        lock (_lock) _theme = string.IsNullOrWhiteSpace(value) ? "dark" : value;
        Save();
    }
}

/// <summary>settings.json 的结构（bool?/int?：旧配置文件缺失的字段为 null，Load 时按字段默认值处理）。</summary>
public sealed class SettingsData
{
    public bool? FastViewer { get; set; }
    public bool? CloseToTray { get; set; }
    public bool? AiEnabled { get; set; }
    public int? ViewerBg { get; set; }
    public bool? FlashBg { get; set; }
    public string? EditorPath { get; set; }
    public bool? RightClickCloseViewer { get; set; }
    public bool? UpdateCheckEnabled { get; set; }
    public string? Lang { get; set; }
    public bool? ShowDetailError { get; set; }
    public string? Theme { get; set; }
}
