<?php
// POST /discover.php — report one discovery (star, planet, or moon).
//
// Idempotent and first-finder-wins: the first report for an object_id inserts; later reports
// for the same object change nothing and get back the original finder's record. The response
// is always the authoritative row, plus `isNew` telling the caller whether *it* got credit.
//
// Auth: requires the X-Api-Key header. Body: JSON (see validation below).

declare(strict_types=1);
require __DIR__ . '/db.php';

handle_cors();
if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'POST') {
    send_error(405, 'POST required.');
}
require_api_key();

$in = json_decode(file_get_contents('php://input') ?: '', true);
if (!is_array($in)) {
    send_error(400, 'Invalid JSON body.');
}

$objectId    = (string)($in['objectId'] ?? '');
$kind        = (string)($in['kind'] ?? '');
$starId      = (string)($in['starId'] ?? '');
$designation = trim((string)($in['designation'] ?? ''));
$discoverer  = trim((string)($in['discoverer'] ?? ''));
$meta        = array_key_exists('meta', $in) ? $in['meta'] : null;

// --- Validation -----------------------------------------------------------------
// Ids are '{galaxyId}-{starId}[-PP[-MM]]'; the galaxy id prefixes the star id (a star id is only
// unique within its galaxy). starId is the system root: the first two segments, '{galaxyId}-{starId}'.
if (!in_array($kind, ['star', 'planet', 'moon'], true)) {
    send_error(400, 'Bad kind.');
}
if (!preg_match('/^\d{1,20}-\d{1,20}(-\d{2}){0,2}$/', $objectId)) {
    send_error(400, 'Bad objectId.');
}
if (!preg_match('/^\d{1,20}-\d{1,20}$/', $starId)) {
    send_error(400, 'Bad starId.');
}
// starId must be the first two segments of objectId (the galaxy + star root).
$segments = explode('-', $objectId);
$root     = $segments[0] . '-' . $segments[1];
if ($root !== $starId) {
    send_error(400, 'starId must be the "{galaxyId}-{starId}" root of objectId.');
}
// kind must match the id shape: star = 1 dash, planet = 2, moon = 3.
$dashes   = substr_count($objectId, '-');
$expected = $kind === 'star' ? 1 : ($kind === 'planet' ? 2 : 3);
if ($dashes !== $expected) {
    send_error(400, 'kind does not match objectId shape.');
}
if ($discoverer === '' || mb_strlen($discoverer) > 64) {
    send_error(400, 'discoverer must be 1–64 characters.');
}
if (mb_strlen($designation) > 96) {
    $designation = mb_substr($designation, 0, 96);
}
$metaJson = $meta === null ? null : json_encode($meta, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);

// --- Upsert (first finder wins) -------------------------------------------------
$pdo = db();
try {
    // ON DUPLICATE KEY UPDATE id = id is a deliberate no-op: the existing row is left
    // untouched, and affected-rows is 1 for a fresh insert, 0 for an existing object.
    $stmt = $pdo->prepare(
        'INSERT INTO discoveries (object_id, kind, star_id, designation, discoverer, discovered_at, meta)
         VALUES (:oid, :kind, :sid, :desig, :disc, UTC_TIMESTAMP(), :meta)
         ON DUPLICATE KEY UPDATE id = id'
    );
    $stmt->execute([
        ':oid'   => $objectId,
        ':kind'  => $kind,
        ':sid'   => $starId,
        ':desig' => $designation,
        ':disc'  => $discoverer,
        ':meta'  => $metaJson,
    ]);
    $isNew = $stmt->rowCount() === 1;

    $sel = $pdo->prepare('SELECT * FROM discoveries WHERE object_id = :oid');
    $sel->execute([':oid' => $objectId]);
    $row = $sel->fetch();
} catch (Throwable $e) {
    send_error(500, 'Database error.');
}

if (!$row) {
    send_error(500, 'Insert/select failed.');
}

$record = row_to_record($row);
$record['isNew'] = $isNew;
send_json($record);
