using System.Numerics;
using Silk.NET.OpenAL;

namespace Engine.Audio;

/// <summary>
/// The game's audio subsystem: owns the OpenAL device/context, a pool of voices for one-shot sound
/// effects, and a single streaming music track. Three volume "buses" — master, sfx, music — let the
/// UI mix them independently (master is the listener gain, so it scales everything live, including
/// already-playing sounds).
///
/// Robustness: if no audio device can be opened (headless CI, no output) the whole engine quietly
/// becomes a no-op — every method returns harmlessly and the game runs silent rather than crashing.
/// Check <see cref="Available"/> if you need to know.
///
/// Threading: not thread-safe. Create it, and call every method, from the game/render thread.
/// </summary>
public sealed unsafe class AudioEngine : IDisposable
{
    private const int SfxVoiceCount = 24; // simultaneous one-shot sounds before the oldest is stolen

    private readonly ALContext _alc = null!;
    private readonly AL _al = null!;
    private readonly Device* _device;
    private readonly Context* _context;
    private readonly bool _ready;

    private readonly uint[] _sfxVoices = Array.Empty<uint>();
    private readonly List<uint> _ownedBuffers = new();
    private int _voiceCursor; // round-robin starting point when stealing a busy voice

    private MusicStream? _music;

    private float _master = 1f;
    private float _sfxVolume = 1f;
    private float _musicVolume = 0.6f;

    /// <summary>True when an output device opened successfully. When false every call is a no-op.</summary>
    public bool Available => _ready;

    public AudioEngine()
    {
        try
        {
            _alc = ALContext.GetApi();
            _al = AL.GetApi();
            _device = _alc.OpenDevice("");
            if (_device == null)
            {
                Console.WriteLine("[audio] no output device — running silent.");
                return;
            }

            _context = _alc.CreateContext(_device, null);
            if (_context == null || !_alc.MakeContextCurrent(_context))
            {
                Console.WriteLine("[audio] could not create/activate context — running silent.");
                return;
            }
            _al.GetError(); // clear any benign init error

            _sfxVoices = new uint[SfxVoiceCount];
            for (int i = 0; i < SfxVoiceCount; i++) _sfxVoices[i] = _al.GenSource();

            _al.SetListenerProperty(ListenerFloat.Gain, _master);
            _ready = true;
            Console.WriteLine("[audio] OpenAL ready.");
        }
        catch (Exception e)
        {
            // Missing native, denied device, etc. — degrade to silence, never take the game down.
            Console.WriteLine($"[audio] init failed ({e.Message}) — running silent.");
            _ready = false;
        }
    }

    /// <summary>Overall output level [0,1]. Applied as the OpenAL listener gain, so it scales every
    /// sound and the music live.</summary>
    public float MasterVolume
    {
        get => _master;
        set
        {
            _master = Math.Clamp(value, 0f, 1f);
            if (_ready) _al.SetListenerProperty(ListenerFloat.Gain, _master);
        }
    }

    /// <summary>Level for one-shot sound effects [0,1]; multiplied into each play.</summary>
    public float SfxVolume { get => _sfxVolume; set => _sfxVolume = Math.Clamp(value, 0f, 1f); }

    /// <summary>Level for the music track [0,1]; applied to the live stream each <see cref="Update"/>.</summary>
    public float MusicVolume { get => _musicVolume; set => _musicVolume = Math.Clamp(value, 0f, 1f); }

    /// <summary>Upload a decoded clip into a reusable OpenAL buffer for low-latency one-shot playback.</summary>
    public Sound CreateSound(AudioClip clip)
    {
        if (!_ready) return new Sound(0, clip.DurationSeconds);
        uint buffer = _al.GenBuffer();
        _al.BufferData(buffer, clip.Format, clip.Pcm, clip.SampleRate);
        _ownedBuffers.Add(buffer);
        return new Sound(buffer, clip.DurationSeconds);
    }

    /// <summary>Load a WAV from disk and upload it as a one-shot sound.</summary>
    public Sound LoadSound(string path) => CreateSound(WavLoader.Load(path));

    /// <summary>Play a sound effect head-relative (always centred, full level) — the right call for
    /// UI clicks and non-diegetic cues. <paramref name="volume"/> and <paramref name="pitch"/> are
    /// per-play multipliers (pitch 1 = normal, 2 = an octave up).</summary>
    public void PlaySound(Sound sound, float volume = 1f, float pitch = 1f)
        => Play(sound, volume, pitch, relative: true, position: Vector3.Zero);

    /// <summary>Play a sound effect positioned in space, attenuating with distance from the listener.
    /// <paramref name="cameraRelative"/> is the source position relative to the camera, in metres
    /// (i.e. world position minus camera position) — keeping it float-sized despite the universe's
    /// double-precision absolute coordinates.</summary>
    public void PlaySoundAt(Sound sound, Vector3 cameraRelative, float volume = 1f, float pitch = 1f)
        => Play(sound, volume, pitch, relative: false, position: cameraRelative);

    private void Play(Sound sound, float volume, float pitch, bool relative, Vector3 position)
    {
        if (!_ready || sound.Buffer == 0) return;
        uint voice = AcquireVoice();

        _al.SetSourceProperty(voice, SourceInteger.Buffer, (int)sound.Buffer);
        _al.SetSourceProperty(voice, SourceBoolean.Looping, false);
        _al.SetSourceProperty(voice, SourceBoolean.SourceRelative, relative);
        _al.SetSourceProperty(voice, SourceVector3.Position, position);
        _al.SetSourceProperty(voice, SourceFloat.Gain, _sfxVolume * Math.Clamp(volume, 0f, 4f));
        _al.SetSourceProperty(voice, SourceFloat.Pitch, Math.Clamp(pitch, 0.25f, 4f));
        _al.SourcePlay(voice);
    }

    /// <summary>Find a free voice; if all are busy, steal the next one round-robin so a fresh sound
    /// always plays (briefly cutting the oldest rather than dropping the new one).</summary>
    private uint AcquireVoice()
    {
        for (int i = 0; i < _sfxVoices.Length; i++)
        {
            uint v = _sfxVoices[i];
            _al.GetSourceProperty(v, GetSourceInteger.SourceState, out int state);
            if (state != (int)SourceState.Playing) return v;
        }
        uint stolen = _sfxVoices[_voiceCursor];
        _voiceCursor = (_voiceCursor + 1) % _sfxVoices.Length;
        _al.SourceStop(stolen);
        return stolen;
    }

    /// <summary>Start (or replace) the music track. Loops by default; pass a clip from
    /// <see cref="WavLoader"/> or <see cref="Synth"/>.</summary>
    public void PlayMusic(AudioClip clip, bool loop = true)
    {
        StopMusic();
        if (!_ready) return;
        _music = new MusicStream(_al, clip, loop);
        _music.SetGain(_musicVolume);
        _music.Start();
    }

    public void StopMusic()
    {
        _music?.Dispose();
        _music = null;
    }

    public bool MusicPlaying => _music is { Finished: false };

    /// <summary>Pump the music stream and apply live volume. Call once per frame.</summary>
    public void Update(double dt)
    {
        if (!_ready || _music == null) return;
        _music.SetGain(_musicVolume);
        _music.Pump();
        if (_music.Finished) StopMusic();
    }

    /// <summary>Place and orient the listener so positional sounds pan/attenuate correctly. The
    /// listener sits at the origin of the camera-relative frame (position is normally
    /// <see cref="Vector3.Zero"/>); <paramref name="forward"/> and <paramref name="up"/> come straight
    /// from the camera basis.</summary>
    public void SetListener(Vector3 position, Vector3 forward, Vector3 up)
    {
        if (!_ready) return;
        _al.SetListenerProperty(ListenerVector3.Position, position);
        // OpenAL orientation is six floats: the "at" vector followed by the "up" vector.
        float* orientation = stackalloc float[6]
            { forward.X, forward.Y, forward.Z, up.X, up.Y, up.Z };
        _al.SetListenerProperty(ListenerFloatArray.Orientation, orientation);
    }

    public void Dispose()
    {
        StopMusic();
        if (_ready)
        {
            if (_sfxVoices.Length > 0) _al.DeleteSources(_sfxVoices);
            if (_ownedBuffers.Count > 0) _al.DeleteBuffers(_ownedBuffers.ToArray());
        }
        if (_context != null)
        {
            _alc.MakeContextCurrent(null);
            _alc.DestroyContext(_context);
        }
        if (_device != null) _alc.CloseDevice(_device);
    }
}
