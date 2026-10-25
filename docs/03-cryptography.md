# 03 — Cryptography Specification

> **Rule zero:** we implement *composition*, never *primitives*. Every construction below is either a
> published standard or a published protocol with public test vectors. If a feature needs something
> that is not on this page, the feature changes.

## 1. Primitive register

| Purpose | Algorithm | Parameters | Implementation |
|---------|-----------|------------|----------------|
| AEAD (the only one) | AES-256-GCM | 96-bit nonce, 128-bit tag, counter-managed or random one-shot | `System.Security.Cryptography.AesGcm` (BCL, OS-accelerated) |
| KEM (classical) | X25519 | RFC 7748 | libsodium / BCL |
| KEM (PQ) | ML-KEM-768 | FIPS 203 | .NET 10 `MLKem` (verify API at G0; fallback BouncyCastle 2.6+) |
| Signature (classical) | Ed25519 | RFC 8032 | BouncyCastle 2.7.0 (registry verification + offline signing implemented; identity/device signing is a G2 task) |
| Signature (PQ) | ML-DSA-65 | FIPS 204 | .NET 10 `MLDsa` (registry verification + offline signing implemented; identity/device signing is a G2 task) |
| Signature (root, optional) | SLH-DSA-SHA2-128s | FIPS 205 | .NET 10 / BouncyCastle |
| Signature (ownership USB) | RSA-3072-PSS | SHA-256, PSS | BCL `RSA` (slice + title statements) |
| Key wrap (ownership USB) | RSA-3072-OAEP | SHA-256 | BCL `RSA` |
| Hash | SHA-256, SHA-512 | — | BCL |
| Content ID | BLAKE3-256 | — | BLAKE3 C# binding (vetted, pinned) |
| KDF | HKDF-SHA-512 | RFC 5869, 32-byte salt, info labels | BCL `HKDF` |
| Password KDF | Argon2id v=19 | m=64 MiB, t=3, p=4, salt 16 B, out 32 B; stored params versioned | BouncyCastle.Cryptography 2.7.0 |
| Key wrapping (all symmetric keys) | AES-256-GCM | per-wrap random 96-bit nonce, AAD = wrapping context | BCL |
| Envelope | HPKE base mode, RFC 9180 | KEM = hybrid X25519+ML-KEM-768, KDF = HKDF-SHA-512, AEAD = AES-256-GCM | own thin composition over verified primitives + official test vectors |
| 1:1 messaging | X3DH + Double Ratchet | Signal spec v3, hybrid prekeys | own composition, spec vectors |
| Group messaging | MLS (RFC 9420) | ciphersuite `MLS_256_XWING_AES256GCM_SHA512_Ed25519` if stable, else hybrid X25519+MLKEM768 / AES-256-GCM / SHA-512 / Ed25519+MLDSA | MLS implementation (audit before use) |
| Transport | Noise_XX_25519_AESGCM_SHA256 + PQ mix into `psk` | per-connection | own state machine, Noise vectors |
| RNG | OS CSPRNG only | — | `RandomNumberGenerator` / `sodium_randombytes` |

**Forbidden:** MD5, SHA-1, RC4, DES/3DES, ECB, CBC without MAC, PKCS#1 v1.5 padding,
`Random`, `System.Random`, custom S-boxes, "encrypt twice with two ciphers" as a security
argument, password-derived keys used directly as data keys. RSA is allowed only for the ownership
USB envelope and title signatures, never for bulk data.

### 1.1 One AEAD, everywhere

AES-256-GCM is the only symmetric AEAD in the product. There is no ChaCha/XChaCha path, no
cipher negotiation, and no second implementation to audit. This is deliberate:

- AES-256 with a 256-bit key still has ~128-bit security under Grover; the post-quantum work is
  done by ML-KEM-768 and ML-DSA-65, not by swapping symmetric ciphers.
- Every supported target (x86-64, ARM64, the Kickl device SoC) has AES instructions. If a port
  lacks them, the port is out of spec rather than silently switching primitives.
- Nonce discipline is one rule: 96-bit, strictly increasing counters for streams, random one-shot
  nonces for single objects, rekey at 2³¹ encryptions under one key. No 192-bit-nonce exception.

The slice already uses AES-256-GCM for the keystore, document blobs, keystroke cells, PFS lookup
traffic, stego cache, and the ownership token; RSA-3072-OAEP only wraps the AES key on the owner
USB, and RSA-PSS only signs title statements.

Suite IDs are fixed per protocol version; **the attacker never chooses the suite**. A future
"suite v2" ships as a new version with its own vectors.

## 2. Key hierarchy

```
                    24-word BIP-39 phrase (offline backup)
                              │  PBKDF2-HMAC-SHA512(2048) → 64-byte seed
                              ▼
                    ┌──── root key (never stored plaintext; derived on unlock)
                    │
   ┌────────────────┼──────────────────┬─────────────────────┐
   ▼                ▼                  ▼                     ▼
identity-sign     identity-kem      device-sign          vault-master
Ed25519+MLDSA65   X25519+MLKEM768   per device           random 256-bit
   │                │                  │                     │
   └─ certifies ────┴─ device certs ───┘                     │
                                                             ├─ DEK-mail   = HKDF(vault,"mail")
                                                             ├─ DEK-chat   = HKDF(vault,"chat")
                                                             ├─ DEK-reg    = HKDF(vault,"registry")
                                                             ├─ DEK-cookie = HKDF(vault,"cookies")
                                                             └─ KEK-blob   = HKDF(vault,"blob",per-blob-salt)
```

- **Identity keys** are long-lived and only used to sign device certificates and registry records.
  They are not used for message encryption.
- **Device keys** are per installation, revocable, and certified by the identity key. Losing a device
  does not lose the identity.
- **Vault master** is random; it is wrapped by the PIN-derived KEK *and* sealed with the OS keystore.

### 2.1 PIN → KEK

```
salt      = 16 random bytes stored in identity.db (plaintext, non-secret)
KEK       = Argon2id(PIN, salt, m=64 MiB, t=3, p=4, out=32)
wrapKey   = HKDF-SHA-512(KEK, info="kickl/v1/vault-wrap", salt=vaultSalt)
wrapped   = AES-256-GCM(wrapKey, nonce=random12, aad="kickl/v1/vault", vaultMaster)
```

- Argon2id raises the cost of each offline guess, but a 6-digit PIN still has only 10⁶ candidates.
  Measure unlock cost on minimum hardware before publishing timing or brute-force estimates. TPM
  sealing is part of the target design; it is not implemented in the current slice, so the PIN KDF
  must not be presented as a second factor.
- Attempt throttling in-app (5 tries → 30 s, 10 → 5 min, configurable) is UX, not security; the real
  control is the TPM + Argon2 cost.
- Changing the PIN re-wraps the vault key; it does not re-encrypt data.
- PIN is cached in memory for a configurable window (default 5 min) as a *derived capability*; the
  raw PIN string is zeroised immediately after KDF.

### 2.2 Recovery and device enrollment

- Recovery phrase restores the identity keys and therefore the address. It does **not** restore
  message history (which was encrypted to device/vault keys) unless the user exported an encrypted
  backup. State this plainly in the UI.
- Enrolling a second device: existing device signs a `DeviceCert` (device pubkeys, name, expiry) and
  sends it over the sealed channel; new device generates its own keys locally. No key export of the
  identity private key is required after the first device (it can be re-derived from the phrase).
- Revocation publishes a signed `Revoke` record to the registry and to contacts on next contact.

## 3. Session handshake (Noise + hybrid PQ)

Goal: mutual authentication, per-session ephemeral keys, transcript binding, PFS on session
compromise, and PQ resistance for recorded traffic.

```
Noise_XX with hybrid mix:

msg1 (I→R): e
            ephX = X25519 keypair
            ephP = ML-KEM-768 keypair
            send ephX.pub || ephP.pub || random 32-byte nonce

msg2 (R→I): e, ee, s, es, pq
            R generates its own ephX/ephP, encapsulates to I's ephP
            DH1 = X25519(R.eph, I.eph)
            PQ  = ML-KEM.Encaps(I.ephP)
            K   = HKDF-SHA-512(DH1 || PQ.shared || transcript_hash,
                                info="kickl/v1/noise")
            send R.ephX.pub || R.ephP.pub || ct || encrypted(R.staticSig, R.staticKem) || sig

msg3 (I→R): s, se, pq2
            I encapsulates to R.ephP, sends its static material + signature over transcript
```

- Static keys are the **device** keys, not identity keys. Signatures are hybrid: `Ed25519 sig ||
  ML-DSA-65 sig` over the same transcript; both must verify.
- After handshake, `Split()` yields `k_i2r` and `k_r2i`. Each direction starts at counter 0.
- **Rekey:** after 2²⁰ messages or 24 h, run a `Noise` rekey (HKDF of current key + fresh ephemeral
  DH) without a new handshake.
- **Replay:** 64-bit sliding window per direction; duplicate or too-old counter → drop + metric.
- **Downgrade:** the suite is bound into the transcript hash; a peer offering an unknown suite is
  rejected, not negotiated down.

### 3.1 Message frame

```
offset size field
0      1    version (0x01)
1      1    suite id (0x11 = X25519+MLKEM768 / AES-256-GCM)
2      1    type (1=data, 2=ack, 3=rekey, 4=close, 5=ping)
3      1    flags (bit0 = wants-ack)
4      8    counter (big-endian)
12     4    ciphertext length
16     n    ciphertext (AEAD)
16+n   16   tag
AAD = version||suite||type||flags||counter||conversation_id||transcript_hash_prefix
```

- Explicit nonce = 96-bit big-endian counter (never random for AES-GCM).
- On counter exhaustion before rekey: hard error, close session, re-handshake. No nonce reuse, ever.
- Max frame 1 MiB; larger payloads use the blob channel with a key in the message.

## 4. Envelopes (mail, blobs, registry payloads)

HPKE base mode, single recipient:

```
recipient key = X25519 static + ML-KEM-768 static (from address/registry record)
enc           = HPKE.Seal(recipient, info="kickl/v1/envelope", aad=header, pt=payload)
wire          = CBOR {
                  v: 1,
                  suite: 0x21,
                  kid: <8-byte key id>,
                  eph: <X25519 pub 32>,
                  pq:  <ML-KEM ct 1088>,
                  n:   <12-byte AEAD nonce>,
                  aad: <header hash 32>,
                  ct:  <ciphertext>,
                  sig: <hybrid sender signature>,
                  ts:  <unix seconds>,
                  expires: <unix seconds>
                }
```

- The sender's signature covers the whole CBOR map with the `sig` field empty; it is *sealed sender*
  for mail (the recipient can verify the sender only if the signature key is included inside the
  ciphertext — a deliberate trade-off; see `docs/08` §4).
- `kid` selects the recipient key version; old keys remain valid for a grace period (default 90 days)
  so that queued mail is not lost.
- Size classes are padded to 4 KiB / 16 KiB / 64 KiB / 256 KiB to blunt size fingerprinting.
- Multi-recipient (group blob): generate a random content key, wrap it to each recipient with HPKE,
  encrypt the payload once with AES-256-GCM-SIV keyed by the content key. Never reuse the same
  (key, nonce) across recipients.

## 5. Ratchets

### 5.1 1:1 (Double Ratchet, Signal spec)

- X3DH initial agreement with a hybrid prekey bundle: signed prekey (X25519+ML-KEM), one-time
  prekeys (X25519), identity keys.
- Root/chain keys advance per message; skipped-message keys capped at 1000 and expire in 7 days.
- Header encryption on; the header key ratchets too.
- Session state is written **before** the message is acknowledged (crash-safe: replay after restart
  must not reuse a key).
- Out-of-order messages: bounded skipped-key store; beyond the cap, drop and show "message could not
  be decrypted (out of order)".

### 5.2 Groups (MLS)

- One MLS group per conversation; Welcome messages carry the group secrets sealed per member.
- Epoch rotates on: membership change, 100 messages, or 60 s of activity (matching the existing
  Pinwheel "20-second epoch" idea — we use 60 s by default because 20 s causes excessive CPU and
  message overhead on large groups; configurable 20–300 s).
- Commit ordering via the DS (delivery service = Pinwheel relay) with a monotonic epoch; a client
  that sees an epoch jump it cannot verify re-syncs from a Welcome/proposal bundle.
- The DS sees group membership churn as opaque commit blobs; it does not see identities.

### 5.3 Blob ratchet (vault)

Each blob has a chain of data keys:

```
K0 = HKDF(vaultMaster, info="kickl/v1/blob", salt=blobId)
Kn+1 = HKDF-SHA-512(Kn, info="kickl/v1/blob-ratchet", salt=blobId||counter)
```

- Opening a blob increments the counter and **re-wraps** the blob under `Kn+1` in a single atomic
  write (write temp → fsync → rename). Plaintext is unchanged; ciphertext differs.
- Trigger: every N opens (default 8) or 24 h, whichever first — not literally every open, to avoid
  SSD write amplification and to keep open latency < 250 ms.
- **Honest limit:** this is key hygiene and limits the value of an old ciphertext snapshot. It does
  not hide content you have already unlocked, and it is not PFS against an attacker who has the vault
  key *and* a plaintext copy. Say so in the help page.

## 6. Signatures, certificates, registry

- **Identity certificate** (self-signed): identity pubkeys, address, handle, creation time, device
  list hash. Published in the registry.
- **Device certificate**: `{identity, devicePub, name, notBefore, notAfter, capabilities}` signed by
  identity. Capabilities: `mail`, `chat`, `vault`, `registry-admin`.
- **Domain record** (registry): `{name, ownerIdentity, ownerKem, endpoints[], version, prevHash,
  notAfter, sig}`. Signed hybrid. The registry operator cannot forge a record, only withhold it.
- **Registry snapshot/delta signing** is implemented in `RegistrySigningKeyPair` and the offline
  `tools/Kickl.RegistrySigner`: keys are generated once (`keygen`), snapshots are canonically
  serialised and Merkle-rooted, and the same domain-separated message is signed by Ed25519 and
  ML-DSA-65. Private keys stay with the signer; the PHP API only stores the signed envelope. This
  is the first real signing path in the repo (identity/device signing is still G2).
- **Transparency log**: every record version appended as a Merkle leaf (RFC 6962 style); the head is
  signed and clients gossip the head via the update channel. A conflicting head is a **split-view
  alarm** surfaced to the user, and the client refuses to trust the newer snapshot until resolved.
- **Pinning**: the client stores the first-seen `ownerKem` per name (TOFU) plus a registry inclusion
  proof. A key change requires either (a) proof of rotation signed by the old key, or (b) explicit
  user approval with a scary interstitial and a 7-day cool-down for high-value names.
- **Certificate checking (clearnet)** is delegated to WebView2/Chromium certificate verification, with
  no bypass UI. Errors are terminal. Optional strict mode adds CT-log checks and blocks
  `http://` entirely.

## 7. Cookies (custom jar)

- Per *partition* (sealed profile, clearnet profile, per-domain isolation if enabled).
- SameSite=Lax default, `Secure` required for `jttp://` (treated as a secure context), `HttpOnly`
  honoured through the bridge, no third-party cookies by default.
- Jar is encrypted with `DEK-cookie`; values are individually encrypted so a corrupt row cannot
  expose neighbours.
- UI: per-site cookie inspector ("who set what, when, size"), one-click clear, "always clear on close"
  global toggle (default ON for clearnet).
- Cookie writes from sealed documents are mediated by the shell and capped (e.g. 50 per domain,
  4 KiB each) to prevent storage abuse.

## 8. Randomness, time, and side channels

- All randomness from OS CSPRNG; no deterministic RNG in production paths. Tests may inject a DRBG
  only under `#if DEBUG` with a compile-time guard.
- Time comes from `DateTimeOffset.UtcNow`; TLS/Noise transcripts do not depend on wall-clock for
  security. Certificate expiry uses a monotonic guard against clock rollback (store last-seen time;
  refuse if the clock moves backwards by > 24 h and warn).
- Constant-time comparisons for tags, MACs, and identifiers (`CryptographicOperations.FixedTimeEquals`).
- No secret-dependent branches in our code; primitives are library calls. Argon2 and AEAD are not
  written by us.

## 9. Zeroisation and crypto-erase

- `SecureBuffer` (pinned, `VirtualLock`, `CryptographicOperations.ZeroMemory` on dispose) for keys,
  PINs, and decrypted fragments that must persist briefly.
- Decrypted fragments are *not* held in managed `string`s where avoidable (strings are immutable and
  may linger); use `ReadOnlySpan<byte>`/`char[]` buffers and clear them.
- **Panic/wipe order matters:** destroy keys first (vault master, DEKs, wrapped copies), then delete
  files, then the WebView2 profiles, then the logs. Once keys are gone, ciphertext is noise even if
  file deletion is imperfect on SSD.
- Wipe scopes: `session` (lock + clear RAM), `chat+mail`, `vault`, `everything`.
- Honest limits: SSD wear-levelling, VSS shadow copies, OneDrive/File History backups, and OS paging
  files may retain copies. The UI states this; the app disables pagefile inclusion where it can and
  offers a "check for copies" helper that lists known backup locations.
- TPM-sealed blob: if available, wrap the vault key to the TPM so that moving the disk does not move
  the key. If the TPM is cleared, the vault is unrecoverable — warn during setup.

## 10. Limits we will state publicly

1. Plaintext exists in RAM and on screen while you read it.
2. A compromised OS, keylogger, or screen capture defeats any browser.
3. Metadata (IP, timing, size) is visible to the relay; use Tor/OHTTP if that matters.
4. Losing the recovery phrase and the PIN means permanent loss. There is no backdoor.
5. Argon2 protects a weak PIN only up to the cost of one guess; use 8+ digits or a passphrase.
6. Steganography is obfuscation, not encryption; the container's security is the AEAD, not the LSBs.
7. "Deleted" means "keys destroyed"; filesystem remnants are documented, not promised away.

## 11. Verification plan

| Layer | Test |
|-------|------|
| AEAD/KDF/KEM | NIST ACVP vectors, Wycheproof, RFC 9180 official vectors |
| Noise | Noise spec vectors + interop with a reference implementation |
| Double Ratchet | Signal test vectors + property tests (order, loss, replay, ratchet advance) |
| MLS | RFC 9420 interop harness (at least two independent implementations) |
| HPKE | All official test vectors (base mode, our suite) |
| Argon2 | RFC 9106 vectors + timing benchmark across hardware |
| Containers | Round-trip fuzz, truncation, bit-flip, wrong-PIN, torn-write simulation |
| Wipe | Boot a VM, write data, wipe, image the disk, grep for known plaintext → must be absent |
| RNG | Statistical smoke (not a substitute) + OS source audit |
| Side channels | `dudect`-style timing on PIN compare and tag compare (must be flat) |
| Protocol | Tamper/replay/downgrade/reflection tests; transcript mismatch must fail closed |

**External audit** at G2 (design + crypto core) and G14 (whole product). Budget for remediation.

## 12. Pitfall checklist (review these on every crypto PR)

- [ ] Is a nonce ever reused with the same key? (counter vs random, rekey thresholds)
- [ ] Is AAD bound to version, suite, type, counter, and conversation id?
- [ ] Are keys separated by distinct HKDF `info` labels?
- [ ] Is any key derived directly from a password without a KDF?
- [ ] Is a signature verified before any decryption or state change?
- [ ] Is the transcript hash checked at the end of the handshake (channel binding)?
- [ ] Could an attacker force a weaker suite or an older key version?
- [ ] Is state persisted *before* acknowledging, so a crash cannot reuse a ratchet key?
- [ ] Are errors indistinguishable enough to avoid an oracle?
- [ ] Are secrets zeroised on every early-return path?
- [ ] Does the test suite include a negative case for each check above?
