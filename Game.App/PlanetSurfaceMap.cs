using Engine.Core;
using Game.Universe;
using Silk.NET.Maths;
using Silk.NET.OpenGL;

namespace Game.App;

/// <summary>
/// Bakes an equirectangular <b>albedo + normal</b> map for one planet straight from its CPU terrain
/// (<see cref="PlanetTerrain.HeightAt"/> / <see cref="PlanetTerrain.ColorAt"/>), so the distant view
/// (the <see cref="SystemRenderer"/> sphere) and the near view (the quadtree terrain) draw from one
/// source of truth. A crater seen from orbit is then literally the same crater you land in — the two
/// can't drift apart, because they're sampled from the same baked data instead of two different noise
/// fields. Sampling is by direction→lat/long, a mapping defined identically here and in the shaders.
///
/// Each texel is sampled with a <i>texel-sized</i> band-limit, so the map carries exactly the scales
/// the mesh shows at that LOD (large craters from orbit; finer ones arrive as real geometry up close)
/// and never aliases. Baking runs on the thread pool; the finished bytes upload on the render thread.
/// One map is kept at a time, for the nearest surfaced body, and re-baked when that body changes.
/// </summary>
public sealed class PlanetSurfaceMap : IDisposable
{
    public const int MapWidth = 2048;
    public const int MapHeight = 1024;

    private readonly GL _gl;
    private uint _albedoTex;
    private uint _normalTex;

    private Task<(byte[] albedo, byte[] normal)>? _bake;
    private ulong _bakingId = ulong.MaxValue;

    /// <summary>True once a baked map has been uploaded and is ready to sample.</summary>
    public bool Ready { get; private set; }
    /// <summary>The body the currently-uploaded map belongs to (sample only when drawing this body).</summary>
    public ulong BodyId { get; private set; } = ulong.MaxValue;
    public uint AlbedoTex => _albedoTex;
    public uint NormalTex => _normalTex;

    public PlanetSurfaceMap(GL gl) => _gl = gl;

    /// <summary>Ensure a map is baking/built for <paramref name="bodyId"/>. Cheap to call every frame;
    /// it only kicks off a new bake when the target body actually changes.</summary>
    public void Request(PlanetTerrain terrain, ulong bodyId)
    {
        if (bodyId == _bakingId) return; // already baking or already built this body
        _bakingId = bodyId;
        Ready = bodyId == BodyId;        // until the new bake lands, fall back (renderers go procedural)
        PlanetTerrain t = terrain;       // immutable + pure reads → safe on the pool
        _bake = Task.Run(() => Bake(t));
    }

    /// <summary>Render-thread: upload a finished bake (if any) to GL. Stale bakes (target changed mid-
    /// flight) are dropped.</summary>
    public void Update()
    {
        if (_bake is not { IsCompleted: true }) return;
        if (_bake.IsCompletedSuccessfully)
        {
            (byte[] albedo, byte[] normal) = _bake.Result;
            Upload(ref _albedoTex, albedo);
            Upload(ref _normalTex, normal);
            BodyId = _bakingId;
            Ready = true;
        }
        _bake = null;
    }

    private static (byte[] albedo, byte[] normal) Bake(PlanetTerrain t)
    {
        var albedo = new byte[MapWidth * MapHeight * 3];
        var normal = new byte[MapWidth * MapHeight * 3];
        double radius = t.Radius;
        double texArc = 2.0 * Math.PI * radius / MapWidth; // metres across one texel at the equator
        bool hasOcean = t.HasOcean;
        double seaLevel = t.SeaLevelMeters, amp = t.Amplitude;
        var shallow = new Vector3D<float>(0.20f, 0.55f, 0.62f);
        var deep = new Vector3D<float>(0.02f, 0.10f, 0.26f);

        Parallel.For(0, MapHeight, j =>
        {
            double lat = ((j + 0.5) / MapHeight - 0.5) * Math.PI;     // [-π/2, π/2]
            for (int i = 0; i < MapWidth; i++)
            {
                double lon = ((i + 0.5) / MapWidth - 0.5) * 2.0 * Math.PI; // [-π, π]
                Vector3D<double> dir = DirFromLatLon(lat, lon);

                double h = t.HeightAt(dir, texArc);
                Vector3D<double> n;
                Vector3D<float> c;
                if (hasOcean && h < seaLevel)
                {
                    // Below the waterline the map must show the WATER surface (the translucent water is
                    // a separate pass it doesn't capture) — flat, blue, matching the water renderer.
                    float f = (float)Math.Clamp((seaLevel - h) / (amp * 0.12 + 1.0), 0, 1);
                    c = shallow + (deep - shallow) * f;
                    n = dir; // flat sea surface
                }
                else
                {
                    n = NormalAt(t, dir, texArc, radius);
                    double slope = Vector3D.Dot(n, dir);              // cos(steepness): 1 flat → 0 cliff
                    c = t.ColorAt(dir, h, slope, texArc);
                }

                int o = (j * MapWidth + i) * 3;
                albedo[o + 0] = ToByte(c.X); albedo[o + 1] = ToByte(c.Y); albedo[o + 2] = ToByte(c.Z);
                // Object-space (planet-local) normal, encoded to [0,1]. Planet-local == world
                // orientation (the terrain mesh is translated, never rotated), so the shader can light
                // it directly against the camera-relative sun without any extra transform.
                normal[o + 0] = ToByte((float)(n.X * 0.5 + 0.5));
                normal[o + 1] = ToByte((float)(n.Y * 0.5 + 0.5));
                normal[o + 2] = ToByte((float)(n.Z * 0.5 + 0.5));
            }
        });
        return (albedo, normal);
    }

    /// <summary>Surface normal at a direction from a centred height difference along two tangents,
    /// band-limited to the texel scale (matches the coarse mesh normal the patch would show).</summary>
    private static Vector3D<double> NormalAt(PlanetTerrain t, Vector3D<double> dir, double texArc, double radius)
    {
        double d = texArc / radius; // angular step (radians)
        Vector3D<double> up = Math.Abs(dir.Y) < 0.99 ? new Vector3D<double>(0, 1, 0) : new Vector3D<double>(1, 0, 0);
        Vector3D<double> east = Vector3D.Normalize(Vector3D.Cross(up, dir));
        Vector3D<double> north = Vector3D.Cross(dir, east);

        Vector3D<double> dE = Vector3D.Normalize(dir + east * d), dW = Vector3D.Normalize(dir - east * d);
        Vector3D<double> dN = Vector3D.Normalize(dir + north * d), dS = Vector3D.Normalize(dir - north * d);
        Vector3D<double> pE = dE * (radius + t.HeightAt(dE, texArc)), pW = dW * (radius + t.HeightAt(dW, texArc));
        Vector3D<double> pN = dN * (radius + t.HeightAt(dN, texArc)), pS = dS * (radius + t.HeightAt(dS, texArc));

        Vector3D<double> nrm = Vector3D.Cross(pE - pW, pN - pS);
        if (Vector3D.Dot(nrm, dir) < 0) nrm = -nrm;
        return nrm.LengthSquared > 0 ? Vector3D.Normalize(nrm) : dir;
    }

    /// <summary>Direction from latitude/longitude. Inverse of the shader's dir→uv (atan/asin) — they
    /// must stay in lockstep or the baked craters would land in the wrong place.</summary>
    private static Vector3D<double> DirFromLatLon(double lat, double lon)
    {
        double cl = Math.Cos(lat);
        return new Vector3D<double>(cl * Math.Cos(lon), Math.Sin(lat), cl * Math.Sin(lon));
    }

    private static byte ToByte(float v) => (byte)Math.Clamp((int)MathF.Round(v * 255f), 0, 255);

    private unsafe void Upload(ref uint tex, byte[] rgb)
    {
        if (tex == 0) tex = _gl.GenTexture();
        _gl.BindTexture(TextureTarget.Texture2D, tex);
        fixed (byte* p = rgb)
            _gl.TexImage2D(TextureTarget.Texture2D, 0, (int)InternalFormat.Rgb8, MapWidth, MapHeight, 0,
                PixelFormat.Rgb, PixelType.UnsignedByte, p);
        _gl.GenerateMipmap(TextureTarget.Texture2D);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.LinearMipmapLinear);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        // Longitude wraps (seamless ±180°); latitude clamps at the poles.
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.Repeat);
        _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        _gl.BindTexture(TextureTarget.Texture2D, 0);
    }

    public void Dispose()
    {
        if (_albedoTex != 0) _gl.DeleteTexture(_albedoTex);
        if (_normalTex != 0) _gl.DeleteTexture(_normalTex);
    }
}
