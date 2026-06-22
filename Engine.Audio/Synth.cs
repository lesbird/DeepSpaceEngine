namespace Engine.Audio;

/// <summary>
/// Tiny offline tone generator. The engine ships no audio assets, so these synthesise a couple of
/// 16-bit mono clips procedurally — enough to prove the pipeline (and give the HUD a click and the
/// scene an ambient bed) without binary files in the repo. Drop real WAVs in and these become the
/// fallback. All maths is plain doubles baked to a byte[] once at load; nothing runs per-frame.
/// </summary>
public static class Synth
{
    private const int Rate = 44100;

    /// <summary>A short, soft UI "blip": a sine with a fast exponential decay so it reads as a click,
    /// not a beep. Good for menu/map toggles and confirmations.</summary>
    public static AudioClip Blip(double freq = 660.0, double seconds = 0.10)
    {
        int n = (int)(Rate * seconds);
        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double env = Math.Exp(-t * 32.0);                 // quick percussive decay
            samples[i] = Math.Sin(2 * Math.PI * freq * t) * env * 0.6;
        }
        return Mono16(samples);
    }

    /// <summary>A seamlessly-loopable ambient pad: a few detuned low sine partials with a slow
    /// tremolo. Built over an exact number of whole cycles per partial so the end meets the start with
    /// no click when looped. Quiet by design — it sits under everything as a space drone.</summary>
    public static AudioClip AmbientPad(double seconds = 8.0)
    {
        int n = (int)(Rate * seconds);
        double baseHz = 55.0; // A1 — deep
        // Snap each partial to a whole number of cycles across the buffer for a glitch-free loop.
        double[] partials =
        {
            Round(baseHz, n), Round(baseHz * 1.5, n), Round(baseHz * 2.01, n), Round(baseHz * 3.0, n),
        };
        double[] gains = { 0.5, 0.28, 0.18, 0.10 };
        double tremHz = Round(0.18, n); // slow swell, also loop-aligned

        var samples = new double[n];
        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double s = 0;
            for (int p = 0; p < partials.Length; p++)
                s += Math.Sin(2 * Math.PI * partials[p] * t) * gains[p];
            double trem = 0.75 + 0.25 * Math.Sin(2 * Math.PI * tremHz * t);
            samples[i] = s * trem * 0.22;
        }
        return Mono16(samples);
    }

    /// <summary>Nearest frequency whose period divides the buffer length evenly (whole cycles), so a
    /// looped buffer has no discontinuity at the wrap point.</summary>
    private static double Round(double hz, int n)
    {
        double cycles = Math.Max(1, Math.Round(hz * n / Rate));
        return cycles * Rate / n;
    }

    private static AudioClip Mono16(double[] samples)
    {
        var pcm = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            double v = Math.Clamp(samples[i], -1.0, 1.0);
            short s = (short)(v * short.MaxValue);
            pcm[i * 2] = (byte)(s & 0xFF);
            pcm[i * 2 + 1] = (byte)((s >> 8) & 0xFF);
        }
        return new AudioClip(pcm, Rate, channels: 1, bitsPerSample: 16);
    }
}
