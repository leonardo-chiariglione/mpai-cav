using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.FileProviders;

using Mpai.Abv.Server;

// THE AVATAR VIDEOCONFERENCE SERVER OF THE LIVE DEMO. HTTP on 5180 and HTTPS on 5181 (browsers allow the microphone only on localhost or over HTTPS: the certificate is the server's own,
// made once for this machine's names and addresses - a participant accepts the warning once). ABV/PROTOCOL.md says what the browser client and the server say to each other.

var root = FindRoot();
var here = AppContext.BaseDirectory;
var siteRoot = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) ? Directory.GetCurrentDirectory() : Path.Combine(root, "Apps", "ABV", "AVS", "Server");
var avatarDir = Path.Combine(siteRoot, "avatars");
Directory.CreateDirectory(avatarDir); Directory.CreateDirectory(Path.Combine(siteRoot, "wwwroot"));

// The avatars to choose from: four men and a woman (Rocketbox, MIT). The other woman, Thalia, is the Secretary's and is not offered.
var labels = new Dictionary<string, string> { ["Persona2"] = "Man 1", ["Male03"] = "Man 2", ["Male05"] = "Man 3", ["Male08"] = "Man 4", ["Persona1"] = "Woman" };
var offered = labels.Keys.ToList();
var avatars = Directory.EnumerateFiles(avatarDir, "*.glb").Select(Path.GetFileNameWithoutExtension).Where(n => n is not null && offered.Contains(n!)).OrderBy(n => offered.IndexOf(n!))
    .Select(n => new Meeting.AvatarInfo(n!, "/avatars/" + n + ".glb", labels[n!])).ToList();
if (avatars.Count == 0) { Console.WriteLine($"[ABV] no avatars (*.glb) in {avatarDir}"); return 1; }

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = siteRoot, WebRootPath = Path.Combine(siteRoot, "wwwroot") });
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("ABV_PORT"), out var hp) ? hp : 5180;
var httpsPort = httpPort + 1;
var cert = Certificate(Path.Combine(siteRoot, "abv-cert.pfx"));
builder.WebHost.ConfigureKestrel(k =>
{
    // Closed by default: this PC only. Open to the network only when the run script says so (ABV_LAN=1) - the others on the LAN, or a tunnel, then reach it.
    if (Environment.GetEnvironmentVariable("ABV_LAN") == "1")
    {
        k.ListenAnyIP(httpPort);
        k.ListenAnyIP(httpsPort, o => { o.Protocols = HttpProtocols.Http1; o.UseHttps(cert); });
    }
    else
    {
        k.ListenLocalhost(httpPort);
        k.ListenLocalhost(httpsPort, o => { o.Protocols = HttpProtocols.Http1; o.UseHttps(cert); });
    }
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var meetingCode = Environment.GetEnvironmentVariable("ABV_CODE") ?? "";       // where set (the tunnel script), nobody joins without it
var whisper = new Whisper(root);
var meeting = new Meeting(root, avatars, whisper);
var app = builder.Build();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
app.UseDefaultFiles();
var types = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider(); types.Mappings[".glb"] = "model/gltf-binary"; types.Mappings[".mjs"] = "text/javascript";
app.UseStaticFiles(new StaticFileOptions { ContentTypeProvider = types, OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
app.UseStaticFiles(new StaticFileOptions { FileProvider = new PhysicalFileProvider(avatarDir), RequestPath = "/avatars", ContentTypeProvider = types });

// The join page's pictures: the first browser to render an avatar leaves its picture here (a PNG, as the body of the request), so that the next page opens with them at once.
Directory.CreateDirectory(Path.Combine(siteRoot, "wwwroot", "thumbs"));
app.MapPost("/api/thumb/{id}", async (string id, HttpRequest request) =>
{
    if (avatars.All(a => a.Id != id)) return Results.NotFound();
    using var body = new MemoryStream(); await request.Body.CopyToAsync(body);
    if (body.Length is 0 or > 2_000_000) return Results.BadRequest();
    await File.WriteAllBytesAsync(Path.Combine(siteRoot, "wwwroot", "thumbs", id + ".png"), body.ToArray());
    return Results.Ok();
});

app.MapGet("/api/info", () => Results.Json(new { codeRequired = meetingCode.Length > 0 }));

app.MapGet("/api/avatars", () => Results.Json(avatars.Select(a => new { id = a.Id, file = a.File, label = a.Label })));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    Meeting.Participant? who = null;
    var buffer = new byte[64 * 1024];
    try
    {
        while (ws.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult received;
            do
            {
                received = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (received.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, received.Count);
            } while (!received.EndOfMessage);
            if (received.MessageType == WebSocketMessageType.Close) break;
            var bytes = message.ToArray();

            if (received.MessageType == WebSocketMessageType.Binary)
            {
                if (who is not null && bytes.Length > 1 && bytes[0] == 1) await meeting.Audio(who, bytes[1..]);
                continue;
            }

            JsonNode? json; try { json = JsonNode.Parse(Encoding.UTF8.GetString(bytes)); } catch (JsonException) { continue; }
            switch ((string?)json?["t"])
            {
                case "join" when who is null && meetingCode.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(((string?)json["code"] ?? "").Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(meetingCode.ToUpperInvariant())):
                    Console.WriteLine("[ABV] a join was refused: wrong meeting code");
                    await Meeting.SendText(new Meeting.Participant { Ws = ws }, new JsonObject { ["t"] = "error", ["text"] = "wrong meeting code" });
                    break;
                case "join" when who is null:
                    var (joined, error) = meeting.Join((string?)json["name"] ?? "", (string?)json["avatar"] ?? "", ws, (string?)json["lang"]);
                    if (joined is null)
                    {
                        await Meeting.SendText(new Meeting.Participant { Ws = ws }, new JsonObject { ["t"] = "error", ["text"] = error });
                        break;
                    }
                    who = joined;
                    Console.WriteLine($"[ABV] {who.Id} joins at seat {who.Seat} as {who.Avatar}, speaking {who.Lang}");
                    await Meeting.SendText(who, meeting.Welcome(who));
                    await meeting.BroadcastScenes();
                    await Meeting.SendText(who, new JsonObject { ["t"] = "minutes", ["text"] = meeting.MinutesText() });
                    break;
                case "chat" when who is not null:
                    await meeting.Chat(who, (string?)json["text"] ?? "");
                    break;
            }
        }
    }
    catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
    finally
    {
        if (who is not null)
        {
            Console.WriteLine($"[ABV] {who.Id} leaves");
            meeting.Leave(who);
            await meeting.BroadcastScenes();
        }
    }
});

_ = Task.Run(async () =>
{
    while (true) { await Task.Delay(150); try { await meeting.Tick(); } catch (Exception e) { Console.WriteLine("[ABV] tick: " + e.Message); } }
});

whisper.Start();
Console.WriteLine($"[ABV] Avatars: {string.Join(", ", avatars.Select(a => a.Id))}");
Console.WriteLine($"[ABV] Open  http://localhost:{httpPort}/   (this PC only)");
if (Environment.GetEnvironmentVariable("ABV_LAN") == "1")
{
    Console.WriteLine("[ABV] OPEN TO THE NETWORK (ABV_LAN=1): the others open one of");
    foreach (var ip in Addresses()) Console.WriteLine($"[ABV]   https://{ip}:{httpsPort}/   (accept the certificate warning once)");
}
AppDomain.CurrentDomain.ProcessExit += (_, _) => whisper.Dispose();
await app.RunAsync();
return 0;

static string FindRoot()
{
    var d = new DirectoryInfo(AppContext.BaseDirectory);
    while (d is not null && !Directory.Exists(Path.Combine(d.FullName, "AIMs", "AMDs"))) d = d.Parent;
    return d?.FullName ?? throw new InvalidOperationException("the repository (AIMs/AMDs) was not found above " + AppContext.BaseDirectory);
}

static IEnumerable<string> Addresses() =>
    NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses).Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).Select(a => a.Address.ToString()).Distinct();

// The server's own certificate, for localhost, the machine's name and every address it has now; kept in a file so that the browsers' acceptance holds from one run to the next.
static X509Certificate2 Certificate(string path)
{
    var names = new HashSet<string>(Addresses()) { "localhost", Environment.MachineName };
    if (File.Exists(path))
    {
        try
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, "abv", X509KeyStorageFlags.Exportable);
            var san = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            var covered = san is not null && names.All(n => san.EnumerateDnsNames().Contains(n, StringComparer.OrdinalIgnoreCase) || san.EnumerateIPAddresses().Any(ip => ip.ToString() == n));
            if (covered && existing.NotAfter > DateTime.UtcNow.AddDays(7)) return existing;
        }
        catch (CryptographicException) { }
    }
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=ABV demo server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var alt = new SubjectAlternativeNameBuilder();
    foreach (var n in names) { if (IPAddress.TryParse(n, out var ip)) alt.AddIpAddress(ip); else alt.AddDnsName(n); }
    alt.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(alt.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
    File.WriteAllBytes(path, made.Export(X509ContentType.Pfx, "abv"));
    return X509CertificateLoader.LoadPkcs12FromFile(path, "abv", X509KeyStorageFlags.Exportable);
}
