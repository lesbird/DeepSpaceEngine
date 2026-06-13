# MetalEngine3

A C# / OpenGL space-exploration engine built for **true-scale distances** — billions of
procedurally-generated star systems, light-years apart, with seamless transitions from
interstellar flight down to standing on a planet. See the full design in
[`docs/PLAN.md`](docs/PLAN.md).

> **Status: Milestones M0–M4 complete.** Window + GL 4.1 context, the hierarchical
> coordinate system, a free-fly camera, a streaming procedural **star field**,
> **spawnable solar systems**, and now **landable planets**. Fly within 0.5 ly of a star
> and its deterministic system materialises (emissive sun, lit Keplerian-orbiting planets +
> moons, orbit rings). Approach a rocky planet and it switches to a **cube-sphere quadtree
> LOD terrain** generated from noise — descend all the way to the surface with no jitter
> (per-patch floating origin), and the camera rides the planet's orbital frame so it doesn't
> get left behind by time-lapse. The terrain layers **fBm continents**, **domain-warped
> ridged-multifractal mountains**, and **band-limited detail roughness** (octaves clamped to
> each LOD patch, so it's rugged up close yet alias-free from orbit). The LOD is **smooth**:
> detail **fades in continuously** (fractional octaves) and patches **geomorph** toward their
> parent between levels, so the surface no longer visibly rebuilds itself as you descend or skim.
> Together they give dramatic ranges and canyons, with **slope-aware colouring** (cliffs read as bare rock, snow only on gentle
> highlands) and per-type silhouettes. Ocean worlds get a **rugged sea floor under a translucent
> water surface** — coastlines, sandy beaches, depth-shaded shallows, and a sun glint, all from
> the land simply piercing a flat sea. Planets carry **real volumetric atmospheres** — a
> fullscreen pass ray-marches Rayleigh + Mie
> scattering, so the sky glows on the limb from space and turns blue overhead / hazy at the
> horizon / red toward the terminator on the surface. A live **tuning HUD** exposes the
> atmosphere, relief, and biome knobs and saves them to `tuning.json`. Once you're low over a
> surface, press **R** to deploy a **drivable rover**: an arcade-grounded vehicle with real
> per-planet gravity that follows the terrain, tilts onto slopes, falls off ledges and lands,
> driven with throttle/steer under a third-person chase camera — press **R** again to lift back
> into free-fly. Deep space is no longer black — a **distant-galaxy backdrop** (a far-field star
> dome plus a glowing Milky-Way band with dust lanes and a central bulge) sits behind the streamed
> stars, and an **auto speed control** caps your velocity on entering a system (up to ~1,000,000 c,
> easing down the closer you get to a planet so you settle into an approach instead of blasting
> past). Generation is fully deterministic. M5 (fidelity) is underway.

## Tech stack

- **.NET 7** (targets `net7.0`; the plan calls for net8 LTS — a one-line change in
  `Directory.Build.props` once that SDK is installed)
- **Silk.NET 2.23.0** — OpenGL, windowing (GLFW), input, and `Vector3D<double>` math
- **OpenGL 4.1 core** — the cross-platform baseline that also runs on macOS (no compute
  shaders; procedural generation is CPU-side)
- **ImGui.NET** — debug HUD
- All NuGet versions are pinned exactly (no floating versions).

## Project layout

| Project | Responsibility |
|---------|----------------|
| `Engine.Core` | `UniversePosition` (hierarchical coords), deterministic hashing/RNG, math constants |
| `Engine.Rendering` | GL wrappers: `Shader`, `Mesh`, `Camera`, `Primitives`, matrix helpers |
| `Engine.Platform` | `GameWindow` — Silk.NET window + GL context + input bootstrap |
| `Game.Universe` | procedural generation: galaxy/`StarField`, the `BackdropStars` far-field dome, `SystemGenerator` (planets + moons), `Noise` (fBm + ridged), `PlanetTerrain`, atmospheres, the `Rover` surface-physics sim, and the live knobs (`TerrainTuning`/`BiomeTuning` globals + `PlanetTuning` per-type overrides) |
| `Game.Systems` | runtime systems: `SolarSystemManager` (spawn/despawn lifecycle, sim time) and `SpeedPolicy` (the free-fly auto speed cap) |
| `Game.App` | entry point, main loop, `FreeFlyController` + `RoverController` (chase-cam driving), renderers (`StarRenderer`, `GalaxyBackdrop`, `SystemRenderer`, `PlanetTerrainRenderer`, `RoverRenderer`, depth-aware `AtmosphereRenderer` over a `SceneFramebuffer`), `StarOverlay`, HUD + tuning panel (`TuningConfig` save/load) |
| `Engine.Core.Tests` | xUnit tests: coordinate precision, generation determinism, terrain, rover, backdrop & speed policy |

## The core idea: precision at any distance

A single `double` resolves to only ~hundreds of km at galactic scale. `UniversePosition`
instead stores `Sector` (an `Int64` cube index per axis, sector = 1 AU) plus a `double`
`Local` offset within that sector. The fractional position always lives inside a small
sector, so the double's full precision yields **sub-millimetre resolution everywhere**, with
effectively unlimited range. Nothing absolute is ever sent to the GPU — `ToCameraRelative`
produces a small, precise float vector relative to the camera (floating origin).

## Build & run

```sh
dotnet build                       # build everything
dotnet test                        # run the coordinate/hashing test suite
dotnet run --project Game.App      # launch the engine
```

## Controls

| Input | Action |
|-------|--------|
| Mouse | Look — true 6-DOF, no pitch limit / no gimbal lock |
| `W` `A` `S` `D` | Move forward/left/back/right |
| `Q` / `E` | Move down / up |
| `Z` / `C` | Roll left / right |
| Mouse wheel | Speed — logarithmic (1 m/s → ~100 ly/s) in open space; throttle % of the auto cap inside a system |
| `,` / `.` | Orbit time-lapse slower / faster |
| `P` | Pause / resume orbital time |
| `R` | Drive the rover (when low over a surface) / return to free-fly |
| `Tab` | Toggle mouse capture (to interact with the HUD) |
| `Esc` | Quit |

**Rover (driving).** Fly within ~2 km of a solid surface and press `R` to drop a rover onto the
ground beneath you under a chase camera.

| Input | Action |
|-------|--------|
| `W` / `S` | Throttle forward / reverse |
| `A` / `D` | Steer left / right |
| `Space` | Brake |
| Mouse | Orbit the chase camera around the rover |
| `R` | Lift back into free-fly from where you parked |

The rover has real per-planet gravity, hugs the terrain and tilts onto slopes, and goes airborne
off ledges until it lands — it is arcade-grounded (per-wheel suspension comes in M5).

**Auto speed.** In open galaxy the wheel sets an unlimited logarithmic speed as before. The moment
a solar system spawns, an auto cap kicks in — up to ~1,000,000 c far from any planet, then falling
in proportion to your distance from the nearest planet's surface and flooring at 1,000 km/s in the
atmosphere, so you decelerate into an approach instead of blasting past at interstellar velocity (the
proportional cap is self-converging — as the cap shrinks, each step covers a fraction of the
remaining distance rather than overshooting). Inside a system the wheel becomes a **throttle**,
scaling a percentage of that auto cap so you can ease below it for a fine descent; the HUD shows the
throttle % and the current cap.

**Navigation aids.** In open space, nearby stars carry reticles (catalog id + class +
distance) and a green arrow always points to the nearest star. Inside a system the star
clutter drops away: the sun is marked, each planet shows its `STAR-PLANET` designation, and
a planet's moons (`STAR-PLANET-MOON`) only reveal once you fly close to it. A "nearest
planet" arrow guides you in.

**HUD.** Shows sector/local position, distance from origin (ly), speed, FPS, the active
system (planets, moons, orbit time-lapse with apparent speed in `c`), and — when landing —
your altitude and live terrain patch counts. Fly to extreme distances to confirm there is
**no positional jitter**.

**Tuning panel.** A second HUD window with live controls for the **galaxy backdrop** (an on/off
toggle plus band-glow and distant-star brightness), the **atmosphere** (an on/off
toggle, plus sun intensity, exposure, Rayleigh/Mie strength, haze anisotropy, shell height —
and a *Debug: transmittance* toggle that shows the ray-march geometry), **terrain relief**
(relief scale, mountain bias, feature frequency), and **biome/colour** (snow line, cliff
threshold and strength, lowland tint, rock/snow/cliff colours). Atmosphere updates instantly;
terrain/biome changes regenerate the meshes live. Turning the atmosphere off renders the bare
surface — handy for inspecting terrain.

The terrain/biome controls edit either the **global defaults** or a **per-planet-type override**:
pick a type (it auto-follows the world you fly down to), tick *Override for &lt;type&gt;*, and that
type carries its own palette/relief — e.g. jagged red lava worlds vs. smooth pale ice worlds —
while every type without an override keeps the global look.

**Save settings** writes everything (globals + enabled overrides) to `tuning.json` (next to
`imgui.ini`), which auto-loads on the next launch — so a look you dial in becomes the new default.
Defaults are neutral, so generation stays deterministic until you turn a knob.

## Roadmap

- **M0 — Foundation** ✅ window, coords + tests, free-fly camera, debug scene
- **M1 — Star field & true scale** ✅ procedural galaxy, point-sprite star rendering, cell streaming, nearest-star HUD, on-screen reticles + nearest-star navigation arrow
- **M2 — Solar systems** ✅ deterministic system generation, spawn/despawn at 0.5 ly (hysteresis), emissive sun + lit orbiting planets + orbit rings
- **M3 — Planets & terrain** ✅ cube-sphere quadtree-LOD terrain with **ridged-multifractal mountains** + domain warp + **per-LOD band-limited detail** + **slope-aware biome colouring**, skirts, per-patch floating origin, frame-riding, descend & land with no jitter; **ocean worlds** (rugged sea floor + translucent water surface + coastlines); **moons**; **volumetric (Rayleigh + Mie) atmospheres**; **live tuning HUD** with `tuning.json` save/load
- **M4 — Rover** ✅ drivable surface vehicle: per-planet gravity, terrain-following + slope tilt, ledge falls/landings, throttle/steer driving under a third-person chase camera (`R` to deploy/exit); arcade-grounded, pure testable sim
- **M5 — Fidelity** (in progress) — **distant-galaxy backdrop** ✅ (a deterministic far-field dome of background stars concentrated on the galactic plane + a fullscreen Milky-Way band with dust lanes and a central bulge, drawn behind the streamed star field, with live brightness knobs saved to `tuning.json`); **smooth terrain LOD** ✅ (continuous fractional-octave detail fade + geomorphing between levels + merge hysteresis, so the surface no longer pops/rebuilds across LOD transitions); still to come: per-wheel suspension, rings, planet rotation, erosion & more biomes, animated water
