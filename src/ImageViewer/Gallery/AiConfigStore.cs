using System.Text.Json;

namespace ImageViewer.Gallery;

/// <summary>
/// 角色识别 API 配置存储：只存 API 基地址（如 http://localhost:5210），
/// 持久化到程序同目录 data/ai.json（随程序走）。
/// </summary>
public sealed class AiConfigStore
{
    private readonly string _file;
    private readonly object _lock = new();
    private string? _apiUrl;

    public AiConfigStore()
    {
        // 配置存到程序同目录 data/ 下（便携式，随程序走）
        var dir = Path.Combine(AppContext.BaseDirectory, "data");
        try { Directory.CreateDirectory(dir); } catch { }
        _file = Path.Combine(dir, "ai.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_file)) return;
            var doc = JsonSerializer.Deserialize<AiConfig>(File.ReadAllText(_file));
            _apiUrl = Normalize(doc?.ApiUrl);
        }
        catch { /* 配置损坏/不可读时忽略，按未配置处理 */ }
    }

    private void Save()
    {
        try
        {
            File.WriteAllText(_file, JsonSerializer.Serialize(new AiConfig { ApiUrl = _apiUrl },
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 写入失败不阻断主流程 */ }
    }

    /// <summary>当前配置的角色识别 API 基地址（如 http://localhost:5210），未配置为 null。</summary>
    public string? GetApiUrl()
    {
        lock (_lock) return _apiUrl;
    }

    /// <summary>保存 API 基地址。空值 = 清除配置。</summary>
    public void SetApiUrl(string? url)
    {
        lock (_lock) _apiUrl = Normalize(url);
        Save();
    }

    /// <summary>规整 API 基地址：去首尾空白/尾部斜杠；去掉误填的尾部「/api」或完整「/api/predict/file」端点路径
    /// （代理层会再拼 /api/predict/file，避免填成 /api 导致双 /api 404）。</summary>
    private static string? Normalize(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var u = url.Trim().TrimEnd('/');
        const string predict = "/api/predict/file";
        if (u.EndsWith(predict, StringComparison.OrdinalIgnoreCase))
            u = u[..^predict.Length].TrimEnd('/');
        else if (u.EndsWith("/api", StringComparison.OrdinalIgnoreCase))
            u = u[..^4].TrimEnd('/');
        return u.Length == 0 ? null : u;
    }
}

/// <summary>ai.json 的结构。</summary>
public sealed class AiConfig
{
    public string? ApiUrl { get; set; }
}
