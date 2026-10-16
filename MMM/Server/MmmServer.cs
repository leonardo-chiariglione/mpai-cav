using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mpai.Mmm.Server;

// THE MMM-API OF AN M-INSTANCE (MMM-API chapter). One POST per Process Action under
// /api/v2.2/<service>/<action>; the Items and Capabilities read-only under GET. A
// Request carries a bearer token, which the M-Instance resolves to the Process it was
// given to; and an Idempotency-Key: a Request retried with the same key is not
// performed twice, its first Response is returned. A Registration's Response carries
// the tokens of the User Processes it created, in the header MMM-Tokens
// (Process=token, comma-separated): how a token reaches its Process is not specified
// by MMM-TEC.
public static class MmmServer
{
    // viewer: the folder of the viewer (MMM/Viewer), served at /viewer/ with the
    // M-Instance as it can be seen at /viewer/state; null, no viewer.
    // onNext, onBack: what the viewer's "next step" (Space, up arrow, a click) and "previous
    // step" (down arrow) do in a manual demonstration; state: what the viewer is shown instead
    // of the M-Instance as it is now (a step already performed, shown again).
    public static WebApplication Build(MInstance m, string url, string? viewer = null, Action? onNext = null,
                                       Action? onBack = null, Func<System.Text.Json.Nodes.JsonObject>? state = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        app.UseWebSockets();
        var streams = new SpeechStreams();
        var replies = new ConcurrentDictionary<string, (int Http, string Body, string? Tokens)>();

        string? Caller(HttpContext ctx) =>
            ctx.Request.Headers.Authorization.ToString() is { } a && a.StartsWith("Bearer ", StringComparison.Ordinal)
                ? m.ProcessOf(a["Bearer ".Length..].Trim()) : null;

        foreach (var (path, action) in Api.Paths)
        {
            app.MapPost($"/api/v2.2/{path}", async (HttpContext ctx) =>
            {
                var key = ctx.Request.Headers["Idempotency-Key"].ToString();
                if (key.Length == 0) return Results.BadRequest("Idempotency-Key is required");
                var caller = Caller(ctx);
                var memo = $"{caller ?? "-"}|{key}";
                if (replies.TryGetValue(memo, out var seen)) return Reply(ctx, seen);

                ProcessActionRequest? rq;
                try { rq = await JsonSerializer.DeserializeAsync<ProcessActionRequest>(ctx.Request.Body, Wire.Json); }
                catch (JsonException e) { return Results.BadRequest(e.Message); }
                if (rq is null) return Results.BadRequest("no Request");
                if (rq.Action.Length == 0) rq.Action = action;
                if (rq.Action != action) return Results.BadRequest($"{path} performs {action}, the Request asks {rq.Action}");

                var outcome = m.Perform(caller, rq);
                var reply = (outcome.Http, JsonSerializer.Serialize(outcome.Response, Wire.Json),
                             outcome.Tokens.Count == 0 ? null : string.Join(",", outcome.Tokens.Select(t => $"{t.Key}={t.Value}")));
                replies.TryAdd(memo, reply);
                return Reply(ctx, reply);
            });
        }

        app.MapGet("/api/v2.2/items", (HttpContext ctx, string? dataType) => Caller(ctx) is { } p
            ? Results.Json(m.List(dataType).Where(i => m.MayRead(p, i.ID)).Select(i => new { itemID = i.ID, dataType = i.DataType }))
            : Results.Unauthorized());
        app.MapGet("/api/v2.2/items/{itemID}", (HttpContext ctx, string itemID) =>
            Caller(ctx) is not { } p ? Results.Unauthorized()
            : m.Get(itemID) is not { } it ? Results.NotFound()
            : m.MayRead(p, itemID) ? Results.Json(m.AsAnyItem(it)) : Results.StatusCode(403));
        app.MapGet("/api/v2.2/items/{itemID}/rights", (HttpContext ctx, string itemID) =>
            Caller(ctx) is null ? Results.Unauthorized() : Results.Json(m.HoldersOf(itemID).Select(h => new { processID = h, rights = m.RightsOf(h) })));
        // Not in MMM-TEC V2.2: how a Process learns which Messages reached it (it reads each by its identifier with GET items/{itemID}).
        app.MapGet("/api/v2.2/processes/{processID}/inbox", (HttpContext ctx, string processID) =>
            Caller(ctx) != processID ? Results.StatusCode(403) : Results.Json(m.InboxWithSenders(processID).Select(i => new { itemID = i.ID, from = i.From })));
        app.MapGet("/api/v2.2/processes/{processID}/rights", (HttpContext ctx, string processID) =>
            Caller(ctx) is null ? Results.Unauthorized() : Results.Json(m.RightsOf(processID)));
        app.MapGet("/api/v2.2/processes/{processID}/capabilities", (HttpContext ctx, string processID) =>
            Caller(ctx) is null ? Results.Unauthorized() : Results.Json(new { processID, rights = m.RightsOf(processID).Count }));
        app.MapGet("/api/v2.2/m-instance/capabilities", () => Results.Json(new JsonObject
        {
            ["MInstanceID"] = m.ID, ["MEnvironmentID"] = m.EnvironmentID,
            ["Actions"] = new JsonArray(m.Performed.Select(a => (JsonNode)a).ToArray())
        }));
        // The Speech Objects that are streams. The client feeds the M-Instance with a WebSocket (a browser cannot give a WebSocket a header, so its token is in the address);
        // whoever may hear the Speech Object listens on a WebSocket, the stream format of its Speech Qualifier: 16 kHz mono Int16 PCM, from the start, as it goes on.
        app.Map("/ws/v2.2/speech/{itemID}", async (HttpContext ctx, string itemID) =>
        {
            var p = ctx.Request.Query["token"].ToString() is { Length: > 0 } t ? m.ProcessOf(t) : null;
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (p is null) { ctx.Response.StatusCode = 401; return; }
            if (m.StreamToFeed(p, itemID) is not { } end) { ctx.Response.StatusCode = 403; return; }   // only its User feeds a Speech Object, and only while it is a stream
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await streams.FeedAsync(socket, itemID, end, m, ctx.RequestAborted);
        });
        // Whoever hears a Speech Object chooses to: its User's client opens the Speech Object's address, a WebSocket (the stream format its Speech Qualifier declares), and receives
        // the PCM from the start of the Speech Object and as it goes on, then the end. Receiving is the receiver's decision (from this User, not from that one); the M-Instance
        // only refuses whoever may not hear: outside the room, or taken out of the meeting.
        app.Map("/ws/v2.2/speech/{itemID}/listen", async (HttpContext ctx, string itemID) =>
        {
            var p = ctx.Request.Query["token"].ToString() is { Length: > 0 } t ? m.ProcessOf(t) : null;
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (p is null) { ctx.Response.StatusCode = 401; return; }
            if (m.Get(itemID) is null) { ctx.Response.StatusCode = 404; return; }
            if (!m.MayRead(p, itemID)) { ctx.Response.StatusCode = 403; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            try
            {
                if (m.SpeechData(itemID) is { } done)                           // already over: what it was, then the end
                    for (var i = 0; i < done.Length; i += 8192) await socket.SendAsync(done.AsMemory(i, Math.Min(8192, done.Length - i)), WebSocketMessageType.Binary, true, ctx.RequestAborted);
                else
                    await foreach (var chunk in streams.ReadAsync(itemID, ctx.RequestAborted)) await socket.SendAsync(chunk, WebSocketMessageType.Binary, true, ctx.RequestAborted);
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None);
            }
            catch (OperationCanceledException) { }
            catch (WebSocketException) { }
        });        if (viewer is not null)
        {
            var root = Path.GetFullPath(viewer);
            app.MapGet("/viewer/state", () => Results.Json(state?.Invoke() ?? m.Snapshot()));
            app.MapPost("/viewer/next", () => { onNext?.Invoke(); return Results.Ok(); });
            app.MapPost("/viewer/back", () => { onBack?.Invoke(); return Results.Ok(); });
            app.MapGet("/viewer/{**path}", (HttpContext ctx, string? path) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";   // a presenter must never see a stale page
                var file = Path.GetFullPath(Path.Combine(root, string.IsNullOrEmpty(path) ? "index.html" : path));
                if (!file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(file)) return Results.NotFound();
                var type = Path.GetExtension(file).ToLowerInvariant() switch
                {
                    ".html" => "text/html; charset=utf-8", ".js" => "text/javascript", ".glb" => "model/gltf-binary",
                    ".json" => "application/json", ".md" => "text/plain; charset=utf-8", _ => "application/octet-stream"
                };
                return Results.File(file, type);
            });
        }
        return app;
    }

    private static IResult Reply(HttpContext ctx, (int Http, string Body, string? Tokens) r)
    {
        if (r.Tokens is not null) ctx.Response.Headers["MMM-Tokens"] = r.Tokens;
        return Results.Content(r.Body, "application/json", statusCode: r.Http);
    }
}
