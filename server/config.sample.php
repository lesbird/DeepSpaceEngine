<?php
// DeepSpaceEngine discovery API — configuration template.
//
// Copy this file to `config.php` (which is gitignored) and fill in real values.
//   cp config.sample.php config.php
//
// `api_key` is required on writes (POST /discover.php) via the `X-Api-Key` header.
// Reads (GET /discoveries.php) are open. The same key string goes in the game client's
// discovery.json. It is not a true secret in a distributed client — it only deters casual
// writes; use a long random string and rotate it if it leaks.

declare(strict_types=1);

return [
    'db_dsn'  => 'mysql:host=127.0.0.1;dbname=deepspace;charset=utf8mb4',
    'db_user' => 'deepspace',
    'db_pass' => 'changeme',
    'api_key' => 'replace-with-a-long-random-string',
];
