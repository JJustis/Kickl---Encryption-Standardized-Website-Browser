<?php

declare(strict_types=1);

namespace Kickl\Registry;

/**
 * Web front for the Kickl CA. All cryptography and every decision (is this receipt genuine, does it clash with a
 * claim already sealed, what goes in the transparency log) live in tools/Kickl.Ca; this class only moves bytes.
 * The root private key is never on this machine: the issuing key here is certified by it and expires in 90 days.
 *
 * Files under server/keys/ca/ (never committed):
 *   issuer/              the online issuing key (ed25519.seed, mldsa65.pkcs8)
 *   issuer-cert.json     that key's certificate, signed by the offline root
 *   root-public.json     the root's public key
 *   shop-keys.txt        public keys (one per line, base64 SPKI) of the shops whose receipts the CA will seal
 *   revocations.json     the current signed revocation list (optional)
 *   data/                the append-only ledger and alert log
 */
final class CaGateway
{
    private const MAX_BODY = 8192;

    private static function dir(): string
    {
        // KICKL_CA_DIR points a throwaway test CA at its own keys and ledger; production leaves it unset.
        return rtrim(Config::env('KICKL_CA_DIR') ?? Config::keysDir() . '/ca', '/\\');
    }

    private static function dll(): string
    {
        return Config::env('KICKL_CA_DLL') ?? Config::repoRoot() . '/tools/Kickl.Ca/bin/Release/net10.0/Kickl.Ca.dll';
    }

    private static function readJson(string $file): ?array
    {
        if (!is_file($file)) {
            return null;
        }
        $value = json_decode((string) file_get_contents($file), true);
        return is_array($value) ? $value : null;
    }

    /** What a client needs to pin: the root's public key and the current issuing certificate. */
    public static function root(): array
    {
        $root = self::readJson(self::dir() . '/root-public.json');
        $issuer = self::readJson(self::dir() . '/issuer-cert.json');
        if ($root === null || $issuer === null) {
            throw new \RuntimeException('This server has no CA configured.');
        }
        return ['root' => $root, 'issuer' => $issuer];
    }

    public static function revocations(): array
    {
        return self::readJson(self::dir() . '/revocations.json')
            ?? ['v' => 1, 'number' => 0, 'generatedAt' => 0, 'revokedIssuerSerials' => [], 'revokedSeals' => [],
                'signedBy' => 'none', 'signerFingerprint' => '', 'sig' => ''];
    }

    /**
     * The public face of the transparency log. Entries carry the site, claim number, times and log position,
     * the same facts a seal already publishes. Never a token, never a receipt.
     *
     * @return list<array<string, mixed>>
     */
    public static function ledger(): array
    {
        $file = self::dir() . '/data/ledger.jsonl';
        if (!is_file($file)) {
            return [];
        }
        $out = [];
        foreach (file($file, FILE_IGNORE_NEW_LINES | FILE_SKIP_EMPTY_LINES) ?: [] as $line) {
            $entry = json_decode($line, true);
            $seal = is_array($entry) ? ($entry['seal'] ?? null) : null;
            if (!is_array($seal)) {
                continue;
            }
            $out[] = [
                'index' => (int) ($seal['logIndex'] ?? 0),
                'site' => (string) ($seal['site'] ?? ''),
                'seq' => (int) ($seal['seq'] ?? 0),
                'issuedAt' => (int) ($seal['issuedAt'] ?? 0),
                'expires' => (int) ($seal['receiptExpires'] ?? 0),
                'treeSize' => (int) ($seal['treeSize'] ?? 0),
                'treeRoot' => (string) ($seal['treeRoot'] ?? ''),
                'issuer' => (int) (($seal['issuer']['serial'] ?? 0)),
            ];
        }
        return $out;
    }

    /** Re-check the whole log with the CA tool. Cached for a minute so the page cannot be used to hammer it. */
    public static function audit(): array
    {
        $cache = sys_get_temp_dir() . '/kickl-ca-audit-' . md5(self::dir()) . '.json';
        if (is_file($cache) && time() - filemtime($cache) < 60) {
            $cached = json_decode((string) file_get_contents($cache), true);
            if (is_array($cached)) {
                return $cached;
            }
        }
        $dll = self::dll();
        $dir = self::dir();
        if (!is_file($dll) || !is_dir($dir . '/issuer')) {
            return ['ok' => false, 'detail' => 'The CA is not set up on this server.'];
        }
        $command = [Config::dotnet(), $dll, 'audit',
            '--issuer-dir', $dir . '/issuer', '--issuer-cert', $dir . '/issuer-cert.json',
            '--root-public', $dir . '/root-public.json', '--shop-keys', $dir . '/shop-keys.txt',
            '--data', $dir . '/data'];
        $pipes = [];
        $process = proc_open($command, [1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
        if (!is_resource($process)) {
            return ['ok' => false, 'detail' => 'Could not start the CA tool.'];
        }
        $stdout = (string) stream_get_contents($pipes[1]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        $exit = proc_close($process);
        $result = ['ok' => $exit === 0, 'detail' => trim(preg_replace('/\s+/', ' ', $stdout) ?? ''), 'checkedAt' => time()];
        @file_put_contents($cache, json_encode($result));
        return $result;
    }

    /** At most 12 seal requests per client per 10 minutes, so the page cannot be used to start endless processes. */
    public static function allowRequest(string $client): bool
    {
        $file = sys_get_temp_dir() . '/kickl-ca-rate-' . md5($client) . '.json';
        $now = time();
        $hits = is_file($file) ? (json_decode((string) file_get_contents($file), true) ?: []) : [];
        $hits = array_values(array_filter($hits, static fn($t) => is_int($t) && $now - $t < 600));
        if (count($hits) >= 12) {
            return false;
        }
        $hits[] = $now;
        @file_put_contents($file, json_encode($hits));
        return true;
    }

    /**
     * @return array{int, array<string, mixed>}  HTTP status and the JSON body to return
     */
    public static function countersign(string $body): array
    {
        if (strlen($body) === 0 || strlen($body) > self::MAX_BODY) {
            return [400, ['error' => 'bad-request', 'message' => 'Send the receipt file as the request body.']];
        }
        $json = json_decode($body, true);
        if (!is_array($json) || ($json['kickl'] ?? '') !== 'ownership-cert') {
            return [400, ['error' => 'bad-request', 'message' => 'That is not an ownership receipt.']];
        }
        // The CA never needs the seal field of an earlier answer; only what the shop signed.
        unset($json['seal']);
        $body = json_encode($json, JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR);

        $dll = self::dll();
        $dir = self::dir();
        if (!is_file($dll) || !is_dir($dir . '/issuer')) {
            return [503, ['error' => 'unavailable', 'message' => 'The CA is not set up on this server.']];
        }

        $command = [
            Config::dotnet(), $dll, 'countersign',
            '--issuer-dir', $dir . '/issuer',
            '--issuer-cert', $dir . '/issuer-cert.json',
            '--root-public', $dir . '/root-public.json',
            '--shop-keys', $dir . '/shop-keys.txt',
            '--data', $dir . '/data',
        ];
        if (is_file($dir . '/revocations.json')) {
            $command[] = '--revocations';
            $command[] = $dir . '/revocations.json';
        }

        $pipes = [];
        $process = proc_open($command, [0 => ['pipe', 'r'], 1 => ['pipe', 'w'], 2 => ['pipe', 'w']], $pipes);
        if (!is_resource($process)) {
            return [503, ['error' => 'unavailable', 'message' => 'Could not start the CA tool.']];
        }
        fwrite($pipes[0], $body);
        fclose($pipes[0]);
        $stdout = (string) stream_get_contents($pipes[1]);
        $stderr = (string) stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        $exit = proc_close($process);

        $answer = json_decode($stdout, true);
        if ($exit === 0 && is_array($answer)) {
            return [200, ['seal' => $answer]];
        }
        if ($exit === 2 && is_array($answer) && isset($answer['error'])) {
            // The CA refused on purpose. Conflicts get 409 so a client can tell them from a bad request.
            $code = (string) $answer['error'];
            $status = in_array($code, ['conflict', 'stale', 'revoked'], true) ? 409 : 422;
            return [$status, ['error' => $code, 'message' => (string) ($answer['message'] ?? 'The CA refused this receipt.')]];
        }
        error_log('Kickl CA tool failed: ' . trim($stderr !== '' ? $stderr : $stdout));
        return [500, ['error' => 'internal', 'message' => 'The CA could not process this receipt.']];
    }
}
