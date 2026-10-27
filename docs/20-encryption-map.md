# 20 — Encryption map: what is plaintext, and where

> Scope: the **slice as implemented today** (`src/Kickl.Core`), not the target design.
> Anything labelled "gap" is a real difference between the two.
>
> Visual version: [`diagrams/encryption-map.svg`](diagrams/encryption-map.svg) · updated 2026-10-26 for ownership certificates, the CA, protected blobs and games

## 1. The key chain

```
  PIN ──Argon2id (64 MiB, t=3, p=4, salt)──► KEK ──AES-256-GCM open──► master (32 B)
                                                                  │
                       ┌──────────────────────────────────────────┤
                       │                                          │
        HKDF(master, fresh salt, "kickl/v1/session")      AES-256-GCM open
                       │                                          │
                       ▼                                          ▼
                 session key (RAM)                            doc key (RAM)
                       │                                          │
        ┌──────────────┼──────────────┐                           │
        ▼              ▼              ▼                           ▼
   region keys    stego keys    keystroke cells            blob plaintext
   HKDF(...,      HKDF(...,      AES-GCM(counter,           AES-GCM(docKey)
   "regions")     seed)          AAD=session|field|slot)

Session opening also creates a fresh P-256 identity key pair; it is never persisted.
PFS lookups and mesh links use separate per-connection ephemeral P-256 pairs.
```

New keystores record the Argon2id version and parameters. Existing slice keystores using
`pbkdf2-sha256-slice` or the earlier Argon2id p=1 profile remain unlockable and are atomically
rewrapped with Argon2id p=4 after a successful unlock. If migration cannot be written, the unlocked
master remains usable and the legacy keystore remains in place. Argon2id timing, RFC vector coverage,
and performance validation still require release review.

Everything symmetric is **AES-256-GCM**. RSA-3072 is used only to wrap the AES key on the
ownership USB and to sign title statements; ECDH is used only for PFS lookup key agreement.

## 2. Three lanes

```
┌─────────────────────────────┐  ┌─────────────────────────────┐  ┌─────────────────────────────┐
│ AT REST (disk / USB)        │  │ IN MEMORY (unlocked session)│  │ ON THE WIRE                 │
├─────────────────────────────┤  ├─────────────────────────────┤  ├─────────────────────────────┤
│ keystore.json               │  │ PIN (transient)             │  │ PFS lookup                  │
│  salt, nonce                │  │ master key (32 B)           │  │  ephemeral P-256 ECDH       │
│  WrappedMaster  [ciphertext]│  │ session key + subkeys       │  │  HKDF-SHA384                │
│                             │  │ doc key (after first blob)  │  │  AES-256-GCM query/response │
│ doc.json                    │  │                             │  │  observer: id, sizes, timing│
│  SealedMarkup [ciphertext]  │  │ page text (public)          │  │  observer: NOT host/status  │
│  blobs          [ciphertext]│  │ one revealed region         │  │                             │
│  PublicWrap, DocKey [wrapped]│  │ blob text (then buffer zero)│  │ mesh link                   │
│                             │  │ keystroke chars (Reveal)    │  │  ephemeral P-256 ECDH       │
│ vault/*.png                 │  │ ownership token (Prove)     │  │  HKDF-SHA256 → 2 dir keys   │
│  AES-GCM envelope in LSB    │  │ search query + result list  │  │  AES-GCM frames + counters  │
│  public tEXt seed           │  │                             │  │  RSA signs, never transports│
│                             │  │ zeroised on Lock()          │  │                             │
│                             │  │                             │  │                             │
│ owner USB                   │  │                             │  │ clearnet http(s)            │
│  RSA private key [AES-GCM]│  │                             │  │  NOT covered by Kickl keys  │
│  WrappedKey (RSA-OAEP)      │  │                             │  │  system browser + TLS only  │
│  TokenCiphertext (AES-GCM)  │  │                             │  │                             │
└─────────────────────────────┘  └─────────────────────────────┘  └─────────────────────────────┘
```

## 3. Opening `jttp://pinwheelexplained.com`

```
host string (RAM, plaintext)
   │
   ├─► PFS: ECDH P-256 → HKDF-SHA384 → AES-256-GCM
   │        wire = ciphertext only; session invalidated after response
   │
   ├─► registry record (decrypted in RAM): status, doc file, owner, badge,
   │        title, headline, tags, updated
   │
   ├─► doc.json read
   │        SealedMarkup ................... AES-256-GCM under the public key
   │        blobs[id].Ciphertext ........... AES-256-GCM
   │        DocKey.Ciphertext .............. AES-256-GCM under master
   │
   ├─► master ──AES-GCM open──► doc key (RAM)
   │
   ├─► render markup ...................... page text on screen
   │
   ├─► click a region
   │        region key = HKDF(session, page seed, "kickl/v1/regions")
   │        AES-GCM opens ONLY that region → plaintext in RAM + on screen
   │
   └─► unlock a blob
            AES-GCM open → plaintext byte[] → UTF-8 string → buffer zeroed
            (the string itself is GC-managed and cannot be zeroed — gap)
```

## 4. Plaintext inventory

### 4.1 Plaintext in RAM, briefly

| Plaintext | Created by | Lifetime |
|---|---|---|
| PIN | user input | until KDF completes |
| Master key | `Keystore.Unlock` | until `Lock()` / process exit |
| Session key + subkeys | `SessionSecret.Create` / HKDF | until `Lock()` |
| Doc key | first `UnlockBlob` | until `Lock()` |
| Page markup / text | `DocumentStore.Load` + `Kml.Parse` | until page changes |
| One revealed region | `SealedPage.Reveal` | until page changes; kept in the page's reveal map |
| Blob text | `UnlockBlob` | byte buffer zeroed; decoded string lives until GC |
| Keystroke characters | `KeystrokeShield.Reveal` | returned `char[]`, caller wipes |
| Ownership token | `OwnershipAuthority.Prove` | inside the call, then zeroed |
| Ownership certificate token | `BrowserSession.ImportCertAsync` / `SubmitToCaAsync` | while it is checked or sent to the CA; the certificate itself is kept encrypted |
| Game memory and 240×160 picture | `WasmGame` | while the game runs; freed when it stops, the page changes or the profile locks |
| Shop and CA public keys | `SiteClaims`, `CaPins` | cached in RAM; nothing secret |
| Lookup hostname or search query | `BrowserSession.Lookup` / `Search` | duration of the call |
| Search result metadata | `Search` / renderer | query response, then GC |
| Session P-256 private key | `SessionSecret.Create` | until `Lock()`; public half is public |

### 4.2 Plaintext on disk (by design or gap)

| Plaintext | Where | Why |
|---|---|---|
| Page title, blob mime and size | `doc.json` | public metadata; the page text itself is `SealedMarkup` ciphertext |
| Legacy draft markup | `profiles/<profile-id>/drafts/` | Migrated files may be plaintext; the browser composer now builds markup in memory and does not write a draft file |
| Registry names, status, owner id, badge id, title, headline, tags | `shared/registry.json` | the phonebook and the live index are public; that is the point of the registry |
| Profile name | `profiles/<profile-id>/` directory name | Local account picker needs a stable label; profile names are not encrypted |
| Salts, nonces, session ids, versions | keystore / docs / stego PNG | metadata required to decrypt |
| Page title, blob mime and size | `doc.json` | public metadata; the page text and public blobs are ciphertext |
| **CA seal** | `doc.json` → `Seal` | public by design: it names the site, claim number, times and log position, and holds only `SHA-256(token)`, never the token. Outside the integrity hash |
| Ownership title | `shared/ownership/<site>.title.json` | owner id, badge id, `SHA-256(token)` commitment, dates |
| Pinned shop key, CA public keys, cached revocation list | `shared/ownership/` | public keys and a signed list; nothing secret |
| CA ledger | `server/keys/ca/data/ledger.jsonl` (CA server) | public: sites, claim numbers, times, log positions, seals |
| Stego seed | PNG `tEXt` chunk | not a secret: payload key = HKDF(session, seed); seed only keys the permutation |
| **Owner USB RSA private key** | `kickl-owner-<site>.key` | Argon2id-derived AES-256-GCM envelope; passphrase required to unlock |
| CLI output / redirected logs | wherever you send it | `open` prints page text by design |

### 4.3 Never plaintext at rest

- master key, session key, doc key
- blob contents (`b1`, `b2`, `manifest`)
- stego envelope JSON
- ownership token
- keystroke cells
- page markup and public blobs (`doc.json`: AES-256-GCM under the per-document public key)
- protected blobs (a separate key, wrapped by an Argon2id key derived from the site's access key)
- ownership certificates (`profiles/<id>/private/certs.kpf`)
- game save data (`profiles/<id>/private/game-saves.kpf`, at most 4 KiB per game)
- saved reading items (`profiles/<profile-id>/private/saved-items.kpf`)
- configured news feed sources (`profiles/<profile-id>/private/news-sources.kpf`)
- home ad-slot preference (`profiles/<profile-id>/private/home-ad-pref.kpf`)
- publisher records and signed tokens (`profiles/<profile-id>/ads/publishers.kpf`)
- PFS lookup and search queries/responses
- mesh frames
- the direct-mail wire (TLS 1.3, pinned to the recipient's own key; see `docs/08` §12) and the mail identity's TLS key
- FreeChat key pins, the FreeChat signing key and the stored credentials (`freechat-prefs`)
- passphrase-sealed `.kcert` copies the browser saves (Argon2id + AES-256-GCM, bound to the site name)

These profile files use purpose-separated HKDF keys derived from the unlocked master key and
AES-256-GCM with the file purpose authenticated as associated data. Legacy plaintext saved-item,
feed-source, home-slot preference, and publisher files migrate to encrypted profile files after unlock.

### 4.4 Outside the crypto boundary

None of the cryptography below protects these:

- OS swap, hibernation file, crash dumps, pagefile
- screen / GPU / compositor / screenshots / remote support
- clipboard, IME, accessibility tools, keyloggers
- IP addresses, DNS, traffic timing and sizes
- remote server memory, backups, logs
- a compromised OS, firmware, or USB port
- the TLS link to the BTC shop and to the CA: the ownership receipt, including its secret token, crosses it. Kickl keys do not protect that link
- anyone who copies the shop's original `.kcert` when it was bought **without** an owner passphrase: whoever holds it can edit the name until the claim expires. A name bought with a passphrase is locked even to a copied file (gap 12)

## 5. Known gaps (do not claim otherwise)

1. **Public content is open to every Kickl reader by design.** Markup and public blobs are AES-256-GCM under a per-document public key, wrapped under a key derived from the site name. This keeps page text out of disk, backup and file-listing reads; it is not a secret. Real secrecy comes from `access="protected"` blobs, keyed by an access key (Argon2id) that the author hands out.
2. **Blob text becomes a managed string.** The byte buffer is zeroed, but `Encoding.UTF8.GetString` creates a GC string that cannot be wiped. Production needs a pinned render buffer.
3. **Trusted-device ceremony is not integrated into hardware.** The software encrypts owner keys with an Argon2id-derived AES-GCM key and supports RSA-wrapped AES-GCM proof packets, but the CLI prompt runs on its own host. A physical first-device prompt, receiver dongle storage/pairing, radio transport, replay management, and legacy plaintext-key migration remain unfinished.
4. **No memory locking.** Keys can be swapped or included in a crash dump; `mlock`/`VirtualLock` is not used.
5. **Region keys are session-long.** No per-reveal rekey; one session key protects every region of a page.
6. **PFS hides names, not traffic shape.** The server sees query/response lengths, session id, and timing.
7. **The live index is publish-time, not crawl-time.** `updated` changes when a site is published; there is no scheduled re-fetch yet.
8. **Stego is not the security boundary.** The AES-GCM payload is. The PNG can be copied; without the session it will not open.
9. **On-screen text is plaintext.** The renderer must display it; screen capture and shoulder-surfing are out of scope.
10. **Clearnet is a different lane.** `http(s)://` is handed to the system browser and gets none of this.
11. **Profile protection is incomplete.** Each local profile has its own key and directory, but profile names, published site markup, legacy drafts, and some profile-owned data remain plaintext. Published markup is the site's public payload; the shared public registry is intentionally outside profile encryption. Recovery, cloud sync, and complete at-rest coverage remain unfinished.

12. **The receipt token travels over TLS only, and a plain certificate is a bearer credential.** The shop hands the buyer a certificate containing a 32-byte secret token, and the owner sends it to the CA to be sealed. Both hops use ordinary HTTPS, not Kickl keys. The CA stores only `SHA-256(token)` and the seal is public, but a party that could read the TLS stream could copy the token. **Mitigation (owner binding, `docs/23`):** if the buyer types an owner passphrase on the shop page, the shop signs an HMAC of it into the certificate and the CA seals that; from then on the file alone cannot edit the name, a stripped or swapped binding breaks the shop's signature, and sealing first gains nothing. The passphrase is never sent to the shop or CA, only the HMAC. Names bought without a passphrase stay bearer files, and an unbound name can still be bound at seal time by whoever seals first. The token itself still crosses TLS; proving possession to the CA without sending it is not done.
13. **A seal does not identify the buyer.** It proves the CA saw a genuine shop receipt, found no clash, and logged it. It does not prove who paid.
14. **The issuing key is online.** The root private key is offline and its public half is built into the browser, but the issuing key that signs seals lives on the CA server. A 90-day certificate bounds the damage and the root can revoke it. Visitors check a seal against the log's inclusion proof, not the log's consistency over time; an auditor re-checks that.
16. **FreeChat trusts its server for membership.** The server relays each member's public key and the wrapped group secret. Key pinning and signed messages (`docs/08` §12.1) catch a swap of a *known* member's key and any forged or replayed message from a signing member, but a member first seen after a malicious swap is trusted on first use, sender names on unsigned web-client messages are the server's word, and the roll tag only proves the server agrees with itself.
17. **Direct mail reveals that a Kickl mail device exists at an address and port,** and when connections happen. The contents and envelope metadata are inside TLS 1.3 pinned to the recipient's key; the registry also publishes the endpoint, by design.
15. **Games are sandboxed, not trusted.** A game has no imports, a fuel budget per call and a memory cap, so it cannot reach the network, files or clock and cannot freeze the browser. It can still draw anything on its picture, including misleading text, and a heavy game makes the page feel slow.

> Rule: if a byte is not in the green/blue lanes above, it is not protected by Kickl.
