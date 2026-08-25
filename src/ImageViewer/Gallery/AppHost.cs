using System.Net.Http.Headers;
using System.Reflection;
using System.Runtime.InteropServices;
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
    /// <summary>应用版本号（来自入口程序集 AssemblyInformationalVersion，如 1.0.1；去掉可能的 +hash 后缀）。</summary>
    public static string AppVersion
    {
        get
        {
            var asm = Assembly.GetEntryAssembly();
            var attr = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>();
            var v = attr?.InformationalVersion ?? asm?.GetName().Version?.ToString() ?? "0.0.0";
            return v.Split('+')[0];
        }
    }

    /// <summary>扩展名 → 友好显示名（设置页「文件关联」用）。</summary>
    private static string FormatDisplayName(string ext) => ext.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" or ".jfif" or ".jpe" => "JPEG 图片",
        ".png" => "PNG 图片",
        ".bmp" => "BMP 图片",
        ".gif" => "GIF 动图",
        ".webp" => "WebP 图片",
        ".tif" or ".tiff" => "TIFF 图片",
        ".ico" => "ICO 图标",
        _ => ext,
    };

    /// <summary>待打开图片路径（双击图片文件用本程序打开时，由 App 启动参数写入；前端启动时一次性读取后清除）。</summary>
    public static string? PendingOpenPath { get; set; }

    /// <summary>构建 WebApplication（未启动）。args 透传给命令行配置（如 --urls）；configureWebHost 在 Build 前调用，可注入 Kestrel 监听配置；
    /// appExePath = 桌面版程序 exe 完整路径（用于文件关联）；浏览器模式传 null。</summary>
    public static WebApplication Build(
        string[]? args = null,
        WebApplicationOptions? options = null,
        Action<ConfigureWebHostBuilder>? configureWebHost = null,
        string? appExePath = null)
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
        // 依赖相册封面存储（自定义封面）
        builder.Services.AddSingleton<CoverStore>();
        builder.Services.AddSingleton(sp => new ImageService(builder.Configuration["Images:Root"], sp.GetRequiredService<CoverStore>()));
        // 已链接相册文件夹的持久化列表（存 LocalAppData，只记路径不复制文件）
        builder.Services.AddSingleton<AlbumStore>();
        // 图片标签存储（标签定义 + 图片↔标签映射）
        builder.Services.AddSingleton<TagStore>();
        // 角色识别 API 配置存储 + HTTP 客户端工厂（转发识别请求用）
        builder.Services.AddSingleton<AiConfigStore>();
        // 相册排序设置存储（按相册目录单独保存排序字段/升降序）
        builder.Services.AddSingleton<SortStore>();
        // 应用设置存储（极速查看器开关等；桌面宿主启动时直接读同一文件，前端通过 /api/fastviewer 读写）
        builder.Services.AddSingleton<SettingsStore>();
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
                System.Diagnostics.Trace.TraceError($"[api] {ex.Message}");
                await context.Response.WriteAsJsonAsync(new { error = "服务器内部错误" });
            }
        });

        // 静态前端（wwwroot/）：本地宿主不缓存静态文件，重启后始终加载最新前端（避免 WebView2 缓存旧 JS/CSS）
        app.UseDefaultFiles();
        app.UseStaticFiles(new StaticFileOptions
        {
            OnPrepareResponse = ctx =>
                ctx.Context.Response.Headers.CacheControl = "no-cache",
        });

        var api = app.MapGroup("/api");

        // 应用版本号（标题栏显示用）
        api.MapGet("/version", () => Results.Ok(new { version = AppVersion }));

        // 双击图片用本程序打开：返回待打开图片路径（一次性，前端启动时获取并打开查看器）
        api.MapGet("/pending-open", () =>
        {
            var p = AppHost.PendingOpenPath;
            AppHost.PendingOpenPath = null;
            return Results.Ok(new { path = p });
        });


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

        // 列出所有已链接相册（不存在的目录自动跳过），封面应用自定义设置
        api.MapGet("/albums", (ImageService svc, AlbumStore store) =>
        {
            var albums = store.GetAll()
                .Where(Directory.Exists)
                .Select(svc.DescribeAlbumWithCover)
                .Where(a => a is not null)
                .Cast<AlbumInfo>()
                .OrderBy(a => a.Name, NaturalComparer.Instance)
                .ToList();
            return Results.Ok(new { albums });
        });

        // 查询相册自定义封面（未配置返回 null）
        api.MapGet("/covers", (CoverStore store, [FromQuery] string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path 不能为空" });
            return Results.Ok(new { cover_path = store.Get(path) });
        });

        // 设置相册自定义封面（cover_path 为空 = 恢复默认第一张）
        api.MapPost("/covers", (CoverStore store, [FromBody] CoverRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.AlbumPath))
                return Results.BadRequest(new { error = "album_path 不能为空" });
            if (!string.IsNullOrWhiteSpace(req.CoverPath) && !File.Exists(req.CoverPath))
                return Results.BadRequest(new { error = "封面图片不存在" });
            store.Set(req.AlbumPath, req.CoverPath ?? "");
            return Results.Ok(new { ok = true, cover_path = store.Get(req.AlbumPath) });
        });

        // 添加相册文件夹链接：仅校验目录存在并写入持久化列表（不枚举图片，避免大目录阻塞）。
        // 相册信息（数量/封面）由前端随后 GET /api/albums 获取。
        api.MapPost("/albums", (ImageService svc, AlbumStore store, [FromBody] AddAlbumRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "路径不能为空" });
            string abs;
            try { abs = Path.GetFullPath(req.Path); }
            catch { return Results.BadRequest(new { error = "路径无效" }); }
            // 缩略图缓存文件夹是程序内部数据，不允许当作相册
            if (svc.IsCachePath(abs))
                return Results.BadRequest(new { error = "无法将缓存文件夹作为相册" });
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
            SettingsStore store, [FromQuery] string path) =>
        {
            var apiUrl = cfg.GetApiUrl();
            if (string.IsNullOrWhiteSpace(apiUrl))
                return Results.BadRequest(new { error = "尚未配置角色识别 API，请到 设置 → 🔍 识别功能 中填写 API 地址" });
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
                    return Results.BadRequest(new { error = "识别服务返回错误", message = $"HTTP {(int)resp.StatusCode}: {body}" });
                return Results.Text(body, "application/json");
            }
            catch (Exception ex)
            {
                // 默认不暴露底层异常文本；设置「显示详细错误信息」开启后附上，便于排查。
                System.Diagnostics.Trace.TraceError($"[recognize] {apiUrl}: {ex.Message}");
                return Results.BadRequest(new
                {
                    error = "无法连接识别服务",
                    message = store.GetShowDetailError() ? ex.Message : null,
                });
            }
        });

        // 检测角色识别 API 服务是否在线（一键识别开始前先探测，避免 API 未连接时进度条空转）
        api.MapGet("/ai/ping", async (AiConfigStore cfg, IHttpClientFactory httpFactory) =>
        {
            var apiUrl = cfg.GetApiUrl();
            if (string.IsNullOrWhiteSpace(apiUrl))
                return Results.Ok(new { ok = false, error = "尚未配置角色识别 API" });
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            try
            {
                // 发出请求并收到响应（含 404/405 等任意状态码）即视为服务在线；仅连接异常算失败
                using var resp = await client.GetAsync(apiUrl);
                return Results.Ok(new { ok = true, api_url = apiUrl, status = (int)resp.StatusCode });
            }
            catch (Exception ex)
            {
                return Results.Ok(new { ok = false, api_url = apiUrl, error = "无法连接识别服务" });
            }
        });

        // 任务栏进度上报（一键识别等批量任务；WPF 宿主注册回调后设置任务栏进度条）
        api.MapPost("/taskbar-progress", ([FromBody] TaskbarProgressRequest req) =>
        {
            TaskbarProgress.Update(req.Value, req.State);
            return Results.Ok(new { ok = true });
        });

        // ---------- 相册排序设置（按相册目录单独保存） ----------

        // 读取某个相册的排序设置（未设置返回默认：名称升序）
        api.MapGet("/sort", (SortStore store, [FromQuery] string? path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
                return Results.BadRequest(new { error = "path 不能为空" });
            var (by, order) = store.Get(path);
            return Results.Ok(new { path, by, order });
        });

        // 保存某个相册的排序设置
        api.MapPost("/sort", (SortStore store, [FromBody] SortRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(req.Path))
                return Results.BadRequest(new { error = "path 不能为空" });
            store.Set(req.Path, req.By, req.Order);
            var (by, order) = store.Get(req.Path);
            return Results.Ok(new { ok = true, path = req.Path, by, order });
        });

        // ---------- 极速查看器开关 ----------

        // 读取极速查看器开关（开启后双击关联图片直接打开原生极速窗口，不加载 WebView2 主界面）
        api.MapGet("/fastviewer", (SettingsStore store) => Results.Ok(new { fast_viewer = store.GetFastViewer() }));

        // 保存极速查看器开关
        api.MapPost("/fastviewer", (SettingsStore store, [FromBody] FastViewerRequest req) =>
        {
            store.SetFastViewer(req.FastViewer);
            return Results.Ok(new { ok = true, fast_viewer = store.GetFastViewer() });
        });

        // ---------- 应用偏好设置（关闭模式 / 识别功能开关） ----------

        // 读取应用偏好：关闭主界面模式 / 识别功能开关 / 查看器背景（主查看器 + Flash）/ 图片编辑器
        api.MapGet("/prefs", (SettingsStore store) => Results.Ok(new
        {
            close_to_tray = store.GetCloseToTray(),
            ai_enabled = store.GetAiEnabled(),
            viewer_bg = store.GetViewerBg(),
            flash_bg = store.GetFlashBg(),
            editor_path = store.GetEditorPath(),
            right_click_close_viewer = store.GetRightClickCloseViewer(),
            update_check_enabled = store.GetUpdateCheck(),
            lang = store.GetLang(),
            show_detail_error = store.GetShowDetailError(),
            theme = store.GetTheme(),
        }));

        // 保存应用偏好（只更新传入的字段）
        api.MapPost("/prefs", (SettingsStore store, [FromBody] PrefsRequest req) =>
        {
            if (req.CloseToTray.HasValue) store.SetCloseToTray(req.CloseToTray.Value);
            if (req.AiEnabled.HasValue) store.SetAiEnabled(req.AiEnabled.Value);
            if (req.ViewerBg.HasValue) store.SetViewerBg(req.ViewerBg.Value);
            if (req.FlashBg.HasValue) store.SetFlashBg(req.FlashBg.Value);
            if (req.EditorPath is not null) store.SetEditorPath(req.EditorPath);
            if (req.RightClickCloseViewer.HasValue) store.SetRightClickCloseViewer(req.RightClickCloseViewer.Value);
            if (req.UpdateCheckEnabled.HasValue) store.SetUpdateCheck(req.UpdateCheckEnabled.Value);
            if (req.Lang is not null) store.SetLang(req.Lang);
            if (req.ShowDetailError.HasValue) store.SetShowDetailError(req.ShowDetailError.Value);
            if (req.Theme is not null) store.SetTheme(req.Theme);
            return Results.Ok(new
            {
                close_to_tray = store.GetCloseToTray(),
                ai_enabled = store.GetAiEnabled(),
                viewer_bg = store.GetViewerBg(),
                flash_bg = store.GetFlashBg(),
                editor_path = store.GetEditorPath(),
                right_click_close_viewer = store.GetRightClickCloseViewer(),
                update_check_enabled = store.GetUpdateCheck(),
                lang = store.GetLang(),
                show_detail_error = store.GetShowDetailError(),
                theme = store.GetTheme(),
            });
        });

        // 用系统默认浏览器打开外部链接（关于板块「打开 GitHub 项目」等；浏览器版同样可用）
        api.MapPost("/open-external", ([FromQuery] string url) =>
        {
            if (string.IsNullOrWhiteSpace(url) ||
                !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                return Results.BadRequest(new { error = "无效链接" });
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                return Results.Ok(new { ok = true });
            }
            catch (Exception ex)
            {
                return Results.BadRequest(new { error = "无法打开浏览器" });
            }
        });

        // 检查 GitHub Releases 是否有新版本（对比当前版本号）。
        // 多次重试（网络抖动容忍）；多次均失败后探测通用站点区分离线/在线。
        api.MapGet("/check-update", async (IHttpClientFactory httpFactory) =>
        {
            var current = AppVersion;
            string? json = null;
            string? failDetail = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    var client = httpFactory.CreateClient();
                    client.Timeout = TimeSpan.FromSeconds(10);
                    client.DefaultRequestHeaders.UserAgent.ParseAdd("HopingImageViewer/" + current);
                    using var resp = await client.GetAsync(
                        "https://api.github.com/repos/HopingStar/Hoping-Image-Viewer/releases/latest");
                    if (resp.IsSuccessStatusCode)
                    {
                        json = await resp.Content.ReadAsStringAsync();
                        break;
                    }
                    failDetail = "GitHub 响应 " + (int)resp.StatusCode;
                }
                catch (Exception ex)
                {
                    failDetail = ex.Message;
                }
                if (attempt < 2) await Task.Delay(700);
            }

            if (json is null)
            {
                var offline = await IsOffline(httpFactory);
                return Results.Ok(new { ok = false, offline, current, error = failDetail ?? "检查更新失败" });
            }

            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var latest = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                latest = latest?.TrimStart('v', 'V');
                var hasUpdate = CompareVersions(latest, current) > 0;
                return Results.Ok(new { ok = true, offline = false, current, latest = latest ?? "", has_update = hasUpdate });
            }
            catch (Exception ex)
            {
                var offline = await IsOffline(httpFactory);
                return Results.Ok(new { ok = false, offline, current, error = "无法连接服务" });
            }
        });

        // 在图片编辑器（用户配置的 / 系统默认画图 / Paint.NET）中打开指定图片。
        // 注意：不能用 UseShellExecute 直接打开文件——图片格式的「打开」关联是本程序（双击用本程序看图），会误开 Flash/主界面。
        // 探测顺序：设置→图片编辑 配置的 exe → 系统 image「编辑」动词（默认画图）→ mspaint → Paint.NET。
        // 都没有时返回 need_config，由前端引导去 设置→图片编辑 配置——不弹系统「打开方式」（选择应用时会把该格式文件关联改掉）。
        api.MapPost("/edit", (SettingsStore store, [FromQuery] string path) =>
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                return Results.BadRequest(new { error = "图片不存在" });

            // 0) 用户配置的图片编辑器（设置 → 图片编辑）
            var configured = store.GetEditorPath();
            if (!string.IsNullOrWhiteSpace(configured))
            {
                try
                {
                    System.Diagnostics.Process.Start(configured, "\"" + path + "\"");
                    return Results.Ok(new { ok = true, editor = "configured" });
                }
                catch (Exception ex)
                {
                    return Results.BadRequest(new { error = "无法打开外部编辑器", message = configured });
                }
            }

            // 1) 系统 image 类型注册的「编辑」命令（标准 Windows 指向画图；用户自定义编辑应用也在此）
            try
            {
                using var editKey = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"SystemFileAssociations\image\shell\edit\command");
                var editCmd = editKey?.GetValue(null) as string;
                if (!string.IsNullOrWhiteSpace(editCmd))
                {
                    var cmd = editCmd.Replace("%1", "\"" + path + "\"");
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c " + cmd)
                    {
                        UseShellExecute = true,
                        CreateNoWindow = true,
                        WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
                    });
                    return Results.Ok(new { ok = true, editor = "system-edit" });
                }
            }
            catch { }

            // 2) Windows 画图（System32 自带）
            var mspaint = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "mspaint.exe");
            if (File.Exists(mspaint))
            {
                System.Diagnostics.Process.Start(mspaint, path);
                return Results.Ok(new { ok = true, editor = "mspaint" });
            }

            // 3) Paint.NET（标准安装目录，不依赖注册表——部分便携/绿色版不写注册表）
            var pdn = FindPaintDotNetExe();
            if (pdn is not null)
            {
                System.Diagnostics.Process.Start(pdn, "\"" + path + "\"");
                return Results.Ok(new { ok = true, editor = "paint.net" });
            }

            // 4) 未配置且未探测到编辑器：返回 need_config，前端引导去 设置→图片编辑 配置
            //    （不弹系统「打开方式」——用户选应用时会提示「始终使用此应用打开」并把该格式关联改掉）
            return Results.Ok(new { ok = false, need_config = true });
        });

        // 浏览：弹系统文件选择框选一个图片编辑器 exe，返回完整路径（取消返回 null）。后端原生对话框，浏览器版同样可用。
        api.MapGet("/edit/pick", ([FromQuery] string? title) => Results.Ok(new { path = PickExecutable(title) }));

        // ---------- 设置：应用版本 / 支持的格式 / 文件关联 ----------

        // 设置总览：版本、是否桌面版、exe 路径、支持的格式及各自关联状态
        // （appExePath 是闭包捕获的 Build 参数，不能写成 lambda 参数——会被当作请求 query 参数绑定为 null）
        api.MapGet("/settings", () =>
        {
            var desktop = !string.IsNullOrWhiteSpace(appExePath);
            var formats = ImageService.SupportedExtensions
                .Select(ext => new
                {
                    ext,
                    name = FormatDisplayName(ext),
                    associated = desktop && FileAssociation.IsAssociated(ext),
                })
                .ToList();
            return Results.Ok(new { version = AppVersion, desktop, exePath = desktop ? appExePath : null, formats });
        });

        // 应用文件关联：勾选的格式建立关联，未勾选的解除（保持与勾选状态一致）
        api.MapPost("/settings/fileassoc", ([FromBody] FileAssocRequest req) =>
        {
            if (string.IsNullOrWhiteSpace(appExePath))
                return Results.BadRequest(new { error = "文件关联仅在桌面版可用，请在桌面版中设置" });
            var exts = req.Extensions ?? new List<string>();
            FileAssociation.Apply(appExePath, exts);
            var formats = ImageService.SupportedExtensions
                .Select(ext => new { ext, associated = FileAssociation.IsAssociated(ext) })
                .ToList();
            return Results.Ok(new { ok = true, formats });
        });

        return app;
    }

    /// <summary>探测是否离线：访问国内通用站点（baidu）成功 = 在线（GitHub 访问失败是别的网络问题）。</summary>
    private static async Task<bool> IsOffline(IHttpClientFactory httpFactory)
    {
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(5);
            using var resp = await client.GetAsync("https://www.baidu.com");
            return !resp.IsSuccessStatusCode;
        }
        catch { return true; }
    }

    /// <summary>版本号比较：a &gt; b 返回正数。忽略空串/非数字段。</summary>
    private static int CompareVersions(string? a, string? b)
    {
        var pa = ParseVersion(a);
        var pb = ParseVersion(b);
        var n = Math.Max(pa.Count, pb.Count);
        for (int i = 0; i < n; i++)
        {
            var x = i < pa.Count ? pa[i] : 0;
            var y = i < pb.Count ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static List<int> ParseVersion(string? v)
    {
        var list = new List<int>();
        if (string.IsNullOrWhiteSpace(v)) return list;
        foreach (var part in v.Split('.'))
            if (int.TryParse(part, out var n)) list.Add(n);
        return list;
    }

    /// <summary>按标准安装目录探测 Paint.NET 可执行文件（不依赖注册表——便携/绿色版不写注册表）。找不到返回 null。</summary>
    private static string? FindPaintDotNetExe()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Paint.NET", "paintdotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Paint.NET", "paintdotnet.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "paint.net", "paintdotnet.exe"),
        };
        foreach (var c in candidates)
        {
            try { if (File.Exists(c)) return c; } catch { }
        }
        return null;
    }

    // ---------- 原生文件选择框（浏览图片编辑器 exe） ----------
    // 不用 WebView2 host object 桥接（新方法在部分环境下暴露异常），直接后端弹 Win32 文件框，浏览器版也能用。

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName
    {
        public uint lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrFilter;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrCustomFilter;
        public uint nMaxCustFilter;
        public uint nFilterIndex;
        public IntPtr lpstrFile;
        public uint nMaxFile;
        public IntPtr lpstrFileTitle;
        public uint nMaxFileTitle;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrInitialDir;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTitle;
        public uint Flags;
        public ushort nFileOffset;
        public ushort nFileExtension;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        [MarshalAs(UnmanagedType.LPWStr)] public string lpstrTemplateName;
        public IntPtr pvReserved;
        public uint dwReserved;
        public uint flagsEx;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetOpenFileNameW(ref OpenFileName ofn);

    /// <summary>弹系统文件选择框选一个 exe，返回完整路径（取消返回 null）。title 为前端本地化后的标题。</summary>
    private static string? PickExecutable(string? title = null)
    {
        // 文件名缓冲区（GetOpenFileName 会把选中路径写进来），先清零
        var buffer = Marshal.AllocHGlobal(1024 * sizeof(char));
        for (int i = 0; i < 1024; i++) Marshal.WriteInt16(buffer, i * 2, 0);
        var ofn = new OpenFileName
        {
            lStructSize = (uint)Marshal.SizeOf<OpenFileName>(),
            lpstrFilter = "可执行文件 (*.exe)\0*.exe\0所有文件 (*.*)\0*.*\0",
            lpstrFile = buffer,
            nMaxFile = 1024,
            lpstrTitle = string.IsNullOrWhiteSpace(title) ? "选择图片编辑器（exe）" : title,
            // OFN_FILEMUSTEXIST | OFN_PATHMUSTEXIST | OFN_HIDEREADONLY
            Flags = 0x00001000u | 0x00000800u | 0x00000004u,
        };
        try
        {
            bool ok = GetOpenFileNameW(ref ofn);
            return ok ? Marshal.PtrToStringUni(buffer) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
