using System.Net.WebSockets;

namespace Mpai.Mmm.Server;

// A SHARED SCREEN, AS THE MMM CARRIES IT. The User with the Right to share places in the room a Visual Object that is a stream (MM-Add) and feeds it as it captures its screen: a WebSocket,
// /ws/v2.2/screen/{id}, whose binary messages are the frames and the sound: the first byte says which (1: a frame, JPEG; 2: sound, raw PCM, 24 kHz mono 16 bits). Whoever may see it - whoever has an Item in the room - opens /ws/v2.2/screen/{id}/watch and receives the frames. A slow
// watcher is not waited for: it is given the latest frame, not the ones in between; the sound it is given all, in order (it is what is heard). Nothing is kept: when the stream is over, the Visual Object is gone.
public sealed class ScreenStreams
{
    private sealed class Live
    {
        public byte[]? Latest;
        public long Sequence;
        public readonly List<byte[]> Sound = [];             // the PCM chunks, the last ones only
        public long SoundBase;                                // how many chunks were dropped from the front
        public bool Closed;
        public TaskCompletionSource Signal = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private Live Of(string id) { lock (live) return live.TryGetValue(id, out var l) ? l : live[id] = new Live(); }

    private void Push(string id, byte[] message)
    {
        var l = Of(id);
        lock (l)
        {
            if (message[0] == 2) { l.Sound.Add(message); if (l.Sound.Count > 400) { l.Sound.RemoveRange(0, 200); l.SoundBase += 200; } }
            else { l.Latest = message; l.Sequence++; }
            var s = l.Signal; l.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously); s.TrySetResult();
        }
    }

    private void Close(string id)
    {
        var l = Of(id);
        lock (l) { l.Closed = true; var s = l.Signal; l.Signal = new(TaskCreationOptions.RunContinuationsAsynchronously); s.TrySetResult(); }
    }

    // The feed: the sharer's WebSocket. It ends when the sharer closes it (or the connection is lost); the Visual Object is then gone.
    public async Task FeedAsync(WebSocket socket, string id, MInstance m, CancellationToken stop)
    {
        var buffer = new byte[256 * 1024];
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested && m.ScreenLive(id))
            {
                var got = new List<byte>();
                WebSocketReceiveResult r;
                do { r = await socket.ReceiveAsync(buffer, stop); got.AddRange(buffer.AsSpan(0, r.Count).ToArray()); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType == WebSocketMessageType.Binary && got.Count > 1 && got[0] is 1 or 2) Push(id, got.ToArray());
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            m.ScreenEnded(id); Close(id);
            try { if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { }
            lock (live) live.Remove(id);
        }
    }

    // A watcher: the latest frame at once, then each one that follows, and the sound from when it came, until the screen is over (or the meeting ends: the Visual Object is gone, or may not be read).
    public async Task WatchAsync(WebSocket socket, string id, Func<bool> may, MInstance m, CancellationToken stop)
    {
        var l = Of(id);
        long seen = 0, sound;
        lock (l) sound = l.SoundBase + l.Sound.Count;                      // what was said before it came is not replayed
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                byte[]? frame = null; var chunks = new List<byte[]>(); bool closed; Task wait;
                lock (l)
                {
                    if (l.Sequence != seen) { frame = l.Latest; seen = l.Sequence; }
                    if (sound < l.SoundBase) sound = l.SoundBase;
                    for (; sound < l.SoundBase + l.Sound.Count; sound++) chunks.Add(l.Sound[(int)(sound - l.SoundBase)]);
                    closed = l.Closed; wait = l.Signal.Task;
                }
                foreach (var c in chunks) await socket.SendAsync(c, WebSocketMessageType.Binary, true, stop);
                if (frame is not null) await socket.SendAsync(frame, WebSocketMessageType.Binary, true, stop);
                if (frame is not null || chunks.Count > 0) continue;
                if (closed || !m.ScreenLive(id) || !may()) break;
                await Task.WhenAny(wait, Task.Delay(1000, stop));
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { try { if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { } }
    }
}
