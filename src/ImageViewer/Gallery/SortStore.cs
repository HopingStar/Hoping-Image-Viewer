using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>
/// 相册排序设置存储：按相册目录（绝对路径）单独保存排序字段与升降序，
/// 持久化到程序同目录 data/sorts.json（便携式，随程序走）。
/// 默认按名称升序（未单独设置时）。
/// </summary>
public sealed class SortStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly Dictionary<string, (string By, string Order)> _sorts = new(StringComparer.OrdinalIgnoreCase);

    public SortStore()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "sorts.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<SortConfig>(File.ReadAllText(_file));
            if (doc?.Sorts is null) return;
            foreach (var kv in doc.Sorts)
            {
                var abs = Path.GetFullPath(kv.Key);
                var by = NormalizeBy(kv.Value?.By);
                var order = NormalizeOrder(kv.Value?.Order);
                _sorts[abs] = (by, order);
            }
        }
        catch { /* 配置损坏/不可读时忽略，用默认排序 */ }
    }

    private void Save()
    {
        try
        {
            var doc = new SortConfig
            {
                Sorts = _sorts.ToDictionary(kv => kv.Key, kv => new SortSetting { By = kv.Value.By, Order = kv.Value.Order }),
            };
            File.WriteAllText(_file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>取某个相册的排序设置（未单独设置返回默认：名称升序）。</summary>
    public (string By, string Order) Get(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        lock (_lock)
            return _sorts.TryGetValue(absPath, out var s) ? s : ("name", "asc");
    }

    /// <summary>保存某个相册的排序设置。空路径忽略。</summary>
    public void Set(string absPath, string? by, string? order)
    {
        if (string.IsNullOrWhiteSpace(absPath)) return;
        var normalizedBy = NormalizeBy(by);
        var normalizedOrder = NormalizeOrder(order);
        absPath = Path.GetFullPath(absPath);
        lock (_lock)
            _sorts[absPath] = (normalizedBy, normalizedOrder);
        Save();
    }

    private static string NormalizeBy(string? by) =>
        by is "modified" or "created" or "size" or "type" ? by : "name";

    private static string NormalizeOrder(string? order) =>
        order == "desc" ? "desc" : "asc";
}

/// <summary>sorts.json 的结构。</summary>
public sealed class SortConfig
{
    public Dictionary<string, SortSetting?> Sorts { get; set; } = new();
}

public sealed class SortSetting
{
    public string? By { get; set; }
    public string? Order { get; set; }
}
