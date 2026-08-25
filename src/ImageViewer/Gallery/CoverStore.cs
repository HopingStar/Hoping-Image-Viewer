using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>相册自定义封面的持久化存储：相册绝对路径 → 封面图片绝对路径。
/// 配置存到程序同目录 data/covers.json（便携式，随程序走）。未配置的相册用默认第一张图。</summary>
public sealed class CoverStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<string, string> _covers = new(StringComparer.OrdinalIgnoreCase);

    public CoverStore()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "covers.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<CoverConfig>(File.ReadAllText(_file));
            if (doc?.Covers is not null)
                foreach (var kv in doc.Covers)
                    if (!string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrWhiteSpace(kv.Value))
                        _covers[kv.Key] = kv.Value;
        }
        catch { /* 配置损坏/不可读时忽略，从空列表开始 */ }
    }

    private void Save()
    {
        try
        {
            var doc = new CoverConfig { Covers = _covers.ToDictionary(kv => kv.Key, kv => kv.Value) };
            File.WriteAllText(_file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>查询相册的自定义封面图片路径（未配置返回 null）。</summary>
    public string? Get(string albumPath)
    {
        lock (_lock) return _covers.TryGetValue(albumPath, out var c) ? c : null;
    }

    /// <summary>设置相册自定义封面（coverPath 为空 = 清除，恢复默认第一张）。</summary>
    public void Set(string albumPath, string coverPath)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(coverPath)) _covers.Remove(albumPath);
            else _covers[albumPath] = coverPath;
        }
        Save();
    }
}

/// <summary>covers.json 的结构。</summary>
public sealed class CoverConfig
{
    public Dictionary<string, string> Covers { get; set; } = new();
}
