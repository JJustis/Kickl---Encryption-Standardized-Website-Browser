<?php

declare(strict_types=1);

namespace Kickl\Registry;

use PDO;

/**
 * The mail directory is deliberately tiny. It stores a signed public identity
 * bundle so a sender can look up a PFS id, verify the bundle on the client, and
 * then POST the sealed message straight to that device. Message rows do not exist.
 */
final class MailConflictException extends \RuntimeException
{
}

final class MailIdentityStore
{
    /** @param array<string, mixed> $input @return array<string, mixed> */
    public static function register(PDO $db, array $input): array
    {
        $bundle = $input['bundle'] ?? null;
        if (!is_array($bundle)) {
            throw new ValidationException('bundle must be an object.');
        }

        $id = strtolower(trim((string) ($bundle['id'] ?? '')));
        if (!preg_match('/^[0-9a-f]{16,64}$/', $id)) {
            throw new ValidationException('bundle.id must be 16-64 lowercase hex characters.');
        }
        $shortId = trim((string) ($bundle['shortId'] ?? ''));
        if (!preg_match('/^\d{8}$/', $shortId)) {
            throw new ValidationException('bundle.shortId must be eight digits.');
        }
        $handle = trim((string) ($bundle['handle'] ?? ''));
        if ($handle === '' || strlen($handle) > 32 || preg_match('/[\x00-\x1f\x7f]/', $handle)) {
            throw new ValidationException('bundle.handle must be 1-32 printable characters.');
        }
        $endpoint = rtrim(trim((string) ($bundle['endpoint'] ?? '')), '/');
        $url = filter_var($endpoint, FILTER_VALIDATE_URL);
        if ($url === false || !preg_match('#^https?://#i', $endpoint) || strlen($endpoint) > 255
            || !preg_match('/:\d{1,5}$/', $endpoint)) {
            throw new ValidationException('bundle.endpoint must be an http(s) URL with a port.');
        }
        $rsa = (string) ($bundle['rsaPublicKey'] ?? '');
        $mldsa = (string) ($bundle['mlDsaPublicKey'] ?? '');
        $sig = (string) ($bundle['sig'] ?? '');
        if (strlen($rsa) < 300 || strlen($rsa) > 4096) {
            throw new ValidationException('bundle.rsaPublicKey is missing or too large.');
        }
        if (strlen($mldsa) < 2000 || strlen($mldsa) > 5000) {
            throw new ValidationException('bundle.mlDsaPublicKey is missing or too large.');
        }
        if (strlen($sig) < 3000 || strlen($sig) > 6000) {
            throw new ValidationException('bundle.sig is missing or too large.');
        }

        $tokenHash = strtolower(trim((string) ($input['tokenHash'] ?? '')));
        if (!preg_match('/^[0-9a-f]{64}$/', $tokenHash)) {
            throw new ValidationException('tokenHash must be 64 lowercase hex characters.');
        }

        $bundleJson = json_encode($bundle, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE
            | JSON_THROW_ON_ERROR);
        if (strlen($bundleJson) > 12000) {
            throw new ValidationException('The identity bundle is too large.');
        }

        $now = time();
        $existing = $db->prepare('SELECT token_hash FROM mail_identities WHERE id = ?');
        $existing->execute([$id]);
        $storedHash = $existing->fetchColumn();
        if ($storedHash !== false && !hash_equals((string) $storedHash, $tokenHash)) {
            throw new MailConflictException('That mail identity is owned by another sign-in.');
        }

        $shortOwner = $db->prepare('SELECT id FROM mail_identities WHERE short_id = ?');
        $shortOwner->execute([$shortId]);
        $shortOwnerId = $shortOwner->fetchColumn();
        if ($shortOwnerId !== false && !hash_equals((string) $shortOwnerId, $id)) {
            throw new MailConflictException('That PFS id is already taken; generate another.');
        }

        $stmt = $db->prepare(
            'INSERT INTO mail_identities
             (id, short_id, handle, endpoint, bundle_json, token_hash, created_at, updated_at)
             VALUES (?, ?, ?, ?, ?, ?, ?, ?)
             ON DUPLICATE KEY UPDATE
              short_id = VALUES(short_id), handle = VALUES(handle), endpoint = VALUES(endpoint),
              bundle_json = VALUES(bundle_json), updated_at = VALUES(updated_at)');
        $stmt->execute([$id, $shortId, $handle, $endpoint, $bundleJson, $tokenHash, $now, $now]);

        return ['id' => $id, 'shortId' => $shortId, 'endpoint' => $endpoint];
    }

    /** @return array<string, mixed>|null */
    public static function lookup(PDO $db, string $shortId): ?array
    {
        if (!preg_match('/^\d{8}$/', $shortId)) {
            throw new ValidationException('short must be eight digits.');
        }
        $stmt = $db->prepare('SELECT bundle_json FROM mail_identities WHERE short_id = ?');
        $stmt->execute([$shortId]);
        $json = $stmt->fetchColumn();
        if ($json === false) {
            return null;
        }
        $bundle = json_decode((string) $json, true);
        return is_array($bundle) ? ['bundle' => $bundle] : null;
    }
}
