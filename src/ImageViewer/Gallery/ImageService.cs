using System.Collections.Concurrent;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Processing;

namespace ImageViewer.Gallery;

/// <summary>图片浏览服务：目录扫描、缩略图（内存 + 加密磁盘缓存）、旋转导出、自然排序。</summary>
public sealed class ImageService
{
    private const int ThumbCacheLimit = 500;
    private readonly ConcurrentDictionary<string, byte[]> _thumbCache = new();
    private readonly ThumbCache _thumbDisk = new();   // 磁盘缩略图缓存（AES 加密，程序同目录 .thumbcache/）

    /// <summary>默认图片目录（绝对路径）。</summary>
    public string Root { get; }

    public ImageService(string? configuredRoot)
    {
        Root = ResolveConfiguredRoot(configuredRoot);
    }

    // ---------- 目录扫描 ----------

    /// <summary>扫描目录。root 为前端当前「相册根」（空则用默认 Root）：根处 is_root=true，前端不可再回退。</summary>
    public FolderListing Scan(string? path, string? root)
    {
        var abs = ResolvePath(path);
        // 缩略图缓存文件夹是程序内部数据，不允许当作相册浏览
        if (IsCachePath(abs))
            throw new InvalidOperationException("无法将缓存文件夹作为相册");
        var currentRoot = string.IsNullOrWhiteSpace(root) ? Root : ResolvePath(root);
        var isRoot = string.Equals(abs, currentRoot, StringComparison.OrdinalIgnoreCase);

        // 默认目录缺失（未捆绑示例图、用户尚未添加相册）→ 返回空列表，前端显示「添加文件夹」引导
        if (!Directory.Exists(abs))
        {
            if (string.IsNullOrWhiteSpace(path) && string.IsNullOrWhiteSpace(root))
                return new FolderListing(
                    abs,
                    Path.GetFileName(abs) is { Length: > 0 } n ? n : abs,
                    null,
                    true,
                    new List<PhotoInfo>(),
                    new List<AlbumInfo>());
            throw new DirectoryNotFoundException($"目录不存在: {abs}");
        }

        var photos = Directory.EnumerateFiles(abs)
            .Where(f => ContentTypeFor(f) is not null)
            .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
            .Select(f =>
            {
                var info = new FileInfo(f);
                return new PhotoInfo(
                    info.Name,
                    f,
                    info.Length,
                    info.LastWriteTime,
                    info.CreationTime,
                    PhotoUrl(f),
                    ThumbUrl(f));
            })
            .ToList();

        var albums = Directory.EnumerateDirectories(abs)
            .Where(d => !IsCachePath(d))     // 排除缩略图缓存目录，避免它被当作相册展示
            .Select(DescribeAlbum)
            .Where(a => a is not null)
            .Cast<AlbumInfo>()
            .OrderBy(a => a.Name, NaturalComparer.Instance)
            .ToList();

        var parent = isRoot ? null : Directory.GetParent(abs)?.FullName;
        return new FolderListing(
            abs,
            string.IsNullOrEmpty(Path.GetFileName(abs)) ? abs : Path.GetFileName(abs)!,
            parent,
            isRoot,
            photos,
            albums);
    }

    /// <summary>统计一个文件夹作为相册：count = 递归统计（含所有嵌套子目录）的图片总数，cover = 递归第一张。
    /// 无图片返回 null。也用于「已链接相册」列表。</summary>
    public static AlbumInfo? DescribeAlbum(string dir)
    {
        var all = EnumerateImages(dir).ToList();
        if (all.Count == 0) return null;
        return new AlbumInfo(Path.GetFileName(dir), dir, all.Count, ThumbUrl(all[0]));
    }

    /// <summary>递归枚举目录下全部图片（含所有嵌套子目录），跳过无权限/损坏目录。</summary>
    private static IEnumerable<string> EnumerateImages(string dir)
    {
        List<string> files;
        List<string> subs;
        try
        {
            files = Directory.EnumerateFiles(dir)
                .Where(f => ContentTypeFor(f) is not null)
                .OrderBy(f => Path.GetFileName(f), NaturalComparer.Instance)
                .ToList();
            subs = Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or DirectoryNotFoundException)
        {
            yield break;
        }

        foreach (var f in files) yield return f;
        foreach (var sub in subs)
            foreach (var f in EnumerateImages(sub))
                yield return f;
    }

    // ---------- 图片访问 ----------

    /// <summary>由绝对路径构建图片信息（用于标签筛选结果等）。无效/非图片返回 null。</summary>
    public static PhotoInfo? BuildPhoto(string absPath)
    {
        if (!File.Exists(absPath)) return null;
        if (ContentTypeFor(absPath) is null) return null;
        var info = new FileInfo(absPath);
        return new PhotoInfo(info.Name, absPath, info.Length, info.LastWriteTime, info.CreationTime, PhotoUrl(absPath), ThumbUrl(absPath));
    }

    /// <summary>打开原图流。路径无效或不是图片返回 null。</summary>
    public (Stream Stream, string ContentType)? OpenPhoto(string? path)
    {
        var abs = ResolvePath(path);
        if (!File.Exists(abs)) return null;
        var ct = ContentTypeFor(abs);
        if (ct is null) return null;
        return (File.OpenRead(abs), ct);
    }

    /// <summary>取缩略图（最大边不超过 max，JPEG）：内存 → 加密磁盘缓存 → 生成（生成后加密落盘，明文不落盘）。
    /// GIF 保留动画直接返回原文件。缓存键含源文件修改时间，图片被替换后自动重生成。</summary>
    public byte[]? GetThumbnail(string? path, int max)
    {
        var abs = ResolvePath(path);
        if (!File.Exists(abs) || ContentTypeFor(abs) is null) return null;
        // GIF：缩略图也要动图——直接返回原文件（不压缩不落盘缓存）
        if (Path.GetExtension(abs).ToLowerInvariant() == ".gif")
            return File.ReadAllBytes(abs);
        max = Math.Clamp(max, 32, 512);

        // 缓存键 = 源路径|尺寸|修改时间 的哈希（图片被替换 → 键变化 → 重生成）
        var diskKey = ThumbCache.KeyFor(abs, max, File.GetLastWriteTimeUtc(abs));

        if (_thumbCache.TryGetValue(diskKey, out var cached)) return cached;         // 1) 内存
        var diskBytes = _thumbDisk.Read(diskKey);                                     // 2) 加密磁盘缓存
        if (diskBytes is not null)
        {
            if (_thumbCache.Count >= ThumbCacheLimit) _thumbCache.Clear();
            _thumbCache[diskKey] = diskBytes;
            return diskBytes;
        }

        // 3) 生成 + 加密落盘 + 内存
        var bytes = File.ReadAllBytes(abs);
        using var image = Image.Load(bytes);
        image.Mutate(x => x.Resize(new ResizeOptions
        {
            Size = new Size(max, max),
            Mode = ResizeMode.Max
        }));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new JpegEncoder { Quality = 82 });
        var outBytes = ms.ToArray();
        _thumbDisk.Write(diskKey, outBytes);
        if (_thumbCache.Count >= ThumbCacheLimit) _thumbCache.Clear();
        _thumbCache[diskKey] = outBytes;
        return outBytes;
    }

    /// <summary>旋转（顺时针 0/90/180/270）后返回图片字节，供导出下载。</summary>
    public (byte[] Bytes, string ContentType)? RotateAndExport(string? path, int degrees)
    {
        var abs = ResolvePath(path);
        if (!File.Exists(abs)) return null;
        var ct = ContentTypeFor(abs);
        if (ct is null) return null;
        degrees = ((degrees % 360) + 360) % 360;

        var bytes = File.ReadAllBytes(abs);
        var format = Image.DetectFormat(bytes);
        using var image = Image.Load(bytes);
        if (degrees != 0)
            image.Mutate(x => x.Rotate(degrees)); // ImageSharp 正角度 = 顺时针
        using var ms = new MemoryStream();
        image.Save(ms, format);
        return (ms.ToArray(), ct);
    }

    // ---------- 路径解析 ----------

    /// <summary>缩略图缓存目录绝对路径（供前端/校验使用）。</summary>
    public string CacheDirectoryPath => _thumbDisk.DirectoryPath;

    /// <summary>路径是否为缩略图缓存目录（或其内部子目录）——不允许作为相册浏览/添加。</summary>
    public bool IsCachePath(string absPath)
    {
        var cache = _thumbDisk.DirectoryPath;
        if (string.IsNullOrEmpty(cache)) return false;
        return string.Equals(absPath, cache, StringComparison.OrdinalIgnoreCase)
            || absPath.StartsWith(cache.TrimEnd('\\', '/') + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>把请求里的 path 解析为绝对路径：空 → 默认目录；相对 → 相对默认目录；绝对 → 直接用。</summary>
    public string ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return Root;
        return Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Root, path));
    }

    private static string ResolveConfiguredRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        // 默认目录 = exe 旁 pictures/（发布版不再捆绑示例图：相册由用户「添加文件夹」链接外部目录，
        // 默认目录缺失时 Scan 返回空列表并引导添加，见 Scan）。
        var publishPictures = Path.Combine(AppContext.BaseDirectory, "pictures");
#if DEBUG
        // 仅开发期：exe 旁没有 pictures 时，向上找仓库根 src 旁 pictures/ 作示例数据
        if (Directory.Exists(publishPictures)) return publishPictures;
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
                return Path.Combine(dir.FullName, "pictures");
            dir = dir.Parent;
        }
#endif
        return publishPictures;
    }

    // ---------- 辅助 ----------

    private static string PhotoUrl(string absPath) => $"/api/photo?path={Uri.EscapeDataString(absPath)}";
    private static string ThumbUrl(string absPath) => $"/api/thumb?path={Uri.EscapeDataString(absPath)}&max=256";

    /// <summary>支持的图片扩展名 → MIME 类型；不支持返回 null。</summary>
    public static string? ContentTypeFor(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".jfif" or ".jpe" => "image/jpeg",
            ".png" => "image/png",
            ".bmp" => "image/bmp",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            ".tif" or ".tiff" => "image/tiff",
            ".ico" => "image/x-icon",
            _ => null
        };

    /// <summary>全部支持的图片扩展名（带点小写），供设置页「文件关联」勾选使用。</summary>
    public static readonly string[] SupportedExtensions =
    {
        ".jpg", ".jpeg", ".jfif", ".jpe",
        ".png",
        ".bmp",
        ".gif",
        ".webp",
        ".tif", ".tiff",
        ".ico",
    };
}

/// <summary>自然排序（数字感知）：img2 排在 img10 之前，文件名大小写不敏感。</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (x is null) return y is null ? 0 : -1;
        if (y is null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            char cx = x[i], cy = y[j];
            if (char.IsDigit(cx) && char.IsDigit(cy))
            {
                // 整段数字按数值比较（去前导零）
                int si = i, sj = j;
                while (i < x.Length && char.IsDigit(x[i])) i++;
                while (j < y.Length && char.IsDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                if (a.Length != b.Length) return a.Length.CompareTo(b.Length);
                int c = string.CompareOrdinal(a, b);
                if (c != 0) return c;
            }
            else
            {
                int c = char.ToLowerInvariant(cx).CompareTo(char.ToLowerInvariant(cy));
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        return (x.Length - i).CompareTo(y.Length - j);
    }
}
