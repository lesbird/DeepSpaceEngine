# Discovery Reporting — Design & Implementation Plan

A networked "who discovered it first" layer over the deterministic universe. When a
player **enters a star system** the sun is reported to a server; when they **enter a body's
near-surface environment** (its atmosphere — or a notional shell for airless worlds) that
planet or moon is reported. The first player to reach an object is credited forever. On the
HUD, any already-discovered star/planet/moon shows **who** found it and **when**. Players set
their name in-game; the client pulls the full discovery list at launch. The backend is
**PHP + MySQL** with a small REST API and a read-only HTML log.

---

## 0. The linchpin: a shared, deterministic universe

Generation is a pure function of a fixed `WorldSeed` (`0xA11CE5EED`, `Program.cs`). Every
player therefore sees the **same** stars (same `Star.Id`) and the same planets/moons in the
same generation order. That is what makes cross-player identity possible without the server
knowing anything about geometry — an object's identity is just a string id.

### 0.1 String id scheme

All ids are **strings**, both on the wire and in MySQL (`VARCHAR`). No integers, so there is
no 64-bit / JavaScript `2^53` precision concern anywhere.

- **Star id** = `"{galaxyId}-{starId}"` — the containing galaxy's id and the star's catalog id, both
  decimal → e.g. `12345-12407198355`. The galaxy id prefixes the star id because `Star.Id` is only
  unique *within* a galaxy (the star-lattice block field wraps at galaxy-to-galaxy distances); a galaxy
  id is globally unique, so the pair is too.
- **Planet id** = `"{galaxyId}-{starId}-{PP}"`, `PP` = the planet's index in `SolarSystem.Planets`,
  zero-padded to 2 digits → `12345-12407198355-02`.
- **Moon id** = `"{galaxyId}-{starId}-{PP}-{MM}"`, `MM` = the moon's index in `Planet.Moons`,
  zero-padded → `12345-12407198355-02-03`.

Indices are **0-based array positions** (deterministic generation order, identical for all
players). One helper produces these so client and server never disagree:

```csharp
static class ObjectId
{
    public static string Star(in Star s)            => s.Id.ToString();
    public static string Planet(in Star s, int pi)  => $"{Star(s)}-{pi:D2}";
    public static string Moon(in Star s, int pi, int mi) => $"{Planet(s, pi)}-{mi:D2}";
    // Locate a CelestialBody in the active system → (id, kind). Linear scan of
    // Planets/Moons by reference (or Seed); arrays are tiny.
    public static (string id, string kind) For(SolarSystem sys, CelestialBody body);
}
```

`kind` ∈ `{ "star", "planet", "moon" }` is derived from where the body is found. (The star
id is a plain decimal string, so `12407198355-02` is unambiguous — the suffix groups always
follow the first `-`.)

---

## 1. Server — PHP + MySQL

A tiny LAMP app. No framework; PDO with prepared statements throughout.

### 1.1 Schema (`server/schema.sql`)

```sql
CREATE TABLE discoveries (
  id            BIGINT UNSIGNED NOT NULL AUTO_INCREMENT PRIMARY KEY,
  object_id     VARCHAR(64)  NOT NULL,                -- '12345-56789' | '…-00' | '…-00-03'
  kind          ENUM('star','planet','moon') NOT NULL,
  star_id       VARCHAR(48)  NOT NULL,                -- system root '{galaxyId}-{starId}' (grouping / by-system views)
  designation   VARCHAR(96)  NOT NULL DEFAULT '',
  discoverer    VARCHAR(64)  NOT NULL,
  discovered_at DATETIME     NOT NULL,
  meta          JSON         NULL,                    -- class/type/temp for the web list (display only)
  UNIQUE KEY uniq_object (object_id),                 -- enforces first-finder-wins
  KEY idx_discoverer (discoverer),
  KEY idx_star (star_id)
) CHARACTER SET ascii;                                -- ids are decimal/ASCII; discoverer can be utf8 (own column collation)
```

`object_id` is the canonical string key, and its `UNIQUE` constraint is the whole
first-finder-wins mechanism: a second report for the same object cannot insert, and the
server returns the **existing** row instead. `star_id` is kept as its own column so the web
page can group a system's finds and filter by star without parsing the composite id.
(`discoverer` should be `utf8mb4` to allow non-ASCII names — set per-column or make the
table `utf8mb4` and keep ids in ASCII columns.)

### 1.2 Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| `GET`  | `/api/discoveries.php` | open | Return **all** discoveries (client pulls this at launch). Optional `?since=ISO8601` for incremental polling later. |
| `POST` | `/api/discover.php`    | API key | Report one discovery. Idempotent: returns the **authoritative** record (yours if you were first, otherwise the prior finder's). |
| `GET`  | `/` (`index.php`)      | open | Read-only HTML log + leaderboard. |

**Auth:** writes require `X-Api-Key: <key>` matched against a server-side constant. Reads
are open. (Documented limitation: a shared key shipped in a client isn't a real secret —
it deters casual abuse, nothing more. Rate-limit by IP if needed.)

**`POST /api/discover.php`** — request body (all strings):

```json
{ "objectId": "12345-12407198355-02", "kind": "planet",
  "starId": "12345-12407198355", "designation": "Kepler-7f", "discoverer": "Ada",
  "meta": { "type": "Ocean", "tempK": 288, "hasAtmosphere": true } }
```

Server logic (PDO):
1. Validate `kind` against the enum; `objectId` matches `/^\d{1,20}-\d{1,20}(-\d{2}){0,2}$/`;
   `starId` matches `/^\d{1,20}-\d{1,20}$/` and equals `objectId`'s `{galaxyId}-{starId}` root (first
   two segments); `discoverer` non-empty (≤64 chars); API key matches.
2. `INSERT ... ON DUPLICATE KEY UPDATE id = LAST_INSERT_ID(id)` (no-op touch so the row id is returned), or `INSERT IGNORE` then `SELECT`.
3. `SELECT` the row by `object_id` and return it.

Response (`200`) — the authoritative record:

```json
{ "objectId": "12345-12407198355-02", "kind": "planet",
  "starId": "12345-12407198355", "designation": "Kepler-7f", "discoverer": "Ada",
  "discoveredAt": "2026-06-22T18:04:11Z", "isNew": false }
```

`isNew` tells the client whether *it* got the credit (so it can show "You discovered…").

**`GET /api/discoveries.php`** → `{ "discoveries": [ {record}, ... ] }`, `object_id` keyed,
`discoveredAt` as ISO-8601 UTC.

### 1.3 Files

```
server/
  config.sample.php   # DB DSN/user/pass + API_KEY; copied to config.php (gitignored)
  db.php              # PDO factory, JSON helpers, api-key check, CORS headers
  discoveries.php     # GET all
  discover.php        # POST one
  index.php           # HTML: table of discoveries + "top discoverers" leaderboard (server-rendered)
  schema.sql          # table DDL
  README.md           # deploy steps
```

`index.php` renders server-side (PHP reads the DB and prints the table), so it has no JS
big-int problem and needs no client API. Leaderboard = `SELECT discoverer, COUNT(*) ...
GROUP BY discoverer ORDER BY 2 DESC`.

---

## 2. Client — C# integration

No new NuGet packages: **`System.Net.Http.HttpClient`** for transport, **`System.Text.Json`**
for JSON (already used by `TuningConfig`). New code lives in **`Game.Systems`** (it already
depends on `Game.Universe` + `Engine.Core`), with config + wiring in `Game.App`.

### 2.1 New types

```
Game.Systems/Discovery/
  ObjectId.cs          # the string-id scheme + For(sys, body) → (id, kind)  (§0.1)
  DiscoveryRecord.cs   # struct: ObjectId, Kind, StarId, Designation,
                       #         Discoverer, DiscoveredAtUtc(DateTime) — all strings/DateTime
  DiscoveryClient.cs   # REST transport (async): GetAllAsync(), ReportAsync(record)
                       #   - one HttpClient, BaseUrl + ApiKey header; plain string JSON
  DiscoveryService.cs  # game-facing cache + policy (below)
Game.App/
  DiscoveryConfig.cs   # PlayerName, ServerUrl, ApiKey, Enabled — JSON, mirrors TuningConfig
```

`discovery.json` (next to `tuning.json`) persists config and is **gitignored** (carries the
API key + player name). A `discovery.sample.json` is committed.

### 2.2 `DiscoveryService` — cache & policy

State (thread-safe):
- `ConcurrentDictionary<string, DiscoveryRecord> _byId` — one map keyed by the string
  `object_id` (ids are unique across stars/planets/moons, so a single dict suffices).
- `string PlayerName` (from config / HUD field).
- `enum SyncState { Loading, Ready, Offline }` for a HUD indicator.

API (all main-thread-callable; network work is async/off-thread):
- `Task InitializeAsync()` — `GetAllAsync()`, fill the cache, set `Ready`/`Offline`. Called at launch.
- `void ReportStar(in Star sun)` — `id = ObjectId.Star(sun)`; if `_byId` lacks it, optimistically insert a local record (`PlayerName`, `now`), then fire `ReportAsync` on a background task. When the server replies, **overwrite** the cache entry with the authoritative record (handles "someone beat me").
- `void ReportBody(SolarSystem sys, CelestialBody body)` — `(id, kind) = ObjectId.For(sys, body)`; same optimistic-then-reconcile path. Covers planets *and* moons.
- `bool TryGet(string objectId, out DiscoveryRecord)` — HUD lookup (lock-free read). Convenience overloads `TryGetStar(in Star)` / `TryGetBody(sys, body)` wrap `ObjectId`.

**Threading model:** the render/update thread only ever reads the concurrent dictionaries
and enqueues async sends; `HttpClient` continuations mutate the dictionaries (thread-safe).
No blocking on the game loop, ever. A bounded **retry queue** holds failed POSTs and is
flushed on a timer (so discoveries survive a brief server outage); on repeated failure the
service flips to `Offline` and the HUD shows a local-only credit.

### 2.3 Edge detection (in `Program.OnUpdate`)

Reusing the existing `Edge`/`_prev*` idiom already in the update loop:

- **System entry** — after `_systemManager.Update(...)`:
  ```csharp
  ulong? active = _systemManager.ActiveStarId;
  if (active is ulong id && id != _prevReportedStarId)
  {
      _discovery.ReportStar(_systemManager.Active!.Sun);
      _prevReportedStarId = id;
  }
  if (active is null) _prevReportedStarId = 0;
  ```

- **Environment entry** — fires for **every** surfaced body (planet or moon), airless or
  not, when the camera crosses its near-surface "environment" shell. Distinct from
  terrain-LOD activation (`PickTerrainTarget`, ~40 radii) — this is the much closer
  air-column / surface-proximity boundary. Airless worlds use a notional shell so they
  discover "as if they had an atmosphere":
  ```csharp
  const double AirlessEnvShell = 0.05; // shell as a fraction of radius for HasAtmosphere == false (tunable)

  CelestialBody? b = NearestSurfacedBody();
  if (b != null && _systemManager.Active is SolarSystem sys)
  {
      double alt   = b.CurrentPosition.DistanceTo(_camera.Position) - b.RadiusMeters;
      double frac  = b.HasAtmosphere ? b.AtmosphereHeight : AirlessEnvShell;
      double shell = b.RadiusMeters * frac;   // top of the (real or notional) environment
      if (alt < shell && _prevEnvBodySeed != b.Seed)
      {
          _discovery.ReportBody(sys, b);      // builds the planet/moon id via ObjectId.For
          _prevEnvBodySeed = b.Seed;
      }
      else if (alt > shell * 1.2 && _prevEnvBodySeed == b.Seed) _prevEnvBodySeed = 0; // hysteresis
  }
  ```
  `b.Seed` is only a local edge-tracking key (cheap, stable); the wire id comes from
  `ObjectId.For`, which returns `kind = "planet"` or `"moon"` and the matching id. `meta`
  still records `hasAtmosphere` for the web list, but it no longer gates discovery.
  (`AirlessEnvShell` is one constant — surface it in the Discovery config if you want it
  tunable. Choosing it near typical atmosphere heights keeps the "enter the environment"
  altitude consistent between airless and atmospheric worlds.)

- **Scanning** — when the scanner panel (`F`) is open and a body is within scan range
  (`radius × ScanRangeRadii`, far wider than the environment shell), `DrawScanner` calls
  `_discovery.ReportBody(sys, target, meta)` for the body it reads out. So a world can be
  discovered from scanner distance without descending. `ReportBody` is idempotent, so the
  per-frame call no-ops once the object is known — no edge key needed.

### 2.4 HUD display

Look up the cache wherever a star/planet is labelled and append discovery credit:

- **`StarOverlay.cs`** — system sun label and nearby-star reticles: `TryGetStar(s)` →
  ` — ⚑ Ada · 2026-06-22` when found, else dim `· unexplored`.
- **`Program.DrawScanner`** — the detailed panel: add a line
  `Discovered by {Discoverer} on {DiscoveredAtUtc:yyyy-MM-dd}` (or `Undiscovered`), and for
  the active planet/moon, the same via `TryGetBody(sys, body)`.
- **`Program.DrawHud`** — the system header and per-planet rows get a compact ⚑ marker.

To pass the service in: construct `_discovery` in `OnLoad` (after config load) and hand it
to `StarOverlay.Draw(...)` (add a parameter) or expose a static lookup. The overlay already
receives `_systemManager`, so threading the service through is a one-arg change.

### 2.5 Player-name + server fields

A new **"Discovery"** section in the Tuning panel (mirrors the existing `ImGui.InputText`
star-search box):
- `InputText` **Player name** (≤64 chars) → `_discovery.PlayerName` + persisted to `discovery.json`.
- `InputText` **Server URL** and **API key**, an **Enabled** checkbox, and a status line
  (`Syncing… / Ready (N discoveries) / Offline`).
- A **"Re-sync"** button calling `InitializeAsync()` again.

Name defaults to `Environment.UserName` if blank; reporting is skipped (local-only) until a
name is set, so nobody is credited as empty.

### 2.6 Launch retrieval

In `OnLoad`, after building `_discovery`: `_ = _discovery.InitializeAsync();` (fire-and-
forget; the HUD shows `Syncing…` until the cache is populated). Reporting that happens
before the fetch completes is reconciled when each POST returns the authoritative record.

---

## 3. End-to-end flow

```
launch ─► DiscoveryService.InitializeAsync() ─► GET /api/discoveries.php ─► fill caches
play   ─► enter system  ─► ReportStar(sun)   ─► POST /api/discover.php (kind=star)   ─► cache := authoritative
       ─► enter env.    ─► ReportBody(...)   ─► POST /api/discover.php (planet/moon) ─► cache := authoritative
HUD    ─► near a star/planet ─► TryGet... ─► "⚑ Discovered by <name> on <date>"
web    ─► GET /        ─► server-rendered table + leaderboard
```

---

## 4. Robustness, security, privacy

- **Never blocks the loop / never crashes:** all network calls are async and fully
  try/caught; any failure logs once and the game continues (mirrors `AudioEngine`'s
  degrade-to-silent stance).
- **Offline-tolerant:** unsent reports sit in the retry queue; the HUD still shows a
  provisional local credit, reconciled to the server's answer once it's reachable.
- **Server input hardening:** PDO prepared statements; validate `kind` against the enum,
  ids as `/^\d{1,20}$/`, clamp string lengths; reject oversized bodies.
- **Auth reality:** the shared key stops drive-by writes but is not secret in a shipped
  client — documented as such. Per-player tokens are a future upgrade.
- **Secrets out of git:** `config.php` and `discovery.json` are gitignored; `*.sample.*`
  templates are committed.
- **CORS:** the API sends permissive read CORS so the HTML page (or future tools) can read
  it; writes still require the key.

---

## 5. Phased implementation

1. **Server stand-up.** `schema.sql`, `db.php`, `discover.php`, `discoveries.php`,
   `config.sample.php`; verify with `curl` (POST a star, POST again → same row, GET all).
2. **HTML log.** `index.php` table + leaderboard.
3. **Client transport.** `DiscoveryRecord`, `DiscoveryClient` (+ string-id JSON
   converters), `DiscoveryConfig` + `discovery.json` load/save. Unit-test the client
   against the live PHP (or a stub) for round-trip + dedup.
4. **Service + launch fetch.** `DiscoveryService` with concurrent caches, threading, retry
   queue; wire `InitializeAsync()` into `OnLoad`.
5. **Edge detection.** System-entry and environment-entry reporting in `OnUpdate`.
6. **HUD + config UI.** Discovery credit in `StarOverlay`/`DrawScanner`/`DrawHud`; the
   Discovery tuning section with the name/URL/key fields and sync status.
7. **Hardening + docs.** Retry/offline polish; `server/README.md` deploy guide; update the
   root `README.md` and `docs/ARCHITECTURE.md` (a new networking section).

**New files:** `server/{schema.sql,config.sample.php,db.php,discover.php,discoveries.php,index.php,README.md}`,
`Game.Systems/Discovery/{ObjectId.cs,DiscoveryRecord.cs,DiscoveryClient.cs,DiscoveryService.cs}`,
`Game.App/DiscoveryConfig.cs`, `discovery.sample.json`.
**Touched:** `Game.App/Program.cs` (construct/wire/report/HUD/config UI),
`Game.App/StarOverlay.cs` (credit labels), `.gitignore` (`discovery.json`, `server/config.php`).

---

## 6. Open questions / future

- **Environment-shell size:** every surfaced planet and moon is discoverable on entering its
  environment shell (real atmosphere height, or `AirlessEnvShell` for airless worlds).
  `AirlessEnvShell = 0.05·radius` is a starting value — confirm it feels right, or expose it
  as a tunable.
- **Re-discovery feedback:** a one-shot HUD toast / UI blip (the audio system is already in)
  when *you* get a first discovery (`isNew: true`).
- **Incremental sync:** `?since=` polling so long sessions pick up others' finds without a
  full re-fetch.
- **Identity hardening:** per-player tokens; signing reports to prevent name spoofing.
- **Migrations:** if `WorldSeed` ever changes, ids change — add a `world_seed` column so the
  table is scoped to a universe.
```
