# Reference — Pinwheel API v1

Base URL: `https://api.pinwheel.example/v1` (deployment-specific; pin the SPKI hash in the client).
Content type: `application/cbor` for binary endpoints, `application/json` for errors.

## Conventions

- All binary fields are base64url (no padding) in JSON, raw bytes in CBOR.
- Timestamps are Unix seconds (UTC).
- Every authenticated request carries:

```
X-Kickl-Device:   <device public key id, 16 hex>
X-Kickl-Nonce:    <16 random bytes, b64url>
X-Kickl-Timestamp:<unix seconds>
X-Kickl-Signature:<hybrid signature over the canonical request hash>
```

Canonical request hash:

```
SHA-256( method || 0x00 || path || 0x00 || query || 0x00 || bodyHash || 0x00 || nonce || 0x00 || timestamp )
```

`bodyHash` = SHA-256 of the raw body (empty body → SHA-256 of empty string). Signatures are
`Ed25519(64) || ML-DSA-65(3309)`, 3373 bytes, base64url ≈ 4498 chars.

## Errors

```json
{ "error": "rate_limited", "message": "Too many registrations", "retryAfter": 3600, "requestId": "…" }
```

| HTTP | code | meaning |
|------|------|---------|
| 400 | `bad_request` | malformed CBOR/JSON or missing field |
| 401 | `bad_signature` | signature does not verify |
| 401 | `stale_timestamp` | outside ±120 s |
| 409 | `nonce_reused` | replay detected |
| 403 | `name_reserved` | policy block |
| 404 | `not_found` | unknown name/mailbox/blob |
| 409 | `version_conflict` | registry record `prevHash` mismatch |
| 413 | `too_large` | body over limit |
| 429 | `rate_limited` | see `Retry-After` |
| 503 | `maintenance` | kill switch or deploy |
| 507 | `quota_exceeded` | mailbox/blob quota |

## Registry

> **Implemented slice (development, 2026-10-25).** `server/` serves the registry routes below as
> JSON under `/v1/registry/*`: `head`, `snapshot`, `delta?from=N`, `key`, and operator-only
> `register` (authenticated with `X-Kickl-Admin`). It does not implement CBOR, shards, `ETag`,
> device-signed requests, PoW, `renew`, or `mismatch` yet. Its record schema uses the client's
> camelCase fields (`contentHash`, `ownerId`, `docFile`, …), not the target fields shown here.
> Snapshot/delta bytes are canonical JSON signed with Ed25519 + ML-DSA-65 by
> `tools/Kickl.RegistrySigner`; see [`server/README.md`](../server/README.md). The rest of this
> section is the target contract, not the current server.

### `GET /registry/head`

```json
{
  "version": 18422,
  "rootHash": "b64url:32",
  "logHead": { "treeSize": 912340, "rootHash": "b64url:32" },
  "generatedAt": 1792800000,
  "keyId": "reg-2026-01",
  "sig": "b64url:3373"
}
```

Signed payload = CBOR map without `sig`, canonical (RFC 8949 §4.2.1).

### `GET /registry/snapshot?shard=0`

Response: CBOR `{ version, rootHash, generatedAt, records: [...], sig }`.
`ETag` = `"<version>-<rootHash>"`; supports `If-None-Match` → `304`.

Record:

```json
{
  "name": "nyx.custom",
  "owner": "kb1q…",
  "ownerKem": "b64url:1216",      // X25519(32) || ML-KEM-768 pub(1184)
  "endpoints": ["relay:abc…", "https://nyx.example"],
  "title": "Nyx",
  "headline": "One line for the phonebook.",
  "tags": ["art", "night", "personal"],
  "updatedAt": 1790000000,
  "contentSha256": "b64url:32",
  "contentMismatchCount": 0,
  "lastMismatchAt": null,
  "createdAt": 1780000000,
  "notAfter": 1811536000,
  "version": 3,
  "prevHash": "b64url:32",
  "capabilities": ["pages", "mail"],
  "sig": "b64url:3373"
}
```

`headline` and `tags` are required for new records. `tags` is 1–8 entries, each 2–24 lowercase
LDH characters. `updatedAt` is server-set on every accepted publish. `contentSha256` is the SHA-256
of the delivered package (name + markup + sealed blob ciphertexts, sorted by id).

### `GET /registry/delta?from=18400`

```json
{ "from": 18400, "to": 18422, "records": [ ... ], "consistencyProof": ["b64url:32", ...],
  "rootHash": "b64url:32", "sig": "b64url:3373" }
```

### `POST /registry/register`

```json
{
  "name": "nyx.custom",
  "ownerKem": "b64url:1216",
  "endpoints": ["relay:abc…"],
  "title": "Nyx",
  "headline": "One line for the phonebook.",
  "tags": ["art", "night", "personal"],
  "contentSha256": "b64url:32",
  "notAfter": 1811536000,
  "prevHash": "b64url:32",
  "pow": { "nonce": 918273, "difficulty": 22 }
}
```

`422 metadata_required` if `headline` is empty, `tags` is empty, or a tag fails the LDH/length rule.

`201` → `{ "version": 4, "recordHash": "b64url:32", "logIndex": 912341 }`.
`409 version_conflict` if `prevHash` is stale (client must re-read the head and retry).

### `POST /registry/renew`

Same as register with `prevHash` = current record hash and no name change.

### `POST /registry/mismatch`

Anonymous attack report, sent by the receiver when the delivered package fails the record's
`contentSha256` check. No device id, IP, or page content is accepted.

```json
{ "name": "nyx.custom", "expected": "b64url:32", "actual": "b64url:32", "observedAt": 1790000000 }
```

`202` → `{ "contentMismatchCount": 7 }`. The server increments the per-name counter, records
`lastMismatchAt`, and rate-limits the endpoint. A report never changes the record's contents.

## Live index — `secupgrade.com/legenddxI2`

`https://secupgrade.com/legenddxI2` is the pinned bootstrap address, not a plaintext query API.
After the pinned service handshake, requests and responses travel in a PFS session:

```
client ephemeral P-256 ──┐
                         ├── ECDH ──► HKDF-SHA384(salt) ──► AES-256-GCM
server ephemeral P-256 ──┘
```

Sealed request:

```json
{ "kind": "name",   "value": "nyx.custom" }
{ "kind": "search", "value": "art" }
```

Sealed search response:

```json
{
  "query": "art",
  "results": [
    {
      "host": "nyx.custom",
      "title": "Nyx",
      "headline": "One line for the phonebook.",
      "updatedUtc": 1790000000,
      "tags": ["art", "night", "personal"],
      "badgeId": "kb1b…",
      "sha256": "b64url:32",
      "version": 3
    }
  ]
}
```

- Only `active` records are searchable; closed names return nothing.
- Tag matches rank before name/title/headline substrings, then newest `updatedUtc`.
- At most 50 results.
- The session key is destroyed after the response. The server never receives a page key.
- Activating a result is a client-side confirmation, never an automatic fetch.

## Transparency log

### `GET /log/head`

```json
{ "treeSize": 912341, "rootHash": "b64url:32", "timestamp": 1792800000, "sig": "b64url:3373" }
```

### `GET /log/proof?leaf=912341`

```json
{ "leaf": "b64url:32", "index": 912341, "auditPath": ["b64url:32", "..."], "rootHash": "b64url:32" }
```

## Rendezvous

### `POST /rendezvous`

```json
{ "peerId": "b64url:32", "endpoints": ["udp:1.2.3.4:51820", "relay:abc…"],
  "capabilities": ["relay", "mail"], "expires": 1792800120,
  "proof": "b64url:3373" }
```

`204`. Body is opaque beyond routing fields; the relay never parses `proof`.

### `GET /rendezvous/{peerId}`

```json
{ "peerId": "…", "endpoints": ["…"], "capabilities": ["…"], "expires": 1792800120 }
```

`404` when absent/expired (indistinguishable from "never existed").

## Mail

> **Spec vs. shipped.** This section is the planned store-and-forward mailbox API. The mail that ships today is *direct*:
> the registry only holds the sender/recipient public bundle (`POST /v1/mail/identity`, `GET /v1/mail/lookup`) and the
> sender POSTs the sealed message straight to the recipient device over TLS 1.3 pinned to the key in the bundle's
> endpoint (`https://host:port#pin=...`). See `docs/08-mail-freechat.md` §12 and `server/README.md`.

### `POST /mail/{mailbox}/token`

Signed. Returns `{ "token": "…", "expires": 1792800600 }` — `HMAC-SHA256(serverKey,
mailbox || expiry)`, scoped to one mailbox.

### `POST /mail/{mailbox}`

Headers: `X-Kickl-Pow: <nonce>`. Body: envelope CBOR (≤ 1 MiB).

```json
{ "id": "b64url:16", "sizeClass": 16384, "depositedAt": 1792800010, "expires": 1795392010 }
```

### `GET /mail/{mailbox}?token=…&since=…`

```json
{ "envelopes": [ { "id": "…", "ct": "b64url", "sizeClass": 16384, "depositedAt": 1792800010 } ],
  "cursor": "b64url:16", "more": false }
```

### `POST /mail/{mailbox}/ack`

```json
{ "ids": ["b64url:16", "..."] }
```

`204`. Idempotent.

## Relay

### `POST /relay/{peerId}`

```json
{ "id": "b64url:16", "frame": "b64url", "expires": 1793404800, "destDevice": "hex16" }
```

`202`. Frame ≤ 64 KiB, opaque.

### `GET /relay/{peerId}?since=…`

Returns `{ "frames": [ { "id": "…", "frame": "…", "storedAt": … } ], "cursor": "…" }`.

## Blobs

### `POST /blob`

Headers: `X-Kickl-Id: <BLAKE3-256 hex>`, `Content-Length`. Body = ciphertext only.
Response: `{ "id": "hex", "size": 12345, "expires": 1795392010 }`.

### `GET /blob/{id}`

Returns raw ciphertext with `Content-Digest: sha-256=:…:`. The client verifies the digest and the
AEAD tag; a mismatch is a hard failure and a security event.

## Updates

### `GET /updates/{channel}` where channel ∈ `stable`, `beta`

```json
{
  "version": "1.0.3",
  "minOs": "10.0.19045",
  "url": "https://cdn…/kickl-1.0.3.msix",
  "sha256": "hex",
  "size": 84213760,
  "rollout": 25,
  "mandatory": false,
  "publishedAt": 1792800000,
  "notes": "…",
  "sig": "b64url:3373"
}
```

Mandatory updates may be auto-applied **only** if the signature verifies against the pinned update
key, the version is greater than installed, and rollback protection passes.

## Nickles (testnet only)

### `GET /nickles/head`

```json
{ "height": 12044, "hash": "hex32", "difficulty": 20, "timestamp": 1792800000, "sig": "…" }
```

### `POST /nickles/tx`

```json
{ "tx": { "from": "kb1…", "to": "kb1…", "amount": 25, "fee": 1, "nonce": 7,
          "memo": "test", "ts": 1792800010 }, "sig": "…" }
```

`202` → `{ "txid": "hex32", "status": "pending" }`. **No fiat value, no exchange, no promise.**

## Pagination, idempotency, retries

- Cursors are opaque base64url; never parse them.
- `Idempotency-Key` header on all POSTs; the server stores the response for 24 h.
- Clients retry `429`, `500`, `502`, `503`, `504` with exponential backoff + jitter; never retry
  `4xx` except `429`.
- Max response size 8 MiB; clients stream larger blobs to disk.
