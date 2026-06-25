# Discovery API (PHP + MySQL)

The server side of the [discovery reporting plan](../docs/DISCOVERY_PLAN.md). A tiny REST
API over a single `discoveries` table — no framework, PDO with prepared statements — plus a
server-rendered HTML log. No client-side JavaScript, so the decimal string ids render
exactly as stored.

## Files

| File | Purpose |
|---|---|
| `schema.sql` | The `discoveries` table (`UNIQUE(object_id)` enforces first-finder-wins). |
| `config.sample.php` | Template for `config.php` (DB credentials + API key). |
| `db.php` | Shared PDO/JSON/CORS/API-key helpers. |
| `discover.php` | `POST` — report one discovery (auth: `X-Api-Key`). |
| `discoveries.php` | `GET` — list all discoveries (open; optional `?since=`). |
| `index.php` | `GET /` — read-only HTML discovery log + leaderboard (open). |

## Deploy

Requires PHP 7.4+ (8.x fine) with `pdo_mysql`, and MySQL/MariaDB.

```sh
# 1. Database + table
mysql -u root -p -e "CREATE DATABASE deepspace CHARACTER SET utf8mb4;"
mysql -u root -p deepspace < schema.sql

# 2. A least-privilege app user
mysql -u root -p -e "CREATE USER 'deepspace'@'localhost' IDENTIFIED BY 'a-strong-password';
                     GRANT SELECT, INSERT ON deepspace.discoveries TO 'deepspace'@'localhost';"

# 3. Config (gitignored)
cp config.sample.php config.php
#   edit config.php: db_pass + a long random api_key

# 4. Serve the server/ directory under your web root (Apache/nginx+php-fpm),
#    or for a quick local test:
php -S 127.0.0.1:8080
```

The app user only needs `SELECT` + `INSERT` — discoveries are never updated or deleted by
the API (first-finder-wins). Grant `DELETE` only if/when the Phase-2 admin page needs it.

## Smoke test with curl

Assuming `php -S 127.0.0.1:8080` and `api_key = testkey`:

```sh
BASE=http://127.0.0.1:8080
KEY=testkey

# Object ids are '{galaxyId}-{starId}[-PP[-MM]]'; starId is the '{galaxyId}-{starId}' root.

# Report a star — expect isNew:true
curl -s -X POST $BASE/discover.php -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"objectId":"12345-12407198355","kind":"star","starId":"12345-12407198355",
       "designation":"Helix Prime","discoverer":"Ada",
       "meta":{"class":"G","tempK":5800}}'

# Report the SAME star as a different player — expect isNew:false, discoverer still "Ada"
curl -s -X POST $BASE/discover.php -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"objectId":"12345-12407198355","kind":"star","starId":"12345-12407198355",
       "designation":"Helix Prime","discoverer":"Bob"}'

# Report a planet and a moon
curl -s -X POST $BASE/discover.php -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"objectId":"12345-12407198355-02","kind":"planet","starId":"12345-12407198355",
       "designation":"Kepler-7f","discoverer":"Ada","meta":{"type":"Ocean","hasAtmosphere":true}}'
curl -s -X POST $BASE/discover.php -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"objectId":"12345-12407198355-02-03","kind":"moon","starId":"12345-12407198355",
       "designation":"Kepler-7f III","discoverer":"Ada","meta":{"hasAtmosphere":false}}'

# List everything
curl -s $BASE/discoveries.php

# Auth + validation failures
curl -s -X POST $BASE/discover.php -H 'Content-Type: application/json' -d '{}'          # 401 (no key)
curl -s -X POST $BASE/discover.php -H "X-Api-Key: $KEY" -H 'Content-Type: application/json' \
  -d '{"objectId":"12345-12407198355-2","kind":"planet","starId":"12345-12407198355","discoverer":"X"}'  # 400 (PP not 2 digits)
```

Expected: the second star POST returns `"isNew":false` with `"discoverer":"Ada"` (first
finder kept); `discoveries.php` lists 3 objects; the bad requests return `401`/`400` with an
`{"error":...}` body.

## API reference

### `POST /discover.php`  (auth: `X-Api-Key`)

Request body (all ids are strings):

```json
{ "objectId": "12345-12407198355-02", "kind": "planet",
  "starId": "12345-12407198355", "designation": "Kepler-7f", "discoverer": "Ada",
  "meta": { "type": "Ocean", "tempK": 288, "hasAtmosphere": true } }
```

- Ids are `{galaxyId}-{starId}[-PP[-MM]]` — the galaxy id prefixes the star id, which is only unique
  within its galaxy.
- `kind` ∈ `star | planet | moon`; must match the id shape (1 / 2 / 3 dashes for star / planet / moon).
- `objectId` matches `^\d{1,20}-\d{1,20}(-\d{2}){0,2}$`; `starId` matches `^\d{1,20}-\d{1,20}$` and
  equals the `{galaxyId}-{starId}` root (first two segments) of `objectId`.
- `discoverer` 1–64 chars; `designation` truncated to 96; `meta` is free-form JSON or omitted.

Response `200` — the authoritative record:

```json
{ "objectId":"12345-12407198355-02", "kind":"planet", "starId":"12345-12407198355",
  "designation":"Kepler-7f", "discoverer":"Ada",
  "discoveredAt":"2026-06-22T18:04:11Z", "isNew": false }
```

`isNew` is `true` only when this request created the row. Errors: `400` (validation),
`401` (key), `405` (method), `500` (server) as `{"error":"..."}`.

### `GET /discoveries.php`  (open)

`{ "discoveries": [ <record>, ... ] }`, ordered oldest-first, `discoveredAt` as UTC
ISO-8601. Optional `?since=2026-06-22T00:00:00Z` returns only newer rows.

### `GET /` (`index.php`)  (open)

A server-rendered HTML page: totals per kind, a **top-discoverers** leaderboard, and the
latest 500 discoveries (newest first). Click a discoverer to filter to their finds; filter a
system with `?star=<galaxyId>-<starId>`. Times shown in UTC. Visit `http://<host>/` (or
`http://127.0.0.1:8080/` under the built-in server).

## Notes

- The shared API key deters casual writes but is not secret in a distributed client —
  documented limitation; rotate if leaked, and rate-limit by IP at the web server if abused.
- `discovered_at` is stored in UTC (`UTC_TIMESTAMP()`) and returned with a `Z` suffix.
- `config.php` is gitignored; never commit real credentials.
