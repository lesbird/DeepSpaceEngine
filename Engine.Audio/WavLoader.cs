using System.Buffers.Binary;

namespace Engine.Audio;

/// <summary>
/// Minimal RIFF/WAVE reader for uncompressed PCM — no external decoder dependency. Walks the
/// chunk list so it tolerates extra chunks (LIST/INFO/fact) some tools insert between "fmt " and
/// "data". Handles 8-bit (unsigned) and 16-bit (signed) mono/stereo, which is everything OpenAL's
/// base buffer formats accept; anything else throws with a clear message.
/// </summary>
public static class WavLoader
{
    public static AudioClip Load(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        try
        {
            return Parse(bytes);
        }
        catch (Exception e)
        {
            throw new InvalidDataException($"Failed to parse WAV '{path}': {e.Message}", e);
        }
    }

    public static AudioClip Parse(ReadOnlySpan<byte> b)
    {
        if (b.Length < 12 || !Tag(b, 0, "RIFF") || !Tag(b, 8, "WAVE"))
            throw new InvalidDataException("Not a RIFF/WAVE file.");

        int channels = 0, sampleRate = 0, bits = 0, audioFormat = 0;
        bool haveFmt = false;
        ReadOnlySpan<byte> data = default;

        // Chunk walk: 4-byte id, 4-byte little-endian size, then payload (padded to even length).
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            int size = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(pos + 4, 4));
            int body = pos + 8;
            if (size < 0 || body + size > b.Length)
                size = b.Length - body; // tolerate a truncated/over-stated final chunk

            if (Tag(b, pos, "fmt "))
            {
                // fmt layout: format(2) channels(2) sampleRate(4) byteRate(4) blockAlign(2) bits(2).
                audioFormat = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(body, 2));
                channels    = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(body + 2, 2));
                sampleRate  = BinaryPrimitives.ReadInt32LittleEndian(b.Slice(body + 4, 4));
                bits        = BinaryPrimitives.ReadInt16LittleEndian(b.Slice(body + 14, 2));
                haveFmt = true;
            }
            else if (Tag(b, pos, "data"))
            {
                data = b.Slice(body, size);
            }

            pos = body + size + (size & 1); // chunks are word-aligned
        }

        if (!haveFmt) throw new InvalidDataException("Missing 'fmt ' chunk.");
        if (data.Length == 0) throw new InvalidDataException("Missing or empty 'data' chunk.");
        // 1 = PCM, 0xFFFE = WAVE_FORMAT_EXTENSIBLE (still linear PCM for our purposes).
        if (audioFormat != 1 && audioFormat != 0xFFFE)
            throw new InvalidDataException($"Only uncompressed PCM is supported (format tag {audioFormat}).");

        return new AudioClip(data.ToArray(), sampleRate, channels, bits);
    }

    private static bool Tag(ReadOnlySpan<byte> b, int at, string tag) =>
        b[at] == tag[0] && b[at + 1] == tag[1] && b[at + 2] == tag[2] && b[at + 3] == tag[3];
}
