using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Channels;

// HOW A LINK IS ADMITTED: what the Controller's end sends first, and how it
// judges the answer; what the host's end answers, and whether it admits. Each end
// sees the TLS certificate of the other.
public interface ILinkAdmission
{
    // This end's TLS certificate; null: the host makes one for the run, the
    // Controller presents none.
    X509Certificate2? Certificate { get; }

    // The Controller's end: its first frame, and null where the host's answer admits
    // it and is trusted - otherwise why not.
    JsonObject Opening(X509Certificate? host);
    string? Admitted(JsonObject answer, X509Certificate? host);

    // The host's end: its answer to the first frame, and whether it admits.
    (JsonObject Answer, bool Admits) Answer(JsonObject opening, X509Certificate? controller);
}

// ADMITTED BY A KEY SHARED IN CONFIGURATION (M3217 3.3): where no Trust Anchor is
// configured. The host presents a certificate made for the run, which gives the
// link its confidentiality; the Controller is admitted by the key the host was
// started with, sent first. Identity is the Trust Protocol's (M3223 3.4).
public sealed class KeyAdmission(string key) : ILinkAdmission
{
    public X509Certificate2? Certificate => null;
    public JsonObject Opening(X509Certificate? host) => new() { ["Kind"] = "Hello", ["Key"] = key };
    public string? Admitted(JsonObject answer, X509Certificate? host) =>
        answer["Ok"]?.GetValue<bool>() == true ? null : "it did not admit this Controller";
    public (JsonObject Answer, bool Admits) Answer(JsonObject opening, X509Certificate? controller)
    {
        var ok = opening["Kind"]?.GetValue<string>() == "Hello" && opening["Key"]?.GetValue<string>() == key;
        return (new JsonObject { ["Ok"] = ok }, ok);
    }
}

// ONE LINK BETWEEN A CONTROLLER AND AN AIM HOST (M3217 3.2, 3.3): TLS over TCP,
// frames of length-prefixed UTF-8 JSON. A frame is a request - it carries an id
// and waits for the frame that replies to it - or a notice, which waits for
// nothing. The Channels between the two and the control path share the link. It
// opens with the first frame of its admission, and is used only once admitted.
public sealed class RemoteLink : IAsyncDisposable
{
    private readonly Stream stream;
    private readonly SemaphoreSlim writing = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonObject>> waiting = new();
    private readonly CancellationTokenSource closed = new();
    private long ids;

    // Answers a request (null: no reply); receives a notice.
    public Func<JsonObject, Task<JsonObject?>>? OnRequest { get; set; }
    public Func<JsonObject, Task>? OnNotice { get; set; }

    // The link has gone: the reason.
    public event Action<string>? Lost;
    public bool IsClosed => closed.IsCancellationRequested;

    private RemoteLink(Stream stream) => this.stream = stream;

    // ---- opening -------------------------------------------------------------

    public static Task<RemoteLink> ConnectAsync(string host, int port, string key, CancellationToken cancel = default) =>
        ConnectAsync(host, port, new KeyAdmission(key), cancel);

    public static async Task<RemoteLink> ConnectAsync(string host, int port, ILinkAdmission admission, CancellationToken cancel = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancel);
        // The host's certificate is judged by the admission, not by a chain.
        var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = host,
            ClientCertificates = admission.Certificate is { } own ? new X509CertificateCollection { own } : null
        }, cancel);
        var link = new RemoteLink(tls);
        link.Start();
        JsonObject answer;
        try { answer = await link.RequestAsync(admission.Opening(tls.RemoteCertificate), cancel); }
        catch (IOException) { answer = new JsonObject { ["Ok"] = false }; }       // closed on us: not admitted
        if (admission.Admitted(answer, tls.RemoteCertificate) is { } refused)
        {
            await link.DisposeAsync();
            throw new UnauthorizedAccessException($"The host at {host}:{port}: {refused}.");
        }
        return link;
    }

    // A host listens; each Controller its admission admits is a link.
    public sealed class Listener : IAsyncDisposable
    {
        private readonly TcpListener tcp;
        private readonly X509Certificate2 certificate;
        private readonly CancellationTokenSource stop = new();
        public int Port => ((IPEndPoint)tcp.LocalEndpoint).Port;

        // A connection that failed before it was a link - its TLS, most often - and why.
        public event Action<string>? Failed;

        public Listener(int port, string key, Action<RemoteLink> admitted) : this(port, new KeyAdmission(key), admitted) { }

        public Listener(int port, ILinkAdmission admission, Action<RemoteLink> admitted)
        {
            certificate = admission.Certificate ?? MadeForTheRun();
            // Both IPv6 and IPv4: a Controller that reaches "localhost" by ::1 first
            // is not kept waiting for its fallback to 127.0.0.1.
            tcp = new TcpListener(IPAddress.IPv6Any, port);
            tcp.Server.DualMode = true;
            tcp.Start();
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try { client = await tcp.AcceptTcpClientAsync(stop.Token); }
                    catch { return; }
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            client.NoDelay = true;
                            var tls = new SslStream(client.GetStream(), false);
                            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                            {
                                ServerCertificate = certificate,
                                // Where the admission has a certificate, the Controller
                                // presents one too; the admission judges it.
                                ClientCertificateRequired = admission.Certificate is not null,
                                RemoteCertificateValidationCallback = (_, _, _, _) => true
                            });
                            var link = new RemoteLink(tls);
                            var admittedOnce = new TaskCompletionSource<bool>();
                            link.OnRequest = frame =>
                            {
                                var (answer, admits) = admission.Answer(frame, tls.RemoteCertificate);
                                admittedOnce.TrySetResult(admits);
                                return Task.FromResult<JsonObject?>(answer);
                            };
                            link.Start();
                            if (await admittedOnce.Task) admitted(link);
                            else
                            {
                                await Task.Delay(500);                      // the refusal sent before the link is closed
                                await link.DisposeAsync();
                            }
                        }
                        catch (Exception failure)
                        {
                            Failed?.Invoke(failure.Message + (failure.InnerException is { } inner ? " - " + inner.Message : ""));
                            client.Dispose();
                        }
                    });
                }
            });
        }

        public ValueTask DisposeAsync()
        {
            stop.Cancel();
            tcp.Stop();
            return ValueTask.CompletedTask;
        }

        private static X509Certificate2 MadeForTheRun()
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=MPAI AIM host", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
            return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
        }
    }

    // A certificate of this end for the run, to be named by a signed message: its
    // key made for it, held only in this process.
    public static X509Certificate2 CertificateForTheRun(string name)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", ecdsa, HashAlgorithmName.SHA256);
        using var made = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(made.Export(X509ContentType.Pfx), null);
    }

    // ---- frames ----------------------------------------------------------------

    public async Task<JsonObject> RequestAsync(JsonObject frame, CancellationToken cancel = default)
    {
        var id = Interlocked.Increment(ref ids);
        var reply = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        waiting[id] = reply;
        frame["Id"] = id;
        try
        {
            await SendAsync(frame, cancel);
            using var both = CancellationTokenSource.CreateLinkedTokenSource(cancel, closed.Token);
            return await reply.Task.WaitAsync(both.Token);
        }
        catch (OperationCanceledException) when (closed.IsCancellationRequested)
        {
            throw new IOException("The link was lost.");
        }
        finally { waiting.TryRemove(id, out _); }
    }

    public Task NoticeAsync(JsonObject frame, CancellationToken cancel = default) => SendAsync(frame, cancel);

    private async Task SendAsync(JsonObject frame, CancellationToken cancel)
    {
        var body = Encoding.UTF8.GetBytes(frame.ToJsonString());
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, body.Length);
        await writing.WaitAsync(cancel);
        try
        {
            await stream.WriteAsync(length, cancel);
            await stream.WriteAsync(body, cancel);
            await stream.FlushAsync(cancel);
        }
        catch (Exception failure) when (failure is IOException or ObjectDisposedException)
        {
            Close(failure.Message);
            throw new IOException("The link was lost: " + failure.Message, failure);
        }
        finally { writing.Release(); }
    }

    private void Start() => _ = Task.Run(ReceiveAsync);

    private async Task ReceiveAsync()
    {
        var length = new byte[4];
        try
        {
            while (!closed.IsCancellationRequested)
            {
                await stream.ReadExactlyAsync(length, closed.Token);
                var body = new byte[System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(length)];
                await stream.ReadExactlyAsync(body, closed.Token);
                if (JsonNode.Parse(body) is not JsonObject frame) continue;

                if (frame["ReplyTo"] is { } replyTo)
                {
                    if (waiting.TryRemove(replyTo.GetValue<long>(), out var reply)) reply.TrySetResult(frame);
                    continue;
                }
                _ = Task.Run(async () =>
                {
                    if (frame["Id"] is { } id)
                    {
                        JsonObject answer;
                        try { answer = (OnRequest is null ? null : await OnRequest(frame)) ?? new JsonObject(); }
                        catch (Exception failure) { answer = new JsonObject { ["Error"] = failure.Message }; }
                        answer["ReplyTo"] = id.GetValue<long>();
                        try { await SendAsync(answer, CancellationToken.None); } catch { }
                    }
                    else if (OnNotice is not null) await OnNotice(frame);
                });
            }
        }
        catch (Exception failure)
        {
            Close(failure is EndOfStreamException ? "the other end closed the link" : failure.Message);
        }
    }

    private void Close(string reason)
    {
        if (closed.IsCancellationRequested) return;
        closed.Cancel();
        foreach (var (_, reply) in waiting) reply.TrySetCanceled();
        Lost?.Invoke(reason);
    }

    public ValueTask DisposeAsync()
    {
        Close("closed");
        stream.Dispose();
        return ValueTask.CompletedTask;
    }
}
