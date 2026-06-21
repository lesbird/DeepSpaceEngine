# Planet Rotation / Day-Night — Design Plan

**Status:** not started (planning only). Rotation is currently **disabled** — `SystemGenerator.ApplyScanData`
pins `RotationPeriodSeconds = 0`, so `CelestialBody.ApparentSunDir` returns the true sun and all rotation
plumbing is inert.

**Goal:** SpaceEngine-style rotation — planets visibly spin from orbit, and a landed observer gets a real
day/night cycle (sun rises/sets, stars wheel overhead) with no jitter and no terrain re-baking.

**Chosen model:** *attach the camera to the spinning planet* (camera co-rotates with the body). We do **not**
rotate the sky/sun/stars around the planet. See "Two equivalent models" below for why.

---

## Background: why the first attempt was scrapped

The earlier version (commit `359f373`, 2026-06-18) kept the camera bolted to the co-rotating surface and
rotated **only the sun's lighting direction** (`CelestialBody.ApparentSunDir`). The ground darkened, but the
*rendered* sun disk, the sibling planets, and the star backdrop stayed frozen in the system frame — half the
sky moved, half didn't, so it read as a bug. Real geometric spin was also considered and scrapped at the time
as "too big." It isn't, given how this engine is already structured (see below).

---

## Two equivalent models — and why we pick the camera one

Everything renders **camera-relative**: `clip = Proj · View(orientation) · (P_world − cam)`. Day/night is
relative motion between the observer and the sky, so two implementations produce the **identical image**:

- **(A) Rotate the sky** by `R(t)⁻¹` about the planet centre, keep the camera still.
- **(B) Rotate the camera** by `R(t)` with the planet, keep the sky still. ← **chosen**

They are the same transform attributed to different objects. We pick **(B)** because of where the numerical
work lands — above all, the **starfield lattice**:

- Under (A), making the catalog stars wheel means rotating every resident star's `UniversePosition`
  (sector + double local) by a quaternion about the planet centre **every frame, for millions of stars**, then
  feeding those rotated positions into `StarCatalogPager` (which pages blocks on position). Wasteful *and*
  destabilising — rotated positions near block boundaries, precision wobble, re-paging churn.
- Under (B), **the stars never move.** They keep their true galaxy positions; the wheel falls out of the
  camera's view matrix for free. No per-star math, no paging churn, no precision risk.

The cancellation that makes (B) cheap: fold `R(t)` into the **camera's** world pose, and for a body-fixed
terrain point the `R(t)` on the camera and the `R(t)` carrying the terrain **cancel** — the ground renders
exactly as today, **no tile re-bake**. For a sky point (fixed in the universe) nothing cancels, so it sweeps.
One transform on one object does both jobs.

**Lattice impact under (B):** the camera's world position changes by at most one planet radius as you spin
(you orbit the planet centre at surface radius) — negligible against the light-year paging radius. No block
churn, no re-paging.

---

## Phase 0 — Re-arm the seed + one rotation helper

- **`SystemGenerator.ApplyScanData`** (`:255-258`): restore the seeding — `RotationPeriodSeconds`
  (8–48 h surfaced worlds, 6–16 h giants) and a tilted `SpinAxis`.
- **`CelestialBody`**: add `RotationAt(double simTime)` returning the body's rotation quaternion `R(t)`
  (`θ = 2π·simTime / RotationPeriodSeconds` about `SpinAxis`) and its inverse. Keep `ApparentSunDir` as a thin
  wrapper (`R⁻¹·sunDir` is just the body-frame sun direction). Identity when `period == 0`, so everything
  stays inert until seeded.

## Phase 1 — Orbital spin (cheap, safe, ship first)

Make planets visibly rotate when viewed from space — independent of the grounded work, and needed because the
camera is **not** attached to the body when you're out in the system.

- In **`SystemRenderer`**'s planet-sphere fragment shader, rotate the **surface-sample direction** by `R(t)⁻¹`
  (a `mat3` uniform per body) before sampling the baked map / tint — equivalently spin the sphere by `R(t)`.
- Same one-line direction rotation in **`PlanetSurfaceMap`** sampling if needed.

No camera or sky changes — distant planets just spin. Independently shippable, low risk.

## Phase 2 — Grounded day/night: attach the camera to the body frame

The model: on the surface we work entirely in the planet's body-fixed **rotating** frame. The terrain,
controller, rover physics, speed limiter and **lattice pager all stay exactly as today** (they already operate
relative to the planet). The whole frame is simply *spinning* relative to the universe, and we fold that spin
into the camera's world pose for the **sky** passes only.

Work inverts compared to model (A) — override the few **ground** passes, get all the **sky** passes for free:

- **Canonical camera = body-frame pose** (orientation relative to ground, position = offset from planet).
  This is the camera the controller / physics / lattice already use — unchanged. Extend the existing
  orbital-frame "ride" (`Program.cs:298-303`) to also carry the **rotational** delta about the planet centre,
  so a stationary observer stays over the same ground point as `t` advances.
- **Sky passes consume a world pose** with `R(t)` folded into the camera (`pos = C + R(t)·offset`,
  `orientation = R(t)·orientation_body`). These all call `camera.ViewMatrix` / `ToCameraRelative(cam)`, so they
  **wheel for free** with zero per-renderer rotation and zero touched star data:
  `GalaxyBackdrop`, `StarRenderer.RenderCatalog`, `SystemRenderer` (sun + siblings), `PlanetGlowRenderer`,
  sun-lit asteroids, `AtmosphereRenderer`.
- **Ground passes use the body pose (= today, unchanged):** terrain, rover, scatter — one pipeline, already in
  body coords. Terrain/rover lighting keeps using `ApparentSunDir` (the body-frame sun direction). Skip the
  focused `_terrainTarget` in `SystemRenderer` as now (terrain draws it).

Net change surface: one derived "world camera" for the sky passes + the rotational ride on the canonical
camera. The `R(t)` cancels for the ground (no rebake); the sky wheels via the view matrix.

## Phase 3 — Attaching / detaching the camera to the body frame (the one real cost)

Entering/leaving the surface is an explicit **re-parent** of the camera to/from the body frame:

- On **attach** (drop toward a surface): convert the camera's orientation into the body frame and, ideally, add
  the surface velocity `ω × r` so neither the view nor the motion jolts. Stamp `captureTime`.
- Use **capture-relative phase** for the grounded spin: `θ = 2π·(simTime − captureTime) / period`, so `θ = 0`
  at attach → the sky starts exactly at its true position → **no pop** → day/night proceeds from there. (The
  ground's day phase isn't tied to the far-view spin phase, but you never see both at once, so it's invisible.)
- On **detach** (climb back to free-flight): convert the camera's pose back to world orientation, dropping the
  body spin.

This is the same transition risk as before, but localised to one attach/detach point instead of smeared across
the sky renderers.

---

## Recommended sequencing & guardrails

1. **Phase 0 + 1 behind a HUD toggle** (`TerrainTuning.Rotation`, default **off**) — get orbital spin right in
   isolation.
2. **Phase 2 + 3** with capture-relative phase; verify at time-lapse on a landed world that sun, sky and
   lighting move as one and the horizon stays put.
3. Keep the `period == 0` inert path so disabled/un-seeded worlds are byte-for-byte unchanged and the feature
   is reversible.

**Verify:** land, `.` to ~x1000 → sun arcs and sets, stars wheel, the terminator crosses the ground, the
atmosphere reddens at the moving terminator, horizon/gravity stay stable; fly back to orbit → the planet spins;
no pop crossing the attach/detach boundary; rover driving is unaffected (it's body-fixed); the lattice does not
re-page from the spin.

**Effort estimate:** Phase 1 ≈ an afternoon. Phase 2 ≈ a day — but it's a *derived world camera for the sky*
plus the rotational ride, not seven per-renderer edits. Phase 3 is the attach/detach plumbing + the capture
timestamp.

---

## Rejected alternative — rotate the sky (model A)

Recorded so we don't re-litigate it: rotating the sky/sun/stars by `R(t)⁻¹` about the planet centre yields the
same image but forces per-frame rotation of millions of catalog-star `UniversePosition`s and destabilises
`StarCatalogPager` (paging churn + precision wobble near block boundaries), and touches ~7 sky renderers
instead of 3 ground passes. The camera-attached model (B) avoids all of it.

---

## Related

- Memory note: `features-rotation-cities-giants-eclipse` (status + the disabled-rotation history).
- README *Ideas & backlog*: "Planet rotation + day/night cycle — needs true geometric spin."
- Eclipses shipped disabled in the same batch for a related reason (abrupt global dimming consistent with the
  faked rotation); a moving-umbra redo pairs naturally with real geometric rotation.
