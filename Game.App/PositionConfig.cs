using System.IO;
using System.Text.Json;
using Engine.Core;
using Engine.Rendering;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>
/// Persisted camera location — saved on quit and restored on the next launch so you resume exactly
/// where you left off. Stores the exact <see cref="UniversePosition"/> (sector Int64 indices + local
/// double offsets) so sub-millimetre precision survives a round-trip, plus the orientation quaternion
/// and the wheel-controlled speed exponent.
/// </summary>
public sealed class PositionConfig
{
    public long[] Sector { get; set; } = { 0, 0, 0 };
    public double[] Local { get; set; } = { 0, 0, 0 };
    public float[] Orientation { get; set; } = { 0, 0, 0, 1 };
    public float SpeedExponent { get; set; } = 15f;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Serialize the camera's current position/orientation to <paramref name="path"/>. Returns false on IO error.</summary>
    public static bool Save(Camera cam, FreeFlyController ctrl, string path)
    {
        try
        {
            UniversePosition p = cam.Position;
            Quaternion<float> q = cam.Orientation;
            var cfg = new PositionConfig
            {
                Sector = new[] { p.Sector.X, p.Sector.Y, p.Sector.Z },
                Local = new[] { p.Local.X, p.Local.Y, p.Local.Z },
                Orientation = new[] { q.X, q.Y, q.Z, q.W },
                SpeedExponent = ctrl.SpeedExponent,
            };
            File.WriteAllText(path, JsonSerializer.Serialize(cfg, Options));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Restore a saved position/orientation from <paramref name="path"/> if it exists. Returns false if absent/invalid.</summary>
    public static bool Load(Camera cam, FreeFlyController ctrl, string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            PositionConfig? cfg = JsonSerializer.Deserialize<PositionConfig>(File.ReadAllText(path));
            if (cfg is not { Sector.Length: >= 3, Local.Length: >= 3 }) return false;

            var pos = new UniversePosition(
                new Vector3D<long>(cfg.Sector[0], cfg.Sector[1], cfg.Sector[2]),
                new Vector3D<double>(cfg.Local[0], cfg.Local[1], cfg.Local[2]));
            pos.Normalize();
            cam.Position = pos;

            if (cfg.Orientation is { Length: >= 4 })
                cam.Orientation = Quaternion<float>.Normalize(new Quaternion<float>(
                    cfg.Orientation[0], cfg.Orientation[1], cfg.Orientation[2], cfg.Orientation[3]));

            ctrl.SpeedExponent = cfg.SpeedExponent;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
