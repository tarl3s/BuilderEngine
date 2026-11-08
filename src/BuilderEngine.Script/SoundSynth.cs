namespace BuilderEngine.Script;

/// <summary>
/// Tiny procedural sound effects (ported from EasyEngine's SoundSynth): 16-bit mono PCM WAV bytes at 22.05 kHz.
/// Fixed seeds, so the same call always makes the same file. Every clip is at least 150 ms with 5 ms fades.
/// New projects get these as their first sounds (see <see cref="Sounds.WriteDefaults"/>).
/// </summary>
public static class SoundSynth
{
    public const int Rate = 22050;

    /// <summary>The sounds every new project starts with: file name -> generator.</summary>
    public static readonly (string File, Func<byte[]> Make)[] Defaults =
    [
        ("coin.wav", Coin), ("jump.wav", Jump), ("hit.wav", Hit), ("powerup.wav", Powerup),
    ];

    /// <summary>A bright two-note "bling" (B5 then E6), like picking up a coin.</summary>
    public static byte[] Coin()
    {
        var s = Buffer(0.30);
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double f = t < 0.07 ? 987.77 : 1318.51;
            double local = t < 0.07 ? t : t - 0.07;
            double sq = Math.Sign(Math.Sin(2 * Math.PI * f * t)) * 0.5 + Math.Sin(2 * Math.PI * f * t) * 0.5;
            s[i] = sq * Math.Exp(-local / (t < 0.07 ? 0.2 : 0.09));
        }
        return Finish(s, 0.6);
    }

    /// <summary>A quick rising "boing": a square wave sliding up.</summary>
    public static byte[] Jump()
    {
        var s = Buffer(0.22);
        double phase = 0, lo = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            double f = 220 + 520 * (t / 0.22);
            phase += 2 * Math.PI * f / Rate;
            double sq = Math.Sign(Math.Sin(phase));
            lo += 0.3 * (sq - lo);                                   // soften the square
            s[i] = lo * Math.Exp(-t / 0.16);
        }
        return Finish(s, 0.55);
    }

    /// <summary>A short crunchy thump: noise plus a falling low tone.</summary>
    public static byte[] Hit()
    {
        var s = Buffer(0.20);
        var rng = new Random(41);
        double lo = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            lo += 0.35 * ((rng.NextDouble() * 2 - 1) - lo);
            double tone = Math.Sin(2 * Math.PI * (180 - 300 * t) * t);
            s[i] = (lo * 0.8 + tone) * Math.Exp(-t / 0.05);
        }
        return Finish(s, 0.8);
    }

    /// <summary>A cheerful rising arpeggio (C E G C).</summary>
    public static byte[] Powerup()
    {
        double[] notes = [523.25, 659.25, 783.99, 1046.50];
        const double step = 0.08;
        var s = Buffer(notes.Length * step + 0.12);
        for (int i = 0; i < s.Length; i++)
        {
            double t = (double)i / Rate;
            int n = Math.Min(notes.Length - 1, (int)(t / step));
            double local = t - n * step;
            double tri = 2 / Math.PI * Math.Asin(Math.Sin(2 * Math.PI * notes[n] * t));
            s[i] = tri * Math.Exp(-local / (n == notes.Length - 1 ? 0.12 : 0.06));
        }
        return Finish(s, 0.6);
    }

    // ---------------- helpers ----------------

    static double[] Buffer(double seconds) => new double[Math.Max((int)(Rate * Math.Max(0.15, seconds)), 1)];

    /// <summary>Normalize to the peak, add 5 ms fades, wrap as a WAV file.</summary>
    static byte[] Finish(double[] s, double peak)
    {
        double max = 1e-9;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        int fade = Rate * 5 / 1000;
        var pcm = new short[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double g = 1;
            if (i < fade) g = (double)i / fade;
            else if (i > s.Length - fade) g = (double)(s.Length - i) / fade;
            pcm[i] = (short)Math.Clamp(s[i] / max * peak * g * short.MaxValue, short.MinValue, short.MaxValue);
        }
        return Wav(pcm);
    }

    /// <summary>16-bit mono PCM samples as a WAV file.</summary>
    public static byte[] Wav(short[] pcm)
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        int dataBytes = pcm.Length * 2;
        w.Write("RIFF"u8); w.Write(36 + dataBytes); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); // PCM, mono
        w.Write(Rate); w.Write(Rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(dataBytes);
        foreach (var v in pcm) w.Write(v);
        w.Flush();
        return ms.ToArray();
    }
}
