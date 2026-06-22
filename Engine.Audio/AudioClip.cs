using Silk.NET.OpenAL;

namespace Engine.Audio;

/// <summary>
/// Raw decoded PCM audio held in managed memory: the interleaved sample bytes plus the
/// format needed to upload them to OpenAL. This is the engine-neutral payload — both the
/// WAV loader and the procedural <see cref="Synth"/> produce one of these, and both the
/// one-shot SFX path and the streaming music path consume it.
/// </summary>
public sealed class AudioClip
{
    public byte[] Pcm { get; }
    public int SampleRate { get; }
    public int Channels { get; }       // 1 = mono, 2 = stereo
    public int BitsPerSample { get; }  // 8 or 16

    public AudioClip(byte[] pcm, int sampleRate, int channels, int bitsPerSample)
    {
        Pcm = pcm;
        SampleRate = sampleRate;
        Channels = channels;
        BitsPerSample = bitsPerSample;
    }

    public int BytesPerFrame => Channels * (BitsPerSample / 8);
    public double DurationSeconds => SampleRate > 0 ? (double)Pcm.Length / (SampleRate * BytesPerFrame) : 0.0;

    /// <summary>The OpenAL buffer format matching this clip's channel count and bit depth.</summary>
    public BufferFormat Format => (Channels, BitsPerSample) switch
    {
        (1, 8)  => BufferFormat.Mono8,
        (1, 16) => BufferFormat.Mono16,
        (2, 8)  => BufferFormat.Stereo8,
        (2, 16) => BufferFormat.Stereo16,
        _ => throw new NotSupportedException(
            $"Unsupported PCM layout: {Channels}ch / {BitsPerSample}-bit (need mono/stereo, 8/16-bit).")
    };
}
