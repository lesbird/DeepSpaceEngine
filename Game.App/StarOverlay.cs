using Engine.Core;
using Engine.Rendering;
using Game.Systems;
using Game.Systems.Discovery;
using Game.Universe;
using ImGuiNET;
using Silk.NET.Maths;
using Vector2 = System.Numerics.Vector2;

namespace Game.App;

/// <summary>
/// 2D navigation HUD drawn with ImGui's foreground draw list: a reticle + catalog label
/// for each nearby star, and an arrow/marker pointing to the nearest star (highlighted in
/// place when on screen, clamped to the screen edge as an arrow when off screen or behind).
/// </summary>
public sealed class StarOverlay
{
    /// <summary>Only stars within this distance get a reticle + label.</summary>
    public double LabelRadiusLy = 7.0;
    public int MaxLabels = 16;

    /// <summary>Show a planet's moon reticles only when the camera is within this multiple
    /// of the planet's outermost moon orbit (keeps far-away planets uncluttered).</summary>
    public double MoonRevealFactor = 40.0;

    /// <summary>Hold off the per-planet reticles until the nearest planet is within this distance (AU).
    /// Far out, every planet projects onto the same spot by the sun, so their reticles pile up; this
    /// keeps just the sun + the directional nearest-planet marker until you've closed in.</summary>
    public double PlanetRevealAu = 10.0;

    private readonly List<(double DistSq, Star Star)> _scratch = new();

    // Set for the duration of one Draw call so the label helpers can credit discovered objects.
    private DiscoveryService? _discovery;

    /// <summary>Reticle suffix crediting a discovered star/body (name only — the full date is in the
    /// scanner). Empty when undiscovered or discovery is off.</summary>
    private string StarCredit(in Star s) =>
        _discovery != null && _discovery.TryGetStar(s, out DiscoveryRecord r) ? $"  by {r.Discoverer}" : "";

    private string BodyCredit(SolarSystem sys, CelestialBody b) =>
        _discovery != null && _discovery.TryGetBody(sys, b, out DiscoveryRecord r) ? $"  by {r.Discoverer}" : "";

    public void Draw(Camera camera, StarCatalogPager field, SolarSystemManager manager,
        DiscoveryService? discovery = null, Star? searchTarget = null, bool atSurface = false)
    {
        _discovery = discovery;
        var io = ImGui.GetIO();
        Vector2 vp = io.DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        var dl = ImGui.GetForegroundDrawList();
        DrawCenterReticle(dl, vp); // the aiming crosshair stays — it's a screen aid, not an object marker

        // Once you've descended to a world (within its atmosphere / near-surface shell), every reticle for
        // something *outside* that planet is just clutter pointing through the ground: the other planets,
        // moons, the sun, nearby stars, and the searched-for star. Suppress them all here. (Future:
        // surface-based reticles — pickups, mineable deposits — would be drawn in this branch instead.)
        if (atSurface) return;

        Matrix4X4<float> m = camera.ViewMatrix * camera.ProjectionMatrix;
        var invOrientation = Quaternion<float>.Inverse(camera.Orientation);
        UniversePosition cam = camera.Position;

        if (manager.HasActive)
        {
            // Inside a system: drop the star clutter, mark the sun + planets instead.
            SolarSystem sys = manager.Active!;
            DrawSystemReticles(dl, sys, cam, m, vp);
            DrawNearestPlanetMarker(dl, sys, cam, m, vp, invOrientation);
        }
        else
        {
            DrawNearbyReticles(dl, field, cam, m, vp);
            if (field.HasNearest)
                DrawNearestMarker(dl, camera, field, cam, m, vp, invOrientation);
        }

        // A searched-for star is flagged in both modes so you can keep it in view while
        // closing in and entering its system. Star positions are fixed, so the marker
        // keeps pointing even after the star leaves the render bubble.
        if (searchTarget.HasValue)
            DrawSearchMarker(dl, searchTarget.Value, cam, m, vp, invOrientation);
    }

    private static void DrawSearchMarker(ImDrawListPtr dl, in Star s, in UniversePosition cam,
        in Matrix4X4<float> m, Vector2 vp, in Quaternion<float> invOrientation)
    {
        double distLy = s.Position.DistanceTo(cam) / MathUtil.LightYear;
        uint hi = Col(255, 215, 90, 255); // amber — distinct from the green "nearest" marker
        string label = $"TARGET  {s.Name}  #{s.Id}   {distLy:0.000} ly";

        Vector3D<float> rel = s.Position.ToCameraRelative(cam);
        if (Project(rel, m, vp, out Vector2 screen) && OnScreen(screen, vp))
        {
            const float r = 18f;
            DrawBrackets(dl, screen, r, hi);
            dl.AddCircle(screen, r + 4f, hi, 24, 1.5f);
            dl.AddText(screen + new Vector2(r + 8, -r), hi, label);
            return;
        }

        // Off screen or behind: clamp an arrow to the screen edge pointing toward it.
        Vector3D<float> viewDir = Vector3D.Transform(rel, invOrientation);
        Vector2 dir = new(viewDir.X, -viewDir.Y);
        if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
        dir = Vector2.Normalize(dir);

        Vector2 pos = ClampToEdge(vp * 0.5f, dir, vp, margin: 64f);
        DrawArrow(dl, pos, dir, hi);
        Vector2 textPos = Vector2.Clamp(pos - dir * 26f - new Vector2(70, -10),
            new Vector2(8, 8), vp - new Vector2(240, 24));
        dl.AddText(textPos, hi, label);
    }

    private void DrawSystemReticles(ImDrawListPtr dl, SolarSystem sys,
        in UniversePosition cam, in Matrix4X4<float> m, Vector2 vp)
    {
        // The sun (the one star we keep).
        Vector3D<float> sunRel = sys.Sun.Position.ToCameraRelative(cam);
        if (Project(sunRel, m, vp, out Vector2 sunScreen) && OnScreen(sunScreen, vp))
        {
            uint col = StarColor(sys.Sun, 0xE0);
            dl.AddCircle(sunScreen, 13f, col, 22, 2f);
            dl.AddText(sunScreen + new Vector2(16, -8), col, $"{sys.Sun.Name}  ({sys.Sun.ClassLetter}-class){StarCredit(sys.Sun)}");
        }

        // Until the nearest planet is reasonably close, skip the planet/moon reticles entirely: from far
        // out they all crowd the sun's screen position into an unreadable pile. The sun reticle above and
        // the directional nearest-planet marker still guide you in.
        double nearestSq = double.MaxValue;
        foreach (Planet p in sys.Planets)
            nearestSq = Math.Min(nearestSq, p.CurrentPosition.DistanceSquaredTo(cam));
        double revealM = PlanetRevealAu * MathUtil.AstronomicalUnit;
        if (nearestSq > revealM * revealM) return;

        // Every on-screen planet gets a reticle: designation + type/distance.
        foreach (Planet p in sys.Planets)
        {
            double planetDist = p.CurrentPosition.DistanceTo(cam);
            Vector3D<float> rel = p.CurrentPosition.ToCameraRelative(cam);
            if (Project(rel, m, vp, out Vector2 s) && OnScreen(s, vp))
            {
                uint col = PlanetColor(p.Color, 0xD0);
                dl.AddCircle(s, 8f, col, 16, 1.6f);
                dl.AddText(s + new Vector2(11, -8), col, $"{p.Designation}{BodyCredit(sys, p)}");
                dl.AddText(s + new Vector2(11, 6), Col(180, 195, 215, 205), $"{p.Type}  {Dist(planetDist)}");
            }

            // Reveal a planet's moon reticles only when we're close to that planet.
            if (p.Moons.Length == 0) continue;
            double maxMoonOrbit = 0;
            foreach (Moon mn in p.Moons) maxMoonOrbit = Math.Max(maxMoonOrbit, mn.SemiMajorAxis);
            if (planetDist > maxMoonOrbit * MoonRevealFactor) continue;

            foreach (Moon mn in p.Moons)
            {
                Vector3D<float> mrel = mn.CurrentPosition.ToCameraRelative(cam);
                if (!Project(mrel, m, vp, out Vector2 ms) || !OnScreen(ms, vp)) continue;
                uint mcol = PlanetColor(mn.Color, 0xB0);
                dl.AddCircle(ms, 5f, mcol, 12, 1.3f);
                dl.AddText(ms + new Vector2(8, -6), mcol, $"{mn.Designation}{BodyCredit(sys, mn)}");
            }
        }
    }

    private static void DrawNearestPlanetMarker(ImDrawListPtr dl, SolarSystem sys,
        in UniversePosition cam, in Matrix4X4<float> m, Vector2 vp, in Quaternion<float> invOrientation)
    {
        Planet? nearest = null;
        double bestSq = double.MaxValue;
        foreach (Planet p in sys.Planets)
        {
            double d2 = p.CurrentPosition.DistanceSquaredTo(cam);
            if (d2 < bestSq) { bestSq = d2; nearest = p; }
        }
        if (nearest == null) return;

        uint hi = Col(120, 255, 160, 255);
        string label = $"NEAREST PLANET  {nearest.Designation}  {nearest.Type}  {Dist(Math.Sqrt(bestSq))}";
        Vector3D<float> rel = nearest.CurrentPosition.ToCameraRelative(cam);

        if (Project(rel, m, vp, out Vector2 screen) && OnScreen(screen, vp))
        {
            DrawBrackets(dl, screen, 15f, hi);
            dl.AddText(screen + new Vector2(19, -15), hi, label);
            return;
        }

        Vector3D<float> viewDir = Vector3D.Transform(rel, invOrientation);
        Vector2 dir = new(viewDir.X, -viewDir.Y);
        if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
        dir = Vector2.Normalize(dir);

        Vector2 pos = ClampToEdge(vp * 0.5f, dir, vp, margin: 64f);
        DrawArrow(dl, pos, dir, hi);
        Vector2 textPos = Vector2.Clamp(pos - dir * 26f - new Vector2(70, -10), new Vector2(8, 8), vp - new Vector2(240, 24));
        dl.AddText(textPos, hi, label);
    }

    private void DrawNearbyReticles(ImDrawListPtr dl, StarCatalogPager field, in UniversePosition cam,
        in Matrix4X4<float> m, Vector2 vp)
    {
        double radiusM = LabelRadiusLy * MathUtil.LightYear;
        double radiusSq = radiusM * radiusM;

        _scratch.Clear();
        var visible = field.Visible;
        for (int i = 0; i < visible.Count; i++)
        {
            double d2 = visible[i].Position.DistanceSquaredTo(cam);
            if (d2 <= radiusSq) _scratch.Add((d2, visible[i]));
        }
        _scratch.Sort((a, b) => a.DistSq.CompareTo(b.DistSq));

        int n = Math.Min(_scratch.Count, MaxLabels);
        for (int i = 0; i < n; i++)
        {
            Star s = _scratch[i].Star;
            Vector3D<float> rel = s.Position.ToCameraRelative(cam);
            if (!Project(rel, m, vp, out Vector2 screen) || !OnScreen(screen, vp)) continue;

            double distLy = Math.Sqrt(_scratch[i].DistSq) / MathUtil.LightYear;
            uint col = StarColor(s, 0xC0);
            dl.AddCircle(screen, 9f, col, 16, 1.5f);
            dl.AddText(screen + new Vector2(12, -8), col, $"{s.Name}{StarCredit(s)}");
            dl.AddText(screen + new Vector2(12, 6), Col(170, 190, 220, 200), $"{s.ClassLetter}  {distLy:0.00} ly");
        }
    }

    private static void DrawNearestMarker(ImDrawListPtr dl, Camera camera, StarCatalogPager field,
        in UniversePosition cam, in Matrix4X4<float> m, Vector2 vp, in Quaternion<float> invOrientation)
    {
        Star s = field.Nearest;
        double distLy = field.NearestDistanceMeters / MathUtil.LightYear;
        uint hi = Col(120, 255, 160, 255);
        string label = $"NEAREST  {s.Name}  #{s.Id}   {distLy:0.000} ly";

        Vector3D<float> rel = s.Position.ToCameraRelative(cam);
        bool inFront = Project(rel, m, vp, out Vector2 screen) && OnScreen(screen, vp);

        if (inFront)
        {
            // Animated-looking corner brackets around the star.
            const float r = 16f;
            DrawBrackets(dl, screen, r, hi);
            dl.AddText(screen + new Vector2(r + 4, -r), hi, label);
            return;
        }

        // Off screen or behind: clamp an arrow to the screen edge pointing toward it.
        Vector3D<float> viewDir = Vector3D.Transform(rel, invOrientation); // camera space (-Z forward)
        Vector2 dir = new(viewDir.X, -viewDir.Y);
        if (dir.LengthSquared() < 1e-6f) dir = new Vector2(0, 1);
        dir = Vector2.Normalize(dir);

        Vector2 center = vp * 0.5f;
        Vector2 pos = ClampToEdge(center, dir, vp, margin: 64f);
        DrawArrow(dl, pos, dir, hi);

        Vector2 textPos = pos - dir * 26f - new Vector2(60, -10);
        textPos = Vector2.Clamp(textPos, new Vector2(8, 8), vp - new Vector2(220, 24));
        dl.AddText(textPos, hi, label);
    }

    // --- helpers ---

    /// <summary>A fixed aiming reticle at the centre of the screen: line a star or planet up inside
    /// it and thrust forward to fly straight at it. A gap is left in the middle so the crosshair never
    /// hides the tiny disc of a distant target you're trying to centre.</summary>
    private static void DrawCenterReticle(ImDrawListPtr dl, Vector2 vp)
    {
        Vector2 c = vp * 0.5f;
        uint col = Col(140, 235, 180, 200); // soft green, semi-transparent, matching the nav highlights
        const float gap = 6f, len = 12f, thick = 1.5f;
        dl.AddLine(c - new Vector2(0, gap), c - new Vector2(0, gap + len), col, thick); // up tick
        dl.AddLine(c + new Vector2(0, gap), c + new Vector2(0, gap + len), col, thick); // down tick
        dl.AddLine(c - new Vector2(gap, 0), c - new Vector2(gap + len, 0), col, thick); // left tick
        dl.AddLine(c + new Vector2(gap, 0), c + new Vector2(gap + len, 0), col, thick); // right tick
        dl.AddCircleFilled(c, 1.5f, col, 6);                                            // centre dot
    }

    /// <summary>Bracket reticle + label on each globular cluster currently shown as a fuzzy sprite.
    /// Clusters sit far beyond the camera far plane, so they are projected by <i>direction</i> — a unit
    /// vector at a safe radius inside the frustum gives the correct screen position for a point along
    /// that ray. Call after <see cref="Draw"/>.</summary>
    public void DrawGlobularReticles(Camera camera, IReadOnlyList<GlobularCluster> clusters)
    {
        if (clusters.Count == 0) return;
        Vector2 vp = ImGui.GetIO().DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        var dl = ImGui.GetForegroundDrawList();
        Matrix4X4<float> m = camera.ViewMatrix * camera.ProjectionMatrix;
        uint col = Col(255, 225, 140, 220); // warm gold, matching the cluster tint

        foreach (GlobularCluster c in clusters)
        {
            Vector3D<double> rel = c.Center.DeltaMeters(camera.Position);
            double dist = rel.Length;
            if (dist < 1.0) continue;
            var dir = new Vector3D<float>((float)(rel.X / dist), (float)(rel.Y / dist), (float)(rel.Z / dist));
            if (!Project(dir * 1.0e10f, m, vp, out Vector2 s) || !OnScreen(s, vp)) continue;

            double distLy = dist / MathUtil.LightYear;
            DrawBrackets(dl, s, 9f, col);
            dl.AddText(s + new Vector2(13f, -7f), col, $"GC  {distLy:0} ly");
        }
    }

    /// <summary>Mark the centre of the galaxy you're inside — the supermassive black hole — so you can
    /// fly to it. Like the globular reticle the core is projected by <i>direction</i> (it can be tens of
    /// thousands of ly away, past the far plane); when it's off screen an edge arrow points the way.</summary>
    public void DrawGalaxyCenterReticle(Camera camera, in UniversePosition center)
    {
        Vector2 vp = ImGui.GetIO().DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        Vector3D<double> rel = center.DeltaMeters(camera.Position);
        double dist = rel.Length;
        if (dist < 1.0) return; // already at the core
        var dir = new Vector3D<float>((float)(rel.X / dist), (float)(rel.Y / dist), (float)(rel.Z / dist));

        var dl = ImGui.GetForegroundDrawList();
        Matrix4X4<float> m = camera.ViewMatrix * camera.ProjectionMatrix;
        uint col = Col(255, 120, 140, 235); // hot magenta-red — the SMBH core, distinct from gold/green
        double distLy = dist / MathUtil.LightYear;
        string label = distLy >= 1000 ? $"CORE  {distLy / 1000.0:0.0} kly" : $"CORE  {distLy:0} ly";

        if (Project(dir * 1.0e10f, m, vp, out Vector2 s) && OnScreen(s, vp))
        {
            DrawBrackets(dl, s, 11f, col);
            dl.AddText(s + new Vector2(15f, -8f), col, label);
            return;
        }

        // Off screen or behind: clamp an arrow to the screen edge pointing toward the core.
        var invOrientation = Quaternion<float>.Inverse(camera.Orientation);
        Vector3D<float> viewDir = Vector3D.Transform(dir, invOrientation);
        Vector2 ad = new(viewDir.X, -viewDir.Y);
        if (ad.LengthSquared() < 1e-6f) ad = new Vector2(0, 1);
        ad = Vector2.Normalize(ad);

        Vector2 pos = ClampToEdge(vp * 0.5f, ad, vp, margin: 64f);
        DrawArrow(dl, pos, ad, col);
        Vector2 textPos = pos - ad * 26f - new Vector2(60, -10);
        textPos = Vector2.Clamp(textPos, new Vector2(8, 8), vp - new Vector2(220, 24));
        dl.AddText(textPos, col, label);
    }

    /// <summary>In intergalactic space, mark the galaxy the centre reticle is lined up with (chosen by
    /// the caller) and read out the info you can't get from the sprite alone: its catalog id, morphology
    /// (shape), star count and distance. Like the core/globular reticles it is projected by
    /// <i>direction</i> (galaxies sit millions of ly away, far past the far plane).</summary>
    public void DrawAimedGalaxyReticle(Camera camera, in Galaxy g, double distanceMeters)
    {
        Vector2 vp = ImGui.GetIO().DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        Vector3D<double> rel = g.Center.DeltaMeters(camera.Position);
        double dist = rel.Length;
        if (dist < 1.0) return;
        var dir = new Vector3D<float>((float)(rel.X / dist), (float)(rel.Y / dist), (float)(rel.Z / dist));

        var dl = ImGui.GetForegroundDrawList();
        Matrix4X4<float> m = camera.ViewMatrix * camera.ProjectionMatrix;
        uint col = Col(180, 205, 255, 235); // cool blue, matching the intergalactic nav tint

        double mly = distanceMeters / MathUtil.LightYear / 1.0e6;
        string distStr = mly >= 1.0 ? $"{mly:0.00} Mly" : $"{mly * 1000.0:0} kly";
        // Multi-line so the sprite stays readable: id+name, then shape + population, then distance.
        string label = $"GALAXY  {g.Name}\n{g.Type}  •  {StarCountStr(g.StarCount)} stars\n{distStr}";

        if (Project(dir * 1.0e10f, m, vp, out Vector2 s) && OnScreen(s, vp))
        {
            DrawBrackets(dl, s, 13f, col);
            dl.AddText(s + new Vector2(17f, -9f), col, label);
            return;
        }

        // Off screen or behind: clamp an arrow to the screen edge pointing toward the galaxy.
        var invOrientation = Quaternion<float>.Inverse(camera.Orientation);
        Vector3D<float> viewDir = Vector3D.Transform(dir, invOrientation);
        Vector2 ad = new(viewDir.X, -viewDir.Y);
        if (ad.LengthSquared() < 1e-6f) ad = new Vector2(0, 1);
        ad = Vector2.Normalize(ad);

        Vector2 pos = ClampToEdge(vp * 0.5f, ad, vp, margin: 64f);
        DrawArrow(dl, pos, ad, col);
        Vector2 textPos = pos - ad * 26f - new Vector2(70, -10);
        textPos = Vector2.Clamp(textPos, new Vector2(8, 8), vp - new Vector2(260, 40));
        dl.AddText(textPos, col, label);
    }

    /// <summary>Compact star-count readout (12.3 B, 4.5 M, …) for the galaxy reticle.</summary>
    private static string StarCountStr(double n)
    {
        if (n >= 1e12) return $"{n / 1e12:0.#} T";
        if (n >= 1e9) return $"{n / 1e9:0.#} B";
        if (n >= 1e6) return $"{n / 1e6:0.#} M";
        if (n >= 1e3) return $"{n / 1e3:0.#} k";
        return $"{n:0}";
    }

    /// <summary>A persistent course marker to the current leg of a set course (galaxy → star → planet):
    /// corner brackets + a ring when the target is on screen, an edge arrow pointing the way when it's
    /// off. Projected by <i>direction</i> so it works at any distance (galaxy/star legs sit far past the
    /// far plane). Cyan — distinct from the nav green and the core magenta.</summary>
    public void DrawCourseMarker(Camera camera, in UniversePosition target, string label)
    {
        Vector2 vp = ImGui.GetIO().DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        Vector3D<double> rel = target.DeltaMeters(camera.Position);
        double dist = rel.Length;
        if (dist < 1.0) return;
        var dir = new Vector3D<float>((float)(rel.X / dist), (float)(rel.Y / dist), (float)(rel.Z / dist));

        var dl = ImGui.GetForegroundDrawList();
        Matrix4X4<float> m = camera.ViewMatrix * camera.ProjectionMatrix;
        uint col = Col(80, 220, 255, 240); // bright cyan

        if (Project(dir * 1.0e10f, m, vp, out Vector2 s) && OnScreen(s, vp))
        {
            DrawBrackets(dl, s, 15f, col);
            dl.AddCircle(s, 20f, col, 28, 1.5f);
            dl.AddText(s + new Vector2(24f, -8f), col, label);
            return;
        }

        var invOrientation = Quaternion<float>.Inverse(camera.Orientation);
        Vector3D<float> viewDir = Vector3D.Transform(dir, invOrientation);
        Vector2 ad = new(viewDir.X, -viewDir.Y);
        if (ad.LengthSquared() < 1e-6f) ad = new Vector2(0, 1);
        ad = Vector2.Normalize(ad);

        Vector2 pos = ClampToEdge(vp * 0.5f, ad, vp, margin: 80f);
        DrawArrow(dl, pos, ad, col);
        Vector2 textPos = Vector2.Clamp(pos - ad * 30f - new Vector2(80, -10), new Vector2(8, 8), vp - new Vector2(300, 24));
        dl.AddText(textPos, col, label);
    }

    private static bool Project(Vector3D<float> p, in Matrix4X4<float> m, Vector2 vp, out Vector2 screen)
    {
        // Matches the GPU path: clip = (p,1) * (view*proj) in Silk's row-vector convention.
        float cx = p.X * m.M11 + p.Y * m.M21 + p.Z * m.M31 + m.M41;
        float cy = p.X * m.M12 + p.Y * m.M22 + p.Z * m.M32 + m.M42;
        float cw = p.X * m.M14 + p.Y * m.M24 + p.Z * m.M34 + m.M44;
        if (cw <= 1e-4f) { screen = default; return false; } // behind the camera
        float ndcX = cx / cw, ndcY = cy / cw;
        screen = new Vector2((ndcX * 0.5f + 0.5f) * vp.X, (1f - (ndcY * 0.5f + 0.5f)) * vp.Y);
        return true;
    }

    private static bool OnScreen(Vector2 s, Vector2 vp)
        => s.X >= 0 && s.Y >= 0 && s.X <= vp.X && s.Y <= vp.Y;

    private static Vector2 ClampToEdge(Vector2 center, Vector2 dir, Vector2 vp, float margin)
    {
        float minX = margin, minY = margin, maxX = vp.X - margin, maxY = vp.Y - margin;
        float tx = dir.X > 0 ? (maxX - center.X) / dir.X : dir.X < 0 ? (minX - center.X) / dir.X : float.MaxValue;
        float ty = dir.Y > 0 ? (maxY - center.Y) / dir.Y : dir.Y < 0 ? (minY - center.Y) / dir.Y : float.MaxValue;
        float t = MathF.Max(0f, MathF.Min(tx, ty));
        return center + dir * t;
    }

    private static void DrawArrow(ImDrawListPtr dl, Vector2 tip, Vector2 dir, uint col)
    {
        Vector2 perp = new(-dir.Y, dir.X);
        Vector2 back = tip - dir * 18f;
        dl.AddTriangleFilled(tip, back + perp * 9f, back - perp * 9f, col);
        dl.AddLine(back, back - dir * 14f, col, 3f);
    }

    private static void DrawBrackets(ImDrawListPtr dl, Vector2 c, float r, uint col)
    {
        const float e = 6f;
        // four L-shaped corners
        dl.AddLine(c + new Vector2(-r, -r), c + new Vector2(-r + e, -r), col, 2f);
        dl.AddLine(c + new Vector2(-r, -r), c + new Vector2(-r, -r + e), col, 2f);
        dl.AddLine(c + new Vector2(r, -r), c + new Vector2(r - e, -r), col, 2f);
        dl.AddLine(c + new Vector2(r, -r), c + new Vector2(r, -r + e), col, 2f);
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(-r + e, r), col, 2f);
        dl.AddLine(c + new Vector2(-r, r), c + new Vector2(-r, r - e), col, 2f);
        dl.AddLine(c + new Vector2(r, r), c + new Vector2(r - e, r), col, 2f);
        dl.AddLine(c + new Vector2(r, r), c + new Vector2(r, r - e), col, 2f);
    }

    private static uint StarColor(in Star s, byte alpha)
    {
        byte r = (byte)Math.Clamp(s.Color.X * 255f + 40f, 0, 255);
        byte g = (byte)Math.Clamp(s.Color.Y * 255f + 40f, 0, 255);
        byte b = (byte)Math.Clamp(s.Color.Z * 255f + 40f, 0, 255);
        return Col(r, g, b, alpha);
    }

    private static uint PlanetColor(Vector3D<float> color, byte alpha)
    {
        byte r = (byte)Math.Clamp(color.X * 255f + 50f, 0, 255);
        byte g = (byte)Math.Clamp(color.Y * 255f + 50f, 0, 255);
        byte b = (byte)Math.Clamp(color.Z * 255f + 50f, 0, 255);
        return Col(r, g, b, alpha);
    }

    private static string Dist(double meters)
    {
        if (meters >= 0.01 * MathUtil.AstronomicalUnit) return $"{meters / MathUtil.AstronomicalUnit:0.00} AU";
        if (meters >= 1000) return $"{meters / 1000:0.0} km";
        return $"{meters:0} m";
    }

    private static uint Col(byte r, byte g, byte b, byte a)
        => (uint)(r | (g << 8) | (b << 16) | (a << 24));
}
