# 21 — PFS everywhere, temporal keys, and the live index

This document captures the rules agreed after the encryption map (`docs/20`):

1. Every Kickl-to-Kickl connection uses fresh ECDH keys and AES-256-GCM.
2. Keys are **temporal**: they exist for one opening or one connection, then die.
3. The search index returns `title`, `headline`, `timestamp`, and `domain` for each live site.
4. Every searchable site must declare tags. No tags, no phonebook entry.
5. Clicking a result never navigates silently. The device asks first.
6. The top screen is a touch RSS surface with a stylus-driven mini search.

## 1. What "PFS" covers

| Link | Key agreement | Forward secrecy | Status |
|---|---|---|---|
| Registry name lookup | fresh P-256 ECDH per lookup → HKDF-SHA384 → AES-256-GCM | yes | implemented |
| Live-index search | fresh P-256 ECDH per search → HKDF-SHA384 → AES-256-GCM | yes | implemented (slice) |
| Device mesh link | fresh P-256 ECDH per link → HKDF-SHA256 (salted with the whole handshake transcript) → two AES-256-GCM keys; RSA-3072 signs the offer and accept | yes | implemented, hardened 2026-10-27 |
| Direct mail delivery | TLS 1.3, ECDSA P-256 device key pinned in the signed endpoint (`#pin=`) | yes (TLS 1.3 ephemeral) | implemented 2026-10-27 |
| FreeChat | ML-KEM-1024 wraps a group secret; per-epoch HKDF keys; ML-DSA-65 message signatures; TOFU key pins | no (group secret is long-lived per room) | implemented (client side) |
| Mail / chat ratchet | HPKE + ratchet (production) | yes, by ratchet | spec only |
| Clearnet `http(s)` | TLS to the remote site, handled by the system browser | not ours | out of scope |

**RSA does not transport the link key any more.** The earlier mesh design wrapped an AES key with
RSA-OAEP. That is encrypted, but it is not forward-secret: whoever later obtains the device's RSA
private key can decrypt recorded links. The mesh now does ephemeral ECDH and uses RSA only to sign
the offer/accept, binding the one-time keys to the pinned device identity.

**Mesh hardening (2026-10-27).** `Accept` takes the id of the device it expects (`expectedPeerId`): a valid
signature only proves the sender holds *some* key, and the id is what ties it to a device this one agreed to
talk to. Callers must pass it. The link keys are salted with a hash of both device ids, both ephemeral keys and
the nonce, only P-256 handshake keys are accepted (another curve or garbage is a clean refusal), a frame whose
nonce is not the counter's is refused, and the send counter cannot wrap into nonce reuse.

The same rule applies to the registry server: its record-signing key may be long-lived, but every
lookup/search session gets a fresh P-256 key pair and the session key is destroyed on return.

## 2. Temporal key model

```
┌──────────────────────────────────────────────────────────────────────┐
│ STABLE (survives restarts)                                          │
│  • ownership RSA-3072 key on the USB   → proves the title           │
│  • device authentication RSA-3072 key  → pins the mesh peer         │
│  • profile master key (wrapped by PIN) → unwraps document keys      │
├──────────────────────────────────────────────────────────────────────┤
│ PER BROWSER OPENING                                                 │
│  • session secret  = HKDF(master, fresh 32-byte salt)               │
│  • session P-256 identity key pair                                  │
│  • session subkeys (region, stego, keystroke)                       │
├──────────────────────────────────────────────────────────────────────┤
│ PER CONNECTION / PER LOOKUP                                         │
│  • ephemeral P-256 key pair (client and server)                     │
│  • HKDF output → AES-256-GCM key(s)                                 │
│  • nonces and counters                                              │
└──────────────────────────────────────────────────────────────────────┘
```

All three lower layers are destroyed on `Lock()` / link close. Nothing in the per-opening or
per-connection layers is written to disk.

**Do not rotate the ownership key per opening.** It is the physical proof of the title. Rotating it
would orphan every badge and make transfers impossible. The per-opening key is an additional
session identity, not a replacement for the ownership key.

## 3. Live index at `secupgrade.com/legenddxI2`

`https://secupgrade.com/legenddxI2` is the bootstrap address. It is not a plaintext query API: the
HTTP connection establishes the pinned registry service, and the actual lookup/search travels
inside a PFS session as AES-256-GCM frames.

Each result carries exactly:

| Field | Meaning | Source |
|---|---|---|
| `host` | the `jttp://` name (domain) | registry name |
| `title` | page title | `<title>` in KML |
| `headline` | one-line summary | `<meta name="headline">` |
| `updated` | Unix seconds of the last publish | registry `UpdatedUtc` |
| `tags` | required search tags | `<meta name="tags">` |
| `badge` | ownership badge, or empty | ownership registry |
| `sha256` | SHA-256 of the delivered package (markup + sealed blobs) | publisher, stored on the record |
| `version` | monotonic record version | registry |

Request/response shape (inside the sealed PFS frame):

```json
{ "kind": "search", "value": "privacy" }
```

```json
{
  "query": "privacy",
  "results": [
    {
      "host": "pinwheelexplained.com",
      "title": "Pinwheel, explained",
      "headline": "A sealed site on Kickl's own network.",
      "updatedUtc": 1790000000,
      "tags": ["kickl", "privacy", "sealed-pages"],
      "badgeId": "kb1b…",
      "sha256": "9f2c…",
      "version": 4
    }
  ]
}
```

Rules:

- Only `active` names appear. Closed names are invisible to search.
- A tag match ranks above a substring match; then newest `updated`, then name.
- Maximum 50 results per response.
- The query string is inside the ciphertext. The observer sees frame sizes and timing only.
- Results are metadata, never page content. Opening a result is a separate, user-confirmed action.

### Required site metadata

Every publish must contain:

```xml
<head>
  <title>Pinwheel, explained</title>
  <meta name="headline" content="A sealed site on Kickl's own network."/>
  <meta name="tags" content="kickl, privacy, sealed-pages"/>
</head>
```

- `headline`: non-empty, one line, recommended ≤ 140 characters.
- `tags`: 1–8 tags, each 2–24 characters, lowercase `a–z`, `0–9`, `-`.
- Duplicate tags collapse case-insensitively.
- `updated` is set by the registry on publish, never by the page.
- Publishing without a headline or without tags is rejected (`KmlException`).

This is the phonebook contract: a name that cannot be described cannot be found.

### Content commitment and the attack counter

At publish, the client sends a SHA-256 over the delivered package: the canonical site name, the KML
markup, and every sealed blob ciphertext (sorted by blob id). The registry stores it on the record and
returns it in every lookup/search result.

On arrival, **before parsing or rendering**, the receiver recomputes the same hash and compares it in
constant time:

1. match → continue to parse/render;
2. mismatch or malformed hash → increment `contentMismatchCount` on the record, set
   `lastMismatchUtc`, and fail closed. Nothing is rendered. The exception names the host and the new
   count.

The counter is the attack number: one increment per failed arrival. The slice increments the local
registry mirror; production also queues an anonymous, rate-limited `POST /registry/mismatch` so the
central Pinwheel DB sees the same increment. No device id, IP, or page content is part of the report.

This proves **transport integrity**, not authorship. A malicious registry can serve a different
package and a matching hash; only the ownership signature and the transparency log address that.

## 4. Clicking a result prompts the device

Search results are inert. Activating one raises a native confirmation sheet that shows:

```
jttp://pinwheelexplained.com/
Pinwheel, explained
A sealed site on Kickl's own network.
tags: kickl, privacy, sealed-pages
owner: kb199c… (badge active)
[ Open ]   [ Cancel ]
```

Only **Open** calls `BrowserSession.Open`. There is no auto-navigation, no hover-prefetch, no
background fetch, and no click telemetry. This also blocks a malicious result from opening itself.
The package is checked against the registry SHA-256 before the first parser call; a mismatch aborts
the open and increments the attack counter (§3.1).

## 5. Top screen: RSS + stylus mini search

The handheld has two screens (see the hardware notes):

- **Top screen** — always-on, touch-active, portrait. Shows only public RSS headlines in a slow
  vertical scroller. No session keys, no mailbox, no ads, no page content.
- **Inner screen** — the browser, social pane, and the Nickles ad slot.

Top-screen behaviour:

1. RSS items scroll continuously; tap pauses, tap again resumes.
2. A mini search field is hidden at the top edge. A stylus tap (or a short downward swipe) pops it
   over the feed.
3. Typing goes through `KeystrokeShield`: each character is an AES-256-GCM cell under the live
   session key. No pointer path, hover, or dwell time is recorded.
4. Submitting runs the same PFS live-index search as the main browser, but shows a compact result
   strip on the top screen. Selecting a result hands off to the inner screen and the confirmation
   prompt in §4.
5. If the device is locked, the mini search still accepts text into a local shield, but results
   require an unlock; RSS remains readable because it is public.

The top screen never shows decrypted blobs, mail, chat, or the ownership token.

## 6. Slice status

| Item | Status |
|---|---|
| PFS name lookup | done, tested |
| PFS live-index search | done, tested |
| Required tags + headline on publish | done, tested |
| Content SHA-256 published + verified before parse/render | done, tested |
| Wrong hash fails closed and increments the attack counter | done, tested |
| Anonymous `POST /registry/mismatch` reporting | spec only; slice increments the local mirror |
| Mesh ephemeral ECDH + RSA-signed offers, transcript-bound keys, pinned peer id | done, tested |
| Direct mail over pinned TLS 1.3 | done, tested |
| FreeChat key pins + signed messages | done, tested |
| Per-opening session P-256 identity | done, tested |
| `/legenddxI2` HTTP deployment | spec only; slice uses the local registry mirror |
| PHP/MariaDB registry + signed snapshot/delta sync | done locally (head, snapshot, delta, key, operator register; `kickl registry sync`) |
| Head-signature verification, delta consistency proofs, in-app sync | **not done**; snapshot/delta signatures and Merkle roots are verified |
| Live re-fetch of site metadata | **not done**; `updated` changes on publish, not on a crawl |
| Stylus / top-screen UI | spec only; no Avalonia shell yet |
| Click confirmation UI | spec only; CLI prints the prompt rule |

## 7. Open questions

- Q-11: does the live index re-fetch each site's headline on a schedule, or only on publish?
- Q-12: RSS source allowlist and refresh cadence for the top screen.
- Q-13: stylus input: does it use the same KeystrokeShield, or a separate handwriting buffer?
- Q-14: should search results show the owner badge to anonymous users, or only the name and tags?
- Q-15: per-connection key lifetime cap for mesh links (frames or minutes) before forced rekey.
