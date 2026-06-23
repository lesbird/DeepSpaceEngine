using Engine.Audio;
using Engine.Core;
using Engine.Platform;
using Engine.Rendering;
using Game.Systems;
using Game.Systems.Discovery;
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
    private static GalaxyCatalogPager _galaxyPager = null!;
    private static StarRenderer _starRenderer = null!;
    private static GalaxyRenderer _galaxyRenderer = null!;
    private static GalaxyBackdrop _backdrop = null!;
    private static NebulaField _nebulaField = null!;
    private static NebulaRenderer _nebulaRenderer = null!;
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
    private static ScatterRenderer _scatter = null!;
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

    // Audio: OpenAL subsystem + the resident UI click. Music streams from _audio. Silent if no device.
    private static AudioEngine _audio = null!;
    private static Sound _uiBlip = null!;
    private static bool _musicEnabled = true;
    private const string AudioDir = "Assets/Audio";

    // Discovery: player-side settings, REST transport, and the cache/report service.
    private static DiscoveryConfig _discoveryConfig = null!;
    private static DiscoveryClient _discoveryClient = null!;
    private static DiscoveryService _discovery = null!;
    private static volatile string _discoveryStatus = "";
    private static bool _prevDiscoveryEnabled;
    private const string DiscoveryPath = "discovery.json";
    private static ulong _prevReportedStarId;   // edge-track: last system whose sun we reported
    private static ulong _prevEnvBodySeed;       // edge-track: body whose environment we're inside
    private const double AirlessEnvShell = 0.05; // notional environment shell (× radius) for airless worlds

    private const double TerrainActivateRadii = 40.0; // switch to terrain LOD within N planet radii (~50,000 km for a small world)
    private const double ScanRangeRadii = 80.0;       // scanner reaches a body within N of its radii
    private const string TuningPath = "tuning.json";
    private static string _tuningStatus = "";

    // Camera position persisted across launches: saved on quit, restored on load.
    private const string PositionPath = "position.json";
    private const float HomeSpeedExponent = 15f; // ~0.1 ly/s — roam between stars; wheel to adjust

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
    private static bool _prevM;                          // 'M' toggles the system map
    private static bool _systemMapVisible;               // 2D top-down system map overlay (nearby-stars map when not in a system)
    private static CelestialBody? _systemMapSel;         // selected body in the system map
    private static float _nearbyRangeLy = 10f;           // nearby-stars map radius (slider, 10–50 ly)
    private static Star _nearbySel;                      // selected star in the nearby-stars map
    private static bool _nearbyHasSel;
    private static bool _prevN;                          // 'N' toggles the galaxy/sector map
    private static bool _galaxyMapVisible;               // 2D top-down galaxy map overlay
    private static double _galaxyMpp = 0.3 * MathUtil.LightYear; // galaxy-map scale (metres per pixel)
    private static Vector3D<double> _galaxyPan;          // map-centre offset from the camera (m, XZ plane)
    private static Star _galaxySel;                      // selected star (valid when _galaxyHasSel)
    private static bool _galaxyHasSel;
    private static float _galaxyDragPx;                  // drag accumulated this press (click vs pan)
    private static readonly List<(System.Numerics.Vector2 pos, float r, uint col, Star star)> _galaxyDots = new();
    private static bool _galaxy3DVisible;                // full-screen 3D galaxy map mode
    private static Camera _mapCamera = null!;            // orbit camera for the 3D map
    private static UniversePosition _mapFocus;           // point the 3D map orbits around
    private static float _mapYaw, _mapPitch = 0.5f;      // orbit angles (radians)
    private static double _mapDistLy = 80.0;             // orbit distance (light-years)
    private static readonly List<Star> _mapChain = new(); // in-range stars in greedy nearest-neighbour order
    private static bool _mapChainValid;                  // recompute the chain when the focus moves
    private static float _mapRangeLy = 10f;              // 3D map shows stars within this radius of the focus (slider)
    private static bool _mapPressed;                     // a left-press is in progress on the map background
    private static float _mapDragPx;                     // accumulated drag this press (distinguishes click from orbit)
    private static bool _mapClick;                       // a click (not a drag) was released this frame
    private static ulong _mapFocusStarId;               // 0 = focus is the ship; else the id of the picked centre star
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
        // Persist the camera location so the next launch resumes here. Skipped for the headless smoke
        // run so an automated pass doesn't clobber the player's saved position. Save() swallows any
        // error (e.g. if the window failed to load before the camera was built).
        if (!_smoke)
            PositionConfig.Save(_camera, _controller, PositionPath);
        _audio?.Dispose();
        _discoveryClient?.Dispose();
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
        _controller = new FreeFlyController(_camera, _window.Keyboard, _window.Mouse);

        // Frame the home view (oblique look at the galactic-centre black hole). Restored below if a
        // saved position exists, so a fresh install opens here but returning players resume in place.
        ResetToHome();

        _galaxyPager = new GalaxyCatalogPager(new GalaxyField(WorldSeed));
        _starPager = new StarCatalogPager(_galaxyPager); // stars are confined to the galaxies it streams
        _starRenderer = new StarRenderer(_gl);
        _galaxyRenderer = new GalaxyRenderer(_gl);
        _backdrop = new GalaxyBackdrop(_gl, WorldSeed);
        _nebulaField = new NebulaField(WorldSeed);
        _nebulaRenderer = new NebulaRenderer(_gl);
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
        _scatter = new ScatterRenderer(_gl);
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
        if (TuningConfig.Load(_atmosphereRenderer, _backdrop, _scatter, TuningPath))
        {
            _tuningStatus = $"Loaded {TuningPath}";
            _nebulaField = new NebulaField(WorldSeed); // load may have changed count/radius — rebuild from the new tuning
        }

        // Restore the last quit position so you resume where you left off (overrides the home view).
        PositionConfig.Load(_camera, _controller, PositionPath);

        InitAudio();

        // Discovery: load settings, build the REST client + cache/report service. The launch sync
        // fires from OnUpdate when reporting is enabled (so enabling at runtime syncs too).
        _discoveryConfig = DiscoveryConfig.Load(DiscoveryPath);
        _discoveryClient = new DiscoveryClient(_discoveryConfig.ServerUrl, _discoveryConfig.ApiKey);
        _discovery = new DiscoveryService(_discoveryClient)
        {
            Enabled = _discoveryConfig.Enabled,
            PlayerName = _discoveryConfig.PlayerName,
        };

        SetMouseCaptured(true);
    }

    /// <summary>Bring up the audio engine and prime its assets, then start the soundtrack. Both the UI
    /// click and the music fall back to the procedural synth when no WAV is shipped — music uses
    /// <see cref="Synth.CasualSpace"/>, a calm looping ambient track, so it's pleasant to leave on (and
    /// easily silenced in Tuning ▸ Audio). Music is skipped in headless smoke runs.</summary>
    private static void InitAudio()
    {
        _audio = new AudioEngine();

        // UI click: blip.wav if shipped, otherwise a synthesised one.
        string blipPath = Path.Combine(AudioDir, "blip.wav");
        _uiBlip = _audio.CreateSound(File.Exists(blipPath) ? WavLoader.Load(blipPath) : Synth.Blip());

        _musicEnabled = !_smoke;
        if (_musicEnabled)
            _audio.PlayMusic(LoadMusic(), loop: true);
    }

    /// <summary>The soundtrack clip: Assets/Audio/music.wav if shipped, else the procedural
    /// <see cref="Synth.CasualSpace"/> ambient track.</summary>
    private static AudioClip LoadMusic()
    {
        string musicPath = Path.Combine(AudioDir, "music.wav");
        return File.Exists(musicPath) ? WavLoader.Load(musicPath) : Synth.CasualSpace();
    }

    /// <summary>Click the UI sound (no-op when audio is unavailable).</summary>
    private static void Blip(float pitch = 1f) => _audio.PlaySound(_uiBlip, volume: 0.6f, pitch: pitch);

    /// <summary>Edge-detect the two discovery events each frame: entering a star system (report its
    /// sun) and entering a body's near-surface environment — its atmosphere, or a notional shell for
    /// airless worlds — (report that planet/moon). The service de-dupes, so re-entries are free.</summary>
    private static void ReportDiscoveries()
    {
        if (!_discovery.Enabled) return;

        if (_systemManager.Active is not SolarSystem sys)
        {
            _prevReportedStarId = 0; // left the system → allow the next entry to re-fire
            return;
        }

        // System entry: the active sun.
        if (sys.Sun.Id != _prevReportedStarId)
        {
            _discovery.ReportStar(sys.Sun, new Dictionary<string, object?>
            {
                ["class"] = sys.Sun.ClassLetter.ToString(),
                ["tempK"] = (int)sys.Sun.Temperature,
            });
            _prevReportedStarId = sys.Sun.Id;
        }

        // Environment entry: nearest surfaced body, once inside its (real or notional) shell.
        CelestialBody? b = NearestSurfacedBody();
        if (b == null) return;
        double alt = b.CurrentPosition.DistanceTo(_camera.Position) - b.RadiusMeters;
        double shell = b.RadiusMeters * (b.HasAtmosphere ? b.AtmosphereHeight : AirlessEnvShell);
        if (alt < shell && _prevEnvBodySeed != b.Seed)
        {
            _discovery.ReportBody(sys, b, new Dictionary<string, object?>
            {
                ["type"] = b.Type.ToString(),
                ["hasAtmosphere"] = b.HasAtmosphere,
                ["tempK"] = (int)b.SurfaceTempK,
            });
            _prevEnvBodySeed = b.Seed;
        }
        else if (alt > shell * 1.2 && _prevEnvBodySeed == b.Seed)
        {
            _prevEnvBodySeed = 0; // climbed back out (hysteresis avoids edge flicker)
        }
    }

    /// <summary>Async ping of the discovery server (GET all) to confirm the URL works; updates the
    /// status line shown in the Discovery panel. Runs off-thread so the frame never blocks.</summary>
    private static void TestDiscoveryConnection()
    {
        _discoveryClient.ServerUrl = _discoveryConfig.ServerUrl;
        _discoveryClient.ApiKey = _discoveryConfig.ApiKey;
        _discoveryStatus = "Testing…";
        _ = Task.Run(async () =>
        {
            IReadOnlyList<DiscoveryRecord>? list = await _discoveryClient.GetAllAsync();
            _discoveryStatus = list != null
                ? $"OK — {list.Count} discoveries on server"
                : "Failed — check the URL / server";
        });
    }

    private static void OnUpdate(double dt)
    {
        // Resolve the galaxy lattice first: the star pager confines star generation to whichever galaxy
        // contains/overlaps the camera, so its resident galaxies must be current before it runs (here
        // and in the smoke jump below). Galaxies are enormous, so a frame's motion is negligible.
        _galaxyPager.Update(_camera.Position);

        if (_smoke && _smokeFrames == 0)
        {
            // Headless: jump next to the nearest star so the spawn/generate/render path runs. The
            // offset must be inside SolarSystemManager.SpawnLightYears (0.01 ly) or no system spawns —
            // 0.005 ly lands us in-system so terrain (and discovery reporting) actually exercise.
            _starPager.Update(_camera.Position, TrackRadiusLy * MathUtil.LightYear);
            if (_starPager.HasNearest)
                _camera.Position = _starPager.Nearest.Position
                    .Translated(new Vector3D<double>(0.005 * MathUtil.LightYear, 0, 0));
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
        if (Edge(Key.M, ref _prevM)) { _systemMapVisible = !_systemMapVisible; Blip(); }
        // 'N' opens the 2D galaxy map; if the 3D map is already up, 'N' closes it instead.
        if (Edge(Key.N, ref _prevN))
        {
            if (_galaxy3DVisible) _galaxy3DVisible = false;
            else _galaxyMapVisible = !_galaxyMapVisible;
            Blip();
        }

        // 'H' hides/shows the whole HUD (reticles + all panels) for a clean view / screenshots.
        if (Edge(Key.H, ref _prevH)) _hudVisible = !_hudVisible;

        // Navigator shortcuts to actually find the asteroids: 'B' frames the nearest belted system,
        // 'J' jumps into the nearest deep-space asteroid field.
        if (!_driving && Edge(Key.B, ref _prevB)) { JumpToBelt(); Blip(0.8f); }
        if (!_driving && Edge(Key.J, ref _prevJ)) { JumpToCluster(); Blip(0.8f); }

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

        if (!_driving && !_galaxy3DVisible) _controller.Update(dt); // ship is frozen while orbiting the 3D map
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

        // Audio: orient the listener to the camera (positional sounds are passed camera-relative, so
        // the listener stays at the origin of that frame), then pump the music stream and apply volumes.
        Vector3D<float> f = _camera.Forward, u = _camera.Up;
        _audio.SetListener(
            System.Numerics.Vector3.Zero,
            new System.Numerics.Vector3(f.X, f.Y, f.Z),
            new System.Numerics.Vector3(u.X, u.Y, u.Z));
        _audio.Update(dt);

        // Discovery: keep the service in sync with the config, sync the catalog when reporting is first
        // enabled (covers launch and runtime toggling), then drive its retry queue.
        _discovery.Enabled = _discoveryConfig.Enabled;
        _discovery.PlayerName = _discoveryConfig.PlayerName;
        if (_discoveryConfig.Enabled && !_prevDiscoveryEnabled) _ = _discovery.InitializeAsync();
        _prevDiscoveryEnabled = _discoveryConfig.Enabled;
        ReportDiscoveries();
        _discovery.Update(dt);

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

        // While the cursor is locked for flight, the wheel drives speed — so keep ImGui from also
        // consuming it (which would scroll whatever window the hidden cursor sits over, e.g. the tuning
        // panel). NoMouse makes NewFrame ignore mouse pos/buttons/wheel; cleared once the cursor is freed.
        ImGuiIOPtr io = ImGui.GetIO();
        if (_mouseCaptured) io.ConfigFlags |= ImGuiConfigFlags.NoMouse;
        else io.ConfigFlags &= ~ImGuiConfigFlags.NoMouse;

        _imgui.Update((float)dt);

        // The 3D galaxy map is its own full-screen mode: an orbit camera over the resident star cloud,
        // reusing the catalog star pipeline + bloom. It replaces the normal scene entirely.
        if (_galaxy3DVisible) { RenderGalaxyMap3DFrame(); return; }

        // Render the scene (stars + system + terrain) into an offscreen buffer so the atmosphere
        // can read its depth and clamp its ray march to the real surface.
        _sceneFbo.Bind();
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        // Distant galaxy first (writes no depth), so the streamed stars, system, and atmosphere all
        // composite over it and any opaque body occludes the sky behind it.
        // The painted backdrop (Milky-Way band + dome) is the view from INSIDE the galaxy; fade it out
        // as you leave so it stops drowning the real galaxy sprites in intergalactic space. Full within
        // the galaxy, easing to a dim cosmic floor by ~2× its radius.
        float backdropDim = 0.12f;
        if (_galaxyPager.HasNearest)
        {
            double r = _galaxyPager.Nearest.RadiusMeters;
            double d = _galaxyPager.NearestDistanceMeters;
            double t = r > 0 ? Math.Clamp((2.0 * r - d) / r, 0.0, 1.0) : 0.0;
            backdropDim = (float)(0.12 + 0.88 * t);
        }
        _backdrop.ExternalDim = backdropDim;
        _backdrop.Render(_camera);
        // Other galaxies as bright point sprites (the farthest LOD tier) — additive, direction-only,
        // over the painted backdrop. Skips the galaxy we're inside (its stars stream via the catalog).
        _galaxyRenderer.Render(_camera, _galaxyPager);
        // Nebulae over the backdrop but under the catalog stars — additive, so the stars that fall inside a
        // nebula read as points embedded in its glow.
        _nebulaRenderer.Render(_camera, _nebulaField);
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
            // Eclipse dimming DISABLED: the surface dropped to twilight too abruptly and read as a bug, not a
            // celestial event. Plumbing (uEclipse / AtmosphereRenderer) stays inert with _eclipse pinned at 0.
            // Re-enable with a gradual ramp:  _eclipse = EclipseFactor(_terrainTarget, sys);
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
            _scatter.Render(_camera, _terrainTarget, _terrainRenderer, sunDir,
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
            _overlay.Draw(_camera, _starPager, _systemManager, _discovery, _hasSearchTarget ? _searchTarget : null);
            DrawSpeedOverlay();
            DrawHud();
            DrawTuning();
            DrawScanner();
            DrawSystemMap();
            DrawNearbyMap();
            DrawGalaxyMap();
        }
        _imgui.Render();

        if (_smoke && ++_smokeFrames > 24)
        {
            Console.WriteLine($"[smoke] terrain target: {_terrainTarget?.Designation ?? "none"}, " +
                $"patches: {_terrainRenderer.PatchCount}, drawn: {_terrainRenderer.LeafCount}");
            _window.Window.Close();
        }
    }

    // Heads-up readout at the top-center of the screen, drawn on the foreground draw list so it
    // sits outside (and on top of) any panel: current speed, target (set) speed, and FPS.
    private static void DrawSpeedOverlay()
    {
        var io = ImGui.GetIO();
        System.Numerics.Vector2 vp = io.DisplaySize;
        if (vp.X < 2 || vp.Y < 2) return;

        string speed = $"Speed {FormatSpeed(_controller.ActualSpeed)}";
        string target = $"Target {FormatSpeed(_controller.DesiredSpeed)}";
        string fps = $"FPS {_fps:0}";
        string line = $"{speed}     {target}     {fps}";

        var dl = ImGui.GetForegroundDrawList();
        System.Numerics.Vector2 size = ImGui.CalcTextSize(line);
        var pos = new System.Numerics.Vector2((vp.X - size.X) * 0.5f, 8f);

        // Soft shadow for legibility over bright starfields, then the text.
        dl.AddText(pos + new System.Numerics.Vector2(1, 1), Col(0f, 0f, 0f, 0.78f), line);
        uint color = _controller.SpeedCapped ? Col(1f, 0.8f, 0.4f) : Col(0.92f, 0.94f, 1f);
        dl.AddText(pos, color, line);
    }

    private static void DrawHud()
    {
        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 10), ImGuiCond.Always);
        // Start collapsed (the key flight readouts live in the top-screen overlay); expandable any time.
        ImGui.SetNextWindowCollapsed(true, ImGuiCond.FirstUseEver);
        ImGui.Begin("Navigation",
            ImGuiWindowFlags.AlwaysAutoResize | ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoNav);

        ImGui.Text($"FPS: {_fps:0}");
        ImGui.Separator();

        var p = _camera.Position;
        double distLy = p.DistanceTo(UniversePosition.Origin) / MathUtil.LightYear;
        ImGui.Text($"Sector: {p.Sector.X}, {p.Sector.Y}, {p.Sector.Z}");
        ImGui.Text($"From origin: {distLy:0.000} ly");
        DrawGalaxyStatus();
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
        if (ImGui.Button("Reset to home"))
        {
            Blip();
            ResetToHome();
            _jumpStatus = "Returned to home (galactic centre)";
        }
        ImGui.SameLine();
        ImGui.TextDisabled("(galactic centre)");

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
            if (_discovery.TryGetStar(sys.Sun, out DiscoveryRecord hudSun))
                ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.7f, 1f),
                    $"  discovered by {hudSun.Discoverer} ({hudSun.DiscoveredAtUtc:yyyy-MM-dd})");
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
                ImGui.Text($"  scatter: {_scatter.Count} objects  ({_terrainRenderer.GrassLeaves.Count} near leaves, finest patch {finestStr})");
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

    /// <summary>
    /// One HUD line locating the camera in the universe-of-galaxies hierarchy: the galaxy you're inside
    /// (green) or, in intergalactic space, the nearest galaxy and its distance. Phase-0 readout that
    /// confirms the galaxy lattice resolves (you start inside the Milky Way).
    /// </summary>
    private static void DrawGalaxyStatus()
    {
        if (_galaxyPager.IsInside)
        {
            Galaxy g = _galaxyPager.Containing;
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.7f, 1f),
                $"Galaxy: {g.Name} ({g.Type}, {g.RadiusLy / 1000.0:0.#}k ly r)");
        }
        else if (_galaxyPager.HasNearest)
        {
            Galaxy g = _galaxyPager.Nearest;
            double mly = _galaxyPager.NearestDistanceMeters / MathUtil.LightYear / 1.0e6;
            ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.8f, 1f, 1f),
                $"Intergalactic — nearest {g.Name} ({g.Type}) {mly:0.00} Mly");
        }
        ImGui.Text($"Galaxies: {_galaxyPager.LoadedGalaxyCount} resident, {_galaxyRenderer.LastDrawn} pts, {_galaxyRenderer.LastImpostors} disks");
    }

    /// <summary>
    /// Return the camera to its opening "home" view: an oblique look at the galactic-centre black
    /// hole, backed off along the forward axis so the camera frames the accretion disk from above the
    /// disk rather than starting inside the event horizon (the origin). Also restores the default
    /// roam speed. Used both for the initial frame and the Navigation "Reset to home" button.
    /// </summary>
    private static void ResetToHome()
    {
        _controller.SpeedExponent = HomeSpeedExponent;
        _camera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, -0.5f);
        Vector3D<float> fwd = _camera.Forward;
        const double startDist = 1.4e15; // ~0.15 ly — frames the accretion disk
        _camera.Position = UniversePosition.Origin.Translated(
            new Vector3D<double>(-fwd.X * startDist, -fwd.Y * startDist, -fwd.Z * startDist));
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

        if (ImGui.CollapsingHeader("Audio", ImGuiTreeNodeFlags.DefaultOpen))
        {
            if (!_audio.Available)
            {
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.6f, 0.5f, 1f), "No audio device — silent.");
            }
            else
            {
                float master = _audio.MasterVolume, music = _audio.MusicVolume, sfx = _audio.SfxVolume;
                if (ImGui.SliderFloat("Master", ref master, 0f, 1f)) _audio.MasterVolume = master;
                if (ImGui.SliderFloat("Music", ref music, 0f, 1f)) _audio.MusicVolume = music;
                if (ImGui.SliderFloat("SFX", ref sfx, 0f, 1f)) _audio.SfxVolume = sfx;
                if (ImGui.Checkbox("Music on", ref _musicEnabled))
                {
                    if (_musicEnabled) _audio.PlayMusic(LoadMusic(), loop: true);
                    else _audio.StopMusic();
                }
                ImGui.SameLine();
                if (ImGui.Button("Test SFX")) Blip();
                ImGui.TextDisabled("(drop blip.wav / music.wav in Assets/Audio to replace synth)");
            }
        }

        if (ImGui.CollapsingHeader("Discovery", ImGuiTreeNodeFlags.DefaultOpen))
        {
            DiscoveryConfig dc = _discoveryConfig;

            bool enabled = dc.Enabled;
            if (ImGui.Checkbox("Enable discovery reporting", ref enabled)) dc.Enabled = enabled;
            ImGui.TextDisabled(dc.Enabled
                ? "(stars/planets you reach will be reported)"
                : "(disabled — nothing is sent to the server)");

            string name = dc.PlayerName;
            ImGui.SetNextItemWidth(190);
            if (ImGui.InputText("Player name", ref name, 64)) dc.PlayerName = name;

            string url = dc.ServerUrl;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("Server URL", ref url, 200)) dc.ServerUrl = url;

            string key = dc.ApiKey;
            ImGui.SetNextItemWidth(220);
            if (ImGui.InputText("API key", ref key, 128, ImGuiInputTextFlags.Password)) dc.ApiKey = key;

            if (ImGui.Button("Save"))
            {
                _discoveryClient.ServerUrl = dc.ServerUrl;
                _discoveryClient.ApiKey = dc.ApiKey;
                _discoveryStatus = dc.Save(DiscoveryPath) ? $"Saved {DiscoveryPath}" : "Save failed";
            }
            ImGui.SameLine();
            if (ImGui.Button("Test connection")) TestDiscoveryConnection();
            ImGui.SameLine();
            if (ImGui.Button("Re-sync"))
            {
                _discoveryClient.ServerUrl = dc.ServerUrl;
                _discoveryClient.ApiKey = dc.ApiKey;
                _ = _discovery.InitializeAsync();
            }
            if (_discoveryStatus.Length > 0) ImGui.TextDisabled(_discoveryStatus);
            ImGui.TextDisabled($"Sync: {_discovery.State} — {_discovery.Count} known");
        }

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
            ImGui.Separator();
            // Fly-to nebulae (the placed NebulaField clouds, distinct from the painted backdrop nebulosity).
            ImGui.Checkbox("Render nebulae (fly-to)", ref _nebulaRenderer.Enabled);
            ImGui.SliderFloat("Nebula glow", ref _nebulaRenderer.Intensity, 0f, 2f);
            // Count / size are generation inputs: regenerate the field when a slider is released.
            bool nebDirty = false;
            ImGui.SliderInt("Nebula count", ref NebulaTuning.Count, 1, 300);
            nebDirty |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SliderFloat("Nebula radius min (ly)", ref NebulaTuning.MinRadiusLy, 10f, 800f);
            nebDirty |= ImGui.IsItemDeactivatedAfterEdit();
            ImGui.SliderFloat("Nebula radius max (ly)", ref NebulaTuning.MaxRadiusLy, 10f, 800f);
            nebDirty |= ImGui.IsItemDeactivatedAfterEdit();
            if (nebDirty) _nebulaField = new NebulaField(WorldSeed);
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
            DrawScatterUI();
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
            _tuningStatus = TuningConfig.Save(_atmosphereRenderer, _backdrop, _scatter, TuningPath)
                ? $"Saved {TuningPath}" : "Save failed";
        ImGui.SameLine();
        if (ImGui.Button("Reload"))
        {
            _tuningStatus = TuningConfig.Load(_atmosphereRenderer, _backdrop, _scatter, TuningPath)
                ? $"Loaded {TuningPath}" : "No saved file";
            _terrainRenderer.Rebuild();
            _nebulaField = new NebulaField(WorldSeed); // pick up any loaded count/radius change
        }
        if (_tuningStatus.Length > 0)
            ImGui.TextDisabled(_tuningStatus);

        ImGui.End();
    }

    /// <summary>
    /// The scanner panel (toggled with F): a detailed readout — class, gravity, temperature,
    /// hydrosphere, and atmosphere/surface composition — for the nearest body once we're close
    /// enough to scan it. Works for planets and moons alike (both now carry the same scan data).
    /// When no body is within scan range it falls back to a wider readout: the star + system if a
    /// system is active, otherwise the sector (position + nearest star).
    /// </summary>
    /// <summary>Render a discovery-credit line: who found this object and when, or "Undiscovered"
    /// while reporting is enabled (nothing at all when discovery is off, to avoid clutter).</summary>
    private static void DiscoveryLine(bool discovered, DiscoveryRecord? rec)
    {
        if (discovered && rec != null)
            ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.7f, 1f),
                $"Discovered by {rec.Discoverer} on {rec.DiscoveredAtUtc:yyyy-MM-dd} UTC");
        else if (_discoveryConfig.Enabled)
            ImGui.TextDisabled("Undiscovered");
    }

    private static void DrawScanner()
    {
        if (!_scannerOpen) return;

        ImGui.SetNextWindowPos(new System.Numerics.Vector2(10, 470), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(330, 0), ImGuiCond.FirstUseEver);
        ImGui.Begin("Scanner  [F]", ImGuiWindowFlags.NoNav);

        if (!_systemManager.HasActive)
        {
            // Out in interstellar space — no system to scan. Report the sector instead.
            DrawSectorScan();
            ImGui.End();
            return;
        }

        CelestialBody? target = NearestBody(out double dist);
        // No body within scan range → widen the scan to the star + system rather than a dead end.
        if (target == null || dist > target.RadiusMeters * ScanRangeRadii)
        {
            DrawSystemScan(_systemManager.Active!, target, dist);
            ImGui.End();
            return;
        }

        ImGui.Text($"Nearest: {target.Designation}");
        bool bodyFound = _discovery.TryGetBody(_systemManager.Active!, target, out DiscoveryRecord bodyRec);
        DiscoveryLine(bodyFound, bodyFound ? bodyRec : null);
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

    /// <summary>Scanner fallback when a system is active but no body is in scan range: read out the
    /// star and its system (the nearest body, if any, is noted with how close to approach to scan it).</summary>
    private static void DrawSystemScan(SolarSystem sys, CelestialBody? nearest, double nearestDist)
    {
        Star sun = sys.Sun;
        ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.5f, 1f), $"STAR SCAN — {sun.Designation}");
        bool sunFound = _discovery.TryGetStar(sun, out DiscoveryRecord sunRec);
        DiscoveryLine(sunFound, sunFound ? sunRec : null);
        ImGui.Separator();
        ImGui.Text($"Class: {sun.ClassLetter}  ({sun.Class}-type)");
        ImGui.Text($"Temperature: {sun.Temperature:0} K");
        ImGui.Text($"Luminosity: {sun.Luminosity:0.00} Lsun");
        ImGui.Text($"Mass: {sun.MassSolar:0.00} Msun");
        ImGui.Text($"Radius: {sun.RadiusMeters / MathUtil.SolarRadiusM:0.00} Rsun");
        ImGui.Spacing();

        string belt = sys.Belt != null ? "  + asteroid belt" : "";
        ImGui.TextColored(new System.Numerics.Vector4(0.7f, 0.85f, 1f, 1f),
            $"System: {sys.Planets.Length} planet(s){belt}");
        foreach (Planet pl in sys.Planets)
        {
            string moons = pl.Moons.Length > 0 ? $"  ({pl.Moons.Length} moons)" : "";
            ImGui.Text($"  {pl.Designation}  {pl.Type,-9} a={pl.SemiMajorAxis / MathUtil.AstronomicalUnit:0.00} AU{moons}");
        }
        if (sys.Planets.Length > 0)
            ImGui.Text($"Outermost orbit: {sys.MaxOrbitRadius / MathUtil.AstronomicalUnit:0.00} AU");
        ImGui.Spacing();

        if (nearest != null)
        {
            double range = nearest.RadiusMeters * ScanRangeRadii;
            double alt = nearestDist - nearest.RadiusMeters;
            ImGui.TextDisabled($"Nearest body: {nearest.Designation} ({FormatDistance(Math.Max(0, alt))} away)");
            ImGui.TextDisabled($"Approach to within {FormatDistance(range)} to scan it.");
        }
        else
        {
            ImGui.TextDisabled("No planetary bodies in this system.");
        }
    }

    /// <summary>Scanner fallback in interstellar space (no active system): read out the sector —
    /// galactic position and the nearest catalog star (the one whose system would spawn).</summary>
    private static void DrawSectorScan()
    {
        var p = _camera.Position;
        double distLy = p.DistanceTo(UniversePosition.Origin) / MathUtil.LightYear;
        ImGui.TextColored(new System.Numerics.Vector4(0.6f, 0.85f, 1f, 1f), "SECTOR SCAN");
        ImGui.Separator();
        ImGui.Text($"Sector: {p.Sector.X}, {p.Sector.Y}, {p.Sector.Z}");
        ImGui.Text($"From origin: {distLy:0.000} ly");
        ImGui.Text($"Resident: {_starPager.LoadedStarCount:N0} stars in {_starPager.LoadedBlockCount} blocks");
        ImGui.TextDisabled($"  ({StarCatalog.BlockSideLy:0} ly per block)");
        ImGui.Spacing();

        if (_starPager.HasNearest)
        {
            Star s = _starPager.Nearest;
            double nLy = _starPager.NearestDistanceMeters / MathUtil.LightYear;
            ImGui.TextColored(new System.Numerics.Vector4(1f, 0.9f, 0.5f, 1f), $"Nearest star — {s.Designation}");
            ImGui.Text($"Class: {s.ClassLetter}  ({s.Class}-type)");
            ImGui.Text($"Temperature: {s.Temperature:0} K");
            ImGui.Text($"Luminosity: {s.Luminosity:0.00} Lsun");
            ImGui.Text($"Mass: {s.MassSolar:0.00} Msun");
            ImGui.Text($"Distance: {nLy:0.0000} ly");
            if (nLy < _systemManager.SpawnLightYears)
                ImGui.TextColored(new System.Numerics.Vector4(0.4f, 1f, 0.5f, 1f),
                    $"  >> within {_systemManager.SpawnLightYears:0.###} ly — system in range");
            else
                ImGui.TextDisabled($"  Approach within {_systemManager.SpawnLightYears:0.###} ly to spawn its system.");
        }
        else
        {
            ImGui.TextDisabled("No stars within tracking range.");
        }
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

    /// <summary>2D top-down schematic of the active system (toggle with <c>M</c>): sun at centre, planets
    /// on log-radial orbit rings at their live angle, moons, the asteroid belt, and a "you are here"
    /// marker. Click a planet/moon to select it; "Travel here" jumps the camera next to it.</summary>
    private static void DrawSystemMap()
    {
        if (!_systemMapVisible || !_systemManager.HasActive) return;
        SolarSystem sys = _systemManager.Active!;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 640), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("System map  [M]", ref _systemMapVisible, ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }

        System.Numerics.Vector2 canvasPos = ImGui.GetCursorScreenPos();
        System.Numerics.Vector2 avail = ImGui.GetContentRegionAvail();
        float side = MathF.Max(120f, MathF.Min(avail.X, avail.Y - 140f)); // reserve the bottom for info
        var size = new System.Numerics.Vector2(avail.X, side);
        ImGui.InvisibleButton("##mapcanvas", size);
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);
        System.Numerics.Vector2 mouse = ImGui.GetIO().MousePos;

        var dl = ImGui.GetWindowDrawList();
        var center = canvasPos + new System.Numerics.Vector2(avail.X * 0.5f, side * 0.5f);
        float radiusPx = side * 0.5f - 18f;
        dl.AddRectFilled(canvasPos, canvasPos + size, Col(0.03f, 0.04f, 0.06f));

        double au = MathUtil.AstronomicalUnit;
        double maxAu = Math.Max(sys.MaxOrbitRadius / au, 1e-3);
        float Rpx(double a) => (float)(radiusPx * Math.Log(1.0 + Math.Max(a, 0.0)) / Math.Log(1.0 + maxAu));
        System.Numerics.Vector2 Project(double aAu, double ang)
        {
            float rp = Rpx(aAu);
            return center + new System.Numerics.Vector2((float)Math.Cos(ang) * rp, (float)Math.Sin(ang) * rp);
        }
        (double a, double ang) Polar(in UniversePosition p, in UniversePosition origin)
        {
            Vector3D<double> d = p.DeltaMeters(origin); // p - origin
            return (Math.Sqrt(d.X * d.X + d.Z * d.Z) / au, Math.Atan2(d.Z, d.X));
        }

        CelestialBody? hit = null;
        void Consider(CelestialBody b, System.Numerics.Vector2 p, float r)
        {
            if (hovered && System.Numerics.Vector2.Distance(mouse, p) < r + 4f) hit = b;
        }

        // Asteroid belt: a faint band between inner and outer radius.
        if (sys.Belt != null)
        {
            dl.AddCircle(center, Rpx(sys.Belt.InnerRadius / au), Col(0.5f, 0.45f, 0.35f, 0.4f), 96);
            dl.AddCircle(center, Rpx(sys.Belt.OuterRadius / au), Col(0.5f, 0.45f, 0.35f, 0.4f), 96);
        }

        // Planets on their orbit rings, moons on a small fixed ring around each planet.
        foreach (Planet p in sys.Planets)
        {
            (double pa, double pang) = Polar(p.CurrentPosition, sys.Sun.Position);
            dl.AddCircle(center, Rpx(pa), Col(0.30f, 0.40f, 0.55f, 0.5f), 96);
            System.Numerics.Vector2 pp = Project(pa, pang);
            float pr = 3f + (float)Math.Clamp(p.RadiusMeters / MathUtil.EarthRadiusM, 0.4, 3.0);
            dl.AddCircleFilled(pp, pr, ColorFor(p));
            Consider(p, pp, pr);
            if (ReferenceEquals(p, _systemMapSel)) dl.AddCircle(pp, pr + 4f, Col(1f, 1f, 1f, 0.9f), 20, 2f);

            int mi = 0;
            foreach (Moon mn in p.Moons)
            {
                (double _, double mang) = Polar(mn.CurrentPosition, p.CurrentPosition);
                float mr = 11f + mi * 5f;
                var mp = pp + new System.Numerics.Vector2((float)Math.Cos(mang) * mr, (float)Math.Sin(mang) * mr);
                dl.AddCircleFilled(mp, 2.2f, ColorFor(mn));
                Consider(mn, mp, 2.2f);
                if (ReferenceEquals(mn, _systemMapSel)) dl.AddCircle(mp, 5f, Col(1f, 1f, 1f, 0.9f), 14, 1.5f);
                mi++;
            }
        }

        // Sun (drawn last over the inner rings) and the camera's "you are here" marker.
        dl.AddCircleFilled(center, 7f, Col(1f, 0.85f, 0.3f));
        (double ca, double cang) = Polar(_camera.Position, sys.Sun.Position);
        System.Numerics.Vector2 cp = Project(ca, cang);
        dl.AddCircle(cp, 5f, Col(0.4f, 1f, 0.6f), 14, 2f);
        dl.AddLine(cp - new System.Numerics.Vector2(7, 0), cp + new System.Numerics.Vector2(7, 0), Col(0.4f, 1f, 0.6f));
        dl.AddLine(cp - new System.Numerics.Vector2(0, 7), cp + new System.Numerics.Vector2(0, 7), Col(0.4f, 1f, 0.6f));

        if (clicked) _systemMapSel = hit; // click empty space to deselect

        // Info + travel.
        ImGui.Spacing();
        ImGui.Separator();
        if (_systemMapSel is { } b2)
        {
            ImGui.Text(b2.Designation.Length > 0 ? b2.Designation : b2.Type.ToString());
            ImGui.TextDisabled($"{b2.Type}  •  r {b2.RadiusMeters / 1000.0:0} km  •  {b2.SurfaceTempK:0} K");
            if (b2.HasAtmosphere)
                ImGui.TextDisabled($"atmosphere {b2.SurfacePressureBar:0.000} bar{(b2.Habitable ? "  •  habitable" : "")}");
            double distM = _camera.Position.DistanceTo(b2.CurrentPosition);
            ImGui.TextDisabled($"distance {distM / au:0.000} AU");
            if (ImGui.Button("Travel here"))
                _camera.Position = b2.CurrentPosition.Translated(
                    new Vector3D<double>(Math.Max(b2.RadiusMeters * 3.0, 1.0e7), 0, 0));
        }
        else
        {
            ImGui.TextDisabled("click a planet or moon to select it");
        }
        ImGui.End();
    }

    /// <summary>Nearby-stars map — shown by <c>M</c> when you're NOT in a solar system (the system map takes
    /// over once you arrive at one). Resident catalog stars within a slider-set radius (10–50 ly), plotted
    /// top-down on the galactic plane (world XZ) and auto-centred on the ship. Click a star to select it, then
    /// jump to it or set it as the navigation search target.</summary>
    private static void DrawNearbyMap()
    {
        if (!_systemMapVisible || _systemManager.HasActive) return;

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(560, 660), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Nearby stars  [M]", ref _systemMapVisible, ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(220f);
        ImGui.SliderFloat("Range (ly)", ref _nearbyRangeLy, 10f, 50f, "%.0f");

        var io = ImGui.GetIO();
        System.Numerics.Vector2 canvasPos = ImGui.GetCursorScreenPos();
        System.Numerics.Vector2 avail = ImGui.GetContentRegionAvail();
        float side = MathF.Max(120f, MathF.Min(avail.X, avail.Y - 120f)); // reserve the bottom for info
        var size = new System.Numerics.Vector2(avail.X, side);
        ImGui.InvisibleButton("##nearbycanvas", size);
        bool hovered = ImGui.IsItemHovered();
        bool clicked = ImGui.IsItemClicked(ImGuiMouseButton.Left);

        var dl = ImGui.GetWindowDrawList();
        var center = canvasPos + new System.Numerics.Vector2(avail.X * 0.5f, side * 0.5f);
        float radiusPx = side * 0.5f - 18f;
        dl.AddRectFilled(canvasPos, canvasPos + size, Col(0.02f, 0.03f, 0.05f));

        double rangeM = _nearbyRangeLy * MathUtil.LightYear;
        double mpp = rangeM / radiusPx; // metres per pixel so the slider radius fills the disc

        // Heading-up: rotate the plot so the ship's forward heading points up. Screen-up = forward (horizontal
        // projection), screen-right = the camera's right (horizontal). Falls back to a fixed frame and a
        // perpendicular right when looking straight up/down.
        Vector3D<float> fwd = _camera.Forward, rgt = _camera.Right;
        double ux = fwd.X, uz = fwd.Z, ulen = Math.Sqrt(fwd.X * fwd.X + fwd.Z * fwd.Z);
        if (ulen < 1e-6) { ux = 0; uz = -1; ulen = 1; }
        ux /= ulen; uz /= ulen;
        double rx = rgt.X, rz = rgt.Z, rlen = Math.Sqrt(rgt.X * rgt.X + rgt.Z * rgt.Z);
        if (rlen < 1e-6) { rx = -uz; rz = ux; rlen = 1; }
        rx /= rlen; rz /= rlen;

        // World XZ delta → screen: camera-right component on +X, camera-forward component on -Y (screen up).
        System.Numerics.Vector2 ToScreen(in Vector3D<double> d) =>
            center + new System.Numerics.Vector2(
                (float)((d.X * rx + d.Z * rz) / mpp), (float)(-(d.X * ux + d.Z * uz) / mpp));

        // Range rings at 1/3, 2/3, full radius (faint reference circles).
        for (int k = 1; k <= 3; k++)
            dl.AddCircle(center, radiusPx * k / 3f, Col(0.25f, 0.32f, 0.45f, 0.5f), 64);

        // Field-of-view wedge: two lines from the centre at ±half the horizontal FOV about straight up (forward),
        // so you can read which nearby stars are actually on screen.
        float halfHFov = MathF.Atan(MathF.Tan(_camera.FovRadians * 0.5f) * _camera.AspectRatio);
        dl.AddLine(center, center + new System.Numerics.Vector2(-MathF.Sin(halfHFov), -MathF.Cos(halfHFov)) * radiusPx,
            Col(0.5f, 0.7f, 1f, 0.55f));
        dl.AddLine(center, center + new System.Numerics.Vector2(MathF.Sin(halfHFov), -MathF.Cos(halfHFov)) * radiusPx,
            Col(0.5f, 0.7f, 1f, 0.55f));

        // Resident stars within the (true 3D) range sphere, plotted on XZ. Block-culled against the sphere so
        // only the camera's block (and any face-adjacent ones) are scanned — at ≤50 ly vs 350 ly blocks that's
        // typically one block, so this stays cheap.
        UniversePosition cam = _camera.Position;
        Star hitStar = default; bool hasHit = false; float hitD = 7f;
        int shown = 0;
        foreach (StarCatalog block in _starPager.LoadedBlocks)
        {
            Vector3D<double> bc = block.Origin.DeltaMeters(cam); // block centre relative to the ship
            double half = block.SideMeters * 0.5;
            double nx = Math.Max(0.0, Math.Abs(bc.X) - half);
            double ny = Math.Max(0.0, Math.Abs(bc.Y) - half);
            double nz = Math.Max(0.0, Math.Abs(bc.Z) - half);
            if (nx * nx + ny * ny + nz * nz > rangeM * rangeM) continue; // block's nearest corner is out of range

            foreach (Star st in block.Stars)
            {
                Vector3D<double> d = st.Position.DeltaMeters(cam);
                if (d.X * d.X + d.Y * d.Y + d.Z * d.Z > rangeM * rangeM) continue;
                var p = ToScreen(d);
                float r = Math.Clamp(1.4f + st.SizeCue * 1.4f, 1.4f, 4.5f);
                uint col = Col(MathF.Max(st.Color.X, 0.2f), MathF.Max(st.Color.Y, 0.2f), MathF.Max(st.Color.Z, 0.2f));
                dl.AddCircleFilled(p, r, col, 8);
                shown++;
                if (hovered)
                {
                    float dd = System.Numerics.Vector2.Distance(io.MousePos, p);
                    if (dd < hitD + r) { hitD = dd; hitStar = st; hasHit = true; }
                }
            }
        }

        // Selection ring + the ship's "you are here" crosshair at the centre.
        if (_nearbyHasSel) dl.AddCircle(ToScreen(_nearbySel.Position.DeltaMeters(cam)), 7f, Col(1f, 1f, 1f), 16, 2f);
        dl.AddCircle(center, 5f, Col(0.4f, 1f, 0.6f), 14, 1.5f);
        dl.AddLine(center - new System.Numerics.Vector2(8, 0), center + new System.Numerics.Vector2(8, 0), Col(0.4f, 1f, 0.6f));
        dl.AddLine(center - new System.Numerics.Vector2(0, 8), center + new System.Numerics.Vector2(0, 8), Col(0.4f, 1f, 0.6f));

        if (clicked) { _nearbyHasSel = hasHit; if (hasHit) _nearbySel = hitStar; }

        ImGui.Spacing();
        ImGui.TextDisabled($"{shown:n0} stars within {_nearbyRangeLy:0} ly  •  click a star to select");
        ImGui.Separator();
        if (_nearbyHasSel)
        {
            Star s2 = _nearbySel;
            double ly = s2.Position.DistanceTo(cam) / MathUtil.LightYear;
            ImGui.Text($"#{s2.Id}  •  {s2.ClassLetter}-class  •  {s2.Temperature:0} K  •  {ly:0.000} ly");
            if (ImGui.Button("Jump here"))
            {
                _camera.Position = s2.Position.Translated(new Vector3D<double>(8.0 * MathUtil.AstronomicalUnit, 0, 0));
                _jumpStatus = $"Jumped to star #{s2.Id} ({s2.ClassLetter}-class)";
            }
            ImGui.SameLine();
            if (ImGui.Button("Set as search target")) { _starSearch = s2.Id.ToString(); FindStar(); }
        }
        else
        {
            ImGui.TextDisabled("no star selected");
        }
        ImGui.End();
    }

    /// <summary>Pack a colour for an ImGui draw list.</summary>
    private static uint Col(float r, float g, float b, float a = 1f)
        => ImGui.ColorConvertFloat4ToU32(new System.Numerics.Vector4(r, g, b, a));

    /// <summary>Map-dot colour from a body's surface albedo (floored so dark worlds still read).</summary>
    private static uint ColorFor(CelestialBody b)
    {
        Vector3D<float> c = b.SurfaceAlbedo;
        return Col(MathF.Max(c.X, 0.12f), MathF.Max(c.Y, 0.12f), MathF.Max(c.Z, 0.12f));
    }

    /// <summary>2D top-down galaxy/sector map (toggle with <c>N</c>): the pager's resident catalog stars
    /// projected onto the galactic plane (world XZ), drag to pan and wheel to zoom. Markers for the
    /// camera, the active system, and the search target. Click a star to select it, then jump to it or
    /// set it as the navigation search target. Resident-bubble only for now (panning past it shows no
    /// new stars until the streamer is taught to follow the map centre).</summary>
    private static void DrawGalaxyMap()
    {
        if (!_galaxyMapVisible) return;
        ImGui.SetNextWindowSize(new System.Numerics.Vector2(640, 700), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("Galaxy map  [N]", ref _galaxyMapVisible, ImGuiWindowFlags.NoNav))
        {
            ImGui.End();
            return;
        }

        var io = ImGui.GetIO();
        System.Numerics.Vector2 canvasPos = ImGui.GetCursorScreenPos();
        System.Numerics.Vector2 avail = ImGui.GetContentRegionAvail();
        float ch = MathF.Max(120f, avail.Y - 116f); // reserve the bottom for footer + info
        var size = new System.Numerics.Vector2(avail.X, ch);
        ImGui.InvisibleButton("##galaxycanvas", size);
        bool hovered = ImGui.IsItemHovered();

        var dl = ImGui.GetWindowDrawList();
        var c1 = canvasPos + size;
        var canvasCenter = canvasPos + size * 0.5f;
        dl.PushClipRect(canvasPos, c1, true);
        dl.AddRectFilled(canvasPos, c1, Col(0.02f, 0.02f, 0.04f));

        // Zoom on the wheel, pan on a left-drag (a press that barely moves counts as a click → select).
        if (hovered && io.MouseWheel != 0f)
            _galaxyMpp = Math.Clamp(_galaxyMpp * Math.Pow(1.2, -io.MouseWheel),
                0.02 * MathUtil.LightYear, 80.0 * MathUtil.LightYear);
        if (ImGui.IsItemActivated()) _galaxyDragPx = 0f;
        if (ImGui.IsItemActive() && (io.MouseDelta.X != 0f || io.MouseDelta.Y != 0f))
        {
            _galaxyDragPx += MathF.Abs(io.MouseDelta.X) + MathF.Abs(io.MouseDelta.Y);
            _galaxyPan = new Vector3D<double>(
                _galaxyPan.X - io.MouseDelta.X * _galaxyMpp, 0,
                _galaxyPan.Z - io.MouseDelta.Y * _galaxyMpp);
        }
        bool click = ImGui.IsItemDeactivated() && _galaxyDragPx < 4f;

        double mpp = _galaxyMpp;
        UniversePosition mapCenter = _camera.Position.Translated(_galaxyPan);
        double halfW = size.X * 0.5 * mpp, halfH = ch * 0.5 * mpp;
        float minCue = (float)Math.Clamp(mpp / MathUtil.LightYear * 0.12, 0.0, 6.0); // hide faint stars when zoomed out

        System.Numerics.Vector2 ToScreen(in Vector3D<double> relMeters) =>
            canvasCenter + new System.Numerics.Vector2((float)(relMeters.X / mpp), (float)(relMeters.Z / mpp));

        // Nebulae as translucent coloured disks (drawn under the stars), sized to their true radius, so they
        // read as glowing regions on the map you can aim for. Labelled on hover.
        foreach (Nebula neb in _nebulaField.Nebulae)
        {
            Vector3D<double> d = neb.Position.DeltaMeters(mapCenter);
            float rpx = (float)(neb.RadiusMeters / mpp);
            if (d.X + neb.RadiusMeters < -halfW || d.X - neb.RadiusMeters > halfW ||
                d.Z + neb.RadiusMeters < -halfH || d.Z - neb.RadiusMeters > halfH) continue;
            var np = ToScreen(d);
            uint fill = Col(neb.Color.X, neb.Color.Y, neb.Color.Z, 0.16f);
            uint ring = Col(neb.Color.X, neb.Color.Y, neb.Color.Z, 0.7f);
            dl.AddCircleFilled(np, MathF.Max(rpx, 2f), fill, 32);
            dl.AddCircle(np, MathF.Max(rpx, 2f), ring, 32);
            if (hovered && System.Numerics.Vector2.Distance(io.MousePos, np) < MathF.Max(rpx, 5f))
                dl.AddText(np + new System.Numerics.Vector2(6, -14), ring, neb.Name);
        }

        // Collect in-view stars, block-culled (skip whole blocks whose XZ box misses the view). Each block is
        // sub-sampled with a uniform stride so a zoomed-out view doesn't scan the millions of resident stars
        // (~257k per 350 ly block × up to 27 blocks) every frame — that scan, plus drawing them, dropped the
        // map to ~15 fps. The map now shows a representative thinned set rather than every star.
        _galaxyDots.Clear();
        const int CollectCap = 6000;
        const int PerBlockScanCap = 10000; // examine at most this many stars per block (uniform stride)
        foreach (StarCatalog block in _starPager.LoadedBlocks)
        {
            Vector3D<double> bo = block.Origin.DeltaMeters(mapCenter); // block min corner relative to centre
            double bs = block.SideMeters;
            if (bo.X + bs < -halfW || bo.X > halfW || bo.Z + bs < -halfH || bo.Z > halfH) continue;
            IReadOnlyList<Star> stars = block.Stars;
            int n = stars.Count;
            int bstride = Math.Max(1, n / PerBlockScanCap);
            for (int i = 0; i < n; i += bstride)
            {
                Star st = stars[i];
                if (st.SizeCue < minCue) continue;
                Vector3D<double> d = st.Position.DeltaMeters(mapCenter);
                if (d.X < -halfW || d.X > halfW || d.Z < -halfH || d.Z > halfH) continue;
                if (_galaxyDots.Count >= CollectCap) break;
                float r = Math.Clamp(0.8f + st.SizeCue * 1.4f, 0.8f, 4.0f);
                uint col = Col(MathF.Max(st.Color.X, 0.15f), MathF.Max(st.Color.Y, 0.15f), MathF.Max(st.Color.Z, 0.15f));
                _galaxyDots.Add((ToScreen(d), r, col, st));
            }
            if (_galaxyDots.Count >= CollectCap) break;
        }

        // Draw (uniformly thinned if over the cap) and hit-test against the mouse.
        const int DrawCap = 1500;
        int stride = Math.Max(1, (_galaxyDots.Count + DrawCap - 1) / DrawCap);
        Star hitStar = default;
        bool hasHit = false;
        float hitD = 6f;
        for (int i = 0; i < _galaxyDots.Count; i += stride)
        {
            var (p, r, col, st) = _galaxyDots[i];
            dl.AddCircleFilled(p, r, col, 6);
            if (hovered)
            {
                float dd = System.Numerics.Vector2.Distance(io.MousePos, p);
                if (dd < hitD + r) { hitD = dd; hitStar = st; hasHit = true; }
            }
        }

        void Marker(in UniversePosition pos, uint col, float rad) =>
            dl.AddCircle(ToScreen(pos.DeltaMeters(mapCenter)), rad, col, 16, 2f);
        if (_systemManager.HasActive) Marker(_systemManager.Active!.Sun.Position, Col(1f, 0.85f, 0.3f), 7f);
        if (_hasSearchTarget) Marker(_searchTarget.Position, Col(0.4f, 0.8f, 1f), 7f);
        if (_galaxyHasSel) Marker(_galaxySel.Position, Col(1f, 1f, 1f), 6f);

        // Camera "you are here" crosshair.
        System.Numerics.Vector2 me = ToScreen(_camera.Position.DeltaMeters(mapCenter));
        dl.AddCircle(me, 5f, Col(0.4f, 1f, 0.6f), 12, 2f);
        dl.AddLine(me - new System.Numerics.Vector2(8, 0), me + new System.Numerics.Vector2(8, 0), Col(0.4f, 1f, 0.6f));
        dl.AddLine(me - new System.Numerics.Vector2(0, 8), me + new System.Numerics.Vector2(0, 8), Col(0.4f, 1f, 0.6f));
        dl.PopClipRect();

        if (click) { _galaxyHasSel = hasHit; if (hasHit) _galaxySel = hitStar; }

        // Footer + selection info.
        double acrossLy = size.X * mpp / MathUtil.LightYear;
        ImGui.TextDisabled($"view {acrossLy:0.0} ly across  •  {_galaxyDots.Count:n0} shown / {_starPager.LoadedStarCount:n0} resident  •  drag = pan, wheel = zoom");
        if (ImGui.Button("Center on me")) _galaxyPan = default;
        ImGui.SameLine();
        if (ImGui.Button("Reset zoom")) _galaxyMpp = 0.3 * MathUtil.LightYear;

        ImGui.Separator();
        if (_galaxyHasSel)
        {
            Star s2 = _galaxySel;
            double ly = s2.Position.DistanceTo(_camera.Position) / MathUtil.LightYear;
            ImGui.Text($"#{s2.Id}  •  {s2.ClassLetter}-class  •  {s2.Temperature:0} K  •  {ly:0.000} ly");
            if (ImGui.Button("Jump here"))
            {
                _camera.Position = s2.Position.Translated(new Vector3D<double>(8.0 * MathUtil.AstronomicalUnit, 0, 0));
                _jumpStatus = $"Jumped to star #{s2.Id} ({s2.ClassLetter}-class)";
            }
            ImGui.SameLine();
            if (ImGui.Button("Set as search target")) { _starSearch = s2.Id.ToString(); FindStar(); }
        }
        else
        {
            ImGui.TextDisabled("click a star to select it");
        }
        ImGui.SameLine();
        if (ImGui.Button("Open 3D view"))
        {
            _galaxy3DVisible = true;
            _galaxyMapVisible = false;
            _mapFocus = _camera.Position;
            _mapYaw = 0f; _mapPitch = 0.5f; _mapDistLy = 80.0;
            _mapFocusStarId = 0;
            _mapChainValid = false;
            SetMouseCaptured(false); // free the cursor to drag-orbit + use the panel
        }
        ImGui.End();
    }

    /// <summary>Full-screen 3D galaxy map: an orbit camera over the resident star cloud, drawn with the
    /// existing catalog-star pipeline (so far stars project correctly) and the normal bloom/composite.
    /// Drag to rotate, wheel to zoom; markers (drawn over the top) flag you, the active system and the
    /// search target. First look — picking/jump in 3D is the next step.</summary>
    private static void RenderGalaxyMap3DFrame()
    {
        _mapCamera ??= new Camera { NearPlane = 1.0e9f, FarPlane = 1.0e20f };
        var io = ImGui.GetIO();

        // Orbit input on the background (an ImGui panel keeps its own mouse via WantCaptureMouse).
        if (!io.WantCaptureMouse)
        {
            if (io.MouseDown[0]) { _mapYaw -= io.MouseDelta.X * 0.005f; _mapPitch -= io.MouseDelta.Y * 0.005f; }
            if (io.MouseWheel != 0f) _mapDistLy = Math.Clamp(_mapDistLy * Math.Pow(1.1, -io.MouseWheel), 2.0, 5000.0);
        }
        _mapPitch = Math.Clamp(_mapPitch, -1.5f, 1.5f);

        // Click vs drag: a left press that barely moves is a star pick; a moving press orbits the camera.
        if (io.MouseClicked[0] && !io.WantCaptureMouse) { _mapPressed = true; _mapDragPx = 0f; }
        if (_mapPressed && io.MouseDown[0]) _mapDragPx += MathF.Abs(io.MouseDelta.X) + MathF.Abs(io.MouseDelta.Y);
        _mapClick = false;
        if (_mapPressed && io.MouseReleased[0]) { _mapClick = _mapDragPx < 4f && !io.WantCaptureMouse; _mapPressed = false; }

        _mapCamera.AspectRatio = io.DisplaySize.X / MathF.Max(1f, io.DisplaySize.Y);
        _mapCamera.Orientation = Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitY, _mapYaw)
                               * Quaternion<float>.CreateFromAxisAngle(Vector3D<float>.UnitX, _mapPitch);
        Vector3D<float> fwd = _mapCamera.Forward;
        double distM = _mapDistLy * MathUtil.LightYear;
        _mapCamera.Position = _mapFocus.Translated(new Vector3D<double>(-fwd.X * distM, -fwd.Y * distM, -fwd.Z * distM));

        // No full star cloud — it was too noisy. Just clear to deep space; only the immediate neighbourhood
        // (nearest stars + nav lines + markers) is drawn, as an ImGui overlay in DrawGalaxy3DPanel.
        _gl.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
        _gl.Viewport(0, 0, (uint)_sceneFbo.Width, (uint)_sceneFbo.Height);
        _gl.ClearColor(0.01f, 0.012f, 0.02f, 1f);
        _gl.Clear((uint)(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit));

        if (_hudVisible) DrawGalaxy3DPanel(io);
        _imgui.Render();
    }

    /// <summary>3D-map overlay: screen-projected markers (you / active system / search target) drawn over
    /// the star cloud, plus a small control panel.</summary>
    private static void DrawGalaxy3DPanel(ImGuiIOPtr io)
    {
        Matrix4X4<float> viewProj = _mapCamera.ViewMatrix * _mapCamera.ProjectionMatrix;
        var fg = ImGui.GetForegroundDrawList();

        bool Project(in UniversePosition pos, out System.Numerics.Vector2 screen)
        {
            Vector3D<float> rel = pos.ToCameraRelative(_mapCamera.Position);
            Vector4D<float> clip = Vector4D.Transform(new Vector4D<float>(rel, 1f), viewProj);
            screen = default;
            if (clip.W <= 1e-4f) return false; // behind the camera
            float nx = clip.X / clip.W, ny = clip.Y / clip.W;
            if (nx is < -1.2f or > 1.2f || ny is < -1.2f or > 1.2f) return false;
            screen = new System.Numerics.Vector2((nx * 0.5f + 0.5f) * io.DisplaySize.X, (0.5f - ny * 0.5f) * io.DisplaySize.Y);
            return true;
        }

        EnsureMapChain();
        bool focusVis = Project(_mapFocus, out var fp);

        // A single navigation route: focus → its nearest star → that star's nearest, and so on through the
        // local stars. Lines connect consecutive visible hops; each star is a colour dot + bright core + label.
        bool lastVis = focusVis;
        System.Numerics.Vector2 lastPt = fp;
        Star pickHit = default;
        bool pickHas = false;
        float pickD = 12f;
        for (int i = 0; i < _mapChain.Count; i++)
        {
            Star st = _mapChain[i];
            bool vis = Project(st.Position, out var sp2);
            if (vis && lastVis) fg.AddLine(lastPt, sp2, Col(0.5f, 0.7f, 1f, 0.5f), 1.5f);
            if (vis)
            {
                Vector3D<float> sc = st.Color;
                uint scol = Col(MathF.Max(sc.X, 0.45f), MathF.Max(sc.Y, 0.45f), MathF.Max(sc.Z, 0.45f));
                float r = Math.Clamp(3.5f + st.SizeCue * 2.5f, 3.5f, 9f);
                fg.AddCircleFilled(sp2, r, scol, 16);
                fg.AddCircleFilled(sp2, r * 0.45f, Col(1f, 1f, 1f, 0.9f), 12); // bright core
                if (i == 0) // label only the nearest neighbour (the centre itself is labelled separately)
                {
                    double ly = st.Position.DistanceTo(_mapFocus) / MathUtil.LightYear;
                    fg.AddText(sp2 + new System.Numerics.Vector2(r + 3f, -5f), Col(0.8f, 0.9f, 1f), $"#{st.Id}  {ly:0.0} ly");
                }
                float dd = System.Numerics.Vector2.Distance(io.MousePos, sp2);
                if (dd < pickD) { pickD = dd; pickHit = st; pickHas = true; }
            }
            lastVis = vis;
            lastPt = sp2;
        }

        // Click a star → make it the new centre (rebuild the local view) AND set it as the fly-to navigation
        // target, so the in-world arrow + Find/Go-to guide you to it back in fly view.
        if (_mapClick && pickHas)
        {
            _mapFocus = pickHit.Position;
            _mapFocusStarId = pickHit.Id;
            _mapChainValid = false;

            _searchTarget = pickHit;
            _hasSearchTarget = true;
            _starSearch = pickHit.Id.ToString();
            double tly = pickHit.Position.DistanceTo(_camera.Position) / MathUtil.LightYear;
            _searchStatus = $"#{pickHit.Id}: {pickHit.ClassLetter}-class, {tly:0.000} ly — Go to it";
        }

        if (focusVis)
        {
            uint fcol = _mapFocusStarId == 0 ? Col(0.4f, 1f, 0.6f) : Col(1f, 1f, 1f, 0.95f);
            string flabel = _mapFocusStarId == 0 ? "you are here" : $"#{_mapFocusStarId} (centre)";
            fg.AddCircle(fp, 8f, fcol, 18, 2f);
            fg.AddText(fp + new System.Numerics.Vector2(10, -6), fcol, flabel);
        }
        // When centred on a picked star, still show where the ship actually is.
        if (_mapFocusStarId != 0 && Project(_camera.Position, out var shipP))
        {
            fg.AddCircle(shipP, 6f, Col(0.4f, 1f, 0.6f), 14, 2f);
            fg.AddText(shipP + new System.Numerics.Vector2(8, -6), Col(0.4f, 1f, 0.6f), "ship");
        }
        if (_systemManager.HasActive && Project(_systemManager.Active!.Sun.Position, out var sp))
        {
            fg.AddCircle(sp, 7f, Col(1f, 0.85f, 0.3f), 16, 2f);
            fg.AddText(sp + new System.Numerics.Vector2(9, -6), Col(1f, 0.85f, 0.3f), $"#{_systemManager.Active!.Sun.Id}");
        }
        if (_hasSearchTarget && Project(_searchTarget.Position, out var tp))
            fg.AddCircle(tp, 7f, Col(0.4f, 0.8f, 1f), 16, 2f);

        ImGui.SetNextWindowSize(new System.Numerics.Vector2(330, 0), ImGuiCond.FirstUseEver);
        ImGui.Begin("Galaxy map — 3D", ImGuiWindowFlags.NoNav | ImGuiWindowFlags.AlwaysAutoResize);
        ImGui.TextDisabled($"orbit {_mapDistLy:0.#} ly  •  {_mapChain.Count} stars within {_mapRangeLy:0} ly");
        ImGui.TextDisabled("drag = rotate  •  wheel = zoom");
        if (ImGui.SliderFloat("range (ly)", ref _mapRangeLy, 10f, 50f, "%.0f")) _mapChainValid = false;
        if (ImGui.Button("Recenter on me")) { _mapFocus = _camera.Position; _mapFocusStarId = 0; _mapChainValid = false; }
        ImGui.SameLine();
        if (ImGui.Button("Close")) _galaxy3DVisible = false;

        if (_hasSearchTarget)
        {
            ImGui.Separator();
            double ly = _searchTarget.Position.DistanceTo(_camera.Position) / MathUtil.LightYear;
            ImGui.Text($"target #{_searchTarget.Id}  •  {_searchTarget.ClassLetter}-class  •  {ly:0.000} ly");
            ImGui.TextDisabled("(an arrow points to it back in fly view)");
            if (ImGui.Button("Fly to target")) { GoToStar(); _galaxy3DVisible = false; }
        }
        ImGui.End();
    }

    /// <summary>Build the navigation chain for the 3D map: every resident star within <see cref="MapRangeLy"/>
    /// of the focus, ordered by a greedy nearest-neighbour walk from the focus (focus → its nearest star →
    /// that star's nearest unvisited star → …). Recomputed only when the focus moves.</summary>
    private static void EnsureMapChain()
    {
        if (_mapChainValid) return;
        _mapChainValid = true;
        _mapChain.Clear();

        double maxM = _mapRangeLy * MathUtil.LightYear;
        const int Cap = 600; // safety bound on a very dense neighbourhood
        var inRange = new List<Star>();
        foreach (StarCatalog block in _starPager.LoadedBlocks)
        {
            foreach (Star st in block.Stars)
            {
                double d = st.Position.DistanceTo(_mapFocus);
                if (d < 1.0e6 || d > maxM) continue; // skip a star essentially at the focus, and out-of-range
                inRange.Add(st);
                if (inRange.Count >= Cap) break;
            }
            if (inRange.Count >= Cap) break;
        }

        // Greedy nearest-neighbour walk from the focus through all in-range stars.
        var used = new bool[inRange.Count];
        UniversePosition cur = _mapFocus;
        for (int step = 0; step < inRange.Count; step++)
        {
            int best = -1;
            double bestD = double.MaxValue;
            for (int i = 0; i < inRange.Count; i++)
            {
                if (used[i]) continue;
                double d = inRange[i].Position.DistanceTo(cur);
                if (d < bestD) { bestD = d; best = i; }
            }
            if (best < 0) break;
            used[best] = true;
            _mapChain.Add(inRange[best]);
            cur = inRange[best].Position;
        }
    }

    /// <summary>Surface-object scatter controls: a master toggle + spawn range, then a live-editable
    /// list of spawner layers (mesh, density, size, orientation, env-trait gate, per-world spawn chance).</summary>
    private static void DrawScatterUI()
    {
        ImGui.Checkbox("Surface objects", ref _scatter.Enabled);
        ImGui.SameLine();
        ImGui.SliderFloat("spawn range (m)##scatter", ref TerrainTuning.ScatterRange, 100f, 9000f);

        // Context for the altitude limits: this world's relief envelope (metres above the base radius),
        // so the user can pick min/max altitude against real numbers instead of guessing.
        PlanetTerrain? terr = _terrainRenderer.ActiveTerrain;
        float worldAmp = terr != null ? (float)terr.Amplitude : 1000f;
        bool hasSea = terr != null && !double.IsInfinity(terr.SeaLevelMeters);
        float worldSea = hasSea ? (float)terr!.SeaLevelMeters : 0f;
        if (terr != null)
            ImGui.TextDisabled(hasSea
                ? $"world relief: ±{worldAmp:0} m,  sea level {worldSea:0} m"
                : $"world relief: ±{worldAmp:0} m,  no ocean");

        int remove = -1;
        for (int i = 0; i < _scatter.Spawners.Count; i++)
        {
            Spawner sp = _scatter.Spawners[i];
            ImGui.PushID(i);
            string label = (sp.Name.Length > 0 ? sp.Name : "(spawner)") + (sp.ActiveHere ? "  [active here]" : "  [inactive here]");
            if (ImGui.TreeNodeEx(label, ImGuiTreeNodeFlags.DefaultOpen))
            {
                ImGui.Checkbox("enabled", ref sp.Enabled);
                ImGui.InputText("name", ref sp.Name, 32);
                ImGui.Combo("mesh", ref sp.MeshId, ScatterRenderer.MeshCombo);
                ImGui.Combo("orient", ref sp.Orient, "World up\0Surface normal\0Random\0");
                ImGui.SliderFloat("density", ref sp.Density, 0f, 1f);
                ImGui.SliderFloat("min size (m)", ref sp.MinSize, 0.5f, 40f);
                ImGui.SliderFloat("max size (m)", ref sp.MaxSize, 0.5f, 40f);
                ImGui.SliderFloat("spawn chance", ref sp.SpawnChance, 0f, 1f);

                // Altitude band (metres of relief above the base radius). Off by default; enabling seeds it
                // to "above sea level, up to the peaks" so it immediately keeps objects out of the ocean.
                bool limitAlt = sp.MinAltitude > -Spawner.NoAltLimit || sp.MaxAltitude < Spawner.NoAltLimit;
                if (ImGui.Checkbox("limit altitude", ref limitAlt))
                {
                    if (limitAlt) { sp.MinAltitude = hasSea ? worldSea : 0f; sp.MaxAltitude = worldAmp; }
                    else { sp.MinAltitude = -Spawner.NoAltLimit; sp.MaxAltitude = Spawner.NoAltLimit; }
                }
                if (limitAlt)
                {
                    float lim = MathF.Max(2000f, worldAmp * 1.1f);
                    ImGui.DragFloat("min alt (m)", ref sp.MinAltitude, lim / 400f, -lim, lim, "%.0f");
                    ImGui.DragFloat("max alt (m)", ref sp.MaxAltitude, lim / 400f, -lim, lim, "%.0f");
                    if (sp.MaxAltitude < sp.MinAltitude) sp.MaxAltitude = sp.MinAltitude;
                }

                ImGui.TextDisabled("require these traits on the world:");
                TraitCheckbox("surface", ref sp.Require, EnvTrait.Surface); ImGui.SameLine();
                TraitCheckbox("atmosphere", ref sp.Require, EnvTrait.Atmosphere); ImGui.SameLine();
                TraitCheckbox("life", ref sp.Require, EnvTrait.Life);
                TraitCheckbox("water", ref sp.Require, EnvTrait.Water); ImGui.SameLine();
                TraitCheckbox("molten", ref sp.Require, EnvTrait.Molten); ImGui.SameLine();
                TraitCheckbox("frozen", ref sp.Require, EnvTrait.Frozen);

                if (ImGui.Button("remove")) remove = i;
                ImGui.TreePop();
            }
            ImGui.PopID();
        }
        if (remove >= 0) _scatter.Spawners.RemoveAt(remove);
        if (ImGui.Button("Add spawner"))
        {
            int n = _scatter.Spawners.Count + 1;
            _scatter.Spawners.Add(new Spawner { Name = $"Spawner {n}", Seed = (uint)(n * 131 + 17) });
        }
        // Edits to traits / chance / seed change which spawners are active here → recompute next frame.
        _scatter.InvalidateActivation();
        ImGui.TextDisabled("(gates: required env traits + per-world spawn chance; layers roll independently)");
    }

    /// <summary>An ImGui checkbox bound to one bit of an <see cref="EnvTrait"/> flags mask.</summary>
    private static void TraitCheckbox(string label, ref EnvTrait mask, EnvTrait bit)
    {
        bool on = (mask & bit) != 0;
        if (ImGui.Checkbox(label, ref on))
            mask = on ? (mask | bit) : (mask & ~bit);
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
        if (mps >= 0.1 * 1e9 * MathUtil.LightYear)
            return $"{mps / (1e9 * MathUtil.LightYear):0.000} Gly/s";
        if (mps >= 0.1 * 1e6 * MathUtil.LightYear)
            return $"{mps / (1e6 * MathUtil.LightYear):0.000} Mly/s";
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
