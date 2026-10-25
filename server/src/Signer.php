<?php

declare(strict_types=1);

namespace Kickl\Registry;

/**
 * The API never canonicalises or signs registry state itself. It hands the record set to
 * tools/Kickl.RegistrySigner, which owns the private key and the canonical byte format, then
 * stores the signed envelopes. This keeps one implementation of the signing rules.
 */
final class Signer
{
    /**
     * @param list<array<string, mixed>> $records
     * @return array{snapshot: array<string, mixed>, delta: ?array<string, mixed>, fingerprint: string}
     */
    public static function publish(array $records, int $version, int $generatedAt,
        ?string $previousEnvelope): array
    {
        $dll = Config::signerDll();
        if (!is_file($dll)) {
            throw new \RuntimeException(
                'Registry signer is not built. Run: dotnet build tools/Kickl.RegistrySigner -c Release');
        }
        $keyFile = Config::keysDir() . '/registry-key.json';
        if (!is_file($keyFile)) {
            throw new \RuntimeException(
                'No registry signing key. Run: dotnet <signer.dll> keygen --dir server/keys');
        }

        $work = sys_get_temp_dir() . '/kickl-registry-' . bin2hex(random_bytes(8));
        if (!mkdir($work, 0700, true) && !is_dir($work)) {
            throw new \RuntimeException('Could not create a signer workspace.');
        }

        try {
            $recordsPath = $work . '/records.json';
            $snapshotPath = $work . '/snapshot.json';
            $deltaPath = $work . '/delta.json';
            $previousPath = $work . '/previous.json';

            $encoded = json_encode($records, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE
                | JSON_THROW_ON_ERROR | JSON_PRESERVE_ZERO_FRACTION);
            file_put_contents($recordsPath, $encoded);

            $command = [
                Config::dotnet(), $dll, 'publish',
                '--records', $recordsPath,
                '--version', (string) $version,
                '--generated-at', (string) $generatedAt,
                '--key-dir', Config::keysDir(),
                '--out', $snapshotPath,
            ];
            if ($previousEnvelope !== null) {
                file_put_contents($previousPath, $previousEnvelope);
                $command[] = '--previous';
                $command[] = $previousPath;
                $command[] = '--out-delta';
                $command[] = $deltaPath;
            }

            [$exit, $stdout, $stderr] = self::run($command);
            if ($exit !== 0) {
                throw new \RuntimeException(
                    'Registry signer failed: ' . trim($stderr !== '' ? $stderr : $stdout));
            }
            if (!is_file($snapshotPath)) {
                throw new \RuntimeException('Registry signer produced no snapshot.');
            }

            $snapshot = json_decode((string) file_get_contents($snapshotPath), true, 512,
                JSON_THROW_ON_ERROR);
            $delta = null;
            if ($previousEnvelope !== null) {
                if (!is_file($deltaPath)) {
                    throw new \RuntimeException('Registry signer produced no delta.');
                }
                $delta = json_decode((string) file_get_contents($deltaPath), true, 512,
                    JSON_THROW_ON_ERROR);
            }

            $publicKey = json_decode((string) file_get_contents($keyFile), true, 512,
                JSON_THROW_ON_ERROR);
            $fingerprint = (string) ($publicKey['fingerprint'] ?? '');
            if (!preg_match('/^[0-9a-f]{64}$/', $fingerprint)) {
                throw new \RuntimeException('The registry public key file is malformed.');
            }

            return ['snapshot' => $snapshot, 'delta' => $delta, 'fingerprint' => $fingerprint];
        } finally {
            foreach (glob($work . '/*') ?: [] as $file) {
                @unlink($file);
            }
            @rmdir($work);
        }
    }

    /** @param list<string> $command @return array{int, string, string} */
    private static function run(array $command): array
    {
        $pipes = [];
        $process = proc_open($command, [
            0 => ['pipe', 'r'],
            1 => ['pipe', 'w'],
            2 => ['pipe', 'w'],
        ], $pipes);
        if (!is_resource($process)) {
            throw new \RuntimeException('Could not start the registry signer.');
        }

        fclose($pipes[0]);
        $stdout = (string) stream_get_contents($pipes[1]);
        $stderr = (string) stream_get_contents($pipes[2]);
        fclose($pipes[1]);
        fclose($pipes[2]);
        $exit = proc_close($process);

        return [$exit, $stdout, $stderr];
    }
}
