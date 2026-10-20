using System;

namespace Mpai.Cae.Usc;

// A radix-2 fast Fourier transform on arrays of the real and the imaginary parts (the length a power of two).
public static class Fft
{
    public static void Transform(double[] re, double[] im, bool inverse)
    {
        var n = re.Length;
        if (n == 0 || (n & (n - 1)) != 0 || im.Length != n) throw new ArgumentException("The length of a transform is a power of two.");
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var angle = 2 * Math.PI / len * (inverse ? 1 : -1);
            double wr = Math.Cos(angle), wi = Math.Sin(angle);
            for (var i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    var tr = re[b] * cr - im[b] * ci; var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti; re[a] += tr; im[a] += ti;
                    var nr = cr * wr - ci * wi; ci = cr * wi + ci * wr; cr = nr;
                }
            }
        }
        if (inverse) for (var i = 0; i < n; i++) { re[i] /= n; im[i] /= n; }
    }

    public static int NextPowerOfTwo(int n) { var p = 1; while (p < n) p <<= 1; return p; }
}
