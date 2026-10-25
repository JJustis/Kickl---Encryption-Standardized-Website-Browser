# 05 — Names, Identity, Certificates, Cookies

## 1. `jttp://` — the customized DNS (not an HTTP replacement)

Kickl has its own **name authority**: the Pinwheel registry. `jttp://` is how a sealed document is
addressed in that namespace. It is not a wrapper around HTTP, it does not tunnel HTTP, and it is never
used in place of `http(s)://`.

**The two lanes are separate:**

| Input | Lane | What happens |
|-------|------|--------------|
| `jttp://name.custom/…` | sealed | resolved from the local registry mirror; rendered by the sealed surface |
| `jttp://pinwheelexplained.com/…` | sealed | same, but the name is a real-TLD name; `verified` only with `_kickl` TXT proof |
| `https://example.com/…` | clearnet | system default browser (or the Kickl mini-browser if chosen) |
| `http://example.com/…` | clearnet | same; never upgraded into `jttp` |
| `name.custom` (bare) | sealed | `.custom` cannot exist on the public web, so it resolves via `jttp` |
| `name.com` (bare) | clearnet | real TLD → open-web lane |
| `name` (bare, no dot) | search | LegendDX local search; open-web only on an explicit click |

Nothing is rewritten across lanes. A clearnet URL cannot silently become a sealed page, and a `jttp`
name cannot silently make a network request to the public internet.

- Registered with WebView2 as a **standard, secure** custom scheme
  (`CoreWebView2CustomSchemeRegistration { TreatAsSecure = true, HasAuthorityComponent = true }`) and
  with WebKitGTK via `webkit_web_context_register_uri_scheme()`, so it is a secure context, relative
  URLs resolve, and CSP/History APIs behave.
- We never register a global protocol handler for `http`/`https`, and never install a root CA. We do
  register `jttp` so external links can open the app.
- A `jttp` name is a *namespace label*, validated as a hostname (LDH rules, ≤ 63 chars per label,
  exactly two labels in 1.0: `name.tld`). `tld` is either the reserved `custom` namespace or a real
  TLD.
- **Real-TLD names are a claim, not a redirect.** `jttp://pinwheelexplained.com` does not contact
  `pinwheelexplained.com`; it fetches a sealed record from the registry, signed by the name's owner.
  Proof-of-control is a DNS TXT record (`_kickl.pinwheelexplained.com = <record-hash>`), checked by
  the client through the user's resolver (or the registry, with a visible "unverified" state).

**URL grammar**

```
jttp-uri   = "jttp://" authority [ "/" path-abempty ] [ "?" query ] [ "#" fragment ]
authority  = label "." tld [ ":" port ]          ; port reserved, absent in 1.0
label      = 1*63( ldh )                          ; lowercase, no leading/trailing "-"
tld        = "custom" | real-tld                  ; real-tld requires a registry record
```

Reserved labels: `home`, `help`, `settings`, `registry`, `mail`, `chat`, `vault`, `panic`, `about`,
`security`, `debug`, `localhost`, all single characters, and RFC 2606 names.

**Clearnet handoff (default).** `http(s)` opens the **system default browser**, preserving the user's
normal browsing and its protections. The Kickl mini-browser (JS off, disposable profile, no cookies) is
offered as a second action and can be made the default in Settings → Privacy. The phrase "open in
Kickl" always means the contained mini-browser, never the sealed surface.

## 2. Registry semantics

| Operation | Who | Rules |
|-----------|-----|-------|
| Register | any identity, first-come | 3/day/device, 20/device, PoW 22 bits, 1-year term |
| Renew | owner only | extends `notAfter`; no key change required |
| Update endpoints | owner only | version++, prevHash chain |
| Rotate owner key | owner, signed by old key | 7-day cool-down; contacts notified |
| Transfer | owner, signed + acceptance by recipient | both signatures in the log |
| Freeze | operator | legal/abuse only, visible flag, appeal published, logged |
| Revoke | owner | marks revoked; pages return a tombstone |

**Squatting and disputes.** No automated trademark enforcement. A published complaint process with a
72 h operator response, a public decision log, and a right of appeal. Freezing never transfers
ownership. This is a governance document, not code — but the UI must show a frozen/revoked banner so
users are not silently served a tombstone.

**Expiry.** 90/30/7-day warnings to the owner (mail + in-app). After expiry, the name enters a
30-day grace period (tombstone served), then returns to the pool. The log records everything.

## 3. Identity addresses ("the phone number that looks like Bitcoin")

### 3.1 `kb1…` address

```
identity_bundle = CBOR {
  v: 1,
  sign_pub: Ed25519(32) || ML-DSA-65(1920),
  kem_pub:  X25519(32) || ML-KEM-768(1184),
  handle:   UTF-8 ≤ 32 bytes (optional display name),
  salt:     16 random bytes
}
digest   = SHA-256("kickl/identity/v1" || identity_bundle)[0..20]
data     = [ version=0x01 ] || digest          ; 21 bytes
address  = bech32m("kb", data)                  ; BIP-350, 5-bit groups, 6-char checksum
```

A 21-byte payload is 34 five-bit groups, so an address is 43 characters
(`kb1` + 34 data chars + 6 checksum chars). Example shape:
`kb1q9x7f3m2v8k4s6d0a5c1e2r7t9y8u3i4o5p6zx` (illustrative, not a real digest).
Properties:

- **Verifiable:** anyone can derive it from the public bundle and compare.
- **Tamper-evident:** a single character change fails Bech32m checksum (≈ 1 in 10⁹ undetected).
- **Not a key:** it is a hash, so publishing it reveals nothing that the public keys do not.
- **Not an authenticator:** seeing the address proves nothing about who sent a message; signatures do.`handle` is a *claim*, not unique. Only the registry can bind a name (`nyx.custom`) to an
address, and only the owner's signature can do that.

Implemented in `Kickl.Core/Identity` (`Bech32m`, `IdentityBundle`, `IdentityAddress`,
`ShortCode`, `SafetyNumber`) with BIP-350 vectors. The shell does not show an address yet.

Snapshot intake is implemented in `Kickl.Core/Registry` (`RegistryMirror`, `MerkleLog`,
`RegistryStateStore`): the signature is checked over the received bytes, records are hashed into
an RFC 6962 Merkle root, rollback and same-version equivocation are refused, and first-seen owner
keys are pinned. `HybridSnapshotVerifier` verifies `b64url(Ed25519(64) || ML-DSA-65(3309))` over
`"kickl/v1/registry-snapshot" || 0x00 || payload`; both halves must verify and there is no
classical-only fallback. Inclusion **and** RFC 6962 consistency proofs are implemented and tested;
the browser shield panel can approve a held owner-key change. `RegistryClient` fetches the head,
a full snapshot, or a delta, verifies the signature over canonical bytes before writing, pins the
registry signing-key fingerprint in `MirrorState` (a change fails closed), and merges delta records
into the held full record set before recomputing the Merkle root.

**End-to-end registry slice (2026-10-25).** A runnable server now exists in `server/` (PHP 8.2 +
MySQL/MariaDB): `GET /v1/registry/head`, `/snapshot`, `/delta?from=N`, `/key`, and an operator-only
`POST /v1/registry/register`. The API never signs: `tools/Kickl.RegistrySigner` owns the hybrid
private key, computes the Merkle root over canonical record bytes, and signs snapshots and deltas
offline; the API stores and serves those exact envelopes. `kickl registry sync --url URL --key
registry-key.json` performs the first full-snapshot fetch and later deltas, verifies both, and pins
the signing-key fingerprint. This was exercised against a local MariaDB with two registrations and
a delta sync (`v0 -> v1 -> v2`), including under an `/apicalls` URL prefix (`/apicalls/healthz`,
`/apicalls/v1/registry/head`, register, and sync with `--url http://127.0.0.1:8098/apicalls`).
The same routes are now served by the XAMPP Apache deployment at
`https://secupgrade.com/apicalls`: `healthz` and `head` respond, an operator registration
advanced the log to v4, and a client synced `v3 -> v4` over HTTPS.

Still pending on this path: ETag/`If-None-Match`, independent head-signature verification, attaching
RFC 6962 consistency proofs to deltas (the client currently recomputes the root from the merged set),
owner-signed registration with PoW (`register` is a shared admin token), transparency-log gossip,
and in-app browser wiring. The server is a development deployment, not the public secupgrade service.

### 3.2 Short code ("phone number")

```
short = 6 random digits || 2 check digits (ISO 7064 MOD 97-10)   → "4821 7390"
```

- Assigned by the registry on request, unique, rate-limited, free, rotatable (old code quarantined
  90 days).
- **Alias only.** Resolving a short code gives an address; the client still verifies signatures
  against the address. A short code is convenient, not authoritative — the UI shows both.
- Directory lookups for short codes are public by design (phonebook), so a short code is not a
  secret. Do not use one as a password or as proof of identity.

### 3.3 Safety numbers

For each contact pair, derive a human-verifiable fingerprint:

```
safety = SHA-256(sort(addrA, addrB) || sort(kemA, kemB) || "kickl/safety/v1")
display as 12 groups of 5 digits (60 digits, 2×30), or a 6-word BIP-39 encoding (optional)
```

Shown on first contact and on any key change. Users compare out-of-band. This is the only defence
against a registry that lies about a public key; it is why the registry is *untrusted* by design.

## 4. Domain certificates (sealed surface)

There is no X.509 PKI for `jttp://`. Trust comes from three layers:

1. **Owner identity certificate** — self-signed, contains owner address + KEM + endpoints, published
   in the registry and included in the transparency log.
2. **Record inclusion proof** — the client verifies the record is in the signed Merkle tree
   (RFC 6962). The operator cannot forge a record; it can only withhold one (detectable via log
   gossip).
3. **TOFU pin** — first-seen `ownerKem`/`ownerSig` per name is stored. Any change requires a signed
   rotation by the old key or explicit user approval with a cool-down.

**Certificate view in the UI** ("shield" in the rail) shows: name, owner address, key fingerprint,
record version, log index, pin age, last change, and a plain-language verdict (`pinned`,
`rotated by owner`, `CHANGED — verify before trusting`). No green lock without an inclusion proof.

## 5. What "local DDNS" means here

Every resolved domain is cached locally as an **address record** (not a page):

```
{ name, ownerKem, endpoints[], version, recordHash, notAfter, lastVerified, pinned }
```

- On navigation, the cached record is used if it is within its `notAfter` window and the last head
  sync is fresh (< 24 h). Otherwise the client refreshes the snapshot.
- Endpoint changes are normal (a home IP changes); the *identity* is the key, not the endpoint.
- The user can inspect and clear these records; "clear registry cache" is part of the panic scopes.
- Because resolution is local, typing a name sends **no packet**. That is the privacy property, and
  it is tested: a test navigates 100 random names and asserts zero outbound connections.

## 6. Cookie system for sealed documents

### 6.1 Model

```
Cookie {
  name, value_ct, domain, path, expires, secure, http_only, same_site, created, last_access, partition
}
```

- Stored in `cookies.db` (SQLCipher), values individually encrypted with `DEK-cookie` and an AAD of
  `domain||name||path` so a row cannot be swapped between sites.
- **Partitions:** `sealed` (default), `clearnet` (separate, wiped on modal close), and optional
  `isolate-per-site` (each name gets its own partition; breaks cross-subdomain sharing by design).
- Default policy: first-party only, `SameSite=Lax`, third-party blocked, max 50 cookies/domain,
  4 KiB/value, 64 KiB/domain total. Writes beyond the cap evict LRU with a visible count.
- `Secure` is implied for `jttp://` (secure context). `HttpOnly` is enforced by the bridge: the
  document's script cannot read it — and the sealed surface has no script anyway, so it exists for
  compatibility and for the clearnet surface.
- **No cookie is ever sent to the Pinwheel service.** There is no ambient authority toward it.

### 6.2 UI

- Shield menu → "Cookies for this site": table of name, size, set-time, expiry, party, flags; delete
  one / all; "always clear on close" toggle (global and per-site).
- A status chip on the rail shows the count when > 0, and animates when a cookie is blocked.
- Deleting a cookie is a real delete (row + key material), not a soft flag.

## 7. Clearnet certificate handling

- Delegated entirely to WebView2/Chromium: TLS 1.2 minimum (prefer 1.3), no user override, no
  "proceed anyway" button anywhere in the app.
- Interstitials are native and explain the failure code in plain language.
- **Strict mode** (off by default in 1.0, planned default in 1.1): require CT log inclusion,
  block `http://`, block mixed content, block HSTS-bypass, warn on certificate pin changes via
  Chromium's built-in pinning.
- Certificate *viewer* is native: subject, issuer, SANs, validity, key type, CT count, and a
  "why this matters" link.
- No custom root CA is installed by the app. Ever. (If a future enterprise feature needs one, it is
  a separate opt-in profile with a loud banner.)

## 8. Registration UX (owner side)

```
Registry → "Claim a name"
  1. name availability (local mirror) + policy check
  2. choose endpoints: relay (default), direct host:port (advanced), https URL (if the owner runs one)
  3. generate domain keypair (or reuse identity) → sign record
  4. PoW solve (progress bar, ~seconds)
  5. POST register → show log index + record hash + a "verify" link
  6. show the DNS-style TXT record to publish on a real domain if they own one (proof of control)
```

**Optional proof-of-control** for names that look like a real domain (`nyx.com.custom`): the owner
publishes `_kickl.<domain>` TXT = record hash. This is not required for registration but is displayed
as a trust badge and reduces impersonation disputes.

## 9. Threat notes specific to naming

| Threat | Mitigation |
|--------|-----------|
| Homograph (`xn--` lookalikes) | UTS-46 display + confusable warning + show punycode on hover |
| Registry withholds a record (eclipse) | log gossip, multiple mirrors, staleness banner |
| Registry serves different records to different users (split view) | signed head gossip; alarm on mismatch |
| Key substitution by operator | record signatures + Merkle inclusion + TOFU pin + safety numbers |
| Name squatting | PoW, quotas, complaint process, expiry |
| Malicious owner changes endpoints to a hostile host | endpoint change is a record version; UI shows "endpoint changed" and the transport still authenticates to the owner key |
| User typos into a squatted name | omnibox shows first-seen date + owner age for new names; warn if a name is < 7 days old and the user has visited it before |
