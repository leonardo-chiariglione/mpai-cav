using System.Net.WebSockets;
using System.Text;

namespace Mpai.Mmm.Server;

// AN ANIMATION STREAM, AS THE MMM CARRIES IT. A User that animates its Persona with data it derives as it goes - here the mouth, from the speech of its human (an Animation Qualifier,
// TFA-AMQ, whose transport format is AudioVisemeStream) - UM-Captures it, has it Identified as a stream Item and MM-Animates its Persona with it. The data then goes out as it is
// produced: a WebSocket, /ws/v2.2/animation/{id}, whose text messages are the frames. Whoever may see the Persona watches (/ws/v2.2/animation/{id}/watch) and is given the frames as they
// arrive, not the ones it missed; a late watcher is given the latest frame. Nothing is kept: when the stream is over it is gone and the Persona is no longer animated by it.
public sealed class AnimationStreams
{
    private sealed class Live
    {
        public readonly List<WebSocket> Watchers = [];
        public string? Last;
    }

    private readonly Dictionary<string, Live> live = new(StringComparer.Ordinal);
    private Live Of(string id) { lock (live) return live.TryGetValue(id, out var l) ? l : live[id] = new Live(); }

    public async Task FeedAsync(WebSocket socket, string id, MInstance m, CancellationToken stop)
    {
        var l = Of(id);
        var buffer = new byte[8192];
        try
        {
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(stop);
                idle.CancelAfter(SpeechStreams.IdleMs);
                var got = new List<byte>();
                WebSocketReceiveResult r;
                do { r = await socket.ReceiveAsync(buffer, idle.Token); got.AddRange(buffer.AsSpan(0, r.Count).ToArray()); } while (!r.EndOfMessage);
                if (r.MessageType == WebSocketMessageType.Close) break;
                if (r.MessageType != WebSocketMessageType.Text || got.Count == 0) continue;
                var frame = Encoding.UTF8.GetString(got.ToArray());
                List<WebSocket> to;
                lock (l) { l.Last = frame; to = l.Watchers.ToList(); }
                foreach (var w in to)
                {
                    try { if (w.State == WebSocketState.Open) await w.SendAsync(Encoding.UTF8.GetBytes(frame), WebSocketMessageType.Text, true, stop); }
                    catch (WebSocketException) { lock (l) l.Watchers.Remove(w); }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally
        {
            m.AnimationEnded(id);
            List<WebSocket> to; lock (l) { to = l.Watchers.ToList(); l.Watchers.Clear(); }
            foreach (var w in to) { try { if (w.State == WebSocketState.Open) await w.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { } }      // not waited for: a watcher that does not answer does not hold the others
            lock (live) live.Remove(id);
            try { if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived) await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "over", CancellationToken.None); } catch { }
        }
    }

    public async Task WatchAsync(WebSocket socket, string id, CancellationToken stop)
    {
        var l = Of(id);
        string? last; lock (l) last = l.Last;
        try
        {
            if (last is not null) await socket.SendAsync(Encoding.UTF8.GetBytes(last), WebSocketMessageType.Text, true, stop);
            lock (l) l.Watchers.Add(socket);
            var buffer = new byte[256];
            while (socket.State == WebSocketState.Open && !stop.IsCancellationRequested)       // a watcher only listens; it ends when it closes
            {
                var r = await socket.ReceiveAsync(buffer, stop);
                if (r.MessageType == WebSocketMessageType.Close) break;
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        finally { lock (l) l.Watchers.Remove(socket); }
    }
}
