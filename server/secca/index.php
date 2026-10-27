<?php
// secupgrade.com/secca — the Kickl Certificate Authority.
//
// Anyone can send a shop-signed ownership receipt here. The CA checks it, writes it to an append-only transparency
// log, and returns the receipt with the CA's countersigned seal attached. There is no human step: every rule is
// applied by tools/Kickl.Ca, and everything the CA has ever sealed is public on this page.
//
// This site also answers the three paths the Kickl Browser uses: /v1/ca/root, /v1/ca/revocations, /v1/ca/countersign.
// All cryptography lives in tools/Kickl.Ca; this file only moves bytes. The private issuing key is in
// server/keys/ca (outside this folder) and the root private key is not on this computer at all.

declare(strict_types=1);

// In the repository this folder sits next to server/src. Deployed under a web root (for example htdocs/secca), set
// KICKL_APP_ROOT to the server/ directory (SetEnv in .htaccess or the host panel).
if (getenv('KICKL_APP_ROOT') === false || getenv('KICKL_APP_ROOT') === '') {
    putenv('KICKL_APP_ROOT=' . dirname(__DIR__));
}
$appRoot = rtrim((string) getenv('KICKL_APP_ROOT'), '/\\');
require $appRoot . '/src/Config.php';
Kickl\Registry\Config::boot();
require $appRoot . '/src/CaGateway.php';

use Kickl\Registry\CaGateway;

function reply(int $status, array $body): never
{
    http_response_code($status);
    header('Content-Type: application/json; charset=utf-8');
    header('Cache-Control: no-store');
    header('X-Content-Type-Options: nosniff');
    echo json_encode($body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    exit;
}

// path relative to this folder, wherever it is mounted (/secca on secupgrade.com, /secca on localhost)
$uriPath = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?: '/';
$base = rtrim(str_replace('\\', '/', dirname($_SERVER['SCRIPT_NAME'] ?? '/index.php')), '/');
$path = '/' . ltrim(substr($uriPath, strlen($base)), '/');
$method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');

try {
    switch (true) {
        case $method === 'GET' && ($path === '/' || $path === '/index.php'):
            header('Content-Type: text/html; charset=utf-8');
            header('Cache-Control: no-cache');
            header("Content-Security-Policy: default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'");
            header('X-Content-Type-Options: nosniff');
            readfile(__DIR__ . '/page.html');
            exit;

        // ---- the paths the Kickl Browser uses -------------------------------------------------------
        case $method === 'GET' && $path === '/v1/ca/root':
            reply(200, CaGateway::root());

        case $method === 'GET' && $path === '/v1/ca/revocations':
            reply(200, CaGateway::revocations());

        case $method === 'POST' && $path === '/v1/ca/countersign':
            $client = (string) ($_SERVER['REMOTE_ADDR'] ?? 'unknown');
            if (!CaGateway::allowRequest($client)) {
                reply(429, ['error' => 'slow-down', 'message' => 'Too many requests from your address. Try again in a few minutes.']);
            }
            [$status, $body] = CaGateway::countersign((string) file_get_contents('php://input'));
            reply($status, $body);

        case $path === '/v1/ca/countersign':
            reply(405, ['error' => 'Use POST.']);

        // ---- what the web page shows ----------------------------------------------------------------
        case $method === 'GET' && $path === '/api/overview':
            $root = CaGateway::root();
            $log = CaGateway::ledger();
            $revocations = CaGateway::revocations();
            $head = $log === [] ? null : $log[count($log) - 1];
            reply(200, [
                'root' => ['fingerprint' => $root['root']['fingerprint'] ?? '', 'algorithm' => $root['root']['algorithm'] ?? ''],
                'issuer' => [
                    'serial' => $root['issuer']['serial'] ?? 0,
                    'name' => $root['issuer']['name'] ?? '',
                    'fingerprint' => $root['issuer']['key']['fingerprint'] ?? '',
                    'notBefore' => $root['issuer']['notBefore'] ?? 0,
                    'notAfter' => $root['issuer']['notAfter'] ?? 0,
                ],
                'log' => ['size' => count($log), 'root' => $head['treeRoot'] ?? '', 'updated' => $head['issuedAt'] ?? 0],
                'revocations' => [
                    'number' => $revocations['number'] ?? 0,
                    'seals' => count($revocations['revokedSeals'] ?? []),
                    'issuers' => count($revocations['revokedIssuerSerials'] ?? []),
                    'signedBy' => $revocations['signedBy'] ?? 'none',
                ],
                'audit' => CaGateway::audit(),
            ]);

        case $method === 'GET' && $path === '/api/log':
            $log = array_reverse(CaGateway::ledger());
            reply(200, ['entries' => array_slice($log, 0, 200), 'total' => count($log)]);

        case $method === 'GET' && $path === '/api/lookup':
            $site = strtolower(trim((string) ($_GET['site'] ?? '')));
            $site = preg_replace('#^jttp://#', '', $site) ?? '';
            $site = preg_replace('#[/?\#].*$#', '', $site) ?? '';
            if ($site === '' || strlen($site) > 80) {
                reply(400, ['error' => 'bad-request', 'message' => 'Enter a site name such as example.com.']);
            }
            $entries = array_values(array_filter(CaGateway::ledger(), static fn($e) => $e['site'] === $site));
            $revoked = array_map(static fn($r) => $r['site'] . ':' . $r['seq'], CaGateway::revocations()['revokedSeals'] ?? []);
            foreach ($entries as &$e) {
                $e['revoked'] = in_array($e['site'] . ':' . $e['seq'], $revoked, true);
            }
            unset($e);
            reply(200, ['site' => $site, 'entries' => $entries]);

        default:
            reply(404, ['error' => 'Unknown endpoint.']);
    }
} catch (Throwable $e) {
    error_log('secca: ' . $e->getMessage());
    reply(500, ['error' => 'internal', 'message' => 'The CA could not complete that request.']);
}
