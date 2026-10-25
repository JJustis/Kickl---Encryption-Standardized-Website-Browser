# 04 — The Pinwheel Network

Pinwheel is the transport that carries registry snapshots, mail, chat, and updates. It is a **dumb,
opaque relay plus a public phonebook**. It is not a blockchain, not a DHT, and not a mixnet.

## 1. Design goals and constraints

| Goal | Constraint that shapes the design |
|------|-----------------------------------|
| Registry is public | Full signed snapshots, locally cached → no per-name query leaks |
| Server is cheap to run | PHP 8.4 + MySQL 8 on shared/VPS hosting; no always-on sockets required |
| Works behind NAT | Polling + rendezvous + hole punching; relay fallback |
| No plaintext server-side | Envelopes are HPKE ciphertext; the server routes by opaque mailbox hash |
| Survives offline | Store-and-forward with TTL; client queue with retry |
| Fast enough for chat | 2–5 s delivery on relay path; direct P2P when possible |
| Metadata honest | Documented visibility; jitter; optional Tor; no fake anonymity claims |
| Censorship-resistant-ish | Snapshots are signed and gossiped; multiple mirrors; client cross-checks heads |

**Explicit non-goal:** hiding IP addresses from the relay. A relay that forwards packets sees IPs.
Tor/OHTTP support is the mitigation, planned for 1.1.

## 2. Service roles

| Role | Endpoint prefix | What it stores | Retention |
|------|-----------------|----------------|-----------|
| Registry | `/v1/registry/*` | signed domain records, versions, Merkle log | forever (append-only) |
| Rendezvous | `/v1/rendezvous/*` | peer presence blobs (opaque, TTL 120 s) | 120 s |
| Mailbox | `/v1/mail/*` | sealed envelopes keyed by mailbox hash | 30 days default, sender-set max 90 |
| Chat relay | `/v1/relay/*` | opaque frames for offline peers | 7 days |
| Updates | `/v1/updates/*` | signed manifests + packages (CDN-backed) | current + 2 previous |
| Transparency | `/v1/log/*` | Merkle tree nodes, inclusion/consistency proofs | forever |
| Nickles | `/v1/nickles/*` | signed testnet transactions | forever (testnet) |

## 3. Registry: snapshots, not queries

The privacy-critical decision: **the client never asks "who owns X?"**. It downloads the state and
resolves locally.

```
Client                                  Registry
  │ GET /v1/registry/head                 │
  │──────────────────────────────────────►│
  │◄─ {version, rootHash, logHead, sig,  │
  │    keyId, generatedAt}                │
  │                                       │
  │ (if newer) GET /v1/registry/delta?from=v
  │──────────────────────────────────────►│
  │◄─ {from, to, records[], proofs[],     │
  │    rootHash, sig}                     │
  │                                       │
  │ verify sig → verify Merkle root →     │
  │ apply in SQLite transaction →         │
  │ resolve names locally                 │
```

- **Snapshot** = all domain records sorted by name, Merkle-rooted. **Delta** = changed records since
  `from`, plus the new root and a consistency proof from the old root (RFC 6962).
- Clients under 20k records fetch the full snapshot (≈ 2–4 MB compressed) on first run, then deltas.
- **Rollback protection:** version strictly increases; the client stores the highest seen version and
  refuses anything lower. **Equivocation protection:** the client gossips the signed head to its
  peers (piggybacked on chat sessions); two different heads for the same version trigger a visible
  alarm and a freeze on trust.
- **No search endpoint.** Omnibox suggestions run against the local mirror. This is the entire point
  of the design and must not be "optimised away" later.
- **Registration** is a signed request from the owner's identity key: `{name, ownerKem, endpoints,
  notAfter, prevHash}`. Names are first-come, first-served within a policy (no trademark squatting
  enforcement beyond a complaint process; see `docs/05` §2). Registrations are rate-limited and may
  require a small proof-of-work to deter bulk squatting.
- **Updates**: a record version bump is how a site announces "new content". Clients poll the head and
  learn about updates **without** revealing which sites they care about.

### 3.1 Why polling is safe-ish

The registry learns: your IP, that you use Kickl, and roughly when. It does **not** learn which names
you resolve, because every client downloads the same snapshot. Jittered intervals (60–300 s,
±30%), `If-None-Match`, and shard selection (see below) reduce correlation. If a shard design is
used (10k+ names), shard choice must be **deterministic per client** but rotate slowly, or the shard
becomes a fingerprint.

### 3.2 Name policy

- Allowed: `[a-z0-9-]{1,63}`, no leading/trailing hyphen, reserved list (`home`, `help`, `settings`,
  `admin`, `registry`, `mail`, `chat`, single letters, common trademarks).
- IDN: punycode `xn--` allowed; display uses UTS-46 with a homograph warning in the UI.
- Disputes: a published complaint process; the operator can freeze a name (flag in the record) but
  cannot transfer it without the owner's signature. Freezing must be logged and visible.

## 4. Peer connectivity

```
1. Client publishes a presence blob to /v1/rendezvous (TTL 120 s):
   {peerId, endpoints[], capabilities, sig, expires}
   The blob is opaque to the server except for routing fields.
2. To reach a peer: fetch its presence blob, try direct UDP/TCP (ICE-style hole punching via STUN).
3. If direct fails in 4 s: use a relay from the peer's advertised relay set (or the server relay).
4. Relay frames are Noise-encrypted end-to-end; the relay forwards and cannot read.
5. Presence is re-published every 60 s with jitter; failure backs off to 5 min.
```

**Pinwheel ring selection.** Each client maintains `k=3` relays chosen by rendezvous hash:
`relay = HRW(peerId || epoch, relayList)` (highest-random-weight). This spreads load, avoids a single
chokepoint, and rotates hourly. Relays are ordinary peers that opted in (`capabilities: ["relay"]`)
plus the operator's fallback relay.

**Delivery guarantees.** At-least-once with idempotency keys; recipients dedupe by message id.
Ordering is per-conversation via the ratchet counter, not the network.

**Abuse controls.** Per-peer rate limits (frames/s, bytes/s), connection caps, PoW for first contact,
block lists, and a server-side per-IP aggregate cap that does not require reading content.

## 5. Mail transport

- **Addressing:** `mailbox = BLAKE3(recipientAddress || epoch)`, truncated to 16 bytes, base32.
  Epoch = 30-day window; the client polls the current and previous epoch (so mail sent near a
  boundary is not lost). The server sees rotating opaque identifiers, not stable addresses.
- **Sealed sender:** the envelope carries no sender field in the clear; the sender's identity (or a
  per-thread alias key) is inside the ciphertext. Trade-off: server-side spam filtering is impossible,
  so spam control is client-side (allowlist, PoW, reputation vouchers).
- **Deposit:** `POST /v1/mail/{mailbox}` with an envelope ≤ 1 MiB (attachments go to the blob route).
  Server verifies size class and PoW, stores, returns a receipt id.
- **Fetch:** `GET /v1/mail/{mailbox}?since=...` returns envelopes + cursor. Client acks with
  `DELETE`/`ack` after successful decrypt and local persist. Un-acked mail is redelivered.
- **Attachments:** encrypted blob uploaded to `/v1/blob/{id}` (content-addressed, ≤ 25 MiB), key
  travels inside the message. Blob TTL = 30 days after last fetch, extendable by the recipient.
- **Guarantees:** at-least-once, 30-day default retention, no read receipts unless the recipient opts
  in (a signed receipt envelope).

## 6. Chat transport

- 1:1 prefers direct P2P; fallback relay. Groups always use the relay (fan-out) in 1.0; a later
  version can do partial mesh.
- Frames ≤ 64 KiB; larger files use the blob route.
- Typing indicators and presence are **ephemeral** (not stored, TTL 10 s) and opt-out.
- Offline delivery: relay stores opaque frames for 7 days per recipient device; the DS for MLS
  ordering is the relay's per-group log (monotonic sequence, no content visibility).
- 20-second epochs from the original Pinwheel concept are supported but default to 60 s; the UI shows
  the current epoch and a lock icon when the ratchet advances.

## 7. API contract

Full request/response examples: [`../reference/api.md`](../reference/api.md). Summary:

| Method | Path | Auth | Purpose |
|--------|------|------|---------|
| GET | `/v1/registry/head` | none | current signed head |
| GET | `/v1/registry/snapshot` | none | full snapshot (ETag) |
| GET | `/v1/registry/delta?from=` | none | delta + consistency proof |
| GET | `/v1/registry/record/{name}` | none | single record (debug/verification only; clients do not use it for browsing) |
| POST | `/v1/registry/register` | device sig + PoW | create/update a name |
| POST | `/v1/registry/renew` | device sig | extend `notAfter` |
| GET | `/v1/log/head` | none | transparency head |
| GET | `/v1/log/proof?leaf=` | none | inclusion proof |
| POST | `/v1/rendezvous` | device sig | publish presence |
| GET | `/v1/rendezvous/{peerId}` | none | fetch presence |
| POST | `/v1/mail/{mailbox}` | PoW + sig | deposit envelope |
| GET | `/v1/mail/{mailbox}` | mailbox token | fetch envelopes |
| POST | `/v1/mail/{mailbox}/ack` | mailbox token | acknowledge |
| POST | `/v1/mail/{mailbox}/token` | device sig | mint short-lived mailbox token |
| POST | `/v1/relay/{peerId}` | device sig | store-and-forward frame |
| GET | `/v1/relay/{peerId}` | device sig | fetch frames |
| POST | `/v1/blob` | device sig | upload encrypted blob |
| GET | `/v1/blob/{id}` | capability | download blob |
| GET | `/v1/updates/{channel}` | none | signed manifest |
| GET | `/healthz`, `/metrics` | internal | ops |

### 7.1 Authentication

- Every authenticated request is signed: `sig = HybridSign(deviceKey, SHA-256(method||path||bodyHash||
  nonce||timestamp))`.
- `X-Kickl-Nonce` (16 B random, base64url) + `X-Kickl-Timestamp` (±120 s). The server keeps a replay
  cache (Redis or MySQL with a TTL index) keyed by nonce.
- Mail fetch uses a **mailbox token**: `HMAC(serverMailboxKey, mailbox||expiry)` minted after a signed
  request, so the raw address never appears in URLs after setup and the token is scoped/expiring.
- No cookies, no sessions, no CSRF surface.

### 7.2 Rate limits and quotas

| Scope | Limit |
|-------|-------|
| Register | 3 names/day/device, 20/device total (raise via support) |
| Mail deposit | 60/hour/mailbox-token, 5 MB/hour |
| Mailbox storage | 500 envelopes or 50 MB, whichever first |
| Blob upload | 20/day, 25 MiB each |
| Rendezvous publish | 1/30 s |
| Relay | 200 frames/min/peer, 1 MiB/min |
| Registry fetch | 12/hour/IP (ETag makes this nearly free) |

Limits are enforced per-IP and per-device with a token bucket; 429 responses include `Retry-After`.

## 8. Server reference implementation

**Stack:** PHP 8.4 (FPM), MySQL 8.0 (InnoDB, utf8mb4), nginx, TLS 1.3 only, HSTS preload, no PHP
sessions, OPcache, Composer with `--no-dev` and a committed lock file.

```
server/
├─ public/index.php          front controller, strict routing allowlist
├─ src/Http/                 Router, Request, Response, middleware
├─ src/Auth/                 hybrid signature verify, nonce cache
├─ src/Registry/             records, Merkle tree, snapshots, deltas
├─ src/Mail/                 mailboxes, envelopes, tokens, GC
├─ src/Relay/                rendezvous, frames, TTL
├─ src/Blob/                 content-addressed ciphertext store
├─ src/Log/                  transparency log
├─ src/Nickles/              testnet ledger
├─ migrations/               001_init.sql … (forward-only)
├─ tests/                    PHPUnit + integration against MySQL
└─ ops/                      nginx.conf, php.ini, backup.sh, restore.md
```

### 8.1 Implemented now (registry subset, 2026-10-25)

The repository's `server/` is the first slice of this service. It is deliberately smaller than the
tree above:

```
server/
├─ public/index.php          front controller, strict routing allowlist, JSON errors
├─ src/Config.php            environment /.env config
├─ src/Db.php                PDO, prepared statements, exceptions
├─ src/RegistryStore.php     records, validation, transactional publish
├─ src/Signer.php            invokes tools/Kickl.RegistrySigner, stores signed envelopes
├─ migrations/001_init.sql   registry tables (MariaDB 10.4+/MySQL 8)
├─ bin/migrate.php           creates the database and applies migrations
├─ deploy/apicalls/          Apache bootstrap + rewrite for /apicalls deployments
└─ README.md                 setup, endpoints, and the deliberate gap list
```

The API is base-path aware: `public/.htaccess` rewrites pretty URLs in Apache, and the front
controller strips a prefix such as `/apicalls` from `SCRIPT_NAME` (or `KICKL_BASE_PATH`). Confirmed
with both `/v1/...` and `/apicalls/v1/...`, and deployed through XAMPP Apache at
`https://secupgrade.com/apicalls` (health, head, operator register, snapshot, delta, and client
sync over HTTPS). The client takes the full base URL, for example `https://secupgrade.com/apicalls`.

Working routes: `GET /healthz`, `GET /v1/registry/head`, `GET /v1/registry/snapshot`,
`GET /v1/registry/delta?from=N`, `GET /v1/registry/key`, `POST /v1/registry/register`.
The registry signing key is held by the offline signer, not by PHP: `register` writes the row and
log version in one DB transaction, asks the signer for a snapshot + delta, and commits only when
both are signed. `tools/Kickl.RegistrySigner keygen` writes the key pair; its public half
(`registry-key.json`) is what the client pins.

This is a development deployment. Registration currently uses an `X-Kickl-Admin` shared secret,
not the device-signed request in §7.1; there is no PoW, quota, nonce cache, ETag, compression, or
rate limiting. Mail, rendezvous, relay, blobs, freshness proofs, and the standalone transparency
log are not implemented. See `server/README.md` for setup and the exact gap list.

**Hard rules**
1. **No plaintext columns.** Every payload column is `BLOB` ciphertext. Name, public keys, timestamps,
   and sizes are the only readable metadata.
2. **Prepared statements only**; no string interpolation into SQL. A static analyser rule bans
   `mysqli_query` with variables.
3. **No `unserialize()`**, no `eval`, no dynamic include from request data.
4. **Strict types** in every file; Psalm/PHPStan level 8 in CI.
5. **Secrets** from environment, never the docroot; `display_errors=Off`; logs to stderr.
6. **Uploads** are opaque bytes with a size cap, stored outside the docroot with random names;
   never executed, never parsed by PHP.
7. **Backups** encrypted at rest, tested monthly by restore drill; retention 30 days.
8. **Least privilege DB user** (no `DROP`), separate migration user.
9. **Dependency policy:** every Composer package pinned + audited; `composer audit` in CI; no package
   with an unpatched advisory.
10. **Kill switch:** a signed config flag can disable registration/relay/upload without an app update.

**Capacity estimate (1.0):** 10k users → registry 4 MB/snapshot, mail 60 MB/day, relay 5 GB/day at
peak. A 4 vCPU / 8 GB VPS with MySQL handles it; use object storage + CDN for blobs and packages.

## 9. Failure modes and behaviour

| Failure | Client behaviour |
|---------|------------------|
| Registry unreachable | Keep last verified snapshot; banner "phonebook is stale (N h)"; local browsing works |
| Head rollback / equivocation | Freeze trust, show alarm, keep current snapshot, report to user |
| Mailbox full | Sender gets `507`; message stays in outbox; UI explains |
| Relay down | Try next ring member, then direct, then queue |
| Clock skew | Reject signed responses outside ±5 min; show "check your clock" |
| TLS failure | Hard fail; no bypass |
| Signature failure | Hard fail; quarantine the object; log a security event |
| Disk full | Refuse new vault writes; never corrupt an existing container (atomic writes) |

## 10. Observability (privacy-preserving)

- Counters only: requests/min, error codes, queue depth, p95 latency, DB connections. **No per-user
  or per-IP labels**, no request logs with paths for mail/relay endpoints, no User-Agent logging.
- Retention: 14 days of aggregates, 24 h of raw error lines with IPs stripped.
- Alerts: 5xx rate, disk > 80%, replication lag, signature-verify failures, replay-cache misses.
- Public status page; incident postmortems published for security events.

## 11. Deployment runbook (outline)

1. Provision VPS, harden SSH (keys only, no root password), firewall 80/443 only.
2. Install nginx, PHP 8.4-FPM, MySQL 8, certbot/ACME with auto-renew.
3. Create DB + least-privilege user; run migrations; enable `binlog` for point-in-time recovery.
4. Configure nginx: TLS 1.3, HSTS, OCSP stapling, body limits, rate-limit zones, no directory listing.
5. Deploy code with a signed artefact; `composer install --no-dev --classmap-authoritative`.
6. Run smoke tests (`/healthz`, registry head verifies, test mail round-trip).
7. Schedule: DB backup 15 min, blob sync hourly, log rotation daily, dependency audit weekly.
8. Key management: registry signing key in an HSM or offline machine; rotate annually with overlap.
9. Incident checklist: rotate keys, publish advisory, bump minimum client version, enable kill switch.
