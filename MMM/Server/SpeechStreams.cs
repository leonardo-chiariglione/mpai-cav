using System.Net.WebSockets;

namespace Mpai.Mmm.Server;

// THE SPEECH OBJECTS THAT ARE STREAMS (MMM Phase 8). A User's client declares a Speech Object when its speech starts - with the time it lasts, and the address its data
// is delivered from - and feeds the M-Instance as it captures (a WebSocket, /ws/v2.2/speech/{id}: the client's side). Whoever may hear the Speech Object reads it from
// /api/v2.2/speech/{id}/stream as an HTTP stream of continuous audio - the Icecast of the Speech Qualifier's stream formats - from its start, and as it goes on. When the
// stream is over the data is the Speech Object's (read as an Item), and the Secretary, a Process like the others, takes it for the minutes.
public sealed class SpeechStreams
{
    private sealed class Live
    {
        public readonly List<byte[]> Chunks = [];
        public bool Closed;
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);

    private Live Of(string id) { lock (live) return live.TryGetValue(id, out var l) ? l : live[id] = new Live(); }

    public void Append(string id, byte[] chunk)
    {
        var l = Of(id);
        lock (l) { l.Chunks.Add(chunk); var s = l.Signal; l.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously); s.TrySetResult(); }
    }

    // Closes the stream; what arrived, joined.
    public byte[] Close(string id)
    {
        var l = Of(id);
        lock (l)
        {
            l.Closed = true; var s = l.Signal; l.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously); s.TrySetResult();
            return l.Chunks.SelectMany(c => c).ToArray();
        }
    }

    // What has arrived, from the start, and what arrives next, until the stream is closed.
    public async IAsyncEnumerable<byte[]> ReadAsync(string id, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken stop)
    {
        var l = Of(id);
        for (var next = 0; ;)
        {
            Task wait; byte[]? chunk = null; bool closed;
            lock (l) { if (next < l.Chunks.Count) chunk = l.Chunks[next++]; closed = l.Closed; wait = l.Signal.Task; }
            if (chunk is not null) { yield return chunk; continue; }
            if (closed) yield break;
            await wait.WaitAsync(stop);
        }
    }

    // The feed of a Speech Object: the client's WebSocket. Binary messages are PCM, as captured; the stream is over when the client closes, or when the time the Speech
    // Object was declared for is over (a little after: the last frames are still on their way).
    public async Task FeedAsync(WebSocket socket, string id, long declaredEnd, MInstance m, CancellationToken stop)
    {
        var buffer = new byte[16384];
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                var wait = Math.Max(0, declaredEnd + 2000 - DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                timeout.CancelAfter((int)Math.Min(wait, int.MaxValue));
                var got = new List<byte>();
                WebSocketReceiveResult r;
                do { r = await socket.ReceiveAsync(buffer, timeout.Token); got.AddRange(buffer.AsSpan(0, r.Count).ToArray()); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary && got.Count > 0) Append(id, got.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            m.StreamComplete(id, Close(id));
            try { if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { }
        }
    }
}
