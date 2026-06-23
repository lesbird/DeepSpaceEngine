# Galaxies Plan — from "stars in a box" to a universe of galaxies

Today the engine renders **one galaxy's worth of space as a uniform infinite box**. The goal
is a Space-Engine-style **hierarchical universe**: you start inside the Milky Way (~100 billion
stars), and if you leave it you see *other* galaxies — at first as bright star-like points,
then resolving into billion-star galaxies as you approach. Intergalactic distances are
**true cosmic scale** (galaxies millions of ly apart; the voids are genuinely vast and you
cross them under warp speed).

For the engine's two foundational ideas (floating-origin precision, deterministic generation)
see [`ARCHITECTURE.md`](ARCHITECTURE.md). For the original brief see [`PLAN.md`](PLAN.md).

---

## 0. Where we are today (the starting point)

- **`GalaxyModel`** (`Game.Universe/GalaxyModel.cs`) is a **global singleton** describing THE
  galaxy at the universe origin: disk in the XZ plane, exponential radial/vertical falloff,
  2-arm logarithmic-spiral modulation. It exposes `DensityAt(meters)` → stars/ly³.
- **`StarCatalog`** (`Game.Universe/StarCatalog.cs`) is one 350 ly lattice block, ~257k stars.
  Crucially, `Generate` **ignores `DensityAt`** — it uses a flat `BaseDensity` count per block
  (`StarCatalog.cs:54`). The blocks tile *all* of space uniformly. Hence "unlimited stars in a box."
- **`StarCatalogPager`** (`: INearestStar`) pages star blocks within a Chebyshev radius of the
  camera (3×3×3 resident, async generation, hysteresis eviction). This is the single nearest-star
  source the game talks to. `StarId` packs `Star.Id` from `(blockCoord, localIndex)`.
- **`StarField`** is the legacy on-demand cell field; mostly dead now (kept for tests +
  `SampleStarType`, which `StarCatalog.Generate` calls). It *does* use `DensityAt`+Poisson.
- **`GalaxyBackdrop`** (`Game.App/GalaxyBackdrop.cs`) is a **fake painted skybox**: a fullscreen
  Milky-Way band glow + a few procedural emission nebulae + a dome of distant point-stars. All
  **direction-only** — no real structure, no parallax, never moves as you fly.
- **`BlackHoleRenderer`** draws a single SMBH at the universe origin (the home view frames it).
- **`UniversePosition`** = Int64 sector (1 AU) + double local. Range ≈ 1.4e30 m ≈ **1.5e14 ly**
  with sub-mm precision — **the entire observable universe (~93 Gly across) already fits with
  room to spare.** This is the linchpin: *no coordinate-system rework is required.*

**So the work is:** insert a *universe level* above the star level, confine stars to galaxy
interiors, and build the continuous LOD that turns a distant point into a billion-star galaxy.

---

## 1. The core idea — a hierarchical LOD pyramid

A deterministic generation hierarchy, all sharing the one floating-origin `UniversePosition` space:

```
Universe   →  galaxies placed on a sparse lattice (the cosmic web)
  Galaxy   →  per-galaxy density model (disk/halo, spiral arms, central SMBH)
    Stars  →  the existing StarCatalog lattice, but GATED to galaxy interiors
      System → existing SolarSystemManager (unchanged)
```

A galaxy renders at one of four LOD tiers by **apparent angular size**, each tier overlapping
the next so nothing pops:

| Tier | Distance | Representation |
|------|----------|----------------|
| **Point** | very far | single bright point sprite — *this is why other galaxies "appear as bright stars"* |
| **Impostor** | approaching | oriented billboard / shader disk showing the spiral or elliptical shape |
| **Cloud** | near | volumetric GPU particle cloud (~10⁵–10⁶ sampled stars) — bridges impostor → real |
| **Resolved** | inside | the existing `StarCatalogPager` streams the real catalog + full systems |

The cross-fades between tiers are the bulk of the "feel" work and the main visual payoff.

---

## 2. Architecture changes

Deliberately **mirrors the patterns that already work** (`StarCatalog` / `StarCatalogPager` /
`StarId`), one level up the hierarchy.

### 2.1 New — universe level (`Game.Universe`)

- **`Galaxy`** — a descriptor struct/class: id, center `UniversePosition`, type
  (Spiral / Elliptical / Irregular / Dwarf), radius, **disk orientation** (a normal vector),
  star count, integrated color + luminosity, and a per-galaxy seed.
- **`GalaxyField` / `GalaxyCatalog`** — a sparse lattice of *huge* blocks (~10–20 Mly per side)
  that Poisson-places galaxies with a **large-scale-structure density** (filaments, clusters,
  voids). Almost all of intergalactic space generates nothing.
- **`GalaxyCatalogPager : INearestGalaxy`** — pages galaxy blocks near the camera; tracks the
  nearest galaxy and **which galaxy (if any) the camera is currently inside**. Cheap, because
  galaxies are sparse and huge-spaced.
- **`GalaxyId`** — packing scheme analogous to `StarId`.

### 2.2 Refactor — `GalaxyModel` becomes per-galaxy

Today it's a global singleton implicitly at the origin. Each `Galaxy` carries its own model
(scale length/height, arm count/strength/wind, orientation, seed, type-dependent shape).
**The Milky Way is simply the galaxy whose volume contains the universe origin**, so the
current home view and existing `WorldSeed` behaviour keep working.

### 2.3 Refactor — confine star generation to galaxies

`StarCatalog.Generate` changes from "flat count" to:
1. Find the galaxy (or galaxies) whose volume overlaps this block via the `GalaxyCatalogPager`.
2. For each candidate star, transform its position into that galaxy's **local frame** (apply the
   galaxy center + disk orientation) and accept it with probability ∝ `DensityAt` (rejection
   sampling, or a per-block expected count from the density integral, clamped to `StarId.MaxLocal`).
3. A block outside **every** galaxy generates **zero stars**.

`StarCatalogPager` then only loads star blocks when the camera is inside / near a galaxy. In
intergalactic space the foreground lattice is empty and you see only galaxy sprites.

> Edge case: a block straddling a galaxy boundary samples density per-star (smooth edge). Two
> overlapping galaxies are rare at true scale; sum their densities or pick the nearest.

### 2.4 ID strategy (the one real headache)

A star's global address becomes **`(galaxyId, starId)`**. Make each block's generation seed
depend on the **galaxy seed**, so per-galaxy lattices are distinct. Within the Milky Way
`galaxyId = 0`, so existing `#N` star addresses stay valid and the "find #N" box still works
in the home galaxy. The find/jump box and **discovery reporting** (`DISCOVERY_PLAN.md` — ids
are strings) gain an optional galaxy prefix, e.g. `G<gid>-<starId>`.

---

## 3. Rendering

- **`GalaxyRenderer`** (new) — draws all paged galaxies.
  - *Point tier* first (instant payoff: leave the Milky Way → other galaxies are bright stars).
    Reuse the point-sprite + attenuation approach from `GalaxyBackdrop`'s dome / `StarRenderer`.
  - *Impostor tier* — an oriented billboard with a spiral/elliptical disk shader. The band shader
    math in `GalaxyBackdrop.cs` (arm modulation, dust-lane fbm, central bulge) is a ready
    starting point, localized to a galaxy's disk and oriented by its normal.
  - *Cloud tier* — a GPU point cloud sampled from the density model, **only for the nearest 1–3
    galaxies** (cap ~10⁵–10⁶ points each).
- **Cross-fades** keyed on apparent angular size; each tier fades in before the previous fades out.
- **`GalaxyBackdrop` is demoted.** The fake band/dome is superseded by real `GalaxyRenderer`
  sprites for distant structure; the Milky-Way band you see *from inside* emerges naturally from
  the host galaxy's real + cloud stars. Keep a faint cosmic background glow as a floor.
- **`BlackHoleRenderer` becomes per-galaxy** — render the SMBH of the galaxy you're in / near,
  not a fixed one at the origin.

---

## 4. Phasing (each phase independently shippable & verifiable)

- **Phase 0 — DONE 2026-06-23** (build + 76 tests green; pending on-device verify). New
  `Game.Universe` types: `Galaxy` (descriptor), `GalaxyType`, `GalaxyId` (pack/unpack, mirrors
  `StarId`), `GalaxyField` (universe density, uniform for now), `GalaxyCatalog` (one 20 Mly lattice
  block; Poisson-places galaxies; **forces the Milky Way at the origin** in block (0,0,0) — Spiral,
  50 kly radius, seed = `WorldSeed`, disk normal +Y), `GalaxyCatalogPager : INearestGalaxy` (pages
  blocks 3×3×3 / evict 2, tracks nearest + containing galaxy). Wired into `Program`:
  `_galaxyPager.Update(camera)` each frame + a Navigation HUD line (`DrawGalaxyStatus`) showing the
  galaxy you're inside (green) or nearest galaxy + Mly distance. Note: `GalaxyId` uses `AxisBias`
  like `StarId`, so the Milky Way's id is a fixed opaque value, **not** 0 (both classes' "keeps
  id 0" doc claims are aspirational). No star-generation or rendering change yet.
- **Phase 1 — DONE 2026-06-23** (build + 83 tests green; pending on-device verify). `GalaxyModel`
  is now **per-galaxy** (center + disk normal + radial cutoff; `DensityAtLocal(offset)` for precise
  galaxy-relative sampling; `GalaxyModel.For(Galaxy)` maps type+radius→shape, and the Spiral/50 kly
  case reproduces the tuned Milky-Way values exactly). `StarCatalog` takes a `Galaxy` and
  **acceptance-samples** candidates against `density/peak`, so the disk/arms/edge emerge and blocks
  beyond a galaxy's reach come out empty; a second `StarCatalog(blockCoord)` ctor makes a cheap
  empty (intergalactic) block. `StarCatalogPager` now takes the `GalaxyCatalogPager`, resolves each
  block's galaxy on the main thread (value-type `Galaxy` captured into the async gen task) and falls
  back to empty when none overlaps. `StarRenderer.SyncBlocks` skips 0-star blocks. `Program` builds
  the galaxy pager first and updates it before the star pager each frame. Net effect: the Milky Way
  near home is unchanged (now with subtle arm structure); fly out past ~50 kly and the star field
  fades to empty intergalactic space. NOTE: star ids change vs the old uniform field (acceptance
  sampling + galaxy-seeded blocks) — old discovery records won't match new ids (universe regen).
- **Phase 2 — DONE 2026-06-23** (build green; pending on-device verify). `GalaxyRenderer`
  (`Game.App/GalaxyRenderer.cs`): per-frame instance buffer of the resident galaxies drawn as
  additive, **direction-only** point sprites on a dome radius inside the frustum (galaxies sit far
  beyond the camera far plane, like the backdrop dome). Per-galaxy size/brightness from apparent
  flux (`StarCount·1e-11 / distLy²`, sqrt cue, clamped 2–16 px), so a galaxy reads as a faint dot
  tens of Mly out and swells into a bright point on approach. Excludes the galaxy the camera is
  inside (its stars stream via the catalog). Drawn right after `_backdrop.Render`, before nebulae.
  HUD line extended with "Galaxies: N resident, M drawn". Public `Brightness`/`SizeScale`/Min/Max
  fields for tuning (not yet in TuningConfig). No new unit tests (GPU path). **First visual payoff:
  fly out of the Milky Way and the other galaxies appear as bright stars.**
- **Phase 3 — DONE 2026-06-23** (build + 87 tests green; pending on-device verify). `GalaxyRenderer`
  gains an **impostor disk** tier cross-faded with the point tier by apparent angular radius
  (`ratio = galaxyRadius / distance`; point-only below `RatioLo=0.01`, impostor-only above
  `RatioHi=0.03`, complementary alphas between). Each impostor is a unit quad (gl_VertexID triangle
  strip, no VBO) placed in the galaxy's **real disk plane** via a normal-derived basis, so it shows
  inclination (face-on down the normal, edge-on along it), sized so `half/ImpostorRenderDist =
  radius/distance` to reproduce true angular size (ratio clamped 1.2 for the near boundary). Fragment
  shades a procedural disk in [-1,1]² uv: bright core + exponential disk × log-spiral arms (per-type
  arm count/strength/swirl) × fbm dust, soft circular edge. Drawn per-galaxy (usually a handful close
  enough). All giant-vector math kept in double before casting (the overflow lesson). HUD now "N
  resident, P pts, D disks". KNOWN: when you cross into a galaxy (dist < radius → IsInside → excluded)
  the impostor pops to streamed stars — Phase 4 volumetric cloud + Phase 5 hand-off smooth that.
- **Phase 4** — **Volumetric star-cloud** LOD + cross-fade with the impostor.
- **Phase 5** — Hand-off cloud → real streamed catalog on entry; per-galaxy SMBH + backdrop.
- **Phase 6** — Universe-level **map view** (extend the `N` galaxy map to zoom out to the cosmic
  web), performance passes, photo-mode polish.

---

## 5. Scale constants (true cosmic scale)

- Observable universe ≈ 93 Gly across — fits `UniversePosition` ~6 orders of magnitude over.
- Milky Way: ~100k ly diameter, ~100–400 billion stars, disk scale length ~3.5 kly (matches the
  current `GalaxyModel.ScaleLength = 3500`).
- Galaxy lattice block: ~10–20 Mly/side → a handful to dozens of galaxies per block; field
  density follows filament/void structure (Local Group ~10 Mly).
- Voids are essentially empty — crossing them depends on the existing high warp speeds
  (the free-fly camera already reaches ~100 ly/s; intergalactic travel may want a higher cap
  and/or a "warp to galaxy" jump).

---

## 6. Main risks

- **Cross-fade tuning** — the impostor↔cloud↔real seams are where the "impressive" lives; budget
  real iteration time.
- **Depth / precision at galaxy scale** — camera-relative floats reach ~1e22 m; render galaxy
  tiers direction-relative and/or with logarithmic / reversed-Z depth (the engine already leans
  on huge far-planes + per-block re-centring to bound float jitter).
- **Cloud particle budget** — strictly cap volumetric clouds to the nearest few galaxies.
- **ID extension** — `(galaxyId, starId)` touches the find/jump box and discovery reporting.
- **`DensityAt` is currently unused by `StarCatalog`** — Phase 1 actually wires it in, so per-block
  star counts become variable and must be clamped to `StarId.MaxLocal`.

---

## 7. Open decisions (settled / deferred)

- **Intergalactic scale:** TRUE cosmic scale (decided 2026-06-23).
- **Warp / travel between galaxies:** top speed cap raised to 1e24 m/s (~100 Mly/s) in Phase 2
  (`FreeFlyController.MaxSpeedExp = 24`), with Mly/s & Gly/s readouts in `FormatSpeed`, so the void
  is crossable in seconds. A "jump to galaxy" affordance is still possible later if wheeling up feels
  slow.
- **Star id format across galaxies:** `(galaxyId, starId)`; Milky Way = galaxy 0 for back-compat.
