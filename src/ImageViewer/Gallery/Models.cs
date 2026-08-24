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
