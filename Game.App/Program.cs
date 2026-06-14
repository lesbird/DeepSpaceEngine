using Engine.Core;
using Engine.Platform;
using Engine.Rendering;
using Game.Systems;
using Game.Universe;
using ImGuiNET;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.OpenGL;
using Silk.NET.OpenGL.Extensions.ImGui;

namespace Game.App;

internal static class Program
{
    private const int RenderRadiusCells = 8;     // ~33 ly bubble
    private const ulong WorldSeed = 0xA11CE5EEDUL;

    private static GL _gl = null!;
    private static Camera _camera = null!;
    private static FreeFlyController _controller = null!;
    private static StarField _starField = null!;
    private static StarRenderer _starRenderer = null!;
    private static GalaxyBackdrop _backdrop = null!;
    private static StarOverlay _overlay = null!;
    private static SolarSystemManager _systemManager = null!;
    private static SystemRenderer _systemRenderer = null!;
    private static AtmosphereRenderer _atmosphereRenderer = null!;
    private static PlanetTerrainRenderer _terrainRenderer = null!;
    private static RoverController _rover = null!;
    private static RoverRenderer _roverRenderer = null!;
    private static SceneFramebuffer _sceneFbo = null!;
    private static Planet? _terrainTarget;
    private static bool _driving;
    private static bool _prevR;
    private static ImGuiController _imgui = null!;

    private const double TerrainActivateRadii = 8.0; // switch to terrain LOD within N planet radii
    private const string TuningPath = "tuning.json";
    private static string _tuningStatus = "";

    // Tuning panel: which surface type the terrain/biome sliders currently edit.
    private static readonly PlanetType[] EditableTypes =
        { PlanetType.Lava, PlanetType.Rocky, PlanetType.Desert, PlanetType.Ocean, PlanetType.Ice };
    private static readonly string[] EditableTypeNames = { "Lava", "Rocky", "Desert", "Ocean", "Ice" };
    private static PlanetType _editType = PlanetType.Rocky;
    private static Planet? _prevEditTarget;

    private static GameWindow _window = null!;

    private static bool _mouseCaptured = true;
    private static bool _prevTab;
    private static bool _prevComma, _prevPeriod, _prevP;
    private static bool _paused;
    private static double _savedTimeScale;
    private static double _fps;
    private static bool _smoke;
    private static int _smokeFrames;

    private static void Main(string[] args)
    {
        _smoke = Array.IndexOf(args, "--smoke") >= 0;

        _window = new GameWindow("MetalEngine3 — M1: Star Field", 1280, 720);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Resize += size =>
        {
            _camera.AspectRatio = size.Y == 0 ? 1f : (float)size.X / size.Y;
            _sceneFbo.Resize(size.X, size.Y);
        };
        _window.Run();
        _window.Dispose();
    }

    private static void OnLoad()
    {
        _gl = _window.Gl;
        Console.WriteLine($"GL Renderer: {_gl.GetStringS(StringName.Renderer)}");
        Console.WriteLine($"GL Version:  {_gl.GetStringS(StringName.Version)}");

        _gl.ClearColor(0.005f, 0.005f, 0.012f, 1f);

        _camera = new Camera
        {
            Position = UniversePosition.Origin,
            AspectRatio = 1280f / 720f,
            NearPlane = 1.0e3f,
            FarPlane = 1.0e18f, // stars stream out to ~tens of ly
        };
        _controller = new FreeFlyController(_camera, _window.Keyboard, _window.Mouse)
        {
            SpeedExponent = 15f, // ~0.1 ly/s — roam between stars; wheel to adjust
        };

        _starField = new StarField(new GalaxyModel(WorldSeed));
        _starRenderer = new StarRenderer(_gl);
        _backdrop = new GalaxyBackdrop(_gl, WorldSeed);
        _overlay = new StarOverlay();
        _systemManager = new SolarSystemManager();
        _systemRenderer = new SystemRenderer(_gl);
        _atmosphereRenderer = new AtmosphereRenderer(_gl);
        _terrainRenderer = new PlanetTerrainRenderer(_gl);
        _rover = new RoverController(_camera, _window.Keyboard, _window.Mouse);
        _roverRenderer = new RoverRenderer(_gl);
        _sceneFbo = new SceneFramebuffer(_gl);
        var fb = _window.Window.FramebufferSize;
        _sceneFbo.Resize(fb.X, fb.Y);
        _imgui = new ImGuiController(_gl, _window.Window, _window.Input);

        // Restore previously-saved tuning so the sliders persist across launches.
        if (TuningConfig.Load(_atmosphereRenderer, _backdrop, TuningPath))
            _tuningStatus = $"Loaded {TuningPath}";

        SetMouseCaptured(true);
    }

    private static void OnUpdate(double dt)
    {
        if (_smoke && _smokeFrames == 0)
        {
            // Headless: jump next to the nearest star so the spawn/generate/render path runs.
            _starField.Update(_camera.Position, RenderRadiusCells);
            if (_starField.HasNearest)
                _camera.Position = _starField.Nearest.Position
                    .Translated(new Vector3D<double>(0.3 * MathUtil.LightYear, 0, 0));
        }
        if (_smoke && _smokeFrames == 1 && _systemManager.HasActive)
        {
            // Then drop in near a surfaced planet to exercise the terrain path.
            Planet? p = NearestSurfacedPlanet();
            if (p != null)
                _camera.Position = p.CurrentPosition.Translated(new Vector3D<double>(p.RadiusMeters * 3, 0, 0));
        }

        if (Edge(Key.Tab, ref _prevTab)) SetMouseCaptured(!_mouseCaptured);

        // 'R' toggles the surface rover. Enter only when a terrain planet is active and we're low
        // enough to be effectively at the surface; exit lifts back into free-fly from the chase cam.
        if (Edge(Key.R, ref _prevR))
        {
            if (_driving)
            {
                _driving = false;
            }
            else if (_terrainTarget != null)
            {
                double alt = _terrainTarget.CurrentPosition.DistanceTo(_camera.Position) - _terrainTarget.RadiusMeters;
                if (alt < Math.Max(2000.0, _terrainTarget.RadiusMeters * 0.002))
                {
                    _rover.Enter(_terrainTarget);
                    _driving = true;
                }
            }
            UpdateInputModes();
        }

        if (_window.Keyboard.IsKeyPressed(Key.Escape))
            _window.Window.Close();

        // Orbit time controls: ',' slower, '.' faster, 'P' pause.
        if (Edge(Key.P, ref _prevP))
        {
            _paused = !_paused;
            if (_paused) { _savedTimeScale = _systemManager.TimeScale; _systemManager.TimeScale = 0; }
            else { _systemManager.TimeScale = _savedTimeScale; }
        }
        double factor = Edge(Key.Period, ref _prevPeriod) ? 2.0 : Edge(Key.Comma, ref _prevComma) ? 0.5 : 0.0;
        if (factor != 0.0)
        {
            if (_paused) _savedTimeScale = Math.Clamp(_savedTimeScale * factor, 1.0, 1.0e7);
            else _systemManager.TimeScale = Math.Clamp(_systemManager.TimeScale * factor, 1.0, 1.0e7);
        }

        // Capture the active planet's position before it advances, so we can ride its frame.
        UniversePosition? ridePrev = _terrainTarget?.CurrentPosition;

        if (!_driving) _controller.Update(dt);
        _starField.Update(_camera.Position, RenderRadiusCells);
        _systemManager.Update(dt, _camera.Position, _starField);

        if (_driving)
        {
            // The rover lives in planet-centred coordinates and recomputes its absolute position
            // from the (now-advanced) planet, so it rides the orbital frame itself and drives the
            // chase camera. No separate camera ride-frame fix-up.
            _rover.Update(dt);
        }
        else if (_terrainTarget != null && ridePrev is { } prev)
        {
            // When near a planet, move with its orbital motion (otherwise time-lapse orbits would
            // leave the camera behind). Done in double precision via DeltaMeters → no drift.
            _camera.Position.Translate(_terrainTarget.CurrentPosition.DeltaMeters(prev));
        }

        // Auto speed cap disabled for now: speed is always wheel-controlled (logarithmic), with no
        // automatic drop inside a system. Restore the SpeedPolicy-based cap by passing the nearest
        // planet surface here (see git history / SetSpeedContext).
        _controller.SetSpeedContext(false, 0);

        _terrainTarget = PickTerrainTarget();
        _terrainRenderer.SetPlanet(_terrainTarget);
        // Keep the terrain finely refined under the rover so the drawn mesh matches the height it
        // rides on (no sinking through coarse, chord-flattened triangles while driving).
        _terrainRenderer.FocusPoint = _driving ? _rover.BodyPosition : null;

        // When you arrive at a new planet, focus the tuning panel on its type.
        if (_terrainTarget != null && !ReferenceEquals(_terrainTarget, _prevEditTarget))
        {
            _prevEditTarget = _terrainTarget;
            if (Array.IndexOf(EditableTypes, _terrainTarget.Type) >= 0)
                _editType = _terrainTarget.Type;
        }

        _fps = dt > 0 ? 1.0 / dt : 0;

        if (_smoke && _smokeFrames == 0 && _systemManager.HasActive)
        {
            SolarSystem sys = _systemManager.Active!;
            int moons = 0;
            foreach (Planet pl in sys.Planets) moons += pl.Moons.Length;
            double fastestC = sys.MaxOrbitalSpeedMps * _systemManager.TimeScale / MathUtil.SpeedOfLight;
            Console.WriteLine($"[smoke] system {sys.Sun.Designation}: {sys.Planets.Length} planets, {moons} moons total, " +
                $"fastest orbit {fastestC:0.000} c at TimeScale {_systemManager.TimeScale:0}");
            if (sys.Planets.Length > 0)
            {
                Planet p0 = sys.Planets[0];
                string m0 = p0.Moons.Length > 0 ? $", first moon {p0.Moons[0].Designation}" : "";
                Console.WriteLine($"[smoke] planet {p0.Designation} ({p0.Type}){m0}");
            }
        }
    }

    private static void OnRender(double dt)
    {
        _imgui.Update((float)dt);

        // Render the scene (stars + system + terrain) into an offscreen buffer so the atmosphere
        // can read its depth and clamp its ray march to the real surface.
        _sceneFbo.Bind();
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Distant galaxy first (writes no depth), so the streamed stars, system, and atmosphere all
        // composite over it and any opaque body occludes the sky behind it.
        _backdrop.Render(_camera);
        _starRenderer.Render(_camera, _starField.Visible, _systemManager.ActiveStarId);

        if (_systemManager.HasActive)
            _systemRenderer.Render(_camera, _systemManager.Active!, _terrainTarget);

        if (_terrainTarget != null)
        {
            SolarSystem sys = _systemManager.Active!;
            Vector3D<float> sunRel = sys.Sun.Position.ToCameraRelative(_camera.Position);
            Vector3D<float> planetRel = _terrainTarget.CurrentPosition.ToCameraRelative(_camera.Position);
            Vector3D<float> sunDir = Vector3D.Normalize(sunRel - planetRel);
            _terrainRenderer.Render(_camera, sunDir);

            // Rover over the terrain it just drew — same near/far so it shares the depth buffer and
            // the depth-aware atmosphere composites over it correctly.
            if (_driving)
                _roverRenderer.Render(_camera, _rover.BodyPosition, _rover.Forward, _rover.Up, sunDir,
                    _terrainRenderer.LastNear, _terrainRenderer.LastFar);
        }

        // Put the scene on screen, then composite the depth-aware atmosphere over it. Near/far come
        // from whichever projection wrote the dominant geometry's depth (terrain when landed, else system).
        _sceneFbo.BlitColorToScreen();
        if (_systemManager.HasActive)
        {
            float near = _terrainTarget != null ? _terrainRenderer.LastNear : _systemRenderer.LastNear;
            float far = _terrainTarget != null ? _terrainRenderer.LastFar : _systemRenderer.LastFar;
            _atmosphereRenderer.Render(_camera, _systemManager.Active!, _sceneFbo.DepthTexture, near, far);
        }

        _overlay.Draw(_camera, _starField, _systemManager);
        DrawHud();
        DrawTuning();
        _imgui.Render();

        if (_smoke && ++_smokeFrames > 24)
        {
            Console.WriteLine($"[smoke] terrain target: {_terrainTarget?.Designation ?? "none"}, " +
                $"patches: {_terrainRenderer.PatchCount}, drawn: {_terrainRenderer.LeafCount}");
            _window.Window.Close();
        }
    }

    private static void DrawHud()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10), ImGuiCond.Always);
        ImGui.Begin("Navigation",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav);

        ImGui.Text($"FPS: {_fps:0}");
        ImGui.Separator();

        var p = _camera.Position;
        double distLy = p.DistanceTo(UniversePosition.Origin) / MathUtil.LightYear;
        ImGui.Text($"Sector: {p.Sector.X}, {p.Sector.Y}, {p.Sector.Z}");
        ImGui.Text($"From origin: {distLy:0.000} ly");
        if (_controller.SpeedCapped)
            ImGui.Text($"Speed: {FormatSpeed(_controller.CurrentSpeed)}  " +
                $"({_controller.ThrottlePercent:0}% of {FormatSpeed(_controller.MaxSpeed)} cap)");
        else
            ImGui.Text($"Speed: {FormatSpeed(_controller.CurrentSpeed)}  (exp {_controller.SpeedExponent:0.0})");
        ImGui.Separator();

        ImGui.Text($"Stars rendered: {_starRenderer.LastDrawn}");
        ImGui.Text($"Cells cached:   {_starField.CachedCellCount}");
        ImGui.Separator();

        if (_starField.HasNearest)
        {
            var s = _starField.Nearest;
            double nLy = _starField.NearestDistanceMeters / MathUtil.LightYear;
            ImGui.Text($"Nearest star: {s.ClassLetter}  #{s.Id:X}");
            ImGui.Text($"  {s.Temperature:0} K   lum {s.Luminosity:0.00} Lsun");
            ImGui.Text($"  Distance: {nLy:0.0000} ly");
            if (nLy < 0.5)
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.5f, 1f),
                    "  >> within 0.5 ly (solar system range)");
        }
        else
        {
            ImGui.Text("Nearest star: (none in range)");
        }

        ImGui.Separator();
        if (_systemManager.HasActive)
        {
            SolarSystem sys = _systemManager.Active!;
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.7f, 1f),
                $"SYSTEM ACTIVE — {sys.Sun.ClassLetter}-class, {sys.Planets.Length} planet(s)");
            double appScale = _paused ? _savedTimeScale : _systemManager.TimeScale;
            double fastestC = sys.MaxOrbitalSpeedMps * _systemManager.TimeScale / MathUtil.SpeedOfLight;
            ImGui.Text($"Time x{appScale:0} {(_paused ? "(PAUSED)" : "")} — fastest orbit {fastestC:0.000} c  [',' '.' 'P']");
            ImGui.Text($"Sim time: {_systemManager.SimTime / 86400.0:0.0} days");
            foreach (Planet pl in sys.Planets)
            {
                double dist = pl.CurrentPosition.DistanceTo(_camera.Position);
                string moons = pl.Moons.Length > 0 ? $"  ({pl.Moons.Length} moons)" : "";
                ImGui.Text($"  {pl.Designation}  {pl.Type,-9} a={pl.SemiMajorAxis / MathUtil.AstronomicalUnit:0.00} AU  {FormatDistance(dist)}{moons}");
            }

            if (_terrainTarget != null)
            {
                double alt = _terrainTarget.CurrentPosition.DistanceTo(_camera.Position) - _terrainTarget.RadiusMeters;
                ImGui.Separator();
                if (_driving)
                {
                    ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f),
                        $"DRIVING {_terrainTarget.Designation} — {_rover.SpeedKph:0} km/h");
                    ImGui.Text("  W/S throttle | A/D steer | Space brake | mouse orbit | R exit");
                }
                else
                {
                    ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.9f, 1f, 1f),
                        $"LANDING {_terrainTarget.Designation} — altitude {FormatDistance(Math.Max(0, alt))}");
                    if (alt < Math.Max(2000.0, _terrainTarget.RadiusMeters * 0.002))
                        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.85f, 0.4f, 1f),
                            "  >> press R to drive the rover");
                }
                ImGui.Text($"  terrain patches: {_terrainRenderer.PatchCount}  (drawn {_terrainRenderer.LeafCount})");
            }
        }
        else
        {
            ImGui.Text("System: none (fly within 0.5 ly of a star)");
        }

        ImGui.Separator();
        ImGui.Text(_mouseCaptured ? "Mouse: captured (Tab to release)" : "Mouse: free (Tab to capture)");
        ImGui.Text(_driving
            ? "DRIVE: W/S throttle | A/D steer | Space brake | R exit | Esc quit"
            : "WASD move | Q/E down/up | Z/C roll | wheel speed/throttle | R drive | Esc quit");
        ImGui.End();
    }

    private static void DrawTuning()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(990, 10), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(280, 560), ImGuiCond.FirstUseEver);
        ImGui.Begin("Tuning  [live]", ImGuiWindowFlags.NoNav);

        // Terrain/biome colours are baked into the patch meshes, so any change to them needs a
        // rebuild; atmosphere is read live by the shader and needs none.
        bool terrainDirty = false;

        if (ImGui.CollapsingHeader("Atmosphere", ImGuiTreeNodeFlags.DefaultOpen))
        {
            AtmosphereRenderer a = _atmosphereRenderer;
            ImGui.Checkbox("Render atmosphere", ref a.Enabled);
            ImGui.Checkbox("Debug: transmittance", ref a.DebugTransmittance);
            ImGui.SliderFloat("Sun intensity", ref a.SunIntensity, 0f, 60f);
            ImGui.SliderFloat("Exposure", ref a.Exposure, 0.1f, 4f);
            ImGui.SliderFloat("Rayleigh", ref a.RayleighStrength, 0f, 4f);
            ImGui.SliderFloat("Mie", ref a.MieStrength, 0f, 2f);
            ImGui.SliderFloat("Mie g (haze)", ref a.MieG, -0.95f, 0.95f);
            ImGui.SliderFloat("Shell height", ref a.HeightScale, 0.2f, 4f);
            if (ImGui.Button("Reset atmosphere"))
            {
                a.SunIntensity = 20f; a.Exposure = 1.2f; a.RayleighStrength = 0.45f;
                a.MieStrength = 0.16f; a.MieG = 0.76f; a.HeightScale = 1.0f;
            }
        }

        if (ImGui.CollapsingHeader("Galaxy backdrop", ImGuiTreeNodeFlags.DefaultOpen))
        {
            GalaxyBackdrop b = _backdrop;
            ImGui.Checkbox("Render backdrop", ref b.Enabled);
            ImGui.SliderFloat("Band glow", ref b.BandBrightness, 0f, 2f);
            ImGui.SliderFloat("Distant stars", ref b.StarBrightness, 0f, 3f);
            if (ImGui.Button("Reset backdrop"))
            {
                b.Enabled = true; b.BandBrightness = 0.6f; b.StarBrightness = 1.0f;
            }
        }

        // Choose what the terrain/biome sliders edit: the global defaults, or a per-type override.
        int idx = Math.Max(0, Array.IndexOf(EditableTypes, _editType));
        if (ImGui.Combo("Editing", ref idx, EditableTypeNames, EditableTypeNames.Length))
            _editType = EditableTypes[idx];
        PlanetTuning.Profile prof = PlanetTuning.For(_editType);
        bool ov = prof.Enabled;
        if (ImGui.Checkbox($"Override for {_editType}", ref ov)) { prof.Enabled = ov; terrainDirty = true; }
        ImGui.TextDisabled(ov
            ? $"editing {_editType}-only override"
            : "editing global defaults (all types without an override)");

        // Bind each control to the override profile when enabled, else the global default.
        if (ImGui.CollapsingHeader("Terrain", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ref float relief = ref (ov ? ref prof.ReliefScale : ref TerrainTuning.ReliefScale);
            ref float mountain = ref (ov ? ref prof.MountainScale : ref TerrainTuning.MountainScale);
            ref float freq = ref (ov ? ref prof.FrequencyScale : ref TerrainTuning.FrequencyScale);
            terrainDirty |= ImGui.SliderFloat("Relief scale", ref relief, 0.1f, 5f);
            terrainDirty |= ImGui.SliderFloat("Mountains", ref mountain, 0f, 2f);
            terrainDirty |= ImGui.SliderFloat("Frequency", ref freq, 0.3f, 3f);
            if (ImGui.Button("Reset terrain"))
            {
                relief = 1f; mountain = 1f; freq = 1f;
                terrainDirty = true;
            }
        }

        if (ImGui.CollapsingHeader("Biome / colour", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ref float snow = ref (ov ? ref prof.SnowLine : ref BiomeTuning.SnowLine);
            ref float cliffT = ref (ov ? ref prof.CliffThreshold : ref BiomeTuning.CliffThreshold);
            ref float cliffS = ref (ov ? ref prof.CliffStrength : ref BiomeTuning.CliffStrength);
            ref Vector3D<float> tint = ref (ov ? ref prof.LowlandTint : ref BiomeTuning.LowlandTint);
            ref Vector3D<float> rock = ref (ov ? ref prof.RockColor : ref BiomeTuning.RockColor);
            ref Vector3D<float> snowC = ref (ov ? ref prof.SnowColor : ref BiomeTuning.SnowColor);
            ref Vector3D<float> cliffC = ref (ov ? ref prof.CliffColor : ref BiomeTuning.CliffColor);
            terrainDirty |= ImGui.SliderFloat("Snow line", ref snow, 0.05f, 0.95f);
            terrainDirty |= ImGui.SliderFloat("Cliff threshold", ref cliffT, 0.4f, 0.95f);
            terrainDirty |= ImGui.SliderFloat("Cliff strength", ref cliffS, 0f, 1f);
            terrainDirty |= ColorEdit3("Lowland tint", ref tint);
            terrainDirty |= ColorEdit3("Rock", ref rock);
            terrainDirty |= ColorEdit3("Snow", ref snowC);
            terrainDirty |= ColorEdit3("Cliff", ref cliffC);
            if (ImGui.Button("Reset biome"))
            {
                snow = 0.55f; cliffT = 0.685f; cliffS = 0.85f;
                tint = new Vector3D<float>(1f, 1f, 1f);
                rock = new Vector3D<float>(0.42f, 0.38f, 0.33f);
                snowC = new Vector3D<float>(0.92f, 0.94f, 0.98f);
                cliffC = new Vector3D<float>(0.30f, 0.27f, 0.24f);
                terrainDirty = true;
            }
        }

        if (terrainDirty) _terrainRenderer.Rebuild();
        if (_terrainTarget == null)
            ImGui.TextDisabled("(fly down to a planet to see terrain)");

        ImGui.Separator();
        if (ImGui.Button("Save settings"))
            _tuningStatus = TuningConfig.Save(_atmosphereRenderer, _backdrop, TuningPath)
                ? $"Saved {TuningPath}" : "Save failed";
        ImGui.SameLine();
        if (ImGui.Button("Reload"))
        {
            _tuningStatus = TuningConfig.Load(_atmosphereRenderer, _backdrop, TuningPath)
                ? $"Loaded {TuningPath}" : "No saved file";
            _terrainRenderer.Rebuild();
        }
        if (_tuningStatus.Length > 0)
            ImGui.TextDisabled(_tuningStatus);

        ImGui.End();
    }

    /// <summary>ImGui colour swatch bound to a Silk <see cref="Vector3D{T}"/>. Returns true on edit.</summary>
    private static bool ColorEdit3(string label, ref Vector3D<float> c)
    {
        var v = new System.Numerics.Vector3(c.X, c.Y, c.Z);
        if (!ImGui.ColorEdit3(label, ref v)) return false;
        c = new Vector3D<float>(v.X, v.Y, v.Z);
        return true;
    }

    private static string FormatSpeed(double mps)
    {
        if (mps >= 0.1 * MathUtil.LightYear)
            return $"{mps / MathUtil.LightYear:0.000} ly/s";
        if (mps >= 0.1 * MathUtil.AstronomicalUnit)
            return $"{mps / MathUtil.AstronomicalUnit:0.000} AU/s";
        if (mps >= 0.1 * MathUtil.SpeedOfLight)
            return $"{mps / MathUtil.SpeedOfLight:0.00} c";
        if (mps >= 1000) return $"{mps / 1000:0.0} km/s";
        return $"{mps:0.0} m/s";
    }

    private static string FormatDistance(double meters)
    {
        if (meters >= 0.1 * MathUtil.LightYear) return $"{meters / MathUtil.LightYear:0.000} ly";
        if (meters >= 0.01 * MathUtil.AstronomicalUnit) return $"{meters / MathUtil.AstronomicalUnit:0.000} AU";
        if (meters >= 1000) return $"{meters / 1000:0.0} km";
        return $"{meters:0} m";
    }

    private static Planet? NearestSurfacedPlanet()
    {
        if (!_systemManager.HasActive) return null;
        Planet? best = null;
        double bestD = double.MaxValue;
        foreach (Planet p in _systemManager.Active!.Planets)
        {
            if (p.Type is PlanetType.GasGiant or PlanetType.IceGiant) continue;
            double d = p.CurrentPosition.DistanceTo(_camera.Position);
            if (d < bestD) { bestD = d; best = p; }
        }
        return best;
    }

    private static Planet? PickTerrainTarget()
    {
        Planet? p = NearestSurfacedPlanet();
        if (p == null) return null;
        double d = p.CurrentPosition.DistanceTo(_camera.Position);
        return d < p.RadiusMeters * TerrainActivateRadii ? p : null;
    }

    private static bool Edge(Key key, ref bool prev)
    {
        bool now = _window.Keyboard.IsKeyPressed(key);
        bool edge = now && !prev;
        prev = now;
        return edge;
    }

    private static void SetMouseCaptured(bool captured)
    {
        _mouseCaptured = captured;
        _window.Mouse.Cursor.CursorMode = captured ? CursorMode.Raw : CursorMode.Normal;
        UpdateInputModes();
    }

    /// <summary>Route mouse-look to whichever controller owns the camera (free-fly vs rover orbit).</summary>
    private static void UpdateInputModes()
    {
        _controller.MouseLookEnabled = _mouseCaptured && !_driving;
        _rover.Active = _driving && _mouseCaptured;
    }
}
