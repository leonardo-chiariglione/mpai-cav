using System;
using System.Threading;
using System.Threading.Tasks;

using Mpai.Core;

namespace Mpai.UaKit;

// THE SPEECH OF THE PERSON, FROM THE MICROPHONE. A device for a Speech Object (OSD-BSO): the capture returns the Object that Speech
// Object Acquisition built, Qualifier and all, and ends when the person stops speaking. A capture cannot be interrupted, so
// Stop - or the typed text that came first - is answered at once and the capture is left to end by itself; the next listening takes
// it up if it is still running, so two captures never hold the microphone together. What a capture brings after nobody was
// waiting for it is dropped.
public sealed class SpeechListener
{
    private readonly AvatarUaHost _avatar;
    private Task<BasicSpeechObject?>? _pending;

    public SpeechListener(AvatarUaHost avatar) { _avatar = avatar; }

    // The speech heard; null when nothing was heard, or when stopped or abandoned.
    public async Task<BasicSpeechObject?> ListenAsync(CancellationToken stop, CancellationToken abandon)
    {
        Task<BasicSpeechObject?> capture;
        lock (this)
        {
            if (_pending is null || _pending.IsCompleted) _pending = Task.Run(() => _avatar.CaptureSpeech());
            capture = _pending;
        }
        using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, abandon);
        var gone = Task.Delay(Timeout.Infinite, either.Token);
        var first = await Task.WhenAny(capture, gone);
        either.Cancel();
        if (first != capture) return null;
        lock (this) if (ReferenceEquals(_pending, capture)) _pending = null;
        var speech = await capture;
        return speech is null || speech.Data.Length == 0 ? null : speech;
    }
}
