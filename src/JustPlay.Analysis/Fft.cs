namespace JustPlay.Analysis;

/// <summary>
/// Compact, allocation-light iterative radix-2 Cooley–Tukey FFT for power-of-two
/// frame sizes. Hand-rolled (no NuGet, no reflection) so the analysis pipeline
/// stays trim-safe / AOT-friendly per the repo conventions.
///
/// Operates in-place on split real/imaginary arrays. The chromagram detector only
/// needs the forward transform, so that's all this exposes.
/// </summary>
internal static class Fft
{
    /// <summary>True if <paramref name="n"/> is a positive power of two.</summary>
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Smallest power of two &gt;= <paramref name="n"/>.</summary>
    public static int NextPowerOfTwo(int n)
    {
        if (n < 1) return 1;
        var p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// In-place forward FFT. <paramref name="re"/> and <paramref name="im"/> must be the
    /// same length and that length must be a power of two. On return they hold the
    /// real and imaginary parts of the spectrum.
    /// </summary>
    public static void Forward(float[] re, float[] im)
    {
        var n = re.Length;
        if (n != im.Length)
            throw new ArgumentException("Real and imaginary buffers must match in length.");
        if (!IsPowerOfTwo(n))
            throw new ArgumentException("FFT length must be a power of two.", nameof(re));
        if (n == 1)
            return;

        // --- Bit-reversal permutation ---------------------------------------
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
                j ^= bit;
            j ^= bit;

            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }

        // --- Danielson–Lanczos butterflies ----------------------------------
        for (var len = 2; len <= n; len <<= 1)
        {
            // Negative angle => forward transform (e^{-2πi k / len}).
            var ang = -2.0 * Math.PI / len;
            var wReal = (float)Math.Cos(ang);
            var wImag = (float)Math.Sin(ang);

            for (var i = 0; i < n; i += len)
            {
                float curReal = 1f, curImag = 0f;
                var half = len >> 1;
                for (var k = 0; k < half; k++)
                {
                    var aIdx = i + k;
                    var bIdx = i + k + half;

                    var bRe = re[bIdx];
                    var bIm = im[bIdx];

                    // t = w^k * b
                    var tReal = curReal * bRe - curImag * bIm;
                    var tImag = curReal * bIm + curImag * bRe;

                    re[bIdx] = re[aIdx] - tReal;
                    im[bIdx] = im[aIdx] - tImag;
                    re[aIdx] += tReal;
                    im[aIdx] += tImag;

                    // advance twiddle: cur *= w
                    var nextReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                }
            }
        }
    }
}
