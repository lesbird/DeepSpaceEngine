namespace Engine.Audio;

/// <summary>
/// A short sound effect fully resident in one OpenAL buffer, ready to be triggered any number of
/// times (each play grabs a voice from the engine's source pool). Created via
/// <see cref="AudioEngine.CreateSound"/>; the engine owns the underlying buffer and frees it on
/// dispose, so callers just hold the handle.
/// </summary>
public sealed class Sound
{
    internal uint Buffer { get; }
    public double DurationSeconds { get; }

    internal Sound(uint buffer, double durationSeconds)
    {
        Buffer = buffer;
        DurationSeconds = durationSeconds;
    }
}
