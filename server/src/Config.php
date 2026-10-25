<?php

declare(strict_types=1);

namespace Kickl\Registry;

/**
 * Configuration is environment-first so the same tree runs under XAMPP, a container, or CI.
 * server/.env is optional and never committed.
 */
final class Config
{
    private static bool $loaded = false;

    public static function boot(): void
    {
        if (self::$loaded) {
            return;
        }
        self::$loaded = true;

        $env = self::serverDir() . '/.env';
        if (!is_file($env)) {
            return;
        }
        foreach (file($env, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) ?: [] as $line) {
            $line = trim($line);
            if ($line === '' || str_starts_with($line, '#') || !str_contains($line, '=')) {
                continue;
            }
            [$key, $value] = explode('=', $line, 2);
            $key = trim($key);
            if (getenv($key) !== false) {
                continue;
            }
            $value = trim($value);
            if (strlen($value) >= 2 && ($value[0] === '"' || $value[0] === "'")
                && $value[0] === $value[strlen($value) - 1]) {
                $value = substr($value, 1, -1);
            }
            putenv($key . '=' . $value);
        }
    }

    public static function env(string $key, ?string $default = null): ?string
    {
        $value = getenv($key);
        return $value === false || $value === '' ? $default : $value;
    }

    /**
     * The app directory that holds src/, public/, migrations/, keys/, and .env.
     * KICKL_APP_ROOT lets public/index.php be deployed alone under a web root while the
     * rest of the app lives outside it.
     */
    public static function appRoot(): string
    {
        $configured = self::env('KICKL_APP_ROOT');
        return rtrim($configured ?? dirname(__DIR__), '/\\');
    }

    public static function repoRoot(): string
    {
        return dirname(self::appRoot());
    }

    public static function serverDir(): string
    {
        return self::appRoot();
    }

    /**
     * URL prefix the API is mounted under, e.g. "/apicalls". Apache deployments are detected
     * from SCRIPT_NAME (/apicalls/index.php); set KICKL_BASE_PATH when that is not reliable
     * (CLI server, proxies, or rewrites that change SCRIPT_NAME).
     */
    public static function basePath(): string
    {
        $configured = self::env('KICKL_BASE_PATH');
        if ($configured !== null) {
            return $configured === '/' ? '' : '/' . trim($configured, '/');
        }

        $script = str_replace('\\', '/', (string) ($_SERVER['SCRIPT_NAME'] ?? ''));
        $dir = rtrim(dirname($script), '/');
        return $dir === '.' || $dir === '/' || $dir === '\\' ? '' : $dir;
    }

    public static function requestPath(): string
    {
        $path = parse_url($_SERVER['REQUEST_URI'] ?? '/', PHP_URL_PATH) ?: '/';
        $path = '/' . trim($path, '/');

        $base = self::basePath();
        if ($base !== '' && ($path === $base || str_starts_with($path, $base . '/'))) {
            $path = substr($path, strlen($base)) ?: '/';
        }
        return $path;
    }

    public static function dbHost(): string
    {
        return self::env('KICKL_DB_HOST', '127.0.0.1');
    }

    public static function dbPort(): int
    {
        return (int) self::env('KICKL_DB_PORT', '3306');
    }

    public static function dbName(): string
    {
        $name = self::env('KICKL_DB_NAME', 'kickl_registry');
        if (!preg_match('/^[A-Za-z0-9_]+$/', $name)) {
            throw new \RuntimeException('KICKL_DB_NAME must contain only letters, digits, and underscores.');
        }
        return $name;
    }

    public static function dbUser(): string
    {
        return self::env('KICKL_DB_USER', 'root');
    }

    public static function dbPassword(): string
    {
        return self::env('KICKL_DB_PASSWORD', '');
    }

    /** Empty means registration is disabled; there is deliberately no default admin token. */
    public static function adminToken(): string
    {
        return self::env('KICKL_ADMIN_TOKEN', '');
    }

    public static function keysDir(): string
    {
        return self::env('KICKL_KEYS_DIR') ?? self::serverDir() . '/keys';
    }

    public static function dotnet(): string
    {
        return self::env('KICKL_DOTNET', 'dotnet');
    }

    public static function signerDll(): string
    {
        return self::env('KICKL_SIGNER_DLL')
            ?? self::repoRoot() . '/tools/Kickl.RegistrySigner/bin/Release/net10.0/Kickl.RegistrySigner.dll';
    }
}
