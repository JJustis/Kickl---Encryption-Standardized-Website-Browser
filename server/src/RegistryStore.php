<?php

declare(strict_types=1);

namespace Kickl\Registry;

use PDO;
use Throwable;

final class RegistryStore
{
    public static function latestVersion(PDO $db): int
    {
        $value = $db->query("SELECT meta_value FROM registry_meta WHERE meta_key = 'latest_version'")
            ->fetchColumn();
        return (int) ($value === false ? 0 : $value);
    }

    /** @return list<array<string, mixed>> */
    public static function records(PDO $db): array
    {
        $rows = $db->query('SELECT * FROM registry_records ORDER BY name')->fetchAll();
        return array_map([self::class, 'toRecord'], $rows);
    }

    /** @return array<string, mixed>|null */
    public static function head(PDO $db): ?array
    {
        $row = $db->query(
            'SELECT version, generated_at, root_hash, envelope FROM registry_snapshots
             ORDER BY version DESC LIMIT 1')->fetch();
        if ($row === false) {
            return null;
        }
        $envelope = self::decodeEnvelope($row['envelope']);
        return [
            'version' => (int) $row['version'],
            'rootHash' => (string) $row['root_hash'],
            'generatedAt' => (int) $row['generated_at'],
            'sig' => (string) ($envelope['sig'] ?? ''),
        ];
    }

    /** @return array<string, mixed>|null */
    public static function snapshot(PDO $db): ?array
    {
        $row = $db->query(
            'SELECT envelope FROM registry_snapshots ORDER BY version DESC LIMIT 1')->fetch();
        return $row === false ? null : self::decodeEnvelope($row['envelope']);
    }

    /** The next signed delta after $from, or null when the client is current. */
    public static function delta(PDO $db, int $from): ?array
    {
        $latest = self::latestVersion($db);
        if ($from <= 0 || $from >= $latest) {
            return null;
        }
        $stmt = $db->prepare(
            'SELECT envelope FROM registry_deltas WHERE from_version = ?
             ORDER BY to_version ASC LIMIT 1');
        $stmt->execute([$from]);
        $row = $stmt->fetch();
        return $row === false ? null : self::decodeEnvelope($row['envelope']);
    }

    /**
     * Upsert a record, advance the log, and store the freshly signed snapshot and delta.
     *
     * @param array<string, mixed> $input
     * @return array<string, mixed>
     */
    public static function register(PDO $db, array $input): array
    {
        $record = self::validate($input);
        $now = time();

        $db->beginTransaction();
        try {
            $db->query("SELECT meta_value FROM registry_meta WHERE meta_key = 'latest_version' FOR UPDATE")
                ->fetchColumn();

            $existing = $db->prepare('SELECT version FROM registry_records WHERE name = ?');
            $existing->execute([$record['name']]);
            $previousRecordVersion = $existing->fetchColumn();
            $record['version'] = $previousRecordVersion === false
                ? 1 : ((int) $previousRecordVersion + 1);
            $record['updatedUtc'] = $now;

            $stmt = $db->prepare(
                'INSERT INTO registry_records
                 (name, owner, doc_file, version, not_after, proof, sig, status, owner_id, badge_id,
                  title_seq, origin_device_id, direct_public_key, direct_endpoint, title, headline,
                  story_author, story_date, story_location, tags, updated_utc, content_hash)
                 VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?, ?)
                 ON DUPLICATE KEY UPDATE
                  owner = VALUES(owner), doc_file = VALUES(doc_file), version = VALUES(version),
                  not_after = VALUES(not_after), proof = VALUES(proof), sig = VALUES(sig),
                  status = VALUES(status), owner_id = VALUES(owner_id), badge_id = VALUES(badge_id),
                  title_seq = VALUES(title_seq), origin_device_id = VALUES(origin_device_id),
                  direct_public_key = VALUES(direct_public_key),
                  direct_endpoint = VALUES(direct_endpoint), title = VALUES(title),
                  headline = VALUES(headline), story_author = VALUES(story_author),
                  story_date = VALUES(story_date), story_location = VALUES(story_location),
                  tags = VALUES(tags), updated_utc = VALUES(updated_utc),
                  content_hash = VALUES(content_hash)');
            $stmt->execute([
                $record['name'], $record['owner'], $record['docFile'], $record['version'],
                $record['notAfter'], $record['proof'], $record['sig'], $record['status'],
                $record['ownerId'], $record['badgeId'], $record['titleSeq'],
                $record['originDeviceId'], $record['directPublicKey'], $record['directEndpoint'],
                $record['title'], $record['headline'], $record['storyAuthor'],
                $record['storyDate'], $record['storyLocation'],
                json_encode($record['tags'], JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR),
                $record['updatedUtc'], $record['contentHash'],
            ]);

            $latest = self::latestVersion($db);
            $version = $latest + 1;
            $db->prepare("UPDATE registry_meta SET meta_value = ? WHERE meta_key = 'latest_version'")
                ->execute([(string) $version]);

            $previousEnvelope = null;
            if ($latest > 0) {
                $row = $db->prepare('SELECT envelope FROM registry_snapshots WHERE version = ?');
                $row->execute([$latest]);
                $previous = $row->fetchColumn();
                if ($previous === false) {
                    throw new \RuntimeException("Missing signed snapshot v{$latest}.");
                }
                $previousEnvelope = (string) $previous;
            }

            $signed = Signer::publish(self::records($db), $version, $now, $previousEnvelope);
            $snapshot = $signed['snapshot'];
            self::storeSnapshot($db, $snapshot, $signed['fingerprint']);
            if ($signed['delta'] !== null) {
                self::storeDelta($db, $signed['delta']);
            }

            $db->commit();
        } catch (Throwable $error) {
            if ($db->inTransaction()) {
                $db->rollBack();
            }
            throw $error;
        }

        return [
            'name' => $record['name'],
            'recordVersion' => $record['version'],
            'version' => (int) $snapshot['version'],
            'rootHash' => (string) $snapshot['rootHash'],
            'generatedAt' => (int) $snapshot['generatedAt'],
            'signerFingerprint' => $signed['fingerprint'],
        ];
    }

    /** @param array<string, mixed> $snapshot */
    private static function storeSnapshot(PDO $db, array $snapshot, string $fingerprint): void
    {
        if (!isset($snapshot['version'], $snapshot['rootHash'], $snapshot['generatedAt'],
            $snapshot['sig'])) {
            throw new \RuntimeException('The signer returned an incomplete snapshot.');
        }
        $stmt = $db->prepare(
            'INSERT INTO registry_snapshots (version, generated_at, root_hash, envelope,
              signer_fingerprint) VALUES (?, ?, ?, ?, ?)');
        $stmt->execute([
            (int) $snapshot['version'],
            (int) $snapshot['generatedAt'],
            (string) $snapshot['rootHash'],
            json_encode($snapshot, JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR),
            $fingerprint,
        ]);
    }

    /** @param array<string, mixed> $delta */
    private static function storeDelta(PDO $db, array $delta): void
    {
        if (!isset($delta['from'], $delta['to'], $delta['rootHash'], $delta['sig'])) {
            throw new \RuntimeException('The signer returned an incomplete delta.');
        }
        $stmt = $db->prepare(
            'INSERT INTO registry_deltas (from_version, to_version, root_hash, envelope)
             VALUES (?, ?, ?, ?)');
        $stmt->execute([
            (int) $delta['from'],
            (int) $delta['to'],
            (string) $delta['rootHash'],
            json_encode($delta, JSON_UNESCAPED_SLASHES | JSON_THROW_ON_ERROR),
        ]);
    }

    /** @param array<string, mixed> $input @return array<string, mixed> */
    private static function validate(array $input): array
    {
        $name = strtolower(trim((string) ($input['name'] ?? '')));
        if (strlen($name) > 80
            || !preg_match('/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?(?:\.[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)+$/', $name)) {
            throw new ValidationException(
                'name must be a lowercase hostname such as example.custom.');
        }

        $headline = trim((string) ($input['headline'] ?? ''));
        if ($headline === '' || strlen($headline) > 400 || preg_match('/[\x00-\x1f\x7f]/', $headline)) {
            throw new ValidationException('headline must be 1-400 printable characters.');
        }

        $tags = $input['tags'] ?? null;
        if (!is_array($tags) || count($tags) < 1 || count($tags) > 8) {
            throw new ValidationException('tags must contain 1-8 entries.');
        }
        $normalisedTags = [];
        foreach ($tags as $tag) {
            $tag = strtolower(trim((string) $tag));
            if (!preg_match('/^[a-z0-9-]{2,24}$/', $tag)) {
                throw new ValidationException('each tag must be 2-24 lowercase letters, digits, or hyphens.');
            }
            $normalisedTags[$tag] = true;
        }
        $normalisedTags = array_keys($normalisedTags);
        if (count($normalisedTags) > 8) {
            throw new ValidationException('tags must contain 1-8 distinct entries.');
        }

        $text = static function (array $input, string $key, int $max) : string {
            $value = trim((string) ($input[$key] ?? ''));
            if (strlen($value) > $max) {
                throw new ValidationException("{$key} is too long.");
            }
            if (preg_match('/[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]/', $value)) {
                throw new ValidationException("{$key} contains control characters.");
            }
            return $value;
        };

        $proof = $text($input, 'proof', 100);
        if ($proof !== '' && !preg_match('/^txt:[0-9a-fA-F]{64}$/', $proof)) {
            throw new ValidationException('proof must be empty or txt:<64 hex>.');
        }

        $contentHash = strtolower(trim((string) ($input['contentHash'] ?? '')));
        if ($contentHash !== '' && !preg_match('/^[0-9a-f]{64}$/', $contentHash)) {
            throw new ValidationException('contentHash must be 64 lowercase hex characters or empty.');
        }

        $status = strtolower(trim((string) ($input['status'] ?? 'active')));
        if (!in_array($status, ['active', 'closed', 'reserved'], true)) {
            throw new ValidationException('status must be active, closed, or reserved.');
        }

        $int = static function (array $input, string $key) : int {
            $value = $input[$key] ?? 0;
            if (!is_int($value) && !(is_string($value) && preg_match('/^\d{1,18}$/', $value))) {
                throw new ValidationException("{$key} must be a non-negative integer.");
            }
            return (int) $value;
        };

        $directPublicKey = $text($input, 'directPublicKey', 4096);
        $directEndpoint = $text($input, 'directEndpoint', 200);

        return [
            'name' => $name,
            'owner' => $text($input, 'owner', 120) ?: 'kb1dev',
            'docFile' => $text($input, 'docFile', 200) ?: 'sites/' . $name . '/doc.json',
            'version' => 1,
            'notAfter' => $int($input, 'notAfter'),
            'proof' => $proof === '' ? null : $proof,
            'sig' => $text($input, 'sig', 200),
            'status' => $status,
            'ownerId' => $text($input, 'ownerId', 120),
            'badgeId' => $text($input, 'badgeId', 120),
            'titleSeq' => $int($input, 'titleSeq'),
            'originDeviceId' => $text($input, 'originDeviceId', 120),
            'directPublicKey' => $directPublicKey,
            'directEndpoint' => $directEndpoint,
            'title' => $text($input, 'title', 200) ?: $name,
            'headline' => $headline,
            'storyAuthor' => $text($input, 'storyAuthor', 120),
            'storyDate' => $text($input, 'storyDate', 40),
            'storyLocation' => $text($input, 'storyLocation', 120),
            'tags' => $normalisedTags,
            'updatedUtc' => 0,
            'contentHash' => $contentHash,
        ];
    }

    /** @param array<string, mixed> $row @return array<string, mixed> */
    private static function toRecord(array $row): array
    {
        $tags = json_decode((string) $row['tags'], true);
        if (!is_array($tags)) {
            throw new \RuntimeException("Record '{$row['name']}' has invalid tags.");
        }
        return [
            'name' => (string) $row['name'],
            'owner' => (string) $row['owner'],
            'docFile' => (string) $row['doc_file'],
            'version' => (int) $row['version'],
            'notAfter' => (int) $row['not_after'],
            'proof' => $row['proof'] === null ? null : (string) $row['proof'],
            'sig' => (string) $row['sig'],
            'status' => (string) $row['status'],
            'ownerId' => (string) $row['owner_id'],
            'badgeId' => (string) $row['badge_id'],
            'titleSeq' => (int) $row['title_seq'],
            'originDeviceId' => (string) $row['origin_device_id'],
            'directPublicKey' => (string) ($row['direct_public_key'] ?? ''),
            'directEndpoint' => (string) ($row['direct_endpoint'] ?? ''),
            'title' => (string) $row['title'],
            'headline' => (string) $row['headline'],
            'storyAuthor' => (string) $row['story_author'],
            'storyDate' => (string) $row['story_date'],
            'storyLocation' => (string) $row['story_location'],
            'tags' => array_values(array_map('strval', $tags)),
            'updatedUtc' => (int) $row['updated_utc'],
            'contentHash' => (string) $row['content_hash'],
            // Mismatch counters stay in SQL; the signed record schema has no such fields.
        ];
    }

    /** @return array<string, mixed> */
    private static function decodeEnvelope(string $json): array
    {
        $value = json_decode($json, true, 512, JSON_THROW_ON_ERROR);
        if (!is_array($value)) {
            throw new \RuntimeException('A stored registry envelope is malformed.');
        }
        return $value;
    }
}

final class ValidationException extends \RuntimeException
{
}
