using System;
using System.Collections.Generic;
using System.Linq;

using NAudio.Wave;

namespace Mpai.PhysicalLayer;

// THE MICROPHONE THAT STAYS OPEN, keeping the last seconds it heard (the author's solution of the browser client, rca.js: "the microphone
// runs from Start on, keeping the last second heard; a person who answers the moment the avatar stops speaking speaks before listening has
// begun, and with that second kept the first word is not lost"). The Desktop client opened the microphone only when a step asked for speech,
// so the first second - "yes" - fell before the device was open, and the recogniser was given the silence that followed.
//
// 16 kHz, 16 bit, mono, in chunks of about 50 ms; the chunks of the last Keep are held, each with its level (the RMS of its samples, 0..1).
public static class RingMicrophone
{
    public sealed record Chunk(DateTime At, byte[] Pcm, double Level);

    private static readonly object Lock = new();
    private static readonly LinkedList<Chunk> Ring = new();
    private static WaveInEvent? _in;
    private static int _rate = 16000;

    public static TimeSpan Keep { get; set; } = TimeSpan.FromSeconds(4);

    // WHAT THE SPEAKERS SAY IS NOT THE PERSON. The avatar's voice reaches the microphone from the loudspeakers; the client knows when her speech
    // ends (its duration is in the speech it plays) and says so here: what the microphone heard before is not taken for an answer.
    public static DateTime IgnoreBefore { get; set; } = DateTime.MinValue;

    // Raised with each chunk as it arrives (on the device's thread).
    public static event Action<Chunk>? Heard;

    public static bool IsOpen => _in is not null;

    // Opens the microphone, once; it stays open.
    public static void EnsureOpen(int sampleRate = 16000)
    {
        lock (Lock)
        {
            if (_in is not null) return;
            _rate = sampleRate;
            var waveIn = new WaveInEvent { WaveFormat = new WaveFormat(sampleRate, 16, 1), BufferMilliseconds = 50 };
            waveIn.DataAvailable += (_, a) =>
            {
                if (a.BytesRecorded < 2) return;
                var pcm = new byte[a.BytesRecorded];
                Buffer.BlockCopy(a.Buffer, 0, pcm, 0, a.BytesRecorded);
                var chunk = new Chunk(DateTime.UtcNow, pcm, Rms(pcm));
                lock (Lock)
                {
                    Ring.AddLast(chunk);
                    while (Ring.Count > 1 && chunk.At - Ring.First!.Value.At > Keep) Ring.RemoveFirst();
                }
                Heard?.Invoke(chunk);
            };
            waveIn.StartRecording();
            _in = waveIn;
        }
    }

    // The chunks of the last span, oldest first.
    public static List<Chunk> Last(TimeSpan span)
    {
        var from = DateTime.UtcNow - span;
        lock (Lock) return Ring.Where(c => c.At >= from).ToList();
    }

    private static double Rms(byte[] pcm)
    {
        long sum = 0; int n = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            short s = (short)(pcm[i] | (pcm[i + 1] << 8));
            sum += (long)s * s; n++;
        }
        return n == 0 ? 0 : Math.Sqrt((double)sum / n) / 32768.0;
    }
}
