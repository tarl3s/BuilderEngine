namespace BuilderEngine.Script;

/// <summary>
/// The Night Shift sample's sounds, made from code (like <see cref="SoundSynth"/>): ambient loops (a drone, the office
/// hum, a buzzing light, a ticking soldier, a music box) and one-shots (doors, static, knocks, a scream, the 6 AM chime).
/// Loops fade into their own start so they repeat without a click.
/// </summary>
internal static class NightShiftSounds
{
    const int Rate = SoundSynth.Rate;

    public static void WriteAll(string soundsDir)
    {
        Directory.CreateDirectory(soundsDir);
        var all = new (string, Func<byte[]>)[]
        {
            ("drone.wav", Drone), ("hum.wav", Hum), ("buzz.wav", Buzz), ("tick.wav", Tick), ("musicbox.wav", MusicBox),
            ("static.wav", Static), ("door.wav", Door), ("jam.wav", Jam), ("click.wav", Click), ("tablet.wav", Tablet),
            ("steps.wav", Steps), ("knock.wav", Knock), ("sting.wav", Sting), ("scream.wav", Scream), ("chime.wav", Chime),
            ("boom.wav", Boom), ("powerdown.wav", PowerDown), ("flicker.wav", Flicker), ("music_stop.wav", MusicStop),
        };
        foreach (var (file, make) in all) File.WriteAllBytes(Path.Combine(soundsDir, file), make());
    }

    // ---------------- Helpers ----------------

    static double[] Buf(double seconds) => new double[(int)(Rate * seconds)];
    static double T(int i) => (double)i / Rate;

    /// <summary>Scales to the peak, fades the ends (or, for a loop, blends the end into the start) and makes the WAV.</summary>
    static byte[] Finish(double[] s, double peak, bool loop = false)
    {
        if (loop)
        {
            int x = Math.Min(s.Length / 4, Rate / 5);
            for (int i = 0; i < x; i++)
            {
                double k = (double)i / x;
                s[i] = s[i] * k + s[s.Length - x + i] * (1 - k);
            }
            Array.Resize(ref s, s.Length - x);
        }
        double max = 1e-9;
        foreach (var v in s) max = Math.Max(max, Math.Abs(v));
        int fade = loop ? 0 : Math.Min(s.Length / 2, Rate / 200);
        var pcm = new short[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            double g = 1;
            if (i < fade) g = (double)i / fade;
            else if (i >= s.Length - fade) g = (double)(s.Length - 1 - i) / fade;
            pcm[i] = (short)Math.Clamp(s[i] / max * peak * g * 32767, -32767, 32767);
        }
        return SoundSynth.Wav(pcm);
    }

    sealed class Noise(int seed)
    {
        readonly Random _r = new(seed);
        double _lp;
        public double White() => _r.NextDouble() * 2 - 1;
        /// <summary>Low-passed noise (k small = darker).</summary>
        public double Low(double k) => _lp += (White() - _lp) * k;
    }

    static double Sin(double f, double t) => Math.Sin(2 * Math.PI * f * t);

    // ---------------- Loops ----------------

    static byte[] Drone()
    {
        var s = Buf(6.2);
        var n = new Noise(1);
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            double lfo = 0.6 + 0.4 * Sin(1 / 6.0, t);
            s[i] = (Sin(55, t) + 0.8 * Sin(55.5, t) + 0.35 * Sin(82.5, t) + 0.2 * Sin(110.33, t)) * 0.5 * lfo + n.Low(0.02) * 1.6;
        }
        return Finish(s, 0.5, loop: true);
    }

    static byte[] Hum()
    {
        var s = Buf(3.2);
        var n = new Noise(2);
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            s[i] = Sin(60, t) * 0.6 + Sin(120, t) * 0.3 + Sin(180, t) * 0.12 + n.Low(0.08) * 0.5 * (0.8 + 0.2 * Sin(7, t));
        }
        return Finish(s, 0.35, loop: true);
    }

    static byte[] Buzz()
    {
        var s = Buf(1.4);
        var n = new Noise(3);
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            double saw = 2 * (t * 120 % 1) - 1;
            double crackle = n.White() > 0.995 ? n.White() * 3 : 0;
            s[i] = saw * 0.4 + Sin(240, t) * 0.2 + crackle;
        }
        return Finish(s, 0.3, loop: true);
    }

    static byte[] Tick()
    {
        var s = Buf(1.2);
        var n = new Noise(4);
        foreach (var (at, f) in new[] { (0.05, 3200.0), (0.55, 2400.0) })
            for (int i = (int)(at * Rate); i < (int)((at + 0.05) * Rate); i++)
            {
                double t = T(i) - at;
                s[i] += (Sin(f, t) * 0.6 + n.White() * 0.4) * Math.Exp(-t / 0.008);
            }
        return Finish(s, 0.6, loop: true);
    }

    static readonly double[] Melody = [76, 72, 69, 71, 72, 74, 71, 67, 69, 72, 76, 75, 76, 81, 79, 76];

    static double Note(double midi) => 440 * Math.Pow(2, (midi - 69) / 12);

    static void Pluck(double[] s, double at, double f, double gain, double decay = 0.6)
    {
        int start = (int)(at * Rate);
        for (int i = start; i < s.Length && i < start + (int)(decay * 4 * Rate); i++)
        {
            double t = T(i - start);
            s[i] += (Sin(f, t) + 0.4 * Sin(f * 2.01, t) + 0.15 * Sin(f * 3.98, t)) * Math.Exp(-t / decay) * gain;
        }
    }

    static byte[] MusicBox()
    {
        const double step = 0.42;
        var s = Buf(Melody.Length * step + 0.25);
        for (int k = 0; k < Melody.Length; k++) Pluck(s, k * step, Note(Melody[k]), 1, 0.45);
        return Finish(s, 0.5, loop: true);
    }

    // ---------------- One-shots ----------------

    static byte[] Static()
    {
        var s = Buf(0.4);
        var n = new Noise(5);
        for (int i = 0; i < s.Length; i++) s[i] = (n.White() - n.Low(0.3) * 0.6) * (0.6 + 0.4 * Sin(50, T(i)));
        return Finish(s, 0.35);
    }

    static byte[] Door()
    {
        var s = Buf(0.6);
        var n = new Noise(6);
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            s[i] = n.Low(0.2) * Math.Exp(-t / 0.05) * 1.4 + Sin(55 - t * 30, t) * Math.Exp(-t / 0.12) + Sin(183, t) * 0.4 * Math.Exp(-t / 0.25) + Sin(291, t) * 0.25 * Math.Exp(-t / 0.18);
        }
        return Finish(s, 0.85);
    }

    static byte[] Jam()
    {
        var s = Buf(0.25);
        var n = new Noise(7);
        for (int i = 0; i < s.Length; i++) { double t = T(i); s[i] = (n.Low(0.1) + Sin(90, t) * 0.5) * Math.Exp(-t / 0.05); }
        return Finish(s, 0.6);
    }

    static byte[] Click()
    {
        var s = Buf(0.15);
        var n = new Noise(8);
        for (int i = 0; i < s.Length; i++) { double t = T(i); s[i] = (n.White() * 0.5 + Sin(1800, t)) * Math.Exp(-t / 0.006); }
        return Finish(s, 0.5);
    }

    static byte[] Tablet()
    {
        var s = Buf(0.35);
        var n = new Noise(9);
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            s[i] = n.Low(0.05 + t) * Math.Sin(Math.PI * t / 0.35) * 0.8 + (t > 0.2 ? Sin(1400, t) * 0.3 * Math.Exp(-(t - 0.2) / 0.04) : 0);
        }
        return Finish(s, 0.5);
    }

    static byte[] Steps()
    {
        var s = Buf(1.3);
        var n = new Noise(10);
        foreach (var at in new[] { 0.05, 0.45, 0.85 })
            for (int i = (int)(at * Rate); i < (int)((at + 0.3) * Rate); i++)
            {
                double t = T(i) - at;
                s[i] += (n.Low(0.06) * 2 + Sin(70, t)) * Math.Exp(-t / 0.05) + Sin(420, t) * 0.08 * Math.Exp(-t / 0.15); // a thud and a creak
            }
        return Finish(s, 0.6);
    }

    static byte[] Knock()
    {
        var s = Buf(1.0);
        var n = new Noise(11);
        foreach (var at in new[] { 0.0, 0.28, 0.56 })
            for (int i = (int)(at * Rate); i < (int)((at + 0.4) * Rate); i++)
            {
                double t = T(i) - at;
                s[i] += n.Low(0.25) * Math.Exp(-t / 0.03) * 2 + (Sin(140, t) + Sin(233, t) * 0.6 + Sin(377, t) * 0.4) * Math.Exp(-t / 0.15);
            }
        return Finish(s, 0.9);
    }

    static byte[] Sting()
    {
        var s = Buf(1.4);
        double[] f = [233.1, 246.9, 349.2, 370.0, 466.2];
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i), env = Math.Min(1, t / 0.02) * Math.Exp(-t / 0.6), trem = 0.7 + 0.3 * Sin(9, t), slide = 1 - t * 0.03;
            double v = 0;
            foreach (var x in f) v += 2 * (t * x * slide % 1) - 1;
            s[i] = v * env * trem;
        }
        return Finish(s, 0.55);
    }

    static byte[] Scream()
    {
        var s = Buf(1.7);
        var n = new Noise(12);
        double ph = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            double f = 1400 - 900 * t + 120 * Sin(23, t);
            ph += 2 * Math.PI * f / Rate;
            double v = Math.Sin(ph + 3 * Math.Sin(ph * 1.5)) + n.White() * 0.7 + Math.Sin(ph * 0.5) * 0.5;
            v = Math.Tanh(v * 2.5); // distorted
            s[i] = v * Math.Min(1, t / 0.01) * (t > 1.2 ? Math.Exp(-(t - 1.2) / 0.15) : 1);
        }
        return Finish(s, 0.95);
    }

    static byte[] Chime()
    {
        var s = Buf(4.5);
        foreach (var (at, f) in new[] { (0.0, 523.25), (0.7, 659.25), (1.4, 783.99), (2.2, 1046.5) })
            for (int i = (int)(at * Rate); i < s.Length; i++)
            {
                double t = T(i) - at, e = Math.Exp(-t / 1.1);
                s[i] += (Sin(f, t) + 0.5 * Sin(f * 2.76, t) * Math.Exp(-t / 0.3) + 0.3 * Sin(f * 5.4, t) * Math.Exp(-t / 0.15)) * e;
            }
        return Finish(s, 0.6);
    }

    static byte[] Boom()
    {
        var s = Buf(2.5);
        var n = new Noise(13);
        for (int i = 0; i < s.Length; i++) { double t = T(i); s[i] = Sin(42 - t * 8, t) * Math.Exp(-t / 0.8) + n.Low(0.01) * 3 * Math.Exp(-t / 0.4); }
        return Finish(s, 0.85);
    }

    static byte[] PowerDown()
    {
        var s = Buf(2.2);
        double ph = 0;
        for (int i = 0; i < s.Length; i++)
        {
            double t = T(i);
            ph += 2 * Math.PI * (240 * Math.Exp(-t * 1.6) + 20) / Rate;
            s[i] = (Math.Sin(ph) + 0.4 * Math.Sin(ph * 2)) * Math.Exp(-t / 1.2) + (t > 1.6 && t < 1.7 ? Math.Sin(t * 600) : 0);
        }
        return Finish(s, 0.7);
    }

    static byte[] Flicker()
    {
        var s = Buf(0.16);
        var n = new Noise(14);
        for (int i = 0; i < s.Length; i++) s[i] = n.White() * (n.White() > 0.6 ? 1 : 0.15);
        return Finish(s, 0.3);
    }

    static byte[] MusicStop()
    {
        var s = Buf(2.4);
        double at = 0, step = 0.35, pitch = 1;
        for (int k = 0; k < 6 && at < 2; k++)
        {
            Pluck(s, at, Note(Melody[k]) * pitch, 1, 0.5);
            at += step;
            step *= 1.35;
            pitch *= 0.97;
        }
        return Finish(s, 0.5);
    }
}
