using System.Security.Cryptography;
using System.Text;

namespace ImageViewer.Gallery;

/// <summary>
/// 缩略图磁盘缓存：缓存的缩略图先用 AES-256-GCM 加密再落盘（缩略图明文不落盘、源图更不落盘），
/// 读取时由本类解密后交给前端。缓存目录与程序同目录：.thumbcache/。
/// 缓存文件名 = 「源图片路径 + 尺寸 + 修改时间」的 SHA-256 十六进制：
/// 图片内容被替换（修改时间变化）后缓存键随之变化，自动失效并按新键重新生成。
/// </summary>
public sealed class ThumbCache
{
    public const string CacheDirName = ".thumbcache";
    private const int MaxFiles = 5000;          // 超过则清理最旧的，防无限增长（图片被替换后旧缓存变孤儿）
    private const int KeepFiles = 4000;

    // 加密密钥：由固定盐值哈希派生（本地加密主要防直接读取缓存文件，密钥随程序走）
    private static readonly byte[] Key =
        SHA256.HashData(Encoding.UTF8.GetBytes("HopingImageViewer.ThumbCache.v1"));

    private readonly string _dir;
    private int _writes;                        // 写入计数（周期性触发清理）

    public ThumbCache()
    {
        _dir = Path.Combine(AppContext.BaseDirectory, CacheDirName);
    }

    /// <summary>缓存目录绝对路径（用于拒绝把缓存文件夹当作相册）。</summary>
    public string DirectoryPath => _dir;

    /// <summary>生成缓存文件名（不含扩展名）：源路径|尺寸|修改时间 的 SHA-256 十六进制。</summary>
    public static string KeyFor(string absPath, int max, DateTime lastWriteUtc)
    {
        var raw = $"{absPath}|{max}|{lastWriteUtc.Ticks}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        var sb = new StringBuilder(hash.Length * 2);
        foreach (var b in hash) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }

    /// <summary>读取缓存并解密。不存在 / 解密失败返回 null（当作缓存缺失重新生成）。</summary>
    public byte[]? Read(string key)
    {
        var file = Path.Combine(_dir, key + ".thb");
        if (!File.Exists(file)) return null;
        try
        {
            var data = File.ReadAllBytes(file);
            if (data.Length < 12 + 16) return null;              // nonce + tag
            var nonce = data[..12];
            var tag = data.AsSpan(12, 16).ToArray();
            var cipher = data.AsSpan(28).ToArray();
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key, 16);
            aes.Decrypt(nonce, cipher, tag, plain);
            return plain;
        }
        catch { return null; }                                   // 损坏/密钥不符 → 按缓存缺失处理
    }

    /// <summary>加密并写入缓存。失败不抛异常（只影响缓存，不影响浏览）。</summary>
    public void Write(string key, byte[] plainBytes)
    {
        try
        {
            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var cipher = new byte[plainBytes.Length];
            using (var aes = new AesGcm(Key, 16))
                aes.Encrypt(nonce, plainBytes, cipher, tag);

            var data = new byte[12 + 16 + cipher.Length];
            Buffer.BlockCopy(nonce, 0, data, 0, 12);
            Buffer.BlockCopy(tag, 0, data, 12, 16);
            Buffer.BlockCopy(cipher, 0, data, 28, cipher.Length);

            File.WriteAllBytes(Path.Combine(_dir, key + ".thb"), data);
            if (++_writes % 64 == 0) Cleanup();
        }
        catch { /* 写入失败忽略 */ }
    }

    /// <summary>清理最旧的缓存文件，防止无限增长。</summary>
    public void Cleanup()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var files = Directory.EnumerateFiles(_dir, "*.thb").ToList();
            if (files.Count <= MaxFiles) return;
            var toDelete = files
                .OrderBy(f => File.GetLastWriteTimeUtc(f))
                .Take(files.Count - KeepFiles);
            foreach (var f in toDelete)
            {
                try { File.Delete(f); } catch { }
            }
        }
        catch { /* 清理失败忽略 */ }
    }
}
