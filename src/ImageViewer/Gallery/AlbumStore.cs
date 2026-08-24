using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>
/// 已链接相册文件夹的持久化存储（只记路径，不复制文件——用户相册可能在任意位置）。
/// 配置存到程序同目录 data/albums.json（便携式，随程序走）。
/// </summary>
public sealed class AlbumStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private readonly List<string> _folders = new();

    public AlbumStore()
    {
        // 配置存到程序同目录 data/ 下（便携式，随程序走）；标签/相册用各自 .json
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "albums.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<AlbumConfig>(File.ReadAllText(_file));
            if (doc?.Folders is not null)
                foreach (var f in doc.Folders)
                    if (!string.IsNullOrWhiteSpace(f))
                        _folders.Add(Path.GetFullPath(f));
        }
        catch { /* 配置损坏/不可读时忽略，从空列表开始 */ }
    }

    private void Save()
    {
        try
        {
            var doc = new AlbumConfig { Folders = _folders.ToList() };
            File.WriteAllText(_file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>全部已链接文件夹（绝对路径）。</summary>
    public IReadOnlyList<string> GetAll()
    {
        lock (_lock) return _folders.ToList();
    }

    /// <summary>添加链接（已存在则忽略）。返回 true 表示有变更并已保存。</summary>
    public bool Add(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        lock (_lock)
        {
            if (_folders.Any(f => string.Equals(f, absPath, StringComparison.OrdinalIgnoreCase))) return false;
            _folders.Add(absPath);
        }
        Save();
        return true;
    }

    /// <summary>移除链接。返回 true 表示有变更并已保存。</summary>
    public bool Remove(string absPath)
    {
        absPath = Path.GetFullPath(absPath);
        bool removed;
        lock (_lock)
        {
            removed = _folders.RemoveAll(f => string.Equals(f, absPath, StringComparison.OrdinalIgnoreCase)) > 0;
        }
        if (removed) Save();
        return removed;
    }
}

/// <summary>albums.json 的结构。</summary>
public sealed class AlbumConfig
{
    public List<string> Folders { get; set; } = new();
}
