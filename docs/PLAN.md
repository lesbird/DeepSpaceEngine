# Plan: Massive-Scale Space Exploration Engine (C# / OpenGL)

## Context

We're building a 3D space-exploration game engine from scratch (the project directory is
currently empty). The defining requirement is **true-scale distances**: billions of stars,
light-years apart, each with a deterministic solar system derived from the star's index, with
seamless transitions from interstellar flight → solar system → planet surface → driving a rover.

The single hardest problem is **numerical precision**. A 32-bit `float` holds ~7 significant
digits; even a 64-bit `double` at galactic magnitudes (~10^21 m) only resolves to ~hundreds of
km — nowhere near the millimeter precision needed to stand on a planet. Everything in this plan
is organized around solving that, plus rendering/streaming billions of procedurally-generated
objects without storing them.

**Decisions locked in with the user:**
- Language/runtime: **C# on .NET 8 (LTS)**
- GL bindings: **Silk.NET** (GL + GLFW windowing + input + Silk.NET.Maths)
- Target: **cross-platform, OpenGL 4.1 baseline** (runs on the user's Mac; no compute shaders →
  procedural generation is CPU-side with `Parallel.For` + SIMD, uploaded via instanced buffers)
- Fidelity: **built incrementally** — get scale/streaming/landing solid first, add realism later

---

## Core technical pillars

### 1. Hierarchical coordinate system (the heart of the engine)

Never feed absolute galactic coordinates to the GPU, and never store them in a single `double`.
Use a two-tier **fixed-point sector + double local-offset** position, combined with
**camera-relative (floating-origin) rendering**.

```csharp
// Engine.Core
public struct UniversePosition {
    public Vector3D<long>   Sector;   // integer sector index per axis (exact, huge range)
    public Vector3D<double> Local;    // offset within the sector, meters, range [0, SECTOR_SIZE)

    public const double SECTOR_SIZE = 1.495978707e11; // 1 AU (configurable; see note)
    public void Normalize();          // carry Local overflow into Sector, keep Local in-range
    public Vector3 ToCameraRelative(in UniversePosition cam); // -> small float vec for GPU
}
```

- **Why these numbers:** with a 1 AU sector, `Local` is always < 1.5×10^11 m, where `double`
  precision is ~0.03 mm — ample for landing. `Sector` is `Int64`, giving a range of ~10^18 AU
  (far beyond the observable universe). Sub-millimeter precision *everywhere*, effectively
  unbounded range. Sector size is a tunable constant (1 light-second is an alternative, smaller
  sectors / more crossings).
- **Rendering:** `delta = (obj.Sector - cam.Sector) * SECTOR_SIZE + (obj.Local - cam.Local)`,
  computed in `double`/`Int64`, then cast to `Vector3` (float). Objects near the camera get tiny,
  precise coordinates → no jitter. Far objects get large coordinates but they're far away, so
  float error is sub-pixel.
- **Floating origin:** the camera is conceptually always at the render-space origin. The view
  matrix carries only orientation; all world positions are pre-translated by the camera relative
  to it on the CPU each frame.
- This must be **unit-tested** thoroughly (round-trip, normalize carry, precision at 10^21 m) —
  it's the foundation everything else stands on.

### 2. Deterministic procedural generation (no storage)

Billions of stars can't be stored — they're regenerated on demand from integer seeds via a fast
hash PRNG (SplitMix64 / PCG / "squirrel" integer noise). Pure functions: same input → same output
forever, identical across machines and runs.

- **Galaxy → stars:** a coarse spatial grid (e.g. ~1 ly cells). `hash(cellCoord)` deterministically
  yields the star count and each star's intra-cell position + unique 64-bit `StarId`. Star density
  is modulated by a **galaxy shape function** (disk profile + spiral arms + bulge) so it reads as a
  galaxy, not uniform noise.
- **Star → system:** `StarId` seeds the star's physical properties (mass, blackbody temperature →
  color, luminosity) and its solar system (planet count, orbital radii, sizes, types, moons, rings,
  per-planet terrain seeds). "Unique solar system per star index" falls out of this directly.

### 3. Streaming & LOD

Only what's near the camera exists in memory. Distance-banded levels of detail:

| Band | Representation |
|------|----------------|
| Far (beyond generation radius) | static star dome / brightest-star point cloud backdrop |
| Mid (nearby sectors, in frustum) | GPU-instanced point sprites, additive glow, blackbody color |
| Close (< ~1–2 ly) | star rendered as a lit sphere + corona |
| Very close (< 0.5 ly) | **full solar system spawned** |

Frustum-cull the coarse cells; for visible cells, generate star points (camera-relative floats)
into an instanced buffer each frame (or cached per-cell). Only thousands–millions survive culling
and draw as points.

---

## Subsystems

### Solar system spawn / despawn
- `SolarSystemManager` tracks the nearest star each frame. Cross **inside 0.5 ly** → instantiate the
  system from `StarId` (sun sphere, planets, moons, orbits). Cross **outside ~0.6 ly** → despawn and
  free GPU resources. The gap between thresholds is **hysteresis** to prevent spawn/despawn thrash at
  the boundary. At most ~1 active system at a time.
- Orbits: Keplerian (or simple circular to start), parameterized by seed, animated by sim time, all
  expressed as `UniversePosition` relative to the star.

### Planets & terrain (incremental)
- **M2 (first):** planets are GPU-shaded spheres lit by the star, on their orbits. "Landing" = fly to
  the sphere; surface is a smooth lit sphere.
- **M3:** **cube-sphere with chunked quadtree LOD** terrain (Outerra/SpaceEngine style). As the camera
  descends, faces subdivide; per-chunk heightmaps are generated from the planet's noise seed. Floating
  origin keeps the surface millimeter-precise under your feet.
- **Later:** atmospheric scattering, water, biomes/texturing, normal maps.

### Rover & on-foot
- On landing, spawn a rover on the surface. Simple vehicle physics: gravity toward planet center,
  wheel/ground contact by sampling the terrain heightfield, WASD to drive. Camera attaches
  (chase / first-person). A **camera/game state machine** governs Free-flight ↔ Orbital approach ↔
  Surface/rover.

### Camera & controls
- 6-DOF free-fly camera with **quaternion orientation** (no gimbal lock). Captured mouse = look;
  WASD + QE = translate; the camera holds a `UniversePosition` (add to `Local`, then `Normalize()`).
- **Mouse wheel = logarithmic speed control**, spanning ~1 m/s up to ~100+ ly/s so you can cross
  interstellar distances and also creep up to a planet. Current speed shown on the HUD.

---

## Project structure (.NET solution)

```
DeepSpaceEngine.sln
  Engine.Core/        UniversePosition, Vec3d math, time, hashing/noise, input, logging
  Engine.Rendering/   GL abstraction: Shader, Buffer, Texture, Mesh, Material, Camera, passes
  Engine.Platform/    Silk.NET window/context/input bootstrap, main-loop host
  Game.Universe/      galaxy density, star catalog (hash placement), system generator
  Game.Systems/       SolarSystemManager, orbits, planet/terrain, rover/physics, game states
  Game.App/           entry point, scene wiring, ImGui debug overlay/HUD
  Engine.Core.Tests/  xUnit tests (coordinate precision, determinism)
```

- Start with **manager + entity-list** organization (simplest); an ECS (e.g. Arch) is an optional
  later refactor if entity counts in active systems grow.
- Math: **Silk.NET.Maths** (`Vector3D<double>`, `Matrix4X4<float>`) plus the custom
  `UniversePosition`/`Vec3d` helpers.
- **C# performance discipline** (this is the part that makes or breaks a C# engine): vectors as
  `struct` value types, no allocations in per-frame/hot loops (pool buffers), `Parallel.For` for
  star/terrain generation, `System.Numerics` SIMD where it pays, and `Span<T>`/`unsafe` for GL buffer
  uploads (Silk.NET takes spans directly).

### Dependencies (NuGet — pinned exact versions per repo convention)
`Silk.NET.OpenGL`, `Silk.NET.Windowing`, `Silk.NET.Input`, `Silk.NET.Maths`,
`ImGui.NET` (debug UI/HUD), `StbImageSharp` (textures), `xunit` (tests). All pinned to exact
versions in each `.csproj` (no floating/`*` versions) — the repo's "pin dependencies" rule applies
to NuGet just as it does to npm.

---

## Phased roadmap

- **M0 — Foundation.** Silk.NET window + GL 4.1 context, main loop, clear/present, ImGui overlay,
  input. `UniversePosition`/math library **with unit tests**. Free-fly quaternion camera in empty
  space with a debug reference grid. *Exit: fly around an empty scene smoothly.*
- **M1 — Star field & true scale.** Hierarchical coords live; procedural galaxy placement; instanced
  point-sprite star rendering with blackbody color; sector streaming + frustum cull; log-scale
  mouse-wheel speed; HUD (position, speed, nearest star, FPS). *Exit: fly light-years with zero
  jitter; billions addressable, thousands rendered.*
- **M2 — Solar systems.** `StarId` → system generator; spawn/despawn at 0.5 ly with hysteresis; sun
  sphere + lit planet spheres on orbits; approach/transition into a system. *Exit: fly to a star, its
  system appears; leave, it despawns.*
- **M3 — Planets & terrain.** Cube-sphere chunked-LOD terrain from planet seeds; descend and land with
  floating-origin precision; basic surface lighting. *Exit: land on a planet surface, no jitter.*
- **M4 — Rover.** Surface physics, drivable rover, camera/state modes, terrain collision.
  *Exit: land, disembark into rover, drive across terrain.*
- **M5+ — Fidelity.** Atmosphere scattering, water, biomes, moons/rings, richer galaxy backdrop,
  bookmarks/save, audio.

---

## Verification

- **Unit tests (`Engine.Core.Tests`, xUnit):**
  - `UniversePosition`: normalize/carry correctness, camera-relative round-trip, and an explicit
    precision assertion at ~10^21 m (relative render coords stay small; sub-mm local precision holds).
  - **Determinism:** a fixed `StarId` produces an identical system across repeated runs; galaxy
    placement is stable for a given cell.
- **Manual / in-engine (each milestone):**
  - HUD shows position, speed, nearest star, frame time, star/draw-call counts.
  - Jitter check: fly to extreme distances and confirm geometry is rock-steady (visual + assert that
    max camera-relative coordinate stays bounded).
  - Flow check: approach a star → system spawns; retreat → despawns (no thrash at the boundary);
    descend → terrain refines; land → drive rover.
- **Performance:** watch frame time and allocation rate (no per-frame GC spikes); confirm
  `Parallel.For` generation keeps streaming off the main thread's critical path.

## Risks / watch-items
- **macOS GL 4.1 ceiling:** no compute shaders — generation is CPU-bound. Mitigate with multithreaded
  generation, per-cell caching, and instancing. If generation can't keep up later, GL 4.3+ compute is
  the cross-platform (non-Mac) escape hatch.
- **C# GC in hot loops:** the top engine-level risk. Enforce struct/value-type math, buffer pooling,
  and zero-alloc render loops from M0 so it doesn't have to be retrofitted.
- **Coordinate system is load-bearing:** get `UniversePosition` + tests right in M0 before building
  anything on top of it.
