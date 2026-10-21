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
                                       Action? onBack = null, Func<System.Text.Json.Nodes.JsonObject>? state = null,
                                       string? code = null, string? avatars = null, string? internalKey = null, Identity? identity = null, ISet<string>? officers = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);
        var app = builder.Build();
        // WHO IS ASKING (identity): an invitation link (?invite=) is turned into its guest's name and kept in a cookie; the chair is whoever browses on this machine; a visitor, if the chair
        // allows them, is named at random. Nobody else is answered. The services of the M-Instance carry the internal key. What a person may do is the M-Instance's: the Rights the chair has given.
        if (identity is not null)
            app.Use(async (ctx, next) =>
            {
                if (internalKey is not null && ctx.Request.Headers["X-Mmm-Internal"] == internalKey) { await next(); return; }
                var secure = ctx.Request.IsHttps || ctx.Request.Headers["X-Forwarded-Proto"] == "https";
                if (identity.NameOfInvitation(ctx.Request.Query["invite"]) is not null)
                {
                    ctx.Response.Cookies.Append(Identity.Cookie, ctx.Request.Query["invite"].ToString(), new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = secure, MaxAge = TimeSpan.FromHours(12) });
                    var back = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(ctx.Request.Path, ctx.Request.Query.Where(q => q.Key != "invite").ToDictionary(q => q.Key, q => (string?)q.Value.ToString()));
                    ctx.Response.Redirect(back);
                    return;
                }
                var who = identity.Of(ctx);
                if (who is null && identity.Visitors && !ctx.Request.Headers.ContainsKey("Tailscale-User-Login"))          // a visitor, the first time: named at random, kept in a cookie
                {
                    var v = Identity.NewToken();
                    ctx.Response.Cookies.Append(Identity.VisitorCookie, v, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = secure, MaxAge = TimeSpan.FromHours(12) });
                    who = new Identity.Who("Visitor-" + v[..6], "visitor", "");
                }
                if (who is null)
                {
                    ctx.Response.StatusCode = 401; ctx.Response.ContentType = "text/html; charset=utf-8";
                    await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>MMM</title><body style='font:18px system-ui;background:#1d2128;color:#e6e8eb;display:flex;height:90vh;align-items:center;justify-content:center;text-align:center'><div><h2>By invitation</h2><p>Open the personal link the chair sent you.</p></div>");
                    return;
                }
                ctx.Items["who"] = who;
                await next();
            });
        // A MEETING CODE (code): nobody gets anything from this server - the viewer, the API, the speech sockets - without it. It is given once, in the address (?code=), and the
        // browser then keeps it in a cookie of this site, which every later request, WebSockets included, carries. Without it a page asks for it and the rest is refused (401).
        if (code is not null)
            app.Use(async (ctx, next) =>
            {
                if (ctx.Request.Cookies["mmm"] == code || (internalKey is not null && ctx.Request.Headers["X-Mmm-Internal"] == internalKey)) { await next(); return; }   // the services of the M-Instance carry the internal key
                if (ctx.Request.Query["code"] == code)
                {
                    ctx.Response.Cookies.Append("mmm", code, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps, MaxAge = TimeSpan.FromHours(12) });
                    var back = Microsoft.AspNetCore.WebUtilities.QueryHelpers.AddQueryString(ctx.Request.Path, ctx.Request.Query.Where(q => q.Key != "code").ToDictionary(q => q.Key, q => (string?)q.Value.ToString()));
                    ctx.Response.Redirect(back);
                    return;
                }
                if (ctx.Request.Path.StartsWithSegments("/viewer") && ctx.Request.Method == "GET" && !ctx.Request.Path.Value!.Contains('.') && !ctx.Request.Path.Value.EndsWith("/state", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = 401; ctx.Response.ContentType = "text/html; charset=utf-8";
                    var name = Uri.EscapeDataString(ctx.Request.Query["user"].ToString());
                    await ctx.Response.WriteAsync("<!doctype html><meta charset=utf-8><meta name=viewport content='width=device-width,initial-scale=1'><title>MMM</title><body style='font:18px system-ui;background:#1d2128;color:#e6e8eb;display:grid;place-items:center;height:100vh;margin:0'>"
                        + "<form method=get style='text-align:center'><h2>The meeting</h2>"
                        + (ctx.Request.Query.ContainsKey("user") ? "" : "<p><input name=user required placeholder='your name' autocomplete=off style='font-size:22px;padding:6px;text-align:center;width:9em'></p>")
                        + (ctx.Request.Query.ContainsKey("world") ? "" : "<input type=hidden name=world value=1>") + (ctx.Request.Query.ContainsKey("mic") ? "" : "<input type=hidden name=mic value=1>")
                        + "<p><input name=code required placeholder='meeting code' autocomplete=off style='font-size:22px;padding:6px;text-align:center;width:9em'></p> "
                        + string.Concat(ctx.Request.Query.Where(q => q.Key != "code").Select(q => $"<input type=hidden name='{System.Net.WebUtility.HtmlEncode(q.Key)}' value='{System.Net.WebUtility.HtmlEncode(q.Value.ToString())}'>"))
                        + "<button style='font-size:22px;padding:6px 16px'>Enter</button></form>");
                    return;
                }
                ctx.Response.StatusCode = 401;
            });
        app.UseWebSockets();
        // Who may be offered, and may take, the MPAI Officer Account Level: with no identity (a private visit) anybody; with identity, the chair and the people the chair designates.
        bool MayBeOfficer(HttpContext ctx) => identity is null || (ctx.Items["who"] is Identity.Who w && (w.Kind == "host" || (officers?.Contains(w.Name) ?? false)));
        var streams = new SpeechStreams();
        var screens = new ScreenStreams();
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
                // A person registers as who Tailscale says it is, and only as that: its human, its User.
                if (identity is not null && action == "Register" && ctx.Items["who"] is Identity.Who me)
                {
                    var users = (rq.Complements?.With ?? []).SelectMany(w => w?["PersonalProfile"]?["Processes"]?.AsArray() ?? []).SelectMany(p => p?["ProcessIDOrProcess"]?.AsArray() ?? [])
                                .Select(p => p?["ProcessID"]?.GetValue<string>());
                    if (rq.SourceProcessID != me.Name + "-human" || users.Any(u => u != me.Name))
                        return Results.Json(new { paStatus = new { code = "InsRights", detail = $"{me.Login}{(me.Login.Length > 0 ? " is" : "")} {me.Name}: it registers only as that" } }, statusCode: 403);
                }

                if (action == "Register" && (rq.Complements?.With ?? []).Any(w => (string?)w?["PersonalProfile"]?["AccountLevel"] == "Officer") && !MayBeOfficer(ctx))
                    return Results.Json(new { paStatus = new { code = "InsRights", detail = "The MPAI Officer Account Level is not offered to you" } }, statusCode: 403);
                var outcome = m.Perform(caller, rq);
                var reply = (outcome.Http, JsonSerializer.Serialize(outcome.Response, Wire.Json),
                             outcome.Tokens.Count == 0 ? null : string.Join(",", outcome.Tokens.Select(t => $"{Uri.EscapeDataString(t.Key)}={t.Value}")));   // a header is ASCII: a name with an accent (Gérard) goes percent-encoded
                replies.TryAdd(memo, reply);
                return Reply(ctx, reply);
            });
        }

        // What a human that has not registered yet is shown: the Personae the M-Instance offers (Register takes the choice). No token: there is no User yet.
        // Registration options: the Account Levels, then (for the one chosen) the Personae the M-Instance makes available. No token: there is no User yet.
        app.MapGet("/api/v2.2/register/options", (HttpContext ctx) => Results.Json(m.AccountLevels.Where(l => l.Id != "Officer" || MayBeOfficer(ctx)).Select(l => new { id = l.Id, name = l.Name, description = l.Description, price = 0 })));
        app.MapGet("/api/v2.2/register/personae", () => Results.Json(m.CatalogueEntries));
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
        // A shared screen: the sharer's feed (its own Visual Object, a stream: only the User with the Right to share has placed one), and whoever may see it, in the room, watches.
        app.Map("/ws/v2.2/screen/{itemID}", async (HttpContext ctx, string itemID) =>
        {
            var p = ctx.Request.Query["token"].ToString() is { Length: > 0 } t ? m.ProcessOf(t) : null;
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (p is null) { ctx.Response.StatusCode = 401; return; }
            if (m.ScreenToFeed(p, itemID) is null) { ctx.Response.StatusCode = 403; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await screens.FeedAsync(socket, itemID, m, ctx.RequestAborted);
        });
        app.Map("/ws/v2.2/screen/{itemID}/watch", async (HttpContext ctx, string itemID) =>
        {
            var p = ctx.Request.Query["token"].ToString() is { Length: > 0 } t ? m.ProcessOf(t) : null;
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            if (p is null) { ctx.Response.StatusCode = 401; return; }
            if (!m.ScreenLive(itemID)) { ctx.Response.StatusCode = 404; return; }
            if (!m.MayRead(p, itemID)) { ctx.Response.StatusCode = 403; return; }
            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            await screens.WatchAsync(socket, itemID, () => m.MayRead(p, itemID), m, ctx.RequestAborted);
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
            // Who the person is, through Tailscale; and if it registered before (its browser was closed), its Process is given a new token and told where its Persona stands.
            app.MapGet("/viewer/whoami", (HttpContext ctx) => ctx.Items["who"] is not Identity.Who w ? Results.NotFound()
                : Results.Json(new { name = w.Name, kind = w.Kind, resume = m.Resume(w.Name) is { } r ? new { token = r.Token, persona = r.Info } : null }));
            app.MapPost("/viewer/next", () => { onNext?.Invoke(); return Results.Ok(); });
            app.MapPost("/viewer/back", () => { onBack?.Invoke(); return Results.Ok(); });
            app.MapGet("/viewer/{**path}", (HttpContext ctx, string? path) =>
            {
                ctx.Response.Headers.CacheControl = "no-store";   // a presenter must never see a stale page
                var file = Path.GetFullPath(Path.Combine(root, string.IsNullOrEmpty(path) ? "index.html" : path));
                if (!File.Exists(file) && avatars is not null && path is not null && path.StartsWith("personae/", StringComparison.Ordinal))
                    file = Path.GetFullPath(Path.Combine(avatars, Path.GetFileName(path)));      // the avatars of the ABV demo (Rocketbox), without a second copy of each
                if (!(file.StartsWith(root, StringComparison.OrdinalIgnoreCase) || (avatars is not null && file.StartsWith(Path.GetFullPath(avatars), StringComparison.OrdinalIgnoreCase))) || !File.Exists(file)) return Results.NotFound();
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
