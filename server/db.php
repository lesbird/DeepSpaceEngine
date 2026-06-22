<?php
// Shared helpers for the discovery API: configuration, a single PDO connection, JSON
// responses, CORS, the API-key gate, and DB-row → API-record mapping. Included by every
// endpoint; defines no output on its own.

declare(strict_types=1);

/** Load config.php once (created from config.sample.php). */
function config(): array
{
    static $cfg = null;
    if ($cfg === null) {
        $path = __DIR__ . '/config.php';
        if (!is_file($path)) {
            send_error(500, 'Server not configured (missing config.php).');
        }
        $cfg = require $path;
    }
    return $cfg;
}

/** One shared PDO connection, in exception mode with real (non-emulated) prepares. */
function db(): PDO
{
    static $pdo = null;
    if ($pdo === null) {
        $c = config();
        try {
            $pdo = new PDO($c['db_dsn'], $c['db_user'], $c['db_pass'], [
                PDO::ATTR_ERRMODE            => PDO::ERRMODE_EXCEPTION,
                PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
                PDO::ATTR_EMULATE_PREPARES   => false,
            ]);
        } catch (Throwable $e) {
            // Don't leak DSN/credentials in the message.
            send_error(500, 'Database connection failed.');
        }
    }
    return $pdo;
}

/** Emit JSON and end the request. */
function send_json($data, int $status = 200): void
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode($data, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

function send_error(int $status, string $message): void
{
    send_json(['error' => $message], $status);
}

/**
 * Send permissive CORS headers (reads are open) and short-circuit preflight requests.
 * Call at the very top of each endpoint.
 */
function handle_cors(): void
{
    header('Access-Control-Allow-Origin: *');
    header('Access-Control-Allow-Headers: Content-Type, X-Api-Key');
    header('Access-Control-Allow-Methods: GET, POST, OPTIONS');
    if (($_SERVER['REQUEST_METHOD'] ?? '') === 'OPTIONS') {
        http_response_code(204);
        exit;
    }
}

/** Reject the request unless the X-Api-Key header matches the configured key. */
function require_api_key(): void
{
    $given    = $_SERVER['HTTP_X_API_KEY'] ?? '';
    $expected = config()['api_key'] ?? '';
    if ($expected === '' || !is_string($given) || !hash_equals($expected, $given)) {
        send_error(401, 'Invalid or missing API key.');
    }
}

/** Map a raw DB row to the API record shape (camelCase, UTC ISO-8601, decoded meta). */
function row_to_record(array $r): array
{
    return [
        'objectId'     => $r['object_id'],
        'kind'         => $r['kind'],
        'starId'       => $r['star_id'],
        'designation'  => $r['designation'],
        'discoverer'   => $r['discoverer'],
        // discovered_at is stored in UTC; render as ...Z.
        'discoveredAt' => gmdate('Y-m-d\TH:i:s\Z', strtotime($r['discovered_at'] . ' UTC')),
        'meta'         => isset($r['meta']) && $r['meta'] !== null
            ? json_decode($r['meta'], true)
            : null,
    ];
}
