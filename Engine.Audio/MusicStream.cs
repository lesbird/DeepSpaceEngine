using System.Numerics;
using Silk.NET.OpenAL;

namespace Engine.Audio;

/// <summary>
/// Streams one music track through a small ring of OpenAL buffers fed from the clip's in-memory PCM.
/// Streaming (rather than one giant static buffer) keeps the upload small, makes looping seamless —
/// the read position simply wraps mid-chunk — and lets <see cref="Pump"/> recover from a buffer
/// underrun by restarting the (still-queued) source. <see cref="Pump"/> must be called regularly
/// (once per frame from <see cref="AudioEngine.Update"/>); everything else is set-and-forget.
/// </summary>
internal sealed class MusicStream : IDisposable
{
    private const int BufferCount = 4; // ~1.2s of audio buffered ahead at the chunk size below

    private readonly AL _al;
    private readonly AudioClip _clip;
    private readonly bool _loop;
    private readonly uint _source;
    private readonly uint[] _buffers;
    private readonly int _chunkBytes;
    private int _readPos; // byte offset into _clip.Pcm

    public bool Finished { get; private set; }

    public MusicStream(AL al, AudioClip clip, bool loop)
    {
        _al = al;
        _clip = clip;
        _loop = loop;

        _source = _al.GenSource();
        // Music is non-positional: pin it to the listener so it plays at full level regardless of
        // where the camera looks or moves.
        _al.SetSourceProperty(_source, SourceBoolean.SourceRelative, true);
        _al.SetSourceProperty(_source, SourceVector3.Position, Vector3.Zero);

        _buffers = new uint[BufferCount];
        for (int i = 0; i < BufferCount; i++) _buffers[i] = _al.GenBuffer();

        // ~0.3s chunks, snapped down to a whole audio frame so we never split a sample.
        int bpf = clip.BytesPerFrame;
        int target = clip.SampleRate * bpf * 3 / 10;
        _chunkBytes = Math.Max(bpf, target / bpf * bpf);
    }

    public void SetGain(float gain) => _al.SetSourceProperty(_source, SourceFloat.Gain, gain);

    public void Start()
    {
        foreach (uint buffer in _buffers)
        {
            if (Fill(buffer) == 0) break; // ran out of data (very short, non-looping clip)
            _al.SourceQueueBuffers(_source, new[] { buffer });
        }
        _al.SourcePlay(_source);
    }

    public void Pump()
    {
        _al.GetSourceProperty(_source, GetSourceInteger.BuffersProcessed, out int processed);
        var one = new uint[1];
        while (processed-- > 0)
        {
            _al.SourceUnqueueBuffers(_source, one);
            if (Fill(one[0]) > 0)
                _al.SourceQueueBuffers(_source, one); // refilled → back in the ring
            // else: a non-looping track has drained; let this buffer fall out of the queue.
        }

        // If the source stopped on its own it either underran (buffers still queued → resume) or
        // genuinely finished (queue empty → done).
        _al.GetSourceProperty(_source, GetSourceInteger.SourceState, out int state);
        if (state != (int)SourceState.Playing)
        {
            _al.GetSourceProperty(_source, GetSourceInteger.BuffersQueued, out int queued);
            if (queued > 0) _al.SourcePlay(_source);
            else Finished = true;
        }
    }

    /// <summary>Pack up to one chunk of PCM into <paramref name="buffer"/>, wrapping to the start when
    /// looping. Returns the byte count uploaded (0 when a non-looping track has no data left).</summary>
    private int Fill(uint buffer)
    {
        var chunk = new byte[_chunkBytes];
        int written = 0;
        while (written < _chunkBytes)
        {
            if (_readPos >= _clip.Pcm.Length)
            {
                if (_loop) _readPos = 0; else break;
            }
            int take = Math.Min(_chunkBytes - written, _clip.Pcm.Length - _readPos);
            Array.Copy(_clip.Pcm, _readPos, chunk, written, take);
            written += take;
            _readPos += take;
            if (!_loop && _readPos >= _clip.Pcm.Length) break;
        }
        if (written == 0) return 0;
        if (written < chunk.Length) Array.Resize(ref chunk, written);
        _al.BufferData(buffer, _clip.Format, chunk, _clip.SampleRate);
        return written;
    }

    public void Dispose()
    {
        _al.SourceStop(_source);
        // Detach every queued buffer before deleting, or the driver rejects the buffer delete.
        _al.SetSourceProperty(_source, SourceInteger.Buffer, 0);
        _al.DeleteSource(_source);
        _al.DeleteBuffers(_buffers);
    }
}
