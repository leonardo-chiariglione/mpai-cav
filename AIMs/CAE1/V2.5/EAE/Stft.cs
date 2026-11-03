using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace Mpai.Cae.Usc.Eae;

// The transform of Audio Analysis Transform and Audio Synthesis Transform: a short-time Fourier transform of blocks of BlockSize samples (a power of two) with 50 % overlap, the
// analysis and the synthesis window a sine window - their squares sum to one at that overlap, so synthesis returns what analysis took (to the rounding of the samples). The block k
// covers the samples from (k - 1) x BlockSize / 2: the first block starts half a block before the audio, and the last ends half a block after it.
public static class Stft
{
    public static TransformBlocks Analyse(MultiAudio audio, int blockSize)
    {
        if (blockSize < 4 || (blockSize & (blockSize - 1)) != 0) throw new FormatException($"The block size of the transform, {blockSize}, is not a power of two.");
        var hop = blockSize / 2; var length = audio.Length; var count = (length + hop - 1) / hop + 1;
        var window = Window(blockSize); var blocks = new List<Complex[][]>(count);
        for (var k = 0; k < count; k++)
        {
            var start = (k - 1) * hop; var block = new Complex[audio.Channels.Length][];
            for (var c = 0; c < block.Length; c++)
            {
                double[] re = new double[blockSize], im = new double[blockSize];
                for (var i = 0; i < blockSize; i++) { var at = start + i; if (at >= 0 && at < length) re[i] = audio.Channels[c][at] * window[i]; }
                Fft.Transform(re, im, false);
                block[c] = Enumerable.Range(0, blockSize / 2 + 1).Select(b => new Complex(re[b], im[b])).ToArray();
            }
            blocks.Add(block);
        }
        return new TransformBlocks(audio.Rate, blockSize, length, blocks);
    }

    // The time-domain signal of each channel of the blocks: samples as doubles, Samples long.
    public static double[][] Synthesise(TransformBlocks t)
    {
        var hop = t.BlockSize / 2; var window = Window(t.BlockSize); var channels = t.Channels;
        var result = Enumerable.Range(0, channels).Select(_ => new double[t.Samples]).ToArray();
        for (var k = 0; k < t.Blocks.Count; k++)
        {
            var start = (k - 1) * hop;
            for (var c = 0; c < channels; c++)
            {
                double[] re = new double[t.BlockSize], im = new double[t.BlockSize];
                var spectrum = t.Blocks[k][c];
                for (var b = 0; b <= t.BlockSize / 2; b++) { re[b] = spectrum[b].Real; im[b] = spectrum[b].Imaginary; if (b > 0 && b < t.BlockSize / 2) { re[t.BlockSize - b] = spectrum[b].Real; im[t.BlockSize - b] = -spectrum[b].Imaginary; } }
                Fft.Transform(re, im, true);
                for (var i = 0; i < t.BlockSize; i++) { var at = start + i; if (at >= 0 && at < t.Samples) result[c][at] += re[i] * window[i]; }
            }
        }
        return result;
    }

    public static short[] ToSamples(double[] x) => x.Select(v => (short)Math.Clamp(Math.Round(v), short.MinValue, short.MaxValue)).ToArray();

    private static double[] Window(int n) => Enumerable.Range(0, n).Select(i => Math.Sin(Math.PI * (i + 0.5) / n)).ToArray();
}
