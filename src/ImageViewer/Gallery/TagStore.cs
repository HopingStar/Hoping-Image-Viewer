using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>
/// 图片标签存储：标签定义 + 图片↔标签映射，持久化到程序同目录 data/tags.json（便携式，随程序走）。
/// 支持按多个标签取交集筛选。
/// </summary>
public sealed class TagStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly SortedSet<string> _tags = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SortedSet<string>> _imageTags = new(StringComparer.OrdinalIgnoreCase);

    public TagStore()
    {
        // 标签存到程序同目录 data/tags.json（便携式，随程序走）
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "tags.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<TagConfig>(File.ReadAllText(_file));
            if (doc is null) return;
            foreach (var t in doc.Tags)
                if (!string.IsNullOrWhiteSpace(t))
                    _tags.Add(t.Trim());
            foreach (var kv in doc.Images)
                if (!string.IsNullOrWhiteSpace(kv.Key))
                    _imageTags[Path.GetFullPath(kv.Key)] =
                        new SortedSet<string>(kv.Value, StringComparer.OrdinalIgnoreCase);
        }
        catch { /* 配置损坏/不可读时忽略 */ }
    }

    private void Save()
    {
        try
        {
            var doc = new TagConfig
            {
                Tags = _tags.ToList(),
                Images = _imageTags.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
            };
            File.WriteAllText(_file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>全部已定义标签（按名称自然排序）。</summary>
    public IReadOnlyList<string> GetAllTags()
    {
        lock (_lock) return _tags.ToList();
    }

    /// <summary>某张图片的标签。</summary>
    public IReadOnlyList<string> GetTagsForImage(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        lock (_lock)
            return _imageTags.TryGetValue(absPath, out var tags) ? tags.ToList() : new List<string>();
    }

    /// <summary>给图片加标签（标签不存在则自动创建）。返回 true 表示有变更并已保存。</summary>
    public bool AddImageTag(string absPath, string tag)
    {
        absPath = Path.GetFullPath(absPath);
        tag = tag.Trim();
        if (tag.Length == 0) return false;
        lock (_lock)
        {
            _tags.Add(tag);
            if (!_imageTags.TryGetValue(absPath, out var set))
            {
                set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                _imageTags[absPath] = set;
            }
            if (!set.Add(tag)) return false;
        }
        Save();
        return true;
    }

    /// <summary>移除图片上的标签。返回 true 表示有变更并已保存。</summary>
    public bool RemoveImageTag(string absPath, string tag)
    {
        absPath = Path.GetFullPath(absPath);
        lock (_lock)
        {
            if (!_imageTags.TryGetValue(absPath, out var set)) return false;
            if (!set.Remove(tag)) return false;
            if (set.Count == 0) _imageTags.Remove(absPath);
        }
        Save();
        return true;
    }

    /// <summary>创建标签。已存在返回 false。</summary>
    public bool CreateTag(string tag)
    {
        tag = tag.Trim();
        if (tag.Length == 0) return false;
        lock (_lock)
        {
            if (!_tags.Add(tag)) return false;
        }
        Save();
        return true;
    }

    /// <summary>删除标签并从所有图片移除。返回 true 表示有变更并已保存。</summary>
    public bool DeleteTag(string tag)
    {
        lock (_lock)
        {
            if (!_tags.Remove(tag)) return false;
            var empty = new List<string>();
            foreach (var kv in _imageTags)
            {
                kv.Value.Remove(tag);
                if (kv.Value.Count == 0) empty.Add(kv.Key);
            }
            foreach (var k in empty) _imageTags.Remove(k);
        }
        Save();
        return true;
    }

    /// <summary>筛选同时包含全部指定标签的图片路径（交集）。无标签返回空。</summary>
    public IReadOnlyList<string> FilterPaths(IReadOnlyList<string> tags)
    {
        if (tags.Count == 0) return new List<string>();
        var tagSet = new HashSet<string>(tags, StringComparer.OrdinalIgnoreCase);
        lock (_lock)
            return _imageTags
                .Where(kv => tagSet.All(t => kv.Value.Contains(t)))
                .Select(kv => kv.Key)
                .ToList();
    }
}

/// <summary>tags.json 的结构。</summary>
public sealed class TagConfig
{
    public List<string> Tags { get; set; } = new();
    public Dictionary<string, List<string>> Images { get; set; } = new();
}
