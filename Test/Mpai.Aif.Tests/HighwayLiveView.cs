using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mpai.Aif.Tests;

// THE LIVE VIEW OF A HIGHWAY RUN (M3253): a small web server on this machine's loopback that gives
// Apps/CAV/CAVViewer/highway.html, the state of the run as the last step made it, and the picture of the front
// camera that step gave the ESS. The run publishes after each step; the page reads and draws.
public sealed class HighwayLiveView : IDisposable
{
    private readonly WebApplication app;
    private volatile string state = "{}";
    private volatile byte[] frame = [];
    private volatile byte[] rear = [];
    private volatile byte[] tele = [];

    public HighwayLiveView(string url)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        app = builder.Build();
        var page = Path.Combine(Repository.Root, "Apps", "CAV", "CAVViewer", "highway.html");
        app.MapGet("/", (HttpContext ctx) => { ctx.Response.Headers.CacheControl = "no-store"; return Results.File(page, "text/html; charset=utf-8"); });
        app.MapGet("/state", (HttpContext ctx) => { ctx.Response.Headers.CacheControl = "no-store"; return Results.Content(state, "application/json"); });
        app.MapGet("/frame.png", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return frame.Length == 0 ? Results.NotFound() : Results.Bytes(frame, "image/png");
        });
        app.MapGet("/rear.png", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return rear.Length == 0 ? Results.NotFound() : Results.Bytes(rear, "image/png");
        });
        app.MapGet("/tele.png", (HttpContext ctx) =>
        {
            ctx.Response.Headers.CacheControl = "no-store";
            return tele.Length == 0 ? Results.NotFound() : Results.Bytes(tele, "image/png");
        });
        app.StartAsync().GetAwaiter().GetResult();
    }

    public void Publish(string json, byte[]? png, byte[]? rearPng = null, byte[]? telePng = null)
    {
        state = json;
        if (png is not null) frame = png;
        if (rearPng is not null) rear = rearPng;
        if (telePng is not null) tele = telePng;
    }

    public void Dispose() => app.StopAsync().GetAwaiter().GetResult();
}
