// THE HOST OF THE BROWSER RCA.
//
//   /                   the client (Blazor WebAssembly) and its files
//   /avatar/...         the avatar page and its model, from AIF\UserAgent\Assets, adapted
//                       as they are served: the page was written for WebView2,
//                       and the file itself is left as it is
//   /mas/MPAI-MAS.orch  the client's own workflow, from AIF\UserAgent\Orchestration
//   /MPAI/AIFU/...      passed on to the MPAI-MAS Service
//
//   dotnet run --project AIF\UserAgent\Clients\Browser\Host -- --Service https://localhost:5005/ --Urls https://localhost:5010
//
// A Service on another machine listens beyond its loopback, and so requires a bearer
// token: this host adds it to what it passes on - the ServiceToken setting, or the
// MPAI_MAS_TOKEN environment variable the desktop client reads too.

// PUBLISHED, OR RUN FROM ITS BUILD (M3248 3.1). Published - the client's files in a
// wwwroot beside this program - it is a public server: its own folder its content
// root, whatever folder it is started from, and Production unless told otherwise.
// Run from its build, the client's files are served from the build output as
// static web assets, which ASP.NET does in Development: that is then the default.
var published = File.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html"));
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = published ? AppContext.BaseDirectory : null,
    EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? (published ? Environments.Production : Environments.Development)
});
builder.WebHost.UseStaticWebAssets();
builder.WebHost.UseUrls(builder.Configuration["Urls"] ?? "https://localhost:5010");

var service = new Uri(builder.Configuration["Service"] ?? "https://localhost:5005/");
var serviceToken = builder.Configuration["ServiceToken"] is { Length: > 0 } configured ? configured
                 : Environment.GetEnvironmentVariable("MPAI_MAS_TOKEN") is { Length: > 0 } fromEnvironment ? fromEnvironment
                 : null;
var root    = RepositoryRoot(AppContext.BaseDirectory);
var assets  = Path.Combine(root, "AIF", "UserAgent", "Assets");
var masOrch = Path.Combine(root, "AIF", "UserAgent", "Orchestration", "MPAI-MAS.orch");

var app = builder.Build();
// WHO USES THE SERVICE, when it is reached through a proxy whose own addresses are all a host sees:
// off unless Usage:Enabled in appsettings.json (UsageLog.cs). It sits before everything else.
var usage = UsageLog.Create(app.Configuration, root);
if (usage is not null)
{
    app.Use(usage.Invoke);
    app.MapGet("/MPAI/Usage", usage.Summary);
}
// WHAT A PUBLIC SITE SENDS (M3248 3.1): HTTPS only from now on; content types as
// declared; no referrer to other sites; framed by this origin only (the avatar's
// page is); the camera and the microphone for this origin alone.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.Use(async (ctx, next) =>
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "same-origin";
        h["X-Frame-Options"] = "SAMEORIGIN";
        h["Permissions-Policy"] = "camera=(self), microphone=(self)";
        await next();
    });
}
// _framework and the client's files are served by MapStaticAssets, the .NET 10 way,
// compression included; the older UseBlazorFrameworkFiles would compete with it.
app.UseStaticFiles();
if (File.Exists(Path.Combine(AppContext.BaseDirectory, $"{builder.Environment.ApplicationName}.staticwebassets.endpoints.json")))
    app.MapStaticAssets();

// THE AVATAR PAGE, AS A BROWSER NEEDS IT. Its model and lighting are fetched from
// here instead of WebView2's virtual host, and a few lines give it the
// chrome.webview messaging it listens to, carried by the browser's own
// postMessage from the page around it.
const string WebViewShim = """
<script>
  window.chrome = window.chrome || {};
  if (!window.chrome.webview) {
    window.chrome.webview = {
      addEventListener: (type, handler) => {
        if (type === 'message')
          window.addEventListener('message', e => {
            if (e.origin === window.location.origin) handler({ data: e.data });
          });
      },
      postMessage: m => window.parent.postMessage(m, window.location.origin)
    };
  }
</script>
""";

app.MapGet("/avatar/cav-webview.html", async () =>
{
    var html = await File.ReadAllTextAsync(Path.Combine(assets, "cav-webview.html"));
    html = html.Replace("https://cavapp.local/", "/avatar/");
    var head = html.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
    html = head >= 0 ? html.Insert(head + "<head>".Length, "\n" + WebViewShim) : WebViewShim + html;
    return Results.Content(html, "text/html; charset=utf-8");
});

var avatarFiles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
{
    ["cav-avatar.glb"] = "model/gltf-binary",
    ["studio.hdr"]     = "application/octet-stream"
};
// The page that plays the video of a Basic AudioVisual Object on the stage (the client shows it in a frame).
app.MapGet("/avatar/stage-video.html", () => Results.File(Path.Combine(assets, "stage-video.html"), "text/html; charset=utf-8"));

app.MapGet("/avatar/{file}", (string file) =>
    avatarFiles.TryGetValue(file, out var type) && File.Exists(Path.Combine(assets, file))
        ? Results.File(Path.Combine(assets, file), type)
        : Results.NotFound());

// NEVER FROM THE BROWSER'S CACHE: a workflow changed on disk is the one run
// next, not a copy the browser kept (it did, and ran the old one).
app.MapGet("/mas/MPAI-MAS.orch", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return File.Exists(masOrch) ? Results.File(masOrch, "text/plain; charset=utf-8") : Results.NotFound();
});

// THE SERVICE, THROUGH THIS ORIGIN. A Service on this machine's loopback is
// accepted as it is; one reached over a network presents a certificate this
// machine trusts (M3248 3.1).
var forward = new HttpClient(new HttpClientHandler
{
    ServerCertificateCustomValidationCallback = Mpai.Rca.ServiceCertificates.Validator(service)
})
{ BaseAddress = service, Timeout = TimeSpan.FromMinutes(3) };

app.Map("/MPAI/AIFU/{**rest}", async (HttpContext http) =>
{
    var target = new Uri(service, http.Request.Path.Value!.TrimStart('/') + http.Request.QueryString);
    using var request = new HttpRequestMessage(new HttpMethod(http.Request.Method), target);

    if (http.Request.ContentLength is > 0 || http.Request.Headers.ContainsKey("Transfer-Encoding"))
    {
        var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body);
        body.Position = 0;
        request.Content = new StreamContent(body);
        if (http.Request.ContentType is { } contentType)
            request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType);
    }
    else if (http.Request.Method is "POST" or "PUT")
    {
        request.Content = new ByteArrayContent(Array.Empty<byte>());
    }
    if (http.Request.Headers.TryGetValue("MPAI-Client", out var clientId))
        request.Headers.TryAddWithoutValidation("MPAI-Client", clientId.ToString());
    // The Service's token, where this host has one; otherwise what the browser sent.
    if (serviceToken is not null)
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + serviceToken);
    else if (http.Request.Headers.Authorization.Count > 0)
        request.Headers.TryAddWithoutValidation("Authorization", http.Request.Headers.Authorization.ToString());

    using var response = await forward.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
    http.Response.StatusCode = (int)response.StatusCode;
    if (response.Content.Headers.ContentType is { } type)
        http.Response.ContentType = type.ToString();
    await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
});

// THALIA'S DESKTOP (what the page shows around the Apps), through this origin.
//
//   /thalia/wallpaper       the picture behind the icons: the Wallpaper setting, else
//                           %LOCALAPPDATA%\MPAI\Thalia\wallpaper.(jpg|png); none if there is none
//   /thalia/status          whether the Service and the MPAI Store answer
//   /thalia/store/{...}     a GET passed on to the MPAI Store (the Store setting, default
//                           http://127.0.0.1:5020/): what the Store has approved, read from the page
var storeUri = new Uri((builder.Configuration["Store"] ?? "http://127.0.0.1:5020/").TrimEnd('/') + "/");
var storeHttp = new HttpClient { BaseAddress = storeUri, Timeout = TimeSpan.FromSeconds(10) };

string? WallpaperFile()
{
    if (builder.Configuration["Wallpaper"] is { Length: > 0 } configured && File.Exists(configured)) return configured;
    var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MPAI", "Thalia");
    return new[] { "wallpaper.jpg", "wallpaper.png" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);
}

app.MapGet("/thalia/wallpaper", (HttpContext ctx) =>
{
    ctx.Response.Headers.CacheControl = "no-cache";
    return WallpaperFile() is { } file
        ? Results.File(file, file.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg")
        : Results.NotFound();
});

app.MapGet("/thalia/status", async () =>
{
    async Task<bool> Answers(HttpClient client, string path)
    {
        try { using var r = await client.GetAsync(path); return r.IsSuccessStatusCode; }
        catch { return false; }
    }
    var serviceUp = await Answers(forward, "MPAI/AIFU/Status");
    var storeUp = await Answers(storeHttp, "MPAI/Store/L3");
    return Results.Json(new { service = serviceUp, store = storeUp });
});

// A SUBMISSION to the Store, passed on with the two headers the Store knows (the administrator key; an Implementer's signature).
app.MapPost("/thalia/store/L3", async (HttpContext http) =>
{
    try
    {
        using var body = new MemoryStream();
        await http.Request.Body.CopyToAsync(body);
        body.Position = 0;
        using var request = new HttpRequestMessage(HttpMethod.Post, "MPAI/Store/L3") { Content = new StreamContent(body) };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/json");
        foreach (var h in new[] { "MPAI-Admin", "MPAI-Signature" })
            if (http.Request.Headers.TryGetValue(h, out var v)) request.Headers.TryAddWithoutValidation(h, v.ToString());
        using var response = await storeHttp.SendAsync(request, http.RequestAborted);
        http.Response.StatusCode = (int)response.StatusCode;
        http.Response.ContentType = "application/json";
        await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
    }
    catch (HttpRequestException)
    {
        http.Response.StatusCode = StatusCodes.Status502BadGateway;
        await http.Response.WriteAsync("The MPAI Store does not answer.");
    }
});

app.MapGet("/thalia/store/{**rest}", async (string rest, HttpContext http) =>
{
    try
    {
        using var response = await storeHttp.GetAsync("MPAI/Store/" + rest + http.Request.QueryString, http.RequestAborted);
        http.Response.StatusCode = (int)response.StatusCode;
        if (response.Content.Headers.ContentType is { } type) http.Response.ContentType = type.ToString();
        await response.Content.CopyToAsync(http.Response.Body, http.RequestAborted);
    }
    catch (HttpRequestException)
    {
        http.Response.StatusCode = StatusCodes.Status502BadGateway;
        await http.Response.WriteAsync("The MPAI Store does not answer.");
    }
});

app.MapFallbackToFile("index.html");

Console.WriteLine($"=== MPAI-MAS browser client ===");
Console.WriteLine($"  Open:     {builder.Configuration["Urls"] ?? "https://localhost:5010"}");
Console.WriteLine($"  Service:  {service}{(serviceToken is null ? "" : " (with its bearer token)")}");
Console.WriteLine($"  Store:    {storeUri}");
Console.WriteLine($"  Assets:   {assets}");
app.Run();

// The repository root is the first folder above this program holding AIF\UserAgent\Assets.
static string RepositoryRoot(string from)
{
    for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "AIF", "UserAgent", "Assets", "cav-webview.html")))
            return dir.FullName;
    throw new DirectoryNotFoundException($"No AIF\\UserAgent\\Assets\\cav-webview.html above {from}.");
}
