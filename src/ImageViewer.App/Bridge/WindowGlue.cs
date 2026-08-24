namespace ImageViewer.App.Bridge;

/// <summary>注入到页面文档开头的 glue 脚本：把宿主对象包装成前端期望的 window.chromeHost 形状。</summary>
public static class WindowGlue
{
    public const string Script = """
        (() => {
          const h = window.chrome?.webview?.hostObjects?.chromeHost;
          if (!h) return;
          window.chromeHost = {
            start_drag: () => h.start_drag(),
            minimize: () => h.minimize(),
            toggle_maximize: () => Promise.resolve(!!h.toggle_maximize()),
            is_maximized: () => Promise.resolve(!!h.is_maximized()),
            close: () => h.close(),
            pick_folder: () => h.pick_folder(),
          };
        })();
        """;
}
