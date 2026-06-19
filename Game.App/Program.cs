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
    // The lattice blocks near the camera are drawn every frame (brightness, not a bubble, culls the
    // far ones). This is only how far out we gather nearby stars for HUD labels, system spawning, and
    // the speed limit — kept well inside one block so the scan stays cheap.
    private static double TrackRadiusLy = 25.0;
    private const int AsteroidFieldRadiusCells = 2; // deep-space clusters within ~1.5 ly
    private const ulong WorldSeed = 0xA11CE5EEDUL;

    private static GL _gl = null!;
    private static Camera _camera = null!;
    private static FreeFlyController _controller = null!;
    private static StarCatalogPager _starPager = null!;
    private static StarRenderer _starRenderer = null!;
    private static GalaxyBackdrop _backdrop = null!;
    private static StarOverlay _overlay = null!;
    private static SolarSystemManager _systemManager = null!;
    private static SystemRenderer _systemRenderer = null!;
    private static AsteroidFieldManager _asteroidFields = null!;
    private static AsteroidRenderer _asteroidRenderer = null!;
    private static readonly List<AsteroidInstance> _asteroidScratch = new();
    private static PlanetGlowRenderer _planetGlow = null!;
    private static GalacticGridRenderer _galacticGrid = null!;
    private static BlackHoleRenderer _blackHole = null!;
    private static AtmosphereRenderer _atmosphereRenderer = null!;
    private static CloudRenderer _cloudRenderer = null!;
    private static PlanetTerrainRenderer _terrainRenderer = null!;
    private static VegetationRenderer _vegetation = null!;
    private static PlanetSurfaceMap _surfaceMap = null!;
    private static ulong _mapBodyId = ulong.MaxValue;
    private static RoverController _rover = null!;
    private static RoverRenderer _roverRenderer = null!;
    private static SceneFramebuffer _sceneFbo = null!;
    private static ColorTarget _postFbo = null!;
    private static BloomRenderer _bloom = null!;
    private static CelestialBody? _terrainTarget;
    private static bool _driving;
    private static bool _prevR;
    private static ImGuiController _imgui = null!;

    private const double TerrainActivateRadii = 40.0; // switch to terrain LOD within N planet radii (~50,000 km for a small world)
    private const double ScanRangeRadii = 80.0;       // scanner reaches a body within N of its radii
    private const string TuningPath = "tuning.json";
    private static string _tuningStatus = "";

    // Tuning panel: which surface type the terrain/biome sliders currently edit.
    private static readonly PlanetType[] EditableTypes =
        { PlanetType.Lava, PlanetType.Rocky, PlanetType.Desert, PlanetType.Ocean, PlanetType.Ice };
    private static readonly string[] EditableTypeNames = { "Lava", "Rocky", "Desert", "Ocean", "Ice" };
    private static PlanetType _editType = PlanetType.Rocky;
    private static CelestialBody? _prevEditTarget;

    private static GameWindow _window = null!;

    private static bool _mouseCaptured = true;
    private static bool _prevTab;
    private static bool _prevComma, _prevPeriod, _prevP, _prevSpace;
    private static bool _prevF;
    private static bool _prevH;
    private static bool _hudVisible = true; // 'H' toggles all on-screen UI (reticles + panels)
    private static float _eclipse;          // this frame's solar-eclipse coverage on the focused body [0,1]
    private static bool _prevB, _prevJ;
    private static string _jumpStatus = "";
    private static bool _scannerOpen;
    private static string _starSearch = "";
    private static Star _searchTarget;
    private static bool _hasSearchTarget;
    private static string _searchStatus = "";
    private static bool _galacticGridVisible;
    private static bool _paused;
    private static double _savedTimeScale;
    private static double _fps;
    // Rolling FPS over the last N frames (averaged as frames ÷ total time, not a mean of per-frame
    // rates, so it isn't skewed by the odd very-short/long frame). _frameTimes is a ring of recent dt.
    private const int FpsWindow = 120;
    private static readonly double[] _frameTimes = new double[FpsWindow];
    private static int _frameTimeIdx, _frameTimeCount;
    private static double _frameTimeSum;
    private static double _renderClock;
    private static bool _smoke;
    private static int _smokeFrames;

    private static void Main(string[] args)
    {
        _smoke = Array.IndexOf(args, "--smoke") >= 0;

        _window = new GameWindow("DeepSpaceEngine", 1920, 1080, fullscreen: true);
        _window.Load += OnLoad;
        _window.Update += OnUpdate;
        _window.Render += OnRender;
        _window.Resize += size =>
        {
            _camera.AspectRatio = size.Y == 0 ? 1f : (float)size.X / size.Y;
            _sceneFbo.Resize(size.X, size.Y);
            _postFbo.Resize(size.X, size.Y);
            _bloom.Resize(size.X, size.Y);
            _cloudRenderer.Resize(size.X, size.Y);
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

        // Open on a framed, oblique view of the galactic-centre black hole rather than starting
        // inside its event horizon (the origin). Pitch down a little, then back off along the
        // resulting forward axis so the camera looks straight at the origin from above the disk.
        _camera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, -0.5f);
        Vector3D<float> fwd = _camera.Forward;
        const double startDist = 1.4e15; // ~0.15 ly — frames the accretion disk
        _camera.Position = UniversePosition.Origin.Translated(
            new Vector3D<double>(-fwd.X * startDist, -fwd.Y * startDist, -fwd.Z * startDist));

        _starPager = new StarCatalogPager(new GalaxyModel(WorldSeed));
        _starRenderer = new StarRenderer(_gl);
        _backdrop = new GalaxyBackdrop(_gl, WorldSeed);
        _overlay = new StarOverlay();
        _systemManager = new SolarSystemManager();
        _systemRenderer = new SystemRenderer(_gl);
        _asteroidFields = new AsteroidFieldManager(WorldSeed);
        _asteroidRenderer = new AsteroidRenderer(_gl);
        _planetGlow = new PlanetGlowRenderer(_gl);
        _galacticGrid = new GalacticGridRenderer(_gl);
        _blackHole = new BlackHoleRenderer(_gl);
        _atmosphereRenderer = new AtmosphereRenderer(_gl);
        _cloudRenderer = new CloudRenderer(_gl);
        _terrainRenderer = new PlanetTerrainRenderer(_gl);
        _vegetation = new VegetationRenderer(_gl);
        _surfaceMap = new PlanetSurfaceMap(_gl);
        _rover = new RoverController(_camera, _window.Keyboard, _window.Mouse, _terrainRenderer);
        _roverRenderer = new RoverRenderer(_gl);
        _sceneFbo = new SceneFramebuffer(_gl);
        _postFbo = new ColorTarget(_gl);
        _bloom = new BloomRenderer(_gl);
        var fb = _window.Window.FramebufferSize;
        _sceneFbo.Resize(fb.X, fb.Y);
        _postFbo.Resize(fb.X, fb.Y);
        _bloom.Resize(fb.X, fb.Y);
        _cloudRenderer.Resize(fb.X, fb.Y);
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
            _starPager.Update(_camera.Position, TrackRadiusLy * MathUtil.LightYear);
            if (_starPager.HasNearest)
                _camera.Position = _starPager.Nearest.Position
                    .Translated(new Vector3D<double>(0.3 * MathUtil.LightYear, 0, 0));
        }
        if (_smoke && _smokeFrames == 1 && _systemManager.HasActive)
        {
            // Then drop in near a surfaced body to exercise the terrain path.
            CelestialBody? p = NearestSurfacedBody();
            if (p != null)
                _camera.Position = p.CurrentPosition.Translated(new Vector3D<double>(p.RadiusMeters * 3, 0, 0));
        }

        if (Edge(Key.Tab, ref _prevTab)) SetMouseCaptured(!_mouseCaptured);

        // 'F' toggles the scanner panel (detailed readout for the nearest body in range).
        if (Edge(Key.F, ref _prevF)) _scannerOpen = !_scannerOpen;

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

        // 'P' toggles the galactic-plane grid: a faint reference plane through the galaxy centre.
        if (Edge(Key.P, ref _prevP)) _galacticGridVisible = !_galacticGridVisible;

        // 'H' hides/shows the whole HUD (reticles + all panels) for a clean view / screenshots.
        if (Edge(Key.H, ref _prevH)) _hudVisible = !_hudVisible;

        // Navigator shortcuts to actually find the asteroids: 'B' frames the nearest belted system,
        // 'J' jumps into the nearest deep-space asteroid field.
        if (!_driving && Edge(Key.B, ref _prevB)) JumpToBelt();
        if (!_driving && Edge(Key.J, ref _prevJ)) JumpToCluster();

        // Orbit time controls: ',' slower, '.' faster, Space pause. (Edge is read every frame so the
        // toggle stays in sync; the pause only fires in free-flight, since Space is the rover brake.)
        bool spaceEdge = Edge(Key.Space, ref _prevSpace);
        if (!_driving && spaceEdge)
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
        _starPager.Update(_camera.Position, TrackRadiusLy * MathUtil.LightYear);
        _asteroidFields.Update(_camera.Position, AsteroidFieldRadiusCells);
        _systemManager.Update(dt, _camera.Position, _starPager);

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

        UpdateSpeedLimit();

        _terrainTarget = PickTerrainTarget();
        _terrainRenderer.SetBody(_terrainTarget);
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

        // Average the frame rate over the last FpsWindow frames so the readout is steady.
        if (dt > 0)
        {
            _frameTimeSum += dt - _frameTimes[_frameTimeIdx];
            _frameTimes[_frameTimeIdx] = dt;
            _frameTimeIdx = (_frameTimeIdx + 1) % FpsWindow;
            if (_frameTimeCount < FpsWindow) _frameTimeCount++;
            _fps = _frameTimeSum > 0 ? _frameTimeCount / _frameTimeSum : 0;
        }

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
        _renderClock += dt; // real elapsed seconds, for time-animated effects (e.g. ocean swell)
        _imgui.Update((float)dt);

        // Render the scene (stars + system + terrain) into an offscreen buffer so the atmosphere
        // can read its depth and clamp its ray march to the real surface.
        _sceneFbo.Bind();
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Distant galaxy first (writes no depth), so the streamed stars, system, and atmosphere all
        // composite over it and any opaque body occludes the sky behind it.
        _backdrop.Render(_camera);
        // Sync GPU buffers to the resident lattice blocks, then draw them all — skipping the active
        // sun (the system pass renders it precisely; its catalog dot would otherwise sit slightly off
        // when viewed up close).
        _starRenderer.SyncBlocks(_starPager.LoadedBlocks);
        _starRenderer.RenderCatalog(_camera, _systemManager.ActiveStarId);

        // Deep-space asteroid clusters: drawn on the still-clear depth buffer with their own fitted
        // projection so rocks self-occlude. Depth is cleared again below before the system pass, so
        // this stays isolated from the system/terrain depth.
        if (_asteroidRenderer.Enabled)
        {
            _asteroidScratch.Clear();
            _asteroidFields.Collect(_systemManager.SimTime, _camera.Position, 0.1 * MathUtil.LightYear, _asteroidScratch);
            if (_asteroidScratch.Count > 0)
            {
                _gl.Enable(EnableCap.DepthTest);
                Matrix4X4<float> vp = AsteroidRenderer.FitProjection(_camera, _asteroidScratch);
                _asteroidRenderer.Render(_camera, _asteroidScratch, vp, default, hasSun: false, _sceneFbo.Height);
                _gl.Disable(EnableCap.DepthTest);
            }
        }

        // Galactic-centre features at the origin: the optional plane grid, then the supermassive
        // black hole. Drawn now while the depth buffer is still clear; both use their own wide
        // projections, so we clear depth again afterwards to leave the system/terrain passes — and
        // the depth-aware atmosphere that linearises this buffer with their near/far — unaffected.
        if (_galacticGridVisible) _galacticGrid.Render(_camera);
        _blackHole.Render(_camera);
        _gl.Clear((uint)ClearBufferMask.DepthBufferBit);

        if (_systemManager.HasActive)
        {
            // Keep an albedo+normal surface map baked for the nearest surfaced body, so its distant
            // sphere and its near terrain sample one source (a crater seen from orbit is the same one
            // you land in). Re-baked only when the nearest body changes; uploaded on this thread.
            CelestialBody? mapBody = NearestSurfacedBody();
            if (mapBody != null && mapBody.Seed != _mapBodyId)
            {
                _mapBodyId = mapBody.Seed;
                _surfaceMap.Request(new PlanetTerrain(mapBody), mapBody.Seed);
            }
            _surfaceMap.Update();

            _systemRenderer.Render(_camera, _systemManager.Active!, _terrainTarget, _surfaceMap);
            // Distance-scaled glow dots that mark each body from afar and fade as its sphere grows.
            _planetGlow.Render(_camera, _systemManager.Active!, _sceneFbo.Height, _terrainTarget);
            // A brighter glow on the sun itself — its catalog dot is suppressed while the system is
            // active, so this keeps the star reading as a bright point until its sphere resolves.
            Star sun = _systemManager.Active!.Sun;
            _planetGlow.RenderStar(_camera, sun.Position, sun.Color, sun.RadiusMeters, _sceneFbo.Height);

            // Sun-lit asteroids sharing the system's projection + depth (planets occlude them and
            // vice versa): the main belt plus each ringed planet's orbiting ring particles, gathered
            // into one batch and drawn hybrid-LOD. All collected in double precision.
            if (_asteroidRenderer.Enabled)
            {
                SolarSystem sys = _systemManager.Active!;
                double simT = _systemManager.SimTime;
                UniversePosition cam = _camera.Position;
                double maxDist = _systemRenderer.LastFar;

                _asteroidScratch.Clear();
                sys.Belt?.Collect(simT, sys.Sun.Position, cam, maxDist, _asteroidScratch);
                foreach (Planet p in sys.Planets)
                    p.RingRocks?.Collect(simT, p.CurrentPosition, cam, maxDist, _asteroidScratch);

                if (_asteroidScratch.Count > 0)
                {
                    Vector3D<float> sunRel = sys.Sun.Position.ToCameraRelative(cam);
                    Matrix4X4<float> vp = _camera.ViewMatrix * MatrixHelper.PerspectiveGL(
                        _camera.FovRadians, _camera.AspectRatio, _systemRenderer.LastNear, _systemRenderer.LastFar);
                    _gl.Enable(EnableCap.DepthTest);
                    _asteroidRenderer.Render(_camera, _asteroidScratch, vp, sunRel, hasSun: true, _sceneFbo.Height);
                    _gl.Disable(EnableCap.DepthTest);
                }
            }
        }

        _eclipse = 0f; // only the body we're at can be eclipsed (set below); 0 keeps distant skies un-dimmed
        if (_terrainTarget != null)
        {
            SolarSystem sys = _systemManager.Active!;
            Vector3D<float> sunRel = sys.Sun.Position.ToCameraRelative(_camera.Position);
            Vector3D<float> planetRel = _terrainTarget.CurrentPosition.ToCameraRelative(_camera.Position);
            Vector3D<float> sunDir = Vector3D.Normalize(sunRel - planetRel);
            sunDir = _terrainTarget.ApparentSunDir(sunDir, _systemManager.SimTime); // axial-rotation day/night
            _eclipse = EclipseFactor(_terrainTarget, sys);  // a moon/planet occluding the sun → twilight
            _terrainRenderer.Eclipse = _eclipse;

            // Reset depth before the terrain pass: the sun/planets/moons SystemRenderer just drew keep
            // their colour but drop back to the cleared far-depth. They were rendered with the system
            // projection, but the depth-aware atmosphere linearises the buffer with the TERRAIN near/far
            // when landed — reading those system-depths directly would mis-place a sky body (e.g. a moon
            // overhead) right at the atmosphere shell and clip the haze off it. Clearing here makes the
            // atmosphere treat them as background and draw the full air column over them (correct haze),
            // while the terrain writes fresh, correctly-linearisable depth. Their depth was only needed
            // to self-occlude during their own pass; when landed they never sit in front of the terrain.
            _gl.Clear((uint)ClearBufferMask.DepthBufferBit);
            _terrainRenderer.Render(_camera, sunDir, (float)_renderClock, _surfaceMap);

            // Rover over the terrain it just drew — same near/far so it shares the depth buffer and
            // the depth-aware atmosphere composites over it correctly.
            if (_driving)
                _roverRenderer.Render(_camera, _rover.BodyPosition, _rover.Forward, _rover.Up,
                    _rover.WheelTravel, sunDir, _terrainRenderer.LastNear, _terrainRenderer.LastFar);

            // Surface vegetation: tufts instanced over the terrain's own near leaves, sampling their height
            // tiles so they sit exactly on the drawn surface.
            _vegetation.Render(_camera, _terrainTarget, _terrainRenderer, sunDir,
                _terrainRenderer.LastNear, _terrainRenderer.LastFar, (float)_renderClock);
        }

        // Capture the finished scene into a sampleable colour buffer, then composite the depth-aware
        // atmosphere over it there. The atmosphere samples the scene DEPTH (from _sceneFbo), which is
        // not attached to _postFbo, so there's no read/write feedback. Near/far come from whichever
        // projection wrote the dominant geometry's depth (terrain when landed, else system).
        _sceneFbo.BlitColorTo(_postFbo.Fbo);
        if (_systemManager.HasActive)
        {
            _postFbo.Bind();
            float near = _terrainTarget != null ? _terrainRenderer.LastNear : _systemRenderer.LastNear;
            float far = _terrainTarget != null ? _terrainRenderer.LastFar : _systemRenderer.LastFar;
            // Clouds first (depth-occluded by terrain), then atmosphere over them so distant clouds
            // pick up aerial-perspective haze and the sky tints them correctly.
            _cloudRenderer.Render(_camera, _systemManager.Active!, _sceneFbo.DepthTexture, near, far,
                (float)_renderClock, _terrainTarget, _terrainRenderer.ActiveAmplitude, _postFbo);
            _atmosphereRenderer.Render(_camera, _systemManager.Active!, _sceneFbo.DepthTexture, near, far,
                _terrainTarget, _terrainRenderer.ActiveAmplitude, _systemManager.SimTime, _eclipse);
        }

        // Post-process bloom: bright-pass + blur off _postFbo (scene + atmosphere), then composite
        // scene + bloom to the screen. When disabled the composite is a straight copy (intensity 0).
        uint bloomTex = _bloom.Enabled ? _bloom.Render(_postFbo.ColorTexture) : _postFbo.ColorTexture;
        _bloom.Composite(_postFbo.ColorTexture, bloomTex);

        // 'H' hides the entire HUD — reticles and every panel — for an unobstructed view. ImGui still
        // renders (an empty frame) so its begin/end frame stays balanced.
        if (_hudVisible)
        {
            _overlay.Draw(_camera, _starPager, _systemManager, _hasSearchTarget ? _searchTarget : null);
            DrawHud();
            DrawTuning();
            DrawScanner();
        }
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
        ImGui.Text($"Speed: {FormatSpeed(_controller.CurrentSpeed)}");
        if (_controller.SpeedCapped)
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.8f, 0.4f, 1f),
                $"  approach limit — set {FormatSpeed(_controller.DesiredSpeed)} (wheel)");
        else
            ImGui.Text($"  set {FormatSpeed(_controller.DesiredSpeed)}  (exp {_controller.SpeedExponent:0.0})");
        ImGui.Separator();

        ImGui.Text($"Stars rendered: {_starRenderer.LastDrawn:N0}");
        ImGui.Text($"Catalog stars:  {_starPager.LoadedStarCount:N0} in {_starPager.LoadedBlockCount} blocks  ({_starPager.Visible.Count} near)");
        int rocks = _asteroidRenderer.LastRocks, sprites = _asteroidRenderer.LastSprites;
        if (rocks + sprites > 0)
            ImGui.Text($"Asteroids:      {rocks} rocks / {sprites} dots");
        ImGui.Separator();

        if (_starPager.HasNearest)
        {
            var s = _starPager.Nearest;
            double nLy = _starPager.NearestDistanceMeters / MathUtil.LightYear;
            ImGui.Text($"Nearest star: {s.ClassLetter}  #{s.Id}");
            ImGui.Text($"  {s.Temperature:0} K   lum {s.Luminosity:0.00} Lsun");
            ImGui.Text($"  Distance: {nLy:0.0000} ly");
            if (nLy < _systemManager.SpawnLightYears)
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.5f, 1f),
                    $"  >> within {_systemManager.SpawnLightYears:0.###} ly (solar system range)");
        }
        else
        {
            ImGui.Text("Nearest star: (none in range)");
        }

        ImGui.Separator();
        ImGui.Text("Find star (catalog #):");
        ImGui.SetNextItemWidth(90);
        bool submit = ImGui.InputText("##starsearch", ref _starSearch, 20,
            ImGuiInputTextFlags.EnterReturnsTrue | ImGuiInputTextFlags.CharsDecimal);
        ImGui.SameLine();
        if (ImGui.Button("Find")) submit = true;
        if (submit) FindStar();
        if (_hasSearchTarget)
        {
            if (ImGui.Button("Go to it")) GoToStar();
            ImGui.SameLine();
            if (ImGui.Button("Clear")) { _hasSearchTarget = false; _searchStatus = ""; }
        }
        if (_searchStatus.Length > 0)
            ImGui.TextColored(_hasSearchTarget
                ? new System.Numerics.Vector4(1f, 0.84f, 0.35f, 1f)
                : new System.Numerics.Vector4(1f, 0.6f, 0.5f, 1f), _searchStatus);

        ImGui.Separator();
        if (_systemManager.HasActive)
        {
            SolarSystem sys = _systemManager.Active!;
            ImGui.TextColored(new System.Numerics.Vector4(0.5f, 1f, 0.7f, 1f),
                $"SYSTEM ACTIVE — {sys.Sun.ClassLetter}-class, {sys.Planets.Length} planet(s)");
            double appScale = _paused ? _savedTimeScale : _systemManager.TimeScale;
            double fastestC = sys.MaxOrbitalSpeedMps * _systemManager.TimeScale / MathUtil.SpeedOfLight;
            ImGui.Text($"Time x{appScale:0} {(_paused ? "(PAUSED)" : "")} — fastest orbit {fastestC:0.000} c  [',' '.' Space]");
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
                double finestPatch = _terrainRenderer.MinDrawnWorldSize;
                string finestStr = finestPatch >= 1e12 ? "—" : $"{finestPatch:0} m"; // sentinel when none drawn
                ImGui.Text($"  vegetation: {_vegetation.Count} tufts  ({_terrainRenderer.GrassLeaves.Count} near leaves, finest patch {finestStr})");
            }
        }
        else
        {
            ImGui.Text($"System: none (fly within {_systemManager.SpawnLightYears:0.###} ly of a star)");
        }

        ImGui.Separator();
        ImGui.Text(_mouseCaptured ? "Mouse: captured (Tab to release)" : "Mouse: free (Tab to capture)");
        ImGui.Text(_driving
            ? "DRIVE: W/S throttle | A/D steer | Space brake | R exit | H hud | Esc quit"
            : "WASD move | Q/E roll | wheel speed | R drive | F scan | P grid | B belt | J field | H hud | Esc quit");
        if (_jumpStatus.Length > 0)
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.7f, 1f), _jumpStatus);
        ImGui.End();
    }

    /// <summary>Look a star up by its global catalog id (the number shown on HUD reticles) and flag
    /// it. The id is stable forever; the pager loads its lattice block on demand so any star in the
    /// universe can be found, not just resident ones. The marker persists once set.</summary>
    private static void FindStar()
    {
        string q = _starSearch.Trim();
        if (q.Length == 0) { _hasSearchTarget = false; _searchStatus = ""; return; }

        if (!ulong.TryParse(q, out ulong id) || !_starPager.TryGetStar(id, out Star star))
        {
            _hasSearchTarget = false;
            _searchStatus = $"No star #{q}";
            return;
        }

        _searchTarget = star;
        _hasSearchTarget = true;
        double ly = star.Position.DistanceTo(_camera.Position) / MathUtil.LightYear;
        _searchStatus = $"#{star.Id}: {star.ClassLetter}-class, {ly:0.000} ly — Go to it";
    }

    /// <summary>Jump the camera to frame the current search target, a few AU out and looking at it
    /// (close enough that its solar system spawns), mirroring the belt/cluster jumps.</summary>
    private static void GoToStar()
    {
        if (!_hasSearchTarget) return;

        _camera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, -0.3f);
        Vector3D<float> fwd = _camera.Forward;
        double dist = 8.0 * MathUtil.AstronomicalUnit; // well inside the system-spawn range
        _camera.Position = _searchTarget.Position.Translated(
            new Vector3D<double>(-fwd.X * dist, -fwd.Y * dist, -fwd.Z * dist));
        double ly = _searchTarget.Position.DistanceTo(_camera.Position) / MathUtil.LightYear;
        _jumpStatus = $"Arrived at star #{_searchTarget.Id} ({_searchTarget.ClassLetter}-class)";
        _searchStatus = $"#{_searchTarget.Id}: {_searchTarget.ClassLetter}-class, {ly:0.000} ly";
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
            ImGui.SliderFloat("Nebulae", ref b.NebulaBrightness, 0f, 2f);
            if (ImGui.Button("Reset backdrop"))
            {
                b.Enabled = true; b.BandBrightness = 0.6f; b.StarBrightness = 1.0f;
                b.NebulaBrightness = 0.7f;
            }
        }

        if (ImGui.CollapsingHeader("Star field", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Resident lattice blocks are drawn every frame; brightness (inverse-square) decides how
            // many read as points vs. fade into the background haze. The lattice is effectively
            // unbounded — blocks page in/out as you fly.
            ImGui.TextDisabled($"{_starPager.LoadedStarCount:N0} stars in {_starPager.LoadedBlockCount} blocks ({StarCatalog.BlockSideLy:0} ly each)");
            ImGui.SliderFloat("Brightness", ref _starRenderer.CatBrightScale, 0.1f, 5f);
            // Lower gamma = flatter falloff = distant stars stay visible (more, fainter points).
            ImGui.SliderFloat("Falloff (gamma)", ref _starRenderer.CatGamma, 0.12f, 0.6f);
            ImGui.SliderFloat("Star size", ref _starRenderer.CatSizeScale, 1f, 20f);
            ImGui.SliderFloat("Max size", ref _starRenderer.CatMaxSize, 20f, 300f);
        }

        if (ImGui.CollapsingHeader("Ocean", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Animate water (swell)", ref _terrainRenderer.AnimateWater);
            ImGui.TextDisabled("(off = perfectly flat sea)");
        }

        if (ImGui.CollapsingHeader("Clouds", ImGuiTreeNodeFlags.DefaultOpen))
        {
            CloudRenderer cr = _cloudRenderer;
            ImGui.Checkbox("Render clouds", ref cr.Enabled);
            ImGui.Checkbox("Volumetric (raymarched)", ref cr.Volumetric);
            ImGui.SliderFloat("Coverage", ref cr.Coverage, 0f, 1f);
            ImGui.SliderFloat("Density", ref cr.Density, 0f, 3f);
            ImGui.SliderFloat("Wind speed", ref cr.WindSpeed, 0f, 0.03f);
            if (ImGui.Button("Reset clouds"))
            {
                cr.Enabled = true; cr.Volumetric = false; cr.Coverage = 0.5f; cr.Density = 1.0f; cr.WindSpeed = 0.004f;
            }
            ImGui.TextDisabled(cr.Volumetric
                ? "(volumetric raymarch — expensive)"
                : "(cheap analytic shell layer)");
        }

        if (ImGui.CollapsingHeader("Asteroids", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Checkbox("Render asteroids", ref _asteroidRenderer.Enabled);
            ImGui.Text($"3D rocks: {_asteroidRenderer.LastRocks}");
            ImGui.Text($"Sprites:  {_asteroidRenderer.LastSprites}");
            ImGui.Text($"Clusters in range: {_asteroidFields.VisibleClusterCount}");
            ImGui.TextDisabled("(belt appears in-system; clusters roam deep space)");
        }

        if (ImGui.CollapsingHeader("Bloom", ImGuiTreeNodeFlags.DefaultOpen))
        {
            BloomRenderer bl = _bloom;
            ImGui.Checkbox("Enable bloom", ref bl.Enabled);
            ImGui.SliderFloat("Threshold", ref bl.Threshold, 0f, 1f);
            ImGui.SliderFloat("Knee (soft)", ref bl.Knee, 0f, 1f);
            ImGui.SliderFloat("Intensity", ref bl.Intensity, 0f, 3f);
            ImGui.SliderInt("Iterations", ref bl.Iterations, 1, 12);
            if (ImGui.Button("Reset bloom"))
            {
                bl.Enabled = true; bl.Threshold = 0.75f; bl.Knee = 0.5f;
                bl.Intensity = 0.6f; bl.Iterations = 5;
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

        // GPU tile-generation terrain path (default; uncheck = the CPU worker-pool bake fallback). Toggling
        // rebuilds the active terrain in the chosen mode so the two can be A/B-compared.
        if (ImGui.Checkbox("GPU terrain (default; uncheck for CPU)", ref TerrainTuning.GpuTerrain)) terrainDirty = true;

        // Bind each control to the override profile when enabled, else the global default.
        if (ImGui.CollapsingHeader("Terrain", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ref float relief = ref (ov ? ref prof.ReliefScale : ref TerrainTuning.ReliefScale);
            ref float mountain = ref (ov ? ref prof.MountainScale : ref TerrainTuning.MountainScale);
            ref float freq = ref (ov ? ref prof.FrequencyScale : ref TerrainTuning.FrequencyScale);
            ref float craterDepth = ref (ov ? ref prof.CraterScale : ref TerrainTuning.CraterScale);
            ref float craterDensity = ref (ov ? ref prof.CraterDensity : ref TerrainTuning.CraterDensity);
            terrainDirty |= ImGui.SliderFloat("Relief scale", ref relief, 0.1f, 5f);
            terrainDirty |= ImGui.SliderFloat("Mountains", ref mountain, 0f, 2f);
            terrainDirty |= ImGui.SliderFloat("Frequency", ref freq, 0.3f, 3f);
            terrainDirty |= ImGui.SliderFloat("Crater depth", ref craterDepth, 0f, 3f);
            terrainDirty |= ImGui.SliderFloat("Crater density", ref craterDensity, 0f, 3f);
            terrainDirty |= ImGui.SliderFloat("Crater albedo", ref TerrainTuning.CraterAlbedo, 0f, 2f);
            terrainDirty |= ImGui.SliderFloat("Maria", ref TerrainTuning.MariaStrength, 0f, 1.5f);
            ImGui.SliderFloat("Lava glow", ref TerrainTuning.LavaGlow, 0f, 6f);   // live (no rebuild)
            ImGui.SliderFloat("City lights", ref TerrainTuning.CityGlow, 0f, 5f); // live (no rebuild)
            ImGui.TextDisabled("(craters/maria: airless; lava: lava worlds; city lights: life worlds)");
            if (ImGui.Button("Reset terrain"))
            {
                relief = 1f; mountain = 1f; freq = 1f; craterDepth = 1f; craterDensity = 1f;
                TerrainTuning.CraterAlbedo = 1f; TerrainTuning.MariaStrength = 0.6f;
                TerrainTuning.LavaGlow = 2.5f; TerrainTuning.CityGlow = 1.5f;
                terrainDirty = true;
            }
        }

        if (ImGui.CollapsingHeader("Surface detail (close-up)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // Micro-relief is geometry → needs a rebuild; the detail-normal knobs are read live.
            // LOD distance is read live too: higher resolves the real geometry (and its craters/relief)
            // from a higher altitude, closing the mid-approach detail gap, at the cost of more patches.
            ImGui.SliderFloat("LOD distance", ref TerrainTuning.LodDistanceFactor, 1.5f, 16f);
            terrainDirty |= ImGui.SliderFloat("Micro relief", ref TerrainTuning.MicroDetailScale, 0f, 3f);
            ImGui.SliderFloat("Detail normals", ref TerrainTuning.DetailNormalStrength, 0f, 1.5f);
            ImGui.SliderFloat("Detail fineness", ref TerrainTuning.DetailNormalScale, 0.25f, 4f);
            ImGui.SliderFloat("Detail range", ref TerrainTuning.SurfaceDetailRange, 1f, 16f);
            ImGui.SliderFloat("Detail albedo", ref TerrainTuning.DetailAlbedo, 0f, 0.5f);
            ImGui.SliderFloat("Material detail", ref TerrainTuning.MaterialDetail, 0f, 1.5f);
            ImGui.SliderFloat("Surface specular", ref TerrainTuning.SurfaceSpecular, 0f, 1f);
            ImGui.SliderFloat("Surface ambient", ref TerrainTuning.SurfaceAmbient, 0f, 0.3f);
            ImGui.Checkbox("Surface objects", ref _vegetation.Enabled);
            ImGui.SliderFloat("spawn range (m)##veg", ref TerrainTuning.ScatterRange, 100f, 9000f);
            ImGui.SliderFloat("density##veg", ref _vegetation.Density, 0f, 1f);
            ImGui.SliderFloat("min size (m)##veg", ref _vegetation.MinSize, 0.5f, 40f);
            ImGui.SliderFloat("max size (m)##veg", ref _vegetation.MaxSize, 0.5f, 40f);
            ImGui.Combo("orient##veg", ref _vegetation.Orient, "World up\0Surface normal\0Random\0");
            ImGui.TextDisabled("(objects scatter on the drawn surface; size rolls between min and max)");
            if (ImGui.Button("Reset detail"))
            {
                TerrainTuning.LodDistanceFactor = 4f;
                TerrainTuning.MicroDetailScale = 1f; TerrainTuning.DetailNormalStrength = 0.4f;
                TerrainTuning.DetailNormalScale = 1f; TerrainTuning.DetailAlbedo = 0.12f;
                TerrainTuning.SurfaceDetailRange = 4f;
                TerrainTuning.MaterialDetail = 0.5f; TerrainTuning.SurfaceSpecular = 0.2f;
                TerrainTuning.SurfaceAmbient = 0.10f;
                terrainDirty = true;
            }
        }

        if (ImGui.CollapsingHeader("Orbital relief (from space)", ImGuiTreeNodeFlags.DefaultOpen))
        {
            // All read live by the shader — mountain ranges lit/mottled on the smooth far-LOD mesh.
            ImGui.SliderFloat("Relief shading", ref TerrainTuning.OrbitalReliefStrength, 0f, 3f);
            ImGui.SliderFloat("Relief albedo", ref TerrainTuning.OrbitalReliefAlbedo, 0f, 0.6f);
            ImGui.SliderFloat("Relief fineness", ref TerrainTuning.OrbitalReliefScale, 0.25f, 4f);
            ImGui.TextDisabled("(fades out as you descend and real terrain takes over)");
            if (ImGui.Button("Reset orbital relief"))
            {
                TerrainTuning.OrbitalReliefStrength = 1f;
                TerrainTuning.OrbitalReliefAlbedo = 0.2f;
                TerrainTuning.OrbitalReliefScale = 1f;
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

    /// <summary>
    /// The scanner panel (toggled with F): a detailed readout — class, gravity, temperature,
    /// hydrosphere, and atmosphere/surface composition — for the nearest body once we're close
    /// enough to scan it. Works for planets and moons alike (both now carry the same scan data).
    /// </summary>
    private static void DrawScanner()
    {
        if (!_scannerOpen) return;

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 470), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(330, 0), ImGuiCond.FirstUseEver);
        ImGui.Begin("Scanner  [F]", ImGuiWindowFlags.NoNav);

        if (!_systemManager.HasActive)
        {
            ImGui.TextDisabled("No system in range. Approach a star.");
            ImGui.End();
            return;
        }

        CelestialBody? target = NearestBody(out double dist);
        if (target == null)
        {
            ImGui.TextDisabled("No bodies detected.");
            ImGui.End();
            return;
        }

        double alt = dist - target.RadiusMeters;
        double range = target.RadiusMeters * ScanRangeRadii;
        ImGui.Text($"Nearest: {target.Designation}");
        if (dist > range)
        {
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.7f, 0.4f, 1f),
                $"OUT OF SCAN RANGE - {FormatDistance(Math.Max(0, alt))}");
            ImGui.TextDisabled($"Approach to within {FormatDistance(range)} to scan.");
            ImGui.End();
            return;
        }

        ImGui.Separator();
        ImGui.Text($"Class: {target.Type}");
        if (target.Habitable)
            ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.5f, 1f),
                "HABITABLE - liquid water & breathable air");
        ImGui.Spacing();

        double earthR = target.RadiusMeters / MathUtil.EarthRadiusM;
        ImGui.Text($"Radius: {target.RadiusMeters / 1000:0} km  ({earthR:0.00} Earth radii)");
        ImGui.Text($"Surface gravity: {target.SurfaceGravity:0.00} m/s^2  ({target.SurfaceGravity / 9.80665:0.00} g)");
        ImGui.Text($"Surface temp: {target.SurfaceTempK:0} K  ({target.SurfaceTempK - 273.15:0} C)");
        ImGui.Text($"Hydrosphere: {(target.HasLiquidWater ? "liquid water oceans" : "none")}");
        ImGui.Spacing();

        if (target.AtmosphereComposition.Length > 0)
        {
            string press = target.SurfacePressureBar >= 0.01
                ? $"{target.SurfacePressureBar:0.00} bar"
                : $"{target.SurfacePressureBar * 1000:0.0} mbar";
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.85f, 1f, 1f), $"Atmosphere  ({press})");
            foreach (Constituent c in target.AtmosphereComposition)
                ImGui.Text($"  {c.Name,-18} {c.Fraction * 100:0.0}%");
        }
        else
        {
            ImGui.TextDisabled("Atmosphere: none (vacuum)");
        }
        ImGui.Spacing();

        if (target.SurfaceComposition.Length > 0)
        {
            ImGui.TextColored(new System.Numerics.Vector4(0.9f, 0.8f, 0.6f, 1f), "Surface composition");
            foreach (Constituent c in target.SurfaceComposition)
                ImGui.Text($"  {c.Name,-18} {c.Fraction * 100:0.0}%");
        }
        else
        {
            ImGui.TextDisabled("Surface: no solid surface");
        }

        if (target is Planet pl && pl.Moons.Length > 0)
        {
            ImGui.Spacing();
            ImGui.TextDisabled($"Satellites: {pl.Moons.Length}");
        }

        ImGui.End();
    }

    /// <summary>Nearest body (planet or moon) to the camera, with its centre distance.</summary>
    private static CelestialBody? NearestBody(out double dist)
    {
        CelestialBody? best = null;
        double bestD = double.MaxValue;
        foreach (CelestialBody b in _systemManager.Active!.AllBodies())
        {
            double d = b.CurrentPosition.DistanceTo(_camera.Position);
            if (d < bestD) { bestD = d; best = b; }
        }
        dist = bestD;
        return best;
    }

    /// <summary>Solar-eclipse coverage [0,1] on <paramref name="focus"/>: the largest fraction of the sun's
    /// angular disk any other body (a moon, the parent planet, a sibling) blocks as seen from this world —
    /// 1 = totality. Dims the surface to twilight during an eclipse.</summary>
    private static float EclipseFactor(CelestialBody focus, SolarSystem sys)
    {
        UniversePosition P = focus.CurrentPosition;
        double dSun = P.DistanceTo(sys.Sun.Position);
        if (dSun <= 1.0) return 0f;
        Vector3D<double> toSun = Vector3D.Normalize(sys.Sun.Position.DeltaMeters(P)); // S - P
        double sunAng = Math.Asin(Math.Clamp(sys.Sun.RadiusMeters / dSun, 0.0, 1.0));

        float Cover(CelestialBody b)
        {
            if (ReferenceEquals(b, focus)) return 0f;
            double dB = P.DistanceTo(b.CurrentPosition);
            if (dB <= 1.0 || dB >= dSun) return 0f;             // must sit between this world and the sun
            Vector3D<double> toB = Vector3D.Normalize(b.CurrentPosition.DeltaMeters(P));
            double align = Vector3D.Dot(toB, toSun);
            if (align <= 0.0) return 0f;                        // toward the sun, not behind us
            double bAng = Math.Asin(Math.Clamp(b.RadiusMeters / dB, 0.0, 1.0));
            double sep = Math.Acos(Math.Clamp(align, -1.0, 1.0));
            return (float)Math.Clamp((sunAng + bAng - sep) / (2.0 * sunAng), 0.0, 1.0);
        }

        float ecl = 0f;
        foreach (Planet pl in sys.Planets)
        {
            ecl = Math.Max(ecl, Cover(pl));
            foreach (Moon mn in pl.Moons) ecl = Math.Max(ecl, Cover(mn));
        }
        return ecl;
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

    private static CelestialBody? NearestSurfacedBody()
    {
        if (!_systemManager.HasActive) return null;
        CelestialBody? best = null;
        double bestD = double.MaxValue;
        // Planets and moons both land — moons are never gas/ice giants, so they always have a surface.
        foreach (CelestialBody b in _systemManager.Active!.AllBodies())
        {
            if (!b.HasSurface) continue;
            double d = b.CurrentPosition.DistanceTo(_camera.Position);
            if (d < bestD) { bestD = d; best = b; }
        }
        return best;
    }

    /// <summary>
    /// Recompute the free-fly proximity speed limit for this frame. The cap is the minimum over the
    /// nearest star and every body in the active system; far from all of them it stays infinite
    /// (unlimited). <see cref="SpeedPolicy"/> shapes each body's contribution and the gap feeds the
    /// controller's anti-tunnelling clamp.
    /// </summary>
    private static void UpdateSpeedLimit()
    {
        double cap = double.PositiveInfinity;
        double gap = double.PositiveInfinity;

        void Consider(double centerDistance, double radius, bool isStar)
        {
            double surface = centerDistance - radius;
            double engage = SpeedPolicy.EngageDistance(radius, isStar);
            cap = Math.Min(cap, SpeedPolicy.Cap(surface, engage, SpeedPolicy.RateFor(isStar)));
            if (surface < engage) gap = Math.Min(gap, Math.Max(0.0, surface));
        }

        // The nearest catalog star — present even before its system spawns, so the slowdown begins as
        // you approach the star itself, not only once planets appear.
        if (_starPager.HasNearest)
            Consider(_starPager.NearestDistanceMeters, _starPager.Nearest.RadiusMeters, isStar: true);

        // Planets and moons tighten the limit further the closer you get to one.
        if (_systemManager.HasActive)
            foreach (CelestialBody b in _systemManager.Active!.AllBodies())
                Consider(b.CurrentPosition.DistanceTo(_camera.Position), b.RadiusMeters, isStar: false);

        _controller.SetSpeedContext(cap, gap);
    }

    /// <summary>Frame the nearest belted star's system from an oblique angle, so the asteroid ring is
    /// in view. The system itself spawns on the next update (the new position is well within range).</summary>
    private static void JumpToBelt()
    {
        Star best = default;
        bool found = false;
        double bestSq = double.MaxValue;
        foreach (Star s in _starPager.Visible)
        {
            if (!AsteroidBelt.Rolls(s)) continue;
            double dsq = s.Position.DistanceSquaredTo(_camera.Position);
            if (dsq < bestSq) { bestSq = dsq; best = s; found = true; }
        }
        if (!found) { _jumpStatus = "No belted system in range"; return; }

        SolarSystem sys = SystemGenerator.Generate(best);
        if (sys.Belt is not { } belt) { _jumpStatus = "No belt on nearest candidate"; return; }

        // Same framing the app opens with on the black hole: pitch down, then back off along forward.
        _camera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, -0.5f);
        Vector3D<float> fwd = _camera.Forward;
        double dist = belt.OuterRadius * 1.6;
        _camera.Position = best.Position.Translated(
            new Vector3D<double>(-fwd.X * dist, -fwd.Y * dist, -fwd.Z * dist));
        _jumpStatus = $"Framed belt of {best.Designation} — {belt.Count} rocks (fly in to resolve them)";
    }

    /// <summary>Drop the camera into the nearest deep-space asteroid field, looking through its core.</summary>
    private static void JumpToCluster()
    {
        if (!_asteroidFields.TryFindNearest(_camera.Position, searchCells: 6, out AsteroidField f))
        {
            _jumpStatus = "No asteroid field found within range";
            return;
        }
        _camera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, -0.15f);
        Vector3D<float> fwd = _camera.Forward;
        double back = f.BoundRadius * 0.5; // sit toward one edge so the bulk of the field is ahead
        _camera.Position = f.Center.Translated(
            new Vector3D<double>(-fwd.X * back, -fwd.Y * back, -fwd.Z * back));
        _jumpStatus = $"Dropped into asteroid field — {f.Count} rocks";
    }

    private static CelestialBody? PickTerrainTarget()
    {
        CelestialBody? b = NearestSurfacedBody();
        if (b == null) return null;
        double d = b.CurrentPosition.DistanceTo(_camera.Position);
        return d < b.RadiusMeters * TerrainActivateRadii ? b : null;
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
