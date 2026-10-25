<?php

declare(strict_types=1);

/*
 * Upload this file (and .htaccess next to it) to public_html/apicalls/index.php.
 *
 * The rest of the app — src/, migrations/, keys/, .env — stays outside the web root:
 *
 *   /home/USER/kickl-registry/          <- copy of this repository's server/ directory
 *   /home/USER/public_html/apicalls/    <- only index.php + .htaccess
 *
 * Default app root: /home/USER/kickl-registry. If the folder has another name or lives
 * elsewhere, set KICKL_APP_ROOT in the host panel or in .htaccess:
 *
 *   SetEnv KICKL_APP_ROOT /home/USER/kickl-registry
 */

$appRoot = getenv('KICKL_APP_ROOT');
if ($appRoot === false || $appRoot === '') {
    $appRoot = $_SERVER['KICKL_APP_ROOT'] ?? '';
}
if ($appRoot === '') {
    $appRoot = dirname(__DIR__, 2) . DIRECTORY_SEPARATOR . 'kickl-registry';
}

$appRoot = rtrim($appRoot, '/\\');
if (!is_file($appRoot . '/public/index.php')) {
    http_response_code(500);
    header('Content-Type: application/json; charset=utf-8');
    echo json_encode(['error' => 'Registry app root is not configured correctly.']);
    exit;
}

putenv('KICKL_APP_ROOT=' . $appRoot);
require $appRoot . '/public/index.php';
