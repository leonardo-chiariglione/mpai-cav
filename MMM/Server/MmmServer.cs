using System.Collections.Concurrent;
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
    // onNext: what the viewer's "next step" (Space, a click) does in a manual demonstration.
    public static WebApplication Build(MInstance m, string url, string? viewer = null, Action? onNext = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
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
        app.MapGet("/api/v2.2/processes/{processID}/rights", (HttpContext ctx, string processID) =>
            Caller(ctx) is null ? Results.Unauthorized() : Results.Json(m.RightsOf(processID)));
        app.MapGet("/api/v2.2/processes/{processID}/capabilities", (HttpContext ctx, string processID) =>
            Caller(ctx) is null ? Results.Unauthorized() : Results.Json(new { processID, rights = m.RightsOf(processID).Count }));
        app.MapGet("/api/v2.2/m-instance/capabilities", () => Results.Json(new JsonObject
        {
            ["MInstanceID"] = m.ID, ["MEnvironmentID"] = m.EnvironmentID,
            ["Actions"] = new JsonArray(m.Performed.Select(a => (JsonNode)a).ToArray())
        }));
        if (viewer is not null)
        {
            var root = Path.GetFullPath(viewer);
            app.MapGet("/viewer/state", () => Results.Json(m.Snapshot()));
            app.MapPost("/viewer/next", () => { onNext?.Invoke(); return Results.Ok(); });
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
