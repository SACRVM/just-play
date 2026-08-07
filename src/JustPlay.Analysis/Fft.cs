namespace JustPlay.Analysis;

/// <summary>
/// Compact, allocation-light iterative radix-2 Cooley-Tukey FFT for power-of-two
/// frame sizes. Hand-rolled (no NuGet, no reflection) so the analysis pipeline
/// stays trim-safe / AOT-friendly per the repo conventions.
///
/// Operates in-place on split real/imaginary arrays. The chromagram detector only
/// needs the forward transform, so that's all this exposes.
/// </summary>
public static class Fft
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
    /// In-place inverse FFT. Applies the forward transform with positive angles (conjugate),
    /// then divides by N. <paramref name="re"/> and <paramref name="im"/> hold complex
    /// spectrum on entry; on return they hold the real-valued reconstructed samples in
    /// <paramref name="re"/> (the imaginary part is near-zero for a real signal).
    /// Used by the HPSS drum suppressor (N19 experiment - not on the shipped path).
    /// </summary>
    public static void Inverse(float[] re, float[] im)
    {
        var n = re.Length;
        if (n != im.Length)
            throw new ArgumentException("Real and imaginary buffers must match in length.");
        if (!IsPowerOfTwo(n))
            throw new ArgumentException("FFT length must be a power of two.", nameof(re));
        if (n == 1) return;

        // Conjugate, forward-transform, conjugate again, scale by 1/N.
        for (var i = 0; i < n; i++) im[i] = -im[i];

        // Bit-reversal permutation (same as Forward).
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        // Danielson-Lanczos butterflies (positive angle = inverse direction).
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2.0 * Math.PI / len;   // same as forward after the conjugation
            var wReal = (float)Math.Cos(ang);
            var wImag = (float)Math.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float curReal = 1f, curImag = 0f;
                var half = len >> 1;
                for (var k = 0; k < half; k++)
                {
                    var aIdx = i + k; var bIdx = i + k + half;
                    var bRe = re[bIdx]; var bIm = im[bIdx];
                    var tReal = curReal * bRe - curImag * bIm;
                    var tImag = curReal * bIm + curImag * bRe;
                    re[bIdx] = re[aIdx] - tReal; im[bIdx] = im[aIdx] - tImag;
                    re[aIdx] += tReal; im[aIdx] += tImag;
                    var nextReal = curReal * wReal - curImag * wImag;
                    curImag = curReal * wImag + curImag * wReal;
                    curReal = nextReal;
                }
            }
        }

        // Conjugate output and scale by 1/N.
        var invN = 1.0f / n;
        for (var i = 0; i < n; i++) { re[i] *= invN; im[i] = -im[i] * invN; }
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

        // --- Danielson-Lanczos butterflies ----------------------------------
        for (var len = 2; len <= n; len <<= 1)
        {
            // Negative angle => forward transform (e^{-2pii k / len}).
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
