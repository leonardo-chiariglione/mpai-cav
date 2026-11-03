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

using Mpai.Cae.Apps;
using Mpai.Cae.Eae.Server;
using Mpai.Cae.Usc;

// THE ENHANCED AUDIOCONFERENCE SERVER OF THE LIVE DEMO. HTTP on 5190 and HTTPS on 5191 (browsers allow the microphone only on localhost or over HTTPS: the certificate is the server's own, made
// once for this machine's names and addresses - a participant accepts the warning once). PROTOCOL.md says what the browser client and the server say to each other.
//
//   EAE_PORT      the HTTP port (default 5190; HTTPS is the next)
//   EAE_LAN=1     answer on every address of this PC, not on this PC only
//   EAE_CODE      a meeting code a participant must give to join
//   EAE_ARRAY     a file with the Microphone Array Geometry (CAE-MAG-V2.5 JSON) of the room's array; without it, six microphones on the axes, 1.2 cm from the centre
//   EAE_SEGMENT   seconds of the room's audio the Module takes at a time (default 3)
//   EAE_DEMO=1    a virtual room: two speakers who speak in turn and together in a noisy room, as if a room were captured (no microphone array needed)
//   EAE_SPEECH    with EAE_DEMO: two recordings of speech (a.wav;b.wav, WAV) as the two speakers - the first from 60 degrees (a little above), the second from -90 - each repeating every
//                 recording length + 2 s, in a room with noise (EAE_NOISE, default 150): real voices in a simulated room

var root = ModuleHost.Root;
var siteRoot = Directory.Exists(Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")) ? Directory.GetCurrentDirectory() : Path.Combine(root, "Apps", "CAE", "EAE", "Server");
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = siteRoot, WebRootPath = Path.Combine(siteRoot, "wwwroot") });
var httpPort = int.TryParse(Environment.GetEnvironmentVariable("EAE_PORT"), out var hp) ? hp : 5190;
var httpsPort = httpPort + 1;
var plain = Environment.GetEnvironmentVariable("EAE_HTTP_ONLY") == "1";                  // no HTTPS (the browser then allows the microphone on localhost only)
var cert = plain ? null! : Certificate(Path.Combine(siteRoot, "eae-cert.pfx"));
var lan = Environment.GetEnvironmentVariable("EAE_LAN") == "1";
builder.WebHost.ConfigureKestrel(k =>
{
    if (plain) { if (lan) k.ListenAnyIP(httpPort); else k.ListenLocalhost(httpPort); }
    else if (lan) { k.ListenAnyIP(httpPort); k.ListenAnyIP(httpsPort, o => { o.Protocols = HttpProtocols.Http1; o.UseHttps(cert); }); }
    else { k.ListenLocalhost(httpPort); k.ListenLocalhost(httpsPort, o => { o.Protocols = HttpProtocols.Http1; o.UseHttps(cert); }); }
});
builder.Logging.SetMinimumLevel(LogLevel.Warning);

var code = Environment.GetEnvironmentVariable("EAE_CODE") ?? "";
var array = Environment.GetEnvironmentVariable("EAE_ARRAY") is { Length: > 0 } f ? ArrayGeometry.FromJson(JsonNode.Parse(File.ReadAllText(f))) : Room.Octahedron();
var segment = double.TryParse(Environment.GetEnvironmentVariable("EAE_SEGMENT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var sg) ? sg : 3.0;

if (Environment.GetEnvironmentVariable("EAE_TRACE") == "1")                       // a thread of its own that says the server lives, and what its pool is doing
    new Thread(() => { while (true) { Thread.Sleep(1000); Console.WriteLine($"[EAE] heartbeat {DateTime.Now:HH:mm:ss}: pool threads {ThreadPool.ThreadCount}, queued {ThreadPool.PendingWorkItemCount}, GCs {GC.CollectionCount(0)}/{GC.CollectionCount(2)}"); } }) { IsBackground = true, Name = "heartbeat" }.Start();
Console.WriteLine("[EAE] Starting the Module (a first start takes a while)...");
var bisect = Environment.GetEnvironmentVariable("EAE_BISECT") ?? "";                            // a fault is being looked for: "none" = no Module, "host" = the Module and no User Agent
using var module = bisect.Contains("none") ? null! : ModuleHost.Start("1CAE-EAE-V2.5-I01", null, s => Console.WriteLine("[EAE] " + s));
using var conference = new Conference(module, array, segment); if (bisect.Length == 0) conference.Start();

var app = builder.Build();
if (!bisect.Contains("nomiddleware"))
{
    app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(20) });
    app.UseDefaultFiles();
    app.UseStaticFiles(new StaticFileOptions { OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache" });
}
app.MapGet("/api/info", () => Results.Json(new { codeRequired = code.Length > 0, channels = conference.Channels, rate = conference.Rate, segment }));

app.Map("/ws", async context =>
{
    if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
    using var ws = await context.WebSockets.AcceptWebSocketAsync();
    Conference.Participant? who = null;
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
                if (who is { Role: "room" } && bytes.Length > 1 && bytes[0] == 1) conference.Audio(bytes[1..]);
                continue;
            }

            JsonNode? json; try { json = JsonNode.Parse(Encoding.UTF8.GetString(bytes)); } catch (JsonException) { continue; }
            if ((string?)json?["t"] != "join" || who is not null) continue;
            if (code.Length > 0 && !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(((string?)json["code"] ?? "").Trim().ToUpperInvariant()), Encoding.UTF8.GetBytes(code.ToUpperInvariant())))
            {
                Console.WriteLine("[EAE] a join was refused: wrong meeting code");
                await Conference.SendText(new Conference.Participant { Ws = ws }, new JsonObject { ["t"] = "error", ["text"] = "wrong meeting code" });
                continue;
            }
            var (joined, error) = conference.Join((string?)json["name"] ?? "", (string?)json["role"] ?? "listener", ws);
            if (joined is null) { await Conference.SendText(new Conference.Participant { Ws = ws }, new JsonObject { ["t"] = "error", ["text"] = error }); continue; }
            who = joined;
            Console.WriteLine($"[EAE] {who.Id} joins as {who.Role}");
            await Conference.SendText(who, conference.Welcome(who));
        }
    }
    catch (Exception e) when (e is WebSocketException or OperationCanceledException or IOException) { }
    finally { if (who is not null) { Console.WriteLine($"[EAE] {who.Id} leaves"); conference.Leave(who); } }
});

// A virtual room: segments of two speakers who speak one after the other and together, as the Module would be given them by a captured room.
if (Environment.GetEnvironmentVariable("EAE_DEMO") == "1")
{
    var speech = (Environment.GetEnvironmentVariable("EAE_SPEECH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var noise = int.TryParse(Environment.GetEnvironmentVariable("EAE_NOISE"), out var nz) ? nz : 150;
    MultiAudio? film = null;                                                       // with real speech: a long capture, cut into segments in turn
    if (speech.Length == 2)
    {
        var a = Inputs.Speech(speech[0]); var b = Inputs.Speech(speech[1]);
        var period = Math.Max(a.Seconds, b.Seconds) + 2.0; var cycles = 6;
        film = Room.Speech(array, period * cycles, noise, new Room.Voice(a, 60, 20, 0.9, 0, period), new Room.Voice(b, -90, 0, 0.9, period * 0.4, period));
        Console.WriteLine($"[EAE] EAE_SPEECH: {speech[0]} from 60 degrees and {speech[1]} from -90, a cycle of {period:0.0} s, noise {noise}.");
    }
    _ = Task.Run(async () =>
    {
      try {
        for (var k = 0; ; k++)
        {
            await Task.Delay(TimeSpan.FromSeconds(segment));
            if (Environment.GetEnvironmentVariable("EAE_TRACE") == "1") Console.WriteLine($"[EAE] the virtual room gives segment {k}");
            if (film is not null)
            {
                var n = (int)(segment * array.Rate); var at = k * n % Math.Max(n, film.Length - n);
                if (Environment.GetEnvironmentVariable("EAE_TRACE") == "1") Console.WriteLine($"[EAE] slice {at}+{n} of {film.Length}");
                var slice = new MultiAudio(film.Channels.Select(c => c[at..(at + n)]).ToArray(), array.Rate);
                if (Environment.GetEnvironmentVariable("EAE_TRACE") == "1") Console.WriteLine("[EAE] sliced");
                conference.Process(slice);
                continue;
            }
            var a = new Room.Source(1000, 8000, 60, 20, 0, k % 3 == 1 ? segment * 0.6 : segment);
            var b = new Room.Source(2000, 6000, -90, 0, k % 3 == 1 ? segment * 0.4 : 0, segment);
            conference.Process(k % 3 == 0 ? Room.Capture(array, segment, 200, a) : k % 3 == 1 ? Room.Capture(array, segment, 200, a, b) : Room.Capture(array, segment, 200, b));
        }
      } catch (Exception e) { Console.WriteLine("[EAE] the virtual room stopped: " + e); }
    });
    Console.WriteLine("[EAE] EAE_DEMO: a virtual room speaks.");
}
Console.WriteLine($"[EAE] The room's array has {array.Count} microphones at {array.Rate} Hz; the Module takes {segment} s at a time.");
Console.WriteLine($"[EAE] Open  http://localhost:{httpPort}/   (this PC only)");
if (lan)
{
    Console.WriteLine("[EAE] OPEN TO THE NETWORK (EAE_LAN=1): the others open one of");
    foreach (var ip in Addresses()) Console.WriteLine($"[EAE]   https://{ip}:{httpsPort}/   (accept the certificate warning once)");
}
if (Environment.GetEnvironmentVariable("EAE_NOWEB") == "1") await Task.Delay(Timeout.Infinite);        // the Module and the virtual room without the web server, to tell which of them a fault is in
await app.RunAsync();
return 0;

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
            var existing = X509CertificateLoader.LoadPkcs12FromFile(path, "eae", X509KeyStorageFlags.Exportable);
            var san = existing.Extensions.OfType<X509SubjectAlternativeNameExtension>().FirstOrDefault();
            var covered = san is not null && names.All(n => san.EnumerateDnsNames().Contains(n, StringComparer.OrdinalIgnoreCase) || san.EnumerateIPAddresses().Any(ip => ip.ToString() == n));
            if (covered && existing.NotAfter > DateTime.UtcNow.AddDays(7)) return existing;
        }
        catch (CryptographicException) { }
    }
    using var rsa = RSA.Create(2048);
    var request = new CertificateRequest("CN=EAE demo server", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    var alt = new SubjectAlternativeNameBuilder();
    foreach (var n in names) { if (IPAddress.TryParse(n, out var ip)) alt.AddIpAddress(ip); else alt.AddDnsName(n); }
    alt.AddIpAddress(IPAddress.Loopback);
    request.CertificateExtensions.Add(alt.Build());
    request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
    var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
    File.WriteAllBytes(path, made.Export(X509ContentType.Pfx, "eae"));
    return X509CertificateLoader.LoadPkcs12FromFile(path, "eae", X509KeyStorageFlags.Exportable);
}