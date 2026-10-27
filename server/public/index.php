<?php

declare(strict_types=1);

use Kickl\Registry\CaGateway;
use Kickl\Registry\Config;
use Kickl\Registry\Db;
use Kickl\Registry\MailConflictException;
use Kickl\Registry\MailIdentityStore;
use Kickl\Registry\RegistryStore;
use Kickl\Registry\ValidationException;

ini_set('display_errors', '0');
header('Content-Type: application/json; charset=utf-8');
header('Cache-Control: no-store');
header('X-Content-Type-Options: nosniff');
header_remove('X-Powered-By');

// public/index.php may be deployed alone under a web root (for example /apicalls), with the
// rest of the app outside it. The bootstrap then sets KICKL_APP_ROOT before including us.
$appRoot = getenv('KICKL_APP_ROOT');
if ($appRoot === false || $appRoot === '') {
    $appRoot = dirname(__DIR__);
}
$appRoot = rtrim($appRoot, '/\\');

require $appRoot . '/src/Config.php';
Config::boot();
require $appRoot . '/src/Db.php';
require $appRoot . '/src/Signer.php';
require $appRoot . '/src/RegistryStore.php';
require $appRoot . '/src/MailIdentityStore.php';
require $appRoot . '/src/CaGateway.php';

$method = strtoupper($_SERVER['REQUEST_METHOD'] ?? 'GET');
$path = Config::requestPath();

try {
    switch (true) {
        case $method === 'GET' && $path === '/':
            respond(200, [
                'service' => 'kickl-registry',
                'api' => 'v1',
                'endpoints' => [
                    'GET /v1/registry/head',
                    'GET /v1/registry/snapshot',
                    'GET /v1/registry/delta?from=N',
                    'GET /v1/registry/key',
                    'POST /v1/registry/register',
                    'POST /v1/mail/identity',
                    'GET /v1/mail/lookup?short=NNNNNNNN',
                    'GET /v1/ca/root',
                    'GET /v1/ca/revocations',
                    'POST /v1/ca/countersign',
                ],
            ]);

        case $method === 'GET' && $path === '/healthz':
            Db::connect()->query('SELECT 1');
            respond(200, ['ok' => true, 'service' => 'kickl-registry']);

        case $method === 'GET' && $path === '/v1/registry/head':
            $head = RegistryStore::head(Db::connect());
            // An empty registry reports version 0 so a first sync is a no-op, not an error.
            respond(200, $head ?? [
                'version' => 0,
                'rootHash' => str_repeat('0', 64),
                'generatedAt' => 0,
                'sig' => '',
            ]);

        case $method === 'GET' && $path === '/v1/registry/snapshot':
            $snapshot = RegistryStore::snapshot(Db::connect());
            if ($snapshot === null) {
                respond(404, ['error' => 'No snapshot has been published yet.']);
            }
            respond(200, $snapshot);

        case $method === 'GET' && $path === '/v1/registry/delta':
            $from = filter_input(INPUT_GET, 'from', FILTER_VALIDATE_INT);
            if ($from === false || $from === null || $from < 0) {
                respond(400, ['error' => 'from must be a non-negative integer.']);
            }
            $delta = RegistryStore::delta(Db::connect(), (int) $from);
            if ($delta === null) {
                respond(404, ['error' => 'No delta from that version.']);
            }
            respond(200, $delta);

        case $method === 'GET' && $path === '/v1/registry/key':
            $file = Config::keysDir() . '/registry-key.json';
            if (!is_file($file)) {
                respond(404, ['error' => 'No registry key has been generated.']);
            }
            $key = json_decode((string) file_get_contents($file), true);
            respond(200, is_array($key) ? $key : ['error' => 'The registry key file is malformed.']);

        case $method === 'POST' && $path === '/v1/registry/register':
            requireAdmin();
            respond(201, RegistryStore::register(Db::connect(), readJsonBody()));

        case $path === '/v1/registry/register':
            respond(405, ['error' => 'Use POST.']);

        case $method === 'POST' && $path === '/v1/mail/identity':
            respond(201, MailIdentityStore::register(Db::connect(), readJsonBody()));

        case $path === '/v1/mail/identity':
            respond(405, ['error' => 'Use POST.']);

        case $method === 'GET' && $path === '/v1/mail/lookup':
            $short = trim((string) ($_GET['short'] ?? ''));
            $identity = MailIdentityStore::lookup(Db::connect(), $short);
            if ($identity === null) {
                respond(404, ['error' => 'No mail identity has that PFS id.']);
            }
            respond(200, $identity);

        case $method === 'GET' && $path === '/v1/ca/root':
            respond(200, CaGateway::root());

        case $method === 'GET' && $path === '/v1/ca/revocations':
            respond(200, CaGateway::revocations());

        case $method === 'POST' && $path === '/v1/ca/countersign':
            [$caStatus, $caBody] = CaGateway::countersign((string) file_get_contents('php://input'));
            respond($caStatus, $caBody);

        case $path === '/v1/ca/countersign':
            respond(405, ['error' => 'Use POST.']);

        default:
            respond(404, ['error' => 'Unknown endpoint.']);
    }
} catch (MailConflictException $error) {
    respond(409, ['error' => $error->getMessage()]);
} catch (ValidationException $error) {
    respond(422, ['error' => $error->getMessage()]);
} catch (JsonException $error) {
    respond(400, ['error' => 'The request body is not valid JSON.']);
} catch (Throwable $error) {
    error_log('kickl-registry: ' . $error->getMessage());
    respond(500, ['error' => 'The registry could not complete the request.']);
}

/** @param array<string, mixed> $body */
function respond(int $status, array $body): never
{
    http_response_code($status);
    echo json_encode($body, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE
        | JSON_THROW_ON_ERROR);
    exit;
}

function requireAdmin(): void
{
    $token = Config::adminToken();
    if ($token === '') {
        respond(503, ['error' => 'Registration is disabled; set KICKL_ADMIN_TOKEN.']);
    }
    $given = (string) ($_SERVER['HTTP_X_KICKL_ADMIN'] ?? '');
    if (!hash_equals($token, $given)) {
        respond(401, ['error' => 'Registration requires a valid X-Kickl-Admin token.']);
    }
}

/** @return array<string, mixed> */
function readJsonBody(): array
{
    $raw = file_get_contents('php://input');
    if ($raw === false || strlen($raw) > 65536) {
        respond(413, ['error' => 'The request body is too large.']);
    }
    $body = json_decode($raw, true, 512, JSON_THROW_ON_ERROR);
    if (!is_array($body)) {
        respond(400, ['error' => 'The request body must be a JSON object.']);
    }
    return $body;
}
