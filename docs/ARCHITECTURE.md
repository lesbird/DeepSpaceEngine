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
Game.Universe      procedural generation (galaxies→stars→terrain) → Engine.Core
Game.Systems       runtime lifecycle: systems, speed, fields,  → Universe, Core
                   Discovery/ (networked discovery client)
Game.App           entry point, main loop, all renderers, HUD  → everything
Engine.Core.Tests  xUnit: precision, determinism, sim          → Core, Universe…
server/            PHP + MySQL discovery API + HTML log         (standalone)
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
 ├─ GalaxyField ............. cosmic galaxy density
 ├─ GalaxyCatalog (per block) → Galaxy ..... a galaxy (id, centre, type, seed, radius…)
 │    via GalaxyCatalogPager (streaming) / GalaxyId / INearestGalaxy
 │    └─ GalaxyModel(Galaxy) ... that galaxy's stellar density (disk/arms/bulge)
 │    └─ GalaxyCloud(Galaxy) ... volumetric point-cloud LOD
 │    └─ GalaxyGlow(Galaxy) .... nebula-style body-glow puffs (bulge + disk)
 │    └─ GlobularClusters.ForGalaxy(Galaxy) → GlobularCluster (halo knots)
 ├─ StarCatalog (per block, GATED to a galaxy) → Star ..... a star
 │    via StarCatalogPager (streaming) / StarId / INearestStar
 │    └─ injects GlobularCluster stars where a cluster overlaps the block
 ├─ SystemGenerator(Star) → SolarSystem → Planet/Moon (: CelestialBody)
 │    └─ AtmosphereModel.Derive(body), AsteroidBelt, PlanetRing
 ├─ PlanetTerrain(body) ..... height + colour field, backed by Noise
 ├─ NebulaField(Galaxy) ..... fly-to gas clouds (per galaxy)
 └─ BackdropStars ........... direction-only sky dome
Tuning globals: TerrainTuning · BiomeTuning · PlanetTuning · NebulaTuning · EnvTrait/Spawner
```

### 7.1 The galaxy hierarchy & the star lattice

The universe is a hierarchy: a sparse lattice of **galaxies**, each containing a streamed **star
lattice**. The galaxy level mirrors the star level one tier up (catalog block + distance pager +
packed id), so the same patterns recur.

**Galaxy** (readonly struct) — `Id`, `Center` (`UniversePosition`), `Type` (`GalaxyType`
Spiral/Elliptical/Irregular/Dwarf), `Seed`, `RadiusMeters`, `DiskNormal`, `StarCount`, `Color`, `Name`.

**GalaxyField** (sealed) — the cosmic galaxy density: `double DensityAt(meters)` (≈ uniform; the
default ~5.8e-22/ly³ gives a ~12 Mly mean separation — true scale).

**GalaxyCatalog** (sealed) — one ~20 Mly lattice block; Poisson-places galaxies, and **forces the
Milky Way at the universe origin** in block `(0,0,0)` (Spiral, 50 kly radius, seed = `WorldSeed`). Exposes `Galaxies`, `Count`, `Origin`, `BlockCoord`.

**GalaxyCatalogPager** (sealed, implements `INearestGalaxy`) — `Update(in camera)` pages galaxy blocks
(9×9×9 resident, evict at 5 — a deep field for the universe map / intergalactic view), tracks `Nearest`
and the galaxy the camera `IsInside`/`Containing`.
`bool TryGetGalaxyForBlock(point, boundingRadius, out Galaxy)` — which galaxy a star-lattice block
belongs to (the gate that confines stars to galaxies).
`bool TryGetAimedGalaxy(in camera, forward, out Galaxy, out distance, tolerance)` — the galaxy under the
centre reticle (smallest angular offset within the galaxy's own apparent radius + a small cone), for the
intergalactic HUD readout. Exposes `LoadedBlocks`, `LoadedGalaxyCount`. The resident radius is kept
larger than `GalaxyRenderer`'s far-fade so distant galaxies fade out before a block can pop in/out.

**GalaxyId** (static) — invertible `(block, local) ↔ ulong`, like `StarId` (16 local + 3×16 axis bits).

**GalaxyModel** (sealed) — **one galaxy's** stellar-density field. `GalaxyModel.For(Galaxy)` builds it
(centre + disk normal + type-scaled shape); `DensityAtLocal(offsetFromCentre)` returns stars/ly³ via
exponential radial+vertical fall-off × logarithmic spiral arms, zeroed beyond the galaxy radius. The
Spiral/50 kly case reproduces the tuned Milky-Way values, so the home galaxy is unchanged.

**GalaxyCloud** (static) — `float[] Generate(Galaxy, count)`: a galaxy's volumetric point cloud (disk +
arm-biased angle + bulge), offsets-from-centre + colour + size, for the near-LOD render tier.

**GalaxyGlow** (static) — `GalaxyGlowPuff[] ForGalaxy(Galaxy)`: the galaxy body as a handful of large
nebula-style fBm "puffs" — one concentrated warm bulge + ~18 cooler arm-following disk puffs (offset,
radius, colour, brightness, seed, `Core` 0=wispy/1=bulge), following the same disk model. Rendered as
overlapping additive billboards so a galaxy reads as billowing volumetric gas up close (see §9.4).

#### Stars & the lattice (confined to galaxies)

**StarId** (static) — invertible global id.
`ulong Pack(Vector3D<long> block, int local)` /
`void Unpack(ulong id, out block, out local)`. Layout: 21 local bits, 14 bits per
block axis. (The `(0,0,0)`-keeps-`0…N-1` claim is aspirational — the axis bias makes block 0 a fixed,
non-zero field; ids are simply stable and invertible.)

**StarCatalog** (sealed) — one 350 ly cube of stars with a spatial grid, **populated from the host
galaxy**. `StarCatalog(Galaxy, Vector3D<long> blockCoord)` samples the galaxy's density once at the
block centre and places that many stars (macro disk/arm/edge structure emerges from per-block density);
`StarCatalog(Vector3D<long>)` is the empty (intergalactic) block. It also **injects globular-cluster
stars** where a halo cluster overlaps the block (`CollectClusterStars`, shared per-star generator with
`GlobularClusters.Stars`), so cluster stars are real catalog stars. `bool TryGetLocal(int, out Star)`;
`void Collect(in camera, radius, …)` gathers stars in range + tracks the nearest. Exposes `Stars`,
`Count`, `Origin`, `BlockCoord`.

**StarCatalogPager** (sealed, the live source — implements `INearestStar`).
`StarCatalogPager(GalaxyCatalogPager)`; `void Update(in camera, double trackRadiusMeters)`
resolves each block's galaxy (on the main thread, captured into the off-thread gen), loads blocks
within 1 Chebyshev block and evicts beyond 2; blocks with no galaxy are empty. Rebuilds `Visible` +
`Nearest`. `bool TryGetStar(ulong id, out Star)` loads any star's block on demand (find-by-number).
`StarCatalog EnsureLoaded(Vector3D<long>)`. Exposes `Visible`, `LoadedBlocks`, `LoadedBlockCount`,
`LoadedStarCount`, `HasNearest`, `Nearest`, `NearestDistanceMeters`.

**Star** (readonly struct) — `Id`, `Position`, `Temperature`, `Luminosity`,
`MassSolar`, `RadiusMeters`, `Class` (`SpectralClass` M…O), `Color` (from
`Blackbody.ColorOf`), plus `SizeCue`, `ClassLetter`, `Designation`, and `Name` (from `Naming`).

**Naming** (static) — deterministic display names from a 64-bit id, regenerated never stored.
`StarName(id)` builds an evocative two-word name (*Helix Prime*, *Crimson Vega*) from curated word lists
× patterns — a large but deliberately **non-unique** space (the id stays the unique handle; the name is
flavour). `GalaxyName(id)` builds a catalog-style designation (*M 31*, *NGC 224*, *UGC 12158*), weighted
toward the larger real catalogs. The Milky Way keeps its literal name (special-cased in `GalaxyCatalog`).

**GlobularCluster** (readonly struct) + **GlobularClusters** (static) — `ForGalaxy(Galaxy)` places
~200 clusters in a galaxy's spheroidal outer halo (40–200 ly across); `StarRng(c,i)` + `StarOffset` is
the **shared per-star generator** used by both the decorative cloud (`Stars(c)`) and the catalog
injection, so the resolved cloud and the real stars co-locate. `StarOffset` uses a density-∝-r⁻²
profile **softened by a flat ~0.05 ly core** (`rr = sqrt((R·u)² + core²)`) so the dense centre stays
bright yet keeps its stars spaced well beyond the ~500 AU system-spawn range — i.e. flyable. `FloatsPerStar = 7`.

**BackdropStars** (sealed) — `BackdropStars(ulong worldSeed, int count = 9000)`
builds an interleaved `float[]` (`FloatsPerStar = 7`: dir3+colour3+size) of
direction-only distant stars concentrated on the galactic plane.

**StarField** (legacy infinite cell-streamer, also `INearestStar`) — superseded by
the pager; retained for tests + `SampleStarType`.

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

**NebulaField** (sealed): `NebulaField(Galaxy galaxy, int count = -1)` — placed in **that galaxy's**
disk frame (centre + disk normal, seeded from `galaxy.Seed`), so every galaxy has its own nebulae; the
parameterless ctor is the empty (intergalactic) field. Scatters clouds over the inner disk (~12% of the
galaxy radius, thin vertical spread; sqrt-radius for uniform area, power-biased height toward the
plane); radius drawn from `[NebulaTuning.MinRadiusLy, MaxRadiusLy]` (clamped so the range can't invert);
colour from an emission palette + jitter. Exposes `IReadOnlyList<Nebula> Nebulae`. (`Program` rebuilds
the field when the camera enters a different galaxy.)

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
despawns the active system past `DespawnAu`, spawns the nearest star's system
inside `SpawnAu` (≈500 AU — holds the whole system, whose outermost planet reaches
~100 AU; the gap to despawn is hysteresis against thrashing), and advances
`SimTime += dt·TimeScale` then `Active.UpdatePositions(SimTime)`. Exposes
`HasActive`, `Active`, `ActiveStarId`, `SimTime`, `ActiveStarDistanceAu`, and the
tunables `SpawnAu`, `DespawnAu`, `TimeScale`.

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
(`CellSizeSectors = 32_768` ≈ 0.52 ly; ~20% of cells roll a cluster, each ~1800–4800 rocks generated
synchronously). `Update(in camera, int radiusCells)` rebuilds `Visible` and evicts distant cells — but
**short-circuits when the camera's cell jumped farther than the visible bubble** since last frame (i.e.
faster than any 0.5 ly cluster could be seen), so intergalactic warp doesn't burn the main thread
generating clusters that whip past invisibly. `Collect(t, camera, maxDist, output)` gathers
camera-relative rocks; `bool TryFindNearest(in from, int searchCells, out AsteroidField)` backs the
jump-to-cluster key.

**Discovery** (`Game.Systems/Discovery/`) — the client half of the networked
discovery system (full design in §10): `ObjectId` (the shared string-id scheme),
`DiscoveryRecord`/`ReportRequest`/`DiscoveryResult`, the async `DiscoveryClient`
(REST transport), and `DiscoveryService` (thread-safe cache + report policy).

---

## 9. Game.App — the application & main loop

`Program` is a static class holding every subsystem. `GameWindow` events drive
three phases.

### 9.1 OnLoad — construction order

Camera + `FreeFlyController` → `GalaxyCatalogPager` → `StarCatalogPager`(galaxyPager)/`StarRenderer`
→ `GalaxyRenderer`/`GlobularClusterRenderer` → `GalaxyBackdrop`
→ `NebulaField`(empty)/`NebulaRenderer` → `SolarSystemManager`/`SystemRenderer` →
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
`_galaxyPager.Update(camera)` (then rebuild nebulae + globular clusters if the host galaxy changed) →
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
  backdrop.ExternalDim = …; backdrop.Render(camera)  // painted sky; recedes when a real galaxy cloud is up
  galaxyRenderer.Render(camera, galaxyPager, sceneFbo, w, h)  // point → impostor → body glow (glow at half-res); cloud off by default
  nebulaRenderer.Render(camera, nebulaField, sceneFbo, w, h)  // fly-to clouds, rendered half-res & composited up (additive, no depth)
  starRenderer.SyncBlocks(..., clock); RenderCatalog(camera, activeStarId)   // per-block fade-in; frustum-culled + distance-thinned
  globularRenderer.Render(camera, viewportH)    // halo clusters: fuzzy sprite ↔ resolved star cloud
  [deep-space asteroids, fitted projection];  clear depth
  galacticGrid.Render(camera) [if on];  if inside a galaxy: blackHole.Render(camera, galaxy.Center);  clear depth
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
HUD: overlay.Draw + overlay.DrawGlobularReticles(camera, globularRenderer.Marks)
     + if inside a galaxy: overlay.DrawGalaxyCenterReticle(camera, galaxy.Center)        // CORE/SMBH reticle
     + else if TryGetAimedGalaxy: overlay.DrawAimedGalaxyReticle(camera, galaxy, dist)   // name/shape/stars/distance
     + DrawHud/DrawTuning/DrawScanner/DrawSystemMap/DrawGalaxyMap/DrawUniverseMap; imgui.Render()
```

### 9.4 Renderers (key entry points)

All take `Camera` and compute camera-relative geometry; most fit their own near/far.

| Renderer | Entry point | Draws |
|---|---|---|
| **StarRenderer** | `RenderCatalog(camera, ulong? excludeId)`, `SyncBlocks(loadedBlocks, now)`, `Render(camera, visible, bubbleMeters, excludeId)` | additive point sprites; magnitude falloff `bright·flux^gamma`; **per-block temporal fade-in** (streamed blocks ramp in over ~0.7 s, no chunk-pop); skips the active sun. **Frustum-culls** resident blocks (planes from the view-proj) and **distance-thins** far blocks (draws a shrinking front-prefix — unbiased since stars are stored in uniform-random order), so a dense region draws a few million of its tens of millions resident, no visual change (`LastDrawn`/`LastBlocksDrawn`). Catalog positions baked **hi/lo float pairs** (compensated relative-to-camera, §11) so near stars don't jitter. Tunables: `CatBrightScale/Gamma/SizeScale`, min/max size |
| **GalaxyRenderer** | `Render(camera, galaxyPager, sceneFbo, w, h)` | other galaxies, cross-faded by apparent size: fuzzy **point** sprite (visible out to ~56 Mly, fading before the streaming pop-in distance) → oriented **impostor disk** (bulge + arms + fbm dust lanes, nearest few) → optional **volumetric cloud** (`GalaxyCloud`; **off by default** via `CloudEnabled` — inside a galaxy it looked flyable but isn't) + nebula-style **body glow** (`GalaxyGlow` puffs, nearest 2, per-puff fade-on-enter) **rendered to a half-res buffer and composited up** (≈4× less fill on the screen-filling fBm). An `insideAmount` gate fades faint distant galaxies as you enter one; skips point/impostor for the galaxy you're inside. Tunables: point/impostor/cloud/**glow** brightness, sizes, cloud point scale, `CloudEnabled` |
| **GlobularClusterRenderer** | `SetClusters(GlobularCluster[])`, `Render(camera, viewportH)` | halo clusters cross-faded by apparent size: **fuzzy sprite** (positioned, sized by angular radius) ↔ **resolved star cloud** (async-cached, near-faded to the real injected catalog stars). The sprite **holds at full strength until that cluster's cloud is actually resident**, so a fast approach (or a re-approach after eviction) never shows a gap between sprite and resolved stars. Exposes `Marks` for the HUD reticle. `SpriteBrightness`, `CloudBrightness`, `CloudPointScale` |
| **GalaxyBackdrop** | `Render(camera)` | fullscreen band (fBm dust + arms + bulge), 6 painted nebula patches, dome stars. `ExternalDim` (set per-frame — recedes once a real galaxy cloud is on screen). `Enabled`, `BandBrightness`, `StarBrightness`, `NebulaBrightness` |
| **NebulaRenderer** | `Render(camera, NebulaField, sceneFbo, w, h)` | additive fBm billboards, fade-on-entry; wide depth-irrelevant projection. **Half-res** (rendered into a quarter-pixel buffer, composited up), 3-octave fBm, and size-culled/capped to the prominent few — soft clouds upscale invisibly, overdraw falls ~5–6×. `Enabled`, `Intensity` |
| **SystemRenderer** | `Render(camera, system, terrainBody?, map?)` | emissive sun, lit planets/moons (procedural relief/craters/maria or gas bands, or the baked map for the focused body), banded rings + planet shadow, faint orbit rings. `LastNear/LastFar` feed atmosphere |
| **PlanetTerrainRenderer** | `Render(camera, sunDir, renderClock, map)`; `SetBody(body)`, `FocusPoint`, `Rebuild()`, `TrySurfaceHeight(dir, out h)` | cube-sphere quadtree LOD with geomorph; GPU tile path by default (`TerrainTileGenerator`/`TerrainTileCache` bake height/normal/albedo into an atlas; vertex texture fetch displaces). Counts: `PatchCount`, `LeafCount`, `GrassLeaves` |
| **ScatterRenderer** | `Render(camera, body, terrain, sunDir, near, far, clock)`; `Spawners`, `InvalidateActivation()` | instanced surface objects sampling the same height tile; per-layer mesh/density/size/orient + `EnvTrait`/spawn-chance/altitude gating |
| **AtmosphereRenderer** | `Render(camera, system, depthTex, near, far, terrainBody, amplitude, simTime, eclipse)` | fullscreen Rayleigh+Mie ray-march, depth-clamped. `RayleighStrength`, `MieStrength`, `MieG`, `SunIntensity`, `Exposure`, `HeightScale`, `DebugTransmittance` |
| **CloudRenderer** | `Render(camera, system, depthTex, near, far, clock, body, amplitude, composite)`; `Resize` | half-res raymarched shell. `Volumetric`, `Coverage`, `Density`, `WindSpeed` |
| **BloomRenderer** | `Render(srcTex) → bloomTex`; `Composite(sceneTex, bloomTex)`; `Resize` | soft-knee bright pass + separable Gaussian ping-pong. `Threshold`, `Knee`, `Intensity`, `Iterations` |
| **SceneFramebuffer** | `Bind()`, `BlitColorTo(fbo)`, `DepthTexture`, `Resize` | offscreen colour + sampleable depth |
| **AsteroidRenderer** | `Render(camera, instances, viewProj, sunRel, hasSun, viewportH)`; `static FitProjection(...)` | hybrid LOD: 3D tumbling rocks ↔ point sprites, cross-faded |
| **PlanetGlowRenderer** | `Render(camera, system, viewportH, terrainBody)`, `RenderStar(...)` | additive distance-scaled body markers that hand off to the real disk |
| **BlackHoleRenderer** | `Render(camera, UniversePosition center)` | the host galaxy's central SMBH (event-horizon sphere + photon ring + banded, beamed accretion disk); drawn only when inside a galaxy, at its centre |
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

A serializable snapshot of every live knob (the **galaxy LOD** renderer — point/impostor/cloud
brightness, sizes, cloud point scale; backdrop incl. `NebulaCount`/`NebulaMinRadiusLy`/
`NebulaMaxRadiusLy`; atmosphere; terrain globals; biome; per-type overrides; scatter spawners).
Old `tuning.json` files without the galaxy fields load to the matching defaults.

```csharp
static TuningConfig Capture(AtmosphereRenderer a, GalaxyBackdrop b, ScatterRenderer s, GalaxyRenderer g)
void Apply(AtmosphereRenderer a, GalaxyBackdrop b, ScatterRenderer s, GalaxyRenderer g)
static bool Save(a, b, s, g, string path)     // → tuning.json
static bool Load(a, b, s, g, string path)     // ← tuning.json, then Apply
```

`Capture` reads live renderer state + the static tuning classes; `Apply` writes
them back (and replaces the scatter layer set only if the file carried one, so an
older file doesn't wipe defaults). Startup loads `tuning.json`; "Save settings"
captures and writes it. Because nebula count/radius are generation inputs,
`Program` rebuilds the `NebulaField` after any load that may have changed them.

### 9.7 HUD / overlays

`StarOverlay.Draw(camera, pager, systems, searchTarget?)` paints reticles, labels (now leading with each
object's **name**) and the nearest-star arrow; `DrawGlobularReticles(camera, marks)` adds a `GC` bracket
reticle + distance on each in-range globular cluster; `DrawGalaxyCenterReticle(camera, galaxy.Center)`
(when inside a galaxy) marks the **galactic core / SMBH** with a `CORE` reticle and an off-screen arrow;
`DrawAimedGalaxyReticle(camera, galaxy, dist)` (intergalactic) marks the galaxy under the centre reticle
with its **name, morphology, star count and distance** — all projected by direction, since the targets
sit past the far plane. The top **speed overlay** shows speed / target / **FPS + frame-time (ms)** /
**framebuffer resolution**. ImGui panels: `DrawHud` (position/speed/FPS/system, the **galaxy/cluster**
status line, a **travel-to-galaxy** list, find-star box, drawn-vs-resident star counts), `DrawTuning`
(all sliders + save/load, including the **Galaxies (LOD)** — point/impostor/cloud + body-glow, with the
**Render galaxy cloud** toggle — and **Globular clusters** sections and the **Audio** panel: master/music/
sfx volume, music on/off, Test-SFX — see §6), `DrawScanner` (body / star / sector readout, F, which now
**appends a host-galaxy section** — name, type, radius, star count, core distance — whenever inside a
galaxy), `DrawSystemMap` (top-down system, M, with nebula disks and click-to-travel),
`DrawGalaxyMap`/3D neighbourhood (N), and `DrawUniverseMap` (top-down chart of the **galaxies**, U,
click-to-jump via `GoToGalaxy`).

---

## 10. Discovery reporting — the networked layer

An optional "who discovered it first" layer over the deterministic universe (full plan in
[docs/DISCOVERY_PLAN.md](DISCOVERY_PLAN.md)). Because every player generates the **same**
universe, an object's identity is just a string id — the server stores ids and names, never
geometry. **Opt-in** (off by default; enable + name + server URL in Tuning ▸ Discovery).

### 10.1 Identity (`ObjectId`)

Strings, decimal, matching what the HUD shows:

```
star   = Star.Id                         "12407198355"
planet = "{starId}-{PP}"  (0-based idx)  "12407198355-02"
moon   = "{starId}-{PP}-{MM}"            "12407198355-02-03"
```

`ObjectId.TryFor(sys, body)` resolves a `CelestialBody` to its `(id, kind)` by locating it in
the active system's `Planets`/`Moons`.

### 10.2 Client (`Game.Systems/Discovery/`)

- **DiscoveryClient** — async `HttpClient` transport (`GetAllAsync`, `ReportAsync`); camelCase
  JSON; `X-Api-Key` on writes. Every call is try/caught and returns null on any failure, so
  the game degrades to "offline" and never throws.
- **DiscoveryService** — a thread-safe `ConcurrentDictionary` cache plus the report policy:
  first-finder-wins with **optimistic local credit** (reaching an unknown object credits you
  immediately, then POSTs; the server's authoritative reply overwrites it, so if someone beat
  you, *their* name shows). A `TryAdd` de-dupe guard ensures one send per object; failed sends
  go to a retry queue drained from `Update(dt)`. `InitializeAsync()` pulls the full list at
  launch. The render thread only reads the cache and fires async sends — the frame never blocks.

### 10.3 Game wiring (`Game.App`)

- **DiscoveryConfig** → `discovery.json` (gitignored; Enabled / PlayerName / ServerUrl / ApiKey),
  edited in the Tuning ▸ Discovery panel (toggle, fields, Save, Test connection, Re-sync, sync
  status).
- **Edge detection** (`Program.ReportDiscoveries`, each frame when enabled): **system entry** →
  `ReportStar` when `ActiveStarId` changes; **environment entry** → `ReportBody` once the nearest
  surfaced body's altitude drops below its shell `radius × (HasAtmosphere ? AtmosphereHeight :
  AirlessEnvShell=0.05)` — so airless worlds discover on entry too — with `> 1.2×` hysteresis.
  Both attach display `meta` (class/type/temp).
- **HUD credit** — `StarOverlay` reticles append `by {name}`; `DrawScanner` and the `DrawHud`
  system header show `Discovered by {name} on {date}` / `Undiscovered`.

### 10.4 Server (`server/`, PHP + MySQL)

One `discoveries` table; `UNIQUE(object_id)` *is* the first-finder-wins rule. `discover.php`
(POST, key-gated) upserts and returns the authoritative record + `isNew`; `discoveries.php`
(GET, open) lists all; `index.php` is a server-rendered log + leaderboard (no client JS, so the
decimal ids render exactly). See [server/README.md](../server/README.md) for deploy + `curl` tests.

---

## 11. Extending the engine — quick recipes

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
- **Query the nearest star/body/galaxy:** depend on `INearestStar` / `INearestGalaxy`
  (the pagers), not a concrete field, so legacy/streamed sources are interchangeable.
- **Render at galaxy scale:** camera-relative positions reach ~1e22 m, where a `float`
  component *squared* overflows (`>3.4e38`). Do every length/normalize/delta in `double`
  (or scale before `length()` in a shader, e.g. `length(world*1e-18)*1e18`) and cast only the
  small/unit result to `float`. Things past the far plane (galaxies, clusters) render
  direction-only on a dome radius or with a dedicated wide-far-plane projection (additive, depth off).
- **Precise positions in a static GPU buffer:** baking a position as one `float` relative to a far
  origin loses precision (a 350 ly block origin ⇒ ulp ~1.8 AU; the per-frame `pos − camRel` then
  *jumps* as the camera moves — invisible far away, but a few px for a star a few hundred AU off, e.g. a
  globular-cluster neighbour). Fix with a **compensated two-float (hi/lo) pair**: bake the offset as
  `hi=(float)d, lo=(float)(d−hi)`, split `camRel` the same way, and reconstruct
  `rel = (posHi − camRelHi) + (posLo − camRelLo)`. For near points `posHi ≈ camRelHi`, so by the
  **Sterbenz lemma** that difference is *exact* → ~km precision; far points lose low bits but are
  sub-pixel anyway. (See `StarRenderer` catalog path.)
- **Play a sound or music:** load a WAV (`_audio.LoadSound`/`WavLoader`) or synth a
  clip, then `PlaySound` (UI, head-relative) / `PlaySoundAt(sound, camRel)` (in-world,
  pass `body.CurrentPosition.ToCameraRelative(camera.Position)`) / `PlayMusic`. Guard
  nothing — the engine no-ops when `Available` is false. See §6.

---

*Generated as a structural reference; for exact, current signatures consult the
source — the public surface evolves with the milestones in the README.*
