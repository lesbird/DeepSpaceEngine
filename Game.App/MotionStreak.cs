using Engine.Rendering;
using Silk.NET.Maths;

namespace Game.App;

/// <summary>How fast-flight motion stretching is rendered.</summary>
public enum StreakMode
{
    /// <summary>No stretching — plain point sprites.</summary>
    Off,
    /// <summary>Per-point geometry-shader streaks: each star/galaxy point becomes a capsule along its
    /// own screen-space motion. Crisp, physically-correct radial streaks. (Primary technique.)</summary>
    Streaks,
    /// <summary>Full-screen radial velocity blur, centred on the travel direction. Cheaper and uniform,
    /// but softer and blurs everything. (Backup technique.)</summary>
    ScreenBlur,
}

/// <summary>
/// Shared state and GLSL for the high-speed motion-streak effect. Stars and galaxy sprites are drawn as
/// GL_POINTS (always square), so to stretch them we expand each point into an elongated quad in a
/// geometry stage, oriented along where that point sat one "exposure" (<see cref="ExposureSeconds"/>)
/// ago. The renderers share the geometry+fragment stages below; each supplies its own vertex stage that
/// emits the common <c>V2G</c> interface block (clip-space now via gl_Position, plus colour, brightness,
/// pixel size, and the clip-space position one exposure ago).
/// </summary>
public static class MotionStreak
{
    /// <summary>Active technique. Toggled at runtime (see the tuning panel / key binding).</summary>
    public static StreakMode Mode = StreakMode.Streaks;

    /// <summary>Effective exposure (s): how far back along the motion each point's tail reaches. Larger =
    /// longer streaks. The streak length is <c>|cameraVelocity| · ExposureSeconds</c> in world metres —
    /// which is already huge at galaxy-flight speeds, so <see cref="MaxStreakNdc"/> is the knob that
    /// actually sets the on-screen length; this mostly shapes the low-speed ramp-in. Kept short: long
    /// streaks read as smeared rather than crisp.</summary>
    public static float ExposureSeconds = 0.05f;

    /// <summary>Streak length hard cap, in aspect-corrected NDC (screen height = 2). This is the dominant
    /// length control at speed. Short (~0.2 = a tenth of the screen) gives tight, crisp star streaks;
    /// large values smear points across the screen.</summary>
    public static float MaxStreakNdc = 0.2f;

    /// <summary>Below this ground speed (m/s) the streak programs are skipped entirely and the plain
    /// point programs draw — so normal flight is pixel-identical and pays no geometry-shader cost. Also
    /// the speed at which the screen-blur backup starts ramping in.</summary>
    public static float MinSpeedMps = 5.0e6f;

    /// <summary>Peak radial blur strength for <see cref="StreakMode.ScreenBlur"/> — the fraction of the
    /// focus-of-expansion→pixel vector smeared across at full speed.</summary>
    public static float ScreenBlurStrength = 0.25f;

    /// <summary>True when the per-point streak stage should run this frame for the given camera velocity.</summary>
    public static bool StreaksActive(in Vector3D<double> worldVelocity)
        => Mode == StreakMode.Streaks && worldVelocity.Length >= MinSpeedMps;

    /// <summary>Camera world velocity as float metres/second (positions are camera-relative world metres,
    /// so this shares their space directly).</summary>
    public static Vector3D<float> VelocityF(in Vector3D<double> worldVelocity)
        => new((float)worldVelocity.X, (float)worldVelocity.Y, (float)worldVelocity.Z);

    /// <summary>Set the geometry-stage uniforms every streak program needs (viewport + length cap).</summary>
    public static void SetGeomUniforms(Shader s, float viewportW, float viewportH)
    {
        s.SetVector2("uViewport", new Vector2D<float>(viewportW, viewportH));
        s.SetFloat("uMaxStreak", MaxStreakNdc);
    }

    /// <summary>
    /// Geometry stage shared by every streak program. Expands one point into a rounded-end quad
    /// (capsule) spanning its projected position now → one exposure ago. When the two coincide (idle),
    /// it degenerates to a round dot identical to the plain sprite, so there's no pop at the threshold.
    /// </summary>
    public const string GeometrySource = @"#version 410 core
layout(points) in;
layout(triangle_strip, max_vertices = 4) out;
in V2G { vec3 color; float bright; float size; vec4 clipPrev; } i[];
uniform vec2 uViewport;   // pixels
uniform float uMaxStreak; // clamp on streak length, in aspect-corrected NDC (screen height = 2)
out vec3 gColor;
out float gBright;
out vec2 gLocal;   // x: along-axis in half-width units, y: perpendicular in [-1,1]
out float gUnits;  // half-length of the straight core, in half-width units
void main() {
    vec4 cNow = gl_in[0].gl_Position;
    if (cNow.w <= 0.0) return;                       // point behind the camera — drop it
    vec2 aspect = vec2(uViewport.x / uViewport.y, 1.0); // work in square units so the cross-section stays round
    vec2 ndcNow = cNow.xy / cNow.w;
    vec4 cPrev = i[0].clipPrev;
    vec2 ndcPrev = cPrev.w > 0.0 ? cPrev.xy / cPrev.w : ndcNow; // if the tail is behind us, no streak

    vec2 a = ndcNow * aspect;
    vec2 b = ndcPrev * aspect;
    vec2 d = a - b;
    float len = length(d);
    if (len > uMaxStreak) { d *= uMaxStreak / len; len = uMaxStreak; b = a - d; }
    vec2 axis = len > 1e-6 ? d / len : vec2(1.0, 0.0);
    vec2 perp = vec2(-axis.y, axis.x);

    float halfW = max(i[0].size, 1.0) / uViewport.y;  // half-thickness in square-NDC (size is a diameter in px)
    float halfLen = len * 0.5;
    float units = halfLen / halfW;
    vec2 c = (a + b) * 0.5;
    vec2 ext = axis * (halfLen + halfW);              // reach past each end by one radius for the rounded cap
    vec2 wid = perp * halfW;

    gColor = i[0].color; gBright = i[0].bright; gUnits = units;

    gLocal = vec2(units + 1.0,  1.0); gl_Position = vec4((c + ext + wid) / aspect, 0.0, 1.0); EmitVertex();
    gLocal = vec2(units + 1.0, -1.0); gl_Position = vec4((c + ext - wid) / aspect, 0.0, 1.0); EmitVertex();
    gLocal = vec2(-(units + 1.0), 1.0); gl_Position = vec4((c - ext + wid) / aspect, 0.0, 1.0); EmitVertex();
    gLocal = vec2(-(units + 1.0),-1.0); gl_Position = vec4((c - ext - wid) / aspect, 0.0, 1.0); EmitVertex();
    EndPrimitive();
}";

    /// <summary>
    /// Fragment stage shared by every streak program: a soft capsule whose round cross-section matches the
    /// plain sprite's <c>exp(-r²·2.5)</c> falloff, so at zero length it looks exactly like the dot it
    /// replaces. Brightness is carried in <c>gBright</c> and colour in <c>gColor</c>, so this works under
    /// both the additive (One,One) and SrcAlpha,One blend regimes the callers use.
    /// </summary>
    public const string FragmentSource = @"#version 410 core
in vec3 gColor;
in float gBright;
in vec2 gLocal;
in float gUnits;
out vec4 FragColor;
void main() {
    float dx = max(abs(gLocal.x) - gUnits, 0.0);      // distance past the straight core along the axis
    float d2 = dx * dx + gLocal.y * gLocal.y;         // squared distance from the capsule centre-line
    if (d2 > 1.0) discard;
    float a = exp(-d2 * 2.5) * gBright;
    FragColor = vec4(gColor, 1.0) * a;
}";
}
