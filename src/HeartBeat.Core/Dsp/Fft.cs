namespace HeartBeat.Core.Dsp;

public static class Fft
{
    /// <summary>In-place iterative radix-2 complex FFT. Length must be a power of two.</summary>
    public static void Transform(double[] re, double[] im)
    {
        int n = re.Length;
        if (n != im.Length || (n & (n - 1)) != 0) throw new ArgumentException("Length must be a power of two.");

        for (int i = 1, j = 0; i < n; i++)
        {
            int bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }

        for (int len = 2; len <= n; len <<= 1)
        {
            double ang = -2 * Math.PI / len;
            double wr = Math.Cos(ang), wi = Math.Sin(ang);
            for (int i = 0; i < n; i += len)
            {
                double cr = 1, ci = 0;
                for (int k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = a + len / 2;
                    double tr = re[b] * cr - im[b] * ci, ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr; im[b] = im[a] - ti;
                    re[a] += tr; im[a] += ti;
                    double ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }

    public static int NextPowerOfTwo(int n)
    {
        int p = 1;
        while (p < n) p <<= 1;
        return p;
    }

    /// <summary>
    /// Power spectrum |X|² of a real signal (mean removed, Hann window, zero-padded to <paramref name="nfft"/>).
    /// Returns nfft/2+1 bins; bin k is at k·fs/nfft Hz.
    /// </summary>
    public static double[] HannPowerSpectrum(ReadOnlySpan<double> x, int nfft)
    {
        var re = new double[nfft];
        var im = new double[nfft];
        double mean = 0;
        foreach (double v in x) mean += v;
        mean /= x.Length;
        for (int i = 0; i < x.Length; i++)
        {
            double w = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (x.Length - 1));
            re[i] = (x[i] - mean) * w;
        }
        Transform(re, im);
        var p = new double[nfft / 2 + 1];
        for (int k = 0; k < p.Length; k++) p[k] = re[k] * re[k] + im[k] * im[k];
        return p;
    }
}
