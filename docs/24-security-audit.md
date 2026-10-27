# 24 — Security audit (first pass, 2026-10-26; findings 1–5 fixed 2026-10-27)

> **Status: partial.** Findings 1–5 below were fixed on 2026-10-27 (see *Resolutions* below). Findings 6–9 are open.
>
> This pass covered what the application writes to disk and how key material is stored.
> It did **not** cover the whole codebase. The list of what is still unaudited is at the end; do not read the absence
> of a finding there as "checked and fine".

## Findings so far

Severity is my judgement of impact × likelihood for this project as it is today.

| # | Severity | Finding | Where | Fix / suggested fix |
|---|---|---|---|---|
| 1 | **High** (CA) | The CA's **issuing private key is a plaintext file** (`ed25519.seed`, `mldsa65.pkcs8`) with no access restriction. The web server must be able to read it to countersign, so anyone who can run code as the web-server user, or read that folder, can forge seals until the key expires or is revoked. | `HybridSigner.SaveTo`, `server/keys/ca/issuer` | Keep the key readable only by a dedicated account that runs the CA tool; on Windows protect it with DPAPI (`ProtectedData`, machine scope); run countersigning as a small separate service instead of from PHP; keep the 90-day life short and revoke on any suspicion. |
| 2 | **High** (shop) | The shop's **signing key** (`btcshop-private/claim-signing.pem`) is an unencrypted PEM readable by Apache. A web-app bug in the shop exposes the key that signs every ownership receipt. | `btcshop/config.php` (outside the repo) | Same as above; also rotate it if the shop site is ever compromised. The CA only seals receipts from keys listed in `shop-keys.txt`, so a rotation is a deliberate change there. |
| 3 | **Medium** | **Owner-key file restriction is skipped on Windows.** `RestrictToOwner` returns immediately on Windows, so the owner key envelope and any other file it is meant to protect rely only on the user profile's default permissions. (The envelope itself is Argon2id + AES-256-GCM, so this is defence in depth.) | `Ownership.cs` `RestrictToOwner` | Set an owner-only ACL on Windows (`FileSecurity`) for key files, and apply the same helper to every key file written. |
| 4 | **Medium** | **First-contact trust for the shop key.** The browser pins the shop's signing key the first time it meets it. An attacker in the path at that moment pins their own key and can hand out certificates this browser accepts. | `SiteClaims.PinShopKey` | Ship the shop key inside the build, like the CA root, so there is nothing to learn at first contact. |
| 5 | **Medium** | **`crash.log` is plaintext and unbounded.** It stores `exception.ToString()`, which can contain host names, paths and message text, and it is never rotated or encrypted. | `Kickl.Browser/CrashLog.cs` | Cap its size, scrub messages, and when a profile is unlocked write it through the profile cipher (or keep only a code and a stack, not the message). |
| 6 | **Low–Medium** | **`mail.db` is a plain SQLite file.** Message bodies and the identity are encrypted per record, but the database structure, row counts and ordering are visible to anyone who can read the file. | `MailStore.cs`, `Microsoft.Data.Sqlite` | Move to SQLCipher, or accept and document the metadata exposure. |
| 7 | **Low** | The **ownership receipt's secret token crosses TLS only** (shop → buyer → CA). The CA keeps only `SHA-256(token)`, but a party who can read the TLS stream could copy the token. A `.kcert` file is a bearer credential. | by design; `docs/20` gap 12 | Offer an optional passphrase seal for the saved certificate; consider proving possession of the token to the CA without sending it. |
| 8 | **Low** | **Key destruction is best-effort.** Old issuing keys are overwritten and deleted, which does not guarantee erasure on journaling or SSD storage. | `CaOps.DestroyKeys` | Keep issuing keys short-lived (already 90 days) and prefer DPAPI-wrapped keys so a recovered file is useless. |
| 9 | **Info** | The CA revocation list is fetched **from the CA itself**; a visitor who cannot reach it keeps the last list seen. | by design; `docs/23` §9 | Carry the list number in the registry snapshot so a stale list is detectable. |

## Resolutions (2026-10-27)

| # | Status | What was done | Risk that remains |
|---|---|---|---|
| 1 | **Fixed** | The CA issuing key is wrapped with Windows DPAPI (machine scope) and its folder is restricted to the owner, SYSTEM and Administrators. Existing keys are wrapped automatically by `maintain` (or `kickl-ca protect-keys`); the plaintext is only destroyed after the wrapped copy is proven to give the same key. The live CA was converted and still signs. The offline root deliberately stays portable. | DPAPI at machine scope stops a copied, backed-up or stolen-disk key file, but **any code running on this computer can still ask Windows to unwrap it**, including a compromised web server. Full separation needs the CA to run under its own account. The 90-day life and root revocation bound the damage. |
| 2 | **Fixed** | The shop's signing key left PHP entirely. It is now held by the CA tool in a DPAPI-wrapped, owner-only store (`shop-sign`, `shop-pubkey`); the shop's PHP only asks for signatures. The existing key was imported, so the public key (`0da62146…`) and every signature are unchanged, and the plaintext PEM was destroyed. | Same DPAPI limit as above. Signing now spawns a process, which is fine at claim volumes. |
| 3 | **Fixed** | One helper, `FileProtection`, locks key files and folders to the owner on Windows (ACLs) and Unix (0600/0700). It is applied to the keystore, relay key, owner keys, CA keys and the shop key store. It reports a clear error if the account may not change a folder's permissions, and quietly skips media with no permissions (FAT). | A folder created with elevated rights (such as one under `C:\xampp`) cannot be locked down by a normal account, so keep keys in your own folders. |
| 4 | **Downgraded to Low** | Let's Encrypt on `secupgrade.com` covers a network attacker at first contact. The browser now learns the shop key only over **https from secupgrade.com**, with redirects refused. An optional built-in key list (`Trust/shop-keys.json`, shipped empty) can pin the shop key into the build instead, and supports rotation by listing old and new keys. | If the web server itself were compromised, an attacker could serve their own key to a *new* install. Filling the built-in list closes that. |
| 5 | **Fixed** | `crash.log` is scrubbed (addresses, file paths and account names, key-like strings, emails and identifiers are removed), each entry is capped, the file is bounded at 256 KiB with one rotated copy, and it is locked to its owner. | It is still plaintext, but it no longer names the sites you visit or contains key material. |

Also changed in this round, at your request: **`https://secupgrade.com/secca/` is the only CA.** In a shipped build the CA address is a
constant; http, other hosts, other ports and redirects are refused; the root is only the one built into the browser. The test-CA, test-shop and
test-root settings exist only in Debug builds.

## Things the audit confirmed are done well
- Profile files (reading list, feed sources, ad preference, ownership certificates, game saves) use
  purpose-separated keys and AES-256-GCM with the purpose as associated data (checked in `ProfileDataCipher` and its callers).
  The reading list, feed sources and ad preference writes in `BrowserWindow.cs` go through it before they touch disk.
- Each mail message and the mail identity are encrypted per record under the mail vault key.
- The owner key envelope, the relay key and the keystore are wrapped, not plaintext.
- No private key, token or `.env` is tracked in Git; `server/keys/` and `.kickl-*/` are ignored.
- Every CA signature is hybrid (Ed25519 + ML-DSA-65) and domain-separated.

## Round 2 (2026-10-27): the untrusted-input surface

This round worked down the list of what had not been audited. Every finding below was **fixed and has regression tests**.

| # | Severity | Finding | Fix |
|---|---|---|---|
| 10 | **High** | **A 1.4 KB SVG freezes the renderer.** Inline SVG allows `<use>`, `id` and `href="#id"`, so references multiply: 10^6 draws from 1.6 KB hung `Avalonia.Svg.Skia` for over 20 s, growing 10× per nesting level. The node budget did not help, because the cost multiplies through references. Any sealed page, ad or feed card could hang the browser. Measured, not guessed. | The sanitizer now computes the true expanded drawing cost (children + everything referenced by `href` or `url(#id)`) and rejects anything over the plain node budget (8,192), and rejects reference cycles and chains deeper than 128. Definitions (`defs`, gradients, `symbol`…) cost nothing until referenced. |
| 11 | **High** | **Sealed SVG blobs bypassed the sanitizer entirely.** An `image/svg+xml` blob went straight to the renderer as raw bytes, and a hostile site's package chooses its own blob types. It could ship the freeze above (or anything the renderer supports) as an "image". | `InlineHtml.SanitizeSvgFile` runs SVG files through the same sanitizer and limits; the viewer uses it. |
| 12 | **Medium** | **A registry record could steer the browser to any file on disk.** `DocFile` (registry data) was joined with `Path.Combine`, which drops the data folder for an absolute path and follows `..\`. The browser would read that file, and the seal/key upgrade could rewrite it. | `SafeNames.ResolveUnder` accepts only a relative path that stays inside the data folder (no rooted paths, `..`, drive letters, UNC, NTFS streams, control characters). |
| 13 | **Low** | Path helpers (`SiteDoc`, owner-key and title files) trusted a host name that callers usually, but not always, validated. | `SafeNames.RequireHost` at every such helper. |
| 14 | **Medium** | **Most network reads had no size cap** (.NET's default is 2 GB): registry snapshots, chat and mail replies, news feeds (a chunked reply carries no `Content-Length`, so the header check alone proved nothing), CA and shop replies. A hostile or compromised server could exhaust memory. | `HttpLimits` ceilings on every client: CA/shop 1 MiB, registry 32 MiB, chat and mail 8 MiB, feeds 2 MiB. |
| 15 | **Medium** | **Site packages and tunnel payloads were unbounded.** `DocumentStore` read a whole `doc.json` with no ceiling; the direct tunnel decrypted and parsed any size; a registry snapshot accepted any record count. | 64 MiB per package (512 blobs, 32 MiB each, 1 MiB of page text); tunnel replies capped before decryption, requests at 64 KiB; snapshots capped at 32 MiB, 200,000 records and 253-character names. |
| 16 | **Low** | `filter`, `mask` and `clip-path` accepted any value without `javascript:`, so an external `url(https://…)` reference would pass. | They now go through the same in-document-only check as `fill` and `stroke`. |
| 17 | **Low** | The mail vault's PBKDF2 iteration count is read from `mail.db`, so a tampered file could set it huge and hang the unlock. | Refused outside 100,000–5,000,000. |
| 18 | **Low–Medium** | PHP `display_errors = On` (the XAMPP default) printed warnings, with file paths, into API responses. | Off for the shop and the CA site (log instead). Turn it off globally in `php.ini` too. |
| 9 | **Low** | `OpenExternal` handed any string to the Windows shell. | Only `http(s)` addresses with a host. |

## Checked and found sound
- **XML:** all three parsers (KML, RSS template, news feed) refuse DTDs and external resolvers.
- **TLS:** certificate validation is never disabled anywhere.
- **Crypto primitives:** no MD5, SHA-1, DES, RC4 or CBC/ECB; no non-cryptographic randomness where it matters; Argon2id for PINs, PBKDF2 at 600,000 for the mail vault; the AEAD wrapper uses random 96-bit nonces, checks key and nonce sizes, and delegates tag checking to the platform; 17 secret comparisons are constant-time and the rest compare public values.
- **PHP:** SQL is prepared or constant (the one `quote()` is correct); the admin-token comparison is constant-time; `download.php` serves only catalog products behind a paid order, with `realpath` containment.
- **Dependencies:** no known-vulnerable package in any project, transitive ones included (including Wasmtime).
- **WebAssembly sandbox:** 1,000+ corrupted, truncated, padded and random modules, and two crafted "enormous claim" modules, produced only clean refusals or contained halts, never another exception type, a crash or a hang.

## Round 3 (2026-10-27): the remaining list

**Fixed**
- **Mesh link** (`MeshLink`): `Accept` can now be told which device it expects (`expectedPeerId`), because a valid signature only proves the sender holds *some* key; the session keys are bound to the whole transcript (both ids, both ephemeral keys, the nonce); only P-256 handshake keys are accepted and a bad or foreign-curve key is a clean refusal; a frame whose nonce is not the counter's is refused; the send counter cannot wrap into nonce reuse; a bad tag is a `MeshException`. The mesh is used by tests and the lab, not wired into the shipped UI, so callers must pass `expectedPeerId`.
- **PNG carrier / stego** (`PngCarrier.Read`): a decompression bomb. A few kilobytes of compressed zeros, or a header claiming a 60,000 x 60,000 canvas (or a width/height that overflows), could exhaust memory. Canvas is now capped at 16 M pixels, inflation stops at the size the header implies, and text metadata is capped.
- **`.kcert` parser**: `"site": null` and similar threw a `NullReferenceException` (found by the new fuzzer); the same hardening is in `SealedCert`.
- **Rate limiting**: the registry API now limits every client (600 requests a minute overall; snapshot 20/min, delta 60/min, mail registration 10/hour, mail lookup 120 per 10 min; 429 + Retry-After) by `REMOTE_ADDR` only. The BTC shop limits overall traffic, secure-ID minting (20/hour), orders (20/hour) and payment checks (60 per 10 min). The shop's `api.php` lives only on the server, not in this repository.
- **Device emulator** starts the .NET host by its install path (`DOTNET_ROOT`, Program Files, `/usr/share/dotnet`…), not by the bare name `dotnet`, so a planted `dotnet.exe` on the search path is not run.
- **FreeChat**: the server address must be https (the PIN and cookie go there); the member list and per-poll message count are capped; ML-KEM shared secrets are zeroed after use; members' key fingerprints can be shown for out-of-band checking (`FreeChatClient.KeyFingerprint`).
- **Parser fuzz** (`ParserFuzzTests`): 2,000 mutated and random inputs each through the KML, RSS, registry head/delta/snapshot, `.kcert`, sealed `.kcert`, mail bundle and envelope parsers. After the `.kcert` fix, they refuse with a normal exception and finish quickly.
- **`mail.db` metadata**: checked and fine. Every message record (peer, handle, time, envelope) is sealed with the vault key; the database shows only a random message id and sizes. The "plain SQLite metadata" worry in round 2 was wrong.

**Not fixable here, and what that means**
- **FreeChat trusts its server for membership.** The server relays each member's ML-KEM public key and the wrapped group secret, so a *malicious* server (not a passive one) can substitute its own key and read the room; and sender names are asserted by the server, not signed. Closing that needs a protocol change on the existing service (members signing their keys and messages). Until then, compare `KeyFingerprint` codes out of band for anything sensitive. The roll tag only proves the server agrees with itself.
- **The shop's original `.kcert` download is a bearer credential** (browser-made copies are now passphrase-sealed). Proving possession of the token to the CA without sending it is a CA-protocol change and is not done.
- **Memory handling:** managed strings (typed text, blob text, passwords) cannot be wiped. Some byte-array secrets are now zeroed sooner; the rest is a limit of the runtime.
- **DPAPI** protects against other users and offline copies, not against malware running as the same user.
- **An independent penetration test** is still the one thing only a person other than the author can supply.


## Round 4 (2026-10-27)
- **Owner binding** (see `docs/23`): a `.kcert` alone no longer edits a name once the owner sealed it with a passphrase; the CA signs only an HMAC of it. This closes finding 7 for names sealed that way.
- **FreeChat key pinning:** the first ML-KEM key seen per username is pinned in the encrypted profile; a different key later is shown as KEY CHANGED and is given no group secret until accepted. Member fingerprints are shown in the room list. This stops a server from silently swapping a *known* member's key. It does not help for a member first seen after the swap, and does not authenticate message senders; that needs members to sign their keys and messages, which the existing service and web client do not do.

## Round 5 (2026-10-27): FreeChat sender authentication
Each member now has an ML-DSA-65 signing key (kept in the encrypted profile). Every message carries a signed trailer appended after the PNG's end marker, where PNG readers ignore it, so the existing service and web client are unchanged. The signature covers the room, epoch, message position, sender id and name, the image bytes, and a hash of the sender's own ML-KEM room key. A receiver pins each member's signing key on first sight (like the room key) and labels every message: signed, signed with a new key, signing key changed, forged or wrong sender, or unsigned.

What this closes: the server (or another member) can no longer put words in a signing member's mouth, replay a signed message in another place, or swap a member's room key without it showing: a message signed for one room key but listed under another is flagged as forged. What it does not: a member first seen after a key swap is still trusted on first use (compare fingerprints for that), unsigned messages from the web client are still accepted and labelled "unsigned", and the signing key is only as safe as the profile.

Owner binding can now also be set on the CA's own web page (WebCrypto PBKDF2, 600,000 rounds, tagged in the seal). Still true: the first seal decides, and a copy of the file with its seal removed stays unbound until the browser next reaches the CA.

## Round 6 (2026-10-27): owner binding signed by the shop
The two remaining owner-binding gaps are closed for names bought with a passphrase: the shop signs the binding into the receipt, so there is no "first seal decides", and a copy with its seal stripped is still locked because the binding is in the shop-signed part. Still true: a name bought **without** a passphrase is a bearer file, exactly as before; and the binding protects editing, not reading the receipt file itself.

## Round 7 (2026-10-27): sharing games between computers
New surface, reviewed as it was built (`docs/28-game-sharing.md` §6): (1) **cards** are signed with ML-DSA-65 and verified by every client; the registry cannot forge one. (2) A downloaded game is accepted
only if its length and SHA-256 match the signed card, it loads in the sandbox with no imports, and it fits the limits; nothing is written before that. (3) Downloads use TLS 1.3 pinned to the key in the source's
address, no proxy, no redirects, at most 8 sources, and read no more than the card's size, so a hostile source or a hostile list cannot make the client buffer or trust anything. (4) The serving listener answers one
fixed request for games the computer holds, with the mail listener's connection caps and deadlines. (5) The registry validates structure, size, the publisher-id-to-key binding and token ownership, and rate limits
every endpoint. **Found while testing:** a metadata size limit that silently dropped a downloaded game's card, and a same-second cursor bug that would have missed updates (both fixed, with tests).
**Open:** a game can be published under a misleading title (cards show the publisher fingerprint); there is no reporting or review beyond operator removal; a source address can point a client at an arbitrary
host (the client makes one pinned request and checks the content); and an operator-controlled registry decides which cards you see, though not what any game contains.
