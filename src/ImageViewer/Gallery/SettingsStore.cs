using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>应用设置存储（极速查看器开关等），持久化到程序同目录 data/settings.json（随程序走）。
/// 桌面宿主启动时直接读取，前端通过 /api/fastviewer 读写同一文件。</summary>
public sealed class SettingsStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private bool _fastViewer;

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
            _fastViewer = doc?.FastViewer ?? false;
        }
        catch { /* 配置损坏/不可读时忽略，按未开启处理 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(new SettingsData { FastViewer = _fastViewer },
                new JsonSerializerOptions { WriteIndented = true }));
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
}

/// <summary>settings.json 的结构。</summary>
public sealed class SettingsData
{
    public bool FastViewer { get; set; }
}
