using ImageViewer.Gallery;

// 浏览器模式入口：dotnet run --urls http://localhost:5211
// 桌面模式（WPF + WebView2，不依赖浏览器）见 ../ImageViewer.App，两者共用 AppHost。
var app = AppHost.Build(args: args);
app.Run();
