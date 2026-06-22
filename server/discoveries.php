<?php
// GET /discoveries.php — return all discoveries (the client pulls this at launch).
//
// Optional ?since=<ISO-8601 UTC> returns only rows discovered after that instant, for
// lightweight incremental polling. Open (no API key).

declare(strict_types=1);
require __DIR__ . '/db.php';

handle_cors();
if (($_SERVER['REQUEST_METHOD'] ?? '') !== 'GET') {
    send_error(405, 'GET required.');
}

$pdo   = db();
$since = $_GET['since'] ?? null;

try {
    if ($since !== null && $since !== '') {
        $ts = strtotime((string)$since);
        if ($ts === false) {
            send_error(400, 'Bad since timestamp.');
        }
        $stmt = $pdo->prepare(
            'SELECT * FROM discoveries WHERE discovered_at > :since ORDER BY discovered_at ASC, id ASC'
        );
        $stmt->execute([':since' => gmdate('Y-m-d H:i:s', $ts)]);
    } else {
        $stmt = $pdo->query('SELECT * FROM discoveries ORDER BY discovered_at ASC, id ASC');
    }
    $rows = $stmt->fetchAll();
} catch (Throwable $e) {
    send_error(500, 'Database error.');
}

send_json(['discoveries' => array_map('row_to_record', $rows)]);
