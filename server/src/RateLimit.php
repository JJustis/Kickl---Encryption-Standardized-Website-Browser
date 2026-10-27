<?php

declare(strict_types=1);

namespace Kickl\Registry;

/**
 * A small per-client sliding-window limiter for the unauthenticated endpoints, so one address cannot bloat the database or
 * hammer the server. Counts live in the system temp directory (no database write per request). REMOTE_ADDR only: forwarded-for
 * headers are client-controlled and are never trusted here.
 */
final class RateLimit
{
    /** Returns false when $client has used up $max hits for $bucket within the last $windowSeconds. */
    public static function allow(string $bucket, int $max, int $windowSeconds): bool
    {
        $client = (string) ($_SERVER['REMOTE_ADDR'] ?? 'unknown');
        $file = sys_get_temp_dir() . '/kickl-rl-' . md5($bucket . '|' . $client) . '.json';
        $now = time();
        $handle = @fopen($file, 'c+');
        if ($handle === false) {
            return true;   // a limiter that cannot write must not take the service down
        }
        try {
            flock($handle, LOCK_EX);
            $raw = stream_get_contents($handle);
            $hits = $raw !== false && $raw !== '' ? (json_decode($raw, true) ?: []) : [];
            $hits = array_values(array_filter($hits, static fn($t) => is_int($t) && $now - $t < $windowSeconds));
            $allowed = count($hits) < $max;
            if ($allowed) {
                $hits[] = $now;
            }
            ftruncate($handle, 0);
            rewind($handle);
            fwrite($handle, json_encode($hits));
            return $allowed;
        } finally {
            flock($handle, LOCK_UN);
            fclose($handle);
        }
    }

    /** Stop the request with 429 when over the limit. */
    public static function enforce(string $bucket, int $max, int $windowSeconds): void
    {
        if (!self::allow($bucket, $max, $windowSeconds)) {
            http_response_code(429);
            header('Retry-After: ' . $windowSeconds);
            header('Content-Type: application/json');
            echo json_encode(['error' => 'Too many requests. Slow down and try again later.']);
            exit;
        }
    }
}
