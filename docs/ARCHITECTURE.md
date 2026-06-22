# DeepSpaceEngine — Architecture & API Reference

How the engine is put together, the public API of each layer, and how the
calls work. For the milestone narrative and feature list see
[`../README.md`](../README.md); for the original design brief see
[`PLAN.md`](PLAN.md).

> Scope: this documents the engine's *internal* C# API — the types you call
> when extending the engine — not an external/embeddable SDK. Signatures here
> were read from source; if a signature drifts, the source is authoritative.

---

## 1. The two ideas everything rests on

Almost every design decision falls out of two principles.

### 1.1 Floating-origin precision

A single `double` resolves to only hundreds of km at galactic scale, so the
engine never stores or sends absolute coordinates to the GPU. Instead every
world position is a [`UniversePosition`](#21-universeposition): an exact 64-bit
integer **sector** index (1 sector = 1 AU) plus a `double` **local** offset kept
inside that sector. The double's full precision is therefore always spent on a
value `< 1.5e11 m`, giving sub-millimetre resolution *everywhere* with
effectively unlimited range.

At render time, `ToCameraRelative(camera)` collapses a position into a small,
precise `float` vector relative to the camera. The camera is effectively always
at the render-space origin, so GPU math never sees a gigantic coordinate. This
is why `Camera.ViewMatrix` carries **rotation only** — translation is already
baked into each object's camera-relative position on the CPU.

### 1.2 Determinism from a seed

Nothing about the universe is stored. Billions of stars, their systems,
terrain, atmospheres and nebulae are all *pure functions* of a 64-bit
`WorldSeed` plus an integer coordinate or id. The same input always reproduces
the same output, bit-for-bit, on any machine. The machinery:

- [`Hashing`](#22-hashing--deterministicrng) — SplitMix64 mixing/combining,
  cell hashing, hash→unit-float helpers.
- [`DeterministicRng`](#22-hashing--deterministicrng) — a tiny value-type PRNG
  seeded per generated object, so generation *order* never affects results.

Each subsystem draws from its **own** RNG stream (seeded from a hash of the
object's id/coords) so adding rings to a planet, say, doesn't perturb the
planet-spacing stream. Live tuning values are applied as *multipliers* on top of
seeded values, preserving per-world variety while letting you nudge the look.

---

## 2. Project layout

```
Engine.Core        coords, hashing/RNG, math constants        (no deps)
Engine.Rendering   GL wrappers: Shader, Mesh, Camera, …        → Engine.Core
Engine.Platform    GameWindow: Silk.NET window + GL + input    → Core, Rendering
Engine.Audio       AudioEngine: OpenAL sound + music + synth    → Engine.Core
Game.Universe      procedural generation (stars→terrain)       → Engine.Core
Game.Systems       runtime lifecycle: systems, speed, fields   → Universe, Core
Game.App           entry point, main loop, all renderers, HUD  → everything
Engine.Core.Tests  xUnit: precision, determinism, sim          → Core, Universe…
```

Dependency arrows point downward only — generation never references rendering.
Targets `net7.0`, **Silk.NET 2.23.0**, **OpenGL 4.1 core** (the macOS-safe
baseline; terrain is baked via fragment passes, no compute shaders).

---

## 3. Engine.Core — coordinates, hashing, math

### 3.1 UniversePosition (struct)

`absolute_meters = Sector * SectorSize + Local`, with `SectorSize = 1 AU =
1.495978707e11 m`. `Local` is always normalized into `[0, SectorSize)` per axis.

| Member | Signature | Notes |
|---|---|---|
| Fields | `Vector3D<long> Sector`, `Vector3D<double> Local` | exact index + in-sector offset |
| Const | `const double SectorSize` | 1 AU in metres |
| Static | `static UniversePosition Origin { get; }` | (0,0,0)+（0,0,0) |
| Factory | `static UniversePosition FromMeters(Vector3D<double>)` / `(double x,y,z)` | decomposes + normalizes |
| | `void Normalize()` | carries `Local` overflow into `Sector` (uses `MathUtil.FloorDiv` so negatives are correct) |
| | `Vector3D<float> ToCameraRelative(in UniversePosition camera)` | **the GPU hand-off** — small precise float vector |
| | `Vector3D<double> DeltaMeters(in UniversePosition other)` | exact displacement |
| | `double DistanceTo(in UniversePosition other)` / `DistanceSquaredTo(…)` | metres |
| | `UniversePosition Translated(Vector3D<double> d)` | returns a new, normalized position |
| | `void Translate(Vector3D<double> d)` | in-place, normalized |

**Invariant:** any arithmetic that touches `Local` must be followed by
`Normalize()` (the `Translate*` helpers do this for you).

### 3.2 Hashing & DeterministicRng

`static class Hashing`:

| Signature | What it does |
|---|---|
| `ulong Mix64(ulong x)` | SplitMix64 finalizer — 1-bit input change → ~half output bits flip |
| `ulong Combine(ulong seed, ulong value)` | `Mix64(seed ^ Mix64(value))`; order matters |
| `ulong HashCell(long x, long y, long z, ulong worldSeed)` | folds a 3D cell + world seed → 64-bit |
| `double ToUnitDouble(ulong h)` / `float ToUnitFloat(ulong h)` | uniform `[0,1)` |
| `double Range(ulong h, double min, double max)` | scaled to `[min,max)` |

`struct DeterministicRng` (copy it to fork a stream):

| Signature | Notes |
|---|---|
| `DeterministicRng(ulong seed)` | seed *is* the initial state |
| `ulong NextULong()` | advance state by SplitMix64 gamma, `Mix64` the result |
| `double NextDouble()` / `float NextFloat()` | unit range |
| `double Range(double min, double max)` / `int RangeInt(int,int)` / `long RangeLong(long)` | |
| `int Poisson(double lambda)` | Knuth's method; used for stars/planets-per-cell counts |

### 3.3 MathUtil (static)

Constants: `LightYear`, `SpeedOfLight`, `AstronomicalUnit (= SectorSize)`,
`GravitationalConstant`, `SolarMassKg`, `SolarRadiusM`, `EarthRadiusM`.
Plus `long FloorDiv(long a, long b)` — true floor division (rounds toward −∞),
required for correct sector decomposition of negative coordinates.

---

## 4. Engine.Rendering — the thin GL layer

All geometry lives in GPU buffers; there is no immediate mode. The standard
vertex format is **6 interleaved floats** `[x,y,z, r,g,b]` (some meshes reuse the
colour slot for a normal).

### 4.1 Shader (sealed, IDisposable)

```csharp
Shader(GL gl, string vertexSource, string fragmentSource)   // compiles + links, throws on error
void Use()
void SetMatrix(string name, in Matrix4X4<float> m)          // row-major upload, transpose=false
void SetVector2/3/4(string, …)   void SetFloat(string,float)   void SetInt(string,int)
uint Handle { get; }
```
Uniform locations are queried once and cached in a dictionary.

### 4.2 Camera (sealed)

```csharp
UniversePosition Position;            Quaternion<float> Orientation = Identity;
float FovRadians = π/3;  float AspectRatio = 16/9;  float NearPlane = 0.5;  float FarPlane = 1e7;
Vector3D<float> Forward / Right / Up;                       // basis from Orientation
Matrix4X4<float> ViewMatrix;          // rotation only (Inverse(Orientation)); translation is camera-relative
Matrix4X4<float> ProjectionMatrix;    // MatrixHelper.PerspectiveGL(...)
```
Note: most renderers fit their **own** near/far each frame for precision rather
than using the camera defaults.

### 4.3 Mesh (sealed, IDisposable)

```csharp
const int FloatsPerVertex = 6;
Mesh(GL gl, ReadOnlySpan<float> vertices, PrimitiveType primitive)   // builds VAO+VBO, StaticDraw
void Draw()                                                          // bind, glDrawArrays, unbind
```

### 4.4 Primitives (static factories)

`BuildGrid`, `BuildAxes`, `BuildCube`, `BuildSphere(stacks,slices)`,
`BuildCircleLine(segments)` (orbit rings), and `BuildRingAnnulus(segments)` —
the annulus stores a *unit* direction + a radial `t` in the colour channel so one
mesh serves every planet's rings via per-draw radius uniforms.

### 4.5 MatrixHelper (static)

`PerspectiveGL(fovY, aspect, near, far)` returns the GL projection in Silk.NET's
row-vector convention (the transpose of the classic column-major matrix, so GLSL
can use the natural `uMVP * vec4(pos,1)`). `ToRowMajor(in m, Span<float> dst)`
flattens for upload.

### Render-loop shape

```
relPos = obj.WorldPos.ToCameraRelative(camera.Position);
mvp    = CreateTranslation(relPos) * obj.Rotation * camera.ViewMatrix * camera.ProjectionMatrix;
shader.Use(); shader.SetMatrix("uMVP", mvp); shader.SetVector3("uColor", …); mesh.Draw();
```

---

## 5. Engine.Platform — windowing

### GameWindow (sealed, IDisposable)

```csharp
GameWindow(string title, int width, int height, bool fullscreen = false)
void Run();   void Dispose();
IWindow Window;  GL Gl;  IInputContext Input;  IKeyboard Keyboard;  IMouse Mouse;   // set on Load
event Action? Load;  event Action<double>? Update;  event Action<double>? Render;
event Action<Vector2D<int>>? Resize;
```

Creates a **GL 4.1 core, forward-compatible** context (required on macOS) with
VSync on. `Gl`/`Input`/`Keyboard`/`Mouse` are null until `Load` fires. `Run()`
blocks on the Silk.NET loop: `Load` once, then `Update`→`Render` each frame,
`Resize` on framebuffer change (the viewport is updated for you before the event;
query framebuffer size on high-DPI displays for projection). All callbacks run on
the main thread — GL and input are main-thread only.

---

## 6. Engine.Audio — sound & music

A self-contained audio layer over **OpenAL** (Silk.NET.OpenAL + the OpenAL Soft
native binaries, which ship in-package for macOS/Windows/Linux — no system OpenAL
required). Depends only on `Engine.Core`; the game talks to it through one façade,
`AudioEngine`.

**Design rules**

- **Never crashes the game.** If no output device opens (headless CI, no audio
  hardware) the whole engine becomes a silent no-op — every method returns
  harmlessly and `Available` is `false`.
- **Three volume buses.** `MasterVolume` is the OpenAL *listener* gain, so it scales
  everything live (including already-playing voices); `SfxVolume` is multiplied into
  each one-shot at trigger time; `MusicVolume` is applied to the stream each frame.
- **Camera-relative positional audio.** The universe is double-precision, OpenAL is
  float. Positional sounds take a *camera-relative* offset in metres (world − camera,
  the same hand-off as `UniversePosition.ToCameraRelative`); the listener sits at the
  origin of that frame and is oriented from the camera basis (§9.2).
- **Not thread-safe.** Construct and call everything from the main/render thread,
  alongside GL.

### 6.1 AudioEngine (sealed, IDisposable)

```csharp
AudioEngine();                                   // opens device/context, or degrades to silent
bool Available { get; }                           // false ⇒ every call is a no-op
float MasterVolume / SfxVolume / MusicVolume { get; set; }   // [0,1] buses

Sound CreateSound(AudioClip clip);                // upload PCM into a reusable buffer
Sound LoadSound(string path);                     // = CreateSound(WavLoader.Load(path))
void  PlaySound(Sound s, float volume=1, float pitch=1);            // head-relative (UI cues)
void  PlaySoundAt(Sound s, Vector3 camRel, float volume=1, float pitch=1);  // positional, attenuated

void  PlayMusic(AudioClip clip, bool loop=true);  // replaces the current track
void  StopMusic();   bool MusicPlaying { get; }

void  SetListener(Vector3 position, Vector3 forward, Vector3 up);  // orient the listener
void  Update(double dt);                          // pump the music stream + apply live volumes
```

Internals: a fixed pool of **24 SFX voices** (sources). A play grabs the first idle
voice, or steals the next one round-robin when all are busy, so a fresh sound always
plays. `Update` is the only per-frame requirement — it drives the music stream.

### 6.2 Music streaming (`MusicStream`, internal)

Music plays through a 4-buffer ring fed from the clip's in-memory PCM, rather than one
giant static buffer. Benefits: small uploads, **gapless looping** (the read cursor just
wraps mid-chunk), and underrun recovery — if the source drains because a frame ran long,
`Pump` (called from `Update`) re-queues and restarts it. ~0.3 s chunks, snapped to a
whole audio frame.

### 6.3 WAV loading & the procedural synth

`WavLoader` is a dependency-free RIFF/WAVE reader for uncompressed PCM (8/16-bit,
mono/stereo) that walks the chunk list, so it tolerates LIST/INFO/fact chunks. It
produces an `AudioClip` (the engine-neutral payload: `byte[] Pcm`, sample rate,
channels, bits, plus the matching OpenAL `BufferFormat`).

The repo ships **no audio assets**, so `Synth` generates clips procedurally and these are
the fallback whenever a WAV is absent (see §9.1):

- `Blip()` — a short decaying sine for UI clicks.
- `CasualSpace(seconds, seed)` — a full **calm, looping ambient music track**, stereo,
  deterministic from a seed (reusing `DeterministicRng`). Three layers: a soft continuous
  **drone** (root+fifth+octave, snapped to whole cycles so it crosses the loop seam without
  a click); slow **7th-chord pads** that swell in and fade to silence within each slot (so
  the chord level is zero at the seam); and a sparse **bell melody** wandering a major-
  pentatonic scale, each note panned with two stereo echo taps. Seamlessness is by
  construction — the drone is phase-continuous and pads/melody are arranged to be silent at
  the seam (no note or echo is scheduled close enough to the end to ring across it). ~0.3 s
  to bake a 48 s track.
- `AmbientPad()` — the original simple loop-aligned drone, kept as a minimal alternative.

---

## 7. Game.Universe — procedural generation

The data flow, top to bottom:

```
WorldSeed
 ├─ GalaxyModel ............ stellar-density field over the disk
 ├─ StarCatalog (per block) → Star ........ a star (id, position, class, colour…)
 │    via StarCatalogPager (streaming) / StarId (id pack) / INearestStar
 ├─ SystemGenerator(Star) → SolarSystem → Planet/Moon (: CelestialBody)
 │    └─ AtmosphereModel.Derive(body), AsteroidBelt, PlanetRing
 ├─ PlanetTerrain(body) ..... height + colour field, backed by Noise
 ├─ NebulaField ............. fly-to gas clouds
 └─ BackdropStars ........... direction-only sky dome
Tuning globals: TerrainTuning · BiomeTuning · PlanetTuning · NebulaTuning · EnvTrait/Spawner
```

### 7.1 Stars & the lattice

**GalaxyModel** (sealed) — the large-scale density function.
`GalaxyModel(ulong worldSeed)`; `double DensityAt(Vector3D<double> meters)`
returns stars/ly³ via exponential radial+vertical falloff modulated by
logarithmic spiral arms. Tunable fields: `BaseDensity`, `ScaleLength`,
`ScaleHeight`, `ArmCount`, `ArmStrength`, `ArmWind`.

**StarId** (static) — invertible global id.
`ulong Pack(Vector3D<long> block, int local)` /
`void Unpack(ulong id, out block, out local)`. Layout: 21 local bits, 14 bits per
block axis. Home block `(0,0,0)` keeps ids `0…N-1`.

**StarCatalog** (sealed) — one 350 ly cube of stars with a spatial grid.
`StarCatalog(GalaxyModel, Vector3D<long> blockCoord)` generates deterministically;
`bool TryGetLocal(int, out Star)`; `void Collect(in camera, radius, List<Star>
visible, ref bestSq, ref nearest, ref hasNearest)` gathers stars in range and
tracks the nearest across blocks. Exposes `Stars`, `Count`, `Origin`, `BlockCoord`.

**StarCatalogPager** (sealed, the live source — implements `INearestStar`).
`StarCatalogPager(GalaxyModel)`; `void Update(in camera, double trackRadiusMeters)`
loads blocks within 1 Chebyshev block (camera's own block synchronously, the rest
off-thread) and evicts beyond 2; rebuilds `Visible` + `Nearest`.
`bool TryGetStar(ulong id, out Star)` loads any star's block on demand (the
find-by-number feature). `StarCatalog EnsureLoaded(Vector3D<long>)`. Exposes
`Visible`, `LoadedBlocks`, `LoadedBlockCount`, `LoadedStarCount`, `HasNearest`,
`Nearest`, `NearestDistanceMeters`.

**Star** (readonly struct) — `Id`, `Position`, `Temperature`, `Luminosity`,
`MassSolar`, `RadiusMeters`, `Class` (`SpectralClass` M…O), `Color` (from
`Blackbody.ColorOf`), plus `SizeCue`, `ClassLetter`, `Designation`.

**BackdropStars** (sealed) — `BackdropStars(ulong worldSeed, int count = 9000)`
builds an interleaved `float[]` (`FloatsPerStar = 7`: dir3+colour3+size) of
direction-only distant stars concentrated on the galactic plane.

**StarField** (legacy infinite cell-streamer, also `INearestStar`) — superseded by
the pager; retained as a fallback.

### 7.2 Systems & bodies

**SystemGenerator** (static): `SolarSystem Generate(Star star)` — seeded from
`star.Id`: snow line `2.7·√L` AU, Poisson planet count (~3.2, clamped 1–9),
Titius–Bode-like spacing, type by distance vs. snow line, radius/mass per type,
Keplerian mean motion from star mass, moons (own RNG stream), optional rings (35%
for giants), `AtmosphereModel.Derive`, and a main belt (~75%).

**SolarSystem** (sealed): `Sun`, `Planet[] Planets`, `AsteroidBelt? Belt`;
`MaxOrbitRadius`, `MaxPlanetRadius`, `MaxOrbitalSpeedMps`;
`void UpdatePositions(double t)` advances every body to sim-time `t`;
`IEnumerable<CelestialBody> AllBodies()` yields planets then their moons.

**CelestialBody** (abstract base for `Planet`/`Moon`): seed, type, designation,
Keplerian elements (`SemiMajorAxis`, `Inclination`, `AscendingNode`, `Phase`,
`MeanMotion`), `RadiusMeters`/`MassKg`, colours/albedo, atmosphere fields
(`HasAtmosphere`, `SurfacePressureBar`, `AtmosphereColor/Height/Density`,
`MieColor/Density`), `SurfaceTempK`, `Habitable`, composition arrays,
rotation (`RotationPeriodSeconds`, `SpinAxis`), `CurrentPosition`. Computed:
`SurfaceGravity`, `PeriodSeconds`, `HasSurface`, `HasLiquidWater`,
`EnvTrait Traits`; `Vector3D<double> OrbitalOffset(double t)`;
`Vector3D<float> ApparentSunDir(trueSunDir, simTime)`. `PlanetType` enum:
Lava, Rocky, Desert, Ocean, Ice, IceGiant, GasGiant.

**AtmosphereModel** (static): `void Derive(CelestialBody body)` turns scanned
chemistry into optics — molar mass → scale height (Boltzmann barometer, ×26 for
visible limb), Rayleigh strength ∝ pressure·refractivity², absorption tint from
coloured gases (methane→cyan), Mie haze from sulphur + lofted surface dust. This
is why the scanner readout and the rendered sky always agree.

### 7.3 Terrain

**PlanetTerrain** (sealed) — the height + colour field for one body.
`PlanetTerrain(CelestialBody body)`. Key calls:

| Signature | Notes |
|---|---|
| `double HeightAt(Vector3D<double> unitDir)` | full-detail height (m) |
| `double HeightAt(Vector3D<double> unitDir, double sampleSpacing)` | **LOD-aware** — octaves clamped to patch vertex spacing (smooth fade, no popping) |
| `void HeightAt2(unitDir, fineSpacing, coarseSpacing, out fine, out coarse, out craterFine)` | dual-resolution in one pass (~2× cheaper) for geomorphing |
| `GpuTerrainParams GpuParams()` | all deterministic params the GPU tile shader needs (CPU/GPU paths match) |
| `Vector3D<float> ColorAt(unitDir, sampleSpacing, craterValue)` | elevation/slope/Whittaker-biome ramp |
| `double AverageAlbedo()` | mean colour for the distant sphere |
| `Amplitude`, `MacroReliefFrequency`, `IsCratered`, `CraterOrbitalFrequency`, `FinestGeometryWavelength` | bounds/frequencies for orbital-relief shaders |

Layers blended (each octave-clamped by frequency): continents (fBm) · ridged
mountains (domain-warped, masked by a low-freq ruggedness field) · eroded detail ·
sub-metre micro-relief · impact craters (cellular cascade) · volcano cones (lava) ·
strata terracing. Per-type weights give lava jaggedness, rocky ranges, desert
mesas, smoother ice, etc.

**Noise** (sealed) — `Noise(ulong seed)`; deterministic 3D value noise hashed from
integer lattice (no textures). `Value`, `Fbm` (int + fractional octaves), `Fbm2`
(dual cutoff), `Ridged`/`Ridged2`, `ErodedFbm`/`ErodedFbm2` (per-octave gradient
damping → carved valleys), `ValueD` (value+gradient), `CellFeature` (jittered
crater centres). Fractional-octave variants are *continuous* in octave count,
which is what makes terrain LOD pop-free.

### 7.4 Nebulae

**NebulaField** (sealed): `NebulaField(ulong worldSeed, int count = -1)` — `-1`
reads `NebulaTuning.Count`. Scatters clouds over ±6000 ly radially / ±1800 ly
vertically (sqrt-radius for uniform area, power-biased height toward the plane);
radius drawn from `[NebulaTuning.MinRadiusLy, MaxRadiusLy]` (clamped so the range
can't invert); colour from an emission palette + jitter. Exposes
`IReadOnlyList<Nebula> Nebulae`.

**Nebula** (readonly struct): `Position`, `RadiusMeters`, `Color`, `Seed`, `Name`.

Because count/radius are *generation* inputs, changing them requires rebuilding
the field (`new NebulaField(WorldSeed)`); both consumers (`NebulaRenderer` and the
galaxy map) read `Nebulae` fresh each frame, so a reassignment is all it takes.

### 7.5 Asteroids

**AsteroidBelt** (sealed): `static bool Rolls(Star)` (cheap pre-check),
`static AsteroidBelt? Generate(star, snowLineAu, starMassKg)`,
`void Collect(t, in star, in camera, maxDist, List<AsteroidInstance>)`.
**AsteroidField** (sealed, deep-space cluster): `static Generate(center, seed)`,
`Collect(t, camera, maxDist, output)`. **PlanetRing** (sealed): `static Generate(Planet)`,
`Collect(...)`. **AsteroidInstance** (struct) carries camera-relative pos, radius,
colour, shape seed, spin axis/angle for the renderer.

### 7.6 Tuning globals

- **TerrainTuning** (static) — live *multipliers* on seeded terrain + render knobs:
  `GpuTerrain` (bool, default on), `ReliefScale`, `MountainScale`, `FrequencyScale`,
  `CraterScale`, `CraterDensity`, `CraterAlbedo`, `MariaStrength`, `LavaGlow`,
  `CityGlow`, micro/detail-normal fields, `LodDistanceFactor`, orbital-relief
  fields, `ScatterRange`, etc. Defaults of 1.0 reproduce seeded values exactly.
- **BiomeTuning** (static) — colour ramp: `SnowLine`, `CliffThreshold`,
  `CliffStrength`, `LowlandTint`, `RockColor`, `SnowColor`, `CliffColor`.
- **PlanetTuning** (static) — per-`PlanetType` override `Profile`s;
  `For(type)`, `ResetAll()`, `EffectiveRelief/Mountain/…(type)` fall back to the
  globals when an override is disabled.
- **NebulaTuning** (static) — `int Count = 40`, `float MinRadiusLy = 120`,
  `float MaxRadiusLy = 400` (changing any requires a field rebuild).
- **EnvTrait** (flags) — `Surface`, `Atmosphere`, `Life`, `Water`, `Molten`,
  `Frozen`; `body.Traits` gates which scatter `Spawner`s are eligible.

---

## 8. Game.Systems — runtime lifecycle

**SolarSystemManager** (sealed) — owns the single active system.
`void Update(double dt, in UniversePosition camera, INearestStar field)`:
despawns the active system past `DespawnLightYears`, spawns the nearest star's
system inside `SpawnLightYears` (the gap is hysteresis against thrashing), and
advances `SimTime += dt·TimeScale` then `Active.UpdatePositions(SimTime)`.
Exposes `HasActive`, `Active`, `ActiveStarId`, `SimTime`, `ActiveStarDistanceLy`,
and the tunables `SpawnLightYears`, `DespawnLightYears`, `TimeScale`.

**SpeedPolicy** (static) — the proximity speed limiter, stateless math:

```csharp
double EngageDistance(double radiusMeters, bool isStar)   // radius × (isStar? 1e6 : 4e3)
double RateFor(bool isStar)                               // isStar? 1.2 : 3.0  (m/s per metre)
double Cap(double distToSurface, double engageDist)                  // uses ApproachRate
double Cap(double distToSurface, double engageDist, double rate)
```

`Cap = rate·d / (1 − d/engage)`, floored at `MinApproachSpeed` (10 km/s) and
`+∞` outside the zone. The denominator singularity at the zone edge means the
limit rises *continuously* on entry (no cliff); the proportional numerator makes
descent self-converging (each second covers a fraction of the remaining gap, so
you can't overshoot). The main loop takes the **minimum** cap over the nearest
star and every body in the active system, plus the smallest surface gap for
anti-tunnelling, and feeds both to the controller via `SetSpeedContext`.

**AsteroidFieldManager** (sealed) — streams deep-space clusters on a coarse grid
(`CellSizeSectors = 32_768` ≈ 0.52 ly; ~20% of cells roll a cluster).
`Update(in camera, int radiusCells)` rebuilds `Visible` and evicts distant cells;
`Collect(t, camera, maxDist, output)` gathers camera-relative rocks;
`bool TryFindNearest(in from, int searchCells, out AsteroidField)` backs the
jump-to-cluster key.

---

## 9. Game.App — the application & main loop

`Program` is a static class holding every subsystem. `GameWindow` events drive
three phases.

### 9.1 OnLoad — construction order

Camera + `FreeFlyController` → `StarCatalogPager`/`StarRenderer` → `GalaxyBackdrop`
→ `NebulaField`/`NebulaRenderer` → `SolarSystemManager`/`SystemRenderer` →
terrain (`PlanetTerrainRenderer`, tile cache, `ScatterRenderer`, `PlanetSurfaceMap`)
→ rover, asteroid/glow/grid/black-hole renderers → atmosphere/cloud →
framebuffers (`SceneFramebuffer`, `ColorTarget`, `BloomRenderer`) → `ImGuiController`,
`StarOverlay` → `TuningConfig.Load("tuning.json")` (and, because it may change
nebula count/radius, the field is rebuilt right after a successful load) →
`InitAudio()`.

`InitAudio()` brings up the `AudioEngine`, primes the UI click (`Assets/Audio/blip.wav`
if shipped, else `Synth.Blip()`), and **starts the soundtrack, looped** — `music.wav` if
shipped, otherwise the procedural `Synth.CasualSpace()` ambient track (`LoadMusic()` picks
between them). It's pleasant to leave on and is easily silenced or balanced in the
Tuning ▸ Audio panel. Music is skipped in `--smoke` runs.

### 9.2 OnUpdate(dt) — simulation

Edge-detected key toggles (Tab capture, F scanner, R rover, H HUD, P grid, M/N
maps, B/J jumps, Space pause, `,`/`.` time-lapse) → if not driving/orbiting,
`_controller.Update(dt)` → `_starPager.Update` → `_asteroidFields.Update` →
`_systemManager.Update(dt, camera, _starPager)` → rover step *or* carry the camera
along a moving body's frame → `PickTerrainTarget()` sets the terrain body and
focus → `UpdateSpeedLimit()` (the `SpeedPolicy` reduction above) → FPS bookkeeping
→ audio: `SetListener(0, camera.Forward, camera.Up)` then `_audio.Update(dt)` (pumps
the music stream). Map toggles and belt/cluster jumps fire a UI `Blip()`.

### 9.3 OnRender(dt) — the frame, in order

The scene is drawn into an offscreen `SceneFramebuffer` (colour + sampleable
depth), then depth-aware effects composite in a `ColorTarget`, then bloom and the
HUD go to screen. The depth buffer is **cleared between unrelated passes** so each
can fit its own near/far for precision.

```
SceneFramebuffer.Bind(); clear color+depth
  backdrop.Render(camera)                       // Milky-Way band + dome stars (additive, no depth)
  nebulaRenderer.Render(camera, nebulaField)    // fly-to clouds (additive, no depth)
  starRenderer.SyncBlocks(...); RenderCatalog(camera, activeStarId)
  [deep-space asteroids, fitted projection];  clear depth
  galacticGrid.Render(camera) [if on];  blackHole.Render(camera);  clear depth
  if active system:
     surfaceMap.Request/Update                  // bakes the focused body's albedo/normal
     systemRenderer.Render(camera, system, terrainTarget, surfaceMap)
     planetGlow.Render(...) / RenderStar(...)
     [belt + ring asteroids, depth-tested];  clear depth
  if terrainTarget: terrainRenderer.Render(camera, sunDir, clock, surfaceMap)
                    roverRenderer.Render(...) [if driving]
                    scatter.Render(...)
SceneFramebuffer.BlitColorTo(postFbo)
  cloudRenderer.Render(... depthTex, near, far ...)     // raymarched, depth-clamped
  atmosphereRenderer.Render(... depthTex, near, far ...) // volumetric scattering
bloom.Render(postFbo.ColorTexture); bloom.Composite(scene, bloom)   // to screen
HUD: overlay.Draw + DrawHud/DrawTuning/DrawScanner/DrawSystemMap/DrawGalaxyMap; imgui.Render()
```

### 9.4 Renderers (key entry points)

All take `Camera` and compute camera-relative geometry; most fit their own near/far.

| Renderer | Entry point | Draws |
|---|---|---|
| **StarRenderer** | `RenderCatalog(camera, ulong? excludeId)`, `SyncBlocks(loadedBlocks)`, `Render(camera, visible, bubbleMeters, excludeId)` | additive point sprites; magnitude falloff `bright·flux^gamma`; skips the active sun. Tunables: `CatBrightScale/Gamma/SizeScale`, min/max size |
| **GalaxyBackdrop** | `Render(camera)` | fullscreen band (fBm dust + arms + bulge), 6 painted nebula patches, dome stars. `Enabled`, `BandBrightness`, `StarBrightness`, `NebulaBrightness` |
| **NebulaRenderer** | `Render(camera, NebulaField)` | additive fBm billboards, fade-on-entry; wide depth-irrelevant projection. `Enabled`, `Intensity` |
| **SystemRenderer** | `Render(camera, system, terrainBody?, map?)` | emissive sun, lit planets/moons (procedural relief/craters/maria or gas bands, or the baked map for the focused body), banded rings + planet shadow, faint orbit rings. `LastNear/LastFar` feed atmosphere |
| **PlanetTerrainRenderer** | `Render(camera, sunDir, renderClock, map)`; `SetBody(body)`, `FocusPoint`, `Rebuild()`, `TrySurfaceHeight(dir, out h)` | cube-sphere quadtree LOD with geomorph; GPU tile path by default (`TerrainTileGenerator`/`TerrainTileCache` bake height/normal/albedo into an atlas; vertex texture fetch displaces). Counts: `PatchCount`, `LeafCount`, `GrassLeaves` |
| **ScatterRenderer** | `Render(camera, body, terrain, sunDir, near, far, clock)`; `Spawners`, `InvalidateActivation()` | instanced surface objects sampling the same height tile; per-layer mesh/density/size/orient + `EnvTrait`/spawn-chance/altitude gating |
| **AtmosphereRenderer** | `Render(camera, system, depthTex, near, far, terrainBody, amplitude, simTime, eclipse)` | fullscreen Rayleigh+Mie ray-march, depth-clamped. `RayleighStrength`, `MieStrength`, `MieG`, `SunIntensity`, `Exposure`, `HeightScale`, `DebugTransmittance` |
| **CloudRenderer** | `Render(camera, system, depthTex, near, far, clock, body, amplitude, composite)`; `Resize` | half-res raymarched shell. `Volumetric`, `Coverage`, `Density`, `WindSpeed` |
| **BloomRenderer** | `Render(srcTex) → bloomTex`; `Composite(sceneTex, bloomTex)`; `Resize` | soft-knee bright pass + separable Gaussian ping-pong. `Threshold`, `Knee`, `Intensity`, `Iterations` |
| **SceneFramebuffer** | `Bind()`, `BlitColorTo(fbo)`, `DepthTexture`, `Resize` | offscreen colour + sampleable depth |
| **AsteroidRenderer** | `Render(camera, instances, viewProj, sunRel, hasSun, viewportH)`; `static FitProjection(...)` | hybrid LOD: 3D tumbling rocks ↔ point sprites, cross-faded |
| **PlanetGlowRenderer** | `Render(camera, system, viewportH, terrainBody)`, `RenderStar(...)` | additive distance-scaled body markers that hand off to the real disk |
| **BlackHoleRenderer** | `Render(camera)` | event-horizon sphere + photon ring + banded, beamed accretion disk |
| **RoverRenderer** | `Render(camera, bodyPos, forward, up, wheelTravel, sunDir, near, far)` | chassis + 4 independently-sprung wheels |
| **GalacticGridRenderer** | `Render(camera)` | faint galactic-plane reference grid |

### 9.5 Controllers

**FreeFlyController** `(Camera, IKeyboard, IMouse)` — true 6-DOF: mouse yaw/pitch +
Q/E roll compose as incremental *local* quaternion rotations (no world-up, no
gimbal lock); WASD translate in local space. The wheel sets a logarithmic
`SpeedExponent` (`DesiredSpeed = 10^exp`); `SetSpeedContext(maxSpeed, approachGap)`
feeds the proximity cap so `CurrentSpeed = min(desired, cap)` and an
anti-tunnelling clamp keeps a step below `AntiTunnelFraction·gap`. `Update(dt)`.

**RoverController** `(Camera, IKeyboard, IMouse, PlanetTerrainRenderer)` —
`Enter(CelestialBody)` drops the rover under the camera; `Update(dt)` reads
throttle/steer/brake, steps the `Rover` sim, slews the render basis onto the
terrain normal, and orbits a chase camera (mouse yaw/pitch, up = surface normal).
Exposes `BodyPosition`, `Forward`, `Up`, `SpeedKph`, `WheelTravel`.

The **Rover** sim itself lives in `Game.Universe`: `Rover(radius, surfaceGravity,
Func<Vector3D<double>,double> heightAt)`, `Seed(groundDir, headingHint)`,
`Update(dt, throttle, steer, brake)`, `SurfaceBasis()`. It samples terrain height
at four wheel corners, fits a footprint normal, and settles a critically-damped
per-wheel suspension (absorbs bumps, never bounces) — pure kinematics, fully
testable.

### 9.6 TuningConfig — persistence

A serializable snapshot of every live knob (backdrop incl. `NebulaCount`/
`NebulaMinRadiusLy`/`NebulaMaxRadiusLy`, atmosphere, terrain globals, biome,
per-type overrides, scatter spawners).

```csharp
static TuningConfig Capture(AtmosphereRenderer a, GalaxyBackdrop b, ScatterRenderer s)
void Apply(AtmosphereRenderer a, GalaxyBackdrop b, ScatterRenderer s)
static bool Save(a, b, s, string path)     // → tuning.json
static bool Load(a, b, s, string path)     // ← tuning.json, then Apply
```

`Capture` reads live renderer state + the static tuning classes; `Apply` writes
them back (and replaces the scatter layer set only if the file carried one, so an
older file doesn't wipe defaults). Startup loads `tuning.json`; "Save settings"
captures and writes it. Because nebula count/radius are generation inputs,
`Program` rebuilds the `NebulaField` after any load that may have changed them.

### 9.7 HUD / overlays

`StarOverlay.Draw(camera, pager, systems, searchTarget?)` paints reticles, labels
and the nearest-star arrow. ImGui panels: `DrawHud` (position/speed/FPS/system,
find-star box), `DrawTuning` (all sliders + save/load, including the **Audio** panel:
master/music/sfx volume, music on/off, Test-SFX — see §6), `DrawScanner` (body / star
/ sector readout, F), `DrawSystemMap` (top-down system, M, with nebula disks and
click-to-travel), `DrawGalaxyMap`/3D neighbourhood (N).

---

## 10. Extending the engine — quick recipes

- **Add a procedural feature:** generate it as a pure function of `WorldSeed`
  (+ coords/id) using its own `DeterministicRng` stream; never store it, never
  perturb another subsystem's stream.
- **Add a renderer:** take `Camera`, convert positions with `ToCameraRelative`,
  fit your own near/far, and slot a `Render(...)` call into the OnRender order in
  §9.3 at the right depth-clear boundary. Use additive + no-depth for glows.
- **Add a tunable:** put the field on the relevant static tuning class (or
  renderer), add a slider in `DrawTuning`, and add it to `TuningConfig`'s
  `Capture`/`Apply` (+ a DTO field) so it persists. If it's a *generation* input,
  rebuild the affected object after `Load` — see the nebula precedent in §9.6.
- **Query the nearest star/body:** depend on `INearestStar` (the pager), not a
  concrete field, so legacy/streamed sources are interchangeable.
- **Play a sound or music:** load a WAV (`_audio.LoadSound`/`WavLoader`) or synth a
  clip, then `PlaySound` (UI, head-relative) / `PlaySoundAt(sound, camRel)` (in-world,
  pass `body.CurrentPosition.ToCameraRelative(camera.Position)`) / `PlayMusic`. Guard
  nothing — the engine no-ops when `Available` is false. See §6.

---

*Generated as a structural reference; for exact, current signatures consult the
source — the public surface evolves with the milestones in the README.*
