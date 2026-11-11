using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Security.Cryptography.X509Certificates;

using Mpai.Aif.PortData;
using AIF.Bout;
using AIF.Trust;
using System.Security.Cryptography;

namespace Mpai.Mas.Server;

// The MPAI-MAS V1.1 API over a Module runner.
//
// KNOWS NO APPLICATION. No Module name, no AIM name, no Data Type literal
// appears below. The Module name arrives in a request body, the Data Types
// arrive in URL segments, and the codecs come from a registry. Adding an
// application means writing a host; adding a Data Type means writing a codec.
// Neither means editing this file.
//
// ROUTES, from the specification:
//   POST   /MPAI/AIFU/Controller                                -> 201 {id}
//   POST   /MPAI/AIFU/{cid}/MODULE/Start        {"module": "x"} -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}                        -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Pause                  -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Resume                 -> 200 {state}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Stop                   -> 200
//   POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Input/{DataType}/{PortNumber}   -> 200
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}  -> 200 application/json
//
// A BOUNDARY PORT IS TWO PATH SEGMENTS (MPAI-MAS V1.1, API 1): the Data Type in canonical form, its Header, and the Port Number, always present. The
// form <DataType>#<PortNumber> is not used in a URL, where # begins a fragment. The body is the JSON object of the Data Type (application/json).
//   POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}/Bout        -> 201 the Bout | 202 {BoutID}
//   GET    /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}/Bout        -> 200 the Bout in force
//   DELETE /MPAI/AIFU/{cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}/Bout        -> 200 the CloseBout Trust Message
//   POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Input/{DataType}/{PortNumber}/Bout         -> 201 (the Bout of the RCA accepted)
//   POST   /MPAI/AIFU/{cid}/MODULE/{mid}/Input/{DataType}/{PortNumber}/Bout/Close   -> 200
//   DELETE /MPAI/AIFU/Controller/{cid}                          -> 200
//
// MODULE, not AIW. The old server used /AIW/ throughout and was non-conformant
// on every route regardless of how its ports were keyed.
public sealed class MasServer
{
    private const string Prefix = "/MPAI/AIFU";

    // WHAT THIS SERVICE OFFERS, if anything. A Service with no catalogue serves
    // Modules to clients that already know which one they want; a Service with
    // one also tells a client what Apps it has, so a client holding no
    // application can be handed one.
    public AppCatalogue Catalogue { get; init; } = AppCatalogue.Scan(null);

    // WHICH APPS EACH COLLECTION SHOWS. Unless set, the catalogue's Apps are the
    // default collection and there are no others - today's behaviour exactly.
    private AppOffer? offer;
    public AppOffer Offer { get => offer ??= AppOffer.FromCatalogue(Catalogue); init => offer = value; }

    private readonly IModuleRunner runner;
    private readonly PortDataCodecs codecs;
    private readonly string listenUrl;
    private readonly string? bearerToken;

    // The certificate to present, or null to let Kestrel choose - which on a
    // loopback address means the development certificate, and anywhere else
    // means the caller should not have got this far. The server package neither
    // creates nor renews: it presents what the platform placed on disk.
    private readonly X509Certificate2? certificate;

    // The intermediates presented WITH the certificate, so a client can build a
    // path to a root it already trusts. Not a store of what this server trusts.
    private readonly X509Certificate2Collection? authority;

    // One SCI per Controller Instance created by the RCA.
    private readonly ConcurrentDictionary<string, Sci> instances = new();

    // WHO IS USING THE SERVICE NOW. A client names itself with a random
    // identifier, made when it starts, on every request (header MPAI-Client); one
    // person's client uses several Controller Instances - one for MPAI-MAS, one
    // per App - so they are counted by that identifier, not by instance. A client
    // counts while it is open: it says goodbye when it closes (POST /Leave), and
    // while open it makes a request at least every 30 seconds, so one that crashes
    // or loses its connection drops out after 90.
    private readonly ConcurrentDictionary<string, DateTimeOffset> clients = new();
    private static readonly TimeSpan ActiveWindow = TimeSpan.FromSeconds(90);

    public MasServer(
        IModuleRunner runner,
        PortDataCodecs codecs,
        string listenUrl,
        string? bearerToken = null,
        X509Certificate2? certificate = null,
        X509Certificate2Collection? authority = null)
    {
        this.runner      = runner;
        this.codecs      = codecs;
        this.listenUrl   = listenUrl;
        this.bearerToken = bearerToken;
        this.certificate = certificate;
        this.authority   = authority;
    }

    private sealed class Sci
    {
        public string Id { get; init; } = string.Empty;

        // The client that made it (header MPAI-Client): when that client leaves, or
        // is heard from no more, the session of this Controller Instance closes.
        public string Client { get; init; } = string.Empty;

        // Module Instance id -> running Module.
        public ConcurrentDictionary<string, ModuleInstance> Modules { get; } = new();

        // THE CONTROLLER OF THE SCI AS A TRUST ANCHOR: its key signs the Bouts it gives on output Ports and the Instance Credential it issues to the
        // RCA (MPAI-MAS V1.1, API 2.1), with which the RCA gives Bouts on input Ports.
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        public BoutCredentials? Credentials { get; set; }
        private int nextBout = 32768 + RandomNumberGenerator.GetInt32(1 << 20);
        public int NewBoutId() => Interlocked.Increment(ref nextBout);
    }

    private sealed class ModuleInstance
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string State { get; set; } = "ACTIVE";
        public DateTimeOffset Changed { get; set; } = DateTimeOffset.UtcNow;

        // Boundary inputs buffered until the run. Keyed "DataType#PortNumber".
        public Dictionary<string, string> Inputs { get; } = new();

        // Outputs of the last completed run, same keying. Null before a run.
        public Dictionary<string, string>? Outputs { get; set; }

        // The Bouts in force: given on output Ports, received on input Ports, keyed "DataType#PortNumber".
        public ConcurrentDictionary<string, BoutGiver> OutputBouts { get; } = new();
        public ConcurrentDictionary<string, BoutReceiver> InputBouts { get; } = new();

        // One run at a time per Module. A run holds models and takes seconds;
        // two overlapping runs would interleave their boundaries.
        public SemaphoreSlim Gate { get; } = new(1, 1);
    }

    public async Task RunAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls(listenUrl);
        builder.Logging.ClearProviders();

        // ONE LINE, AND IT IS THE WHOLE OF TLS. Kestrel negotiates from a
        // certificate held in memory, identically on Windows and Linux. This is
        // why the listener had to be Kestrel: HttpListener binds a certificate
        // to a port administratively on Windows and cannot serve HTTPS on Linux
        // at any price, so with it there was no OS-independent HTTPS layer to
        // write. Here the platform difference is reduced to a file path.
        if (certificate is not null)
            builder.WebHost.ConfigureKestrel(
                options => options.ConfigureHttpsDefaults(https =>
                {
                    https.ServerCertificate = certificate;

                    // A CLIENT THAT CANNOT BUILD THE CHAIN REFUSES BEFORE ASKING.
                    // It sends no request, so the server sees nothing and reports
                    // nothing - the failure is visible only at the client, as a
                    // trust error. Presenting the intermediates is what prevents it.
                    if (authority is not null && authority.Count > 0)
                        https.ServerCertificateChain = authority;
                }));

        var app = builder.Build();
        app.Run(HandleAsync);

        Console.WriteLine($"[MAS] Listening on {listenUrl}");
        Console.WriteLine(certificate is null
            ? "[MAS] No certificate supplied - Kestrel default."
            : $"[MAS] Certificate: {certificate.Subject} (expires {certificate.NotAfter:u})");
        Console.WriteLine($"[MAS] Data Types: {string.Join(", ", codecs.KnownDataTypes)}");
        Console.WriteLine(bearerToken is null
            ? "[MAS] No bearer token configured - every caller is accepted."
            : "[MAS] Bearer token required.");

        await app.RunAsync();
    }

    private async Task HandleAsync(HttpContext ctx)
    {
        try
        {
            if (!Authorised(ctx))
            {
                await Write(ctx, 401, "text/plain", "Unauthorised.");
                return;
            }

            var path   = (ctx.Request.Path.Value ?? string.Empty).TrimEnd('/');
            var method = ctx.Request.Method;

            var client = ctx.Request.Headers["MPAI-Client"].ToString();
            if (client.Length is > 0 and <= 64) clients[client] = DateTimeOffset.UtcNow;

            if (!path.StartsWith(Prefix, StringComparison.Ordinal))
            {
                await Write(ctx, 404, "text/plain", "Not an MPAI AIFU route.");
                return;
            }

            // Segments after the prefix.
            var rest = path.Substring(Prefix.Length).Trim('/');
            var segs = rest.Length == 0
                ? Array.Empty<string>()
                : rest.Split('/');

            // POST /Leave - the caller is closing: it no longer counts
            if (method == "POST" && segs.Length == 1 && segs[0] == "Leave")
            {
                if (client.Length > 0) clients.TryRemove(client, out _);
                Left(client);
                await Write(ctx, 200, "text/plain", "Goodbye.");
                return;
            }

            // GET /Status - how many clients are active now, the caller included
            if (method == "GET" && segs.Length == 1 && segs[0] == "Status")
            {
                var since = DateTimeOffset.UtcNow - ActiveWindow;
                foreach (var old in clients.Where(c => c.Value < since).Select(c => c.Key).ToList())
                {
                    clients.TryRemove(old, out _);
                    Left(old);
                }
                await Write(ctx, 200, "application/json", $"{{\"activeClients\":{clients.Count}}}");
                return;
            }

            // THE APP ROUTES: /Apps, /Apps/{id}, /Apps/{id}/Icon as always, for the
            // default collection; /Apps?q=&category=, /Apps/{id}/Descriptor,
            // /Categories and /Collections; and all of them under /c/{collection}.
            if (method == "GET" && await ServeApps(ctx, segs))
                return;

            // POST /Controller
            if (method == "POST" && segs.Length == 1 && segs[0] == "Controller")
            {
                await CreateController(ctx);
                return;
            }

            // DELETE /Controller/{cid}
            if (method == "DELETE" && segs.Length == 2 && segs[0] == "Controller")
            {
                await DeleteController(ctx, segs[1]);
                return;
            }

            // Everything else is {cid}/MODULE/...
            if (segs.Length < 2 || segs[1] != "MODULE")
            {
                await Write(ctx, 404, "text/plain", "Unknown route.");
                return;
            }

            if (!instances.TryGetValue(segs[0], out var sci))
            {
                await Write(ctx, 404, "text/plain", "No such Controller Instance.");
                return;
            }

            // POST {cid}/MODULE/Start
            if (method == "POST" && segs.Length == 3 && segs[2] == "Start")
            {
                await StartModule(ctx, sci);
                return;
            }

            if (segs.Length < 3)
            {
                await Write(ctx, 404, "text/plain", "Unknown route.");
                return;
            }

            if (!sci.Modules.TryGetValue(segs[2], out var module))
            {
                await Write(ctx, 404, "text/plain", "No such Module Instance.");
                return;
            }

            // GET {cid}/MODULE/{mid}
            if (method == "GET" && segs.Length == 3)
            {
                await WriteState(ctx, sci, module);
                return;
            }

            // GET {cid}/MODULE/{mid}/{Pause|Resume|Stop}
            if (method == "GET" && segs.Length == 4)
            {
                switch (segs[3])
                {
                    case "Pause":
                        module.State   = "PAUSED";
                        module.Changed = DateTimeOffset.UtcNow;
                        await WriteState(ctx, sci, module);
                        return;

                    case "Resume":
                        module.State   = "ACTIVE";
                        module.Changed = DateTimeOffset.UtcNow;
                        await WriteState(ctx, sci, module);
                        return;

                    case "Stop":
                        runner.Stop(module.Name);
                        sci.Modules.TryRemove(module.Id, out _);
                        await Write(ctx, 200, "text/plain", "OK");
                        return;
                }
            }

            // {cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}/Bout: Open (POST), Get (GET), Close (DELETE)
            if (segs.Length == 7 && segs[3] == "Output" && segs[6] == "Bout" && method is "POST" or "GET" or "DELETE")
            {
                await OutputBout(ctx, sci, module, method, segs[4], segs[5]);
                return;
            }

            // POST {cid}/MODULE/{mid}/Input/{DataType}/{PortNumber}/Bout, and .../Bout/Close
            if (method == "POST" && segs.Length >= 7 && segs[3] == "Input" && segs[6] == "Bout" && (segs.Length == 7 || (segs.Length == 8 && segs[7] == "Close")))
            {
                await InputBout(ctx, sci, module, segs[4], segs[5], close: segs.Length == 8);
                return;
            }

            // POST {cid}/MODULE/{mid}/Input/{DataType}/{PortNumber}
            if (method == "POST" && segs.Length == 6 && segs[3] == "Input")
            {
                await ReceiveInput(ctx, module, segs[4], segs[5]);
                return;
            }

            // GET {cid}/MODULE/{mid}/Output/{DataType}/{PortNumber}
            if (method == "GET" && segs.Length == 6 && segs[3] == "Output")
            {
                await SendOutput(ctx, sci, module, segs[4], segs[5]);
                return;
            }

            await Write(ctx, 404, "text/plain", "Unknown route.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MAS] EXCEPTION: {ex}");
            await Write(ctx, 500, "text/plain", ex.Message);
        }
    }

    private async Task<bool> ServeApps(HttpContext ctx, string[] segs)
    {
        if (segs.Length == 1 && segs[0] == "Collections")
        {
            await Write(ctx, 200, "application/json", Offer.CollectionsJson());
            return true;
        }

        // Which collection: named by /c/{id}/..., or the default.
        AppOffer.Collection collection = Offer.Default;
        string prefix = "";
        var rest = segs;
        if (segs.Length >= 3 && segs[0] == "c")
        {
            var named = Offer.Find(segs[1]);
            if (named is null) { await Write(ctx, 404, "text/plain", "No such collection."); return true; }
            collection = named; prefix = $"c/{named.Id}/"; rest = segs[2..];
        }
        if (rest.Length == 0) return false;

        if (rest.Length == 1 && rest[0] == "Categories")
        {
            await Write(ctx, 200, "application/json", AppOffer.CategoriesJson(collection));
            return true;
        }
        if (rest[0] != "Apps") return false;

        // GET /Apps - the collection's Apps; with ?q= or ?category=, a search
        if (rest.Length == 1)
        {
            var q = ctx.Request.Query["q"].ToString();
            var category = ctx.Request.Query["category"].ToString();
            await Write(ctx, 200, "application/json",
                q.Length == 0 && category.Length == 0
                    ? AppOffer.ListJson(collection.Apps, prefix)
                    : AppOffer.ResultJson(AppOffer.Search(collection, q, category), prefix));
            return true;
        }

        // An App of this collection - or the shell, which is served by name to any client.
        var app = collection.Find(rest[1]) ??
                  (string.Equals(rest[1], Catalogue.ShellId, StringComparison.OrdinalIgnoreCase) ? Catalogue.Find(rest[1]) : null);
        if (app is null) { await Write(ctx, 404, "text/plain", "No such App."); return true; }

        // GET /Apps/{id} - the Workflow Description itself
        if (rest.Length == 2)
        {
            await Write(ctx, 200, "text/plain; charset=utf-8", await System.IO.File.ReadAllTextAsync(app.WorkflowPath));
            return true;
        }
        // GET /Apps/{id}/Icon
        if (rest.Length == 3 && rest[2] == "Icon")
        {
            if (app.IconFile is null) { await Write(ctx, 404, "text/plain", "No icon."); return true; }
            var bytes = await System.IO.File.ReadAllBytesAsync(System.IO.Path.Combine(app.Folder, app.IconFile));
            ctx.Response.StatusCode  = 200;
            ctx.Response.ContentType = IconType(app.IconFile);
            await ctx.Response.Body.WriteAsync(bytes);
            return true;
        }
        // GET /Apps/{id}/Descriptor
        if (rest.Length == 3 && rest[2] == "Descriptor")
        {
            await Write(ctx, 200, "application/json", AppOffer.DescriptorJson(app, prefix));
            return true;
        }
        return false;
    }

    // The SLA chooses among BASIC, DIGEST and BEARER; this implements BEARER,
    // and no token configured means no check - which is correct on a loopback
    // demonstration and wrong the moment the port is reachable.
    private bool Authorised(HttpContext ctx)
    {
        if (bearerToken is null) return true;

        var header = ctx.Request.Headers["Authorization"].ToString();
        return header.StartsWith("Bearer ", StringComparison.Ordinal) &&
               string.Equals(header.Substring(7).Trim(), bearerToken, StringComparison.Ordinal);
    }

    private async Task CreateController(HttpContext ctx)
    {
        var id = Guid.NewGuid().ToString();
        var sci = new Sci { Id = id, Client = ctx.Request.Headers["MPAI-Client"].ToString() };
        sci.Credentials = new BoutCredentials(new PtfIssuer(id, sci.Key, id));
        // The Trust Anchor of the SCI (PTF-TRA), over the TLS of this request: with it the RCA verifies the Bouts the SCI gives on output Ports.
        var response = new JsonObject { ["id"] = id, ["anchor"] = new TrustAnchorKey(id, sci.Key, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10)).Object() };

        // AN RCA THAT WILL GIVE BOUTS ON INPUT PORTS sends {"Proof": its Cryptographic Instance Identity}, whose CryptographicInstanceTime is fresh; the
        // SCI verifies it and returns the Instance Credential it issues, valid until the SCI is deleted (MPAI-MAS V1.1, API 2.1).
        var body = await ReadBody(ctx);
        if (body.Trim().Length > 0)
        {
            JsonObject? proof = null;
            try { proof = (JsonNode.Parse(body) as JsonObject)?["Proof"] as JsonObject; } catch (System.Text.Json.JsonException) { }
            if (proof is not null)
            {
                var (credential, reason) = sci.Credentials.Issue(proof, BoutCredentials.RemoteClientApplication);
                if (credential is null)
                {
                    await Write(ctx, 401, "application/json", Refusal(BoutReceiver.SignatureFailure, reason));
                    return;
                }
                response["credential"] = credential.DeepClone();
            }
        }
        instances[id] = sci;

        Console.WriteLine($"[MAS] Controller Instance {id}");

        // The optional "prefix" field is not returned: this server serves the
        // standard /MPAI/AIFU prefix, and an RCA must cope with either.
        await Write(ctx, 201, "application/json", response.ToJsonString());
    }

    private async Task DeleteController(HttpContext ctx, string cid)
    {
        Close(cid);
        await Write(ctx, 200, "text/plain", "OK");
    }

    // THE SESSION CLOSED (M3245 3.3): the Controller Instance ends - its Modules
    // stopped - and what its runs kept for the session goes with it.
    private void Close(string cid)
    {
        if (instances.TryRemove(cid, out var sci))
        {
            foreach (var module in sci.Modules.Values)
                runner.Stop(module.Name);
            // The Instance Credential of the RCA is revoked, and the Bouts it gives are closed (MPAI-MAS V1.1, API 4.2).
            sci.Credentials?.RevokeAll();
        }
        runner.SessionEnded(cid);
        Console.WriteLine($"[MAS] Controller Instance {cid} closed");
    }

    // The sessions of a client that left, or is heard from no more.
    private void Left(string client)
    {
        if (client.Length == 0) return;
        foreach (var sci in instances.Values.Where(i => i.Client == client).ToList()) Close(sci.Id);
    }

    private async Task StartModule(HttpContext ctx, Sci sci)
    {
        var body = await ReadBody(ctx);
        var name = (JsonNode.Parse(body) as JsonObject)?["module"]?.GetValue<string>();

        if (string.IsNullOrWhiteSpace(name))
        {
            await Write(ctx, 400, "text/plain", "Body must be {\"module\": \"...\"}.");
            return;
        }

        var failure = runner.Start(name!);
        if (failure is not null)
        {
            await Write(ctx, 500, "text/plain", failure);
            return;
        }

        var module = new ModuleInstance { Id = Guid.NewGuid().ToString(), Name = name! };
        sci.Modules[module.Id] = module;

        Console.WriteLine($"[MAS] Module {name} started as {module.Id}");

        await WriteState(ctx, sci, module);
    }

    private async Task ReceiveInput(HttpContext ctx, ModuleInstance module, string typeSegment, string numberSegment)
    {
        // A request whose segments do not have the form of the specification is refused with 404 (MPAI-MAS V1.1, API 1).
        if (!PortSegment.TryParse(typeSegment, numberSegment, out var dataType, out var portNumber))
        {
            await Write(ctx, 404, "text/plain", $"Not a boundary Port: '{typeSegment}/{numberSegment}'.");
            return;
        }

        var port = PortSegment.Resolve(
            runner.PortsOf(module.Name), "Input", dataType, portNumber);

        if (port is null)
        {
            await Write(ctx, 404, "text/plain",
                $"No input Port {portNumber} accepting '{dataType}'.");
            return;
        }

        if (!codecs.Knows(dataType))
        {
            await Write(ctx, 415, "text/plain",
                $"No codec for the Data Type '{dataType}'.");
            return;
        }

        // A Bout Instance, where the RCA gives a Bout on this Port (MPAI-MAS V1.1, API 3.3.4).
        if (IsZstd(ctx.Request.ContentType))
        {
            if (!module.InputBouts.TryGetValue(port.Key, out var receiver))
            {
                await Write(ctx, 404, "application/json", Refusal(BoutReceiver.InvalidValue, "no Bout is in force on the Port"));
                return;
            }
            var got = receiver.Receive(await ReadBodyBytes(ctx));
            if (!got.Ok)
            {
                await Write(ctx, 422, "application/json", Refusal(got.ErrorCode!, got.Reason));
                return;
            }
            foreach (var instance in got.Delivered)
                if (await Deliver(module, port, codecs.ToInternal(dataType, Encoding.UTF8.GetBytes(instance.ToJsonString()))) is { } failed)
                {
                    await Write(ctx, 500, "text/plain", failed);
                    return;
                }
            await Write(ctx, got.Held ? 202 : 200, "text/plain", got.Held ? "Held until its segment verifies." : "OK");
            return;
        }

        // The body is the JSON object of the Data Type of the Port (MPAI-MAS V1.1, API 3).
        if (!IsJson(ctx.Request.ContentType))
        {
            await Write(ctx, 415, "text/plain", "The body of an input is application/json or application/zstd.");
            return;
        }

        var wire = await ReadBodyBytes(ctx);
        var value = codecs.ToInternal(dataType, wire);
        if (await Deliver(module, port, value) is { } refusedWrite)
        {
            await Write(ctx, 500, "text/plain", refusedWrite);
            return;
        }
        Console.WriteLine($"[MAS] input {port.Key} ({wire.Length:N0} bytes)");
        await Write(ctx, 200, "text/plain", "OK");
    }

    // An input of the Port, given to the Module: written at once to a continuous Module, buffered for the next run of the others. null, or why not.
    private async Task<string?> Deliver(ModuleInstance module, BoundaryPort port, string value)
    {

        // A continuous Module is written to at once, as the Controller API has it.
        if (runner is IContinuousModuleRunner continuous && continuous.IsContinuous(module.Name))
        {
            return await Task.Run(() => continuous.Write(module.Name, port.Key, value));
        }

        // Under the Module's gate: a run in progress reads these inputs, and must
        // not see them change beneath it.
        await module.Gate.WaitAsync();
        try
        {
            // The RCA names the Data Type it is SENDING, which may be any type the
            // Port accepts. The boundary key comes from the PORT, because that is
            // what the Controller routes on - the Port''s own DataType, the first of
            // its declared set. This is the one place the two conventions meet.
            module.Inputs[port.Key] = value;

            // The first input after a completed run begins a new round. Over MAS the
            // inputs arrive one request at a time and nothing in the API says when a
            // round has ended; this is the only moment that can mean it.
            if (module.Outputs is not null)
            {
                module.Inputs.Clear();
                module.Inputs[port.Key] = value;
                module.Outputs = null;
            }
        }
        finally
        {
            module.Gate.Release();
        }
        return null;
    }

    private async Task SendOutput(HttpContext ctx, Sci sci, ModuleInstance module, string typeSegment, string numberSegment)
    {
        if (!PortSegment.TryParse(typeSegment, numberSegment, out var dataType, out var portNumber))
        {
            await Write(ctx, 404, "text/plain", $"Not a boundary Port: '{typeSegment}/{numberSegment}'.");
            return;
        }

        var port = PortSegment.Resolve(
            runner.PortsOf(module.Name), "Output", dataType, portNumber);

        if (port is null)
        {
            await Write(ctx, 404, "text/plain",
                $"No output Port {portNumber} carrying '{dataType}'.");
            return;
        }

        if (!codecs.Knows(dataType))
        {
            await Write(ctx, 415, "text/plain",
                $"No codec for the Data Type '{dataType}'.");
            return;
        }

        // A continuous Module is read from, as the Controller API has it: what the Port gives within the time the reader states (?timeout=, in milliseconds).
        if (runner is IContinuousModuleRunner continuous && continuous.IsContinuous(module.Name))
        {
            var limit = int.TryParse(ctx.Request.Query["timeout"], out var ms) && ms > 0 ? ms : 60_000;
            var (json, outcome) = await Task.Run(() => continuous.Read(module.Name, port.Key, limit));
            if (json is null)
            {
                await Write(ctx, outcome == "NotProduced" ? 404 : 500, "text/plain", $"No output for Port {port.Key} ({outcome}).");
                return;
            }
            await WriteOutput(ctx, module, port, codecs.ToWire(dataType, json));
            return;
        }

        // The run happens on the first Output request after inputs were written.
        // Reading a second output port then serves the same run rather than
        // running again.
        await module.Gate.WaitAsync();
        try
        {
            if (module.Outputs is null)
            {
                var result = runner.Run(module.Name, module.Inputs, sci.Id);

                if (result.Error is not null)
                {
                    await Write(ctx, 500, "text/plain", result.Error);
                    return;
                }

                module.Outputs = new Dictionary<string, string>(result.Outputs);
            }
        }
        finally
        {
            module.Gate.Release();
        }

        if (!module.Outputs.TryGetValue(port.Key, out var internalJson))
        {
            await Write(ctx, 404, "text/plain", $"No output for Port {port.Key}.");
            return;
        }

        await WriteOutput(ctx, module, port, codecs.ToWire(dataType, internalJson));
    }

    // AN OUTPUT, AS A JSON OBJECT OR, WHERE A BOUT IS IN FORCE ON THE PORT, AS A BOUT INSTANCE (MPAI-MAS V1.1, API 3.3). Where the Bout was opened
    // before the Port gave anything, the Bout is made from the first output, which is given as a full JSON object: the RCA then gets the Bout (Get
    // Bout) before the first Bout Instance.
    private static async Task WriteOutput(HttpContext ctx, ModuleInstance module, BoundaryPort port, byte[] wire)
    {
        if (module.OutputBouts.TryGetValue(port.Key, out var giver) && JsonNode.Parse(wire) is JsonObject instance)
        {
            if (giver.Bout is null) giver.Open(instance);
            else
            {
                var frame = giver.Instance(instance);
                ctx.Response.StatusCode    = 200;
                ctx.Response.ContentType   = "application/zstd";
                ctx.Response.ContentLength = frame.Length;
                await ctx.Response.Body.WriteAsync(frame);
                return;
            }
        }
        ctx.Response.StatusCode    = 200;
        ctx.Response.ContentType   = "application/json";
        ctx.Response.ContentLength = wire.Length;
        await ctx.Response.Body.WriteAsync(wire);
    }

    // OPEN, GET AND CLOSE A BOUT ON AN OUTPUT PORT (MPAI-MAS V1.1, API 3.3.1 to 3.3.3): given and signed by the Controller of the SCI.
    private async Task OutputBout(HttpContext ctx, Sci sci, ModuleInstance module, string method, string typeSegment, string numberSegment)
    {
        if (!PortSegment.TryParse(typeSegment, numberSegment, out var dataType, out var portNumber) ||
            PortSegment.Resolve(runner.PortsOf(module.Name), "Output", dataType, portNumber) is not { } port)
        {
            await Write(ctx, 404, "text/plain", $"Not an output Port: '{typeSegment}/{numberSegment}'.");
            return;
        }

        if (method == "GET")
        {
            if (module.OutputBouts.TryGetValue(port.Key, out var held) && held.Bout is { } bout) await Write(ctx, 200, "application/json", bout.ToJsonString());
            else await Write(ctx, 404, "application/json", Refusal(BoutReceiver.InvalidValue, "no Bout is in force on the Port"));
            return;
        }

        if (method == "DELETE")
        {
            if (!module.OutputBouts.TryRemove(port.Key, out var giver) || giver.Bout is null)
            {
                await Write(ctx, 404, "application/json", Refusal(BoutReceiver.InvalidValue, "no Bout is in force on the Port"));
                return;
            }
            await Write(ctx, 200, "application/json", giver.Close().ToJsonString());
            return;
        }

        // POST: Open Bout.
        if (!port.Bout)
        {
            await Write(ctx, 409, "application/json", Refusal(BoutReceiver.PolicyViolation, "the AIM Description states no Bout for the Port"));
            return;
        }
        if (!Accepts(ctx, "application/zstd"))
        {
            await Write(ctx, 415, "application/json", Refusal(BoutReceiver.UnsupportedAlgorithm, "the RCA does not accept application/zstd"));
            return;
        }
        var request = JsonNode.Parse(await ReadBody(ctx) is { Length: > 0 } b ? b : "{}") as JsonObject ?? new JsonObject();
        var mode = (string?)request["SigningMode"] ?? BoutSecurity.EachInstance;
        var interval = BoutSecurity.AsLong(request["ChainInterval"]) ?? 1;
        if (mode is not (BoutSecurity.EachInstance or BoutSecurity.Chain) || interval < 1 || interval > int.MaxValue)
        {
            await Write(ctx, 400, "application/json", Refusal(BoutReceiver.InvalidValue, "SigningMode is EachInstance or Chain, ChainInterval from 1"));
            return;
        }
        var opened = new BoutGiver(new BoutGiver.Options
        {
            DataType = dataType, PortNumber = portNumber, BoutId = sci.NewBoutId(), SigningMode = mode, ChainInterval = (int)interval,
            Key = sci.Key, KeyId = sci.Id, Issuer = $"urn:mpai:mas:sci:{sci.Id}", SourceAimId = module.Name
        });
        if (module.OutputBouts.TryGetValue(port.Key, out var previous) && previous.Bout is not null) previous.Close();
        module.OutputBouts[port.Key] = opened;

        // The Bout is made from the first Message of the Port: the last output, where there is one.
        if (module.Outputs is { } outputs && outputs.TryGetValue(port.Key, out var last) && JsonNode.Parse(codecs.ToWire(dataType, last)) is JsonObject sample)
        {
            await Write(ctx, 201, "application/json", opened.Open(sample).ToJsonString());
            return;
        }
        await Write(ctx, 202, "application/json", new JsonObject { ["BoutID"] = opened.BoutId }.ToJsonString());
    }

    // GIVE AND END A BOUT ON AN INPUT PORT (MPAI-MAS V1.1, API 3.3.4 and 3.3.5): made and signed by the RCA with the key of its Instance Credential.
    private async Task InputBout(HttpContext ctx, Sci sci, ModuleInstance module, string typeSegment, string numberSegment, bool close)
    {
        if (!PortSegment.TryParse(typeSegment, numberSegment, out var dataType, out var portNumber) ||
            PortSegment.Resolve(runner.PortsOf(module.Name), "Input", dataType, portNumber) is not { } port)
        {
            await Write(ctx, 404, "text/plain", $"Not an input Port: '{typeSegment}/{numberSegment}'.");
            return;
        }
        JsonObject? message = null;
        try { message = JsonNode.Parse(await ReadBody(ctx)) as JsonObject; } catch (System.Text.Json.JsonException) { }
        if (message is null)
        {
            await Write(ctx, 400, "application/json", Refusal(BoutReceiver.SchemaViolation, "the body is not a JSON object"));
            return;
        }

        if (close)
        {
            if (!module.InputBouts.TryGetValue(port.Key, out var receiving))
            {
                await Write(ctx, 404, "application/json", Refusal(BoutReceiver.InvalidValue, "no Bout is in force on the Port"));
                return;
            }
            var ended = receiving.Close(message);
            if (ended.ErrorCode == BoutReceiver.SignatureFailure)
            {
                await Write(ctx, 422, "application/json", Refusal(ended.ErrorCode, ended.Reason));
                return;
            }
            module.InputBouts.TryRemove(port.Key, out _);
            foreach (var instance in ended.Delivered)
                await Deliver(module, port, codecs.ToInternal(dataType, Encoding.UTF8.GetBytes(instance.ToJsonString())));
            await Write(ctx, ended.Ok ? 200 : 422, "application/json", ended.Ok ? "{}" : Refusal(ended.ErrorCode!, ended.Reason));
            return;
        }

        if (!port.Bout)
        {
            await Write(ctx, 409, "application/json", Refusal(BoutReceiver.PolicyViolation, "the AIM Description states no Bout for the Port"));
            return;
        }
        var credentials = sci.Credentials!;
        if (BoutSecurity.KeyIdOf(message) is not { } signer || credentials.KeyFor(signer) is null)
        {
            await Write(ctx, 401, "application/json", Refusal(BoutReceiver.PolicyViolation, "the RCA holds no valid Instance Credential of the SCI"));
            return;
        }
        var receiver = module.InputBouts.GetOrAdd(port.Key, _ => new BoutReceiver(new BoutReceiver.Options
        {
            DataType = dataType, PortNumber = portNumber, KeyFor = credentials.KeyFor, MayGive = credentials.MayGive,
            Clock = () => DateTimeOffset.UtcNow
        }));
        var accepted = receiver.Accept(message);
        if (!accepted.Ok)
        {
            await Write(ctx, 422, "application/json", Refusal(accepted.ErrorCode!, accepted.Reason));
            return;
        }
        await Write(ctx, 201, "application/json", "{}");
    }

    private async Task WriteState(HttpContext ctx, Sci sci, ModuleInstance module)
    {
        var state = new JsonObject
        {
            ["controller"] = sci.Id,
            ["id"]         = module.Id,
            ["name"]       = module.Name,
            ["state"]      = module.State,
            ["change"]     = module.Changed.ToString("o")
        };

        await Write(ctx, 200, "application/json", state.ToJsonString());
    }

    private static bool IsZstd(string? contentType) =>
        contentType is not null && contentType.Split(';')[0].Trim().Equals("application/zstd", StringComparison.OrdinalIgnoreCase);

    private static bool Accepts(HttpContext ctx, string mediaType) =>
        ctx.Request.Headers.Accept.Any(a => a is not null && (a.Contains(mediaType, StringComparison.OrdinalIgnoreCase) || a.Contains("*/*", StringComparison.Ordinal)));

    // A refusal: the MPAI-PTF Error Code of the check that failed (MPAI-MAS V1.1, API 3.3.6).
    private static string Refusal(string errorCode, string? reason) => new JsonObject { ["ErrorCode"] = errorCode, ["Reason"] = reason }.ToJsonString();

    private static bool IsJson(string? contentType) =>
        contentType is not null && contentType.Split(';')[0].Trim().Equals("application/json", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> ReadBody(HttpContext ctx)
    {
        using var reader = new StreamReader(ctx.Request.Body, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<byte[]> ReadBodyBytes(HttpContext ctx)
    {
        using var buffer = new MemoryStream();
        await ctx.Request.Body.CopyToAsync(buffer);
        return buffer.ToArray();
    }

    private static string IconType(string file) =>
        System.IO.Path.GetExtension(file).ToLowerInvariant() switch
        {
            ".png"  => "image/png",
            ".jpg"  => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".svg"  => "image/svg+xml",
            _       => "application/octet-stream"
        };

    private static async Task Write(
        HttpContext ctx, int status, string contentType, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        ctx.Response.StatusCode    = status;
        ctx.Response.ContentType   = contentType;
        ctx.Response.ContentLength = bytes.Length;
        await ctx.Response.Body.WriteAsync(bytes);
    }
}