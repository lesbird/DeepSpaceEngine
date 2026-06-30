<?php
// GET / (index.php) — read-only HTML discovery log + leaderboard.
//
// Server-rendered: PHP reads the DB and prints the page, so there's no client-side
// JavaScript and the decimal string ids show exactly as stored. Open (no API key).
// Optional filters: ?star=<galaxyId>-<starId>  and/or  ?discoverer=<name>.

declare(strict_types=1);
require __DIR__ . '/db.php'; // for config()

function h($s): string { return htmlspecialchars((string)$s, ENT_QUOTES, 'UTF-8'); }
function fmt_dt($s): string { return gmdate('Y-m-d H:i', strtotime($s . ' UTC')); } // header/footer note UTC

/** A compact, human summary of the free-form meta JSON for the table. */
function fmt_meta($json): string
{
    if ($json === null || $json === '') return '';
    $m = json_decode($json, true);
    if (!is_array($m)) return '';
    $bits = [];
    if (!empty($m['class']))  $bits[] = h($m['class']) . '-class';
    if (!empty($m['type']))   $bits[] = h($m['type']);
    if (isset($m['tempK']))   $bits[] = (int)$m['tempK'] . ' K';
    if (array_key_exists('hasAtmosphere', $m)) $bits[] = $m['hasAtmosphere'] ? 'atmosphere' : 'airless';
    return implode(' · ', $bits);
}

/** Render a '{galaxyId}-{starId}[-PP[-MM]]' object id structured: dim galaxy, then star, then body
 *  suffix — with <wbr> at the segment breaks so a long id wraps cleanly inside its cell. */
function fmt_object(string $oid): string
{
    $seg = explode('-', $oid);
    if (count($seg) < 2) return '<span class="gid">' . h($oid) . '</span>';
    $out  = '<span class="gid">' . h($seg[0]) . '</span><wbr>';
    $out .= '<span class="sid">-' . h($seg[1]) . '</span>';
    if (count($seg) > 2) $out .= '<wbr><span class="bid">-' . h(implode('-', array_slice($seg, 2))) . '</span>';
    return $out;
}

$KIND_BADGE = [
    'star'   => ['★', '#ffd86b', 'star'],
    'planet' => ['●', '#6bb8ff', 'planet'],
    'moon'   => ['☾', '#cfd6e6', 'moon'],
];

// Optional filters.
// star filter is the '{galaxyId}-{starId}' system root.
$fStar = isset($_GET['star']) && preg_match('/^\d{1,20}-\d{1,20}$/', $_GET['star']) ? $_GET['star'] : null;
$fDisc = isset($_GET['discoverer']) && $_GET['discoverer'] !== '' ? (string)$_GET['discoverer'] : null;

$error = null;
$rows = [];
$leaders = [];
$counts = ['star' => 0, 'planet' => 0, 'moon' => 0];
$total = 0;

if (!is_file(__DIR__ . '/config.php')) {
    $error = 'Server not configured (missing config.php — copy config.sample.php).';
} else {
    try {
        $c = config();
        $pdo = new PDO($c['db_dsn'], $c['db_user'], $c['db_pass'], [
            PDO::ATTR_ERRMODE            => PDO::ERRMODE_EXCEPTION,
            PDO::ATTR_DEFAULT_FETCH_MODE => PDO::FETCH_ASSOC,
        ]);

        // Filtered discovery list (newest first, capped).
        $where = [];
        $args  = [];
        if ($fStar !== null) { $where[] = 'star_id = :star';    $args[':star'] = $fStar; }
        if ($fDisc !== null) { $where[] = 'discoverer = :disc'; $args[':disc'] = $fDisc; }
        $sql = 'SELECT * FROM discoveries'
             . ($where ? ' WHERE ' . implode(' AND ', $where) : '')
             . ' ORDER BY discovered_at DESC, id DESC LIMIT 500';
        $stmt = $pdo->prepare($sql);
        $stmt->execute($args);
        $rows = $stmt->fetchAll();

        // Leaderboard + per-kind totals are always over the whole table.
        $leaders = $pdo->query(
            'SELECT discoverer, COUNT(*) AS n FROM discoveries
             GROUP BY discoverer ORDER BY n DESC, discoverer ASC LIMIT 25'
        )->fetchAll();
        foreach ($pdo->query('SELECT kind, COUNT(*) AS n FROM discoveries GROUP BY kind') as $r) {
            $counts[$r['kind']] = (int)$r['n'];
        }
        $total = array_sum($counts);
    } catch (Throwable $e) {
        $error = 'Could not load discoveries (database unavailable).';
    }
}
?>
<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>DeepSpaceEngine — Discovery Log</title>
<style>
  :root { color-scheme: dark; }
  * { box-sizing: border-box; }
  body { margin: 0; background: #06080f; color: #cdd6e6;
         font: 14px/1.5 ui-monospace, SFMono-Regular, Menlo, Consolas, monospace; }
  a { color: #6bb8ff; text-decoration: none; }
  a:hover { text-decoration: underline; }
  .wrap { max-width: 1280px; margin: 0 auto; padding: 28px 18px 60px; }
  h1 { font-size: 22px; margin: 0 0 2px; letter-spacing: .04em; }
  h1 .sub { color: #7c89a6; font-size: 13px; font-weight: normal; }
  h2 { font-size: 14px; text-transform: uppercase; letter-spacing: .1em; color: #8ea0c4;
       border-bottom: 1px solid #1d2740; padding-bottom: 6px; margin: 30px 0 12px; }
  .stats { display: flex; gap: 18px; flex-wrap: wrap; margin: 14px 0 6px; color: #aab6d0; }
  .stats b { color: #e7edf7; }
  .cols { display: grid; grid-template-columns: 1fr; gap: 8px; }
  @media (min-width: 860px) { .layout { display: grid; grid-template-columns: 1fr 240px; gap: 32px; align-items: start; } }
  /* Fixed layout + per-column widths so the long galaxy-star ids wrap inside their cell
     instead of shoving the other columns around. */
  table { width: 100%; border-collapse: collapse; table-layout: fixed; }
  th, td { text-align: left; padding: 8px 12px 8px 0; border-bottom: 1px solid #131b2e; vertical-align: top; }
  th { color: #7c89a6; font-weight: normal; font-size: 12px; text-transform: uppercase; letter-spacing: .08em; }
  tr:hover td { background: #0b1020; }
  .id { overflow-wrap: anywhere; line-height: 1.35; }            /* break the 19-20 digit ids cleanly */
  .gid { color: #5c6a88; }                                       /* galaxy prefix, dimmed */
  .sid { color: #cdd6e6; }                                       /* star id */
  .bid { color: #6bb8ff; }                                       /* planet / moon suffix */
  .kind { color: #aab6d0; text-transform: capitalize; }
  .who { color: #e7edf7; overflow-wrap: anywhere; }
  .when { color: #7c89a6; white-space: nowrap; }
  .meta { color: #6f7d9c; }
  .badge { display: inline-block; min-width: 1.4em; text-align: center; margin-right: 6px; vertical-align: top; }
  .lead { width: 100%; border-collapse: collapse; }
  .lead td { border-bottom: 1px solid #131b2e; padding: 6px 8px; }
  .lead .n { text-align: right; color: #e7edf7; }
  .rank { color: #5c6a88; width: 1.6em; }
  .filter { margin: 10px 0 0; color: #aab6d0; }
  .filter a { margin-left: 8px; }
  .empty { color: #6f7d9c; padding: 24px 0; }
  .err { background: #2a1212; border: 1px solid #5a2330; color: #ffb3b3; padding: 12px 14px; border-radius: 6px; }
  footer { margin-top: 40px; color: #5c6a88; font-size: 12px; }
</style>
</head>
<body>
<div class="wrap">
  <h1>DeepSpaceEngine <span class="sub">— Discovery Log</span></h1>

<?php if ($error !== null): ?>
  <p class="err"><?= h($error) ?></p>
<?php else: ?>

  <div class="stats">
    <span><b><?= number_format($total) ?></b> discoveries</span>
    <span>★ <b><?= number_format($counts['star']) ?></b> stars</span>
    <span>● <b><?= number_format($counts['planet']) ?></b> planets</span>
    <span>☾ <b><?= number_format($counts['moon']) ?></b> moons</span>
  </div>

<?php if ($fStar !== null || $fDisc !== null): ?>
  <p class="filter">Filtered by
    <?php if ($fStar !== null): ?>star <b><?= h($fStar) ?></b><?php endif; ?>
    <?php if ($fDisc !== null): ?><?= $fStar !== null ? 'and ' : '' ?>discoverer <b><?= h($fDisc) ?></b><?php endif; ?>
    <a href="?">clear ✕</a>
  </p>
<?php endif; ?>

  <div class="layout">
    <div>
      <h2>Recent discoveries<?= count($rows) >= 500 ? ' (latest 500)' : '' ?></h2>
<?php if (!$rows): ?>
      <p class="empty">Nothing discovered yet — go fly somewhere.</p>
<?php else: ?>
      <table>
        <colgroup>
          <col style="width: 46%"><col style="width: 22%"><col style="width: 14%"><col style="width: 18%">
        </colgroup>
        <tr><th>Object</th><th>Details</th><th>Discoverer</th><th>When (UTC)</th></tr>
<?php foreach ($rows as $r):
        [$glyph, $col] = $KIND_BADGE[$r['kind']] ?? ['?', '#888', ''];
        $kindName = $KIND_BADGE[$r['kind']][2] ?? $r['kind'];
        $metaText = fmt_meta($r['meta'] ?? null); ?>
        <tr>
          <td>
            <span class="badge" style="color: <?= $col ?>" title="<?= h($r['kind']) ?>"><?= $glyph ?></span>
            <span class="id"><?= fmt_object($r['object_id']) ?></span>
          </td>
          <td>
            <span class="kind"><?= h($kindName) ?></span>
            <?php if ($metaText !== ''): ?><div class="meta"><?= $metaText ?></div><?php endif; ?>
          </td>
          <td class="who"><a href="?discoverer=<?= h(rawurlencode($r['discoverer'])) ?>"><?= h($r['discoverer']) ?></a></td>
          <td class="when"><?= h(fmt_dt($r['discovered_at'])) ?></td>
        </tr>
<?php endforeach; ?>
      </table>
<?php endif; ?>
    </div>

    <div>
      <h2>Top discoverers</h2>
<?php if (!$leaders): ?>
      <p class="empty">—</p>
<?php else: ?>
      <table class="lead">
<?php $rank = 0; foreach ($leaders as $l): $rank++; ?>
        <tr>
          <td class="rank"><?= $rank ?></td>
          <td><a href="?discoverer=<?= h(rawurlencode($l['discoverer'])) ?>"><?= h($l['discoverer']) ?></a></td>
          <td class="n"><?= number_format((int)$l['n']) ?></td>
        </tr>
<?php endforeach; ?>
      </table>
<?php endif; ?>
    </div>
  </div>

<?php endif; ?>

  <footer>First finder wins · times in UTC · <a href="discoveries.php">JSON API</a> · <a href="https://github.com/lesbird/DeepSpaceEngine">GitHub</a></footer>
</div>
</body>
</html>
