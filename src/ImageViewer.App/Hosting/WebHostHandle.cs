using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace ImageViewer.App.Hosting;

/// <summary>内嵌 Kestrel 宿主的句柄：读取随机端口 + 优雅停止（防 WPF 主线程死锁）。</summary>
public sealed class WebHostHandle
{
    public WebApplication App { get; }
    public int Port { get; }

    public WebHostHandle(WebApplication app)
    {
        App = app;
        Port = app.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!
            .Addresses
            .Select(a => new Uri(a).Port)
            .First();
    }

    public void Stop()
    {
        try
        {
            // StopAsync/DisposeAsync 的 continuation 可能回 WPF 主线程续跑，
            // 在主线程上 .GetResult() 会永久死锁（关窗卡死）→ Task.Run 移出主线程等待。
            var stop = Task.Run(async () => await App.StopAsync());
            if (!stop.Wait(TimeSpan.FromSeconds(5))) return;
            var dispose = Task.Run(async () => await App.DisposeAsync());
            dispose.Wait(TimeSpan.FromSeconds(2));
        }
        catch { }
    }
}
