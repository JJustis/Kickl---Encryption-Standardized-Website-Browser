# 18 — Sealed Pages, the Steganographic Cache, and Polymorphic Re-seal

## 1. The idea

A sealed page is not "a page with a login". The readable text itself is a set of encrypted
regions. The page opens as placeholders; clicking a region decrypts only that region. The cache
stores the page encrypted inside an image, and every cache request re-encrypts it with a new seed
so the stored bytes never stabilise.

```
open page        → region ciphertext in memory
click region 3   → AES-256-GCM open of region 3 only
leave / lock     → plaintext dropped, placeholders return
cache write      → envelope encrypted, embedded in PNG LSBs
cache read       → envelope extracted, then re-sealed with a new seed
```

## 2. Region keys

```
pageSeed   = 32 random bytes per open
regionKey  = HKDF-SHA-256(sessionKey, salt=pageSeed, info="kickl/v1/regions")
ct_i       = AES-256-GCM(regionKey, nonce_i, text_i,
                         AAD="kickl/v1/region|site|i|kind|level")
```

- One key per page open, one random nonce per region.
- The AAD binds the region to its slot and type; swapped regions fail to open.
- Only headings, paragraphs, code, and quotes are regions. Images/files are already blobs, and
  buttons are native actions.
- A revealed region is held for the current view only. The production compositor keeps it in a
  pinned buffer and clears it after the frame; the slice keeps it in a `SealedPage` object that is
  disposed on lock/navigation.
- This is **screen privacy**, not a claim that the compositor or panel never sees pixels. It limits
  what is on screen at once, which matters for shoulder surfing and casual recording.

## 3. Cache shape

```
profile/vault/stego/<opaque-id>.png
profile/vault/cache.index        (encrypted; site → carrier, not yet implemented)
```

The carrier is a generated abstract "page card" in the slice; the shell will render a real
thumbnail. The envelope:

```json
{ "V": 1, "Site": "...", "Path": "/", "Title": "...", "Content": "...", "CreatedUtc": "..." }
```

The envelope is serialised, sealed with AES-256-GCM, and embedded with keyed LSB steganography.

## 4. Why PNG and not JPG

JPEG is lossy. Its quantisation changes pixel values after you write them, so LSB bits do not
survive a save/load cycle. A stego JPEG needs DCT-coefficient embedding and error-correcting
coding; that is a separate research-grade feature. The slice writes **lossless PNG** and labels it
as such. Calling a PNG "JPG" would be a lie, so the file extension stays `.png`.

If a JPEG carrier is required later:

- embed in quantised DCT coefficients with a keyed coefficient order;
- use redundancy/ECC and verify after recompression;
- keep the payload small (a few KB) and test against the exact encoder used on device.

## 5. Keyed LSB embedding

- Seed: 32 random bytes per seal, stored in a PNG `tEXt` chunk (`kickl-seed`).
- Permutation key: `HKDF-SHA-256(sessionKey, seed, "kickl/v1/stego-permutation")`.
- Payload key: `HKDF-SHA-256(sessionKey, seed, "kickl/v1/stego-payload")`.
- The permutation is a keyed Fisher–Yates shuffle driven by HMAC-SHA-256; bit *i* goes into the
  LSB of RGB byte `order[i]`.
- Header: `KBST` + version + length + SHA-256(payload)[0..16]; a corrupted carrier fails cleanly.
- Without the session secret, the permutation is wrong, the magic is absent, and the carrier is
  just an image.
- A site name is not in the filename or the PNG metadata; it is inside the encrypted envelope.

## 6. Polymorphic re-seal

Every seal:

```
new seed (32 B) → new payload key → new GCM nonce → new ciphertext
new carrier pattern → new permutation → new PNG bytes
```

The plaintext envelope is unchanged. This satisfies "the ciphertext changes every time but the
site is the same". It also means:

- no nonce reuse, because the key changes with the seed;
- no stable ciphertext fingerprint on disk;
- the stego image changes on every cache request, so a copied image is stale;
- the old image cannot be decrypted with the new seed; only the live session can open the current
  one.

`Load(refresh: true)` is the default: read the envelope, then immediately re-seal the same file.
The demo shows the file hash changing while the extracted content stays identical.

## 7. What "only the user in session knows" means

- The cache keys are derived from the session secret, which exists only after the user key unlocks
  the profile.
- Locking zeroises the session secret; the carrier becomes unreadable until the next unlock.
- A different identity on the same machine derives a different session secret and cannot read it.
- This is not protection from a compromised OS while the user is unlocked: at that point the
  session key is in memory and the compositor has the plaintext. The doc and the UI say so.

## 8. Limits

| Claim | Reality |
|-------|---------|
| "Absolutely secret query" | the registry must see the name to resolve it; PFS hides it from the path and logs, not from the resolver |
| "Only the session knows" | true at rest; false against a live kernel compromise |
| "JPG stego" | not built; lossless PNG only, JPEG DCT deferred |
| "Encrypted on screen" | per-region reveal; pixels still exist for the composited frame |
| "Impossible to fingerprint" | LSB statistics can reveal that a carrier holds data; stego is concealment, not a proof |

## 9. Tests in the slice

- region ciphertext does not contain its plaintext; swapped region fails;
- reveal opens exactly one region; dispose destroys the page key;
- PNG round-trip preserves RGB and text chunks;
- embed/extract with the right key; wrong key fails;
- cache store/read; site name and content absent from carrier bytes;
- two seals of the same envelope produce different bytes and the same plaintext;
- wrong session cannot read the carrier;
- lock blocks further access.

## 10. Roadmap

- render real page thumbnails as carriers (UI);
- encrypted cache index with opaque names;
- per-identity cache isolation and wipe on identity switch;
- JPEG/DCT carrier only if a device constraint demands it;
- optional "panic blur" that re-seals every revealed region immediately;
- audit the LSB implementation and add a statistical test that the carrier does not stand out more
  than necessary.
