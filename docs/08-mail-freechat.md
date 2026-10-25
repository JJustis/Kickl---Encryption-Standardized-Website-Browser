# 08 — KicklMail and FreeChat

Both are clients of the same Pinwheel transport. Mail is asynchronous and durable; chat is
synchronous and ephemeral. They share identity, contacts, key management, and the dock UI.

## 1. Data model (client, SQLCipher)

```sql
-- contacts
contacts(address PK, handle, short_code, kem_pub, sign_pub, safety, verified_at,
         source, vouch_chain, blocked, created_at, updated_at)

-- threads (mail)
threads(id PK, peer_address, subject_ct, last_at, unread, labels_ct, mute)

-- mail messages
mail(id PK, thread_id, direction, envelope_id, subject_ct, body_ct, meta_ct,
     sent_at, received_at, state, has_attach, per_msg_key_id)

-- chat sessions
sessions(id PK, peer_or_group, kind, ratchet_state_ct, epoch, last_counter, created_at)

-- chat messages
chat(id PK, session_id, direction, counter, body_ct, sent_at, state, expires_at)

-- outbox / retry
outbox(id PK, kind, payload_ct, attempts, next_try_at, last_error)

-- attachments
attachments(id PK, ref, name_ct, size, mime, key_ct, state, path)
```

All `*_ct` columns are AEAD ciphertext under a per-store DEK; searchable fields are either hashes
(thread id, address) or explicitly indexed with a **blind index** (HMAC of the normalised term) so
local search works without storing plaintext. Full-text search is local only; there is no server-side
search.

## 2. Addressing

- Primary: `kb1…` (41 chars). Display: first 8 + last 6 with a copy button; full value on hover.
- Alias: 8-digit short code (`4821 7390`), resolved from the local registry mirror.
- Mailbox on the server: `BLAKE3(address || epoch)[:16]`, rotates every 30 days. The server never
  sees the address after the initial token exchange.
- Optional per-thread alias: `alias = HMAC(identity, threadSalt)` used as the visible "from" so a
  correspondent cannot correlate all of your mail (sealed sender).

## 3. Sending

```
compose
  → validate recipients (local mirror; show "unknown address — verify" if not in contacts)
  → optional per-message key pair (see §6)
  → build body: sanitized HTML subset + plain text fallback + attachments manifest
  → CBOR → compress (zstd, if it helps) → pad to size class
  → HPKE seal to each recipient (To/Cc/Bcc handled separately; Bcc gets its own envelope)
  → sign with device key (signature inside the ciphertext for sealed sender)
  → persist to outbox (encrypted) BEFORE network I/O
  → POST /v1/mail/{mailbox}; 202 → state=queued
  → on failure: exponential backoff, visible state, manual retry
```

- **Bcc is not a header.** Each recipient gets an independently sealed envelope with no recipient
  list. This is a correctness requirement, not a nicety.
- Subject is inside the ciphertext. The server sees only size class and timing.
- Size classes: 4 KiB, 16 KiB, 64 KiB, 256 KiB; payloads over 256 KiB put bodies inline up to 1 MiB
  and attachments in blobs.
- Sending is idempotent (`Idempotency-Key = mail id`); retries cannot duplicate.
- Drafts are encrypted locally and never synced by default.

## 4. Receiving

- Poll every 30 s while the app is foreground (configurable 15–300 s), jittered; every 5 min when
  backgrounded, unless the user disables background sync.
- Fetch by mailbox token (short-lived, HMAC). Decrypt → verify sender signature → verify the sender
  address matches the signing key → persist → ack.
- **Unknown sender policy:** if the signature verifies but the address is not in contacts, the message
  is filed under `Requests` with a banner and no remote content (there is no remote content by
  design). This is the spam surface.
- **Spam controls (client-side):** allowlist, PoW requirement for first contact, vouches from
  contacts, per-sender mute, and a local reputation score. No content scanning, no server-side
  filtering.
- Read receipts: off by default; when on, they are signed envelopes and are not sent for messages
  opened in a "no receipts" thread.

## 5. Contacts: bundles, vouches, and "friends of friends"

### 5.1 Export bundle

```json
{
  "v": 1,
  "type": "kickl-contact-bundle",
  "owner": "kb1q9x7…",
  "handle": "Nyx",
  "kem": "b64url:1216",
  "sign": "b64url:1952",
  "shortCode": "48217390",
  "createdAt": 1792800000,
  "expiresAt": 1811536000,
  "note": "met at DEF CON",
  "vouchedBy": [
    { "address": "kb1…", "sig": "b64url:3373", "at": 1792700000 }
  ],
  "sig": "b64url:3373"
}
```

- The bundle is self-signed and contains public keys only. Importing it **does not** grant access to
  anything; it adds a contact card.
- The UI shows: signature valid? address matches keys? short code matches? vouches valid? key age?
- Export is a file (`nyx.kickl-contact`) or a QR code (address + short code only; keys are fetched
  from the registry or exchanged over the sealed channel).
- "Become part of a large contact list" is implemented as **explicit import**, never automatic
  propagation. An importer may choose to import the bundle's `vouchedBy` entries as *suggestions*,
  which must each be accepted.

### 5.2 Vouching (the safe version of friends-of-friends)

- A contact can sign a vouch for another address: `{voucher, subject, at, expires, context}`.
- Your client displays vouches as a chain: "Nyx (verified) vouches for Ari (2 weeks old)".
- Vouches never grant privileges automatically; they raise a trust hint and can satisfy the
  first-contact PoW requirement.
- A vouch is revocable; revocation is published to the voucher's contacts on next sync.
- **Privacy:** the introducer does not learn whether you contacted the subject, and the subject does
  not learn who vouched unless you tell them. Do not build a social graph on the server.

## 6. Optional per-message key pairs

The brief asks for an optional key pair per message for extra privacy. Implemented as **per-thread
ephemeral addressing plus an optional second layer**:

```
threadKey  = X25519 keypair, generated per thread, private key stored in the thread row
messageKey = X25519 keypair, generated per message when "Extra privacy" is on
envelope   = HPKE(to: recipientKem, pt: CBOR{ inner: HPKE(to: threadKey, pt: body), msgKeyPub })
```

- The recipient unwraps with the thread key and, if enabled, replies only to `msgKeyPub` for that
  message (a one-off conversation key). This gives an extra hop of separation if the long-term key is
  later compromised — but **not** forward secrecy for messages already delivered; that comes from the
  ratchet in chat, and from deleting per-message keys after a configurable window (default 7 days).
- UI: a single toggle with a plain explanation, not a wall of crypto.
- The key pair is included in the export for that thread only if the user explicitly exports it.

## 7. FreeChat

### 7.1 1:1

- X3DH with hybrid prekeys → Double Ratchet (`docs/03` §5.1).
- Direct P2P preferred (UDP hole punch), relay fallback; the UI shows `direct` or `relayed` in the
  thread header.
- Typing indicators and presence are opt-in, ephemeral, and never stored.
- Disappearing messages: per-thread TTL (off, 1 h, 24 h, 7 d); deletion is local + a signed "burn"
  notice so both sides delete. Not enforceable against a modified client — state that.
- Message edit/delete: not in 1.0 (it complicates the ratchet and creates false expectations).
- Attachments: encrypted blob, key inside the message; ≤ 25 MiB; previews generated locally.

### 7.2 Groups

- MLS (RFC 9420) with the hybrid ciphersuite; the relay is the delivery service and sees only opaque
  commits.
- Group creation: creator invites by address; each Welcome is sealed per invitee.
- Membership changes are commits; a removed member cannot read subsequent epochs, but can read what
  they already received (honest limit).
- Group size cap 1.0: 100 members; epoch rotation on membership change, 100 messages, or 60 s.
- Admin roles: owner, moderator (remove members, not read), member. No "read-only" role in 1.0.
- Group metadata (name, avatar) is encrypted and distributed via the group state; the relay does not
  see it.

### 7.3 Shipped v1 — Pinwheel FreeChat bridge

The current app does **not** implement the Double Ratchet/MLS design above yet. It ships a native
client for the existing Pinwheel FreeChat PHP service (`secupgrade.com/pinwheel-freechat`), so
browser and web-widget members share one room and the same accounts:

- ML-KEM-1024 wraps the room secret at join; HKDF-SHA256 derives epoch/message keys; AES-256-GCM
  seals each message. The relay stores opaque ciphertext.
- Messages are 240 px PNGs rendered locally from the composer text, matching the web widget.
- The room panel shows the member's shareable FreeChat handle and numeric ID (`COPY ID`), members,
  and key state. Sign-in/register/guest mirror the widget; credentials are stored only inside the
  encrypted profile envelope (`freechat-prefs.kpf`) so the room can reconnect after unlock.
- **Per-ID blocklist:** `BLOCK` on a member or an incoming message hides that sender locally and
  persists the block in the encrypted profile. It is an incoming-only local filter: the room is a
  group broadcast, so other members still receive that sender's ciphertext. Unblock from the member
  list or the blocked list.
- Chat is unavailable while the profile is locked: the lock gate is shown instead and the client
  disposes its keys on lock.

### 7.4 Presence and notifications

- Presence blob: `{peerId, online, lastSeenBucket, capabilities, sig}`, TTL 120 s, bucketed to 5 min
  to reduce precision.
- Notifications never contain message text by default; they say "New sealed message from Nyx".
- A "focus mode" suppresses all non-mention notifications.

## 8. UI surfaces

| Surface | Content |
|---------|---------|
| Dock pill | two halves, unread counts, connection state dot (green direct, amber relayed, grey offline) |
| Chat panel | thread list (avatar, handle, last-message time only — no preview by default), thread view, composer with attach + emoji + TTL chip |
| Mail panel | mailbox list (Inbox, Requests, Sent, Archive, Trash), list + reading pane, compose in a sheet |
| Contact card | address, short code, safety number, vouches, key age, actions (mail, chat, verify, block) |
| Compose sheet | To/Cc/Bcc chips, subject, body, attach, "Extra privacy" toggle, send-later |
| Verify dialog | safety number in 12 groups, QR, "mark as verified" |

Visual rules: mono micro-labels, 1 px rules, no bubbles with heavy shadows; incoming messages plain
text on `bg.1`, outgoing with a 1 px accent left border; timestamps in `fg.2` 10 px; attachments as
chips with a lock icon until downloaded.

## 9. Sync and multi-device

- 1.0 supports one active device per identity for mail/chat; additional devices can be enrolled but
  are **read-only** until 2.0 (avoids ratchet forking).
- Device enrollment: signed `DeviceCert` + a one-time transfer of the mail/chat store keys over the
  sealed channel (not the identity private key).
- Read state is local per device in 1.0; no server-side sync.
- Conflict rule if two devices do write (unsupported but possible): last-writer-wins per message id,
  with a visible "sent from another device" marker.

## 10. Failure modes

| Failure | UX |
|---------|-----|
| Recipient key unknown | block send, offer "request keys" via the registry or a bundle |
| Signature mismatch | quarantine, red banner: "sender key does not match address" |
| Decrypt failure | keep ciphertext, "could not open — key rotated or message damaged" |
| Mailbox full (507) | outbox retries later, banner with the reason |
| Relay offline | chat queues, shows `queued`, retries |
| Clock skew | block send with "check your clock" (signatures/timestamps) |
| Contact key changed | strong interstitial + safety-number re-verify before sending |
| Attachment too large | refuse with the limit and a suggestion (blob link) |
| Storage full | stop accepting new mail, keep existing readable, offer cleanup |

## 11. Privacy limits (shown in-app)

1. The relay sees that two devices exchanged *something*, its size class, and when.
2. A compromised recipient device reveals the conversation.
3. Disappearing messages are a courtesy, not a guarantee.
4. Vouches are public to the people you share them with; do not vouch for someone you would not
   introduce in person.
5. Mail metadata (mailbox hash, epoch) rotates but is linkable within an epoch by a colluding relay.

## 12. Shipped KicklMail v1 — direct, local, image-only

The live app now has a working mail slice that follows the brief's direct-delivery rule:

- **Pinwheel is a directory, not a mailbox.** `POST /apicalls/v1/mail/identity` stores the
  self-signed public bundle (RSA-3072 SPKI, ML-DSA-65 public key, handle, eight-digit PFS id and
  direct endpoint). `GET /apicalls/v1/mail/lookup?short=NNNNNNNN` returns that bundle. No message
  row is created on the server.
- **Direct delivery.** The sender resolves the bundle, seals the message, and POSTs the sealed PNG
  straight to the recipient device's advertised endpoint. The recipient's listener verifies the
  bundle, envelope signature and image hash before writing anything. Offline means undeliverable;
  there is deliberately no server store-and-forward in this slice.
- **Crypto order.** ML-DSA-65 (Dilithium) signs the identity bundle and every envelope; a fresh
  AES-256 key is generated per message; RSA-3072-OAEP wraps that key for the recipient; AES-256-GCM
  seals the Brotli-compressed payload. Neither RSA nor Dilithium encrypts the body directly.
- **Eyes-only image.** The AES-GCM ciphertext is inserted into a private `kMLm` chunk of a PNG.
  The pixels are a generic sealed-mail cover; the body is not base64 text and is not visible in the
  image. Opening a row extracts the chunk and decrypts it.
- **Local SQLite vault.** Each profile has `mail/mail.db`. The mail password (separate from the
  profile key) is PBKDF2-SHA256 (600k iterations) and unwraps a random 32-byte vault key. The
  identity private keys, message keys and message records are AES-256-GCM rows under that key. On
  receipt the RSA-wrapped transport key is discarded; only the password-sealed message key remains.
- **90,000 characters.** The composer enforces 90,000 characters, Brotli-compresses the payload,
  then encrypts it. The SQLite row holds the sealed image and envelope, not plaintext.
- **Read-on-click.** Inbox rows show only the sender/date and `SEALED · click to open`; no subject
  or body preview is rendered until the row is opened. A decrypted body is displayed as image
  pages, not selectable text.
- **Direct endpoint caveat.** The advertised endpoint is the device's LAN address and a free port.
  Remote delivery needs that port forwarded (or an endpoint update via `PUBLISH`). A wrong mail
  password is an authentication failure and leaves the vault untouched.

## 13. Implementation checklist (2026-10-25)

- [ ] Mail storage, inbox/compose/read UI, sealed sender, Bcc, outbox, attachments, and receipts.
- [ ] Mail delivery API, idempotency, mailbox tokens, blind-index search, and deletion semantics.
- [x] Pinwheel FreeChat v1: native room panel over the live PHP API, ML-KEM-1024 join handshake,
  PNG message rendering, encrypted local credentials, shareable ID, and per-ID blocklist.
- [x] KicklMail v1: direct delivery after Pinwheel credential lookup, ML-DSA-65 envelopes,
  RSA-3072-wrapped per-message AES-256, Brotli 90k body, PNG blob carrier, password-sealed SQLite
  vault, click-to-open inbox, and image-page reader.
- [ ] Mail outbox/retry, attachments, Bcc, sealed sender, contact bundles, vouches, and search.
- [ ] FreeChat session/store, X3DH and Double Ratchet state, replay/out-of-order handling, and TTL.
- [ ] MLS groups, presence, relay/direct fallback, notifications, and contact/block/report UI.
- [ ] Device enrollment and safe key transfer for communication stores.
- [ ] Packet-capture, database-dump, replay, loss, and multi-device verification.

The repository has shared crypto, identity, relay, registry, the native FreeChat v1 client, and the
direct KicklMail v1 slice. Store-and-forward, attachments, the full contact/vouch model and the
Double Ratchet/MLS design above remain requirements, not shipped behavior.
