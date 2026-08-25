namespace ImageViewer.Gallery;

/// <summary>单张图片信息（列表返回用）。URL 由后端生成，前端直接当 img src。</summary>
public sealed record PhotoInfo(
    string Name,
    string Path,
    long Size,
    DateTime Modified,
    DateTime Created,
    string Url,
    string ThumbUrl);

/// <summary>相册（子文件夹）信息。封面取该文件夹第一张图的缩略图。</summary>
public sealed record AlbumInfo(
    string Name,
    string Path,
    int Count,
    string? CoverThumbUrl);

/// <summary>目录浏览结果：当前目录的直接图片 + 子文件夹相册。</summary>
public sealed record FolderListing(
    string Path,
    string DisplayName,
    string? Parent,
    bool IsRoot,
    List<PhotoInfo> Photos,
    List<AlbumInfo> Albums);

/// <summary>「添加相册文件夹」请求体（POST /api/albums）。</summary>
public sealed class AddAlbumRequest
{
    public string? Path { get; set; }
}

/// <summary>给图片加/去标签的请求体（POST/DELETE /api/tags/image）。</summary>
public sealed class ImageTagRequest
{
    public string? Path { get; set; }
    public string? Tag { get; set; }
}

/// <summary>创建标签的请求体（POST /api/tags）。</summary>
public sealed class CreateTagRequest
{
    public string? Name { get; set; }
}

/// <summary>角色识别 API 配置请求体（POST /api/ai/config）。</summary>
public sealed class AiConfigRequest
{
    public string? ApiUrl { get; set; }
}

/// <summary>相册排序设置请求体（POST /api/sort）。path = 相册目录绝对路径。</summary>
public sealed class SortRequest
{
    public string? Path { get; set; }
    public string? By { get; set; }       // name | modified | created | size | type
    public string? Order { get; set; }    // asc | desc
}

/// <summary>文件关联设置请求体（POST /api/settings/fileassoc）。extensions = 勾选的扩展名（如 [".jpg", ".png"]）。</summary>
public sealed class FileAssocRequest
{
    public List<string>? Extensions { get; set; }
}

/// <summary>设置相册自定义封面请求体（POST /api/covers）。cover_path 为空 = 恢复默认第一张。</summary>
public sealed class CoverRequest
{
    public string? AlbumPath { get; set; }
    public string? CoverPath { get; set; }
}

/// <summary>极速查看器开关请求体（POST /api/fastviewer）。fast_viewer = 双击关联图片直接打开极速查看器。</summary>
public sealed class FastViewerRequest
{
    public bool FastViewer { get; set; }
}

/// <summary>任务栏进度上报请求体（POST /api/taskbar-progress）。value 0..1；state 0=无 1=绿 2=不定 3=红 4=黄。</summary>
public sealed class TaskbarProgressRequest
{
    public double Value { get; set; }
    public int State { get; set; }
}

/// <summary>应用偏好设置请求体（POST /api/prefs）。nullable：只更新传入的字段。</summary>
public sealed class PrefsRequest
{
    public bool? CloseToTray { get; set; }
    public bool? AiEnabled { get; set; }
    public int? ViewerBg { get; set; }
    public bool? FlashBg { get; set; }
    public string? EditorPath { get; set; }   // 非 null 才更新（含空串=清除，恢复自动探测）
    public bool? RightClickCloseViewer { get; set; }
    public bool? UpdateCheckEnabled { get; set; }
    public string? Lang { get; set; }
    public bool? ShowDetailError { get; set; }
    public string? Theme { get; set; }
}
