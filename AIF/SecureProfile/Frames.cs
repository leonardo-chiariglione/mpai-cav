using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AIF.SecureProfile;

// THE FRAMES between the User Agent, the Non-Secure Controller and the Secure Controller. A frame is a JSON object; a request has one reply.
//   Hello    the User Agent to the Secure Controller: Nonce (32 bytes), UaKey (its ephemeral P-256 key, SubjectPublicKeyInfo), UaId (its identity key,
//            SubjectPublicKeyInfo) and UaSig (its signature, ECDSA P-256 SHA-256, of Nonce || UaKey): the Secure Controller serves only the User Agents it
//            was given, and the Non-Secure Controller, which has no such key, cannot open a session of its own
//   Welcome  the reply: Session, ScKey (the Secure Controller's ephemeral key), Token (its Entity Attestation Token over the challenge), Party
//   Sealed   either way, once the session is made: Session, Seq, Data (AES-256-GCM of the request or the reply, nonce = direction and Seq, associated data the
//            Session and the direction) - the Non-Secure Controller relays it and cannot read it
//   Refused  the reply to what the Secure Controller does not accept: Reason
// The challenge of the token is SHA-256(Nonce || UaKey || ScKey): the token binds the key the Secure Controller agrees to the attestation the User Agent checks.
public interface IFrameLink
{
    byte[] Send(byte[] frame);
}

public static class Frames
{
    public const string Info = "MPAI AIF V3.0 User Agent - Secure Controller";
    public const byte ToSecure = 0, ToUserAgent = 1;

    public static byte[] Bytes(JsonObject frame) => Encoding.UTF8.GetBytes(frame.ToJsonString());

    public static JsonObject? Parse(byte[] frame)
    {
        try { return System.Text.Json.Nodes.JsonNode.Parse(frame) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static JsonObject Refused(string reason) => new() { ["Type"] = "Refused", ["Reason"] = reason };

    public static byte[] Signed(byte[] nonce, byte[] uaKey) => [.. nonce, .. uaKey];

    public static byte[] Challenge(byte[] nonce, byte[] uaKey, byte[] scKey) => System.Security.Cryptography.SHA256.HashData([.. nonce, .. uaKey, .. scKey]);

    public static byte[] Nonce(byte direction, long seq)
    {
        var n = new byte[12];
        n[0] = direction;
        BinaryPrimitives.WriteInt64BigEndian(n.AsSpan(4), seq);
        return n;
    }

    public static byte[] Associated(string session, byte direction) => [.. Encoding.UTF8.GetBytes(session), direction];
}

// A FRAME LINK OVER LOOPBACK TCP: four bytes of length (big endian), then the frame; one reply per frame. The Secure Controller in its own process serves
// itself with FrameServer; the Non-Secure Controller reaches it with TcpFrameLink. Several requests may be in flight at once - a read that waits for an Output
// does not hold the others back: each takes a connection of its own (kept for the next request when it is done).
public sealed class TcpFrameLink : IFrameLink, IDisposable
{
    private readonly int port;
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> idle = [];
    private readonly System.Collections.Concurrent.ConcurrentBag<TcpClient> all = [];

    public TcpFrameLink(int port)
    {
        this.port = port;
        idle.Add(Connect());                                                       // the Secure Controller is reached now, or the link fails now
    }

    private TcpClient Connect()
    {
        var client = new TcpClient();
        client.Connect(IPAddress.Loopback, port);
        all.Add(client);
        return client;
    }

    public byte[] Send(byte[] frame)
    {
        var client = idle.TryTake(out var free) ? free : Connect();
        var stream = client.GetStream();
        FrameServer.Write(stream, frame);
        var reply = FrameServer.Read(stream);
        if (reply is null) { client.Dispose(); throw new IOException("the Secure Controller closed the link"); }
        idle.Add(client);
        return reply;
    }

    public void Dispose() { foreach (var c in all) c.Dispose(); }
}

// WHICH SEQUENCE NUMBERS A RECEIVER HAS SEEN: requests in flight at once may arrive out of order; each number is accepted once, and none further back than a
// window (Freshness of Zero Trust and Profiles 2.6: a reader rejects a sequence it has seen).
public sealed class SeenSequences
{
    private const long Window = 4096;
    private readonly HashSet<long> seen = [];
    private long highest = -1;

    public bool Accept(long seq)
    {
        lock (seen)
        {
            if (seq < 0 || seq <= highest - Window || !seen.Add(seq)) return false;
            if (seq > highest)
            {
                highest = seq;
                seen.RemoveWhere(s => s <= highest - Window);
            }
            return true;
        }
    }
}

public sealed class FrameServer : IDisposable
{
    private readonly TcpListener listener;
    private readonly IFrameLink target;
    private readonly CancellationTokenSource stop = new();

    public int Port { get; }

    public FrameServer(IFrameLink target, int port = 0)
    {
        this.target = target;
        listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(Accept);
    }

    private async Task Accept()
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop.Token); }
            catch (Exception) { return; }
            _ = Task.Run(() =>
            {
                using (client)
                using (var s = client.GetStream())
                    while (Read(s) is { } frame) Write(s, target.Send(frame));
            });
        }
    }

    internal static void Write(Stream s, byte[] frame)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, frame.Length);
        s.Write(length);
        s.Write(frame);
        s.Flush();
    }

    internal static byte[]? Read(Stream s)
    {
        var length = new byte[4];
        if (!Fill(s, length)) return null;
        var n = BinaryPrimitives.ReadInt32BigEndian(length);
        if (n is < 0 or > 64 * 1024 * 1024) return null;
        var frame = new byte[n];
        return Fill(s, frame) ? frame : null;
    }

    private static bool Fill(Stream s, byte[] buffer)
    {
        var at = 0;
        while (at < buffer.Length)
        {
            int got;
            try { got = s.Read(buffer, at, buffer.Length - at); }
            catch (IOException) { return false; }
            if (got == 0) return false;
            at += got;
        }
        return true;
    }

    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
