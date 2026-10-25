# Pinwheel registry server

A small PHP 8.2 + MySQL/MariaDB implementation of the registry half of the Pinwheel API.
It is enough to publish signed phonebook snapshots and let the browser sync them. It is **not**
the full Pinwheel service: mail, chat, rendezvous, blobs, transparency-log proofs, and account
authentication are still TODO.

The API never signs anything. `tools/Kickl.RegistrySigner` owns the hybrid private key and the
canonical byte format; the API calls it, stores the signed envelopes, and serves them unchanged.
That keeps one implementation of the snapshot rules (the same code the client verifies with).

## Requirements

- PHP 8.2+ with `pdo_mysql`, `json`, `openssl` (XAMPP's PHP works).
- MySQL 8 or MariaDB 10.4+.
- .NET 10 SDK (to build the signer).
- The MySQL service running (XAMPP Control Panel → MySQL → Start).

## Setup

```bash
# 1. Build the offline signer.
dotnet build tools/Kickl.RegistrySigner -c Release

# 2. Create a registry key. This is the key clients pin. Keep server/keys private.
dotnet tools/Kickl.RegistrySigner/bin/Release/net10.0/Kickl.RegistrySigner.dll \
  keygen --dir server/keys

# 3. Configure. Copy the example and set a long random admin token.
cp server/.env.example server/.env

# 4. Create the database and tables (safe to re-run).
C:/xampp/php/php.exe server/bin/migrate.php

# If antivirus quarantines that helper, apply the SQL directly instead:
#   mysql -u root -e "CREATE DATABASE IF NOT EXISTS kickl_registry"
#   mysql -u root kickl_registry < server/migrations/001_init.sql
#   mysql -u root kickl_registry < server/migrations/002_mail_identity.sql

# 5. Serve the API locally.
C:/xampp/php/php.exe -S 127.0.0.1:8099 -t server/public server/public/index.php
```

`server/.env` is ignored by git. `server/keys/` is ignored too: `ed25519.seed` and
`mldsa65.pkcs8` are unencrypted development keys. In production the signer runs on an offline
machine or with the key in an HSM, and only `registry-key.json` is published.

## Deploying at `https://secupgrade.com/apicalls`

The API accepts a URL prefix. Apache detects `/apicalls` from `SCRIPT_NAME`; the client just uses
the full base URL:

```bash
dotnet …/Kickl.Cli.dll registry sync \
  --url https://secupgrade.com/apicalls \
  --key registry-key.json \
  --data .kickl-live
```

Recommended layout: keep keys and source outside `public_html`.

```text
/home/USER/kickl-registry/          copy of this repository's server/ directory
  src/  migrations/  bin/  keys/  .env
/home/USER/public_html/apicalls/
  index.php                         from server/deploy/apicalls/index.php
  .htaccess                         from server/deploy/apicalls/.htaccess
```

The bootstrap in `server/deploy/apicalls/index.php` loads the app from
`/home/USER/kickl-registry` by default; change the folder or set `KICKL_APP_ROOT` if yours differs.
That keeps `src/`, `.env`, and the private signing keys unservable by Apache.

XAMPP example (app outside `htdocs`):

```text
C:\kickl\server\                     copy of server/ (src, migrations, keys, .env)
C:\xampp\htdocs\apicalls\             index.php + .htaccess from server/deploy/apicalls
```

Set `KICKL_APP_ROOT=C:/kickl/server` in `htdocs\apicalls\.htaccess` (or the Apache config), enable
`mod_rewrite`, and allow `.htaccess` overrides. Then `http://localhost/apicalls/healthz` should
return `{"ok":true,...}` without starting a second PHP process — Apache serves it.

`POST /v1/registry/register` still needs the signer on the machine that runs PHP. Install the .NET 10
runtime, build `tools/Kickl.RegistrySigner`, and set `KICKL_SIGNER_DLL` to that DLL (and
`KICKL_DOTNET` if `dotnet` is not on PATH). If the host cannot run .NET, keep registration on a
local machine and deploy only the signed snapshots to a read-only API.

Use HTTPS. The admin token is a shared secret; do not expose `/register` without TLS, rate limits,
and a long random `KICKL_ADMIN_TOKEN`.

On the current XAMPP machine, `htdocs/apicalls/` contains only the bootstrap and rewrite, with
`KICKL_APP_ROOT` pointing at this repository's `server/` directory. `https://secupgrade.com/apicalls`
is served through Apache and was verified end-to-end (health, register, snapshot, delta, client sync).

## Endpoints

| Method | Path | Auth | Purpose |
|---|---|---|---|
| GET | `/healthz` | none | DB connectivity check |
| GET | `/v1/registry/head` | none | latest version, root, timestamp, signature |
| GET | `/v1/registry/snapshot` | none | latest full signed snapshot |
| GET | `/v1/registry/delta?from=N` | none | next signed delta after `N` |
| GET | `/v1/registry/key` | none | public signing key (pin it out of band) |
| POST | `/v1/registry/register` | `X-Kickl-Admin` | upsert a record, sign a new snapshot + delta |
| POST | `/v1/mail/identity` | token hash | publish a mail public bundle + direct endpoint |
| GET | `/v1/mail/lookup?short=NNNNNNNN` | none | return a mail public bundle |

Mail is a directory here, not a mailbox. The API stores no mail messages; the sender posts the
sealed PNG directly to the endpoint in the recipient's bundle (`/v1/mail/deliver`). The recipient
keeps messages in its local SQLite vault.

Registration is **operator-only** in this slice. It is not yet the signed owner request described
in `docs/04` §3; there is no PoW, per-device quota, renewal, transfer, TXT proof check, or abuse
control. Do not expose this server to the public internet as-is.

### Register a record

```bash
curl -X POST http://127.0.0.1:8099/v1/registry/register \
  -H "X-Kickl-Admin: $KICKL_ADMIN_TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"name":"example.custom","headline":"Hello","tags":["example"],"ownerId":"owner-1"}'
```

`name` is a lowercase hostname; `headline` is 1–400 printable characters; `tags` are 1–8 unique
lowercase tags. `contentHash`, `ownerId`, `badgeId`, `originDeviceId`, `directPublicKey`, and
`directEndpoint` are optional. Each registration bumps the per-record `version` and the global
snapshot version in one transaction; if signing fails, nothing is committed.

## Client sync

```bash
dotnet src/Kickl.Cli/bin/Release/net10.0/Kickl.Cli.dll registry sync \
  --url http://127.0.0.1:8099 \
  --key server/keys/registry-key.json \
  --data .kickl-registry-test

dotnet src/Kickl.Cli/bin/Release/net10.0/Kickl.Cli.dll registry status \
  --data .kickl-registry-test
```

The first sync fetches the full snapshot; later syncs use deltas. Both are verified before
`shared/registry.json` changes: hybrid Ed25519 + ML-DSA-65 signature, Merkle root, version
monotonicity, same-version equivocation, and the pinned signing-key fingerprint.

## Deliberate gaps

- Head signatures are served but the client does not verify them yet; the snapshot/delta signature
  is what it checks.
- Deltas carry an empty `consistencyProof`. RFC 6962 consistency proofs exist in `MerkleLog` but the
  server does not yet generate/attach them, and the client does not require them for a delta it can
  recompute the root over.
- No `ETag`/`If-None-Match`, compression, rate limits, request signing, or nonce replay cache.
- Registration is a shared admin token, not an owner signature; there is no on-chain or DNS proof.
- No record deletion by design (a name is `closed`, not removed).
- The signer is a separate process per publish. Fine for a small phonebook; batch later.

## Database

`migrations/001_init.sql` creates `registry_records`, `registry_snapshots`, `registry_deltas`, and
`registry_meta`. Snapshot envelopes are stored verbatim so the bytes served are the bytes signed.
`registry_meta.latest_version` is the log head; publishing locks it with `SELECT … FOR UPDATE`.
