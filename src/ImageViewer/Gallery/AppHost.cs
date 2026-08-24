using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace ImageViewer.Gallery;

/// <summary>组装图片查看器 Web 应用：静态前端（wwwroot）+ /api/* 端点。
/// 供两种宿主共用：浏览器模式（Program.cs，--urls 固定端口）与桌面模式（ImageViewer.App，内嵌 Kestrel 随机端口）。</summary>
public static class AppHost
{
    /// <summary>构建 WebApplication（未启动）。args 透传给命令行配置（如 --urls）；configureWebHost 在 Build 前调用，可注入 Kestrel 监听配置。</summary>
    public static WebApplication Build(
        string[]? args = null,
        WebApplicationOptions? options = null,
        Action<ConfigureWebHostBuilder>? configureWebHost = null)
    {
        // 优先 options（WPF 宿主：ContentRoot/WebRoot + 内嵌 Kestrel）；否则用 args（浏览器模式 --urls）
        WebApplicationBuilder builder = options is not null
            ? WebApplication.CreateBuilder(options)
            : args is not null
                ? WebApplication.CreateBuilder(args)
                : WebApplication.CreateBuilder();

        configureWebHost?.Invoke(builder.WebHost);

        // snake_case JSON 序列化（对齐工作区惯例）
        builder.Services.ConfigureHttpJsonOptions(o =>
            o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        // 图片浏览服务（单例：缩略图缓存常驻内存）；Images:Root 留空时自动定位仓库根 pictures/
        builder.Services.AddSingleton(new ImageService(builder.Configuration["Images:Root"]));
        // 已链接相册文件夹的持久化列表（存 LocalAppData，只记路径不复制文件）
        builder.Services.AddSingleton<AlbumStore>();
        // 图片标签存储（标签定义 + 图片↔标签映射）
        builder.Services.AddSingleton<TagStore>();
        // 角色识别 API 配置存储 + HTTP 客户端工厂（转发识别请求用）
        builder.Services.AddSingleton<AiConfigStore>();
        builder.Services.AddHttpClient();

        var app = builder.Build();

        // 全局异常 → JSON（目录/文件不存在 → 404，其余 → 400）
        app.Use(async (context, next) =>
        {
            try
            {
                await next();
            }
            catch (Exception ex)
            {
                context.Response.StatusCode = ex is DirectoryNotFoundException or FileNotFoundException
                    ? StatusCodes.Status404NotFound
                    : StatusCodes.Status400BadRequest;
                await context.Response.WriteAsJsonAsync(new { error = ex.Message });
            }
        });

        // 静态前端（wwwroot/）
        app.UseDefaultFiles();
        app.UseStaticFiles();

        var api = app.MapGroup("/api");

        // 列目录：返回当前目录的直接图片 + 子文件夹相册。path 缺省 = 默认图片目录；root 为相册根（根处 is_root，前端不可再回退）。
        api.MapGet("/photos", (ImageService svc, [FromQuery] string? path, [FromQuery] string? root) =>
            Results.Ok(svc.Scan(path, root)));

        // 原图流
        api.MapGet("/photo", (ImageService svc, [FromQuery] string? path) =>
        {
            var photo = svc.OpenPhoto(path);
            return photo is null
                ? Results.BadRequest(new { error = "图片不存在或格式不支持" })
                : Results.File(photo.Value.Stream, photo.Value.ContentType);
        });

        // 缩略图（生成后内存缓存；GIF 直接返回原动图）
        api.MapGet("/thumb", (ImageService svc, [FromQuery] string? path, [FromQuery] int? max) =>
        {
            var bytes = svc.GetThumbnail(path, max ?? 256);
            if (bytes is null)
                return Results.BadRequest(new { error = "图片不存在或格式不支持" });
            var ct = path is not null && Path.GetExtension(path).ToLowerInvariant() == ".gif"
                ? "image/gif"
                : "image/jpeg";
            return Results.File(bytes, ct);
        });

        // 导出旋转后的图片（下载）
        api.MapGet("/photo/export", (ImageService svc, [FromQuery] string? path, [FromQuery] int? rotate) =>
        {
            var result = svc.RotateAndExport(path, rotate ?? 0);
            return result is null
                ? Results.BadRequest(new { error = "图片不存在或格式不支持" })
                : Results.File(result.Value.Bytes, result.Value.ContentType, $"rotated_{Path.GetFileName(path)}");
        });

        // ---------- 已链接相册文件夹（用户在其他位置的相册，直接链接不复制） ----------

        // 列出所有已链接相册（不存在的目录自动跳过）
        api.MapGet("/albums", (ImageService svc, AlbumStore store) =>
        {
            var albums = store.GetAll()
                .Where(Directory.Exists)
                .Select(ImageService.DescribeAlbum)
                .Where(a => a is not null)
                .Cast<AlbumInfo>()
                .OrderBy(a => a.Name, NaturalComparer.Instance)
                .ToList();
            return Results.Ok(new { albums });
        });

        // 添加相册文件夹链接：仅校验目录存在并写入持久化列表（不枚举图片，避免大目录阻塞）。
        // 相册信息（数量/封面）由前端随后 GET /api/albums 获取。
        api.MapPost("/albums", (AlbumStore store, [FromBody] AddAlbumRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "路径不能为空" });
            string abs;
            try { abs = Path.GetFullPath(req.Path); }
            catch { return Results.BadRequest(new { error = "路径无效" }); }
            if (!Directory.Exists(abs))
                return Results.BadRequest(new { error = $"文件夹不存在: {abs}" });
            store.Add(abs);
            return Results.Ok(new { ok = true, path = abs });
        });

        // 移除相册文件夹链接（只移除链接，不删除文件夹本身）
        api.MapDelete("/albums", (AlbumStore store, [FromQuery] string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "路径不能为空" });
            try { store.Remove(Path.GetFullPath(path)); }
            catch { return Results.BadRequest(new { error = "路径无效" }); }
            return Results.Ok(new { ok = true });
        });

        // ---------- 图片标签 ----------

        // 全部已定义标签
        api.MapGet("/tags", (TagStore store) => Results.Ok(new { tags = store.GetAllTags() }));

        // 某张图片的标签
        api.MapGet("/tags/image", (TagStore store, [FromQuery] string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path 不能为空" });
            return Results.Ok(new { tags = store.GetTagsForImage(path) });
        });

        // 给图片加标签（标签不存在自动创建）
        api.MapPost("/tags/image", (TagStore store, [FromBody] ImageTagRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path) || string.IsNullOrWhiteSpace(req.Tag))
                return Results.BadRequest(new { error = "path 和 tag 不能为空" });
            store.AddImageTag(req.Path, req.Tag);
            return Results.Ok(new { ok = true, tags = store.GetTagsForImage(req.Path) });
        });

        // 移除图片上的标签
        api.MapDelete("/tags/image", (TagStore store, [FromQuery] string path, [FromQuery] string tag) =>
        {
            if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(tag))
                return Results.BadRequest(new { error = "path 和 tag 不能为空" });
            store.RemoveImageTag(path, tag);
            return Results.Ok(new { ok = true });
        });

        // 创建标签
        api.MapPost("/tags", (TagStore store, [FromBody] CreateTagRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Name))
                return Results.BadRequest(new { error = "name 不能为空" });
            store.CreateTag(req.Name);
            return Results.Ok(new { ok = true });
        });

        // 删除标签（同时从所有图片移除）
        api.MapDelete("/tags", (TagStore store, [FromQuery] string name) =>
        {
            if (string.IsNullOrWhiteSpace(name))
                return Results.BadRequest(new { error = "name 不能为空" });
            store.DeleteTag(name);
            return Results.Ok(new { ok = true });
        });

        // 按标签交集筛选图片（tags = "a,b,c" → 同时含全部标签的图片）
        api.MapGet("/tags/filter", (TagStore store, [FromQuery] string? tags) =>
        {
            var tagList = (tags ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var photos = store.FilterPaths(tagList)
                .Select(ImageService.BuildPhoto)
                .Where(p => p is not null)
                .Cast<PhotoInfo>()
                .OrderBy(p => p.Name, NaturalComparer.Instance)
                .ToList();
            return Results.Ok(new { photos });
        });

        // ---------- 角色识别（AI，对接用户配置的模型 API，如 TouhouRoleApi_c） ----------

        // 读取角色识别 API 配置
        api.MapGet("/ai/config", (AiConfigStore cfg) => Results.Ok(new { apiUrl = cfg.GetApiUrl() }));

        // 保存角色识别 API 配置（空值清除）
        api.MapPost("/ai/config", (AiConfigStore cfg, [FromBody] AiConfigRequest req) =>
        {
            cfg.SetApiUrl(req.ApiUrl);
            return Results.Ok(new { ok = true, apiUrl = cfg.GetApiUrl() });
        });

        // 调用配置的角色识别 API 识别图片中的角色：上传本地图片字节 → 透传识别结果 JSON。
        // 兼容 TouhouRoleApi 的 POST /api/predict/file（原始字节流方式），返回 { top: [{class,confidence}], ... }。
        api.MapPost("/ai/recognize", async (ImageService svc, AiConfigStore cfg, IHttpClientFactory httpFactory,
            [FromQuery] string path) =>
        {
            var apiUrl = cfg.GetApiUrl();
            if (string.IsNullOrWhiteSpace(apiUrl))
                return Results.BadRequest(new { error = "尚未配置角色识别 API，请在识别面板中填写 API 地址" });
            var photo = svc.OpenPhoto(path);
            if (photo is null)
                return Results.BadRequest(new { error = "图片不存在或格式不支持" });

            byte[] bytes;
            using (var stream = photo.Value.Stream)
            {
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                bytes = ms.ToArray();
            }

            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(90);
            using var content = new ByteArrayContent(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            try
            {
                var resp = await client.PostAsync(apiUrl + "/api/predict/file", content);
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode)
                    return Results.BadRequest(new { error = $"识别服务返回 {(int)resp.StatusCode}: {body}" });
                return Results.Text(body, "application/json");
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "无法连接角色识别 API: " + ex.Message });
            }
        });

        return app;
    }
}
