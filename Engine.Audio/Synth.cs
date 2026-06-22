using Engine.Core;

namespace Engine.Audio;

/// <summary>
/// Tiny offline tone generator. The engine ships no audio assets, so these synthesise clips
/// procedurally — a UI click and a full <see cref="CasualSpace"/> ambient music track — without
/// binary files in the repo. Drop real WAVs in and these become the fallback. Everything is baked to
/// a byte[] once at load; nothing runs per-frame. Generation is deterministic from a seed (reusing
/// Engine.Core's <see cref="DeterministicRng"/>), matching the rest of the engine.
/// </summary>
public static class Synth
{
    private const int Rate = 44100;

    // Equal-tempered note frequencies, in semitones above C0 (≈16.35 Hz). C4 = +48.
    private static double Note(int semitonesAboveC0) => 16.351597831287414 * Math.Pow(2.0, semitonesAboveC0 / 12.0);
    private const int C3 = 36, C4 = 48; // octave anchors used below

    // C major pentatonic (C D E G A) over two octaves from C4 — every pick lands consonant, so a
    // random walk over these always sounds "right". Used by the melody.
    private static readonly int[] PentatonicC = { 0, 2, 4, 7, 9, 12, 14, 16, 19, 21, 24 };

    // A gentle diatonic progression of 7th/6th chords in C major: Cmaj7 · Am7 · Fmaj7 · G6 · Em7 · Dm7.
    // Each is a set of semitone offsets above C3 — warm, unhurried, faintly wistful (the "casual space"
    // mood). Voiced low so they sit under the melody.
    private static readonly int[][] Progression =
    {
        new[] { 0, 4, 7, 11 },   // Cmaj7  C E G B
        new[] { 9, 12, 16, 19 }, // Am7    A C E G
        new[] { 5, 9, 12, 16 },  // Fmaj7  F A C E
        new[] { 7, 11, 14, 16 }, // G6     G B D E
        new[] { 4, 7, 11, 14 },  // Em7    E G B D
        new[] { 2, 5, 9, 12 },   // Dm7    D F A C
    };

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

    /// <summary>
    /// A calm, seamlessly-looping ambient music track — "casual space". Three layers, all stereo:
    ///   1. a soft continuous drone (root+fifth+octave, snapped to whole cycles so it crosses the loop
    ///      seam without a click) that gives the bed its body;
    ///   2. slow 7th-chord pads from <see cref="Progression"/> that swell in and fade back to silence
    ///      within each slot — so the chord level is zero exactly at the loop seam;
    ///   3. a sparse bell melody wandering a major-pentatonic scale, each note panned with two quieter
    ///      stereo echo taps for width and space.
    /// Seamlessness: the drone is phase-continuous, while pads and melody are arranged to be silent at
    /// the seam (chords fade to zero at slot edges; no melody note or echo is scheduled close enough to
    /// the end to ring across it). Deterministic from <paramref name="seed"/>.
    /// </summary>
    public static AudioClip CasualSpace(double seconds = 48.0, ulong seed = 0xCA55EEDUL)
    {
        int n = (int)(Rate * seconds);
        var left = new double[n];
        var right = new double[n];
        var rng = new DeterministicRng(seed);

        AddDrone(left, right, n);
        AddPads(left, right, n);
        AddMelody(left, right, n, seconds, ref rng);

        NormalizeStereo(left, right, peak: 0.82);
        return Stereo16(left, right);
    }

    /// <summary>Layer 1: a quiet, continuous low drone. Partials are snapped to whole cycles over the
    /// buffer so it loops without a discontinuity, with a slow (also loop-aligned) tremolo breath.</summary>
    private static void AddDrone(double[] left, double[] right, int n)
    {
        double root = Note(C3);                                  // C3 — warm, not a sub-bass hum
        double[] partials = { Round(root, n), Round(root * 1.5, n), Round(root * 2.0, n) };
        double[] gains = { 0.10, 0.06, 0.04 };
        double tremHz = Round(0.07, n);                          // ~14 s breath

        for (int i = 0; i < n; i++)
        {
            double t = (double)i / Rate;
            double s = 0;
            for (int p = 0; p < partials.Length; p++)
                s += Math.Sin(2 * Math.PI * partials[p] * t) * gains[p];
            double breath = 0.8 + 0.2 * Math.Sin(2 * Math.PI * tremHz * t);
            s *= breath;
            left[i] += s;
            right[i] += s;
        }
    }

    /// <summary>Layer 2: the chord progression as slow pads. Each chord owns one equal slot and swells
    /// up then back to zero (a raised-sine envelope), so successive chords cross-breathe and the level
    /// is zero at every slot edge — including the buffer ends, keeping the loop seam clean.</summary>
    private static void AddPads(double[] left, double[] right, int n)
    {
        int chords = Progression.Length;
        int slot = n / chords;                                   // samples per chord
        double slotSec = (double)slot / Rate;

        for (int c = 0; c < chords; c++)
        {
            int start = c * slot;
            int end = Math.Min(n, start + slot);
            int[] chord = Progression[c];
            double pan = (c % 2 == 0) ? -0.25 : 0.25;            // alternate side to side
            double lg = PanL(pan), rg = PanR(pan);

            for (int i = start; i < end; i++)
            {
                double lt = (double)(i - start) / Rate;          // time within the slot
                double env = Math.Sin(Math.PI * lt / slotSec);   // 0 → 1 → 0 across the slot
                env *= env;                                      // softer attack/release
                double t = (double)i / Rate;
                double s = 0;
                foreach (int semi in chord)
                {
                    double f = Note(C3 + semi);
                    s += Math.Sin(2 * Math.PI * f * t) * 0.5
                       + Math.Sin(2 * Math.PI * f * 2 * t) * 0.12; // a touch of 2nd harmonic for air
                }
                s *= env * 0.05;
                left[i] += s * lg;
                right[i] += s * rg;
            }
        }
    }

    /// <summary>Layer 3: a sparse, bell-like melody. Walks a major-pentatonic scale on a slow grid with
    /// plenty of rests; each note gets two decaying echo taps panned across the field. Notes are only
    /// scheduled where the whole note + its echoes finish before the buffer ends, so nothing rings
    /// across the loop seam.</summary>
    private static void AddMelody(double[] left, double[] right, int n, double seconds, ref DeterministicRng rng)
    {
        const double step = 0.6;       // grid spacing (s) between possible note onsets
        const double echo1 = 0.30, echo2 = 0.60; // echo tap delays
        int idx = PentatonicC.Length / 2;         // start mid-range, then wander

        for (double t = 2.0; t < seconds; t += step)
        {
            if (rng.NextDouble() > 0.5) continue;  // ~half the slots rest — keep it spacious

            // Wander up/down the scale by a small step, occasionally leaping.
            int move = rng.RangeInt(-2, 3) + (rng.NextDouble() < 0.15 ? rng.RangeInt(-3, 4) : 0);
            idx = Math.Clamp(idx + move, 0, PentatonicC.Length - 1);

            double dur = rng.Range(1.4, 2.6);
            // Skip if the note (plus its longest echo tail) would spill past the end → seam-safe.
            if (t + echo2 + dur + 0.1 >= seconds) continue;

            double freq = Note(C4 + PentatonicC[idx]);
            double gain = rng.Range(0.16, 0.26);
            double pan = rng.Range(-0.6, 0.6);

            RenderBell(left, right, t, dur, freq, gain, pan);
            RenderBell(left, right, t + echo1, dur * 0.9, freq, gain * 0.5, -pan * 0.8);
            RenderBell(left, right, t + echo2, dur * 0.8, freq, gain * 0.28, pan * 0.6);
        }
    }

    /// <summary>Render one bell tone (a few decaying harmonics, soft attack, exponential decay forced
    /// to exactly zero at the end) additively into the stereo buffers at the given time and pan.</summary>
    private static void RenderBell(double[] left, double[] right, double startSec, double durSec,
        double freq, double gain, double pan)
    {
        int start = (int)(startSec * Rate);
        int len = (int)(durSec * Rate);
        if (start < 0 || start + len > left.Length) len = Math.Min(len, left.Length - start);
        if (start < 0 || len <= 0) return;

        // Inharmonic-ish partials give a soft bell/Rhodes timbre rather than a pure beep.
        double[] mul = { 1.0, 2.0, 3.0, 4.2 };
        double[] amp = { 1.0, 0.5, 0.25, 0.12 };
        double lg = PanL(pan) * gain, rg = PanR(pan) * gain;
        double attack = 0.006; // 6 ms, click-free onset

        for (int i = 0; i < len; i++)
        {
            double t = (double)i / Rate;
            double a = t < attack ? t / attack : 1.0;            // fast raised onset
            double decay = Math.Exp(-3.2 * t / durSec);          // body decay
            double tail = 1.0 - (double)i / len;                 // linear → forces exactly 0 at the end
            double env = a * decay * tail;
            double s = 0;
            for (int h = 0; h < mul.Length; h++)
                s += Math.Sin(2 * Math.PI * freq * mul[h] * t) * amp[h];
            s *= env;
            left[start + i] += s * lg;
            right[start + i] += s * rg;
        }
    }

    // Constant-power stereo pan, pan ∈ [-1,1]: -1 hard left, +1 hard right.
    private static double PanL(double pan) => Math.Cos((pan + 1) * 0.25 * Math.PI);
    private static double PanR(double pan) => Math.Sin((pan + 1) * 0.25 * Math.PI);

    /// <summary>Scale the mix so its loudest sample hits <paramref name="peak"/> (leaving headroom),
    /// only attenuating — never boosting a quiet mix up into noise.</summary>
    private static void NormalizeStereo(double[] left, double[] right, double peak)
    {
        double max = 1e-9;
        for (int i = 0; i < left.Length; i++)
            max = Math.Max(max, Math.Max(Math.Abs(left[i]), Math.Abs(right[i])));
        if (max <= peak) return;
        double k = peak / max;
        for (int i = 0; i < left.Length; i++) { left[i] *= k; right[i] *= k; }
    }

    private static AudioClip Stereo16(double[] left, double[] right)
    {
        int n = left.Length;
        var pcm = new byte[n * 4]; // 2 channels × 16-bit, interleaved L,R
        for (int i = 0; i < n; i++)
        {
            short l = (short)(Math.Clamp(left[i], -1.0, 1.0) * short.MaxValue);
            short r = (short)(Math.Clamp(right[i], -1.0, 1.0) * short.MaxValue);
            int o = i * 4;
            pcm[o] = (byte)(l & 0xFF); pcm[o + 1] = (byte)((l >> 8) & 0xFF);
            pcm[o + 2] = (byte)(r & 0xFF); pcm[o + 3] = (byte)((r >> 8) & 0xFF);
        }
        return new AudioClip(pcm, Rate, channels: 2, bitsPerSample: 16);
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
