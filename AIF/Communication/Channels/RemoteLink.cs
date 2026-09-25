using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.Channels;

// ONE LINK BETWEEN A CONTROLLER AND AN AIM HOST (M3217 3.2, 3.3): TLS over TCP,
// frames of length-prefixed UTF-8 JSON. A frame is a request - it carries an id
// and waits for the frame that replies to it - or a notice, which waits for
// nothing. The Channels between the two and the control path share the link.
//
// Until Zero Trust (Phase 13) gives identities: the host presents a certificate
// made for the run, which gives the link its confidentiality; the Controller is
// admitted by the key the host was started with, sent first.
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

    public static async Task<RemoteLink> ConnectAsync(string host, int port, string key, CancellationToken cancel = default)
    {
        var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync(host, port, cancel);
        var tls = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);   // confidentiality; identity is Phase 13
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, cancel);
        var link = new RemoteLink(tls);
        link.Start();
        JsonObject hello;
        try { hello = await link.RequestAsync(new JsonObject { ["Kind"] = "Hello", ["Key"] = key }, cancel); }
        catch (IOException) { hello = new JsonObject { ["Ok"] = false }; }         // closed on us: not admitted
        if (hello["Ok"]?.GetValue<bool>() != true)
        {
            await link.DisposeAsync();
            throw new UnauthorizedAccessException($"The host at {host}:{port} did not admit this Controller.");
        }
        return link;
    }

    // A host listens; each Controller that connects with the right key is a link.
    public sealed class Listener : IAsyncDisposable
    {
        private readonly TcpListener tcp;
        private readonly X509Certificate2 certificate = MadeForTheRun();
        private readonly CancellationTokenSource stop = new();
        public int Port => ((IPEndPoint)tcp.LocalEndpoint).Port;

        public Listener(int port, string key, Action<RemoteLink> admitted)
        {
            tcp = new TcpListener(IPAddress.Any, port);
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
                            await tls.AuthenticateAsServerAsync(certificate);
                            var link = new RemoteLink(tls);
                            var admittedOnce = new TaskCompletionSource<bool>();
                            link.OnRequest = frame =>
                            {
                                var ok = frame["Kind"]?.GetValue<string>() == "Hello" && frame["Key"]?.GetValue<string>() == key;
                                admittedOnce.TrySetResult(ok);
                                return Task.FromResult<JsonObject?>(new JsonObject { ["Ok"] = ok });
                            };
                            link.Start();
                            if (await admittedOnce.Task) admitted(link);
                            else
                            {
                                await Task.Delay(500);                      // the refusal sent before the link is closed
                                await link.DisposeAsync();
                            }
                        }
                        catch { client.Dispose(); }
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
